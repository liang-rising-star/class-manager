using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

namespace ClassManager.KioskSetup;

/// <summary>安装界面：选目录、勾选开机自启与快捷方式，点安装。</summary>
internal sealed class SetupForm : Form
{
    private readonly TextBox _path;
    private readonly CheckBox _autostart;
    private readonly CheckBox _desktop;
    private readonly Button _install;
    private readonly Label _status;
    private readonly ProgressBar _bar;

    public SetupForm()
    {
        Text = "班级管理系统 · 一体机端 安装程序";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(560, 340);
        BackColor = Color.White;
        Font = new Font("Microsoft YaHei UI", 9F);

        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("res.logo.ico");
            if (s != null) Icon = new Icon(s);
        }
        catch { }

        var title = new Label
        {
            Text = "班级管理系统 · 一体机端",
            Font = new Font("Microsoft YaHei UI", 15F, FontStyle.Bold),
            ForeColor = Color.FromArgb(15, 23, 42),
            AutoSize = true,
            Location = new Point(24, 22)
        };

        var subtitle = new Label
        {
            Text = "摄像头直播、双向对讲、语音播报、屏幕直播",
            ForeColor = Color.FromArgb(100, 116, 139),
            AutoSize = true,
            Location = new Point(26, 58)
        };

        var dirLabel = new Label
        {
            Text = "安装位置",
            ForeColor = Color.FromArgb(71, 85, 105),
            AutoSize = true,
            Location = new Point(26, 96)
        };

        _path = new TextBox
        {
            Text = Program.DefaultDir,
            Location = new Point(26, 118),
            Width = 420
        };

        var browse = new Button
        {
            Text = "浏览…",
            Location = new Point(456, 117),
            Width = 78,
            Height = 26
        };
        browse.Click += (s, e) => Browse();

        _autostart = new CheckBox
        {
            Text = "开机自动启动（一体机专用建议勾选）",
            Checked = true,
            AutoSize = true,
            Location = new Point(26, 158)
        };

        _desktop = new CheckBox
        {
            Text = "创建桌面快捷方式",
            Checked = true,
            AutoSize = true,
            Location = new Point(26, 184)
        };

        _bar = new ProgressBar
        {
            Location = new Point(26, 220),
            Width = 508,
            Height = 8,
            Style = ProgressBarStyle.Continuous
        };

        _status = new Label
        {
            Text = "准备就绪",
            ForeColor = Color.FromArgb(100, 116, 139),
            AutoSize = true,
            Location = new Point(26, 236)
        };

        _install = new Button
        {
            Text = "开始安装",
            Location = new Point(392, 276),
            Width = 142,
            Height = 36,
            BackColor = Color.FromArgb(37, 99, 235),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        _install.FlatAppearance.BorderSize = 0;
        _install.Click += async (s, e) => await RunInstall();

        var cancel = new Button
        {
            Text = "取消",
            Location = new Point(300, 276),
            Width = 84,
            Height = 36,
            FlatStyle = FlatStyle.Flat
        };
        cancel.Click += (s, e) => Close();

        Controls.AddRange(new Control[]
        {
            title, subtitle, dirLabel, _path, browse,
            _autostart, _desktop, _bar, _status, _install, cancel
        });
    }

    private void Browse()
    {
        using var dlg = new FolderBrowserDialog { SelectedPath = _path.Text };
        if (dlg.ShowDialog() == DialogResult.OK) _path.Text = dlg.SelectedPath;
    }

    private async Task RunInstall()
    {
        var dir = _path.Text.Trim();
        if (string.IsNullOrWhiteSpace(dir))
        {
            MessageBox.Show("请先选择安装位置", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _install.Enabled = false;
        _bar.Style = ProgressBarStyle.Marquee;

        var options = new InstallOptions
        {
            Autostart = _autostart.Checked,
            DesktopShortcut = _desktop.Checked,
            StartMenuShortcut = true
        };

        try
        {
            await Task.Run(() => Installer.Install(dir, options, m =>
            {
                if (IsDisposed) return;
                BeginInvoke(new Action(() => _status.Text = m));
            }));

            _bar.Style = ProgressBarStyle.Continuous;
            _bar.Value = 100;

            MessageBox.Show("安装完成。\n\n程序已启动，可在托盘图标右键打开设置。",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            Close();
        }
        catch (Exception ex)
        {
            _bar.Style = ProgressBarStyle.Continuous;
            _install.Enabled = true;
            MessageBox.Show("安装失败：" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
