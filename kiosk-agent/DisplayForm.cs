using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ClassManager.KioskAgent
{
    /// <summary>
    /// 投屏全屏窗体：无边框、置顶、铺满主屏，内部用 WebView2 打开 display.html。
    /// 内容靠页面自身轮询 /api/display/state 刷新，这里只负责开和关。
    /// </summary>
    public class DisplayForm : Form
    {
        private readonly string _url;
        private WebView2 _web;
        private bool _ready;

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int X, int Y, int cx, int cy, uint uFlags);

        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_SHOWWINDOW = 0x0040;

        public event Action Closed;

        public DisplayForm(string url)
        {
            _url = url;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.Black;
            KeyPreview = true;

            var screen = Screen.PrimaryScreen ?? Screen.FromControl(this);
            Bounds = screen.Bounds;

            _web = new WebView2 { Dock = DockStyle.Fill };
            Controls.Add(_web);

            Load += async (s, e) => await InitAsync();
            Shown += (s, e) => ForceTopMost();

            // 有些置顶窗口（如调试工具）会抢层级，定时重新顶到最前
            var keep = new System.Windows.Forms.Timer { Interval = 3000 };
            keep.Tick += (s, e) => ForceTopMost();
            keep.Start();
            FormClosed += (s, e) => keep.Stop();
            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape) Close();
            };
        }

        /// <summary>某些机器上被其它置顶窗口抢了层级，这里再强制顶到最前面。</summary>
        public void ForceTopMost()
        {
            try
            {
                TopMost = true;
                Activate();
                BringToFront();
                SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            }
            catch (Exception ex)
            {
                Log.Write("投屏置顶失败: " + ex.Message);
            }
        }

        private async System.Threading.Tasks.Task InitAsync()
        {
            try
            {
                var userData = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ClassManager-KioskAgent", "webview-display");
                System.IO.Directory.CreateDirectory(userData);

                var env = await CoreWebView2Environment.CreateAsync(null, userData);
                await _web.EnsureCoreWebView2Async(env);

                var settings = _web.CoreWebView2.Settings;
                settings.AreDefaultContextMenusEnabled = false;
                settings.IsStatusBarEnabled = false;
                settings.AreDevToolsEnabled = false;
                settings.IsZoomControlEnabled = false;

                _web.CoreWebView2.Navigate(_url);
                _ready = true;
            }
            catch (Exception ex)
            {
                Log.Write("投屏窗口初始化失败: " + ex.Message);
            }
        }

        /// <summary>状态更新时刷新页面（页面也会自己轮询，这里只是让变化更快生效）。</summary>
        public void Refresh(bool force = false)
        {
            try
            {
                if (!_ready || _web?.CoreWebView2 == null) return;
                if (force) _web.CoreWebView2.Navigate(_url);
                else _web.CoreWebView2.ExecuteScriptAsync("window.__poll && window.__poll()");
            }
            catch { }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            try { Closed?.Invoke(); } catch { }
        }
    }
}
