// 班级管理系统 —— 桌面外壳
// Copyright (C) 2026 liang-rising-star
// 本程序依据 GNU GPL v3 发布，不含任何担保；详见仓库根目录的 LICENSE。

using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ClassManager.Desktop;

internal static class Program
{
    /// <summary>命令行传入的项目根目录（可选）：--root "D:\class-manager"</summary>
    public static string? RootOverride { get; private set; }

    /// <summary>单实例互斥体；整体重启时会先释放，交给新进程。</summary>
    /// <summary>主窗口的 WebView2 环境，隐藏打印窗体复用它（共享 cookie 与浏览器进程）。</summary>
    internal static CoreWebView2Environment? BrowserEnvironment;

    /// <summary>启动时解析出的项目根目录。</summary>
    internal static string? RootPath;

    /// <summary>主窗口的 WebView2，打印进度通过它 postMessage 给页面。</summary>
    internal static WebView2? MainView;

    internal static Mutex? InstanceMutex;
    internal static EventWaitHandle? ShowWindowEvent;

    /// <summary>按安装目录算一个短哈希，用于区分不同副本的单实例。</summary>
    private static string InstanceKey()
    {
        var dir = (RootOverride ?? AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant();
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(dir));
        return Convert.ToHexString(hash, 0, 4);
    }

    [STAThread]
    private static void Main(string[] args)
    {
        // 必须最先设置：让进程按显示器 DPI 渲染，否则 150% 缩放下整个窗口被位图拉伸而发糊
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] is "--root" or "-r") RootOverride = args[i + 1];
        }

        // 单实例：同一个安装目录只允许跑一个；不同副本（比如打包版 vs 开发版）互不干扰。
        // 名称里带上安装目录的哈希，避免"启动打包版却把开发版叫到前台"这种串台。
        var instanceKey = InstanceKey();
        InstanceMutex = new Mutex(initiallyOwned: true, $@"Local\class-manager-{instanceKey}", out var isFirstInstance);
        ShowWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\class-manager-show-{instanceKey}");

        if (!isFirstInstance)
        {
            try { ShowWindowEvent.Set(); } catch { }
            return;
        }

        // --tray：开机自启时只驻留托盘，不自动弹主窗口
        var startInTray = args.Contains("--tray");

        try
        {
            using var context = new TrayApplicationContext(startInTray);

            if (args.Contains("--preview-menu"))
            {
                var timer = new System.Windows.Forms.Timer { Interval = 1500 };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    timer.Dispose();
                    context.ShowTrayMenuPreview();
                };
                timer.Start();
            }

            // 另一个实例启动时会 Set 这个事件，这里把主窗口叫到前台
            var listener = new Thread(() =>
            {
                while (true)
                {
                    ShowWindowEvent.WaitOne();
                    context.PostShowMainWindow();
                }
            })
            { IsBackground = true, Name = "show-window-listener" };
            listener.Start();

            Application.Run(context);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "班级管理系统", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

