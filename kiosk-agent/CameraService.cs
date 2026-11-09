using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using AForge.Video;
using AForge.Video.DirectShow;

namespace ClassManager.KioskAgent
{
    public class CameraDeviceInfo
    {
        public int Index { get; set; }
        public string Name { get; set; }
        public string Moniker { get; set; }
    }

    /// <summary>
    /// 摄像头采集：DirectShow 按设备名选源，输出 JPEG 帧供 MJPEG 推送。
    /// </summary>
    public class CameraService : IDisposable
    {
        private readonly object _gate = new object();
        private VideoCaptureDevice _device;
        private byte[] _latestJpeg;
        private DateTime _latestAt = DateTime.MinValue;
        private DateTime _lastFrameAt = DateTime.MinValue;
        private int _frameCount;
        private double _fps;
        private DateTime _fpsWindowStart = DateTime.Now;
        private int _fpsWindowCount;
        private string _lastError = "";
        private string _currentDevice = "";
        private int _targetWidth = 1280;
        private int _targetHeight = 720;
        private int _quality = 70;
        private int _minFrameMs = 66; // ~15fps
        private System.Threading.Timer _watchdog;

        /// <summary>每产生一帧 JPEG 触发（UDP 推流用）</summary>
        public event Action<byte[]> FrameReady;

        public bool IsRunning
        {
            get { lock (_gate) return _device != null && _device.IsRunning; }
        }

        public string CurrentDevice { get { lock (_gate) return _currentDevice; } }
        public string LastError { get { lock (_gate) return _lastError; } }
        public double Fps { get { lock (_gate) return Math.Round(_fps, 1); } }
        public DateTime LatestAt { get { lock (_gate) return _latestAt; } }

        public static List<CameraDeviceInfo> ListDevices()
        {
            var result = new List<CameraDeviceInfo>();
            try
            {
                var collection = new FilterInfoCollection(FilterCategory.VideoInputDevice);
                int i = 0;
                foreach (FilterInfo info in collection)
                {
                    result.Add(new CameraDeviceInfo { Index = i++, Name = info.Name, Moniker = info.MonikerString });
                }
            }
            catch (Exception ex)
            {
                Log.Write("枚举摄像头失败: " + ex.Message);
            }
            return result;
        }

        /// <summary>按设备名启动采集，空名字表示第一个设备</summary>
        public void Start(string deviceName, int width, int height, int fps, int quality)
        {
            Stop();

            var devices = ListDevices();
            if (devices.Count == 0)
            {
                lock (_gate) _lastError = "未检测到摄像头设备";
                return;
            }

            CameraDeviceInfo target = null;
            if (!string.IsNullOrWhiteSpace(deviceName))
                target = devices.Find(d => string.Equals(d.Name, deviceName, StringComparison.OrdinalIgnoreCase));
            target ??= devices[0];

            lock (_gate)
            {
                _targetWidth = Math.Max(160, width);
                _targetHeight = Math.Max(120, height);
                _quality = Math.Clamp(quality, 30, 95);
                _minFrameMs = Math.Max(15, 1000 / Math.Clamp(fps, 1, 30));
                _lastError = "";
                _currentDevice = target.Name;
                _fpsWindowStart = DateTime.Now;
                _fpsWindowCount = 0;
            }

            try
            {
                var dev = new VideoCaptureDevice(target.Moniker);
                dev.NewFrame += OnNewFrame;
                dev.VideoSourceError += (s, e) => { lock (_gate) _lastError = e.Description ?? "采集错误"; };
                dev.Start();

                lock (_gate)
                {
                    _device = dev;
                    _lastFrameAt = DateTime.Now;
                }
                Log.Write($"摄像头已启动: {target.Name}");

                _watchdog ??= new System.Threading.Timer(_ => Watchdog(), null, 5000, 5000);
            }
            catch (Exception ex)
            {
                lock (_gate) _lastError = ex.Message;
                Log.Write("启动摄像头失败: " + ex.Message);
            }
        }

