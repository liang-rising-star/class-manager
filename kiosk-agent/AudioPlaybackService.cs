using System;
using NAudio.Wave;

namespace ClassManager.KioskAgent
{
    /// <summary>
    /// 实时对讲播放：教师端把 PCM 片段通过 HTTP 推上来，这里排队送到扬声器。
    /// 格式固定 16kHz / 16bit / 单声道，和采集端一致。
    /// </summary>
    public class AudioPlaybackService : IDisposable
    {
        public const int SampleRate = 16000;
        public const int BitsPerSample = 16;
        public const int Channels = 1;

        private readonly object _gate = new object();
        private WaveOutEvent _output;
        private BufferedWaveProvider _buffer;
        private long _totalBytes;

        public bool IsPlaying
        {
            get { lock (_gate) return _output != null && _output.PlaybackState == PlaybackState.Playing; }
        }

        public double BufferedSeconds
        {
            get
            {
                lock (_gate)
                {
                    if (_buffer == null) return 0;
                    return Math.Round(_buffer.BufferedDuration.TotalSeconds, 2);
                }
            }
        }

        public long TotalBytes { get { lock (_gate) return _totalBytes; } }

        /// <summary>把一段 PCM 数据送进播放队列（按需启动播放设备）。</summary>
        public void Enqueue(byte[] pcm)
        {
            if (pcm == null || pcm.Length == 0) return;

            lock (_gate)
            {
                try
                {
                    if (_output == null)
                    {
                        _buffer = new BufferedWaveProvider(new WaveFormat(SampleRate, BitsPerSample, Channels))
                        {
                            BufferDuration = TimeSpan.FromSeconds(4),
                            DiscardOnBufferOverflow = true,
                            ReadFully = true
                        };

                        _output = new WaveOutEvent { DesiredLatency = 120, NumberOfBuffers = 3 };
                        _output.Init(_buffer);
                        _output.Play();
                    }

                    // 缓冲里积压太多说明网络抖动，丢掉旧的保持实时
                    if (_buffer.BufferedDuration.TotalSeconds > 2.0)
                        _buffer.ClearBuffer();

                    _buffer.AddSamples(pcm, 0, pcm.Length);
                    _totalBytes += pcm.Length;

                    if (_output.PlaybackState != PlaybackState.Playing)
                        _output.Play();
                }
                catch (Exception ex)
                {
                    Log.Write("对讲播放失败: " + ex.Message);
                }
            }
        }

        /// <summary>停止播放并清空缓冲（挂断对讲时调用）。</summary>
        public void Stop()
        {
            lock (_gate)
            {
                try
                {
                    _buffer?.ClearBuffer();
                    if (_output != null)
                    {
                        _output.Stop();
                        _output.Dispose();
                        _output = null;
                    }
                    _buffer = null;
                }
                catch (Exception ex)
                {
                    Log.Write("停止对讲播放异常: " + ex.Message);
                }
            }
        }

        public void Dispose() => Stop();
    }
}
