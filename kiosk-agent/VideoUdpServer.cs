using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace ClassManager.KioskAgent
{
    /// <summary>
    /// 视频 UDP 推流：教师端发 KVSUB 订阅，一体机端把摄像头帧切片后以 UDP 推过去。
    /// 其余接口（设置、状态、指令、音频）仍然走 TCP/HTTP。
    ///
    /// 协议（小端）：
    ///   客户端 → 一体机端：ASCII 文本 "KVSUB &lt;访问密钥&gt;" / "KVPING &lt;访问密钥&gt;" / "KVBYE &lt;访问密钥&gt;"
    ///   一体机端 → 客户端（每片 16 字节头 + 载荷）：
    ///     magic "KVD1" | frameId(u32) | fragIndex(u16) | fragCount(u16) | frameLen(u32) | payload
    /// </summary>
    public class VideoUdpServer : IDisposable
    {
        private const int HeaderSize = 16;
        private const int MaxPayload = 1300;
        private const int SubscriberTimeoutSeconds = 12;

        private sealed class Subscriber
        {
            public IPEndPoint EndPoint;
            public DateTime LastSeen;
        }

        private readonly ConcurrentDictionary<string, Subscriber> _subscribers = new();
        private UdpClient _udp;
        private Thread _recvThread;
        private Thread _sendThread;
        private volatile bool _running;
        private volatile byte[] _pendingFrame;
        private string _accessKey = "";
        private int _frameId;
        private int _port;
        private DateTime _lastFrameSent = DateTime.MinValue;
        private long _framesSent;

        public int Port => _port;
        public int SubscriberCount => _subscribers.Count;
        public DateTime LastFrameSent => _lastFrameSent;
        public long FramesSent => Interlocked.Read(ref _framesSent);

        public void Start(int port, string accessKey)
        {
            Stop();
            _port = port;
            _accessKey = accessKey ?? "";
            try
            {
                _udp = new UdpClient(new IPEndPoint(IPAddress.Any, port));
                _udp.Client.ReceiveBufferSize = 1 << 20;
                _running = true;

                _recvThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "video-udp-recv" };
                _recvThread.Start();

                _sendThread = new Thread(SendLoop) { IsBackground = true, Name = "video-udp-send" };
                _sendThread.Start();

                Log.Write($"视频 UDP 已监听: 0.0.0.0:{port}");
            }
            catch (Exception ex)
            {
                Log.Write("视频 UDP 启动失败: " + ex.Message);
            }
        }

        public void UpdateKey(string accessKey)
        {
            _accessKey = accessKey ?? "";
        }

        /// <summary>摄像头产生新帧时调用（来自采集线程）</summary>
        public void PushFrame(byte[] jpeg)
        {
            if (jpeg == null || jpeg.Length == 0) return;
            _pendingFrame = jpeg;
        }

        private void ReceiveLoop()
        {
            var remote = new IPEndPoint(IPAddress.Any, 0);
            while (_running)
            {
                try
                {
                    var data = _udp.Receive(ref remote);
                    if (data == null || data.Length == 0) continue;

                    var text = Encoding.ASCII.GetString(data).Trim();
                    int space = text.IndexOf(' ');
                    string cmd = space > 0 ? text.Substring(0, space).ToUpperInvariant() : text.ToUpperInvariant();
                    string key = space > 0 ? text.Substring(space + 1).Trim() : "";
                    string id = remote.ToString();

                    switch (cmd)
                    {
                        case "KVSUB":
                        case "KVPING":
                            if (_accessKey.Length > 0 && !string.Equals(key, _accessKey, StringComparison.Ordinal))
                            {
                                Log.Write($"视频订阅被拒绝（密钥不匹配）: {id}");
                                continue;
                            }
                            _subscribers[id] = new Subscriber { EndPoint = remote, LastSeen = DateTime.Now };
                            break;
                        case "KVBYE":
                            _subscribers.TryRemove(id, out _);
                            break;
                    }
                }
                catch (ObjectDisposedException) { return; }
                catch (SocketException) { continue; }
                catch (Exception ex)
                {
                    if (!_running) return;
                    Log.Write("视频 UDP 接收异常: " + ex.Message);
                }
            }
        }

        private void SendLoop()
        {
            while (_running)
            {
                try
                {
                    var frame = _pendingFrame;
                    if (frame == null || _subscribers.IsEmpty)
                    {
                        Thread.Sleep(15);
                        continue;
                    }
                    _pendingFrame = null;

                    // 清理超时订阅
                    var now = DateTime.Now;
                    foreach (var kv in _subscribers)
                    {
                        if ((now - kv.Value.LastSeen).TotalSeconds > SubscriberTimeoutSeconds)
                            _subscribers.TryRemove(kv.Key, out _);
                    }
                    if (_subscribers.IsEmpty) continue;

                    int frameId = Interlocked.Increment(ref _frameId);
                    int fragCount = (frame.Length + MaxPayload - 1) / MaxPayload;
                    if (fragCount > ushort.MaxValue) continue;

                    var packet = new byte[HeaderSize + MaxPayload];
                    packet[0] = (byte)'K'; packet[1] = (byte)'V'; packet[2] = (byte)'D'; packet[3] = (byte)'1';
                    WriteU32(packet, 4, (uint)frameId);
                    WriteU32(packet, 12, (uint)frame.Length);

                    var targets = new List<Subscriber>(_subscribers.Values);

                    for (int i = 0; i < fragCount; i++)
                    {
                        int offset = i * MaxPayload;
                        int len = Math.Min(MaxPayload, frame.Length - offset);
                        WriteU16(packet, 8, (ushort)i);
                        WriteU16(packet, 10, (ushort)fragCount);
                        Buffer.BlockCopy(frame, offset, packet, HeaderSize, len);

                        foreach (var sub in targets)
                        {
                            try { _udp.Send(packet, HeaderSize + len, sub.EndPoint); }
                            catch { /* 单个订阅失败不影响其他人 */ }
                        }
                    }

                    _lastFrameSent = DateTime.Now;
                    Interlocked.Increment(ref _framesSent);
                }
                catch (ObjectDisposedException) { return; }
                catch (Exception ex)
                {
                    if (!_running) return;
                    Log.Write("视频 UDP 发送异常: " + ex.Message);
                    Thread.Sleep(50);
                }
            }
        }

        private static void WriteU16(byte[] buf, int offset, ushort value)
        {
            buf[offset] = (byte)(value & 0xFF);
            buf[offset + 1] = (byte)((value >> 8) & 0xFF);
        }

        private static void WriteU32(byte[] buf, int offset, uint value)
        {
            buf[offset] = (byte)(value & 0xFF);
            buf[offset + 1] = (byte)((value >> 8) & 0xFF);
            buf[offset + 2] = (byte)((value >> 16) & 0xFF);
            buf[offset + 3] = (byte)((value >> 24) & 0xFF);
        }

        public void Stop()
        {
            _running = false;
            _pendingFrame = null;
            try { _udp?.Close(); } catch { }
            try { _udp?.Dispose(); } catch { }
            _udp = null;
            _subscribers.Clear();
        }

        public void Dispose() => Stop();
    }
}
