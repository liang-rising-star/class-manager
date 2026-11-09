using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace ClassManager.KioskUninstall;

internal static class Program
{
    public const string AppName = "班级管理系统 · 一体机端";
    public const string KioskExe = "class-manager-kiosk.exe";
    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\class-manager-kiosk";
    public const string RunValue = "class-manager-kiosk";

    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var dir = ArgValue(args, "--dir") ?? GuessDir();
        var silent = args.Contains("--silent");

        if (!Directory.Exists(dir))
        {
            if (!silent)
                MessageBox.Show("找不到安装目录：\n" + dir + "\n\n可能已经卸载过了。",
                    AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 1;
        }

        if (silent)
        {
            try
            {
                Uninstaller.Run(dir, null);
                return 0;
            }
            catch (Exception ex)
            {
                return 2;
            }
        }

        Application.Run(new UninstallForm(dir));
        return 0;
    }

    /// <summary>没传 --dir 时，按自身所在目录推断。</summary>
    private static string GuessDir()
    {
        var baseDir = AppContext.BaseDirectory;
        return Path.GetFullPath(baseDir.TrimEnd(Path.DirectorySeparatorChar));
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

/// <summary>卸载动作。</summary>
internal static class Uninstaller
{
    public static void Run(string dir, Action<int, string>? progress)
    {
        dir = Path.GetFullPath(dir);

        progress?.Invoke(8, "正在关闭一体机端…");
        StopKiosk(dir);

        progress?.Invoke(30, "正在清理开机自启…");
        RemoveAutostart();

        progress?.Invoke(45, "正在删除快捷方式…");
        RemoveShortcuts();

        progress?.Invoke(58, "正在删除卸载登记…");
        try { Registry.CurrentUser.DeleteSubKeyTree(Program.UninstallKey, throwOnMissingSubKey: false); }
        catch { }

        progress?.Invoke(70, "正在删除程序文件…");

        // 卸载程序自己也在目录里，先复制到临时目录再干活
        var self = Environment.ProcessPath;
        if (self is not null && self.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var temp = Path.Combine(Path.GetTempPath(), "kiosk-uninstall-" + Guid.NewGuid().ToString("N") + ".exe");
                File.Copy(self, temp, overwrite: true);

                Process.Start(new ProcessStartInfo(temp)
                {
                    UseShellExecute = false,
                    Arguments = "--dir \"" + dir + "\" --silent --from-temp"
                });

                progress?.Invoke(100, "卸载完成");
                Environment.Exit(0);
            }
            catch { }
        }

        DeleteTree(dir);
        progress?.Invoke(100, "卸载完成");
    }

    private static void DeleteTree(string dir)
    {
        for (var i = 0; i < 6; i++)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                Directory.Delete(dir, recursive: true);
                return;
            }
            catch
            {
                Thread.Sleep(600);
            }
        }

        throw new InvalidOperationException("部分文件删除失败，请关闭占用文件的程序后重试");
    }

    /// <summary>先调 /api/exit 优雅退出（会解除进程保护），不行再强制结束。</summary>
    private static void StopKiosk(string dir)
    {
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

        // 1) 优雅退出
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

        foreach (var p in procs)
        {
            try { p.WaitForExit(6000); } catch { }
        }

        // 2) 强制结束（绕过进程保护）
        foreach (var p in procs)
        {
            try
            {
                if (p.HasExited) continue;
                ProcessForcer.TryKill(p, 4000);
            }
            catch { }
        }

        // 3) 目录下其它残留进程
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

        // 4) 还有活的就报错，不带着进程删文件
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Program.KioskExe)))
        {
            try
            {
                var path = p.MainModule?.FileName;
                if (path is not null && path.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        "一体机端仍在运行，无法卸载。请先在托盘图标上右键「退出」，再重新卸载。");
            }
            catch (InvalidOperationException) { throw; }
            catch { }
        }
    }

    private static string? ExtractJsonString(string json, string key)
    {
        var m = System.Text.RegularExpressions.Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"([^\"]*)\"");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static int ExtractJsonNumber(string json, string key)
    {
        var m = System.Text.RegularExpressions.Regex.Match(json, "\"" + key + "\"\\s*:\\s*(\\d+)");
        return m.Success && int.TryParse(m.Groups[1].Value, out var v) ? v : 0;
    }

    private static void RemoveAutostart()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            key?.DeleteValue(Program.RunValue, throwOnMissingValue: false);
        }
        catch { }
    }

    private static void RemoveShortcuts()
    {
        var name = "班级管理系统 一体机端.lnk";

        foreach (var folder in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                     Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                     Environment.GetFolderPath(Environment.SpecialFolder.Startup)
                 })
        {
            try
            {
                var path = Path.Combine(folder, name);
                if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }
    }
}

internal static class ProcessForcer
{
    private const uint WRITE_DAC = 0x00040000;
    private const uint DACL_SECURITY_INFORMATION = 0x00000004;
    private const int SE_KERNEL_OBJECT = 6;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern int SetSecurityInfo(IntPtr handle, int objectType, uint securityInfo,
        IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);

    public static bool TryKill(Process proc, int waitMs = 4000)
    {
        try { if (proc.HasExited) return true; } catch { return true; }

        try
        {
            proc.Kill(entireProcessTree: true);
            proc.WaitForExit(waitMs);
            if (proc.HasExited) return true;
        }
        catch { }

        if (Unlock(proc.Id))
        {
            try
            {
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(waitMs);
                if (proc.HasExited) return true;
            }
            catch { }
        }

        return false;
    }

    private static bool Unlock(int pid)
    {
        IntPtr handle = IntPtr.Zero;
        try
        {
            handle = OpenProcess(WRITE_DAC, false, pid);
            if (handle == IntPtr.Zero) return false;

            return SetSecurityInfo(handle, SE_KERNEL_OBJECT, DACL_SECURITY_INFORMATION,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) == 0;
        }
        catch { return false; }
        finally { if (handle != IntPtr.Zero) CloseHandle(handle); }
    }
}
