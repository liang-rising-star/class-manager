/* 指针拖动：比 HTML5 drag-and-drop 稳（WebView2 / 触屏 / 拖拽被拦时都能用）
   用法：
     createPointerDrag({
         itemSelector: '.tile',
         zoneSelector: '.seat, .tray',
         canDrag: function (el) { return true; },
         onDrop: function (itemEl, zoneEl) { ... }
     });
*/
(function () {
    'use strict';

    window.createPointerDrag = function (opts) {
        opts = opts || {};
        var itemSel = opts.itemSelector;
        var zoneSel = opts.zoneSelector;
        var canDrag = opts.canDrag || function () { return true; };

        var dragging = null;      // 被拖的原元素
        var ghost = null;         // 跟随指针的副本
        var startX = 0, startY = 0;
        var offX = 0, offY = 0;   // 抓取点相对元素左上角的偏移
        var active = false;       // 是否已越过拖动阈值
        var hotZone = null;

        function zones() {
            return Array.prototype.slice.call(document.querySelectorAll(zoneSel));
        }

        function clearHot() {
            zones().forEach(function (z) { z.classList.remove('is-drop'); });
            hotZone = null;
        }

        function makeGhost(el, x, y) {
            var rect = el.getBoundingClientRect();
            offX = x - rect.left;
            offY = y - rect.top;

            var g = el.cloneNode(true);
            g.classList.add('drag-ghost');
            g.style.cssText =
                'position:fixed;left:0;top:0;margin:0;pointer-events:none;opacity:.92;' +
                'z-index:99999;width:' + rect.width + 'px;height:' + rect.height + 'px;' +
                'box-shadow:0 10px 30px rgba(15,23,42,.3);';
            document.body.appendChild(g);
            return g;
        }

        function moveGhost(x, y) {
            if (!ghost) return;
            ghost.style.left = (x - offX) + 'px';
            ghost.style.top = (y - offY) + 'px';
        }

        function zoneAt(x, y) {
            var el = document.elementFromPoint(x, y);   // ghost 是 pointer-events:none
            if (!el) return null;
            return el.closest(zoneSel);
        }

        function highlight(x, y) {
            var z = zoneAt(x, y);
            if (z === hotZone) return;
            clearHot();
            if (z) {
                z.classList.add('is-drop');
                hotZone = z;
            }
        }

        function onDown(e) {
            if (e.button !== undefined && e.button !== 0) return;

            var item = e.target.closest(itemSel);
            if (!item || !canDrag(item)) return;

            dragging = item;
            startX = e.clientX;
            startY = e.clientY;
            active = false;

            document.addEventListener('mousemove', onMove, true);
            document.addEventListener('mouseup', onUp, true);
        }

        function onMove(e) {
            if (!dragging) return;

            if (!active) {
                var dx = e.clientX - startX;
                var dy = e.clientY - startY;
                if (Math.sqrt(dx * dx + dy * dy) < 4) return;
                active = true;
                ghost = makeGhost(dragging, startX, startY);
                dragging.classList.add('is-dragging');
            }

            e.preventDefault();
            moveGhost(e.clientX, e.clientY);
            highlight(e.clientX, e.clientY);
        }

        function onUp(e) {
            document.removeEventListener('mousemove', onMove, true);
            document.removeEventListener('mouseup', onUp, true);

            var item = dragging;
            var zone = active ? zoneAt(e.clientX, e.clientY) : null;
            var wasActive = active;

            if (ghost) { ghost.remove(); ghost = null; }
            if (item) item.classList.remove('is-dragging');
            clearHot();

            dragging = null;
            active = false;

            if (wasActive && item && zone && opts.onDrop) {
                opts.onDrop(item, zone, e.clientX, e.clientY);
            }
        }

        document.addEventListener('mousedown', onDown, true);

        return {
            destroy: function () {
                document.removeEventListener('mousedown', onDown, true);
            }
        };
    };
})();
