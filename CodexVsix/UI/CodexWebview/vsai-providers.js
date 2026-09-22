// VSAI-owned provider editor. No changes to the frozen frontend or account login.
(function () {
    'use strict';
    if (window.__vsaiProvidersInstalled) return;
    window.__vsaiProvidersInstalled = true;

    const loginLabels = new Set(['Use API Key', '使用 API 密钥', '使用 API 金鑰', 'API キーを使用する', 'API 키 사용']);
    const loginButtons = new Map();
    const settingsSurface = document.querySelector('meta[name="vsai-settings-surface"]')?.content === 'true';
    let dialog = null;
    let fields = null;
    let returnFocus = null;
    let providers = [];
    let selectedId = null;
    let confirmDeleteId = null;
    let busy = false;
    let loaded = false;
    let pending = null;
    let message = '';
    let messageIsError = false;
    let scheduled = false;
    let requestSequence = 0;

    function element(tag, className, label) {
        const node = document.createElement(tag);
        if (className) node.className = className;
        if (label !== undefined) node.textContent = label;
        return node;
    }

    function button(label, className, action) {
        const node = element('button', className, label);
        node.type = 'button';
        node.addEventListener('click', event => {
            event.preventDefault();
            event.stopPropagation();
            action();
        });
        return node;
    }

    function installStyles() {
        const style = element('style');
        style.id = 'vsai-providers-styles';
        style.textContent = `
            /* The frozen frontend's run-location trigger; keep other footer controls visible. */
            ._footer_1s82e_2 button:has(> ._labelXs_1s82e_2.max-w-40.truncate) { display:none !important; }
            .vsai-providers-login { position:relative !important; color:transparent !important; }
            .vsai-providers-login > * { visibility:hidden; }
            .vsai-providers-login::after { content:'服务 / API Key'; position:absolute; inset:0;
                display:flex; align-items:center; justify-content:center; color:var(--color-token-foreground,var(--vscode-foreground,#eee)); }
            .vsai-providers-entry { font:inherit; color:inherit; background:transparent; border:0; border-radius:7px;
                cursor:pointer; flex-shrink:0; padding:5px 8px; white-space:nowrap; font-size:12px; }
            .vsai-providers-entry:hover { background:var(--color-token-list-hover-background,var(--vscode-list-hoverBackground,rgba(127,127,127,.14))); }
            .vsai-providers-entry:focus-visible { outline:2px solid var(--vscode-focusBorder,#4b9eed); outline-offset:2px; }
            #vsai-providers-settings-entry { display:flex; width:100%; min-height:30px; align-items:center; gap:8px;
                box-sizing:border-box; text-align:start; text-decoration:none; font-size:13px; }
            #vsai-providers-dialog { color:var(--color-token-foreground,var(--vscode-foreground,#eee));
                background:var(--color-token-main-surface-primary,var(--vscode-editor-background,#262626));
                border:1px solid var(--color-token-border,var(--vscode-widget-border,#444)); border-radius:16px;
                box-shadow:0 20px 70px #0006; width:min(720px,calc(100vw - 24px)); max-width:none;
                max-height:calc(100dvh - 24px); padding:0; margin:auto; font:13px/1.5 'Segoe UI','Microsoft YaHei',sans-serif; }
            #vsai-providers-dialog::backdrop { background:rgba(0,0,0,.48); backdrop-filter:blur(3px); }
            #vsai-providers-dialog * { box-sizing:border-box; }
            #vsai-providers-dialog .vp-header { display:flex; align-items:flex-start; gap:12px; padding:20px 22px 16px;
                border-bottom:1px solid var(--color-token-border,var(--vscode-widget-border,#444)); }
            #vsai-providers-dialog h1 { margin:0; font-size:17px; line-height:1.4; font-weight:600; }
            #vsai-providers-dialog .vp-subtitle { margin:5px 0 0; color:var(--color-token-text-secondary,var(--vscode-descriptionForeground,#aaa)); font-size:12px; }
            #vsai-providers-dialog .vp-header-text { min-width:0; flex:1; }
            #vsai-providers-dialog button { font:inherit; color:inherit; border:1px solid var(--color-token-border,var(--vscode-widget-border,#444));
                border-radius:7px; background:transparent; cursor:pointer; min-height:30px; padding:5px 10px; }
            #vsai-providers-dialog button:hover { background:var(--color-token-list-hover-background,var(--vscode-list-hoverBackground,rgba(127,127,127,.14))); }
            #vsai-providers-dialog button:disabled { opacity:.45; cursor:default; }
            #vsai-providers-dialog :focus-visible { outline:2px solid var(--vscode-focusBorder,#4b9eed); outline-offset:2px; }
            #vsai-providers-dialog .vp-close { border:0; padding:2px 7px; min-height:28px; font-size:20px; line-height:1; flex-shrink:0; }
            #vsai-providers-dialog .vp-body { display:grid; grid-template-columns:210px minmax(0,1fr); overflow-y:auto;
                max-height:calc(100dvh - 145px); min-height:0; }
            #vsai-providers-dialog .vp-sidebar { min-width:0; padding:18px 14px; border-right:1px solid var(--color-token-border,var(--vscode-widget-border,#444)); }
            #vsai-providers-dialog .vp-list-title { display:flex; align-items:center; justify-content:space-between; gap:8px; margin:0 0 12px; }
            #vsai-providers-dialog .vp-list-title h2 { margin:0; font-size:12px; font-weight:500; color:var(--color-token-text-secondary,var(--vscode-descriptionForeground,#aaa)); }
            #vsai-providers-dialog .vp-new { font-size:12px; padding:3px 7px; min-height:26px; }
            #vsai-providers-dialog .vp-empty { margin:0; padding:8px 2px; font-size:12px; color:var(--color-token-text-secondary,var(--vscode-descriptionForeground,#aaa)); }
            #vsai-providers-dialog .vp-card { min-width:0; border:1px solid transparent; border-radius:9px; padding:9px; margin-bottom:7px;
                background:var(--color-token-bg-secondary,rgba(127,127,127,.06)); }
            #vsai-providers-dialog .vp-card[aria-current='true'] { border-color:var(--vscode-focusBorder,#4b9eed); }
            #vsai-providers-dialog .vp-provider-name { margin:0; font-size:13px; font-weight:600; overflow-wrap:anywhere; }
            #vsai-providers-dialog .vp-provider-info { margin:4px 0 8px; font-size:11px; color:var(--color-token-text-secondary,var(--vscode-descriptionForeground,#aaa)); overflow-wrap:anywhere; }
            #vsai-providers-dialog .vp-card-actions { display:flex; gap:6px; flex-wrap:wrap; }
            #vsai-providers-dialog .vp-card-actions button { min-height:25px; font-size:11px; padding:2px 7px; }
            #vsai-providers-dialog .vp-danger { color:var(--vscode-errorForeground,#f48771); }
            #vsai-providers-dialog .vp-confirm { margin:5px 0 9px; font-size:12px; }
            #vsai-providers-dialog form { min-width:0; margin:0; padding:18px 22px 20px; }
            #vsai-providers-dialog .vp-form-title { margin:0 0 16px; font-size:14px; font-weight:600; }
            #vsai-providers-dialog label { display:block; margin:13px 0 5px; font-size:12px; font-weight:500; }
            #vsai-providers-dialog input, #vsai-providers-dialog textarea { display:block; width:100%; min-width:0; font:inherit;
                color:inherit; background:var(--color-token-input-background,var(--vscode-input-background,#333));
                border:1px solid var(--color-token-border,var(--vscode-input-border,#555)); border-radius:7px; padding:8px 10px; }
            #vsai-providers-dialog input::placeholder, #vsai-providers-dialog textarea::placeholder { color:var(--vscode-input-placeholderForeground,#999); }
            #vsai-providers-dialog textarea { resize:vertical; min-height:94px; max-height:240px; font-family:Consolas,monospace; }
            #vsai-providers-dialog .vp-hint { margin:5px 0 0; font-size:11px; color:var(--color-token-text-secondary,var(--vscode-descriptionForeground,#aaa)); }
            #vsai-providers-dialog .vp-status { margin:12px 0 0; min-height:18px; font-size:12px; overflow-wrap:anywhere; color:var(--color-token-text-secondary,var(--vscode-descriptionForeground,#aaa)); }
            #vsai-providers-dialog .vp-status[data-error='true'] { color:var(--vscode-errorForeground,#f48771); }
            #vsai-providers-dialog .vp-footer { display:flex; justify-content:flex-end; gap:8px; margin-top:14px; }
            #vsai-providers-dialog .vp-save { background:var(--vscode-button-background,#086abf); color:var(--vscode-button-foreground,#fff); border-color:transparent; }
            #vsai-providers-dialog .vp-save:hover { background:var(--vscode-button-hoverBackground,#167bcf); }
            @media(max-width:520px) {
                #vsai-providers-dialog { width:calc(100vw - 16px); max-height:calc(100dvh - 16px); border-radius:12px; }
                #vsai-providers-dialog .vp-header { padding:16px 15px 12px; }
                #vsai-providers-dialog .vp-body { display:block; max-height:calc(100dvh - 125px); }
                #vsai-providers-dialog .vp-sidebar { padding:12px 15px; border-right:0; border-bottom:1px solid var(--color-token-border,var(--vscode-widget-border,#444)); }
                #vsai-providers-dialog .vp-list { max-height:155px; overflow-y:auto; }
                #vsai-providers-dialog form { padding:15px; }
            }
            @media(prefers-reduced-motion:reduce) { #vsai-providers-dialog::backdrop { backdrop-filter:none; } }
        `;
        (document.head || document.documentElement).appendChild(style);
    }

    function showMessage(value, isError) {
        message = value || '';
        messageIsError = isError === true;
        updateControls();
    }

    function updateControls() {
        if (!fields) return;
        fields.save.disabled = !loaded || busy || pending !== null;
        fields.save.textContent = pending?.kind === 'providers-save' ? '正在保存…' : '保存服务';
        fields.newButton.disabled = pending !== null;
        const writing = pending !== null && pending.kind !== 'providers-request';
        [fields.name, fields.url, fields.key, fields.models].forEach(node => { node.disabled = writing; });
        fields.list.querySelectorAll('button').forEach(node => {
            node.disabled = pending !== null || (busy && node.dataset.mutates === 'true');
        });
        fields.status.textContent = message || (busy ? '当前任务正在运行，完成后可以保存或删除服务。' : pending?.kind === 'providers-request' ? '正在读取服务…' : '');
        fields.status.dataset.error = String(messageIsError);
    }

    function send(type, extra) {
        if (pending) return;
        const requestId = 'vsai-provider-' + Date.now().toString(36) + '-' + (++requestSequence);
        pending = { requestId: requestId, kind: type };
        showMessage('', false);
        try {
            window.acquireVsCodeApi().postMessage(Object.assign({ type: type, requestId: requestId }, extra || {}));
        } catch (_) {
            pending = null;
            showMessage('未能连接到扩展，请关闭窗口后重试。', true);
        }
    }

    function selectProvider(profile) {
        selectedId = profile?.id || null;
        confirmDeleteId = null;
        fields.name.value = profile?.name || '';
        fields.url.value = profile?.baseUrl || '';
        fields.key.value = '';
        fields.models.value = profile?.models.join('\n') || '';
        fields.key.placeholder = profile?.hasApiKey ? '留空保留已保存的密钥' : '请输入 API Key';
        fields.title.textContent = profile ? '编辑服务' : '添加服务';
        showMessage('', false);
        renderList();
        if (dialog.open) fields.name.focus({ preventScroll: true });
    }

    function renderList() {
        if (!fields) return;
        fields.list.replaceChildren();
        if (!providers.length) fields.list.appendChild(element('p', 'vp-empty', loaded ? '还没有第三方服务。添加后可在模型列表中选择。' : '正在读取服务…'));
        providers.forEach(profile => {
            const card = element('div', 'vp-card');
            card.setAttribute('aria-current', String(selectedId === profile.id));
            card.appendChild(element('h3', 'vp-provider-name', profile.name));
            card.appendChild(element('p', 'vp-provider-info', profile.models.length + ' 个模型 · ' + (profile.hasApiKey ? '密钥已保存' : '未保存密钥')));
            const actions = element('div', 'vp-card-actions');
            if (confirmDeleteId === profile.id) {
                card.appendChild(element('p', 'vp-confirm', '删除此服务及其保存的密钥？'));
                const confirm = button('确认删除', 'vp-danger', () => {
                    if (!busy && !pending) send('providers-delete', { id: profile.id });
                });
                confirm.dataset.mutates = 'true';
                actions.append(confirm, button('取消', '', () => { confirmDeleteId = null; renderList(); }));
            } else {
                actions.appendChild(button('编辑', '', () => selectProvider(profile)));
                const remove = button('删除', 'vp-danger', () => {
                    if (busy || pending) return;
                    confirmDeleteId = profile.id;
                    renderList();
                    fields.list.querySelector('.vp-confirm + .vp-card-actions button')?.focus();
                });
                remove.dataset.mutates = 'true';
                actions.appendChild(remove);
            }
            card.appendChild(actions);
            fields.list.appendChild(card);
        });
        updateControls();
    }

    function save(event) {
        event.preventDefault();
        if (!loaded || busy || pending) return;
        const name = fields.name.value.trim();
        const baseUrl = fields.url.value.trim();
        const models = Array.from(new Set(fields.models.value.split(/\r?\n/).map(value => value.trim()).filter(Boolean)));
        if (!name) { showMessage('请填写服务名称。', true); fields.name.focus(); return; }
        let url;
        try { url = new URL(baseUrl); } catch (_) { /* The field error below explains the expected format. */ }
        if (!url || !['http:', 'https:'].includes(url.protocol) || url.username || url.password || url.hash || url.search || baseUrl.includes('?') || baseUrl.includes('#')) {
            showMessage('请填写有效的 HTTP 或 HTTPS Base URL，地址中不要包含账号、密码、查询参数或片段。', true);
            fields.url.focus();
            return;
        }
        if (!models.length) { showMessage('请至少填写一个模型 ID。', true); fields.models.focus(); return; }
        const existing = providers.find(profile => profile.id === selectedId);
        if (!fields.key.value.trim() && !existing?.hasApiKey) {
            showMessage('请填写该服务的 API Key。', true);
            fields.key.focus();
            return;
        }
        const profile = { name: name, baseUrl: baseUrl, apiKey: fields.key.value.trim(), models: models };
        if (selectedId) profile.id = selectedId;
        // Clear the editor before crossing the bridge; never retain the key in UI state.
        fields.key.value = '';
        send('providers-save', { provider: profile });
        profile.apiKey = '';
    }

    function addField(form, id, label, tag, type, placeholder) {
        const caption = element('label', '', label);
        caption.htmlFor = id;
        const input = element(tag);
        input.id = id;
        if (type) input.type = type;
        input.autocomplete = type === 'password' ? 'new-password' : 'off';
        input.spellcheck = false;
        if (placeholder) input.placeholder = placeholder;
        form.append(caption, input);
        return input;
    }

    function close() {
        if (!dialog?.open) return;
        fields.key.value = '';
        dialog.close();
        confirmDeleteId = null;
        if (returnFocus?.isConnected) returnFocus.focus({ preventScroll: true });
        returnFocus = null;
    }

    function createDialog() {
        dialog = element('dialog');
        dialog.id = 'vsai-providers-dialog';
        dialog.setAttribute('aria-labelledby', 'vsai-providers-title');
        dialog.setAttribute('aria-describedby', 'vsai-providers-description');
        const header = element('div', 'vp-header');
        const headerText = element('div', 'vp-header-text');
        const heading = element('h1', '', '模型与服务');
        heading.id = 'vsai-providers-title';
        const subtitle = element('p', 'vp-subtitle', '添加兼容 Responses API 的第三方服务，与官方模型一起选择。');
        subtitle.id = 'vsai-providers-description';
        headerText.append(heading, subtitle);
        const dismiss = button('×', 'vp-close', close);
        dismiss.setAttribute('aria-label', '关闭模型与服务');
        header.append(headerText, dismiss);
        const body = element('div', 'vp-body');
        const sidebar = element('aside', 'vp-sidebar');
        const listTitle = element('div', 'vp-list-title');
        const newButton = button('＋ 添加', 'vp-new', () => selectProvider(null));
        newButton.setAttribute('aria-label', '添加服务');
        listTitle.append(element('h2', '', '已保存的服务'), newButton);
        const list = element('div', 'vp-list');
        sidebar.append(listTitle, list);
        const form = element('form');
        form.autocomplete = 'off';
        form.noValidate = true;
        const title = element('h2', 'vp-form-title', '添加服务');
        form.appendChild(title);
        const name = addField(form, 'vsai-provider-name', '服务名称', 'input', 'text', '例如：我的模型服务');
        const url = addField(form, 'vsai-provider-url', 'Base URL', 'input', 'url', 'https://api.example.com/v1');
        form.appendChild(element('p', 'vp-hint', '填写服务商提供的 API 基础地址，需要支持 Responses API。'));
        const key = addField(form, 'vsai-provider-key', 'API Key', 'input', 'password', '请输入 API Key');
        form.appendChild(element('p', 'vp-hint', '密钥仅用于该服务。编辑时留空，保留已保存的密钥。'));
        const models = addField(form, 'vsai-provider-models', '模型 ID', 'textarea', null, '每行一个模型 ID');
        models.rows = 3;
        form.appendChild(element('p', 'vp-hint', '使用服务商提供的准确模型 ID，每行一个。'));
        const status = element('p', 'vp-status');
        status.setAttribute('role', 'status');
        status.setAttribute('aria-live', 'polite');
        const footer = element('div', 'vp-footer');
        const saveButton = element('button', 'vp-save', '保存服务');
        saveButton.type = 'submit';
        footer.append(button('关闭', '', close), saveButton);
        form.append(status, footer);
        form.addEventListener('submit', save);
        body.append(sidebar, form);
        dialog.append(header, body);
        fields = { list: list, newButton: newButton, name: name, url: url, key: key, models: models, title: title, status: status, save: saveButton };
        dialog.addEventListener('cancel', event => { event.preventDefault(); close(); });
        dialog.addEventListener('keydown', event => {
            if (event.key !== 'Tab') return;
            const focusable = Array.from(dialog.querySelectorAll('button,input,textarea,[tabindex]')).filter(node => !node.disabled && node.tabIndex >= 0 && node.getClientRects().length);
            const first = focusable[0];
            const last = focusable[focusable.length - 1];
            if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last?.focus(); }
            else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first?.focus(); }
        });
        document.body.appendChild(dialog);
    }

    function open(source) {
        if (!document.body) return;
        if (!dialog) createDialog();
        if (dialog.open) return;
        returnFocus = source || document.activeElement;
        dialog.showModal();
        selectProvider(null);
        // An in-flight save can outlive a closed dialog. Keep its request identity.
        if (!pending) send('providers-request');
    }

    function loginRoot(node) {
        const root = node.closest('div.fixed.inset-0.overflow-hidden.bg-token-side-bar-background');
        if (!root || root.querySelector('[data-codex-intelligence-trigger]') || root.querySelector('h1')?.textContent.trim() !== 'Codex') return null;
        const column = Array.from(root.querySelectorAll('div')).find(candidate => candidate.classList.contains('max-w-[360px]'));
        const options = node.closest('div.mx-auto.inline-flex.w-max.flex-col.items-stretch');
        return column?.contains(node) && options && column.contains(options) ? root : null;
    }

    function mount() {
        scheduled = false;
        for (const [node, originalAria] of loginButtons) {
            if (!node.isConnected || !loginRoot(node) || !loginLabels.has(node.textContent.trim())) {
                node.classList.remove('vsai-providers-login');
                if (originalAria === null) node.removeAttribute('aria-label');
                else node.setAttribute('aria-label', originalAria);
                loginButtons.delete(node);
            }
        }
        document.querySelectorAll('div.fixed.inset-0.overflow-hidden.bg-token-side-bar-background button').forEach(node => {
            if (loginButtons.has(node) || !loginLabels.has(node.textContent.trim()) || !loginRoot(node)) return;
            loginButtons.set(node, node.getAttribute('aria-label'));
            node.classList.add('vsai-providers-login');
            node.setAttribute('aria-label', '服务 / API Key');
        });
        const nav = document.querySelector('[data-settings-panel-slug]')?.closest('nav')
            || (settingsSurface ? document.querySelector('.app-shell-left-panel nav') : null);
        if (nav && !nav.querySelector('#vsai-providers-settings-entry')) {
            const list = nav.querySelector('.overflow-y-auto');
            if (list) {
                // A role=button link avoids the project-settings adapter's native-button navigation capture.
                const entry = element('a', 'vsai-providers-entry', '◇  模型与服务');
                entry.id = 'vsai-providers-settings-entry';
                entry.setAttribute('role', 'button');
                entry.setAttribute('aria-haspopup', 'dialog');
                entry.tabIndex = 0;
                entry.addEventListener('click', event => { event.preventDefault(); event.stopPropagation(); open(entry); });
                entry.addEventListener('keydown', event => {
                    if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); event.stopPropagation(); open(entry); }
                });
                list.appendChild(entry);
            }
        }
    }

    document.addEventListener('click', event => {
        const target = event.target instanceof Element ? event.target.closest('button.vsai-providers-login') : null;
        if (!target || !loginButtons.has(target) || !loginRoot(target)) return;
        event.preventDefault();
        event.stopImmediatePropagation();
        open(target);
    }, true);

    window.addEventListener('codex-host-message', event => {
        const state = event.detail;
        if (state?.type !== 'providers-state') return;
        const completed = pending && state.requestId === pending.requestId ? pending : null;
        // Ignore stale request replies, but accept request-less busy/config broadcasts.
        if (state.requestId && !completed) return;
        if (Array.isArray(state.providers)) {
            providers = state.providers.filter(profile => profile && typeof profile.id === 'string').map(profile => ({
                id: profile.id, name: String(profile.name || ''), baseUrl: String(profile.baseUrl || ''),
                models: Array.isArray(profile.models) ? profile.models.filter(value => typeof value === 'string') : [],
                hasApiKey: profile.hasApiKey === true
            }));
            loaded = true;
        }
        busy = state.isBusy === true;
        if (completed) pending = null;
        if (fields && completed && completed.kind !== 'providers-request') fields.key.value = '';
        if (completed && !state.error && (state.saved || completed.kind === 'providers-delete')) {
            if (fields) selectProvider(null);
            message = completed.kind === 'providers-delete' ? '服务已删除。' : '服务已保存，可在聊天的模型列表中选择。';
            messageIsError = false;
        } else if (state.error) {
            message = String(state.error);
            messageIsError = true;
        } else if (completed) {
            message = '';
            messageIsError = false;
        }
        renderList();
        updateControls();
    });

    installStyles();
    const observer = new MutationObserver(() => {
        if (scheduled) return;
        scheduled = true;
        window.requestAnimationFrame(mount);
    });
    observer.observe(document.documentElement, { childList: true, subtree: true });
    mount();
})();