/// <summary>托盘宿主：负责起后端、开 WebView2 窗口、提供托盘右键菜单。</summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly BackendServer _server;
    private readonly MainForm _form;
    private readonly NotifyIcon _tray;
    private readonly string _launchArgs;
    private readonly SynchronizationContext _uiContext;
    private bool _exiting;

    public TrayApplicationContext(bool startInTray)
    {
        var root = AppPaths.FindProjectRoot()
                   ?? throw new InvalidOperationException(
                       "未找到程序目录。请把本程序放在 class-manager 根目录下（与 class-manager-system 同级）。");

        // 只保留 --root 之后的参数，重启时原样传给新进程
        var all = Environment.GetCommandLineArgs().Skip(1)
            .Where(a => a is not "--preview-menu" and not "--test-restart" and not "--tray")
            .ToArray();
        _launchArgs = string.Join(" ", all.Select(Quote));

        Program.RootPath = root;
        ApplyAutostart(root);

        // 记录 UI 线程上下文：托盘线程/监听线程靠它切回主线程
        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        _server = new BackendServer(root);

        // 先把窗口和托盘准备好并显示（秒开），后端在后台起；
        // 窗口里先放本地启动页，后端就绪后再切到真正的页面。
        _form = new MainForm(null, AppPaths.LoadIcon(root));
        _form.FormClosing += OnFormClosing;

        _tray = new NotifyIcon
        {
            Icon = AppPaths.LoadIcon(root, SystemInformation.SmallIconSize),
            Text = "班级管理系统",
            Visible = true
        };
        _tray.DoubleClick += (_, _) => ShowMainWindow();
        _tray.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Right) ShowTrayMenu(Cursor.Position);
        };

        // 图标放在右下角 ^ 折叠区即可，不强行改用户的托盘设置；
        // 只在驻留托盘（--tray，开机自启）时提示一次图标在哪。
        if (startInTray)
        {
            _tray.BalloonTipTitle = "班级管理系统";
            _tray.BalloonTipText = "程序已在后台运行，图标在右下角 ^ 折叠区里，双击即可打开主界面";
            _tray.BalloonTipIcon = ToolTipIcon.Info;
            _tray.ShowBalloonTip(4000);
        }

        // 开机自启（--tray）时只驻留托盘，其余情况正常开窗
        if (!startInTray) ShowMainWindow();

        // 后台拉起 Python 后端，就绪后把窗口切到真实页面
        Task.Run(() =>
        {
            try
            {
                var url = _server.Start();
                _uiContext.Post(_ => _form.NavigateTo(url), null);
            }
            catch (Exception ex)
            {
                AppLog.Write("backend failed: " + ex.Message);
                _uiContext.Post(_ =>
                {
                    MessageBox.Show(ex.Message, "班级管理系统",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    ExitThread();
                }, null);
            }
        });
    }

    /// <summary>按 data/config.json 里的 desktop.autostart 增删开机启动项（当前用户 Run 键）。</summary>
    private static void ApplyAutostart(string root)
    {
        const string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string valueName = "class-manager";

        try
        {
            var configPath = Path.Combine(root, "class-manager-system", "data", "config.json");

            using var key = Registry.CurrentUser.OpenSubKey(runKey, writable: true);
            if (key is null) return;

            var registered = key.GetValue(valueName) is not null;

            // 首次启动（安装完还没生成 config）：把安装程序登记的注册表状态先落到配置里，
            // 否则设置页会显示"未开启"，用户得开关两次才能关掉。
            if (!File.Exists(configPath))
            {
                if (registered && SetConfigAutostart(configPath, true))
                    AppLog.Write("autostart: 首次启动，按注册表初始化配置为开启");
                return;
            }

            var enabled = false;
            using (var doc = JsonDocument.Parse(File.ReadAllText(configPath)))
            {
                if (doc.RootElement.TryGetProperty("desktop", out var desktop) &&
                    desktop.TryGetProperty("autostart", out var flag))
                {
                    enabled = flag.ValueKind == JsonValueKind.True;
                }
            }

            // 不一致时以注册表为准（安装程序 / 卸载程序都只动注册表）
            if (registered && !enabled)
            {
                if (SetConfigAutostart(configPath, true))
                    AppLog.Write("autostart: 注册表已启用，配置同步为开启");
                return;
            }

            if (enabled)
            {
                var exe = Environment.ProcessPath ?? Application.ExecutablePath;
                key.SetValue(valueName, $"\"{exe}\" --root \"{root}\" --tray");
                AppLog.Write("autostart: 已登记开机启动");
            }
            else if (registered)
            {
                key.DeleteValue(valueName, throwOnMissingValue: false);
                AppLog.Write("autostart: 已取消开机启动");
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("autostart failed: " + ex.Message);
        }
    }

    /// <summary>把 config.json 里的 desktop.autostart 改成指定值，成功返回 true。</summary>
    private static bool SetConfigAutostart(string configPath, bool value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);

            System.Text.Json.Nodes.JsonObject root;
            if (File.Exists(configPath))
            {
                if (System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(configPath))
                    is not System.Text.Json.Nodes.JsonObject parsed) return false;
                root = parsed;
            }
            else
            {
                // 安装完第一次启动时还没有 config：先建一个最小骨架，
                // 后端启动后 read_config 会把默认项补全
                root = new System.Text.Json.Nodes.JsonObject();
            }

            if (root["desktop"] is not System.Text.Json.Nodes.JsonObject desktop)
            {
                desktop = new System.Text.Json.Nodes.JsonObject();
                root["desktop"] = desktop;
            }

            desktop["autostart"] = value;

            var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(configPath, json, new System.Text.UTF8Encoding(false));
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Write("autostart sync failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>在指定屏幕坐标弹出托盘菜单（自绘圆角弹窗，一体机端以此为准）。</summary>
    private void ShowTrayMenu(Point screenPoint)
    {
        var menu = new TrayMenu();
        menu.OpenRequested += ShowMainWindow;
        menu.RestartRequested += RestartApplication;
        menu.ExitRequested += ExitApplication;

        var area = Screen.FromPoint(screenPoint).WorkingArea;
        int x = Math.Min(screenPoint.X, area.Right - menu.Width - 8);
        int y = Math.Min(screenPoint.Y, area.Bottom - menu.Height - 8);

        menu.StartPosition = FormStartPosition.Manual;
        menu.Location = new Point(Math.Max(area.Left + 8, x), Math.Max(area.Top + 8, y));
        menu.Show();
    }

    /// <summary>调试用：把托盘菜单显示在屏幕固定位置，方便截图确认样式。</summary>
    public void ShowTrayMenuPreview()
    {
        ShowTrayMenu(new Point(600, 300));
    }

    /// <summary>
    /// 整体重启：收起托盘图标、关闭窗口、停掉后端，然后以相同参数启动新进程。
    /// 用户看到的效果就是“图标和窗口都消失，随后自动重新出现”。
    /// </summary>
    private void RestartApplication()
    {
        if (_exiting) return;
        _exiting = true;

        try
        {
            AppLog.Write("restart app: 关闭当前实例并拉起新进程");

            // 先让图标和窗口消失
            _tray.Visible = false;
            _tray.Dispose();
            _form.Hide();

            // 停掉自己拉起的后端，等端口释放，避免新实例立刻启动时端口冲突
            _server.StopAndWait();

            // 先释放单实例互斥体，否则新进程会被判定为“已有实例”而直接退出
            try { Program.InstanceMutex?.ReleaseMutex(); } catch { }
            Program.InstanceMutex?.Dispose();
            Program.InstanceMutex = null;

            var exe = Environment.ProcessPath ?? Application.ExecutablePath;
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = _launchArgs,
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            AppLog.Write("restart app FAILED: " + ex.Message);
            MessageBox.Show("重启失败：" + ex.Message, "班级管理系统",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _form.Close();
            ExitThread();
            Application.Exit();
        }
    }

    private static string Quote(string arg) =>
        arg.Contains(' ') ? $"\"{arg}\"" : arg;

    /// <summary>供后台线程调用：切回 UI 线程显示主窗口（窗口句柄可能还没创建，必须走上下文投递）。</summary>
    public void PostShowMainWindow()
    {
        try
        {
            _uiContext.Post(_ => ShowMainWindow(), null);
        }
        catch
        {
            // 退出阶段忽略
        }
    }

    private void ShowMainWindow()
    {
        if (!_form.Visible) _form.Show();
        if (_form.WindowState == FormWindowState.Minimized) _form.WindowState = FormWindowState.Normal;
        _form.Activate();
        _form.BringToFront();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        // 点窗口上的 × 只是收进托盘，真正退出走托盘菜单
        if (_exiting) return;
        e.Cancel = true;
        _form.Hide();
    }

    private void ExitApplication()
    {
        if (_exiting) return;
        _exiting = true;

        _tray.Visible = false;
        _tray.Dispose();
        _server.Stop();
        _form.Close();
        ExitThread();
    }

}

/// <summary>内嵌 Python 后端的生命周期管理。</summary>
internal sealed class BackendServer
{
    private const int PreferredPort = 3445;

    private readonly string _root;
    private Process? _process;
    private bool _reused;

    public BackendServer(string root) => _root = root;

    public string Url => $"http://127.0.0.1:{Port}";

    public int Port { get; private set; }

    public string Start() => StartCore(allowReuse: true);

    /// <summary>重启后端：停掉当前进程，等端口释放后重新拉起。</summary>
    public string Restart()
    {
        var port = Port;
        if (_reused)
        {
            // 复用的是别的实例留下的后端，我们停不掉它，只能另起一个
            _reused = false;
            AppLog.Write("restart: 跳过复用实例，重新拉起自己的后端");
        }
        else
        {
            Stop();
            WaitPortFree(port, TimeSpan.FromSeconds(6));
        }
        return StartCore(allowReuse: false);
    }

    private string StartCore(bool allowReuse)
    {
        // 3445 上已经是本应用（比如上次没退干净）就直接复用，不再起第二个后端
        if (allowReuse && LooksLikeOurApp(PreferredPort))
        {
            Port = PreferredPort;
            _reused = true;
            AppLog.Write($"backend reuse: 已有实例在 {PreferredPort}");
            return Url;
        }

        var appDir = Path.Combine(_root, "class-manager-system");
        var python = Path.Combine(appDir, "python", "python.exe");

        if (!File.Exists(python))
            throw new FileNotFoundException(
                "未找到内嵌 Python 运行时：" + python +
                "\n请确认 class-manager-system\\python 目录完整（该目录不入库，需单独放置）。");

        Port = FindFreePort(PreferredPort);

        var psi = new ProcessStartInfo
        {
            FileName = python,
            Arguments = $"-m uvicorn main:app --host 127.0.0.1 --port {Port}",
            WorkingDirectory = appDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        // 让后端知道桌面端的 exe 与根目录，设置「开机自启」时可直接写注册表（无需重启）
        psi.Environment["CLASSMANAGER_DESKTOP_EXE"] = Environment.ProcessPath ?? Application.ExecutablePath;
        psi.Environment["CLASSMANAGER_ROOT"] = _root;

        _process = Process.Start(psi)
                   ?? throw new InvalidOperationException("无法启动后端服务");

        // 保留最近几行输出，启动失败时能看到真实原因
        var stderrTail = new List<string>();

        // 不读就会塞满管道
        _process.OutputDataReceived += (_, _) => { };
        _process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            lock (stderrTail)
            {
                stderrTail.Add(e.Data);
                if (stderrTail.Count > 20) stderrTail.RemoveAt(0);
            }
        };
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        AppLog.Write($"backend start: {python} {psi.Arguments} (cwd={appDir})");

        if (!WaitForPort(TimeSpan.FromSeconds(25)))
        {
            string detail;
            lock (stderrTail) detail = string.Join("\n", stderrTail);
            Stop();
            AppLog.Write("backend start FAILED: " + detail);
            throw new Exception(
                $"后端服务未能启动（端口 {Port}）。\n\n" +
                (string.IsNullOrWhiteSpace(detail) ? "进程无输出，可能 python 运行时不完整。" : detail));
        }

        AppLog.Write($"backend ready on {Url}");

        return Url;
    }

    /// <summary>停掉后端并等待端口释放，供整体重启时使用。</summary>
    public void StopAndWait()
    {
        var port = Port;
        Stop();
        if (port > 0) WaitPortFree(port, TimeSpan.FromSeconds(6));
    }

    public void Stop()
    {
        if (_reused) return;   // 复用已有实例时不动它

        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(3000);
            }
        }
        catch
        {
            // 退出阶段忽略
        }
        finally
        {
            _process?.Dispose();
            _process = null;
        }
    }

    /// <summary>端口上跑的是不是本应用（看登录页是否含我们的样式类）。</summary>
    private static bool LooksLikeOurApp(int port)
    {
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMilliseconds(900) };
            var html = http.GetStringAsync($"http://127.0.0.1:{port}/login").GetAwaiter().GetResult();
            return html.Contains("auth-card", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static void WaitPortFree(int port, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var probe = new TcpListener(IPAddress.Loopback, port);
                probe.Start();
                probe.Stop();
                return;
            }
            catch (SocketException)
            {
                Thread.Sleep(150);
            }
        }
    }

    private bool WaitForPort(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (_process is { HasExited: true }) return false;
            try
            {
                using var client = new TcpClient();
                var task = client.ConnectAsync(IPAddress.Loopback, Port);
                if (task.Wait(400) && client.Connected) return true;
            }
            catch
            {
                // 还没起来，继续等
            }
            Thread.Sleep(200);
        }
        return false;
    }

    private static int FindFreePort(int start)
    {
        for (var port = start; port < start + 30; port++)
        {
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
                return port;
            }
            catch (SocketException)
            {
                // 被占用，试下一个
            }
        }
        throw new InvalidOperationException($"找不到可用端口（{PreferredPort}-{PreferredPort + 29}）");
    }
}

