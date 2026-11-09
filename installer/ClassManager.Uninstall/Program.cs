using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace ClassManager.Uninstall;

/// <summary>
/// 独立卸载程序（界面与安装器完全同一套 WebView2 向导风格）：
/// 确认 → 进度 → 完成。默认删除整个安装目录（含成绩数据）。
/// </summary>
internal static class Program
{
    public const string AppName = "班级管理系统";
    public const string ExeName = "class-manager.exe";
    public const string AppKeyPath = @"Software\liang-rising-star\class-manager";
    public const string UninstallKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\class-manager";

    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var dir = ArgValue(args, "--dir") ?? ResolveInstallDir();
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            MessageBox.Show("找不到安装目录，可能已经卸载过了。", AppName,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 1;
        }

        dir = Path.GetFullPath(dir.Trim('"'));

        // --silent：不显示界面直接卸载（给静默卸载用）
        if (args.Contains("--silent"))
        {
            var keepData = args.Contains("--keep-data");
            Uninstaller.Run(dir, keepData, new Progress<(int, string)>((_) => { }));

            // 静默模式没人点「关闭」，这里直接安排收尾清理（本进程退出后执行）
            if (Directory.Exists(dir))
            {
                if (keepData)
                {
                    var selfExe = Environment.ProcessPath;
                    if (!string.IsNullOrEmpty(selfExe) && File.Exists(selfExe))
                        Uninstaller.ScheduleFileDelete(selfExe!);
                }
                else
                {
                    Uninstaller.ScheduleTreeDelete(dir);
                }
            }

            return 0;
        }

        Application.Run(new UninstallForm(dir));
        return 0;
    }

    internal static string? ResolveInstallDir()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AppKeyPath);
            if (key?.GetValue("InstallDir") is string dir && Directory.Exists(dir)) return dir;
        }
        catch { }

        // 退而求其次：卸载程序自己所在的目录（在本程序位于 <安装目录>/uninstall/ 时取上一级）
        var selfDir = Path.GetDirectoryName(Environment.ProcessPath);
        if (string.IsNullOrEmpty(selfDir)) return null;

        var parent = Directory.GetParent(selfDir)?.FullName;
        if (parent is not null &&
            string.Equals(Path.GetFileName(selfDir), "uninstall", StringComparison.OrdinalIgnoreCase))
        {
            return parent;
        }

        return selfDir;
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

/// <summary>WebView2 承载的卸载向导。</summary>
public sealed class UninstallForm : Form
{
    private readonly string _dir;
    private readonly WebView2 _web = new();
    private readonly Panel _splash;
    private readonly UninstallBridge _bridge;

    public UninstallForm(string dir)
    {
        _dir = dir;

        Text = Program.AppName + " 卸载程序";
        ClientSize = new Size(760, 560);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        BackColor = Color.FromArgb(247, 249, 252);
        Font = new Font("Microsoft YaHei UI", 9.5f);
        AutoScaleMode = AutoScaleMode.Dpi;

        try
        {
            using var stream = EmbeddedUi.OpenResource("res.logo.ico");
            if (stream is not null) Icon = new Icon(stream);
        }
        catch { }

        _bridge = new UninstallBridge(this);

        _web.Dock = DockStyle.Fill;
        Controls.Add(_web);

        _splash = SetupSplash.Build();
        Controls.Add(_splash);
        _splash.BringToFront();

        Load += async (_, _) => await InitAsync();
    }

