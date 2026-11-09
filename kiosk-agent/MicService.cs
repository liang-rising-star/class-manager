using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using NAudio.Wave;

namespace ClassManager.KioskAgent
{
    public class MicDeviceInfo
    {
        public int Index { get; set; }
        public string Name { get; set; }
        public int Channels { get; set; }
    }

    /// <summary>
    /// 麦克风采集：NAudio 采集 PCM（16k/16bit/单声道），提供电平与实时音频流。
    /// </summary>
    public class MicService : IDisposable
    {
        public const int SampleRate = 16000;
        public const int Channels = 1;
        public const int BitsPerSample = 16;

        private readonly object _gate = new object();
        private readonly Queue<byte[]> _pcmQueue = new Queue<byte[]>();
        private int _queuedBytes;
        private readonly int _maxQueuedBytes = SampleRate * 2 * 8; // 最多缓冲 8 秒
        private WaveInEvent _waveIn;
        private string _currentDevice = "";
        private double _level;
        private double _gain = 1.0;
        private bool _enabled = true;
        private string _lastError = "";
        private long _totalBytes;

        public bool IsCapturing { get { lock (_gate) return _waveIn != null; } }
        public bool Enabled { get { lock (_gate) return _enabled; } }
        public string CurrentDevice { get { lock (_gate) return _currentDevice; } }
        public string LastError { get { lock (_gate) return _lastError; } }
        public double Level { get { lock (_gate) return Math.Round(_level, 3); } }
        public long TotalBytes { get { lock (_gate) return _totalBytes; } }

        public static List<MicDeviceInfo> ListDevices()
        {
            var list = new List<MicDeviceInfo>();
            try
            {
                int count = WaveInEvent.DeviceCount;
                for (int i = 0; i < count; i++)
                {
                    var caps = WaveInEvent.GetCapabilities(i);
                    list.Add(new MicDeviceInfo { Index = i, Name = caps.ProductName, Channels = caps.Channels });
                }
            }
            catch (Exception ex)
            {
                Log.Write("枚举麦克风失败: " + ex.Message);
            }
            return list;
        }

        public static int ResolveDeviceIndex(string deviceName)
        {
            if (string.IsNullOrWhiteSpace(deviceName)) return 0;
            foreach (var d in ListDevices())
                if (string.Equals(d.Name, deviceName, StringComparison.OrdinalIgnoreCase)) return d.Index;
            return 0;
        }

        /// <summary>按设置启动或停止采集</summary>
        public void Apply(bool enabled, string deviceName, double gain)
        {
            lock (_gate)
            {
                _enabled = enabled;
                _gain = Math.Clamp(gain, 0.1, 3.0);
            }

            if (!enabled)
            {
                StopCapture();
                lock (_gate) _level = 0;
                return;
            }

            int index = ResolveDeviceIndex(deviceName);
            if (IsCapturing && string.Equals(CurrentDevice, deviceName, StringComparison.OrdinalIgnoreCase))
                return;

            StartCapture(index, deviceName);
        }

        private void StartCapture(int index, string deviceName)
        {
            StopCapture();
            try
            {
                if (WaveInEvent.DeviceCount == 0)
                {
                    lock (_gate) _lastError = "未检测到麦克风设备";
                    return;
                }

                var wave = new WaveInEvent
                {
                    DeviceNumber = index,
                    WaveFormat = new WaveFormat(SampleRate, BitsPerSample, Channels),
                    BufferMilliseconds = 100,
                    NumberOfBuffers = 3
                };
                wave.DataAvailable += OnData;
                wave.RecordingStopped += (s, e) =>
                {
                    if (e.Exception != null)
                    {
                        lock (_gate) _lastError = e.Exception.Message;
                        Log.Write("麦克风录音停止: " + e.Exception.Message);
                    }
                };
                wave.StartRecording();

                lock (_gate)
                {
                    _waveIn = wave;
                    _currentDevice = string.IsNullOrEmpty(deviceName)
                        ? (index < WaveInEvent.DeviceCount ? WaveInEvent.GetCapabilities(index).ProductName : "")
                        : deviceName;
                    _lastError = "";
                }
                Log.Write("麦克风已启动: " + deviceName);
            }
            catch (Exception ex)
            {
                lock (_gate) _lastError = ex.Message;
                Log.Write("启动麦克风失败: " + ex.Message);
            }
        }

