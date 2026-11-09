/*! 自绘滑块：不依赖浏览器原生 range 渲染，任何内核表现一致 */
(function (global) {
    "use strict";

    var KNOB = 18;          // 圆钮直径
    var EDGE = KNOB / 2;    // 轨道左右留白，保证 0% / 100% 时圆钮不被裁掉

    function initSlider(input) {
        if (!input || input.dataset.sldrReady === "1") return input && input._syncSlider;
        input.dataset.sldrReady = "1";

        // 原生控件留作数据源，界面上隐藏
        input.style.position = "absolute";
        input.style.opacity = "0";
        input.style.pointerEvents = "none";
        input.style.width = "1px";
        input.style.height = "1px";

        var wrap = document.createElement("div");
        wrap.className = "sldr";
        wrap.tabIndex = 0;
        wrap.setAttribute("role", "slider");
        wrap.innerHTML = '<div class="sldr__track"></div>' +
                         '<div class="sldr__fill"></div>' +
                         '<div class="sldr__knob"></div>';
        input.parentNode.insertBefore(wrap, input.nextSibling);

        var track = wrap.querySelector(".sldr__track");
        var fill = wrap.querySelector(".sldr__fill");
        var knob = wrap.querySelector(".sldr__knob");

        // 兜底：样式表没加载到（老缓存）时，直接内联一份，保证组件一定看得见
        function ensureStyles() {
            if (track.offsetHeight > 0) return;
            wrap.style.cssText = "position:relative;height:24px;margin:6px 0 0;cursor:pointer;" +
                                 "touch-action:none;outline:none;";
            track.style.cssText = "position:absolute;left:9px;right:9px;top:50%;" +
                                  "transform:translateY(-50%);height:6px;border-radius:999px;" +
                                  "background:#e3e8f0;";
            fill.style.cssText = "position:absolute;left:9px;top:50%;transform:translateY(-50%);" +
                                 "height:6px;border-radius:999px;width:0;background:#2563eb;";
            knob.style.cssText = "position:absolute;top:50%;left:9px;width:18px;height:18px;" +
                                 "margin-left:-9px;transform:translateY(-50%);border-radius:50%;" +
                                 "background:#fff;border:2px solid #2563eb;" +
                                 "box-shadow:0 1px 4px rgba(23,43,77,.22);";
        }

        function num(v, d) {
            var n = parseFloat(v);
            return isNaN(n) ? d : n;
        }

        function min() { return num(input.min, 0); }
        function max() { return num(input.max, 100); }
        function step() { return num(input.step, 0); }

        function ratio() {
            var mn = min(), mx = max();
            if (mx <= mn) return 0;
            return Math.min(1, Math.max(0, (num(input.value, mn) - mn) / (mx - mn)));
        }

        function render() {
            var w = track.clientWidth;
            var pct = ratio();
            var x = EDGE + pct * w;
            fill.style.width = (pct * w) + "px";
            knob.style.left = x + "px";
            wrap.setAttribute("aria-valuemin", min());
            wrap.setAttribute("aria-valuemax", max());
            wrap.setAttribute("aria-valuenow", num(input.value, min()));
        }

        function commit(v) {
            var mn = min(), mx = max(), st = step();
            if (mx > mn) v = Math.min(mx, Math.max(mn, v));
            if (st > 0) {
                v = mn + Math.round((v - mn) / st) * st;
                v = Math.round(v * 1e6) / 1e6;   // 去掉浮点误差
            }
            if (String(v) !== String(input.value)) {
                input.value = v;
                input.dispatchEvent(new Event("input", { bubbles: true }));
                input.dispatchEvent(new Event("change", { bubbles: true }));
            }
            render();
        }

        function fromClientX(clientX) {
            var r = track.getBoundingClientRect();
            var t = r.width > 0 ? (clientX - r.left) / r.width : 0;
            t = Math.min(1, Math.max(0, t));
            commit(min() + t * (max() - min()));
        }

        var dragging = false;

        wrap.addEventListener("pointerdown", function (e) {
            if (e.button != null && e.button !== 0) return;
            dragging = true;
            try { wrap.setPointerCapture(e.pointerId); } catch (_) { }
            wrap.focus();
            fromClientX(e.clientX);
            e.preventDefault();
        });

        wrap.addEventListener("pointermove", function (e) {
            if (!dragging) return;
            fromClientX(e.clientX);
        });

        function endDrag(e) {
            if (!dragging) return;
            dragging = false;
            try { wrap.releasePointerCapture(e.pointerId); } catch (_) { }
        }
        wrap.addEventListener("pointerup", endDrag);
        wrap.addEventListener("pointercancel", endDrag);

        wrap.addEventListener("keydown", function (e) {
            var mn = min(), mx = max();
            var st = step() > 0 ? step() : (mx - mn) / 100;
            var cur = num(input.value, mn);
            var next = null;
            if (e.key === "ArrowRight" || e.key === "ArrowUp") next = cur + st;
            else if (e.key === "ArrowLeft" || e.key === "ArrowDown") next = cur - st;
            else if (e.key === "PageUp") next = cur + st * 10;
            else if (e.key === "PageDown") next = cur - st * 10;
            else if (e.key === "Home") next = mn;
            else if (e.key === "End") next = mx;
            if (next === null) return;
            e.preventDefault();
            commit(next);
        });

        // 容器尺寸变化（窗口缩放、布局切换）时重算像素位置
        if (global.ResizeObserver) {
            try { new ResizeObserver(render).observe(wrap); } catch (_) { }
        }

        ensureStyles();
        input._syncSlider = render;
        render();
        return render;
    }

    global.initSlider = initSlider;
})(window);
