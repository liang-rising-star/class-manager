using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace ClassManager.KioskSetup;

/// <summary>
/// 一体机端安装程序：把 kiosk-agent 铺到目标目录，建快捷方式、写开机自启、登记卸载信息。
/// 支持
///   setup.exe                                    ← 图形界面
///   setup.exe --silent --dir "D:\kiosk"          ← 静默安装
///   setup.exe --uninstall --dir "D:\kiosk"       ← 静默卸载
/// </summary>
internal static class Program
{
    public const string AppName = "班级管理系统 · 一体机端";
    public const string AppVersion = "1.0";
    public const string KioskExe = "class-manager-kiosk.exe";
    public const string PayloadResource = "payload.zip";
    public const string RunKeyName = "class-manager-kiosk";
    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\class-manager-kiosk";

    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var dir = ArgValue(args, "--dir");
        var silent = args.Contains("--silent");
        var uninstall = args.Contains("--uninstall");

        if (uninstall)
        {
            var target = string.IsNullOrWhiteSpace(dir) ? DefaultDir : dir!;
            try
            {
                Installer.Uninstall(target, silent ? null : (Action<int, string>)((p, m) => Console.WriteLine(m)));
                if (!silent)
                    MessageBox.Show("已卸载：" + target, AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            }
            catch (Exception ex)
            {
                if (!silent)
                    MessageBox.Show("卸载失败：" + ex.Message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        if (silent && !string.IsNullOrWhiteSpace(dir))
        {
            try
            {
                Installer.Install(dir!, new InstallOptions(), null);
                return 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show("静默安装失败：" + ex.Message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        Application.Run(new SetupWebForm());
        return 0;
    }

    /// <summary>
    /// 默认装到非系统盘根目录下的 ClassManager-Kiosk；
    /// 没有其它盘时退回 %LOCALAPPDATA%，普通用户都能写，不需要管理员权限。
    /// </summary>
    public static string DefaultDir
    {
        get
        {
            try
            {
                foreach (var drive in DriveInfo.GetDrives())
                {
                    if (!drive.IsReady) continue;
                    if (drive.DriveType != DriveType.Fixed) continue;
                    if (string.Equals(drive.Name, Path.GetPathRoot(Environment.SystemDirectory),
                            StringComparison.OrdinalIgnoreCase)) continue;

                    return Path.Combine(drive.RootDirectory.FullName, "ClassManager-Kiosk");
                }
            }
            catch { }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ClassManager-Kiosk");
        }
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

internal sealed class InstallOptions
{
    public bool Autostart { get; set; } = true;
    public bool DesktopShortcut { get; set; } = true;
    public bool StartMenuShortcut { get; set; } = true;

    /// <summary>安装前清空本机已有配置（出厂状态，安装包本身不携带任何密码/密钥）</summary>
    public bool ClearExistingConfig { get; set; } = true;
}

internal static class Installer
{
    /// <summary>安装：解压载荷 → 建快捷方式 → 写自启 → 写卸载登记。</summary>
    public static void Install(string dir, InstallOptions options, Action<int, string>? progress)
    {
        dir = Path.GetFullPath(dir);

        progress?.Invoke(4, "关闭正在运行的一体机端…");
        StopKiosk(dir);

        if (options.ClearExistingConfig)
        {
            progress?.Invoke(9, "清除本机旧配置（恢复出厂）…");
            ResetLocalConfig();
        }

        progress?.Invoke(14, "准备安装目录…");
        Directory.CreateDirectory(dir);

        progress?.Invoke(25, "释放程序文件…");
        ExtractPayload(dir);

        var exe = Path.Combine(dir, Program.KioskExe);
        if (!File.Exists(exe))
            throw new InvalidOperationException("安装包不完整（缺少 " + Program.KioskExe + "）");

        if (options.DesktopShortcut)
        {
            progress?.Invoke(70, "创建桌面快捷方式…");
            Shortcuts.CreateDesktop(exe, dir);
        }

        if (options.StartMenuShortcut)
        {
            progress?.Invoke(76, "创建开始菜单快捷方式…");
            Shortcuts.CreateStartMenu(exe, dir);
        }

        progress?.Invoke(82, "写入开机自启…");
        Autostart.Set(options.Autostart, exe, dir);

        progress?.Invoke(88, "登记卸载信息…");
        WriteUninstallEntry(dir, exe);

        progress?.Invoke(94, "启动一体机端…");
        try
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = dir });
        }
        catch { }

        progress?.Invoke(100, "安装完成");
    }

    /// <summary>卸载：先让一体机端正常退出，再删目录和登记信息。</summary>
    public static void Uninstall(string dir, Action<int, string>? progress)
    {
        dir = Path.GetFullPath(dir);

        progress?.Invoke(10, "关闭一体机端…");
        StopKiosk(dir);

        progress?.Invoke(30, "清理开机自启…");
        Autostart.Set(false, null, null);

        progress?.Invoke(45, "删除快捷方式…");
        Shortcuts.Remove(dir);

        progress?.Invoke(55, "删除卸载登记…");
        try { Registry.CurrentUser.DeleteSubKeyTree(Program.UninstallKey, throwOnMissingSubKey: false); }
        catch { }

        progress?.Invoke(65, "删除程序文件…");
        for (var i = 0; i < 5; i++)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                break;
            }
            catch
            {
                Thread.Sleep(500);
            }
        }

        progress?.Invoke(100, "卸载完成");
    }

    /// <summary>
    /// 先让一体机端调 /api/exit 自己退出（它会解除进程保护），
    /// 拿不到密钥或没响应再退化成结束进程。
    /// </summary>
    private static void StopKiosk(string dir)
    {
        // 只处理安装目录下的实例，别误伤开发机/另一个位置的副本
        var procs = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Program.KioskExe))
            .Where(p =>
            {
                try
                {
                    var path = p.MainModule?.FileName;
                    return path is null || path.StartsWith(dir, StringComparison.OrdinalIgnoreCase);
                }
                catch { return true; }
            })
            .ToArray();

        if (procs.Length == 0) return;

        // 1) 优雅退出：用本地配置里的访问密钥请求 /api/exit
        try
        {
            var cfgPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ClassManager-KioskAgent", "config.json");

            if (File.Exists(cfgPath))
            {
                var text = File.ReadAllText(cfgPath, Encoding.UTF8);
                var key = ExtractJsonString(text, "AccessKey");
                var port = ExtractJsonNumber(text, "Port");
                if (port <= 0) port = 3457;

                if (!string.IsNullOrEmpty(key))
                {
                    using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                    var req = new System.Net.Http.HttpRequestMessage(
                        System.Net.Http.HttpMethod.Post, $"http://127.0.0.1:{port}/api/exit");
                    req.Headers.Add("X-Access-Key", key);
                    req.Content = new System.Net.Http.StringContent("{}", Encoding.UTF8, "application/json");
                    try { http.Send(req); } catch { }
                }
            }
        }
        catch { }

        // 2) 等它自己退出
        foreach (var p in procs)
        {
            try { p.WaitForExit(6000); } catch { }
        }

        // 3) 还在就强杀（会先解除进程保护）
        foreach (var p in procs)
        {
            try
            {
                if (p.HasExited) continue;
                ProcessForcer.TryKill(p, 4000);
            }
            catch { }
        }

        // 4) 目录下的其它残留进程（例如旧版本、看门狗）
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == Environment.ProcessId) continue;
                var path = p.MainModule?.FileName;
                if (path is null || !path.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) continue;
                ProcessForcer.TryKill(p, 3000);
            }
            catch { }
        }

        // 5) 仍然活着就别继续删文件，明确报错让用户先手动退出
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Program.KioskExe)))
        {
            try
            {
                var path = p.MainModule?.FileName;
                if (path is not null && path.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        "一体机端仍在运行，无法卸载。请先在托盘图标上右键退出，再重新卸载。");
            }
            catch (InvalidOperationException) { throw; }
            catch { }
        }
    }

    /// <summary>
    /// 把本机的一体机端配置挪走（恢复出厂）。
    /// 安装包里不含任何密码/密钥，这里顺手把旧机器的配置也清掉，
    /// 保证装完是「全新设备」状态，需要重新设访问密码。
    /// </summary>
    private static void ResetLocalConfig()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ClassManager-KioskAgent");

            if (!Directory.Exists(dir)) return;

            var backup = dir + "-backup-" + DateTime.Now.ToString("yyyyMMddHHmmss");
            try
            {
                Directory.Move(dir, backup);
                Log("旧配置已备份到：" + backup);
            }
            catch
            {
                // 挪不动就删掉
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }
        catch { }
    }

    internal static void Log(string message)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "class-manager-kiosk-setup.log");
            File.AppendAllText(path,
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine,
                Encoding.UTF8);
        }
        catch { }
    }

    /// <summary>安装路径检查：给界面用的提示。</summary>
    public static object CheckPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new { ok = false, level = "bad", message = "请选择安装位置" };

        try
        {
            var full = Path.GetFullPath(path);

            if (full.Length <= 3)
                return new { ok = false, level = "bad", message = "不要直接装在磁盘根目录，请加一个文件夹名" };

            var root = Path.GetPathRoot(full) ?? "";

            // 系统盘的程序目录需要管理员权限，普通安装会失败
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrEmpty(programFiles) &&
                full.StartsWith(programFiles, StringComparison.OrdinalIgnoreCase) && !IsElevated())
            {
                return new { ok = true, level = "warn", message = "该位置需要管理员权限，程序将无法自动更新；建议改到 D:\\ClassManager-Kiosk" };
            }

            var parent = Directory.Exists(full) ? full : Path.GetDirectoryName(full);
            while (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                parent = Path.GetDirectoryName(parent);

            if (parent is not null && Directory.Exists(parent))
            {
                var probe = Path.Combine(parent, ".cm-write-test-" + Guid.NewGuid().ToString("N"));
                try
                {
                    File.WriteAllText(probe, "x");
                    File.Delete(probe);
                }
                catch
                {
                    return new { ok = false, level = "bad", message = "当前用户对该位置没有写入权限，换一个目录试试" };
                }
            }

            return new { ok = true, level = "ok", message = "将安装到 " + full };
        }
        catch (Exception ex)
        {
            return new { ok = false, level = "bad", message = "路径无效：" + ex.Message };
        }
    }

    private static bool IsElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    private static string? ExtractJsonString(string json, string key)
    {
        var m = System.Text.RegularExpressions.Regex.Match(
            json, "\"" + key + "\"\\s*:\\s*\"([^\"]*)\"");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static int ExtractJsonNumber(string json, string key)
    {
        var m = System.Text.RegularExpressions.Regex.Match(
            json, "\"" + key + "\"\\s*:\\s*(\\d+)");
        return m.Success && int.TryParse(m.Groups[1].Value, out var v) ? v : 0;
    }

    private static void ExtractPayload(string dir)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(Program.PayloadResource)
                           ?? throw new InvalidOperationException("安装包损坏：找不到内置载荷");

        // 先解到临时目录，再整体盖上，避免留下半成品
        var staging = Path.Combine(Path.GetTempPath(), "class-manager-kiosk-setup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
                zip.ExtractToDirectory(staging, overwriteFiles: true);

            CopyTree(staging, dir);
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch { }
        }
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);

        foreach (var file in Directory.GetFiles(from))
        {
            var target = Path.Combine(to, Path.GetFileName(file));
            File.Copy(file, target, overwrite: true);
        }

        foreach (var sub in Directory.GetDirectories(from))
        {
            CopyTree(sub, Path.Combine(to, Path.GetFileName(sub)));
        }
    }

    private static void WriteUninstallEntry(string dir, string exe)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(Program.UninstallKey);
            key.SetValue("DisplayName", Program.AppName);
            key.SetValue("DisplayVersion", Program.AppVersion);
            key.SetValue("DisplayIcon", exe);
            key.SetValue("Publisher", "liang-rising-star");
            key.SetValue("InstallLocation", dir);
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);

            // 卸载程序随安装包一起铺到安装目录（uninstall.exe）
            var uninstaller = Path.Combine(dir, "uninstall.exe");
            if (File.Exists(uninstaller))
            {
                key.SetValue("UninstallString", "\"" + uninstaller + "\" --dir \"" + dir + "\"");
                key.SetValue("QuietUninstallString", "\"" + uninstaller + "\" --dir \"" + dir + "\" --silent");
            }
        }
        catch { }
    }
}

internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr h, string text, string caption, uint type);

    public static void Set(bool enabled, string? exe, string? dir)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey);

            if (enabled && !string.IsNullOrEmpty(exe))
            {
                var cmd = "\"" + exe + "\"";
                key.SetValue(Program.RunKeyName, cmd);
            }
            else
            {
                key.DeleteValue(Program.RunKeyName, throwOnMissingValue: false);
            }
        }
        catch { }
    }

    /// <summary>写 Startup 文件夹里的快捷方式（比 Run 键更直观，管理员可见）。</summary>
    public static void SetStartupFolder(bool enabled, string? exe, string? dir)
    {
        try
        {
            var startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            var lnk = Path.Combine(startup, "班级管理系统 一体机端.lnk");

            if (enabled && exe != null)
            {
                Shortcuts.Create(lnk, exe, dir ?? Path.GetDirectoryName(exe) ?? "");
            }
            else if (File.Exists(lnk))
            {
                File.Delete(lnk);
            }
        }
        catch { }
    }
}

internal static class Shortcuts
{
    private const string LinkName = "班级管理系统 一体机端.lnk";

    public static void CreateDesktop(string exe, string dir)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        Create(Path.Combine(desktop, LinkName), exe, dir);
    }

    public static void CreateStartMenu(string exe, string dir)
    {
        var menu = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        Create(Path.Combine(menu, LinkName), exe, dir);
    }

    public static void Create(string lnkPath, string exe, string dir)
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null) return;

            dynamic shell = Activator.CreateInstance(type)!;
            dynamic sc = shell.CreateShortcut(lnkPath);
            sc.TargetPath = exe;
            sc.WorkingDirectory = dir;
            sc.IconLocation = exe + ",0";
            sc.Description = Program.AppName;
            sc.Save();
        }
        catch { }
    }

    public static void Remove(string dir)
    {
        try
        {
            var desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), LinkName);
            var menu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), LinkName);
            var startup = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), LinkName);

            foreach (var f in new[] { desktop, menu, startup })
            {
                if (File.Exists(f)) File.Delete(f);
            }
        }
        catch { }
    }
}