        private void OnData(object sender, WaveInEventArgs e)
        {
            if (e.BytesRecorded <= 0) return;

            double gain;
            lock (_gate) gain = _gain;

            var chunk = new byte[e.BytesRecorded];
            Buffer.BlockCopy(e.Buffer, 0, chunk, 0, e.BytesRecorded);

            // 计算电平（RMS）
            double sum = 0;
            int samples = e.BytesRecorded / 2;
            for (int i = 0; i < samples; i++)
            {
                short s = BitConverter.ToInt16(e.Buffer, i * 2);
                double v = s / 32768.0;
                sum += v * v;
            }
            double rms = samples > 0 ? Math.Sqrt(sum / samples) : 0;

            lock (_gate)
            {
                _level = Math.Min(1.0, rms * 3.0);
                _totalBytes += e.BytesRecorded;
                _queuedBytes += chunk.Length;
                _pcmQueue.Enqueue(chunk);
                while (_queuedBytes > _maxQueuedBytes && _pcmQueue.Count > 0)
                {
                    var dropped = _pcmQueue.Dequeue();
                    _queuedBytes -= dropped.Length;
                }
                Monitor.PulseAll(_gate);
            }
        }

        /// <summary>阻塞读取 PCM，返回实际字节数；超时返回 0</summary>
        public int ReadPcm(byte[] dest, int count, int timeoutMs)
        {
            int written = 0;
            var deadline = DateTime.Now.AddMilliseconds(timeoutMs);
            while (written < count)
            {
                lock (_gate)
                {
                    while (_pcmQueue.Count == 0)
                    {
                        int wait = (int)Math.Max(0, (deadline - DateTime.Now).TotalMilliseconds);
                        if (wait <= 0) return written;
                        Monitor.Wait(_gate, wait);
                        if (_pcmQueue.Count > 0) break;
                    }

                    while (written < count && _pcmQueue.Count > 0)
                    {
                        var head = _pcmQueue.Peek();
                        int need = count - written;
                        if (head.Length <= need)
                        {
                            Buffer.BlockCopy(head, 0, dest, written, head.Length);
                            written += head.Length;
                            _queuedBytes -= head.Length;
                            _pcmQueue.Dequeue();
                        }
                        else
                        {
                            Buffer.BlockCopy(head, 0, dest, written, need);
                            written += need;
                            var rest = new byte[head.Length - need];
                            Buffer.BlockCopy(head, need, rest, 0, rest.Length);
                            _queuedBytes -= need;
                            _pcmQueue.Dequeue();
                            _pcmQueue.Enqueue(rest);
                        }
                    }
                }
                return written;
            }
            return written;
        }

        /// <summary>生成 44 字节标准 WAV 头（长度未知时用 0xFFFFFFFF，客户端按流式处理）</summary>
        public static byte[] BuildWavHeader(int dataBytes)
        {
            using var ms = new MemoryStream(44);
            using var bw = new BinaryWriter(ms);
            bw.Write(new[] { 'R', 'I', 'F', 'F' });
            bw.Write(dataBytes + 36);
            bw.Write(new[] { 'W', 'A', 'V', 'E' });
            bw.Write(new[] { 'f', 'm', 't', ' ' });
            bw.Write(16);
            bw.Write((short)1);
            bw.Write((short)Channels);
            bw.Write(SampleRate);
            bw.Write(SampleRate * Channels * BitsPerSample / 8);
            bw.Write((short)(Channels * BitsPerSample / 8));
            bw.Write((short)BitsPerSample);
            bw.Write(new[] { 'd', 'a', 't', 'a' });
            bw.Write(dataBytes);
            bw.Flush();
            return ms.ToArray();
        }

        public void StopCapture()
        {
            WaveInEvent wave;
            lock (_gate)
            {
                wave = _waveIn;
                _waveIn = null;
                _pcmQueue.Clear();
                _queuedBytes = 0;
            }
            if (wave == null) return;
            try
            {
                wave.DataAvailable -= OnData;
                wave.StopRecording();
                wave.Dispose();
            }
            catch (Exception ex)
            {
                Log.Write("停止麦克风异常: " + ex.Message);
            }
        }

        public void Dispose()
        {
            StopCapture();
        }
    }
}