/// <summary>WebView2 主窗口。</summary>
internal sealed class MainForm : Form
{
    private readonly WebView2 _web;
    private string _url;

    public MainForm(string? url, Icon? icon)
    {
        _url = url;

        Text = "班级管理系统";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(880, 600);
        BackColor = Color.White;
        if (icon is not null) Icon = icon;

        // 先把窗口尺寸定好，再创建 WebView2：
        // 否则 WebView2 会先用默认尺寸排版，启动页内容要等重排后才居中。
        ApplyWindowSize();

        _web = new WebView2 { Dock = DockStyle.Fill };
        Controls.Add(_web);

        Load += async (_, _) =>
        {
            await InitializeWebViewAsync();
        };
    }

    /// <summary>弹出“另存为”，让用户决定导出文件保存到哪里。</summary>
    /// <summary>把下载结果发给页面（用户取消 / 真正写完盘）。</summary>
    private static void NotifyDownload(string filename, string state)
    {
        try
        {
            var view = Program.MainView;
            if (view?.CoreWebView2 is null) return;
            view.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new
            {
                type = "download",
                state,
                filename
            }));
        }
        catch { }
    }

    private void ShowSaveDialog(CoreWebView2DownloadStartingEventArgs e)
    {
        try
        {
            var suggested = Path.GetFileName(e.ResultFilePath);
            if (string.IsNullOrWhiteSpace(suggested)) suggested = "积分卡.docx";

            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            using var dialog = new SaveFileDialog
            {
                Title = "保存导出的文件",
                FileName = suggested,
                InitialDirectory = Directory.Exists(docs) ? docs : null,
                Filter = "Word 文档 (*.docx)|*.docx|所有文件 (*.*)|*.*",
                OverwritePrompt = true,
                AddExtension = true
            };

            var fileName = Path.GetFileName(e.ResultFilePath);
            if (string.IsNullOrWhiteSpace(fileName)) fileName = suggested;

            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                e.ResultFilePath = dialog.FileName;
                e.Handled = true;      // 使用我们指定的路径，不再弹 Edge 自己的下载提示

                // 只有真正写盘完成才通知页面；中断/取消时通知取消
                var operation = e.DownloadOperation;
                if (operation is not null)
                {
                    operation.StateChanged += (_, _) =>
                    {
                        if (operation.State == CoreWebView2DownloadState.Completed)
                        {
                            NotifyDownload(fileName, "completed");
                        }
                        else if (operation.State == CoreWebView2DownloadState.Interrupted)
                        {
                            NotifyDownload(fileName, "cancelled");
                        }
                    };
                }
            }
            else
            {
                e.Cancel = true;       // 用户取消，直接中止下载
                NotifyDownload(fileName, "cancelled");
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("download dialog failed: " + ex.Message);
        }
    }

    /// <summary>按屏幕工作区收缩并居中（全部用像素计算，避开 DIP 换算）。</summary>
    private void ApplyWindowSize()
    {
        // 构造阶段窗口还没显示，Screen.FromControl 可能拿到另一块显示器，
        // 导致窗口比主屏还宽、内容偏到右侧。未显示时以主屏为准。
        var screen = Visible ? Screen.FromControl(this) : (Screen.PrimaryScreen ?? Screen.FromControl(this));
        var area = screen.WorkingArea;

        var width = Math.Max(900, (int)(area.Width * 0.86));
        var height = Math.Max(620, (int)(area.Height * 0.86));

        MinimumSize = new Size(880, 600);
        StartPosition = FormStartPosition.Manual;
        SetBounds(
            area.X + (area.Width - width) / 2,
            area.Y + (area.Height - height) / 2,
            width,
            height);
    }

    private bool _lastSystemDark;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ApplyWindowSize();   // 布局完成后再定位，CenterToScreen 才不会用旧尺寸
        _lastSystemDark = WindowEffects.IsSystemDark();
        WindowEffects.Apply(Handle);   // Win11 圆角 + Mica 毛玻璃标题栏
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        // 系统在浅色/深色之间切换过 → 重新套用，否则标题栏按钮会和背景同色
        var dark = WindowEffects.IsSystemDark();
        if (dark != _lastSystemDark)
        {
            _lastSystemDark = dark;
            WindowEffects.Apply(Handle);
        }
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        // WM_THEMECHANGED / WM_SETTINGCHANGE：主题或个性化设置变化
        if (m.Msg == 0x031A || m.Msg == 0x001A)
        {
            var dark = WindowEffects.IsSystemDark();
            _lastSystemDark = dark;
            WindowEffects.Apply(Handle);
        }
    }

    /// <summary>重启后导航到（可能是新的）地址；地址没变就刷新。</summary>
    public void NavigateTo(string url)
    {
        if (string.Equals(_url, url, StringComparison.OrdinalIgnoreCase))
        {
            _web.CoreWebView2?.Reload();
            return;
        }
        _url = url;
        if (_web.CoreWebView2 is not null) _web.Source = new Uri(url);
    }

    /// <summary>程序旁边若有「固定版」WebView2 运行时，就用它，不再依赖系统安装。</summary>
    internal static string? FindFixedWebViewRuntime()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "WebView2Fixed"),
            Path.Combine(AppContext.BaseDirectory, "..", "WebView2Fixed"),
        };

        foreach (var dir in candidates)
        {
            var full = Path.GetFullPath(dir);
            if (File.Exists(Path.Combine(full, "msedgewebview2.exe"))) return full;
        }

        return null;
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            var fixedRuntime = FindFixedWebViewRuntime();

            if (fixedRuntime is null)
            {
                // 没有固定版运行时，检查系统是否装了
                try
                {
                    var version = CoreWebView2Environment.GetAvailableBrowserVersionString();
                    if (string.IsNullOrEmpty(version))
                        throw new InvalidOperationException("未检测到 WebView2 运行时");
                }
                catch (Exception checkEx)
                {
                    var answer = MessageBox.Show(
                        "本程序需要「Microsoft Edge WebView2 运行时」才能显示界面。\n\n" +
                        "点「是」打开官方下载页，安装完成后重新打开本程序即可。\n" +
                        "（Windows 10/11 通常自带；技术细节：" + checkEx.Message + "）",
                        "班级管理系统", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

                    if (answer == DialogResult.Yes)
                    {
                        try
                        {
                            Process.Start(new ProcessStartInfo(
                                "https://go.microsoft.com/fwlink/p/?LinkId=2124703")
                            { UseShellExecute = true });
                        }
                        catch { }
                    }

                    return;
                }

                await _web.EnsureCoreWebView2Async(null);
            }
            else
            {
                var userData = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "class-manager", "WebView2");
                var env = await CoreWebView2Environment.CreateAsync(fixedRuntime, userData);
                await _web.EnsureCoreWebView2Async(env);
            }

            var settings = _web.CoreWebView2.Settings;
            settings.AreDevToolsEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.AreDefaultContextMenusEnabled = false;
            settings.IsZoomControlEnabled = false;

            // 班级监控的「对讲」要用麦克风。桌面版里这个 WebView 只加载本程序自己的页面，
            // 所以直接放行麦克风/摄像头请求（无论页面是本机 127.0.0.1 还是别的地址）。
            _web.CoreWebView2.PermissionRequested += (_, e) =>
            {
                bool isMic = e.PermissionKind == CoreWebView2PermissionKind.Microphone;
                bool isCam = e.PermissionKind == CoreWebView2PermissionKind.Camera;
                if (!isMic && !isCam) return;

                e.State = CoreWebView2PermissionState.Allow;
                e.Handled = true;
            };

            // 页面里的外链走系统浏览器，不在应用内开新窗口
            _web.CoreWebView2.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                try { Process.Start(new ProcessStartInfo(e.Uri) { UseShellExecute = true }); } catch { }
            };

            // 下载（导出 Word）走系统“另存为”，由用户选位置，避免默认目录不可写时报错
            _web.CoreWebView2.DownloadStarting += (_, e) => ShowSaveDialog(e);

            // 页面里的“选择旧版安装目录”调用原生对话框（DPI 正常、带本程序图标）
            try
            {
                Program.BrowserEnvironment = _web.CoreWebView2.Environment;
                Program.MainView = _web;
                _web.CoreWebView2.AddHostObjectToScript("desktopBridge", new DesktopBridge(this));
                AppLog.Write("host bridge registered");
            }
            catch (Exception ex)
            {
                AppLog.Write("host bridge failed: " + ex.Message);
            }

            if (string.IsNullOrEmpty(_url))
                _web.NavigateToString(BuildSplashHtml());
            else
                _web.Source = new Uri(_url);

        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "WebView2 初始化失败：" + ex.Message +
                "\n\n请确认已安装 Microsoft Edge WebView2 运行时。",
                "班级管理系统", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>后端还没起来时显示的启动页：品牌动画 + 「正在启动服务」。</summary>
    private static string BuildSplashHtml()
    {
        var logo = string.Empty;
        try
        {
            var root = Program.RootPath;
            string[] candidates =
            {
                Path.Combine(root, "logo.ico"),
                Path.Combine(root, "class-manager-system", "logo.ico"),
            };

            foreach (var path in candidates)
            {
                if (!File.Exists(path)) continue;
                logo = "data:image/x-icon;base64," + Convert.ToBase64String(File.ReadAllBytes(path));
                break;
            }
        }
        catch { }

        var logoTag = string.IsNullOrEmpty(logo)
            ? "<div class='mark'></div>"
            : $"<img class='logo' src='{logo}' alt=''>";

        return @"<!DOCTYPE html><html><head><meta charset='utf-8'>
<style>
  html,body{height:100%;margin:0;background:#fff;font-family:'Microsoft YaHei UI',system-ui,sans-serif;
            overflow:hidden;-webkit-user-select:none;user-select:none}
  .wrap{position:fixed;inset:0;display:flex;flex-direction:column;align-items:center;justify-content:center}
  .ring{position:relative;width:132px;height:132px;display:grid;place-items:center}
  .ring i{position:absolute;inset:0;border:1px solid rgba(37,99,235,.25);border-radius:50%;
          animation:out 2.6s ease-out infinite}
  .ring i:nth-child(2){animation-delay:1.3s}
  @keyframes out{0%{transform:scale(.7);opacity:0}25%{opacity:.8}100%{transform:scale(1.35);opacity:0}}
  .logo,.mark{width:92px;height:92px;border-radius:50%;animation:pop .8s cubic-bezier(.2,.9,.25,1.15) both}
  .mark{background:linear-gradient(135deg,#2563eb,#06b6d4)}
  @keyframes pop{0%{transform:scale(.55);opacity:0}60%{transform:scale(1.06);opacity:1}100%{transform:scale(1)}}
  h1{margin:22px 0 0;font-size:22px;font-weight:700;color:#0f172a;letter-spacing:.06em;
     animation:fade .6s ease .2s both}
  .sub{margin-top:10px;font-size:13px;color:#94a3b8;animation:fade .6s ease .35s both}
  .bar{margin-top:26px;width:180px;height:4px;border-radius:999px;background:#eef2f7;overflow:hidden;
       animation:fade .6s ease .45s both}
  .bar::after{content:'';display:block;height:100%;width:42%;border-radius:999px;
              background:linear-gradient(90deg,#2563eb,#06b6d4);animation:slide 1.15s ease-in-out infinite}
  @keyframes slide{0%{transform:translateX(-100%)}100%{transform:translateX(240%)}}
  @keyframes fade{from{opacity:0;transform:translateY(6px)}to{opacity:1;transform:none}}
</style></head><body><div class='wrap'>
  <div class='ring'><i></i><i></i>" + logoTag + @"</div>
  <h1>班级管理系统</h1>
  <div class='sub'>正在启动服务…</div>
  <div class='bar'></div>
</div></body></html>";
    }

    public void Reload() => _web.CoreWebView2?.Reload();
}

/// <summary>Win11 窗口效果：圆角 + Mica 毛玻璃标题栏。</summary>
public static class WindowEffects
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;

    [StructLayout(LayoutKind.Sequential)]
    private struct ACCENT_POLICY
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWCOMPOSITIONATTRIBDATA
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WINDOWCOMPOSITIONATTRIBDATA data);

    /// <summary>Win11 亚克力：让窗口（含标题栏）呈现半透明模糊。</summary>
    private static void TryAcrylic(IntPtr handle, bool dark = false)
    {
        try
        {
            var accent = new ACCENT_POLICY
            {
                AccentState = 4,                 // ACCENT_ENABLE_ACRYLICBLURBEHIND
                AccentFlags = 2,
                // AABBGGRR：浅色 0xB3F7F9FC / 深色 0xB31A1D23
                GradientColor = dark ? unchecked((int)0xB31A1D23) : unchecked((int)0xB3F7F9FC)
            };

            var data = new WINDOWCOMPOSITIONATTRIBDATA
            {
                Attribute = 19,                  // WCA_ACCENT_POLICY
                SizeOfData = Marshal.SizeOf(typeof(ACCENT_POLICY)),
                Data = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(ACCENT_POLICY)))
            };

            try
            {
                Marshal.StructureToPtr(accent, data.Data, false);
                SetWindowCompositionAttribute(handle, ref data);
            }
            finally
            {
                Marshal.FreeHGlobal(data.Data);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("acrylic failed: " + ex.Message);
        }
    }

    /// <summary>系统「应用」是否使用深色主题（HKCU 个性化设置）。</summary>
    public static bool IsSystemDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            if (value is int i) return i == 0;
        }
        catch
        {
            // 读不到就按浅色处理
        }
        return false;
    }

    public static void Apply(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return;

        try
        {
            var corner = 2;                 // DWMWCP_ROUND
            DwmSetWindowAttribute(handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

            var backdrop = 2;               // DWMSBT_MAINWINDOW → Mica
            var result = DwmSetWindowAttribute(handle, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
            if (result != 0)
            {
                // 老一些的 Win11 用未公开属性 1029 也有同样效果
                backdrop = 1;
                DwmSetWindowAttribute(handle, 1029, ref backdrop, sizeof(int));
            }

            // 标题栏配色跟着系统主题走：
            // 以前固定成浅色 + 透明标题栏，系统在深色模式下会把 minimize/maximize/close
            // 三个按钮画成白色，配上浅色背景就完全看不见了。
            bool systemDark = IsSystemDark();
            var dark = systemDark ? 1 : 0;
            DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

            int caption, captionText, border;
            if (systemDark)
            {
                caption = unchecked((int)0x00261F1B);       // #1B1F26
                captionText = unchecked((int)0x00F0E9E5);   // #E5E9F0
                border = unchecked((int)0x00342830);        // #283428
            }
            else
            {
                caption = unchecked((int)0x00FCF9F7);       // #F7F9FC
                captionText = unchecked((int)0x002A170F);   // #0F172A
                border = unchecked((int)0x00EEE9E2);        // #E2E9EE
            }

            DwmSetWindowAttribute(handle, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
            DwmSetWindowAttribute(handle, DWMWA_TEXT_COLOR, ref captionText, sizeof(int));
            DwmSetWindowAttribute(handle, DWMWA_BORDER_COLOR, ref border, sizeof(int));

            TryAcrylic(handle, systemDark);
        }
        catch (Exception ex)
        {
            AppLog.Write("window effects failed: " + ex.Message);
        }
    }
}

/// <summary>暴露给网页脚本的桥：目前只提供原生文件夹选择。</summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.AutoDual)]
public class DesktopBridge
{
    private readonly Form _owner;

    public DesktopBridge(Form owner) => _owner = owner;

    /// <summary>弹文件选择框，让用户挑一个本地更新包（zip）。取消返回空串。</summary>
    public string PickUpdateZip()
    {
        try
        {
            using var dialog = new OpenFileDialog
            {
                Title = "选择更新包",
                Filter = "更新包 (*.zip)|*.zip|所有文件 (*.*)|*.*",
                CheckFileExists = true,
            };

            return dialog.ShowDialog(_owner) == DialogResult.OK ? dialog.FileName : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// 导入本地更新包：复制到 class-manager-system/update/update.zip（与自动下载同一位置），再交给 update.exe。
    /// 返回 "ok" 或 "failed:原因"。
    /// </summary>
    public string ImportUpdate(string sourceZip)
    {
        try
        {
            var root = Program.RootPath;
            if (string.IsNullOrEmpty(root)) return "failed: 未找到安装目录";
            if (string.IsNullOrWhiteSpace(sourceZip) || !File.Exists(sourceZip))
                return "failed: 没有选择更新包";

            var updateDir = Path.Combine(root, "class-manager-system", "update");
            Directory.CreateDirectory(updateDir);
            var target = Path.Combine(updateDir, "update.zip");

            if (!string.Equals(Path.GetFullPath(sourceZip), Path.GetFullPath(target),
                               StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(sourceZip, target, overwrite: true);
            }

            return ApplyUpdate(target);
        }
        catch (Exception ex)
        {
            return "failed: " + ex.Message;
        }
    }

    /// <summary>
    /// 应用更新：把 update.zip 交给 update.exe，然后退出本程序。
    /// 返回 "ok" 或 "failed:原因"。
    /// </summary>
    public string ApplyUpdate(string zipPath)
    {
        try
        {
            var root = Program.RootPath;
            if (string.IsNullOrEmpty(root)) return "failed: 未找到安装目录";

            var zipFull = string.IsNullOrWhiteSpace(zipPath) ? string.Empty : Path.GetFullPath(zipPath);
            if (string.IsNullOrEmpty(zipFull) || !File.Exists(zipFull))
                return "failed: 更新包不存在";

            var updater = Path.Combine(root, "update.exe");
            if (!File.Exists(updater)) return "failed: 缺少 update.exe";

            Process.Start(new ProcessStartInfo(updater)
            {
                UseShellExecute = false,
                WorkingDirectory = root,
                Arguments = $"--dir \"{root}\" --zip \"{zipFull}\" --restart",
            });

            // 交给更新程序之后就退出，让它能替换文件
            _owner.BeginInvoke(new Action(() =>
            {
                AppLog.Write("apply update: 启动 update.exe，主程序退出");
                Application.Exit();
            }));

            return "ok";
        }
        catch (Exception ex)
        {
            return "failed: " + ex.Message;
        }
    }

    /// <summary>调用系统关联程序打印 output 目录下的 PDF；返回 queued 或 failed:原因。</summary>
    public string PrintPdf(string pdfFileName, string printerName)
    {
        try
        {
            var root = Program.RootPath;
            if (string.IsNullOrEmpty(root)) return "failed: 未找到项目目录";

            var name = Path.GetFileName(pdfFileName ?? "");
            if (string.IsNullOrWhiteSpace(name)) return "failed: 文件名为空";

            var path = Path.Combine(root, "class-manager-system", "data", "output", name);
            if (!File.Exists(path)) return "failed: PDF 不存在";

            var usePrinter = !string.IsNullOrWhiteSpace(printerName);
            var psi = new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
                Verb = usePrinter ? "printto" : "print",
                Arguments = usePrinter ? $"\"{printerName}\"" : "",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            Process.Start(psi);
            AppLog.Write($"print-pdf: 已交给系统打印 {name} -> {(usePrinter ? printerName : "默认打印机")}");
            return "queued";
        }
        catch (Exception ex)
        {
            AppLog.Write("print-pdf 失败: " + ex.Message);
            return "failed: " + ex.Message;
        }
    }

    /// <summary>用系统关联程序把最近生成的 PDF 送去打印（WebView2 打印失败时的兜底）。</summary>
    private static bool TrySystemPrintPdf(string printerName)
    {
        try
        {
            var root = Program.RootPath;
            if (string.IsNullOrEmpty(root)) return false;

            var outputDir = Path.Combine(root, "class-manager-system", "data", "output");
            if (!Directory.Exists(outputDir)) return false;

            var latest = new DirectoryInfo(outputDir)
                .GetFiles("*.pdf")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            if (latest is null) return false;

            var psi = new ProcessStartInfo
            {
                FileName = latest.FullName,
                UseShellExecute = true,
                Verb = string.IsNullOrWhiteSpace(printerName) ? "print" : "printto",
                Arguments = string.IsNullOrWhiteSpace(printerName) ? "" : $"\"{printerName}\"",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            Process.Start(psi);
            AppLog.Write("print: 回退系统打印 PDF " + latest.Name);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Write("print: 系统打印 PDF 失败 " + ex.Message);
            return false;
        }
    }

    /// <summary>把打印进度推给页面（值 0-100）。</summary>
    private static void Report(int value, string text, string token, bool done = false, bool error = false)
    {
        try
        {
            var view = Program.MainView;
            if (view?.CoreWebView2 is null) return;
            var payload = new Dictionary<string, object>
            {
                ["type"] = "print-progress",
                ["value"] = value,
                ["text"] = text,
                ["done"] = done,
                ["error"] = error,
                ["token"] = token
            };
            view.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload));
        }
        catch { }
    }

    /// <summary>用隐藏的 WebView2 加载打印页并静默打印（不启动 WPS/Word）。</summary>
    public string PrintUrl(string url, string printerName, string token)
    {
        if (string.IsNullOrWhiteSpace(url)) return "failed: empty url";

        try
        {
            _owner.BeginInvoke(new Action(() => { _ = PrintHiddenAsync(url, printerName, token ?? ""); }));
            return "queued";
        }
        catch (Exception ex)
        {
            AppLog.Write("PrintUrl failed: " + ex.Message);
            return "failed: " + ex.Message;
        }
    }

    private async Task PrintHiddenAsync(string url, string printerName, string token)
    {
        Form? host = null;
        WebView2? view = null;
        try
        {
            var environment = Program.BrowserEnvironment;
            if (environment is null)
            {
                AppLog.Write("print: 浏览器环境未就绪");
                return;
            }

            host = new Form
            {
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-32000, -32000),
                Size = new Size(1123, 1587),
                FormBorderStyle = FormBorderStyle.None,
                ShowIcon = false
            };
            view = new WebView2 { Dock = DockStyle.Fill };
            host.Controls.Add(view);
            host.Show();

            Report(10, "准备打印页面…", token);
            await view.EnsureCoreWebView2Async(environment);

            var loaded = new TaskCompletionSource<bool>();
            view.CoreWebView2.NavigationCompleted += (_, e) => loaded.TrySetResult(e.IsSuccess);
            view.CoreWebView2.Navigate(url);

            Report(40, "加载卡片页面…", token);
            if (!await loaded.Task)
            {
                AppLog.Write("print: 打印页加载失败");
                Report(100, "打印页加载失败", token, done: true, error: true);
                return;
            }

            // 等条码图片与字体渲染完
            Report(62, "渲染条码与排版…", token);
            await Task.Delay(900);
            Report(78, "排版完成，准备发送…", token);

            var settings = view.CoreWebView2.Environment.CreatePrintSettings();
            settings.ShouldPrintBackgrounds = true;
            settings.ShouldPrintHeaderAndFooter = false;
            settings.MarginTop = 0;
            settings.MarginBottom = 0;
            settings.MarginLeft = 0;
            settings.MarginRight = 0;
            if (!string.IsNullOrWhiteSpace(printerName)) settings.PrinterName = printerName;

            Report(88, "正在发送到打印机…", token);
            var result = await view.CoreWebView2.PrintAsync(settings);
            var target = string.IsNullOrWhiteSpace(printerName) ? "系统默认打印机" : printerName;
            AppLog.Write($"print: {result} -> {target}");

            var ok = result == CoreWebView2PrintStatus.Succeeded;

            // WebView2 打印 PDF 偶尔会失败，此时退回系统关联程序直接打印该文件
            if (!ok && url.Contains("/print-pdf"))
            {
                var saved = TrySystemPrintPdf(printerName);
                if (saved)
                {
                    Report(100, $"已交给系统打印 PDF → {target}", token, done: true, error: false);
                    return;
                }
            }

            Report(100, ok ? $"已发送到 {target}" : $"打印失败：{result}", token, done: true, error: !ok);
        }
        catch (Exception ex)
        {
            AppLog.Write("print: 失败 " + ex.Message);
            Report(100, "打印失败：" + ex.Message, token, done: true, error: true);
        }
        finally
        {
            try { view?.Dispose(); } catch { }
            try { host?.Close(); host?.Dispose(); } catch { }
        }
    }

    /// <summary>弹出系统文件夹选择框；返回选中路径，取消返回空串。</summary>
    public string PickFolder(string title)
    {
        var result = "";

        void ShowDialog()
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = string.IsNullOrWhiteSpace(title) ? "选择文件夹" : title,
                UseDescriptionForTitle = true,
                ShowNewFolderButton = true
            };
            if (dialog.ShowDialog(_owner) == DialogResult.OK)
            {
                result = dialog.SelectedPath;
            }
        }

        try
        {
            if (_owner.InvokeRequired) _owner.Invoke((Action)ShowDialog);
            else ShowDialog();
        }
        catch (Exception ex)
        {
            AppLog.Write("PickFolder failed: " + ex.Message);
        }

        return result;
    }
}

/// <summary>极简文件日志：%LOCALAPPDATA%\ClassManager-Desktop\desktop.log</summary>
internal static class AppLog
{
    private static readonly object Gate = new();

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClassManager-Desktop", "desktop.log");

    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            lock (Gate)
            {
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // 日志失败不影响主流程
        }
    }
}

