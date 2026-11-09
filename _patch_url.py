import io

# ---------------- 1) 一体机端：远程打开网址 ----------------
p = 'kiosk-agent/AgentServer.cs'
s = io.open(p, encoding='utf-8').read()

anchor = '''            // ---------- 关机（整台一体机） ----------'''
block = '''            // ---------- 在一体机上用浏览器打开指定网址 ----------
            app.MapPost("/api/browser/open", (HttpContext ctx) =>
            {
                string raw = "";
                try
                {
                    using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
                    var body = reader.ReadToEnd();
                    if (!string.IsNullOrWhiteSpace(body))
                    {
                        var obj = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(body);
                        if (obj != null && obj.TryGetValue("url", out var v))
                            raw = v?.ToString() ?? "";
                    }
                }
                catch { }

                var url = (raw ?? "").Trim();
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    return Json(ctx, new { ok = false, error = "只能打开 http/https 网址" });
                }

                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.ToString())
                    {
                        UseShellExecute = true
                    });
                    Log.Write("远程打开网址: " + uri);
                    return Json(ctx, new { ok = true, url = uri.ToString() });
                }
                catch (Exception ex)
                {
                    return Json(ctx, new { ok = false, error = ex.Message });
                }
            });

            // ---------- 关机（整台一体机） ----------'''
assert anchor in s
s = s.replace(anchor, block, 1)
io.open(p, 'w', encoding='utf-8').write(s)
print('browser open endpoint added')

# ---------------- 2) 管理端代理动作 ----------------
p = 'class-manager-system/main.py'
s = io.open(p, encoding='utf-8').read()

s = s.replace('''        elif action == "reboot":''',
'''        elif action == "open_url":
            target = (text or "").strip()
            if not target.lower().startswith(("http://", "https://")):
                return JSONResponse({"ok": False,
                                     "error": "网址必须以 http:// 或 https:// 开头"},
                                    status_code=400)
            res = devices.agent_request(dev, "POST", "/api/browser/open", {"url": target})
        elif action == "reboot":''', 1)

io.open(p, 'w', encoding='utf-8').write(s)
print('proxy open_url added')
