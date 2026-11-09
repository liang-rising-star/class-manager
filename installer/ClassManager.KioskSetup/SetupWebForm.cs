using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ClassManager.KioskSetup;

/// <summary>WebView2 承载的安装向导：界面完全由 HTML/CSS 绘制，和主程序同一套设计风格。</summary>
public sealed class SetupWebForm : Form
{
    private readonly WebView2 _web = new();
    private readonly Panel _splash;
    private readonly Bridge _bridge;

    public SetupWebForm()
    {
        Text = Program.AppName + " 安装程序";
        ClientSize = new Size(720, 882);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        BackColor = Color.FromArgb(247, 249, 252);
        AutoScaleMode = AutoScaleMode.Dpi;

        try
        {
            using var stream = EmbeddedUi.OpenResource("res.logo.ico");
            if (stream is not null) Icon = new Icon(stream);
        }
        catch { }

        _bridge = new Bridge(this);

        _web.Dock = DockStyle.Fill;
        Controls.Add(_web);

        _splash = BuildSplash();
        Controls.Add(_splash);
        _splash.BringToFront();

        Load += async (_, _) => await InitAsync();
    }

    private async Task InitAsync()
    {
        try
        {
            var userData = Path.Combine(Path.GetTempPath(), "class-manager-kiosk-setup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(userData);

            var env = await CoreWebView2Environment.CreateAsync(null, userData);
            await _web.EnsureCoreWebView2Async(env);

            var core = _web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;

            core.AddHostObjectToScript("bridge", _bridge);
            core.NavigateToString(EmbeddedUi.BuildHtml());

            _splash.SendToBack();
            _splash.Visible = false;
        }
        catch (Exception ex)
        {
            MessageBox.Show("界面初始化失败：" + ex.Message +
                            "\n\n本程序需要「Microsoft Edge WebView2 运行时」，" +
                            "Windows 10/11 一般已自带；缺失时请到微软官网安装后重试。",
                Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    private Panel BuildSplash()
    {
        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(247, 249, 252)
        };

        var logo = new PictureBox
        {
            Size = new Size(56, 56),
            SizeMode = PictureBoxSizeMode.Zoom,
            Left = (ClientSize.Width - 56) / 2,
            Top = ClientSize.Height / 2 - 70
        };

        try
        {
            var img = EmbeddedUi.LoadLogoImage();
            if (img is not null) logo.Image = img;
        }
        catch { }

        var text = new Label
        {
            Text = "正在准备安装界面…",
            AutoSize = true,
            ForeColor = Color.FromArgb(100, 116, 139),
            Font = new Font("Microsoft YaHei UI", 9.5f),
            Left = (ClientSize.Width - 140) / 2,
            Top = ClientSize.Height / 2 + 6
        };

        panel.Controls.Add(logo);
        panel.Controls.Add(text);
        return panel;
    }

    internal void PostProgress(int percent, string text)
    {
        if (IsDisposed || _web.CoreWebView2 is null) return;
        try // 失败（窗口已关）不影响安装继续
        {
            _web.CoreWebView2.ExecuteScriptAsync(
                $"window.__progress && window.__progress({percent}, {JsonSerializer.Serialize(text)})");
        }
        catch { }
    }

    internal void PostDone(string dir)
    {
        try
        {
            _web.CoreWebView2.ExecuteScriptAsync(
                $"window.__done && window.__done({JsonSerializer.Serialize(dir)})");
        }
        catch { }
    }

    internal void PostError(string message)
    {
        try
        {
            _web.CoreWebView2.ExecuteScriptAsync(
                $"window.__error && window.__error({JsonSerializer.Serialize(message)})");
        }
        catch { }
    }

    internal void CloseFromScript(bool exit = false)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => CloseFromScript(exit)));
            return;
        }

        Close();

        if (exit)
        {
            // 安装完自动关闭整个安装程序
            Application.Exit();
        }
    }
}

/// <summary>供网页调用的桥接对象。</summary>
[ClassInterface(ClassInterfaceType.AutoDual)]
[ComVisible(true)]
public sealed class Bridge
{
    private readonly SetupWebForm _form;

    public Bridge(SetupWebForm form) => _form = form;

    public string GetInfo() => JsonSerializer.Serialize(new
    {
        appName = Program.AppName,
        version = Program.AppVersion,
        defaultDir = Program.DefaultDir
    });

    public string CheckPath(string path) => JsonSerializer.Serialize(Installer.CheckPath(path));

    public string PickFolder(string current)
    {
        string result = "";

        _form.Invoke(new Action(() =>
        {
            using var dlg = new FolderBrowserDialog
            {
                Description = "选择一体机端的安装位置",
                SelectedPath = Directory.Exists(current) ? current : Program.DefaultDir,
                ShowNewFolderButton = true
            };

            if (dlg.ShowDialog(_form) == DialogResult.OK) result = dlg.SelectedPath;
        }));

        return result;
    }

    public void StartInstall(string path, string autostart, string desktop, string reset)
    {
        var options = new InstallOptions
        {
            Autostart = autostart == "1",
            DesktopShortcut = desktop == "1",
            StartMenuShortcut = true,
            ClearExistingConfig = reset == "1"
        };

        Task.Run(() =>
        {
            try
            {
                Installer.Install(path, options, (percent, text) => _form.PostProgress(percent, text));
                _form.PostDone(Path.GetFullPath(path));
            }
            catch (Exception ex)
            {
                _form.PostError(ex.Message);
            }
        });
    }

    public void CloseSetup()
    {
        _form.CloseFromScript();
    }
}

/// <summary>内嵌界面资源。</summary>
internal static class EmbeddedUi
{
    private static readonly Assembly Asm = Assembly.GetExecutingAssembly();

    public static Stream? OpenResource(string name) => Asm.GetManifestResourceStream(name);

    public static string ReadText(string name)
    {
        using var stream = OpenResource(name);
        if (stream is null) return "";
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    public static Image? LoadLogoImage()
    {
        using var stream = OpenResource("res.logo.ico");
        if (stream is null) return null;
        using var icon = new Icon(stream);
        return icon.ToBitmap();
    }

    public static string BuildHtml()
    {
        var html = ReadText("ui.index.html");

        // logo 转成 data URI，页面就不用再引用外部文件
        using var stream = OpenResource("res.logo.ico");
        if (stream is not null)
        {
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            var data = Convert.ToBase64String(ms.ToArray());
            html = html.Replace("logo.png", "data:image/x-icon;base64," + data);
        }

        return html;
    }
}
