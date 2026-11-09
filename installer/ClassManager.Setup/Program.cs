using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using Microsoft.Win32;

namespace ClassManager.Setup;

internal static class Program
{
    public const string AppName = "班级管理系统";
    public const string AppVersion = "3.0";
    public const string ExeName = "class-manager.exe";
    public const string PayloadResource = "payload.zip";

    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // 静默安装（机房批处理）：setup.exe --silent --dir "D:\class-manager"
        var silentDir = ArgValue(args, "--dir");
        if (args.Contains("--silent") && !string.IsNullOrWhiteSpace(silentDir))
        {
            try
            {
                Installer.Install(silentDir!, new InstallOptions(), progress: null);
                return 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show("静默安装失败：" + ex.Message, AppName,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        Application.Run(new SetupWebForm());
        return 0;
    }

    private static string? ArgValue(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        }
        return null;
    }
}

/// <summary>安装选项。</summary>
internal sealed class InstallOptions
{
    public bool Autostart { get; set; }
    public bool DesktopShortcut { get; set; } = true;
    public bool StartMenuShortcut { get; set; } = true;
}

/// <summary>安装信息登记：写注册表，下次运行安装器就能认出「已安装」。</summary>
internal static class RegistryStore
{
    private const string KeyPath = @"Software\liang-rising-star\class-manager";
    private const string UninstallKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\class-manager";

    internal sealed record InstallInfo(string Dir, string Version);

    public static InstallInfo? Read()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            if (key is null) return null;

            var dir = key.GetValue("InstallDir") as string;
            var version = key.GetValue("Version") as string ?? string.Empty;

            if (string.IsNullOrWhiteSpace(dir)) return null;
            if (!File.Exists(Path.Combine(dir, Program.ExeName))) return null;

            return new InstallInfo(dir!, version);
        }
        catch
        {
            return null;
        }
    }

    public static void Write(string dir, string version)
    {
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(KeyPath))
            {
                key.SetValue("InstallDir", dir);
                key.SetValue("Version", version);
                key.SetValue("InstalledAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            }

            var exe = Path.Combine(dir, Program.ExeName);
            var uninstaller = Path.Combine(dir, "uninstall.exe");

            // 让「设置 → 应用 → 已安装的应用」里能看到并卸载
            using var uninstall = Registry.CurrentUser.CreateSubKey(UninstallKeyPath);
            uninstall.SetValue("DisplayName", Program.AppName);
            uninstall.SetValue("DisplayVersion", version);
            uninstall.SetValue("DisplayIcon", exe);
            uninstall.SetValue("InstallLocation", dir);
            uninstall.SetValue("Publisher", "liang-rising-star");
            uninstall.SetValue("NoModify", 1, RegistryValueKind.DWord);
            uninstall.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            // 系统卸载接口只指向安装目录里的独立卸载程序
            if (File.Exists(uninstaller))
            {
                uninstall.SetValue("UninstallString", "\"" + uninstaller + "\"");
                uninstall.SetValue("QuietUninstallString", "\"" + uninstaller + "\" --silent");
            }
        }
        catch
        {
            // 注册表写失败不影响安装
        }
    }

    public static void Delete()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(KeyPath, throwOnMissingSubKey: false); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKeyPath, throwOnMissingSubKey: false); } catch { }
    }
}

/// <summary>安装逻辑：路径校验、解压、快捷方式、自启动、卸载脚本、卸载。</summary>
internal static class Installer
{
    private const string PayloadResource = "payload.zip";

