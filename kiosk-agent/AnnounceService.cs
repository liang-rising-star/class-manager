using System;

namespace ClassManager.KioskAgent
{
    /// <summary>
    /// 叫号全屏：显示一块覆盖整个桌面的动画卡片，文字 + 语音，播完自动收起。
    /// 这里只维护状态，窗体由 TrayApp 在 UI 线程上创建。
    /// </summary>
    public class AnnounceService
    {
        private readonly object _gate = new object();
        private string _text = "";
        private bool _active;

        /// <summary>(是否显示, 要显示的文字)——由 TrayApp 在 UI 线程上处理</summary>
        public Action<bool, string> RequestShow;

        public bool Active
        {
            get { lock (_gate) return _active; }
        }

        public string Text
        {
            get { lock (_gate) return _text; }
        }

        public void Show(string text)
        {
            lock (_gate)
            {
                _text = text ?? "";
                _active = true;
            }

            Log.Write("叫号全屏：" + (_text.Length > 20 ? _text.Substring(0, 20) + "…" : _text));
            RequestShow?.Invoke(true, _text);
        }

        public void Hide()
        {
            lock (_gate)
            {
                if (!_active) return;
                _active = false;
            }

            RequestShow?.Invoke(false, "");
        }

        /// <summary>窗体被用户关掉时回调，避免状态和界面不一致。</summary>
        public void MarkClosed()
        {
            lock (_gate) _active = false;
        }
    }
}
