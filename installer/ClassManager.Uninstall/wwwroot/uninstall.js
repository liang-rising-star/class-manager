(function () {
    var host = null;
    var steps = [].slice.call(document.querySelectorAll('.un-step'));

    function show(name) {
        steps.forEach(function (s) { s.classList.toggle('is-active', s.dataset.step === name); });
    }

    function callHost(method, args) {
        return new Promise(function (resolve) {
            try {
                var result = host[method].apply(host, args || []);
                if (result && typeof result.then === 'function') result.then(resolve, function () { resolve(null); });
                else resolve(result);
            } catch (e) { resolve(null); }
        });
    }

    function parse(raw) {
        if (!raw) return null;
        try { return typeof raw === 'string' ? JSON.parse(raw) : raw; } catch (e) { return null; }
    }

    if (window.chrome && window.chrome.webview) {
        host = window.chrome.webview.hostObjects.uninstaller;

        window.chrome.webview.addEventListener('message', function (e) {
            var msg = e.data || {};
            if (msg.type === 'progress') {
                document.getElementById('progressFill').style.width = msg.percent + '%';
                document.getElementById('progressPercent').textContent = msg.percent + '%';
                if (msg.text) document.getElementById('progressText').textContent = msg.text;
            } else if (msg.type === 'done') {
                var keep = document.getElementById('doneKeep');
                if (msg.keepDataDir) {
                    document.getElementById('doneSub').textContent = '程序文件已删除，成绩数据已保留';
                    keep.hidden = false;
                    keep.textContent = '数据位置：' + msg.keepDataDir;
                }
                show('done');
                // 停留 1.5 秒后自动关窗，关窗后由后台清理残留目录
                setTimeout(function () { callHost('AutoCloseSoon', [1500]); }, 200);
            } else if (msg.type === 'error') {
                document.getElementById('errorText').textContent = msg.message;
                show('error');
            }
        });

        callHost('GetInfo').then(function (raw) {
            var info = parse(raw);
            if (!info) return;
            document.getElementById('infoDir').textContent = info.dir || '—';
            document.getElementById('infoSize').textContent = info.sizeMb ? info.sizeMb + ' MB' : '—';
            document.getElementById('progressPath').textContent = info.dir || '';
        });
    }

    document.getElementById('btnUninstall').addEventListener('click', function () {
        document.getElementById('progressFill').style.width = '0%';
        document.getElementById('progressPercent').textContent = '0%';
        document.getElementById('progressText').textContent = '准备中…';
        show('progress');
        callHost('Start', [document.getElementById('optKeepData').checked]);
    });

    document.getElementById('btnCancel').addEventListener('click', function () { callHost('CloseWindow'); });
    document.getElementById('btnClose').addEventListener('click', function () { callHost('CloseWindow'); });
    document.getElementById('btnCloseError').addEventListener('click', function () { callHost('CloseWindow'); });
})();
