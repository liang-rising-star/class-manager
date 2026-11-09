using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ClassManager.KioskAgent
{
    /// <summary>
    /// 叫号全屏窗体：无边框、置顶、铺满主屏。
    /// 页面只加载一次，之后用 JS 切换文字并重播动画，避免每次导航导致的闪白。
    /// </summary>
    public class AnnouncementForm : Form
    {
        private readonly string _pageUrl;
        private WebView2 _web;
        private bool _ready;
        private string _pending;

        private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_SHOWWINDOW = 0x0040;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int X, int Y, int cx, int cy, uint uFlags);

        public event Action Closed;
        public event Action Ready;
        public event Action Dismissed;

        public bool IsReady => _ready;

        public AnnouncementForm(string baseUrl)
        {
            _pageUrl = baseUrl + "announce.html";

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.FromArgb(7, 12, 22);

            var screen = Screen.PrimaryScreen ?? Screen.FromControl(this);
            Bounds = screen.Bounds;

            _web = new WebView2 { Dock = DockStyle.Fill, BackColor = Color.FromArgb(7, 12, 22) };
            try { _web.DefaultBackgroundColor = Color.FromArgb(7, 12, 22); } catch { }
            Controls.Add(_web);

            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape)
                {
                    Log.Write("叫号全屏：按 Esc 手动收起");
                    try { Dismissed?.Invoke(); } catch { }
                }
            };

            Load += async (s, e) => await InitAsync();
            Shown += (s, e) => ForceTopMost();

            var keep = new System.Windows.Forms.Timer { Interval = 2000 };
            keep.Tick += (s, e) =>
            {
                // 隐藏状态下绝不能调用 SetWindowPos(SWP_SHOWWINDOW)，
                // 否则会把已经收起的全屏又"闪"出来。
                if (Visible) ForceTopMost();
            };
            keep.Start();
            FormClosed += (s, e) => keep.Stop();
        }

        private async System.Threading.Tasks.Task InitAsync()
        {
            try
            {
                var userData = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ClassManager-KioskAgent", "webview-announce");
                System.IO.Directory.CreateDirectory(userData);

                var env = await CoreWebView2Environment.CreateAsync(null, userData);
                await _web.EnsureCoreWebView2Async(env);

                var core = _web.CoreWebView2;

                var settings = core.Settings;
                settings.AreDefaultContextMenusEnabled = false;
                settings.IsStatusBarEnabled = false;
                settings.AreDevToolsEnabled = false;
                settings.IsZoomControlEnabled = false;
                settings.AreBrowserAcceleratorKeysEnabled = false;

                core.NavigationCompleted += (s, e) =>
                {
                    _ready = true;
                    Log.Write("叫号页面已就绪");

                    if (!string.IsNullOrEmpty(_pending))
                    {
                        var text = _pending;
                        _pending = null;
                        ShowText(text);
                    }

                    try { Ready?.Invoke(); } catch { }
                };

                core.Navigate(_pageUrl);
            }
            catch (Exception ex)
            {
                Log.Write("叫号窗口初始化失败: " + ex.Message);
            }
        }

        public void ForceTopMost()
        {
            if (!Visible) return;

            try
            {
                TopMost = true;
                SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            }
            catch { }
        }

        /// <summary>显示一段文字（页面只加载一次，这里只换文字 + 重播动画）。</summary>
        public void ShowText(string text)
        {
            if (!_ready)
            {
                _pending = text;
                return;
            }

            try
            {
                var json = System.Text.Json.JsonSerializer.Serialize(text ?? "");
                _web.CoreWebView2.ExecuteScriptAsync("window.__show && window.__show(" + json + ")");
                Log.Write("叫号显示：" + (text ?? "").Length.ToString() + " 字");
            }
            catch (Exception ex)
            {
                Log.Write("叫号内容显示失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 播放退场动画后隐藏（不销毁窗口）。
        /// 下次叫号直接复用，不用重新加载页面，也就不会有加载白屏。
        /// </summary>
        public void HideAnimated()
        {
            try
            {
                if (_ready && _web?.CoreWebView2 != null)
                    _web.CoreWebView2.ExecuteScriptAsync("window.__exit && window.__exit()");
            }
            catch { }

            var t = new System.Windows.Forms.Timer { Interval = 800 };
            t.Tick += (s, e) =>
            {
                t.Stop();
                t.Dispose();

                try { Hide(); } catch { }

                // 复位页面：清掉文字和动画状态，避免残留蓝色画面
                try
                {
                    if (_ready && _web?.CoreWebView2 != null)
                        _web.CoreWebView2.ExecuteScriptAsync("window.__reset && window.__reset()");
                }
                catch { }
            };
            t.Start();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            try { Closed?.Invoke(); } catch { }
        }
    }
}
