using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ClassManager.KioskAgent
{
    /// <summary>
    /// 主界面：内嵌 WebView2 打开本地 Web 设置页，关闭时只收进托盘。
    /// </summary>
    public class MainForm : Form
    {
        private readonly string _url;
        private WebView2 _webView;
        private bool _allowClose;

        /// <summary>用户点了关闭（收进托盘）时触发，用于作废登录态。</summary>
        public event Action? HiddenToTray;

        public MainForm(string url)
        {
            _url = url;

            Text = "班级管理系统・一体机端";
            StartPosition = FormStartPosition.CenterScreen;

            // 高度放宽一些：设置页内容较多，原来 760 太挤；同时按屏幕可用区域兜底
            var work = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
            int w = Math.Min(1120, (int)(work.Width * 0.9));
            int h = Math.Min(940, (int)(work.Height * 0.92));
            ClientSize = new Size(w, h);
            MinimumSize = new Size(880, 700);
            BackColor = Color.FromArgb(244, 246, 250);
            try { Icon = LoadIcon(); } catch { }

            _webView = new WebView2 { Dock = DockStyle.Fill };
            Controls.Add(_webView);

            Load += async (s, e) => await InitializeWebViewAsync();

            // 只要窗口被隐藏（点 X / Alt+F4 / 其它方式收起），就当作退出登录
            VisibleChanged += (s, e) =>
            {
                if (!Visible && !_allowClose)
                {
                    try { HiddenToTray?.Invoke(); } catch { }
                }
            };
        }

        private static Icon LoadIcon()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "logo.ico");
            if (!File.Exists(path)) path = Path.Combine(AppContext.BaseDirectory, "..", "logo.ico");
            return File.Exists(path) ? new Icon(path) : SystemIcons.Application;
        }

        private async System.Threading.Tasks.Task InitializeWebViewAsync()
        {
            try
            {
                var userData = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ClassManager-KioskAgent", "webview");
                Directory.CreateDirectory(userData);

                var env = await CoreWebView2Environment.CreateAsync(null, userData);
                await _webView.EnsureCoreWebView2Async(env);

                _webView.CoreWebView2.Settings.AreDevToolsEnabled = true;
                _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
                _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                _webView.CoreWebView2.NewWindowRequested += (s, e) =>
                {
                    e.Handled = true;
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri) { UseShellExecute = true }); }
                    catch { }
                };

                _webView.Source = new Uri(_url);
            }
            catch (Exception ex)
            {
                Log.Write("WebView2 初始化失败: " + ex.Message);
                Controls.Clear();
                Controls.Add(new Label
                {
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleCenter,
                    Text = "WebView2 运行时不可用。\n请安装 Microsoft Edge WebView2 Runtime 后重试。\n\n" + ex.Message,
                    Font = new Font("Microsoft YaHei UI", 10f)
                });
            }
        }

        public void Reload()
        {
            try { _webView?.CoreWebView2?.Reload(); } catch { }
        }

        public void CloseForExit()
        {
            _allowClose = true;
            Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_allowClose && e.CloseReason != CloseReason.WindowsShutDown &&
                e.CloseReason != CloseReason.ApplicationExitCall)
            {
                e.Cancel = true;
                Hide();          // 隐藏由 VisibleChanged 统一处理（会作废登录态）
                return;
            }
            base.OnFormClosing(e);
        }
    }
}
