using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading;

namespace ClassManager.KioskAgent
{
    /// <summary>
    /// 语音播报：调用 Windows 内置 SAPI（无需额外组件），用于班级叫号。
    /// 请求入队后立即返回，由后台线程逐条朗读。
    /// </summary>
    public static class SpeakService
    {
        private static readonly ConcurrentQueue<string> Queue = new();
        private static readonly object Gate = new();
        private static Thread _worker;
        private static object _voice;
        private static Type _voiceType;
        private static volatile bool _busy;
        private static string _lastError = "";

        public static int PendingCount => Queue.Count;
        public static bool IsSpeaking => _busy;
        public static string LastError => _lastError;

        private static bool EnsureVoice()
        {
            lock (Gate)
            {
                if (_voice != null) return true;
                try
                {
                    _voiceType = Type.GetTypeFromProgID("SAPI.SpVoice");
                    if (_voiceType == null)
                    {
                        _lastError = "系统未安装语音引擎（SAPI.SpVoice）";
                        return false;
                    }
                    _voice = Activator.CreateInstance(_voiceType);
                    return _voice != null;
                }
                catch (Exception ex)
                {
                    _lastError = ex.Message;
                    return false;
                }
            }
        }

        public static bool Enqueue(string text, int rate)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (!EnsureVoice()) return false;

            try
            {
                // 语速限制在 -10..10
                int r = Math.Clamp(rate, -10, 10);
                Queue.Enqueue($"{r}|{text.Trim()}");
                StartWorker();
                return true;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                return false;
            }
        }

        private static void StartWorker()
        {
            lock (Gate)
            {
                if (_worker != null && _worker.IsAlive) return;
                _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "sapi-speak" };
                _worker.Start();
            }
        }

        private static void WorkerLoop()
        {
            while (true)
            {
                if (!Queue.TryDequeue(out var item))
                {
                    Thread.Sleep(80);
                    continue;
                }

                int bar = item.IndexOf('|');
                int rate = 0;
                string text = item;
                if (bar > 0 && int.TryParse(item.Substring(0, bar), out var parsed))
                {
                    rate = parsed;
                    text = item.Substring(bar + 1);
                }

                _busy = true;
                try
                {
                    if (EnsureVoice())
                    {
                        _voiceType.InvokeMember("Rate", BindingFlags.SetProperty, null, _voice, new object[] { rate });
                        // 0 = SVSFDefault（同步朗读）
                        _voiceType.InvokeMember("Speak", BindingFlags.InvokeMethod, null, _voice, new object[] { text, 0 });
                    }
                }
                catch (Exception ex)
                {
                    _lastError = ex.Message;
                    Log.Write("语音播报失败: " + ex.Message);
                }
                finally
                {
                    _busy = false;
                }
            }
        }

        /// <summary>立即停止当前播报并清空队列</summary>
        public static void StopAll()
        {
            while (Queue.TryDequeue(out _)) { }
            try
            {
                if (EnsureVoice())
                {
                    // 2 = SVSFPurgeBeforeSpeak，配合空文本即可打断
                    _voiceType.InvokeMember("Speak", BindingFlags.InvokeMethod, null, _voice, new object[] { "", 2 });
                }
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
            }
        }
    }
}
