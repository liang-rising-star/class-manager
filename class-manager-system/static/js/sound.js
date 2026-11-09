// 核销提示音引擎：成功 / 失败两组音效
// 预设名由服务端配置注入 window.SOUND_SETTINGS = { ok: 'chime', fail: 'triple' }
// 「自定义音频」的文件保存在本机浏览器（localStorage），不随配置同步
(function (global) {
    var audioCtx = null;

    var SOUNDS = {
        ok: {
            bell:       { name: '风铃',        notes: [[1046.5, 0, 0.42], [1568, 0.04, 0.5]], type: 'sine', gain: 0.09 },
            blip:       { name: '扫码枪「嘀」', notes: [[2400, 0, 0.06]], type: 'square', gain: 0.06 },
            chime:      { name: '双音上行（默认）', notes: [[1318.5, 0, 0.16], [1760, 0.13, 0.26]], type: 'sine', gain: 0.09 },
            coin:       { name: '金币',        notes: [[1568, 0, 0.07], [2093, 0.08, 0.22]], type: 'square', gain: 0.05 },
            bubble:     { name: '气泡',        notes: [[620, 0, 0.18, 1240]], type: 'sine', gain: 0.09 },
            xylophone:  { name: '木琴',        notes: [[880, 0, 0.2], [1320, 0.02, 0.24]], type: 'triangle', gain: 0.1 },
            drop:       { name: '水滴',        notes: [[1200, 0, 0.12, 620]], type: 'sine', gain: 0.1 },
            tick:       { name: '电子「嘀」',   notes: [[1800, 0, 0.05]], type: 'sine', gain: 0.07 },
            chord:      { name: '三音和弦',    notes: [[1046.5, 0, 0.5], [1318.5, 0, 0.5], [1568, 0, 0.5]], type: 'sine', gain: 0.05 },
            sweepup:    { name: '上扬扫频',    notes: [[500, 0, 0.26, 1500]], type: 'triangle', gain: 0.08 },
            typewriter: { name: '打字机',      notes: [[1200, 0, 0.03], [900, 0.05, 0.04]], type: 'square', gain: 0.07 },
            doublebell: { name: '铃双击',      notes: [[1568, 0, 0.18], [1568, 0.22, 0.3]], type: 'sine', gain: 0.08 },
            custom:     { name: '自定义音频…', custom: true }
        },
        fail: {
            soft:      { name: '轻提示',      notes: [[392, 0, 0.28, 262]], type: 'triangle', gain: 0.11 },
            buzz:      { name: '低沉蜂鸣',    notes: [[180, 0, 0.36]], type: 'square', gain: 0.1 },
            double:    { name: '两短声',      notes: [[440, 0, 0.1], [440, 0.16, 0.1]], type: 'square', gain: 0.08 },
            descend:   { name: '下滑音',      notes: [[420, 0, 0.34, 170]], type: 'sawtooth', gain: 0.09 },
            triple:    { name: '三连蜂鸣（默认）', notes: [[330, 0, 0.08], [330, 0.13, 0.08], [330, 0.26, 0.08]], type: 'square', gain: 0.08 },
            woodblock: { name: '木鱼',        notes: [[600, 0, 0.1, 300]], type: 'triangle', gain: 0.13 },
            thud:      { name: '低音鼓',      notes: [[110, 0, 0.22, 60]], type: 'sine', gain: 0.15 },
            alarm:     { name: '警报两声',    notes: [[880, 0, 0.14], [660, 0.16, 0.16]], type: 'square', gain: 0.09 },
            custom:    { name: '自定义音频…', custom: true }
        }
    };

    var server = global.SOUND_SETTINGS || {};
    var choice = {
        ok: SOUNDS.ok[server.ok] ? server.ok : 'chime',
        fail: SOUNDS.fail[server.fail] ? server.fail : 'triple'
    };

    function playNote(note, type, gainValue) {
        var freq = note[0], delay = note[1] || 0, dur = note[2] || 0.2, slideTo = note[3];
        var now = audioCtx.currentTime + delay;
        var osc = audioCtx.createOscillator();
        var gain = audioCtx.createGain();
        osc.connect(gain);
        gain.connect(audioCtx.destination);

        osc.type = type;
        osc.frequency.setValueAtTime(freq, now);
        if (slideTo) osc.frequency.exponentialRampToValueAtTime(slideTo, now + dur);

        gain.gain.setValueAtTime(0.0001, now);
        gain.gain.exponentialRampToValueAtTime(gainValue, now + 0.012);
        gain.gain.exponentialRampToValueAtTime(0.0001, now + dur);

        osc.start(now);
        osc.stop(now + dur + 0.02);
    }

    function playCustomFile(kind) {
        var dataUrl = localStorage.getItem('class-manager:sound2-' + kind + '-file');
        if (!dataUrl) return false;
        try {
            var audio = new Audio(dataUrl);
            audio.volume = 0.9;
            audio.play();
            return true;
        } catch (e) {
            return false;
        }
    }

    function play(kind) {
        var group = kind === 'bad' ? 'fail' : 'ok';
        var preset = SOUNDS[group][choice[group]] || SOUNDS[group][Object.keys(SOUNDS[group])[0]];

        if (preset.custom) {
            if (playCustomFile(group)) return;
            preset = SOUNDS[group][group === 'fail' ? 'triple' : 'chime'];
        }

        try {
            audioCtx = audioCtx || new (window.AudioContext || window.webkitAudioContext)();
            if (audioCtx.state === 'suspended') audioCtx.resume();
            preset.notes.forEach(function (note) { playNote(note, preset.type, preset.gain); });
        } catch (e) { /* 静默失败 */ }
    }

    function pick(key) {
        return SOUNDS[key];
    }

    // 设置页：把两组音效填进下拉框，并支持试听 / 导入自定义音频
    function mountPicker(options) {
        var okSelect = document.getElementById(options.okSelect);
        var failSelect = document.getElementById(options.failSelect);
        var fileInput = document.getElementById(options.fileInput);
        var pendingKind = null;

        function fill(select, group, kind) {
            if (!select) return;

            Object.keys(SOUNDS[group]).forEach(function (key) {
                var opt = document.createElement('option');
                opt.value = key;
                opt.textContent = SOUNDS[group][key].name;
                select.appendChild(opt);
            });

            select.value = choice[kind];

            select.addEventListener('change', function () {
                if (select.value === 'custom') {
                    pendingKind = kind;
                    if (!fileInput) return;
                    fileInput.value = '';
                    fileInput.click();
                    return;
                }
                choice[kind] = select.value;
                play(kind === 'fail' ? 'bad' : 'ok');
            });
        }

        if (fileInput) {
            fileInput.addEventListener('change', function () {
                var file = fileInput.files && fileInput.files[0];
                if (!file || !pendingKind) return;

                if (file.size > 400 * 1024) {
                    alert('音频文件不要超过 400KB，否则无法记住');
                    pendingKind = null;
                    return;
                }

                var reader = new FileReader();
                reader.onload = function () {
                    try {
                        localStorage.setItem('class-manager:sound2-' + pendingKind + '-file', reader.result);
                        choice[pendingKind] = 'custom';
                        play(pendingKind === 'fail' ? 'bad' : 'ok');
                    } catch (e) {
                        alert('这个音频太大，浏览器存不下，换一个短一点的');
                    }
                    pendingKind = null;
                };
                reader.readAsDataURL(file);
            });
        }

        fill(okSelect, 'ok', 'ok');
        fill(failSelect, 'fail', 'fail');

        [].forEach.call(document.querySelectorAll('.sound-play'), function (btn) {
            btn.addEventListener('click', function (e) {
                e.preventDefault();
                play(btn.dataset.kind === 'fail' ? 'bad' : 'ok');
            });
        });
    }

    global.SoundKit = {
        presets: SOUNDS,
        play: play,
        groups: pick,
        current: choice,
        mountPicker: mountPicker,
        hasCustomFile: function (kind) { return !!localStorage.getItem('class-manager:sound2-' + kind + '-file'); },
        saveCustomFile: function (kind, dataUrl) {
            localStorage.setItem('class-manager:sound2-' + kind + '-file', dataUrl);
        }
    };
})(window);
