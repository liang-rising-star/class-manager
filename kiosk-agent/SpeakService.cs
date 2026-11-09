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
        private static volatile bool _stopRequested;
        private static string _lastError = "";

        /// <summary>播报前提示音</summary>
        public static bool ChimeBefore { get; set; } = true;

        /// <summary>播报后提示音</summary>
        public static bool ChimeAfter { get; set; } = false;

        /// <summary>提示音音效</summary>
        public static string ChimeId { get; set; } = "airup";

        /// <summary>播报音量 0-100；-1 表示不自动调整系统音量</summary>
        public static int Volume { get; set; } = 70;

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
                try { _worker.SetApartmentState(ApartmentState.STA); } catch { }
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
                _stopRequested = false;
                float? savedVolume = null;
                Log.Write($"开始播报（语速 {rate}）：" + (text.Length > 24 ? text.Substring(0, 24) + "…" : text));
                bool spoke = false;
                try
                {
                    // 播报前先把系统音量调到设定值，播完再还原
                    if (Volume >= 0) savedVolume = SystemVolume.Apply(Volume);

                    if (ChimeBefore) ChimePlayer.PlayBefore(ChimeId);

                    if (EnsureVoice())
                    {
                        _voiceType.InvokeMember("Rate", BindingFlags.SetProperty, null, _voice, new object[] { rate });
                        // 1 = SVSFlagsAsync：立即返回，才能在播报过程中打断
                        _voiceType.InvokeMember("Speak", BindingFlags.InvokeMethod, null, _voice, new object[] { text, 1 });
                        spoke = true;
                        WaitUntilDoneOrStopped();
                    }

                    if (ChimeAfter && !_stopRequested) ChimePlayer.PlayAfter(ChimeId);
                }
                catch (Exception ex)
                {
                    _lastError = ex.Message;
                    Log.Write("语音播报失败: " + ex.Message);
                }
                finally
                {
                    if (Volume >= 0) SystemVolume.Restore(savedVolume, Volume);
                    _busy = false;
                }
            }
        }

        /// <summary>
        /// 等这一段念完；期间如果收到停止请求，就在**播报线程**里打断
        /// （SAPI 的 COM 对象只能在创建它的线程上调用，所以不能从 HTTP 线程直接停）。
        /// </summary>
        private static void WaitUntilDoneOrStopped()
        {
            const int SRSEDone = 2;

            while (true)
            {
                if (_stopRequested)
                {
                    PurgeOnWorker();
                    return;
                }

                try
                {
                    var status = _voiceType.InvokeMember("Status", BindingFlags.GetProperty, null, _voice, null);
                    var state = status.GetType()
                        .InvokeMember("RunningState", BindingFlags.GetField, null, status, null);
                    if (Convert.ToInt32(state) == SRSEDone) return;
                }
                catch
                {
                    // 拿不到状态就按固定节奏轮询，避免死循环
                }

                Thread.Sleep(60);
            }
        }

        private static void PurgeOnWorker()
        {
            try
            {
                // 2 = SVSFPurgeBeforeSpeak，配合空文本即可打断
                _voiceType.InvokeMember("Speak", BindingFlags.InvokeMethod, null, _voice, new object[] { "", 2 });
                Log.Write("播报已手动停止");
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
            }
        }

        /// <summary>立即停止当前播报并清空队列（可被任意线程调用）</summary>
        public static void StopAll()
        {
            _stopRequested = true;
            while (Queue.TryDequeue(out _)) { }
        }
    }
}
