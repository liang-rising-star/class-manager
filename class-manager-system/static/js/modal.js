/* 站内弹窗：替代浏览器原生 confirm / alert，样式与页面其它弹窗一致 */
(function () {
    'use strict';

    var dialog = null;

    function build() {
        if (dialog) return dialog;

        dialog = document.createElement('dialog');
        dialog.className = 'modal';
        dialog.id = 'appDialog';
        dialog.innerHTML =
            '<div class="modal__body">' +
            '  <h3 class="modal__title" id="appDialogTitle"></h3>' +
            '  <p class="modal__text" id="appDialogText"></p>' +
            '  <div class="modal__actions">' +
            '    <button type="button" class="btn btn-ghost" id="appDialogCancel">取消</button>' +
            '    <button type="button" class="btn" id="appDialogOk">确定</button>' +
            '  </div>' +
            '</div>';
        document.body.appendChild(dialog);

        // 点遮罩不关闭：必须显式选择（与页面其它弹窗一致）
        dialog.addEventListener('cancel', function (e) { e.preventDefault(); });
        return dialog;
    }

    function show(opts) {
        opts = opts || {};
        var dlg = build();
        var okBtn = dlg.querySelector('#appDialogOk');
        var cancelBtn = dlg.querySelector('#appDialogCancel');

        dlg.querySelector('#appDialogTitle').textContent = opts.title || '提示';
        var textEl = dlg.querySelector('#appDialogText');
        textEl.textContent = opts.text || '';
        textEl.hidden = !opts.text;

        okBtn.textContent = opts.okText || '确定';
        cancelBtn.textContent = opts.cancelText || '取消';
        cancelBtn.hidden = opts.alertOnly === true;

        if (opts.danger) {
            okBtn.style.background = '#dc2626';
            okBtn.style.borderColor = '#dc2626';
            okBtn.style.color = '#fff';
        } else {
            okBtn.style.background = '';
            okBtn.style.borderColor = '';
            okBtn.style.color = '';
        }

        return new Promise(function (resolve) {
            function cleanup() {
                okBtn.removeEventListener('click', onOk);
                cancelBtn.removeEventListener('click', onCancel);
                dlg.removeEventListener('close', onClose);
            }
            function close(v) {
                cleanup();
                if (dlg.open) dlg.close();
                resolve(v);
            }
            function onOk() { close(true); }
            function onCancel() { close(false); }
            function onClose() { cleanup(); resolve(false); }

            okBtn.addEventListener('click', onOk);
            cancelBtn.addEventListener('click', onCancel);
            dlg.addEventListener('close', onClose);

            dlg.showModal();
            setTimeout(function () { (opts.alertOnly ? okBtn : cancelBtn).focus(); }, 30);
        });
    }

    window.appConfirm = function (opts) {
        if (typeof opts === 'string') opts = { text: opts };
        return show(opts);
    };

    window.appAlert = function (opts) {
        if (typeof opts === 'string') opts = { text: opts };
        opts = Object.assign({}, opts, { alertOnly: true, okText: opts.okText || '知道了' });
        return show(opts);
    };
})();