    public static string DefaultTarget()
    {
        var preferred = @"D:\class-manager";
        try
        {
            var drive = Path.GetPathRoot(preferred);
            if (!string.IsNullOrEmpty(drive) && Directory.Exists(drive)) return preferred;
        }
        catch { }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "class-manager");
    }

    public enum PathRisk { Ok, NeedsAdmin, NotWritable, Bad }

    public static (PathRisk risk, string message) Check(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return (PathRisk.Bad, "请先选择安装位置");

        string full;
        try { full = Path.GetFullPath(path.Trim().Trim('"')); }
        catch { return (PathRisk.Bad, "路径格式不正确"); }

        var root = Path.GetPathRoot(full)?.TrimEnd('\\');
        if (string.Equals(full.TrimEnd('\\'), root, StringComparison.OrdinalIgnoreCase))
            return (PathRisk.Bad, "不要直接装到磁盘根目录，请在后面加一个文件夹名，例如 D:\\class-manager");

        var lower = full.ToLowerInvariant();
        var guarded = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        };

        foreach (var g in guarded)
        {
            if (string.IsNullOrEmpty(g)) continue;
            if (lower.StartsWith(g.ToLowerInvariant()))
                return (PathRisk.NeedsAdmin,
                    "这个位置需要管理员权限，程序将无法保存成绩数据、也无法自动更新。建议改到 D:\\class-manager 这类普通目录。");
        }

        try
        {
            Directory.CreateDirectory(full);
            var probe = Path.Combine(full, ".write-test");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            return (PathRisk.NotWritable, "当前用户没有写入权限，换一个位置（例如 D:\\class-manager）再装。");
        }
        catch (Exception ex)
        {
            return (PathRisk.NotWritable, "无法使用该位置：" + ex.Message);
        }

        return (PathRisk.Ok, string.Empty);
    }

    /// <summary>用内置载荷安装（全新安装 / 修复重装）。</summary>
    public static void Install(string target, InstallOptions options,
                               IProgress<(int percent, string text)>? progress)
    {
        using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream(Program.PayloadResource)
            ?? throw new InvalidOperationException("安装包缺少载荷文件（payload.zip）");

        ExtractAndDeploy(payload, target, options, progress);
    }

    /// <summary>用外部 zip 包更新（用户在界面里选的 zip）。</summary>
    public static void InstallFromZip(string zipPath, string target, InstallOptions options,
                                      IProgress<(int percent, string text)>? progress)
    {
        if (!File.Exists(zipPath))
            throw new FileNotFoundException("找不到要更新的 zip 包：" + zipPath);

        using var stream = File.OpenRead(zipPath);
        ExtractAndDeploy(stream, target, options, progress);
    }

    private static void ExtractAndDeploy(Stream payload, string target, InstallOptions options,
                                         IProgress<(int percent, string text)>? progress)
    {
        var full = Path.GetFullPath(target.Trim().Trim('"'));
        Directory.CreateDirectory(full);

        using var zip = new ZipArchive(payload, ZipArchiveMode.Read);
        var entries = zip.Entries;
        var total = Math.Max(entries.Count, 1);

        // 先解到临时目录，确认完整后再覆盖，避免半截安装
        var staging = Path.Combine(Path.GetTempPath(), "class-manager-setup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);

        try
        {
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                var destPath = Path.GetFullPath(Path.Combine(staging, entry.FullName));
                if (!destPath.StartsWith(staging, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("包内含非法路径：" + entry.FullName);

                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                {
                    Directory.CreateDirectory(destPath);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                entry.ExtractToFile(destPath, overwrite: true);

                if (i % 20 == 0 || i == entries.Count - 1)
                    progress?.Report((i * 85 / total, "正在解压 " + entry.Name));
            }

            // 外部 zip 必须真的是一套程序，不能把空包盖上去
            if (!File.Exists(Path.Combine(staging, Program.ExeName)))
                throw new InvalidOperationException(
                    "这个 zip 里没有 " + Program.ExeName + "，不是程序更新包。");

            progress?.Report((90, "正在写入安装目录…"));
            CopyTree(staging, full);

            progress?.Report((95, "正在创建快捷方式…"));
            if (options.DesktopShortcut) CreateDesktopShortcut(full);
            if (options.StartMenuShortcut) CreateStartMenuShortcut(full);

            progress?.Report((97, "正在配置开机自启…"));
            ApplyAutostart(full, options.Autostart);

            RegistryStore.Write(full, Program.AppVersion);
            progress?.Report((100, "完成"));
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch { }
        }
    }

    private static void CopyTree(string from, string to)
    {
        foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));

        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }

    private static void CreateDesktopShortcut(string installDir) =>
        TryShortcut(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Program.AppName + ".lnk"), installDir);

    private static void CreateStartMenuShortcut(string installDir)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Program.AppName);
        Directory.CreateDirectory(dir);
        TryShortcut(Path.Combine(dir, Program.AppName + ".lnk"), installDir);
    }

    private static void TryShortcut(string lnkPath, string installDir)
    {
        var exe = Path.Combine(installDir, Program.ExeName);
        if (!File.Exists(exe)) return;

        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return;

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(lnkPath);
            shortcut.TargetPath = exe;
            shortcut.WorkingDirectory = installDir;
            shortcut.IconLocation = exe + ",0";
            shortcut.Description = Program.AppName;
            shortcut.Save();
        }
        catch { }
    }

    private static void ApplyAutostart(string installDir, bool enabled)
    {
        const string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string valueName = "class-manager";

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(runKey, writable: true);
            if (key is null) return;

            if (enabled)
            {
                var exe = Path.Combine(installDir, Program.ExeName);
                key.SetValue(valueName, "\"" + exe + "\" --root \"" + installDir + "\" --tray");
            }
            else
            {
                try { key.DeleteValue(valueName, throwOnMissingValue: false); } catch { }
            }
        }
        catch { }
    }

}
