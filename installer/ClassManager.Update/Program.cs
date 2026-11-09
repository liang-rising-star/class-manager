using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace ClassManager.Update;

/// <summary>
/// 独立更新程序（原生 AOT，几 MB）：
///   1. 等主程序退出
///   2. 把 class-manager-system\data 挪到临时目录保护起来
///   3. 删掉旧的 class-manager-system、desktop
///   4. 把 update.zip 解压到安装目录
///   5. 把 data 放回去
///   6. 重新拉起主程序，并把自己从 update 目录里删掉
/// </summary>
internal static class Program
{
    private const string AppName = "班级管理系统";
    private const string ExeName = "class-manager.exe";

    private static string _dir = string.Empty;
    private static string _zip = string.Empty;
    private static bool _restart = true;
    private static bool _silent;

    private static int Main(string[] args)
    {
        _dir = Path.GetFullPath(Arg(args, "--dir") ?? GuessInstallDir());
        _zip = Arg(args, "--zip") ?? Path.Combine(_dir, "class-manager-system", "update", "update.zip");
        _restart = !args.Contains("--no-restart");
        _silent = args.Contains("--silent");

        if (!Directory.Exists(_dir))
        {
            Fail("找不到安装目录：" + _dir);
            return 1;
        }

        if (!File.Exists(_zip))
        {
            Fail("找不到更新包：" + _zip);
            return 1;
        }

        // 更新程序自己也在安装目录里，先复制到临时目录再干活
        if (IsInside(_dir, Environment.ProcessPath))
        {
            try
            {
                var temp = Path.Combine(Path.GetTempPath(), "class-manager-updater-" + Guid.NewGuid().ToString("N") + ".exe");
                File.Copy(Environment.ProcessPath!, temp, overwrite: true);

                var psi = new ProcessStartInfo(temp)
                {
                    UseShellExecute = false,
                    Arguments = $"--dir \"{_dir}\" --zip \"{_zip}\"" +
                                (_restart ? "" : " --no-restart") +
                                (_silent ? " --silent" : ""),
                };
                Process.Start(psi);
                return 0;
            }
            catch (Exception ex)
            {
                Fail("更新程序无法启动：" + ex.Message);
                return 1;
            }
        }

        try
        {
            Run();
        }
        catch (Exception ex)
        {
            Fail("更新失败：" + ex.Message + "\n\n旧版本已尽力保留，可重新运行安装程序修复。");
            return 1;
        }

        if (_restart) StartApp();
        ScheduleSelfDelete(Environment.ProcessPath!);
        return 0;
    }

    private static void Run()
    {
        WaitForAppExit();

        var staging = Path.Combine(Path.GetTempPath(), "class-manager-update-" + Guid.NewGuid().ToString("N"));
        var dataBackup = Path.Combine(staging, "data");
        var extractDir = Path.Combine(staging, "new");
        Directory.CreateDirectory(extractDir);

        // 1) 先把新版本解压并校验，确认没问题再动旧文件
        ZipFile.ExtractToDirectory(_zip, extractDir, overwriteFiles: true);
        var payloadRoot = FindPayloadRoot(extractDir);

        if (!File.Exists(Path.Combine(payloadRoot, ExeName)))
            throw new InvalidOperationException("更新包内容不完整（缺少 " + ExeName + "）");

        // 2) 备份数据
        var dataSrc = Path.Combine(_dir, "class-manager-system", "data");
        var hasData = Directory.Exists(dataSrc);
        if (hasData) CopyTree(dataSrc, dataBackup);

        // 3) 删旧的类库目录和桌面壳（根目录下的 exe / 卸载 / 更新程序保留）
        DeleteTree(Path.Combine(_dir, "class-manager-system"));
        DeleteTree(Path.Combine(_dir, "desktop"));

        try
        {
            // 4) 铺新版本
            CopyTree(payloadRoot, _dir);

            // 5) 数据放回去
            if (hasData)
            {
                var dataDst = Path.Combine(_dir, "class-manager-system", "data");
                if (Directory.Exists(dataDst)) DeleteTree(dataDst);
                CopyTree(dataBackup, dataDst);
            }
        }
        catch
        {
            // 出错就把数据还回去，至少别丢成绩
            if (hasData)
            {
                try
                {
                    var dataDst = Path.Combine(_dir, "class-manager-system", "data");
                    if (Directory.Exists(dataDst)) DeleteTree(dataDst);
                    CopyTree(dataBackup, dataDst);
                }
                catch { }
            }
            throw;
        }

        // 更新目录始终保留
        Directory.CreateDirectory(Path.Combine(_dir, "class-manager-system", "update"));

        // 写一个标记：新版本启动后弹一次「安装完毕」
        try
        {
            var version = ReadAppVersion(Path.Combine(_dir, "class-manager-system", "main.py"));
            var mark = Path.Combine(_dir, "class-manager-system", "update", "last-update.json");
            var json = "{\"version\":\"" + version + "\",\"ts\":" +
                       DateTimeOffset.UtcNow.ToUnixTimeSeconds() + "}";
            File.WriteAllText(mark, json, new System.Text.UTF8Encoding(false));
        }
        catch { }

        // 清理 7 天前的更新包
        try
        {
            foreach (var old in Directory.GetFiles(Path.Combine(_dir, "class-manager-system", "update"), "*.zip"))
            {
                if (File.GetLastWriteTime(old) < DateTime.Now.AddDays(-7)) File.Delete(old);
            }
        }
        catch { }

        try { Directory.Delete(staging, recursive: true); } catch { }
    }