    private async Task InitAsync()
    {
        try
        {
            var scale = DeviceDpi / 96.0;
            ClientSize = new Size((int)Math.Round(760 * scale), (int)Math.Round(560 * scale));
            CenterToScreen();

            var runtime = FindFixedRuntime();
            if (runtime is null)
            {
                try
                {
                    var version = CoreWebView2Environment.GetAvailableBrowserVersionString();
                    if (string.IsNullOrEmpty(version)) throw new InvalidOperationException("未检测到 WebView2 运行时");
                }
                catch (Exception checkEx)
                {
                    var answer = MessageBox.Show(
                        "卸载界面需要 Microsoft Edge WebView2 运行时。\n\n" +
                        "点「是」打开官方下载页；点「否」改用命令行静默卸载。\n\n" +
                        "技术细节：" + checkEx.Message,
                        Program.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

                    if (answer == DialogResult.Yes)
                    {
                        try
                        {
                            Process.Start(new ProcessStartInfo("https://go.microsoft.com/fwlink/p/?LinkId=2124703")
                            { UseShellExecute = true });
                        }
                        catch { }
                        Close();
                        return;
                    }

                    // 没有 WebView2 也要能卸载：直接走静默逻辑
                    Uninstaller.Run(_dir, false, new Progress<(int, string)>((_) => { }));
                    MessageBox.Show("已卸载。", Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    Close();
                    return;
                }

                await _web.EnsureCoreWebView2Async(null);
            }
            else
            {
                var userData = Path.Combine(Path.GetTempPath(), "class-manager-uninstall-webview");
                var env = await CoreWebView2Environment.CreateAsync(runtime, userData);
                await _web.EnsureCoreWebView2Async(env);
            }

            var settings = _web.CoreWebView2.Settings;
            settings.AreDevToolsEnabled = false;
            settings.AreDefaultContextMenusEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.IsZoomControlEnabled = false;

            _web.CoreWebView2.AddHostObjectToScript("uninstaller", _bridge);

            _web.CoreWebView2.NavigationCompleted += (_, _) =>
            {
                if (_splash.Visible)
                {
                    _splash.Visible = false;
                    _splash.Dispose();
                }

                // 强制让 WebView2 跟着窗口尺寸重排一次：
                // 否则它可能仍按创建时的大小排版，页面右侧（按钮）会被裁掉。
                _web.Dock = DockStyle.None;
                _web.Bounds = ClientRectangle;
                _web.Dock = DockStyle.Fill;
            };

            _web.CoreWebView2.NavigateToString(EmbeddedUi.BuildHtml());
        }
        catch (Exception ex)
        {
            MessageBox.Show("卸载界面初始化失败：" + ex.Message + "\n\n将改用静默卸载。",
                Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            Uninstaller.Run(_dir, false, new Progress<(int, string)>((_) => { }));
            Close();
        }
    }

    private static string? FindFixedRuntime()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "WebView2Fixed");
        return File.Exists(Path.Combine(dir, "msedgewebview2.exe")) ? dir : null;
    }

    public void PostProgress(int percent, string text)
    {
        var json = JsonSerializer.Serialize(new { type = "progress", percent, text });
        PostJson(json);
    }

    public void PostDone(string keepDataDir = "")
    {
        PostJson(JsonSerializer.Serialize(new { type = "done", keepDataDir }));
    }

    /// <summary>完成页停留片刻后自动关窗（关窗后由后台清理残留目录）。</summary>
    public void AutoCloseSoon(int delayMs)
    {
        Task.Delay(delayMs).ContinueWith(_ =>
        {
            try { BeginInvoke(new Action(Close)); } catch { }
        });
    }

    public void PostError(string message)
    {
        PostJson(JsonSerializer.Serialize(new { type = "error", message }));
    }

    private void PostJson(string json)
    {
        if (IsDisposed || !IsHandleCreated) return;

        void Send()
        {
            try { _web.CoreWebView2?.PostWebMessageAsJson(json); } catch { }
        }

        if (InvokeRequired) { try { BeginInvoke((Action)Send); } catch { } }
        else Send();
    }
}

[ComVisible(true)]
[ClassInterface(ClassInterfaceType.AutoDual)]
public class UninstallBridge
{
    private readonly UninstallForm _form;
    private bool _keepData;

    public UninstallBridge(UninstallForm form) => _form = form;

