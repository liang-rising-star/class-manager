using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace ClassManager.KioskAgent
{
    /// <summary>
    /// 屏幕直播：抓主屏画面 → JPEG。按需启动：有管理端来拉流才采集，没人看就自动停。
    /// </summary>
    public class ScreenService : IDisposable
    {
        private readonly object _gate = new object();
        private Thread _thread;
        private volatile bool _running;
        private int _clients;
        private DateTime _lastAccess = DateTime.MinValue;
        private byte[] _latest;
        private DateTime _latestAt = DateTime.MinValue;
        private double _fps;
        private string _lastError = "";

        private int _fpsTarget = 5;
        private int _quality = 60;
        private int _maxWidth = 1600;

        public bool IsRunning => _running;
        public int Clients => _clients;
        public string LastError { get { lock (_gate) return _lastError; } }
        public double Fps { get { lock (_gate) return Math.Round(_fps, 1); } }
        public DateTime LatestAt { get { lock (_gate) return _latestAt; } }

        public void Configure(int fps, int quality)
        {
            _fpsTarget = Math.Clamp(fps, 1, 15);
            _quality = Math.Clamp(quality, 30, 90);
        }

        /// <summary>有客户端开始拉流：确保采集线程在跑。</summary>
        public void Acquire()
        {
            Interlocked.Increment(ref _clients);
            _lastAccess = DateTime.Now;
            EnsureRunning();
        }

        /// <summary>客户端断开：没人看就停。</summary>
        public void Release()
        {
            if (Interlocked.Decrement(ref _clients) < 0)
                Interlocked.Exchange(ref _clients, 0);
            _lastAccess = DateTime.Now;
        }

        private void EnsureRunning()
        {
            lock (_gate)
            {
                if (_running) return;
                _running = true;
                _thread = new Thread(Loop) { IsBackground = true, Name = "screen-capture" };
                _thread.Start();
                Log.Write("屏幕直播已启动");
            }
        }

        private void Loop()
        {
            int minInterval = Math.Max(60, 1000 / Math.Max(1, _fpsTarget));

            while (_running)
            {
                // 没人拉流超过 15 秒就退出采集，省 CPU
                if (_clients <= 0 && (DateTime.Now - _lastAccess).TotalSeconds > 15)
                {
                    _running = false;
                    Log.Write("屏幕直播已停止（无观看者）");
                    break;
                }

                var sw = Stopwatch.StartNew();
                try
                {
                    CaptureOnce();
                }
                catch (Exception ex)
                {
                    lock (_gate) _lastError = ex.Message;
                    Thread.Sleep(500);
                }

                int spent = (int)sw.ElapsedMilliseconds;
                int wait = minInterval - spent;
                if (wait > 0) Thread.Sleep(wait);
            }
        }

        private void CaptureOnce()
        {
            var screen = Screen.PrimaryScreen;
            if (screen == null) return;
            var bounds = screen.Bounds;

            using var full = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(full))
            {
                g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
            }

            Bitmap scaled = full;
            double scale = Math.Min(1.0, (double)_maxWidth / full.Width);
            if (scale < 0.999)
            {
                int w = Math.Max(2, (int)(full.Width * scale));
                int h = Math.Max(2, (int)(full.Height * scale));
                scaled = new Bitmap(w, h, PixelFormat.Format24bppRgb);
                using var g2 = Graphics.FromImage(scaled);
                g2.InterpolationMode = InterpolationMode.Bilinear;
                g2.DrawImage(full, new Rectangle(0, 0, w, h));
            }

            try
            {
                using var ms = new MemoryStream();
                var codec = GetJpegCodec();
                if (codec != null)
                {
                    using var ps = new EncoderParameters(1);
                    ps.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)_quality);
                    scaled.Save(ms, codec, ps);
                }
                else
                {
                    scaled.Save(ms, ImageFormat.Jpeg);
                }

                var bytes = ms.ToArray();
                lock (_gate)
                {
                    _latest = bytes;
                    _latestAt = DateTime.Now;
                    double span = Math.Max(0.001, Math.Min(1.0, (DateTime.Now - _latestAt).TotalSeconds + 0.001));
                    _fps = 1000.0 / Math.Max(1, minIntervalSafe());
                }
            }
            finally
            {
                if (!ReferenceEquals(scaled, full)) scaled.Dispose();
            }
        }

        private int minIntervalSafe() => Math.Max(60, 1000 / Math.Max(1, _fpsTarget));

        private static ImageCodecInfo GetJpegCodec()
        {
            foreach (var codec in ImageCodecInfo.GetImageEncoders())
                if (codec.FormatID == ImageFormat.Jpeg.Guid) return codec;
            return null;
        }

        public byte[] GetLatestFrame()
        {
            lock (_gate)
            {
                if (_latest == null) return null;
                if ((DateTime.Now - _latestAt).TotalSeconds > 10) return null;
                return _latest;
            }
        }

        public void Dispose()
        {
            _running = false;
        }
    }
}
