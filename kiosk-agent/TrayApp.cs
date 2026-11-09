using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ClassManager.KioskAgent
{
    /// <summary>
    /// 托盘常驻外壳：启动后只在托盘，不自动弹窗；
    /// 右键托盘弹出 WebView2 菜单（打开设置 / 退出）。
    /// </summary>
    public class TrayApp : ApplicationContext
    {
        private readonly AgentConfig _config;
        private readonly AgentServer _server;
        private readonly NotifyIcon _tray;
        private MainForm _form;
        private System.Windows.Forms.Timer _balloonTimer;

        public TrayApp()
        {
            _config = AgentConfig.Load();
            Log.Write($"一体机端启动，设备={_config.DeviceName} 端口={_config.Port}");

            _server = new AgentServer(_config);
            _tray = BuildTrayIcon();

            _ = StartServerAsync();
        }

        private async Task StartServerAsync()
        {
            try
            {
                await _server.StartAsync();
                _server.ApplyRuntimeSettings();
                UpdateTrayText();
            }
            catch (Exception ex)
            {
                Log.Write("启动 Web 服务失败: " + ex.Message);
                _tray.ShowBalloonTip(5000, "启动失败", ex.Message, ToolTipIcon.Error);
            }
        }

        private NotifyIcon BuildTrayIcon()
        {
            var tray = new NotifyIcon
            {
                Icon = LoadIcon(),
                Visible = true,
                Text = "班级管理系统・一体机端"
            };

            // 右键用 WebView2 菜单代替 WinForms 原生菜单
            tray.MouseUp += (s, e) =>
            {
                if (e.Button == MouseButtons.Right) ShowTrayMenu();
            };

            _balloonTimer = new System.Windows.Forms.Timer { Interval = 1500 };
            _balloonTimer.Tick += (s, e) =>
            {
                _balloonTimer.Stop();
                try
                {
                    _tray.ShowBalloonTip(6000, "一体机端已启动",
                        $"教师端可访问：{_server.LanUrl}\n右键托盘图标可打开设置", ToolTipIcon.Info);
                }
                catch { }
            };
            _balloonTimer.Start();

            return tray;
        }

        private void ShowTrayMenu()
        {
            try
            {
                var menu = new TrayMenu();
                menu.OpenRequested += ShowMainWindow;
                menu.ExitRequested += ExitWithPassword;

                var area = Screen.FromPoint(Cursor.Position).WorkingArea;
                var pt = Cursor.Position;
                int x = Math.Min(pt.X, area.Right - menu.Width - 8);
                int y = Math.Min(pt.Y, area.Bottom - menu.Height - 8);

                menu.Location = new Point(Math.Max(area.Left + 8, x),
                                          Math.Max(area.Top + 8, y));
                menu.Show();
            }
            catch (Exception ex)
            {
                Log.Write("托盘菜单显示失败: " + ex.Message);
            }
        }

        /// <summary>调试用：直接弹出退出密码框，方便确认样式。</summary>
        public void ShowPasswordDialogPreview()
        {
            var t = new System.Windows.Forms.Timer { Interval = 2000 };
            t.Tick += (s, e) =>
            {
                t.Stop();
                ExitWithPassword();
            };
            t.Start();
        }

        /// <summary>调试/快捷方式用：启动后直接打开主界面。</summary>
        public void ShowWindowOnStart()
        {
            var t = new System.Windows.Forms.Timer { Interval = 1500 };
            t.Tick += (s, e) => { t.Stop(); ShowMainWindow(); };
            t.Start();
        }

        /// <summary>调试用：把托盘菜单显示在固定位置，方便截图确认样式。</summary>
        public void ShowMenuPreview()
        {
            var t = new System.Windows.Forms.Timer { Interval = 2000 };
            t.Tick += (s, e) =>
            {
                t.Stop();
                Cursor.Position = new Point(600, 300);
                ShowTrayMenu();
            };
            t.Start();
        }

        /// <summary>退出前校验访问密码（未设密码则直接退出）。</summary>
        private void ExitWithPassword()
        {
            if (!_config.HasPassword)
            {
                ExitApp();
                return;
            }

            using var dlg = new PasswordDialog(pwd => _config.VerifyPassword(pwd));
            if (dlg.ShowDialog() == DialogResult.OK)
                ExitApp();
        }

        private static Icon LoadIcon()
        {
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "logo.ico");
                if (!File.Exists(path)) path = Path.Combine(AppContext.BaseDirectory, "..", "logo.ico");
                if (File.Exists(path)) return new Icon(path);

                var asm = typeof(TrayApp).Assembly;
                using var stream = asm.GetManifestResourceStream("res.logo.ico");
                if (stream != null) return new Icon(stream);

                return SystemIcons.Application;
            }
            catch
            {
                return SystemIcons.Application;
            }
        }

        private void UpdateTrayText()
        {
            var text = $"班级管理系统・一体机端\n{_server.LanUrl}";
            if (text.Length > 127) text = text.Substring(0, 127);
            _tray.Text = text;
        }

        public void ShowMainWindow()
        {
            bool reopen = false;

            if (_form == null || _form.IsDisposed)
            {
                _form = new MainForm(_server.LocalUrl);
                _form.FormClosed += (s, e) => _form = null;
                _form.HiddenToTray += () =>
                {
                    // 关窗口即退出登录，下次打开要重新输密码
                    _server.ClearSessions("（窗口已关闭）");
                };
            }
            else
            {
                reopen = true;
            }

            _form.Show();
            if (_form.WindowState == FormWindowState.Minimized)
                _form.WindowState = FormWindowState.Normal;
            _form.Activate();
            _form.BringToFront();

            // 二次打开时强制刷新：服务端会话已清空，页面会回到登录页
            if (reopen) _form.Reload();
        }

        private void ExitApp()
        {
            _tray.Visible = false;
            try { _ = _server.StopAsync(); } catch { }
            _form?.CloseForExit();
            ExitThread();
        }
    }
}