    public string GetInfo()
    {
        var dir = Program.ResolveInstallDir() ?? string.Empty;
        long size = 0;
        try
        {
            if (Directory.Exists(dir))
                size = new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch { }

        return JsonSerializer.Serialize(new
        {
            app = Program.AppName,
            dir,
            sizeMb = Math.Round(size / 1024.0 / 1024.0, 1),
        });
    }

    /// <summary>开始卸载（异步，进度用 postMessage 推给页面）。keepData=true 保留 data 目录。</summary>
    public void Start(bool keepData)
    {
        _keepData = keepData;
        var dir = Program.ResolveInstallDir();
        if (string.IsNullOrWhiteSpace(dir))
        {
            _form.PostError("找不到安装目录");
            return;
        }

        Task.Run(() =>
        {
            try
            {
                var progress = new Progress<(int percent, string text)>(p => _form.PostProgress(p.percent, p.text));
                Uninstaller.Run(dir!, keepData, progress);
                _form.PostDone(keepData ? Path.Combine(dir!, "class-manager-system", "data") : string.Empty);
            }
            catch (Exception ex)
            {
                _form.PostError(ex.Message);
            }
        });
    }

    public void CloseWindow()
    {
        // 卸载程序自己就在安装目录里，进程存活时删不掉自己；
        // 关窗后由 cmd 延时把整个目录（含本程序）清掉。
        var dir = Program.ResolveInstallDir();
        if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
        {
            if (_keepData)
            {
                // 只清掉卸载程序自己（data 目录保留）
                var selfExe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(selfExe) && File.Exists(selfExe))
                    Uninstaller.ScheduleFileDelete(selfExe!);
            }
            else
            {
                Uninstaller.ScheduleTreeDelete(dir!);
            }
        }

        _form.BeginInvoke(new Action(() => _form.Close()));
    }
}

/// <summary>实际卸载动作。</summary>
internal static class Uninstaller
{
    public static void Run(string dir, bool keepData, IProgress<(int percent, string text)> progress)
    {
        dir = Path.GetFullPath(dir);

        progress.Report((10, "正在停止程序…"));
        KillAppProcesses(dir);

        progress.Report((25, "正在清理快捷方式…"));
        RemoveShortcuts();

        progress.Report((40, "正在移除开机自启…"));
        RemoveAutostart();

        progress.Report((55, "正在清理注册表…"));
        RemoveRegistry();

        var dataDir = Path.Combine(dir, "class-manager-system", "data");
        if (keepData && !Directory.Exists(dataDir))
        {
            // 没有数据目录可保留，等同于完全卸载
            keepData = false;
        }

        if (keepData)
        {
            progress.Report((70, "正在删除程序文件（保留数据）…"));
            DeleteExceptData(dir);
            progress.Report((100, "卸载完成，成绩数据已保留"));
        }
        else
        {
            progress.Report((70, "正在删除安装目录…"));
            DeleteTree(dir);
            progress.Report((100, "卸载完成"));
        }
    }

    private static void KillAppProcesses(string dir)
    {
        // 1) 主程序：按进程名全杀（exe 名是我们独有的 class-manager）
        foreach (var proc in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Program.ExeName)))
        {
            try
            {
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(3000);
            }
            catch { }
        }

        // 2) 后端 python：先按可执行文件路径匹配
        var killed = new HashSet<int>();
        foreach (var proc in Process.GetProcessesByName("python"))
        {
            try
            {
                var path = proc.MainModule?.FileName;
                if (path is null || !IsInside(dir, path)) continue;
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(2000);
                killed.Add(proc.Id);
            }
            catch { }
        }

        // 3) 路径取不到（权限不足）时，用 wmic 查命令行再补刀
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
                    if (!line.Contains(dir, StringComparison.OrdinalIgnoreCase)) continue;

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

