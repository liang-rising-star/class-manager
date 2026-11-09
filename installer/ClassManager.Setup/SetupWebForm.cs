using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ClassManager.Setup;

/// <summary>WebView2 承载的安装向导：界面完全用 HTML/CSS，和主程序同一套设计风格。</summary>
public sealed class SetupWebForm : Form
{
    private readonly WebView2 _web = new();
    private readonly Panel _splash;
    private readonly InstallerBridge _bridge;

    public SetupWebForm()
    {
        Text = Program.AppName + " 安装程序";
        ClientSize = new Size(760, 560);   // 真正尺寸在 InitAsync 里按 DPI 换算
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

        _bridge = new InstallerBridge(this);

        _web.Dock = DockStyle.Fill;
        Controls.Add(_web);

        // WebView2 环境初始化 + 首次渲染需要 1-2 秒，
        // 这段时间用原生遮罩顶上，避免用户看到白窗口。
        _splash = BuildSplash();
        Controls.Add(_splash);
        _splash.BringToFront();

        Load += async (_, _) => await InitAsync();
    }

    /// <summary>如果安装包旁边带了「固定版」运行时，就直接用它，不再依赖系统安装。</summary>
    internal static string? FindFixedRuntime()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "WebView2Fixed");
        return File.Exists(Path.Combine(dir, "msedgewebview2.exe")) ? dir : null;
    }

    /// <summary>检测 WebView2 运行时是否可用（固定版优先，其次系统版）。</summary>
    internal static bool RuntimeAvailable()
    {
        if (FindFixedRuntime() is not null) return true;
        try
        {
            var version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            return !string.IsNullOrEmpty(version);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>没装运行时：弹窗引导下载。</summary>
    internal static void PromptInstallRuntime()
    {
        const string url = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";
        var answer = MessageBox.Show(
            "本程序需要「Microsoft Edge WebView2 运行时」才能显示界面，但当前系统里没有检测到。\n\n" +
            "点「是」打开官方下载页，装好之后再运行本安装程序即可。\n" +
            "（Windows 10/11 一般自带，没有的机器装一次就行）",
            Program.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button1);

        if (answer != DialogResult.Yes) return;

        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    /// <summary>原生启动画面：Logo + 标题 + 载入条，WebView2 就绪后移除。</summary>
    private static Panel BuildSplash()
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
            Text = "正在准备安装向导…",
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

    private async Task InitAsync()
    {
        try
        {
            if (!RuntimeAvailable())
            {
                PromptInstallRuntime();
                Close();
                return;
            }

            // 按 DPI 把「CSS 尺寸」换算成物理像素：
            // 150% 缩放下若不换算，WebView2 视口只有 507×373，底部按钮会被挤出窗口。
            var scale = DeviceDpi / 96.0;
            ClientSize = new Size((int)Math.Round(760 * scale), (int)Math.Round(660 * scale));
            CenterToScreen();

            var userData = Path.Combine(Path.GetTempPath(), "class-manager-setup-webview");
            var env = await CoreWebView2Environment.CreateAsync(FindFixedRuntime(), userData);
            await _web.EnsureCoreWebView2Async(env);

            var settings = _web.CoreWebView2.Settings;
            settings.AreDevToolsEnabled = false;
            settings.AreDefaultContextMenusEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.IsZoomControlEnabled = false;

            _web.CoreWebView2.AddHostObjectToScript("installer", _bridge);

            _web.CoreWebView2.NavigationCompleted += (_, _) =>
            {
                if (_splash.Visible)
                {
                    _splash.Visible = false;
                    _splash.Dispose();
                }
            };

            _web.CoreWebView2.NavigateToString(EmbeddedUi.BuildHtml());
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "界面初始化失败：" + ex.Message +
                "\n\n请确认已安装 Microsoft Edge WebView2 运行时。",
                Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    // WebView2 的 PostWebMessageAsJson 必须在 UI 线程调用，
    // 安装是在后台线程跑的，所以统一投递回 UI 线程。
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

    public void PostProgress(int percent, string text) =>
        PostJson(JsonSerializer.Serialize(new { type = "progress", percent, text }));

    public void PostDone(string installDir, string exePath) =>
        PostJson(JsonSerializer.Serialize(new { type = "done", dir = installDir, exe = exePath }));

    public void PostError(string message) =>
        PostJson(JsonSerializer.Serialize(new { type = "error", message }));
}

/// <summary>暴露给页面的脚本对象：window.chrome.webview.hostObjects.installer</summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.AutoDual)]
public class InstallerBridge
{
    private readonly SetupWebForm _form;

    public InstallerBridge(SetupWebForm form) => _form = form;

    /// <summary>初始信息：默认路径、版本、名字。</summary>
    public string GetInfo()
    {
        return JsonSerializer.Serialize(new
        {
            app = Program.AppName,
            version = Program.AppVersion,
            defaultPath = Installer.DefaultTarget(),
            programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        });
    }

    /// <summary>校验路径，返回 {risk, message}。</summary>
    public string CheckPath(string path)
    {
        var (risk, message) = Installer.Check(path ?? string.Empty);
        return JsonSerializer.Serialize(new
        {
            risk = risk switch
            {
                Installer.PathRisk.Ok => "ok",
                Installer.PathRisk.NeedsAdmin => "admin",
                Installer.PathRisk.NotWritable => "readonly",
                _ => "bad",
            },
            message,
        });
    }

    /// <summary>弹系统文件夹选择框，返回选中的目录（取消返回空串）。</summary>
    public string PickFolder(string current)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择安装位置（建议 D:\\class-manager）",
            UseDescriptionForTitle = true,
        };

        try
        {
            if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current))
                dialog.SelectedPath = current;
            else if (!string.IsNullOrWhiteSpace(current))
                dialog.SelectedPath = Path.GetPathRoot(current) ?? string.Empty;
        }
        catch { }

        return dialog.ShowDialog(_form) == DialogResult.OK ? dialog.SelectedPath : string.Empty;
    }

    /// <summary>当前是否已安装：返回 {installed, dir, version, currentVersion}。</summary>
    public string GetState()
    {
        var info = RegistryStore.Read();
        return JsonSerializer.Serialize(new
        {
            installed = info is not null,
            dir = info?.Dir ?? string.Empty,
            version = info?.Version ?? string.Empty,
            currentVersion = Program.AppVersion,
        });
    }

    /// <summary>选择要导入的 zip 更新包。</summary>
    public string PickZip()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择更新包（zip）",
            Filter = "更新包 (*.zip)|*.zip|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };

        return dialog.ShowDialog(_form) == DialogResult.OK ? dialog.FileName : string.Empty;
    }

    /// <summary>用 zip 包更新已安装的程序。</summary>
    public void StartUpdate(string zipPath, string dir)
    {
        Task.Run(() =>
        {
            try
            {
                var progress = new Progress<(int percent, string text)>(p => _form.PostProgress(p.percent, p.text));
                var options = new InstallOptions { Autostart = false };
                Installer.InstallFromZip(zipPath, dir, options, progress);
                _form.PostDone(dir, Path.Combine(dir, Program.ExeName));
            }
            catch (Exception ex)
            {
                _form.PostError(ex.Message);
            }
        });
    }

    /// <summary>开始安装（异步，进度通过 PostWebMessage 推送）。</summary>
    public void StartInstall(string path, bool autostart, bool desktopIcon, bool startMenu)
    {
        var options = new InstallOptions
        {
            Autostart = autostart,
            DesktopShortcut = desktopIcon,
            StartMenuShortcut = startMenu,
        };

        Task.Run(() =>
        {
            try
            {
                var progress = new Progress<(int percent, string text)>(p => _form.PostProgress(p.percent, p.text));
                Installer.Install(path, options, progress);
                _form.PostDone(path, Path.Combine(path, Program.ExeName));
            }
            catch (Exception ex)
            {
                _form.PostError(ex.Message);
            }
        });
    }

    /// <summary>安装完成后启动主程序并关闭安装器。</summary>
    public string LaunchAndClose(string exePath)
    {
        try
        {
            if (File.Exists(exePath))
            {
                Process.Start(new ProcessStartInfo(exePath)
                {
                    WorkingDirectory = Path.GetDirectoryName(exePath)!,
                });
            }
        }
        catch (Exception ex)
        {
            return "启动失败：" + ex.Message;
        }

        _form.BeginInvoke(new Action(() => _form.Close()));
        return string.Empty;
    }

    public void CloseSetup()
    {
        _form.BeginInvoke(new Action(() => _form.Close()));
    }
}

/// <summary>把内嵌的 HTML/CSS/JS/图标拼成自包含页面（单文件 exe 里没有 wwwroot 目录）。</summary>
internal static class EmbeddedUi
{
    private static Assembly Asm => Assembly.GetExecutingAssembly();

    public static Stream? OpenResource(string name) => Asm.GetManifestResourceStream(name);

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

    public static string BuildHtml()
    {
        var html = ReadText("ui.index.html");
        var appCss = ReadText("ui.app.css");
        var wizardCss = ReadText("ui.wizard.css");
        var script = ReadText("ui.wizard.js");
        var logoData = ToDataUri("ui.logo.ico");

        html = html.Replace("<link rel=\"stylesheet\" href=\"app.css\">", "<style>" + appCss + "</style>");
        html = html.Replace("<link rel=\"stylesheet\" href=\"wizard.css\">", "<style>" + wizardCss + "</style>");
        html = html.Replace("<script src=\"wizard.js\"></script>", "<script>" + script + "</script>");
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
