using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

namespace ClassManager.KioskUninstall;

/// <summary>卸载界面：显示安装位置，确认后执行并展示进度。</summary>
internal sealed class UninstallForm : Form
{
    private readonly string _dir;
    private readonly Label _status;
    private readonly ProgressBar _bar;
    private readonly Button _remove;
    private readonly Button _cancel;

    public UninstallForm(string dir)
    {
        _dir = dir;

        Text = "班级管理系统 · 一体机端 卸载";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(520, 300);
        BackColor = Color.White;
        Font = new Font("Microsoft YaHei UI", 9.5f);

        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("res.logo.ico");
            if (s != null) Icon = new Icon(s);
        }
        catch { }

        var title = new Label
        {
            Text = "卸载 班级管理系统 · 一体机端",
            Font = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold),
            ForeColor = Color.FromArgb(15, 23, 42),
            AutoSize = true,
            Location = new Point(24, 22)
        };

        var tip = new Label
        {
            Text = "将删除程序文件、快捷方式和开机自启项。\n本机的访问密码与设备设置会一并清除。",
            ForeColor = Color.FromArgb(100, 116, 139),
            AutoSize = true,
            Location = new Point(26, 62)
        };

        var dirLabel = new Label
        {
            Text = "安装位置：" + dir,
            ForeColor = Color.FromArgb(71, 85, 105),
            AutoSize = false,
            Size = new Size(470, 22),
            Location = new Point(26, 112),
            AutoEllipsis = true
        };

        _bar = new ProgressBar
        {
            Location = new Point(26, 158),
            Width = 468,
            Height = 8,
            Style = ProgressBarStyle.Continuous
        };

        _status = new Label
        {
            Text = "准备就绪",
            ForeColor = Color.FromArgb(100, 116, 139),
            AutoSize = true,
            Location = new Point(26, 174)
        };

        _remove = new Button
        {
            Text = "确认卸载",
            Location = new Point(340, 228),
            Width = 154,
            Height = 38,
            BackColor = Color.FromArgb(220, 38, 38),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        _remove.FlatAppearance.BorderSize = 0;
        _remove.Click += async (s, e) => await RunUninstall();

        _cancel = new Button
        {
            Text = "取消",
            Location = new Point(256, 228),
            Width = 76,
            Height = 38,
            FlatStyle = FlatStyle.Flat
        };
        _cancel.Click += (s, e) => Close();

        Controls.AddRange(new Control[] { title, tip, dirLabel, _bar, _status, _remove, _cancel });
    }

    private async Task RunUninstall()
    {
        _remove.Enabled = false;
        _cancel.Enabled = false;
        _bar.Style = ProgressBarStyle.Marquee;

        try
        {
            await Task.Run(() => Uninstaller.Run(_dir, (percent, text) =>
            {
                if (IsDisposed) return;
                BeginInvoke(new Action(() =>
                {
                    if (percent >= 100) _bar.Style = ProgressBarStyle.Continuous;
                    _bar.Value = Math.Max(0, Math.Min(100, percent));
                    _status.Text = text;
                }));
            }));

            MessageBox.Show("卸载完成。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            Close();
        }
        catch (Exception ex)
        {
            _bar.Style = ProgressBarStyle.Continuous;
            _remove.Enabled = true;
            _cancel.Enabled = true;
            MessageBox.Show("卸载失败：" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