        // 等文件句柄释放，否则删目录会漏文件
        for (var i = 0; i < 10; i++)
        {
            Thread.Sleep(300);
            if (!HasAppProcess(dir)) break;
        }
    }

    /// <summary>安装目录下还有没有活着的进程。</summary>
    private static bool HasAppProcess(string dir)
    {
        foreach (var name in new[] { Path.GetFileNameWithoutExtension(Program.ExeName), "python" })
        {
            foreach (var proc in Process.GetProcessesByName(name))
            {
                try
                {
                    var path = proc.MainModule?.FileName;
                    if (path is not null && IsInside(dir, path)) return true;
                }
                catch { }
            }
        }
        return false;
    }

    private static void RemoveShortcuts()
    {
        Try(() => File.Delete(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Program.AppName + ".lnk")));

        Try(() =>
        {
            var menu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                Program.AppName);
            if (Directory.Exists(menu)) Directory.Delete(menu, recursive: true);
        });
    }

    private static void RemoveAutostart()
    {
        Try(() =>
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            key?.DeleteValue("class-manager", throwOnMissingValue: false);
        });
    }

    private static void RemoveRegistry()
    {
        Try(() => Registry.CurrentUser.DeleteSubKeyTree(Program.AppKeyPath, throwOnMissingSubKey: false));
        Try(() => Registry.CurrentUser.DeleteSubKeyTree(Program.UninstallKeyPath, throwOnMissingSubKey: false));
    }

    /// <summary>删掉除 class-manager-system\data 之外的所有内容（保留成绩数据）。</summary>
    private static void DeleteExceptData(string dir)
    {
        var keep = Path.Combine(dir, "class-manager-system", "data");

        try
        {
            foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                if (file.StartsWith(keep, StringComparison.OrdinalIgnoreCase)) continue;
                Try(() => File.Delete(file));
            }

            foreach (var sub in Directory.GetDirectories(dir, "*", SearchOption.AllDirectories)
                         .OrderByDescending(p => p.Length))
            {
                // 跳过 data 本身、它下面的子目录，以及它的上级目录（否则会把 data 一起带走）
                if (sub.StartsWith(keep, StringComparison.OrdinalIgnoreCase)) continue;
                if (keep.StartsWith(sub, StringComparison.OrdinalIgnoreCase)) continue;
                Try(() => Directory.Delete(sub, recursive: true));
            }
        }
        catch { }
    }

    /// <summary>安排删除单个文件（本进程退出后执行）。</summary>
    public static void ScheduleFileDelete(string path)
    {
        Try(() => Process.Start(new ProcessStartInfo("cmd.exe",
            "/c timeout /t 3 /nobreak >nul & del /f /q \"" + path + "\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        }));
    }

    public static void ScheduleTreeDelete(string dir)
    {
        Try(() => Process.Start(new ProcessStartInfo("cmd.exe",
            "/c timeout /t 3 /nobreak >nul & rmdir /s /q \"" + dir + "\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        }));
    }

    private static void DeleteTree(string dir)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            Try(() => Directory.Delete(dir, recursive: true));
            if (!Directory.Exists(dir)) return;
            Thread.Sleep(400);
        }

        ScheduleTreeDelete(dir);
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

    private static void Try(Action action)
    {
        try { action(); } catch { }
    }
}

/// <summary>WebView2 起来之前的原生启动画面。</summary>
internal static class SetupSplash
{
    public static Panel Build()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(246, 249, 255),
            ColumnCount = 1,
            RowCount = 4,
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

        var logo = new PictureBox
        {
            SizeMode = PictureBoxSizeMode.Zoom,
            Size = new Size(88, 88),
            Anchor = AnchorStyles.None,
            Margin = new Padding(0, 0, 0, 16),
        };

        try
        {
            var image = EmbeddedUi.LoadLogoImage();
            if (image is not null) logo.Image = image;
        }
        catch { }

        var title = new Label
        {
            Text = Program.AppName,
            Font = new Font("Microsoft YaHei UI", 15f, FontStyle.Bold),
            ForeColor = Color.FromArgb(15, 23, 42),
            AutoSize = true,
            Anchor = AnchorStyles.None,
            Margin = new Padding(0),
        };

        var hint = new Label
        {
            Text = "正在准备卸载程序…",
            Font = new Font("Microsoft YaHei UI", 9.5f),
            ForeColor = Color.FromArgb(148, 163, 184),
            AutoSize = true,
            Anchor = AnchorStyles.None,
            Margin = new Padding(0, 8, 0, 0),
        };

        var wrap = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 2,
            Anchor = AnchorStyles.None,
        };
        wrap.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        wrap.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        panel.Controls.Add(logo, 0, 1);
        wrap.Controls.Add(title, 0, 0);
        wrap.Controls.Add(hint, 0, 1);
        panel.Controls.Add(wrap, 0, 2);

        return panel;
    }
}

/// <summary>把内嵌的 HTML/CSS/JS/图标拼成一个自包含页面（单文件 exe 里没有 wwwroot 目录）。</summary>
internal static class EmbeddedUi
{
    private static Assembly Asm => Assembly.GetExecutingAssembly();

    public static Stream? OpenResource(string name) =>
        Asm.GetManifestResourceStream(name);

    public static string ReadText(string name)
    {
        using var stream = OpenResource(name) ?? throw new InvalidOperationException("缺少界面资源：" + name);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    public static Image? LoadLogoImage()
    {
        using var stream = OpenResource("ui.logo.ico");
        if (stream is null) return null;
        using var icon = new Icon(stream);
        return icon.ToBitmap();
    }

    /// <summary>生成完整 HTML：样式内联到 &lt;style&gt;，脚本内联到 &lt;script&gt;，图标转 data URI。</summary>
    public static string BuildHtml()
    {
        var html = ReadText("ui.index.html");
        var appCss = ReadText("ui.app.css");
        var pageCss = ReadText("ui.uninstall.css");
        var script = ReadText("ui.uninstall.js");

        var logoData = ToDataUri("ui.logo.ico");

        html = html.Replace("<link rel=\"stylesheet\" href=\"app.css\">", "<style>" + appCss + "</style>");
        html = html.Replace("<link rel=\"stylesheet\" href=\"uninstall.css\">", "<style>" + pageCss + "</style>");
        html = html.Replace("<script src=\"uninstall.js\"></script>", "<script>" + script + "</script>");
        html = html.Replace("src=\"logo.ico\"", "src=\"" + logoData + "\"");

        return html;
    }

    private static string ToDataUri(string resource)
    {
        using var stream = OpenResource(resource);
        if (stream is null) return string.Empty;

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return "data:image/x-icon;base64," + Convert.ToBase64String(buffer.ToArray());
    }
}