    /// <summary>有的压缩包外面包了一层同名目录，这里自动往下找一层。</summary>
    private static string FindPayloadRoot(string extractDir)
    {
        if (File.Exists(Path.Combine(extractDir, ExeName))) return extractDir;
        if (Directory.Exists(Path.Combine(extractDir, "class-manager-system"))) return extractDir;

        foreach (var dir in Directory.GetDirectories(extractDir))
        {
            if (File.Exists(Path.Combine(dir, ExeName)) ||
                Directory.Exists(Path.Combine(dir, "class-manager-system")))
            {
                return dir;
            }
        }

        return extractDir;
    }

    /// <summary>等主程序和它的后端 python 都退出，等不到就强杀。</summary>
    private static void WaitForAppExit()
    {
        for (var i = 0; i < 120; i++)          // 最多等 60 秒
        {
            if (!AnyAlive()) return;
            Thread.Sleep(500);
        }

        KillProcesses();
    }

    /// <summary>安装目录下还有没有主程序或后端 python 在跑。</summary>
    private static bool AnyAlive()
    {
        if (FindProcesses(Program.ExeName).Count > 0) return true;
        return FindProcesses("python.exe").Any(p => IsInside(_dir, p.Path));
    }

    private sealed record ProcInfo(int Id, string? Path);

    private static List<ProcInfo> FindProcesses(string exeName)
    {
        var list = new List<ProcInfo>();
        foreach (var proc in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exeName)))
        {
            try { list.Add(new ProcInfo(proc.Id, proc.MainModule?.FileName)); }
            catch { list.Add(new ProcInfo(proc.Id, null)); }
        }
        return list;
    }

    /// <summary>强杀主程序与安装目录下的 python（MainModule 取不到时用 wmic 兜底）。</summary>
    private static void KillProcesses()
    {
        // 主程序：进程名是我们独有的，直接全杀
        foreach (var proc in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Program.ExeName)))
        {
            try
            {
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(3000);
            }
            catch { }
        }

        // 后端 python：先按可执行文件路径匹配
        var killed = new HashSet<int>();
        foreach (var info in FindProcesses("python.exe"))
        {
            if (info.Path is null || !IsInside(_dir, info.Path)) continue;
            try
            {
                using var proc = Process.GetProcessById(info.Id);
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(2000);
                killed.Add(info.Id);
            }
            catch { }
        }

        // 路径取不到（权限不足）时用 wmic 查命令行再补刀
        try
        {
            var psi = new ProcessStartInfo("cmd.exe",
                "/c wmic process where \"name='python.exe'\" get ProcessId,CommandLine /format:csv")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            };

            using var p = Process.Start(psi);
            if (p is not null)
            {
                var output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(8000);

                foreach (var line in output.Split((char)10))
                {
                    if (!line.Contains(_dir, StringComparison.OrdinalIgnoreCase)) continue;

                    var parts = line.Trim().Split(',');
                    if (parts.Length < 2) continue;
                    if (!int.TryParse(parts[^1].Trim(), out var pid)) continue;
                    if (pid == Environment.ProcessId || killed.Contains(pid)) continue;

                    try
                    {
                        using var target = Process.GetProcessById(pid);
                        target.Kill(entireProcessTree: true);
                        target.WaitForExit(2000);
                    }
                    catch { }
                }
            }
        }
        catch { }

        // 等文件句柄释放，否则删旧目录会漏文件
        for (var i = 0; i < 10; i++)
        {
            Thread.Sleep(300);
            if (!AnyAlive()) break;
        }
    }

    /// <summary>从 main.py 里读 APP_VERSION，用来写更新标记。</summary>
    private static string ReadAppVersion(string mainPy)
    {
        try
        {
            if (!File.Exists(mainPy)) return "";
            foreach (var line in File.ReadLines(mainPy))
            {
                if (!line.TrimStart().StartsWith("APP_VERSION")) continue;
                var parts = line.Split('"');
                if (parts.Length >= 2) return parts[1];
            }
        }
        catch { }
        return "";
    }

    private static void StartApp()
    {
        var exe = Path.Combine(_dir, ExeName);
        if (!File.Exists(exe)) return;

        try
        {
            Process.Start(new ProcessStartInfo(exe)
            {
                WorkingDirectory = _dir,
                Arguments = "--root \"" + _dir + "\"",
            });
        }
        catch { }
    }

    private static void DeleteTree(string path)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                if (!Directory.Exists(path)) return;
                Directory.Delete(path, recursive: true);
                return;
            }
            catch
            {
                Thread.Sleep(400);
            }
        }

        try
        {
            if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo("cmd.exe", "/c rmdir /s /q \"" + path + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                })?.WaitForExit(15000);
            }
        }
        catch { }
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);

        foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));

        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }

    private static void ScheduleSelfDelete(string selfPath) =>
        Try(() => Process.Start(new ProcessStartInfo("cmd.exe",
            "/c timeout /t 2 /nobreak >nul & del /f /q \"" + selfPath + "\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        }));

    private static void Try(Action action)
    {
        try { action(); } catch { }
    }

    private static void Fail(string message)
    {
        if (_silent) return;
        MessageBoxW(IntPtr.Zero, message, AppName, 0x00000010 /* MB_ICONERROR */);
    }

    private static string GuessInstallDir()
    {
        // 更新程序通常在 <安装目录>\class-manager-system\update\ 或 <安装目录>\ 下
        var dir = Path.GetDirectoryName(Environment.ProcessPath) ?? ".";
        for (var i = 0; i < 3; i++)
        {
            if (File.Exists(Path.Combine(dir, ExeName))) return dir;
            var parent = Path.GetDirectoryName(dir);
            if (string.IsNullOrEmpty(parent)) break;
            dir = parent;
        }
        return dir;
    }

    private static bool IsInside(string dir, string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        try
        {
            return Path.GetFullPath(path).StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static string? Arg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        }
        return null;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