        private void Watchdog()
        {
            bool needRestart;
            string device;
            lock (_gate)
            {
                if (_device == null) return;
                needRestart = (DateTime.Now - _lastFrameAt).TotalSeconds > 6;
                device = _currentDevice;
            }

            if (needRestart)
            {
                Log.Write("摄像头超过 6 秒无画面，尝试重连: " + device);
                var cfg = AgentConfig.Load();
                Start(string.IsNullOrEmpty(device) ? cfg.Camera.DeviceName : device,
                      cfg.Camera.Width, cfg.Camera.Height, cfg.Camera.Fps, cfg.Camera.Quality);
            }
        }

        private void OnNewFrame(object sender, NewFrameEventArgs e)
        {
            try
            {
                lock (_gate)
                {
                    if ((DateTime.Now - _latestAt).TotalMilliseconds < _minFrameMs) return;
                }

                Bitmap frame = e.Frame;
                if (frame == null) return;

                using var ms = new MemoryStream();
                using (var scaled = Resize(frame))
                {
                    var codec = GetJpegCodec();
                    if (codec != null)
                    {
                        using var parameters = new EncoderParameters(1);
                        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)_quality);
                        scaled.Save(ms, codec, parameters);
                    }
                    else
                    {
                        scaled.Save(ms, ImageFormat.Jpeg);
                    }
                }

                var bytes = ms.ToArray();
                try { FrameReady?.Invoke(bytes); } catch { }
                lock (_gate)
                {
                    _latestJpeg = bytes;
                    _latestAt = DateTime.Now;
                    _lastFrameAt = DateTime.Now;
                    _frameCount++;
                    _fpsWindowCount++;
                    var span = (DateTime.Now - _fpsWindowStart).TotalSeconds;
                    if (span >= 1.0)
                    {
                        _fps = _fpsWindowCount / span;
                        _fpsWindowStart = DateTime.Now;
                        _fpsWindowCount = 0;
                    }
                }
            }
            catch (Exception ex)
            {
                lock (_gate) _lastError = ex.Message;
            }
        }

        private Bitmap Resize(Bitmap source)
        {
            int tw, th;
            lock (_gate)
            {
                tw = _targetWidth;
                th = _targetHeight;
            }

            // 只缩小不放大
            double scale = Math.Min(1.0, Math.Min((double)tw / source.Width, (double)th / source.Height));
            int w = Math.Max(2, (int)(source.Width * scale));
            int h = Math.Max(2, (int)(source.Height * scale));
            if (scale >= 1.0) return (Bitmap)source.Clone();

            var dest = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using var g = Graphics.FromImage(dest);
            g.InterpolationMode = InterpolationMode.Bilinear;
            g.DrawImage(source, new Rectangle(0, 0, w, h));
            return dest;
        }

        private static ImageCodecInfo GetJpegCodec()
        {
            foreach (var codec in ImageCodecInfo.GetImageEncoders())
                if (codec.FormatID == ImageFormat.Jpeg.Guid) return codec;
            return null;
        }

        /// <summary>取最新一帧 JPEG，没有画面时返回 null</summary>
        public byte[] GetLatestFrame()
        {
            lock (_gate)
            {
                if (_latestJpeg == null) return null;
                // 10 秒前的帧视为不可用
                if ((DateTime.Now - _latestAt).TotalSeconds > 10) return null;
                return _latestJpeg;
            }
        }

        public void Stop()
        {
            VideoCaptureDevice dev;
            lock (_gate)
            {
                dev = _device;
                _device = null;
                _latestJpeg = null;
            }
            if (dev == null) return;
            try
            {
                dev.NewFrame -= OnNewFrame;
                if (dev.IsRunning) dev.SignalToStop();
                dev.WaitForStop();
            }
            catch (Exception ex)
            {
                Log.Write("停止摄像头异常: " + ex.Message);
            }
            Log.Write("摄像头已停止");
        }

        public void Dispose()
        {
            _watchdog?.Dispose();
            _watchdog = null;
            Stop();
        }
    }
}
