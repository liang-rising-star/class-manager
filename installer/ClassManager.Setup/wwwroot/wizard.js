// 安装向导交互：5 步流程 + 调用宿主对象
(function () {
    var host = null;                 // window.chrome.webview.hostObjects.installer
    var info = { app: '班级管理系统', version: '3.0', defaultPath: 'D:\\class-manager' };
    var installDir = '';
    var installing = false;
    var uninstallMode = false;

    var steps = [].slice.call(document.querySelectorAll('.step'));
    var pathInput = document.getElementById('pathInput');
    var pathHint = document.getElementById('pathHint');
    var progressFill = document.getElementById('progressFill');
    var progressPercent = document.getElementById('progressPercent');
    var progressText = document.getElementById('progressText');
    var progressPath = document.getElementById('progressPath');
    var donePath = document.getElementById('donePath');

    function show(name) {
        steps.forEach(function (s) {
            s.classList.toggle('is-active', s.dataset.step === name);
        });
    }

    // ---------- 宿主桥接 ----------
    function callHost(method, args) {
        return new Promise(function (resolve) {
            try {
                var fn = host[method];
                var result = fn.apply(host, args || []);
                // 异步代理返回 Promise，同步代理直接给值
                if (result && typeof result.then === 'function') {
                    result.then(resolve, function () { resolve(null); });
                } else {
                    resolve(result);
                }
            } catch (e) {
                resolve(null);
            }
        });
    }

    function parse(json) {
        if (!json) return null;
        try { return typeof json === 'string' ? JSON.parse(json) : json; } catch (e) { return null; }
    }

    // ---------- 路径校验 ----------
    var checkTimer = null;
    function validatePath(immediate) {
        clearTimeout(checkTimer);
        var run = function () {
            callHost('CheckPath', [pathInput.value]).then(function (raw) {
                var res = parse(raw);
                if (!res) return;
                pathHint.className = 'path-hint';
                if (res.risk === 'ok') {
                    pathHint.classList.add('is-ok');
                    pathHint.textContent = '✓ 此位置可以正常安装，数据会保存在该文件夹内。';
                } else if (res.risk === 'admin') {
                    pathHint.classList.add('is-warn');
                    pathHint.textContent = '⚠ ' + res.message;
                } else {
                    pathHint.classList.add('is-bad');
                    pathHint.textContent = '⚠ ' + res.message;
                }
            });
        };
        if (immediate) run(); else checkTimer = setTimeout(run, 220);
    }

    // ---------- 初始化 ----------
    function init() {
        if (window.chrome && window.chrome.webview) {
            host = window.chrome.webview.hostObjects.installer;

            window.chrome.webview.addEventListener('message', function (e) {
                var msg = e.data || {};
                if (msg.type === 'progress') {
                    progressFill.style.width = msg.percent + '%';
                    progressPercent.textContent = msg.percent + '%';
                    if (msg.text) progressText.textContent = msg.text;
                } else if (msg.type === 'done') {
                    installing = false;
                    installDir = msg.dir;
                    donePath.textContent = msg.dir;
                    document.getElementById('doneTitle').textContent =
                        uninstallMode ? '卸载完成' : '完成';
                    var launchRow = document.querySelector('.done__switch');
                    if (launchRow) launchRow.style.display = uninstallMode ? 'none' : '';
                    show('done');
                } else if (msg.type === 'error') {
                    installing = false;
                    document.getElementById('errorText').textContent = msg.message;
                    show('error');
                }
            });

            callHost('GetState').then(function (raw) {
                var st = parse(raw);
                if (st && st.installed) {
                    document.getElementById('maintInfo').textContent =
                        '当前版本 v' + (st.version || '?') + ' · ' + st.dir;
                    show('maintain');
                }
            });

            callHost('GetInfo').then(function (raw) {
                var data = parse(raw);
                if (!data) return;
                info = data;
                document.getElementById('appVersion').textContent = info.version;
                pathInput.value = info.defaultPath;
                validatePath(true);
            });
        } else {
            // 浏览器里直接打开时的兜底，方便调样式
            pathInput.value = info.defaultPath;
            validatePath(true);
        }
    }

    // ---------- 事件 ----------

    // ---- 页内确认框（替代 JS confirm，样式与向导一致）----
    function ask(title, text) {
        return new Promise(function (resolve) {
            var dlg = document.getElementById('confirmDialog');
            document.getElementById('confirmTitle').textContent = title;
            document.getElementById('confirmText').textContent = text;

            function cleanup() {
                document.getElementById('confirmYes').removeEventListener('click', onYes);
                document.getElementById('confirmNo').removeEventListener('click', onNo);
            }
            function onYes() { cleanup(); dlg.close(); resolve(true); }
            function onNo() { cleanup(); dlg.close(); resolve(false); }

            document.getElementById('confirmYes').addEventListener('click', onYes);
            document.getElementById('confirmNo').addEventListener('click', onNo);
            dlg.showModal();
        });
    }

    // ---- 维护模式：更新 / 修复 / 卸载 / 全新安装 ----
    document.getElementById('actUpdate').addEventListener('click', function () {
        callHost('PickZip').then(function (zip) {
            if (!zip) return;
            callHost('GetState').then(function (raw) {
                var st = parse(raw) || {};
                if (!st.dir) { alert('没有读到安装目录'); return; }
                var msg = '将用这个 zip 覆盖当前程序文件：\n' + st.dir
                        + '\n\n成绩数据不会被动到，继续吗？';
                ask('更新到新版本', msg).then(function (ok) {
                    if (!ok) return;
                    startOperation('正在更新', st.dir);
                    callHost('StartUpdate', [zip, st.dir]);
                });
            });
        });
    });

    document.getElementById('actRepair').addEventListener('click', function () {
        callHost('GetState').then(function (raw) {
            var st = parse(raw) || {};
            if (!st.dir) { alert('没有读到安装目录'); return; }
            var msg = '将用安装包自带文件覆盖：\n' + st.dir + '\n\n继续吗？';
            ask('修复 / 重新安装', msg).then(function (ok) {
                if (!ok) return;
                startOperation('正在修复', st.dir);
                callHost('StartInstall', [st.dir, false, true, true]);
            });
        });
    });

    document.getElementById('actFresh').addEventListener('click', function () {
        show('welcome');
    });

    function startOperation(title, dir) {
        uninstallMode = title === '正在卸载';
        progressPath.textContent = dir;
        progressFill.style.width = '0%';
        progressPercent.textContent = '0%';
        progressText.textContent = title + '…';
        show('progress');
    }

    document.getElementById('startBtn').addEventListener('click', function () {
        show('path');
        pathInput.focus();
        pathInput.select();
    });

    pathInput.addEventListener('input', function () { validatePath(false); });

    document.getElementById('browseBtn').addEventListener('click', function () {
        callHost('PickFolder', [pathInput.value]).then(function (picked) {
            if (picked) {
                pathInput.value = picked + (picked.slice(-1) === '\\' ? '' : '\\') + 'class-manager';
                validatePath(true);
            }
        });
    });

    document.getElementById('pathNext').addEventListener('click', function () {
        callHost('CheckPath', [pathInput.value]).then(function (raw) {
            var res = parse(raw) || { risk: 'bad', message: '请检查安装位置' };
            if (res.risk === 'bad' || res.risk === 'readonly') {
                validatePath(true);
                alert(res.message);
                return;
            }
            if (res.risk === 'admin') {
                if (!confirm(res.message + '\n\n仍然要安装到这个位置吗？')) return;
            }
            show('options');
        });
    });

    document.getElementById('installBtn').addEventListener('click', function () {
        if (installing) return;
        installing = true;

        progressPath.textContent = pathInput.value;
        progressFill.style.width = '0%';
        progressPercent.textContent = '0%';
        progressText.textContent = '准备中…';
        show('progress');

        callHost('StartInstall', [
            pathInput.value,
            document.getElementById('optAutostart').checked,
            document.getElementById('optDesktop').checked,
            document.getElementById('optStartMenu').checked
        ]);
    });

    document.getElementById('finishBtn').addEventListener('click', function () {
        if (document.getElementById('optLaunch').checked && installDir) {
            callHost('LaunchAndClose', [installDir + '\\class-manager.exe']);
        } else {
            callHost('CloseSetup');
        }
    });

    document.getElementById('closeBtn').addEventListener('click', function () {
        callHost('CloseSetup');
    });

    [].forEach.call(document.querySelectorAll('[data-back]'), function (btn) {
        btn.addEventListener('click', function () { show(btn.dataset.back); });
    });

    init();
})();
