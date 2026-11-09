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
        private static volatile bool _statusWarned;
        private static string _lastError = "";

        /// <summary>播报前提示音</summary>
        public static bool ChimeBefore { get; set; } = true;

        /// <summary>播报后提示音</summary>
        public static bool ChimeAfter { get; set; } = false;

        /// <summary>提示音音效</summary>
        public static string ChimeId { get; set; } = "airup";

        /// <summary>播报音量 0-100；-1 表示不自动调整系统音量</summary>
        public static int Volume { get; set; } = 70;

        /// <summary>一段播报结束时触发：(文本, 是否被手动停止)</summary>
        public static event Action<string, bool> Finished;

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
                        WaitUntilDoneOrStopped(text);
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

                    bool stopped = _stopRequested;
                    try { Finished?.Invoke(text, stopped); } catch { }
                }
            }
        }

        /// <summary>
        /// 等这一段念完。
        /// 首选 SAPI 的 WaitUntilDone(短超时)：它一读完就返回，靠耗时判断是否结束；
        /// 读不到就用「按字数估算」兜底，保证一定能退出。
        /// </summary>
        private static void WaitUntilDoneOrStopped(string text)
        {
            int capMs = Math.Max(3000, (int)((text ?? "").Length * 420) + 4000);
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // 刚发起朗读的一小段时间里 SAPI 还没真正开始，WaitUntilDone 会立刻返回，
            // 这里先跳过判定，避免"刚开始就以为读完了"。
            int earlyHits = 0;

            while (sw.ElapsedMilliseconds < capMs)
            {
                if (_stopRequested)
                {
                    PurgeOnWorker();
                    return;
                }

                if (sw.ElapsedMilliseconds < 600)
                {
                    Thread.Sleep(60);
                    continue;
                }

                if (WaitSlice(150))
                {
                    // 连续两次提前返回才算真的读完
                    if (++earlyHits >= 2) return;
                }
                else
                {
                    earlyHits = 0;
                }
            }

            Log.Write("播报超时兜底结束");
            PurgeOnWorker();
        }

        /// <summary>
        /// 阻塞最多 sliceMs；如果提前返回，说明这段念完了。
        /// WaitUntilDone 不可用时退化成纯等待，由外层按字数兜底。
        /// </summary>
        private static bool WaitSlice(int sliceMs)
        {
            try
            {
                var t = System.Diagnostics.Stopwatch.StartNew();
                _voiceType.InvokeMember("WaitUntilDone", BindingFlags.InvokeMethod, null, _voice,
                    new object[] { sliceMs });

                // 明显早于超时就返回 = 朗读已结束
                return t.ElapsedMilliseconds < sliceMs * 0.7;
            }
            catch
            {
                if (!_statusWarned)
                {
                    _statusWarned = true;
                    Log.Write("SAPI WaitUntilDone 不可用，改用按字数估算播报时长");
                }

                Thread.Sleep(sliceMs);
                return false;
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
