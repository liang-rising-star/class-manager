using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClassManager.KioskAgent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClassManager.KioskAgent
{
    /// <summary>
    /// 本地 Web 服务：一体机上的设置界面 + 教师端对接接口。
    /// </summary>
    public class AgentServer
    {
        public const string Version = "1.0.0";
        private const string SessionCookie = "kiosk_session";

        private readonly AgentConfig _config;
        private readonly ConcurrentDictionary<string, DateTime> _sessions = new();

        private WebApplication _app;
        private CancellationTokenSource _cts;

        public CameraService Camera { get; } = new CameraService();
        public MicService Mic { get; } = new MicService();
        public VideoUdpServer Video { get; } = new VideoUdpServer();
        public AudioPlaybackService Talkback { get; } = new AudioPlaybackService();
        public ScreenService Screen { get; } = new ScreenService();

        /// <summary>配置变更后通知托盘等刷新</summary>
        public event Action ConfigChanged;

        public AgentServer(AgentConfig config)
        {
            _config = config;
        }

        public string LocalUrl => $"http://127.0.0.1:{_config.Port}/";
        public string LanUrl
        {
            get
            {
                var host = GetLanAddress();
                return $"http://{host}:{_config.Port}/";
            }
        }

        public static string GetLanAddress()
        {
            try
            {
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                    foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                            return addr.Address.ToString();
                    }
                }
            }
            catch { }
            return "127.0.0.1";
        }

        public async Task StartAsync()
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ContentRootPath = AppContext.BaseDirectory,
                ApplicationName = "ClassManager.KioskAgent"
            });

            builder.Logging.ClearProviders();
            builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
                p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod().WithExposedHeaders("Content-Type")));

            IPAddress bind = IPAddress.Any;
            if (!string.IsNullOrWhiteSpace(_config.BindAddress) && _config.BindAddress != "0.0.0.0")
                IPAddress.TryParse(_config.BindAddress, out bind);
            bind ??= IPAddress.Any;

            builder.WebHost.ConfigureKestrel(o =>
            {
                o.Listen(bind, _config.Port);
                o.Limits.MaxRequestBodySize = 4 * 1024 * 1024;
            });

            _app = builder.Build();
            _app.UseCors();

            // 静态资源（wwwroot）
            var webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
            if (Directory.Exists(webRoot))
            {
                var provider = new PhysicalFileProvider(webRoot);

                // 首页动态注入资源版本号：WebView2 会启发式缓存静态文件，
                // 老缓存里的 style.css / app.js 不会自动失效，只能用变化的 URL 绕过。
                string indexPath = Path.Combine(webRoot, "index.html");
                _app.Use(async (ctx, next) =>
                {
                    var path = ctx.Request.Path.Value ?? "/";
                    if ((path == "/" || path.Equals("/index.html", StringComparison.OrdinalIgnoreCase))
                        && File.Exists(indexPath))
                    {
                        var html = await File.ReadAllTextAsync(indexPath);
                        html = html.Replace("__ASSET_V__", AssetVersion(webRoot));
                        ctx.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
                        ctx.Response.ContentType = "text/html; charset=utf-8";
                        await ctx.Response.WriteAsync(html);
                        return;
                    }
                    await next();
                });
                _app.UseStaticFiles(new StaticFileOptions { FileProvider = provider });
            }

            // 鉴权中间件
            _app.Use(async (ctx, next) =>
            {
                var path = ctx.Request.Path.Value ?? "/";
                if (!path.StartsWith("/api", StringComparison.OrdinalIgnoreCase) || IsPublicApi(path))
                {
                    await next();
                    return;
                }

                if (!IsAuthorized(ctx))
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    ctx.Response.ContentType = "application/json; charset=utf-8";
                    await ctx.Response.WriteAsync("{\"ok\":false,\"error\":\"未授权：请先登录，或携带 X-Access-Key 请求头\"}");
                    return;
                }
                await next();
            });

            MapApi(_app);

            _cts = new CancellationTokenSource();
            await _app.StartAsync(_cts.Token);

            Camera.FrameReady += OnCameraFrame;
            Video.Start(EffectiveVideoPort, _config.AccessKey);
            Log.Write($"Web 服务已启动: {LocalUrl}（监听 {bind}:{_config.Port}）");
        }

        /// <summary>静态资源版本号：取 wwwroot 下几个关键文件的最后修改时间。</summary>
        private static string AssetVersion(string webRoot)
        {
            long latest = 0;
            foreach (var name in new[] { "style.css", "app.js", "slider.js" })
            {
                try
                {
                    var info = new FileInfo(Path.Combine(webRoot, name));
                    if (info.Exists) latest = Math.Max(latest, info.LastWriteTimeUtc.Ticks);
                }
                catch { }
            }
            return latest.ToString("x");
        }

        private static bool IsPublicApi(string path)
        {
            return path.Equals("/api/info", StringComparison.OrdinalIgnoreCase)
                || path.Equals("/api/login", StringComparison.OrdinalIgnoreCase)
                || path.Equals("/api/setup", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsAuthorized(HttpContext ctx)
        {
            // 1) 教师端：X-Access-Key
            if (ctx.Request.Headers.TryGetValue("X-Access-Key", out var key) && _config.VerifyAccessKey(key))
                return true;
            // 2) 浏览器：会话 Cookie
            if (ctx.Request.Cookies.TryGetValue(SessionCookie, out var sid) && !string.IsNullOrEmpty(sid))
            {
                if (_sessions.TryGetValue(sid, out var expire))
                {
                    if (expire > DateTime.Now) return true;
                    _sessions.TryRemove(sid, out _);
                }
            }
            return false;
        }

        private void MapApi(WebApplication app)
        {
            // ---------- 公开信息 ----------
            app.MapGet("/api/info", (HttpContext ctx) => Json(ctx, new
            {
                ok = true,
                name = "班级管理系统・一体机端",
                version = Version,
                device = _config.DeviceName,
                room = _config.RoomName,
                needSetup = !_config.HasPassword,
                port = _config.Port,
                videoPort = EffectiveVideoPort,
                videoUdp = true,
                cameraEnabled = _config.Camera.Enabled,
                micEnabled = _config.Mic.Enabled,
                remoteExecEnabled = _config.Remote.Enabled,
                serverTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            }));

            // ---------- 登录 / 初始化 ----------
            app.MapPost("/api/setup", async (HttpContext ctx) =>
            {
                if (_config.HasPassword)
                    return Json(ctx, new { ok = false, error = "已设置访问密码，请使用登录" });

                var body = await ReadJson(ctx);
                var pwd = GetString(body, "password");
                if (string.IsNullOrEmpty(pwd) || pwd.Length < 4)
                    return Json(ctx, new { ok = false, error = "密码至少 4 位" });

                _config.SetPassword(pwd);
                Log.Write("已设置访问密码");
                return Json(ctx, new { ok = true });
            });

            app.MapPost("/api/login", async (HttpContext ctx) =>
            {
                var body = await ReadJson(ctx);
                var pwd = GetString(body, "password");
                if (!_config.VerifyPassword(pwd))
                {
                    Log.Write("登录失败：密码错误");
                    return Json(ctx, new { ok = false, error = "密码错误" });
                }

                var sid = Guid.NewGuid().ToString("N");
                _sessions[sid] = DateTime.Now.AddHours(8);
                ctx.Response.Cookies.Append(SessionCookie, sid, new CookieOptions
                {
                    HttpOnly = true,
                    SameSite = SameSiteMode.Lax,
                    Expires = DateTimeOffset.Now.AddHours(8)
                });
                return Json(ctx, new { ok = true });
            });

            app.MapPost("/api/logout", (HttpContext ctx) =>
            {
                if (ctx.Request.Cookies.TryGetValue(SessionCookie, out var sid))
                    _sessions.TryRemove(sid, out _);
                ctx.Response.Cookies.Delete(SessionCookie);
                return Json(ctx, new { ok = true });
            });

            // ---------- 状态 ----------
            app.MapGet("/api/state", (HttpContext ctx) => Json(ctx, new
            {
                ok = true,
                device = _config.DeviceName,
                room = _config.RoomName,
                port = _config.Port,
                lanUrl = LanUrl,
                localUrl = LocalUrl,
                accessKey = _config.AccessKey,
                hasPassword = _config.HasPassword,
                autostart = AutoStart.IsEnabled(),
                camera = new
                {
                    enabled = _config.Camera.Enabled,
                    device = _config.Camera.DeviceName,
                    running = Camera.IsRunning,
                    current = Camera.CurrentDevice,
                    fps = _config.Camera.Fps,
                    actualFps = Camera.Fps,
                    width = _config.Camera.Width,
                    height = _config.Camera.Height,
                    quality = _config.Camera.Quality,
                    error = Camera.LastError
                },
                screen = new
                {
                    running = Screen.IsRunning,
                    clients = Screen.Clients,
                    fps = Screen.Fps,
                    error = Screen.LastError
                },
                talkback = new
                {
                    playing = Talkback.IsPlaying,
                    buffered = Talkback.BufferedSeconds,
                    totalBytes = Talkback.TotalBytes
                },
                video = new
                {
                    udpPort = EffectiveVideoPort,
                    subscribers = Video.SubscriberCount,
                    framesSent = Video.FramesSent
                },
                mic = new
                {
                    enabled = _config.Mic.Enabled,
                    device = _config.Mic.DeviceName,
                    capturing = Mic.IsCapturing,
                    current = Mic.CurrentDevice,
                    level = Mic.Level,
                    gain = _config.Mic.Gain,
                    error = Mic.LastError
                },
                remote = new
                {
                    enabled = _config.Remote.Enabled,
                    timeoutSeconds = _config.Remote.TimeoutSeconds,
                    allowedCommands = _config.Remote.AllowedCommands
                }
            }));

            // ---------- 保存设置 ----------
            app.MapPost("/api/settings", async (HttpContext ctx) =>
            {
                var body = await ReadJson(ctx);

                if (body.TryGetProperty("deviceName", out var dn) && dn.ValueKind == JsonValueKind.String)
                    _config.DeviceName = dn.GetString() ?? _config.DeviceName;
                if (body.TryGetProperty("roomName", out var rn) && rn.ValueKind == JsonValueKind.String)
                    _config.RoomName = rn.GetString() ?? "";

                if (body.TryGetProperty("camera", out var cam) && cam.ValueKind == JsonValueKind.Object)
                {
                    if (cam.TryGetProperty("enabled", out var ce)) _config.Camera.Enabled = ce.GetBoolean();
                    if (cam.TryGetProperty("device", out var cd) && cd.ValueKind == JsonValueKind.String)
                        _config.Camera.DeviceName = cd.GetString() ?? "";
                    if (cam.TryGetProperty("width", out var cw)) _config.Camera.Width = ClampInt(cw, 320, 3840, 1280);
                    if (cam.TryGetProperty("height", out var ch)) _config.Camera.Height = ClampInt(ch, 240, 2160, 720);
                    if (cam.TryGetProperty("fps", out var cf)) _config.Camera.Fps = ClampInt(cf, 1, 30, 15);
                    if (cam.TryGetProperty("quality", out var cq)) _config.Camera.Quality = ClampInt(cq, 30, 95, 70);
                }

                if (body.TryGetProperty("mic", out var mic) && mic.ValueKind == JsonValueKind.Object)
                {
                    if (mic.TryGetProperty("enabled", out var me)) _config.Mic.Enabled = me.GetBoolean();
                    if (mic.TryGetProperty("device", out var md) && md.ValueKind == JsonValueKind.String)
                        _config.Mic.DeviceName = md.GetString() ?? "";
                    if (mic.TryGetProperty("gain", out var mg) && mg.ValueKind == JsonValueKind.Number)
                        _config.Mic.Gain = Math.Clamp(mg.GetDouble(), 0.1, 3.0);
                }

                if (body.TryGetProperty("remote", out var rem) && rem.ValueKind == JsonValueKind.Object)
                {
                    if (rem.TryGetProperty("enabled", out var re)) _config.Remote.Enabled = re.GetBoolean();
                    if (rem.TryGetProperty("timeoutSeconds", out var rt))
                        _config.Remote.TimeoutSeconds = ClampInt(rt, 1, 120, 15);
                    if (rem.TryGetProperty("allowedCommands", out var ac) && ac.ValueKind == JsonValueKind.Array)
                    {
                        _config.Remote.AllowedCommands = ac.EnumerateArray()
                            .Where(x => x.ValueKind == JsonValueKind.String)
                            .Select(x => x.GetString())
                            .Where(x => !string.IsNullOrWhiteSpace(x))
                            .ToList();
                    }
                }

                _config.Save();
                ApplyRuntimeSettings();
                Log.Write("设置已更新");
                return Json(ctx, new { ok = true });
            });

            // ---------- 访问密码 ----------
            app.MapPost("/api/password", async (HttpContext ctx) =>
            {
                var body = await ReadJson(ctx);
                var oldPwd = GetString(body, "oldPassword");
                var newPwd = GetString(body, "newPassword");
                if (!_config.VerifyPassword(oldPwd))
                    return Json(ctx, new { ok = false, error = "当前密码不正确" });
                if (string.IsNullOrEmpty(newPwd) || newPwd.Length < 4)
                    return Json(ctx, new { ok = false, error = "新密码至少 4 位" });

                _config.SetPassword(newPwd);
                _sessions.Clear();
                Log.Write("访问密码已修改");
                return Json(ctx, new { ok = true });
            });

            // ---------- 访问密钥 ----------
            app.MapPost("/api/key/rotate", (HttpContext ctx) =>
            {
                _config.AccessKey = AgentConfig.NewAccessKey();
                _config.Save();
                Video.UpdateKey(_config.AccessKey);
                Log.Write("访问密钥已重置");
                return Json(ctx, new { ok = true, accessKey = _config.AccessKey });
            });

            // ---------- 开机自启 ----------
            app.MapPost("/api/autostart", async (HttpContext ctx) =>
            {
                var body = await ReadJson(ctx);
                bool enabled = false;
                if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("enabled", out var en))
                {
                    if (en.ValueKind == JsonValueKind.True) enabled = true;
                    else if (en.ValueKind == JsonValueKind.False) enabled = false;
                    else if (en.ValueKind == JsonValueKind.String)
                        enabled = bool.TryParse(en.GetString(), out var parsed) && parsed;
                    else if (en.ValueKind == JsonValueKind.Number)
                        enabled = en.TryGetInt32(out var num) && num != 0;
                }

                Log.Write($"开机自启请求: enabled={enabled} (body={body.ValueKind})");
                bool ok = AutoStart.Set(enabled);
                return Json(ctx, new { ok, enabled = AutoStart.IsEnabled() });
            });

            // ---------- 一键配置码（给教师端粘贴用） ----------
            app.MapGet("/api/config-code", (HttpContext ctx) =>
            {
                var payload = new
                {
                    v = 1,
                    n = _config.DeviceName,
                    r = _config.RoomName,
                    h = GetLanAddress(),
                    p = _config.Port,
                    vp = EffectiveVideoPort,
                    k = _config.AccessKey
                };
                var json = JsonSerializer.Serialize(payload);
                var code = Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
                    .TrimEnd('=').Replace('+', '-').Replace('/', '_');
                return Json(ctx, new { ok = true, code, payload });
            });

            // ---------- 摄像头 ----------
            app.MapGet("/api/camera/devices", (HttpContext ctx) =>
            {
                var list = CameraService.ListDevices().Select(d => new { d.Index, d.Name });
                return Json(ctx, new { ok = true, devices = list });
            });

            app.MapPost("/api/camera/select", async (HttpContext ctx) =>
            {
                var body = await ReadJson(ctx);
                var name = GetString(body, "device") ?? "";
                _config.Camera.DeviceName = name;
                _config.Camera.Enabled = true;
                _config.Save();
                ApplyRuntimeSettings();
                return Json(ctx, new { ok = true, current = Camera.CurrentDevice, error = Camera.LastError });
            });

            app.MapPost("/api/camera/stop", (HttpContext ctx) =>
            {
                Camera.Stop();
                _config.Camera.Enabled = false;
                _config.Save();
                return Json(ctx, new { ok = true });
            });

            app.MapGet("/api/camera/snapshot", (HttpContext ctx) =>
            {
                var frame = Camera.GetLatestFrame();
                if (frame == null)
                {
                    ctx.Response.StatusCode = 503;
                    return Json(ctx, new { ok = false, error = "暂无画面" });
                }
                ctx.Response.Headers["Cache-Control"] = "no-store";
                return Results.File(frame, "image/jpeg");
            });

            app.MapGet("/api/camera/stream", async (HttpContext ctx) =>
            {
                if (!_config.Camera.Enabled)
                {
                    ctx.Response.StatusCode = 403;
                    await Json(ctx, new { ok = false, error = "摄像头未启用" }).ExecuteAsync(ctx);
                    return;
                }

                ctx.Response.ContentType = "multipart/x-mixed-replace; boundary=frame";
                ctx.Response.Headers["Cache-Control"] = "no-store";
                ctx.Response.Headers["Connection"] = "close";

                DateTime lastSent = DateTime.MinValue;
                try
                {
                    while (!ctx.RequestAborted.IsCancellationRequested)
                    {
                        var frame = Camera.GetLatestFrame();
                        var at = Camera.LatestAt;
                        if (frame != null && at > lastSent)
                        {
                            lastSent = at;
                            var header = Encoding.ASCII.GetBytes(
                                $"--frame\r\nContent-Type: image/jpeg\r\nContent-Length: {frame.Length}\r\n\r\n");
                            await ctx.Response.Body.WriteAsync(header, ctx.RequestAborted);
                            await ctx.Response.Body.WriteAsync(frame, ctx.RequestAborted);
                            await ctx.Response.Body.WriteAsync(Encoding.ASCII.GetBytes("\r\n"), ctx.RequestAborted);
                            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                        }
                        await Task.Delay(60, ctx.RequestAborted);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Log.Write("视频流中断: " + ex.Message); }
            });

            // ---------- 麦克风 ----------
            app.MapGet("/api/mic/devices", (HttpContext ctx) =>
            {
                var list = MicService.ListDevices().Select(d => new { d.Index, d.Name, d.Channels });
                return Json(ctx, new { ok = true, devices = list });
            });

            app.MapPost("/api/mic/toggle", async (HttpContext ctx) =>
            {
                var body = await ReadJson(ctx);
                if (body.TryGetProperty("enabled", out var en))
                    _config.Mic.Enabled = en.GetBoolean();
                if (body.TryGetProperty("device", out var dv) && dv.ValueKind == JsonValueKind.String)
                    _config.Mic.DeviceName = dv.GetString() ?? "";
                _config.Save();
                ApplyRuntimeSettings();
                return Json(ctx, new { ok = true, enabled = _config.Mic.Enabled, current = Mic.CurrentDevice });
            });

            app.MapGet("/api/mic/level", (HttpContext ctx) => Json(ctx, new
            {
                ok = true,
                enabled = _config.Mic.Enabled,
                capturing = Mic.IsCapturing,
                level = Mic.Level,
                device = Mic.CurrentDevice,
                error = Mic.LastError
            }));

            app.MapGet("/api/mic/stream", async (HttpContext ctx) =>
            {
                if (!_config.Mic.Enabled)
                {
                    ctx.Response.StatusCode = 403;
                    await Json(ctx, new { ok = false, error = "麦克风未启用" }).ExecuteAsync(ctx);
                    return;
                }

                ctx.Response.ContentType = "audio/wav";
                ctx.Response.Headers["Cache-Control"] = "no-store";

                try
                {
                    await ctx.Response.Body.WriteAsync(MicService.BuildWavHeader(int.MaxValue), ctx.RequestAborted);
                    await ctx.Response.Body.FlushAsync(ctx.RequestAborted);

                    var buffer = new byte[4096];
                    while (!ctx.RequestAborted.IsCancellationRequested)
                    {
                        int n = Mic.ReadPcm(buffer, buffer.Length, 1000);
                        if (n <= 0)
                        {
                            await Task.Delay(20, ctx.RequestAborted);
                            continue;
                        }
                        await ctx.Response.Body.WriteAsync(buffer.AsMemory(0, n), ctx.RequestAborted);
                        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Log.Write("音频流中断: " + ex.Message); }
            });

            // ---------- 语音播报（班级叫号用） ----------
            app.MapPost("/api/speak", async (HttpContext ctx) =>
            {
                var body = await ReadJson(ctx);
                var text = GetString(body, "text");
                int rate = 0;
                if (body.TryGetProperty("rate", out var rt) && rt.ValueKind == JsonValueKind.Number)
                    rate = rt.GetInt32();

                if (string.IsNullOrWhiteSpace(text))
                    return Json(ctx, new { ok = false, error = "播报内容为空" });

                bool queued = SpeakService.Enqueue(text, rate);
                if (!queued)
                    return Json(ctx, new { ok = false, error = SpeakService.LastError });

                return Json(ctx, new { ok = true, queued = true, pending = SpeakService.PendingCount });
            });

            app.MapPost("/api/speak/stop", (HttpContext ctx) =>
            {
                SpeakService.StopAll();
                return Json(ctx, new { ok = true });
            });

            // ---------- 屏幕直播（按需采集） ----------
            app.MapGet("/api/screen/snapshot", (HttpContext ctx) =>
            {
                Screen.Acquire();
                try
                {
                    // 等一小会儿让第一帧出来
                    for (int i = 0; i < 20 && Screen.GetLatestFrame() == null; i++)
                        Thread.Sleep(100);

                    var frame = Screen.GetLatestFrame();
                    if (frame == null)
                    {
                        ctx.Response.StatusCode = 503;
                        return Json(ctx, new { ok = false, error = "屏幕画面尚未就绪" });
                    }
                    ctx.Response.Headers["Cache-Control"] = "no-store";
                    return Results.File(frame, "image/jpeg");
                }
                finally { Screen.Release(); }
            });

            app.MapGet("/api/screen/stream", async (HttpContext ctx) =>
            {
                Screen.Configure(5, 60);
                Screen.Acquire();
                ctx.Response.ContentType = "multipart/x-mixed-replace; boundary=frame";
                ctx.Response.Headers["Cache-Control"] = "no-store";

                DateTime lastSent = DateTime.MinValue;
                try
                {
                    while (!ctx.RequestAborted.IsCancellationRequested)
                    {
                        var frame = Screen.GetLatestFrame();
                        var at = Screen.LatestAt;
                        if (frame != null && at > lastSent)
                        {
                            lastSent = at;
                            var head = Encoding.ASCII.GetBytes(
                                $"--frame\r\nContent-Type: image/jpeg\r\nContent-Length: {frame.Length}\r\n\r\n");
                            await ctx.Response.Body.WriteAsync(head, ctx.RequestAborted);
                            await ctx.Response.Body.WriteAsync(frame, ctx.RequestAborted);
                            await ctx.Response.Body.WriteAsync(Encoding.ASCII.GetBytes("\r\n"), ctx.RequestAborted);
                            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                        }
                        await Task.Delay(120, ctx.RequestAborted);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Log.Write("屏幕流中断: " + ex.Message); }
                finally { Screen.Release(); }
            });

            // ---------- 实时对讲（教师端 → 教室音箱） ----------
            app.MapPost("/api/audio/in", async (HttpContext ctx) =>
            {
                using var ms = new MemoryStream();
                await ctx.Request.Body.CopyToAsync(ms);
                var pcm = ms.ToArray();
                if (pcm.Length == 0)
                    return Json(ctx, new { ok = false, error = "空音频" });

                if (pcm.Length % 2 != 0)
                    Array.Resize(ref pcm, pcm.Length - 1);   // 16bit 样本必须偶数

                Talkback.Enqueue(pcm);
                return Json(ctx, new { ok = true, buffered = Talkback.BufferedSeconds });
            });

            app.MapPost("/api/audio/stop", (HttpContext ctx) =>
            {
                Talkback.Stop();
                Screen.Dispose();
                return Json(ctx, new { ok = true });
            });

            // ---------- 远程执行 ----------
            app.MapPost("/api/exec", async (HttpContext ctx) =>
            {
                if (!_config.Remote.Enabled)
                    return Json(ctx, new { ok = false, error = "未授权远程执行指令" });

                // 必须携带访问密钥（会话登录不足以远程执行）
                if (!(ctx.Request.Headers.TryGetValue("X-Access-Key", out var key) && _config.VerifyAccessKey(key)))
                    return Json(ctx, new { ok = false, error = "远程执行需要 X-Access-Key" });

                var body = await ReadJson(ctx);
                var command = GetString(body, "command");
                var result = RemoteExecService.Run(command, _config.Remote.TimeoutSeconds, _config.Remote.AllowedCommands);
                return Json(ctx, new
                {
                    ok = result.Ok,
                    exitCode = result.ExitCode,
                    timedOut = result.TimedOut,
                    output = result.Output,
                    error = result.Error
                });
            });
        }

        /// <summary>实际使用的视频 UDP 端口（0 表示与 HTTP 端口相同）</summary>
        public int EffectiveVideoPort => _config.VideoPort > 0 ? _config.VideoPort : _config.Port;

        private void OnCameraFrame(byte[] jpeg) => Video.PushFrame(jpeg);

        /// <summary>清空所有浏览器会话：窗口关掉就等于退出登录。</summary>
        public void ClearSessions(string reason = "")
        {
            try
            {
                int n = _sessions.Count;
                _sessions.Clear();
                if (n > 0) Log.Write($"登录状态已清除（{n} 个会话）{reason}");
            }
            catch (Exception ex)
            {
                Log.Write("清除会话失败: " + ex.Message);
            }
        }

        public void ApplyRuntimeSettings()
        {
            try
            {
                if (_config.Camera.Enabled)
                    Camera.Start(_config.Camera.DeviceName, _config.Camera.Width, _config.Camera.Height,
                                 _config.Camera.Fps, _config.Camera.Quality);
                else
                    Camera.Stop();

                Mic.Apply(_config.Mic.Enabled, _config.Mic.DeviceName, _config.Mic.Gain);
            }
            catch (Exception ex)
            {
                Log.Write("应用运行时设置失败: " + ex.Message);
            }
            ConfigChanged?.Invoke();
        }

        public async Task StopAsync()
        {
            try
            {
                Camera.Stop();
                Mic.StopCapture();
                Video.Stop();
                Talkback.Stop();
                Screen.Dispose();
                Camera.FrameReady -= OnCameraFrame;
                if (_app != null)
                {
                    _cts?.Cancel();
                    await _app.StopAsync(TimeSpan.FromSeconds(3));
                    await _app.DisposeAsync();
                }
            }
            catch (Exception ex)
            {
                Log.Write("停止服务异常: " + ex.Message);
            }
        }

        // ---------- 辅助 ----------

        private static int ClampInt(JsonElement el, int min, int max, int fallback)
        {
            if (el.ValueKind != JsonValueKind.Number) return fallback;
            var v = el.GetInt32();
            return Math.Clamp(v, min, max);
        }

        private static async Task<JsonElement> ReadJson(HttpContext ctx)
        {
            try
            {
                using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
                return doc.RootElement.Clone();
            }
            catch
            {
                return JsonDocument.Parse("{}").RootElement.Clone();
            }
        }

        private static string GetString(JsonElement obj, string name)
        {
            if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
            return null;
        }

        private static IResult Json(HttpContext ctx, object payload)
        {
            return Results.Json(payload, new JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
        }
    }
}
