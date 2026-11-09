/* 一体机端 · 设置界面 */
(function () {
    "use strict";

    const $ = (id) => document.getElementById(id);
    let state = null;
    let pollTimer = null;
    let levelTimer = null;
    let previewTimer = null;

    // ---------- 通用 ----------

    function toast(message, isError) {
        const el = $("toast");
        el.textContent = message;
        el.className = "toast show" + (isError ? " err" : "");
        setTimeout(() => { el.className = "toast"; }, 2600);
    }

    async function api(path, options) {
        const opts = Object.assign({ credentials: "same-origin", headers: {} }, options || {});
        if (opts.body && typeof opts.body !== "string") {
            opts.body = JSON.stringify(opts.body);
            opts.headers["Content-Type"] = "application/json";
        }
        const res = await fetch(path, opts);
        let data = null;
        try { data = await res.json(); } catch (e) { data = null; }
        if (res.status === 401) {
            showLogin();
            throw new Error("未授权");
        }
        return { status: res.status, data: data };
    }

    // ---------- 视图切换 ----------

    function hideAll() {
        ["setupView", "loginView", "settingsView"].forEach((id) => { $(id).hidden = true; });
    }

    function showSetup() {
        hideAll();
        $("setupView").hidden = false;
        $("setupPwd").focus();
    }

    function showLogin() {
        hideAll();
        $("loginView").hidden = false;
        $("logoutBtn").hidden = true;
        stopTimers();
        $("loginPwd").focus();
    }

    function showSettings() {
        hideAll();
        $("settingsView").hidden = false;
        $("logoutBtn").hidden = false;
        startTimers();
    }

    // ---------- 初始化 ----------

    async function boot() {
        try {
            const info = await (await fetch("/api/info")).json();
            $("deviceName").textContent = info.room ? `${info.room}・${info.device}` : info.device;
            if (info.needSetup) { showSetup(); return; }

            const res = await api("/api/state");
            if (res.status === 200) { state = res.data; applyState(); showSettings(); }
            else showLogin();
        } catch (e) {
            showLogin();
        }
    }

    // ---------- 登录 / 设置密码 ----------

    $("setupBtn").addEventListener("click", async () => {
        const p1 = $("setupPwd").value.trim();
        const p2 = $("setupPwd2").value.trim();
        $("setupError").textContent = "";
        if (p1.length < 4) { $("setupError").textContent = "密码至少 4 位"; return; }
        if (p1 !== p2) { $("setupError").textContent = "两次输入的密码不一致"; return; }

        const res = await api("/api/setup", { method: "POST", body: { password: p1 } });
        if (!res.data || !res.data.ok) {
            $("setupError").textContent = (res.data && res.data.error) || "设置失败";
            return;
        }
        await api("/api/login", { method: "POST", body: { password: p1 } });
        toast("密码已设置");
        await boot();
    });

    $("loginBtn").addEventListener("click", doLogin);
    $("loginPwd").addEventListener("keydown", (e) => { if (e.key === "Enter") doLogin(); });

    async function doLogin() {
        const pwd = $("loginPwd").value;
        $("loginError").textContent = "";
        const res = await api("/api/login", { method: "POST", body: { password: pwd } });
        if (!res.data || !res.data.ok) {
            $("loginError").textContent = (res.data && res.data.error) || "登录失败";
            return;
        }
        $("loginPwd").value = "";
        await boot();
    }

    $("logoutBtn").addEventListener("click", async () => {
        await api("/api/logout", { method: "POST", body: {} });
        showLogin();
    });

    // ---------- 状态渲染 ----------

    function applyState() {
        if (!state) return;
        $("infoRoom").textContent = state.room || "未设置";
        $("infoDevice").textContent = state.device || "—";
        $("infoLan").textContent = state.lanUrl;
        $("infoLocal").textContent = state.localUrl;
        $("infoKey").textContent = state.accessKey;
        $("roomName").value = state.room || "";
        $("deviceNameInput").value = state.device || "";

        $("cameraEnabled").checked = !!state.camera.enabled;
        setResolution(state.camera.width, state.camera.height);
        $("cameraFps").value = state.camera.fps;
        $("cameraQuality").value = state.camera.quality;

        $("micEnabled").checked = !!state.mic.enabled;
        $("micGain").value = state.mic.gain;
        $("gainValue").textContent = Number(state.mic.gain).toFixed(1);
        if (typeof paintRange === "function") paintRange($("micGain"));

        $("autostart").checked = !!state.autostart;

        $("remoteEnabled").checked = !!state.remote.enabled;
        $("remoteTimeout").value = state.remote.timeoutSeconds;
        $("remoteAllowed").value = (state.remote.allowedCommands || []).join("\n");

        updateCameraState();
        setSelectValue("cameraSelect", state.camera.device);
        setSelectValue("micSelect", state.mic.device);
    }

    var RESOLUTIONS = [
        [640, 360], [1280, 720], [1920, 1080], [2560, 1440], [3840, 2160]
    ];

    function setResolution(width, height) {
        var sel = $("cameraResolution");
        var key = width + "x" + height;
        for (var i = 0; i < sel.options.length; i++) {
            if (sel.options[i].value === key) { sel.value = key; return; }
        }
        // 不在预设里就按面积最接近的（默认 720P）
        var best = "1280x720", bestDiff = Infinity;
        RESOLUTIONS.forEach(function (r) {
            var diff = Math.abs(r[0] * r[1] - width * height);
            if (diff < bestDiff) { bestDiff = diff; best = r[0] + "x" + r[1]; }
        });
        sel.value = best;
    }

    function currentResolution() {
        var parts = ($("cameraResolution").value || "1280x720").split("x");
        return { width: parseInt(parts[0], 10) || 1280, height: parseInt(parts[1], 10) || 720 };
    }

    function setSelectValue(id, value) {
        const sel = $(id);
        if (!value) return;
        for (const opt of sel.options) {
            if (opt.value === value) { sel.value = value; return; }
        }
    }

    function updateCameraState() {
        const cam = state ? state.camera : null;
        const badge = $("cameraState");
        const box = $("previewBox");

        if (!cam || !cam.enabled) {
            badge.textContent = "未启用";
            box.innerHTML = '<span>摄像头未启用</span>';
            return;
        }
        if (!cam.running) {
            badge.textContent = cam.error ? "异常" : "启动中";
            box.innerHTML = '<span>' + (cam.error || "正在启动摄像头…") + "</span>";
            return;
        }
        badge.textContent = (cam.actualFps != null ? cam.actualFps : cam.fps) + " fps";
        if (!box.querySelector("img")) {
            box.innerHTML = "";
            const img = document.createElement("img");
            img.alt = "实时画面";
            img.src = "/api/camera/stream?t=" + Date.now();
            img.onerror = () => {
                box.innerHTML = '<span>画面获取失败，稍后自动重试</span>';
                setTimeout(() => { if (state && state.camera.enabled) updateCameraState(); }, 3000);
            };
            box.appendChild(img);
        }
    }

    // ---------- 设备列表 ----------

    async function loadDevices() {
        try {
            const cams = await api("/api/camera/devices");
            const sel = $("cameraSelect");
            sel.innerHTML = "";
            (cams.data.devices || []).forEach((d) => {
                const name = d.name || d.Name || "";
                const o = document.createElement("option");
                o.value = name;
                o.textContent = name;
                sel.appendChild(o);
            });
            if (!cams.data.devices || cams.data.devices.length === 0) {
                const o = document.createElement("option");
                o.value = "";
                o.textContent = "（未检测到摄像头）";
                sel.appendChild(o);
            }
            if (state) setSelectValue("cameraSelect", state.camera.device);
        } catch (e) { /* 忽略 */ }

        try {
            const mics = await api("/api/mic/devices");
            const sel = $("micSelect");
            sel.innerHTML = "";
            (mics.data.devices || []).forEach((d) => {
                const name = d.name || d.Name || "";
                const o = document.createElement("option");
                o.value = name;
                o.textContent = name;
                sel.appendChild(o);
            });
            if (!mics.data.devices || mics.data.devices.length === 0) {
                const o = document.createElement("option");
                o.value = "";
                o.textContent = "（未检测到麦克风）";
                sel.appendChild(o);
            }
            if (state) setSelectValue("micSelect", state.mic.device);
        } catch (e) { /* 忽略 */ }
    }

    // ---------- 定时刷新 ----------

    function startTimers() {
        loadDevices();
        stopTimers();
        pollTimer = setInterval(refreshState, 2500);
        levelTimer = setInterval(refreshLevel, 600);
    }

    function stopTimers() {
        if (pollTimer) clearInterval(pollTimer);
        if (levelTimer) clearInterval(levelTimer);
        if (previewTimer) clearTimeout(previewTimer);
        pollTimer = levelTimer = previewTimer = null;
    }

    async function refreshState() {
        try {
            const res = await api("/api/state");
            if (res.status !== 200) return;
            const prevRunning = state && state.camera.running;
            state = res.data;
            $("deviceName").textContent = state.room ? `${state.room}・${state.device}` : state.device;
            updateCameraState();
            if (prevRunning !== state.camera.running) applyState();
        } catch (e) { /* 未授权时已跳转 */ }
    }

    async function refreshLevel() {
        try {
            const res = await api("/api/mic/level");
            if (res.status !== 200 || !res.data) return;
            const pct = Math.round((res.data.level || 0) * 100);
            $("micMeter").style.width = Math.min(100, pct) + "%";
        } catch (e) { /* 忽略 */ }
    }

    // ---------- 保存操作 ----------

    $("autostart").addEventListener("change", async function () {
        const res = await api("/api/autostart", {
            method: "POST",
            body: { enabled: this.checked }
        });
        if (res.data && res.data.ok) {
            toast(res.data.enabled ? "已开启开机自启" : "已关闭开机自启");
        } else {
            toast("设置失败，请检查系统权限", true);
            this.checked = !this.checked;
        }
    });

    $("genCodeBtn").addEventListener("click", async () => {
        const res = await api("/api/config-code");
        if (res.data && res.data.ok) {
            $("cfgCode").value = res.data.code;
            $("cfgCode").focus();
            $("cfgCode").select();
            toast("配置码已生成，已全选可直接复制");
        } else {
            toast("生成失败", true);
        }
    });

    $("copyCodeBtn").addEventListener("click", async () => {
        const code = ($("cfgCode").value || "").trim();
        if (!code) { toast("先点「生成配置码」", true); return; }
        try {
            await navigator.clipboard.writeText(code);
            toast("配置码已复制");
        } catch (e) {
            // WebView2 里剪贴板 API 偶尔被拒：退回到选中文本，让用户 Ctrl+C
            $("cfgCode").focus();
            $("cfgCode").select();
            try { document.execCommand("copy"); toast("配置码已复制"); }
            catch (_) { toast("已选中，请按 Ctrl+C 复制", true); }
        }
    });

    $("saveBaseBtn").addEventListener("click", async () => {
        const res = await api("/api/settings", {
            method: "POST",
            body: { roomName: $("roomName").value.trim(), deviceName: $("deviceNameInput").value.trim() }
        });
        if (res.data && res.data.ok) { toast("已保存"); refreshState(); }
        else toast("保存失败", true);
    });

    $("saveCameraBtn").addEventListener("click", async () => {
        const res = await api("/api/settings", {
            method: "POST",
            body: {
                camera: {
                    enabled: $("cameraEnabled").checked,
                    device: $("cameraSelect").value,
                    width: currentResolution().width,
                    height: currentResolution().height,
                    fps: parseInt($("cameraFps").value, 10) || 30,
                    quality: parseInt($("cameraQuality").value, 10) || 70
                }
            }
        });
        if (res.data && res.data.ok) { toast("摄像头设置已应用"); await refreshState(); applyState(); }
        else toast("应用失败", true);
    });

    $("stopCameraBtn").addEventListener("click", async () => {
        const res = await api("/api/camera/stop", { method: "POST", body: {} });
        if (res.data && res.data.ok) { toast("已停止"); await refreshState(); applyState(); }
    });

    $("saveMicBtn").addEventListener("click", async () => {
        const res = await api("/api/settings", {
            method: "POST",
            body: {
                mic: {
                    enabled: $("micEnabled").checked,
                    device: $("micSelect").value,
                    gain: parseFloat($("micGain").value) || 1
                }
            }
        });
        if (res.data && res.data.ok) { toast("麦克风设置已保存"); await refreshState(); }
        else toast("保存失败", true);
    });

    function paintRange(el) {
        if (!el) return;
        if (el._syncSlider) el._syncSlider();
    }

    if (window.initSlider) initSlider($("micGain"));

    $("micGain").addEventListener("input", () => {
        $("gainValue").textContent = Number($("micGain").value).toFixed(1);
        paintRange($("micGain"));
    });
    paintRange($("micGain"));

    $("saveRemoteBtn").addEventListener("click", async () => {
        const allowed = $("remoteAllowed").value.split("\n").map((s) => s.trim()).filter(Boolean);
        const res = await api("/api/settings", {
            method: "POST",
            body: {
                remote: {
                    enabled: $("remoteEnabled").checked,
                    timeoutSeconds: parseInt($("remoteTimeout").value, 10) || 15,
                    allowedCommands: allowed
                }
            }
        });
        if (res.data && res.data.ok) { toast("远程执行设置已保存"); await refreshState(); }
        else toast("保存失败", true);
    });

    $("savePwdBtn").addEventListener("click", async () => {
        const res = await api("/api/password", {
            method: "POST",
            body: { oldPassword: $("oldPwd").value, newPassword: $("newPwd").value }
        });
        if (res.data && res.data.ok) {
            toast("密码已修改，请重新登录");
            $("oldPwd").value = "";
            $("newPwd").value = "";
            showLogin();
        } else {
            toast((res.data && res.data.error) || "修改失败", true);
        }
    });

    $("rotateKeyBtn").addEventListener("click", async () => {
        if (!confirm("重置后旧密钥立即失效，教师端需要重新填写。确定继续？")) return;
        const res = await api("/api/key/rotate", { method: "POST", body: {} });
        if (res.data && res.data.ok) {
            $("infoKey").textContent = res.data.accessKey;
            toast("访问密钥已重置");
            await refreshState();
        } else toast("重置失败", true);
    });

    $("copyInfoBtn").addEventListener("click", async () => {
        const text = [
            `教室：${state.room || "未设置"}`,
            `设备：${state.device}`,
            `地址：${state.lanUrl}`,
            `密钥：${state.accessKey}`
        ].join("\n");
        try {
            await navigator.clipboard.writeText(text);
            toast("对接信息已复制");
        } catch (e) {
            toast("复制失败，请手动选择", true);
        }
    });

    boot();
})();