/// <summary>路径与图标解析。</summary>
internal static class AppPaths
{
    public static string? FindProjectRoot()
    {
        // 1) 命令行 --root
        var overridden = Program.RootOverride;
        if (!string.IsNullOrWhiteSpace(overridden) && HasRuntime(overridden)) return overridden;

        // 2) 环境变量 CLASS_MANAGER_ROOT
        var fromEnv = Environment.GetEnvironmentVariable("CLASS_MANAGER_ROOT");
        if (!string.IsNullOrWhiteSpace(fromEnv) && HasRuntime(fromEnv)) return fromEnv;

        // 3) 从 exe 所在目录向上找
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "class-manager-system", "python", "python.exe");
            if (File.Exists(candidate)) return dir.FullName;
        }

        // 兼容把 exe 直接放在 class-manager-system 同级的情况
        var fallback = Path.Combine(AppContext.BaseDirectory, "class-manager-system", "python", "python.exe");
        return File.Exists(fallback) ? AppContext.BaseDirectory : null;
    }

    private static bool HasRuntime(string root) =>
        File.Exists(Path.Combine(root, "class-manager-system", "python", "python.exe"));

    /// <summary>
    /// 读取图标。size 为空时取系统默认帧；指定尺寸能拿到 ico 里对应分辨率的那一帧，
    /// 避免高分屏下被拉伸模糊（ico 内含 16/20/24/32/40/48/64/96/128/256 各档）。
    /// </summary>
    public static Icon? LoadIcon(string root, Size? size = null)
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "logo.ico"),
            Path.Combine(root, "logo.ico"),
            Path.Combine(root, "class-manager-system", "logo.ico")
        };

        // 高分屏下窗口图标按 150% 实际需要 48px，优先拿 64px 那帧再缩小，比放大清晰
        var wanted = size ?? new Size(64, 64);

        foreach (var path in candidates)
        {
            if (!File.Exists(path)) continue;
            try { return new Icon(path, wanted); } catch { }
        }

        return SystemIcons.Application;
    }
}
