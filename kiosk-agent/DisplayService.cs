using System;

namespace ClassManager.KioskAgent
{
    /// <summary>
    /// 投屏内容：教师端把「小组换座」的画面推过来，一体机全屏展示。
    /// 这里只存状态；真正的窗体由 TrayApp 在 UI 线程上创建。
    /// </summary>
    public class DisplayService
    {
        private readonly object _gate = new object();
        private string _state = "{}";
        private bool _showing;
        private DateTime _updatedAt = DateTime.MinValue;

        /// <summary>(是否显示, 最新状态 JSON)——由 TrayApp 在 UI 线程上处理</summary>
        public Action<bool, string> RequestShow;

        public bool Showing
        {
            get { lock (_gate) return _showing; }
        }

        public string State
        {
            get { lock (_gate) return _state; }
        }

        public DateTime UpdatedAt
        {
            get { lock (_gate) return _updatedAt; }
        }

        public void Show(string stateJson)
        {
            string s;
            lock (_gate)
            {
                if (!string.IsNullOrWhiteSpace(stateJson)) _state = stateJson;
                _updatedAt = DateTime.Now;
                _showing = true;
                s = _state;
            }
            Log.Write("投屏：开始显示");
            RequestShow?.Invoke(true, s);
        }

        public void Hide()
        {
            lock (_gate) _showing = false;
            Log.Write("投屏：停止显示");
            RequestShow?.Invoke(false, "");
        }

        /// <summary>窗体被用户关掉时回调，避免状态和界面不一致。</summary>
        public void MarkClosed()
        {
            lock (_gate) _showing = false;
        }
    }
}
