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

        /// <summary>投屏窗口（可为空）</summary>
        private DisplayForm _displayForm;

        /// <summary>叫号全屏窗口（可为空）</summary>
        private AnnouncementForm _announceForm;

        /// <summary>叫号兜底计时器：语音卡住也会自动收起</summary>
        private System.Windows.Forms.Timer _announceGuard;

        /// <summary>无界面控件，仅用来把 HTTP 线程的调用切回 UI 线程</summary>
        private readonly Control _marshaller = new Control();

        public TrayApp()
        {
            _config = AgentConfig.Load();
            Log.Write($"一体机端启动，设备={_config.DeviceName} 端口={_config.Port}");

            _server = new AgentServer(_config);

            // 强制创建句柄，否则 HTTP 线程调用 BeginInvoke 会抛「窗口句柄之前不能调用」
            var marshallerHandle = _marshaller.Handle;
            GC.KeepAlive(marshallerHandle);

            _server.ExitRequested += () =>
            {
                try { _marshaller.BeginInvoke(new Action(ExitApp)); } catch { }
            };

            _server.Announce.RequestShow = (show, text) =>
            {
                try { _marshaller.BeginInvoke(new Action(() => ApplyAnnouncement(show, text))); }
                catch (Exception ex) { Log.Write("叫号切换失败: " + ex.Message); }
            };

            // 一段播报结束（正常读完或被停止）→ 收起全屏
            SpeakService.Finished += (text, stopped) =>
            {
                try
                {
                    _marshaller.BeginInvoke(new Action(() =>
                    {
                        if (_server.Announce.Active) _server.Announce.Hide();
                    }));
                }
                catch { }
            };

            _server.Display.RequestShow = (show, stateJson) =>
            {
                try { _marshaller.BeginInvoke(new Action(() => ApplyDisplay(show, stateJson))); }
                catch (Exception ex) { Log.Write("投屏切换失败: " + ex.Message); }
            };

            _tray = BuildTrayIcon();

            _ = StartServerAsync();
        }

        /// <summary>叫号全屏：显示文字 → 语音播完 → 退场动画 → 消失（UI 线程）。</summary>
        private void ApplyAnnouncement(bool show, string text)
        {
            try
            {
                if (show)
                {
                    if (_announceForm == null || _announceForm.IsDisposed)
                    {
                        _announceForm = new AnnouncementForm(_server.LocalUrl);
                        _announceForm.Closed += () =>
                        {
                            _announceForm = null;
                            _server.Announce.MarkClosed();
                        };

                        // Esc：收起全屏并停止播报
                        _announceForm.Dismissed += () =>
                        {
                            SpeakService.StopAll();
                            _server.Announce.Hide();
                        };
                        _announceForm.Show();
                        Log.Write("叫号全屏已打开");
                    }

                    if (!_announceForm.Visible) _announceForm.Show();   // 复用时重新显示
                    _announceForm.ForceTopMost();
                    _announceForm.ShowText(text);   // 页面没就绪时会先缓存，加载完自动播放

                    // 兜底：40 秒后无论语音是否结束都收起
                    if (_announceGuard == null)
                    {
                        _announceGuard = new System.Windows.Forms.Timer { Interval = 90000 };
                        _announceGuard.Tick += (s, e) =>
                        {
                            _announceGuard.Stop();
                            if (_server.Announce.Active) _server.Announce.Hide();
                        };
                    }
                    _announceGuard.Stop();
                    _announceGuard.Start();
                }
                else
                {
                    if (_announceGuard != null) _announceGuard.Stop();

                    if (_announceForm != null && !_announceForm.IsDisposed)
                    {
                        Log.Write("叫号全屏：退场");
                        _announceForm.HideAnimated();   // 只隐藏，下次复用
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("叫号全屏异常: " + ex.Message);
            }
        }

        /// <summary>按需打开或关闭全屏投屏窗体（必须在 UI 线程调用）。</summary>
        private void ApplyDisplay(bool show, string stateJson)
        {
            try
            {
                if (show)
                {
                    if (_displayForm == null || _displayForm.IsDisposed)
                    {
                        _displayForm = new DisplayForm(_server.LocalUrl + "display.html");
                        _displayForm.Closed += () =>
                        {
                            _displayForm = null;
                            _server.Display.MarkClosed();
                        };
                        _displayForm.Show();
                        _displayForm.ForceTopMost();
                        Log.Write("投屏窗口已打开");
                    }
                    else
                    {
                        _displayForm.ForceTopMost();
                        _displayForm.Refresh(force: true);
                    }
                }
                else
                {
                    if (_displayForm != null && !_displayForm.IsDisposed)
                        _displayForm.Close();
                    _displayForm = null;
                    Log.Write("投屏窗口已关闭");
                }
            }
            catch (Exception ex)
            {
                Log.Write("投屏窗口异常: " + ex.Message);
            }
        }

        private async Task StartServerAsync()
        {
            try
            {
                await _server.StartAsync();
                _server.ApplyRuntimeSettings();
                UpdateTrayText();

                // 起来几秒后再加保护，避免启动阶段出错时连自己也杀不掉
                _ = Task.Run(async () =>
                {
                    await Task.Delay(4000);
                    try { _marshaller.BeginInvoke(new Action(() => ProcessGuard.Protect())); } catch { }
                });
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
            ProcessGuard.Unprotect();      // 先放开保护，否则进程结束不掉

            _tray.Visible = false;
            try { _ = _server.StopAsync(); } catch { }
            _form?.CloseForExit();
            ExitThread();
        }
    }
}
