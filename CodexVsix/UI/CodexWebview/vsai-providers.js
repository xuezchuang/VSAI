// VSAI-owned provider editor. No changes to the frozen frontend or account login.
(function () {
    'use strict';
    if (window.__vsaiProvidersInstalled) return;
    window.__vsaiProvidersInstalled = true;

    const loginLabels = new Set(['Use API Key', '使用 API 密钥', '使用 API 金鑰', 'API キーを使用する', 'API 키 사용']);
    const efforts = ['none', 'minimal', 'low', 'medium', 'high', 'xhigh', 'max', 'ultra'];
    const capabilities = ['supportsImages', 'supportsTools', 'supportsReasoning', 'supportsResponses'];
    const own = (object, key) => Object.prototype.hasOwnProperty.call(object, key);
    const loginButtons = new Map();
    const settingsSurface = document.querySelector('meta[name="vsai-settings-surface"]')?.content === 'true';
    let dialog, fields, returnFocus, selectedId = null, confirmDeleteId = null, providers = [];
    let busy = false, loaded = false, stateRequest = null, writeRequest = null, discoveryRequest = null;
    let officialAccount = { status: 'signed-out', accountType: null, error: null };
    let officialAccountRequest = null, officialLoginRequest = null, officialCancelRequest = null;
    let message = '', messageIsError = false, scheduled = false, sequence = 0, editRevision = 0;

    function el(tag, className, text) {
        const node = document.createElement(tag);
        if (className) node.className = className;
        if (text !== undefined) node.textContent = text;
        return node;
    }
    function btn(text, className, action) {
        const node = el('button', className, text); node.type = 'button';
        node.addEventListener('click', event => { event.preventDefault(); event.stopPropagation(); action(); });
        return node;
    }
    function ids(values) {
        return Array.from(new Set((Array.isArray(values) ? values : []).filter(value => typeof value === 'string').map(value => value.trim()).filter(Boolean)));
    }
    function clone(value) { return value == null ? value : JSON.parse(JSON.stringify(value)); }
    function model(value) {
        if (!value || typeof value !== 'object' || typeof value.id !== 'string' || !value.id.trim()) return null;
        const result = { id: value.id.trim() };
        if (typeof value.displayName === 'string' && value.displayName.trim()) result.displayName = value.displayName.trim();
        ['contextWindow', 'maxOutputTokens'].forEach(name => {
            if (typeof value[name] === 'number' && Number.isSafeInteger(value[name]) && value[name] >= 0) result[name] = value[name];
            else if (typeof value[name] === 'string' && /^\d+$/.test(value[name])) result[name] = value[name];
        });
        capabilities.forEach(name => { if (own(value, name)) result[name] = value[name] === true || value[name] === false ? value[name] : null; });
        if (own(value, 'reasoningEfforts')) result.reasoningEfforts = Array.isArray(value.reasoningEfforts) ? value.reasoningEfforts.filter(value => efforts.includes(value)).filter((value, index, list) => list.indexOf(value) === index) : null;
        if (own(value, 'defaultReasoningEffort') && efforts.includes(value.defaultReasoningEffort)) result.defaultReasoningEffort = value.defaultReasoningEffort;
        return result;
    }
    function catalog(value, legacyModels) {
        value = value && typeof value === 'object' ? value : {};
        const overrides = Object.create(null);
        if (value.overrides && typeof value.overrides === 'object' && !Array.isArray(value.overrides)) Object.keys(value.overrides).forEach(id => {
            const item = model(Object.assign({}, value.overrides[id], { id: id }));
            if (item) overrides[id] = item;
        });
        return {
            autoSync: value.autoSync === true,
            source: ['auto', 'openai', 'midas'].includes(value.source) ? value.source : 'auto',
            manualModels: ids(Array.isArray(value.manualModels) ? value.manualModels : legacyModels),
            discoveredModels: (Array.isArray(value.discoveredModels) ? value.discoveredModels : []).map(model).filter(Boolean),
            overrides: overrides, hiddenModels: ids(value.hiddenModels),
            lastSuccessUtc: typeof value.lastSuccessUtc === 'string' ? value.lastSuccessUtc : null,
            lastAttemptUtc: typeof value.lastAttemptUtc === 'string' ? value.lastAttemptUtc : null,
            status: typeof value.status === 'string' ? value.status : null,
            error: typeof value.error === 'string' ? value.error : null
        };
    }
    function profile(value) {
        if (!value || typeof value.id !== 'string') return null;
        const legacyModels = ids(value.models);
        return { id: value.id, name: String(value.name || ''), baseUrl: String(value.baseUrl || ''), models: legacyModels, hasApiKey: value.hasApiKey === true, catalog: catalog(value.catalog, legacyModels), runtimeWarnings: ids(value.runtimeWarnings) };
    }
    function allModels(value) {
        const discovered = new Map(value.discoveredModels.map(item => [item.id, item]));
        return ids(value.manualModels.concat(value.discoveredModels.map(item => item.id))).map(id => {
            const merged = Object.assign({ id: id }, discovered.get(id) || {});
            const override = own(value.overrides, id) ? value.overrides[id] : null;
            if (override) Object.keys(override).forEach(name => {
                if (name === 'id' || override[name] === null) return;
                merged[name] = override[name];
            });
            if (merged.supportsReasoning === false) {
                delete merged.reasoningEfforts;
                delete merged.defaultReasoningEffort;
            } else if (Array.isArray(merged.reasoningEfforts) && merged.defaultReasoningEffort && !merged.reasoningEfforts.includes(merged.defaultReasoningEffort)) {
                delete merged.defaultReasoningEffort;
            }
            return merged;
        });
    }
    function visibleModels(value) { return allModels(value).map(item => item.id).filter(id => !value.hiddenModels.includes(id)); }
    function nextId() { return 'vsai-provider-' + Date.now().toString(36) + '-' + (++sequence); }
    function currentCatalog() { return catalog(fields?.catalog, []); }
    function setMessage(value, error) { message = value || ''; messageIsError = error === true; controls(); }
    function clearDiscoveryToken() { if (fields) fields.discoveryToken = null; }
    function dirty(clearToken) {
        editRevision++;
        // The host request is read-only, but its result must not keep the editor
        // busy after any field moved to a newer revision.
        discoveryRequest = null;
        if (clearToken) clearDiscoveryToken();
        controls();
    }
    function warningText(value) {
        const labels = {
            'max-output-tokens-metadata-only': '最大输出仅记录服务能力，不会设置 Codex CLI 的硬性输出上限。',
            'runtime-catalog-baseline-unavailable': '未取得官方模型目录基线，运行时上下文覆盖尚未应用。',
            'runtime-metadata-native-id-collision': '模型 ID 与原生目录冲突，运行时元数据未覆盖该模型。',
            'runtime-metadata-cross-provider-collision': '多个服务使用相同模型 ID，运行时元数据未覆盖冲突模型。',
            'runtime-catalog-user-config': '检测到用户自定义模型目录，未叠加 VSAI 的运行时目录。',
            'runtime-catalog-user-assignment': '检测到用户指定的模型目录参数，未叠加 VSAI 的运行时目录。',
            'runtime-metadata-context-unknown': '未确认模型的原生上下文容量，运行时上下文覆盖尚未应用。'
        };
        return labels[value] || value;
    }
    function syncText(value) {
        const labels = { success: '已同步', partial: '部分同步', error: '同步失败', 'auth-error': '认证失败' };
        const succeeded = value.status === 'success' || value.status === 'partial';
        const time = succeeded ? (value.lastSuccessUtc || value.lastAttemptUtc) : (value.lastAttemptUtc || value.lastSuccessUtc);
        const summary = labels[value.status] ? labels[value.status] + (time ? ' · ' + time : '') : (time ? '上次尝试：' + time : '尚未同步');
        const lastSuccess = !succeeded && value.lastSuccessUtc && value.lastSuccessUtc !== time ? ' · 上次成功 ' + value.lastSuccessUtc : '';
        return value.error ? summary + lastSuccess + '：' + value.error : summary + lastSuccess;
    }

    function officialAccountText() {
        if (officialAccount.status === 'signed-in') return officialAccount.accountType === 'apiKey'
            ? '已检测到官方 API Key（不是 ChatGPT 官方订阅）。'
            : 'ChatGPT 官方订阅已登录';
        if (officialAccount.status === 'signing-in') return '正在等待官方账号登录完成。';
        if (officialAccount.status === 'error') return officialAccount.error ? officialAccount.error + ' 请重试登录或刷新状态。' : '官方账号登录未完成。请重试登录，或刷新状态。';
        return 'VSAI 尚未登录官方账号。登录一次后即可使用 GPT；桌面端登录不受影响。';
    }
    function officialAccountPending() { return officialAccount.status === 'signing-in' || officialLoginRequest !== null; }
    function installStyles() {
        const style = el('style'); style.id = 'vsai-providers-styles';
        style.textContent = '#vsai-providers-dialog{color:var(--color-token-foreground,#eee);background:var(--color-token-main-surface-primary,#262626);border:1px solid var(--color-token-border,#444);border-radius:16px;width:min(920px,calc(100vw - 24px));max-width:none;max-height:calc(100dvh - 24px);padding:0;font:13px/1.5 Segoe UI,Microsoft YaHei,sans-serif}#vsai-providers-dialog *{box-sizing:border-box}#vsai-providers-dialog button{font:inherit;color:inherit;border:1px solid var(--color-token-border,#444);border-radius:7px;background:transparent;cursor:pointer;min-height:29px;padding:4px 9px}#vsai-providers-dialog button:disabled{opacity:.45;cursor:default}#vsai-providers-dialog button:hover{background:var(--color-token-list-hover-background,#333)}#vsai-providers-dialog :focus-visible,.vsai-providers-entry:focus-visible{outline:2px solid var(--vscode-focusBorder,#4b9eed);outline-offset:2px}.vp-header{display:flex;gap:12px;padding:20px 22px 16px;border-bottom:1px solid var(--color-token-border,#444)}.vp-header-text{flex:1}.vp-header h1,.vp-header h2,.vp-header h3,.vp-subtitle{margin:0}.vp-subtitle,.vp-hint,.vp-info,.vp-note{color:var(--color-token-text-secondary,#aaa);font-size:12px}.vp-subtitle{margin-top:5px}.vp-close{border:0!important;font-size:20px!important}.vp-body{display:grid;grid-template-columns:210px minmax(0,1fr);max-height:calc(100dvh - 145px);overflow-y:auto}.vp-sidebar{padding:18px 14px;border-right:1px solid var(--color-token-border,#444)}.vp-list-title,.vp-row,.vp-footer,.vp-inline{display:flex;align-items:center;gap:8px}.vp-list-title{justify-content:space-between;margin-bottom:12px}.vp-list-title h2{font-size:12px;color:var(--color-token-text-secondary,#aaa)}.vp-account-title{margin-top:14px}.vp-card{border:1px solid transparent;border-radius:9px;padding:9px;margin-bottom:7px;background:var(--color-token-bg-secondary,#222)}.vp-card[aria-current=true]{border-color:var(--vscode-focusBorder,#4b9eed)}.vp-card h3{margin:0;font-size:13px;overflow-wrap:anywhere}.vp-info{margin:4px 0 8px;font-size:11px}.vp-actions{display:flex;flex-wrap:wrap;gap:6px}.vp-danger,.vp-status[data-error=true]{color:var(--vscode-errorForeground,#f48771)}.vp-confirm,.vp-empty{font-size:12px}.vp-empty{color:var(--color-token-text-secondary,#aaa)}#vsai-providers-dialog form{padding:18px 22px 20px;min-width:0}.vp-form-title{margin:0 0 16px;font-size:14px}#vsai-providers-dialog label{display:block;margin:12px 0 5px;font-size:12px;font-weight:500}#vsai-providers-dialog input,#vsai-providers-dialog textarea,#vsai-providers-dialog select{display:block;width:100%;font:inherit;color:inherit;background:var(--color-token-input-background,#333);border:1px solid var(--color-token-border,#555);border-radius:7px;padding:7px 9px}#vsai-providers-dialog textarea{resize:vertical;min-height:68px;font-family:Consolas,monospace}.vp-hint{margin:5px 0 0;font-size:11px}.vp-status{min-height:18px;overflow-wrap:anywhere}.vp-footer{justify-content:flex-end;margin-top:14px}.vp-save{background:var(--vscode-button-background,#086abf)!important;color:#fff!important;border-color:transparent!important}.vp-catalog{margin-top:18px;padding-top:14px;border-top:1px solid var(--color-token-border,#444)}.vp-switch{display:flex!important;align-items:center;gap:5px;margin:0!important;font-weight:400!important;white-space:nowrap}.vp-switch input,.vp-efforts input{width:auto!important}.vp-table{margin-top:9px;max-height:260px;overflow:auto;border:1px solid var(--color-token-border,#444);border-radius:8px}.vp-table table{width:100%;border-collapse:collapse;font-size:12px}.vp-table th,.vp-table td{padding:7px 8px;border-bottom:1px solid var(--color-token-border,#444);text-align:left;vertical-align:top;overflow-wrap:anywhere}.vp-table th{position:sticky;top:0;background:var(--color-token-main-surface-primary,#262626)}.vp-table tr[data-hidden=true]{opacity:.55}.vp-model-actions{white-space:nowrap}.vp-table summary{cursor:pointer;color:var(--vscode-textLink-foreground,#4b9eed)}.vp-meta{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:8px 10px;padding:9px 0}.vp-meta label{margin:0!important}.vp-meta label>span{display:block;margin-bottom:3px;font-size:11px;font-weight:400}.vp-full{grid-column:1/-1}.vp-efforts{display:flex;flex-wrap:wrap;gap:4px 8px}.vp-efforts label{display:flex!important;align-items:center;gap:3px}.vp-import{margin-top:10px}.vsai-providers-login{position:relative!important;color:transparent!important}.vsai-providers-login>*{visibility:hidden}.vsai-providers-login::after{content:"服务 / API Key";position:absolute;inset:0;display:flex;align-items:center;justify-content:center;color:var(--color-token-foreground,#eee)}.vsai-providers-entry{font:inherit;color:inherit;background:transparent;border:0;border-radius:7px;cursor:pointer;padding:5px 8px;font-size:12px;text-decoration:none}@media(max-width:650px){.vp-body{display:block}.vp-sidebar{border-right:0;border-bottom:1px solid var(--color-token-border,#444)}.vp-list{max-height:155px;overflow-y:auto}.vp-meta{grid-template-columns:1fr}}';
        style.textContent += '.vp-table table{color:inherit}.vp-inline button{white-space:nowrap;flex-shrink:0}';
        (document.head || document.documentElement).appendChild(style);
    }
    function controls() {
        if (!fields) return;
        const writing = writeRequest !== null;
        fields.save.disabled = !loaded || busy || writing;
        fields.save.textContent = writeRequest?.kind === 'providers-save' ? '正在保存…' : '保存服务';
        fields.newButton.disabled = writing;
        [fields.name, fields.url, fields.key, fields.source, fields.search, fields.importText, fields.importButton].forEach(node => { node.disabled = writing; });
        fields.table.querySelectorAll('button,input,select').forEach(node => {
            if (writing) {
                if (!own(node.dataset, 'vpWriteDisabled')) node.dataset.vpWriteDisabled = String(node.disabled);
                node.disabled = true;
            } else if (own(node.dataset, 'vpWriteDisabled')) {
                node.disabled = node.dataset.vpWriteDisabled === 'true';
                delete node.dataset.vpWriteDisabled;
            }
        });
        fields.refresh.disabled = writing || discoveryRequest !== null || !fields.name.value.trim() || !fields.url.value.trim();
        fields.refresh.textContent = discoveryRequest ? '正在刷新…' : '刷新';
        fields.list.querySelectorAll('button').forEach(node => { node.disabled = writing || (busy && node.dataset.mutates === 'true'); });
        fields.status.textContent = message || (busy ? '当前任务正在运行；可以刷新模型，保存和删除需等待任务结束。' : (stateRequest ? '正在读取服务…' : ''));
        fields.status.dataset.error = String(messageIsError); fields.syncStatus.textContent = syncText(currentCatalog());
        fields.warningBox.hidden = !fields.runtimeWarnings.length;
        fields.warningBox.textContent = fields.runtimeWarnings.length ? '运行时提示：' + fields.runtimeWarnings.map(warningText).join('；') : '';
        fields.officialStatus.textContent = officialAccountText();
        fields.officialStatus.dataset.error = String(officialAccount.status === 'error');
        const chatGptSubscription = officialAccount.status === 'signed-in' && officialAccount.accountType !== 'apiKey';
        fields.officialRefresh.disabled = writing || officialAccountRequest !== null || officialCancelRequest !== null;
        fields.officialRefresh.textContent = officialAccountRequest ? '正在读取…' : '刷新登录状态';
        fields.officialLogin.hidden = chatGptSubscription;
        fields.officialLogin.disabled = chatGptSubscription || busy || writing || officialAccountPending() || officialCancelRequest !== null;
        fields.officialLogin.textContent = officialAccountPending() ? '正在登录…' : '登录官方账号';
        fields.officialCancel.hidden = !officialAccountPending();
        fields.officialCancel.disabled = writing || officialCancelRequest !== null;
        fields.officialCancel.textContent = officialCancelRequest ? '正在取消…' : '取消登录';
    }
    function setCatalog(value, redraw) { fields.catalog = catalog(value, []); dirty(false); if (redraw) renderModels(); }
    function select(value) {
        selectedId = value?.id || null; confirmDeleteId = null;
        fields.name.value = value?.name || ''; fields.url.value = value?.baseUrl || ''; fields.key.value = '';
        fields.key.placeholder = value?.hasApiKey ? '留空保留已保存的密钥' : '请输入 API Key';
        fields.source.value = value ? value.catalog.source : 'auto';
        fields.catalog = clone(value?.catalog || catalog({}, [])); fields.runtimeWarnings = value?.runtimeWarnings || []; fields.discoveryToken = null; fields.search.value = ''; fields.importText.value = ''; discoveryRequest = null;
        fields.title.textContent = value ? '编辑服务' : '添加服务'; editRevision++; setMessage('', false); renderList(); renderModels();
        if (dialog.open) fields.name.focus({ preventScroll: true });
    }
    function renderList() {
        if (!fields) return; fields.list.replaceChildren();
        if (!providers.length) fields.list.appendChild(el('p', 'vp-empty', loaded ? '还没有第三方服务。添加后可在模型列表中选择。' : '正在读取服务…'));
        providers.forEach(value => {
            const card = el('div', 'vp-card'); card.setAttribute('aria-current', String(selectedId === value.id));
            card.append(el('h3', '', value.name), el('p', 'vp-info', visibleModels(value.catalog).length + ' 个可用模型 · ' + (value.hasApiKey ? '密钥已保存' : '未保存密钥')));
            const actions = el('div', 'vp-actions');
            if (confirmDeleteId === value.id) {
                card.appendChild(el('p', 'vp-confirm', '删除此服务及其保存的密钥？'));
                const confirm = btn('确认删除', 'vp-danger', () => { if (!busy && !writeRequest) deleteProvider(value.id); }); confirm.dataset.mutates = 'true';
                actions.append(confirm, btn('取消', '', () => { confirmDeleteId = null; renderList(); }));
            } else {
                actions.append(btn('编辑', '', () => select(value)));
                const remove = btn('删除', 'vp-danger', () => { if (!busy && !writeRequest) { confirmDeleteId = value.id; renderList(); } }); remove.dataset.mutates = 'true'; actions.append(remove);
            }
            card.appendChild(actions); fields.list.appendChild(card);
        }); controls();
    }
    function requestOfficialAccount() {
        if (officialAccountRequest || officialCancelRequest) return;
        const id = nextId(); officialAccountRequest = { requestId: id };
        try { window.acquireVsCodeApi().postMessage({ type: 'official-account-request', requestId: id }); }
        catch (_) { officialAccountRequest = null; officialAccount = { status: 'error', accountType: null, error: '未能读取官方账号状态。请关闭窗口后重试。' }; }
        controls();
    }
    function loginOfficialAccount() {
        if (busy || writeRequest || officialAccountPending() || officialCancelRequest || (officialAccount.status === 'signed-in' && officialAccount.accountType !== 'apiKey')) return;
        const id = nextId(); officialAccountRequest = null; officialLoginRequest = { requestId: id };
        officialAccount = { status: 'signing-in', accountType: null, error: null };
        try { window.acquireVsCodeApi().postMessage({ type: 'official-account-login', requestId: id }); }
        catch (_) { officialLoginRequest = null; officialAccount = { status: 'error', accountType: null, error: '未能启动官方账号登录。请关闭窗口后重试。' }; }
        controls();
    }
    function cancelOfficialAccountLogin() {
        if (!officialAccountPending() || writeRequest || officialCancelRequest) return;
        const id = nextId(); officialCancelRequest = { requestId: id };
        try { window.acquireVsCodeApi().postMessage({ type: 'official-account-cancel', requestId: id }); }
        catch (_) { officialCancelRequest = null; officialAccount = { status: 'error', accountType: null, error: '未能取消官方账号登录。请刷新状态后重试。' }; }
        controls();
    }
    function selectField(parent, label, value, choices, change, disabled) {
        const wrapper = el('label'); wrapper.appendChild(el('span', '', label)); const input = el('select');
        choices.forEach(choice => { const option = el('option', '', choice[1]); option.value = choice[0]; option.disabled = choice[2] === true; option.selected = String(value ?? '') === choice[0]; input.appendChild(option); });
        input.disabled = disabled; input.addEventListener('change', () => change(input.value)); wrapper.appendChild(input); parent.appendChild(wrapper); return input;
    }
    function textField(parent, label, value, change, disabled) {
        const wrapper = el('label'); wrapper.appendChild(el('span', '', label)); const input = el('input'); input.type = 'text'; input.value = value == null ? '' : String(value); input.disabled = disabled;
        input.addEventListener('change', () => change(input.value)); wrapper.appendChild(input); parent.appendChild(wrapper);
    }
    function updateOverride(id, change) {
        const value = currentCatalog(), base = allModels(value).find(item => item.id === id) || { id: id };
        const next = Object.assign({}, own(value.overrides, id) ? value.overrides[id] : { id: id }, { id: id }); change(next);
        value.overrides[id] = model(next) || { id: id }; setCatalog(value, false);
    }
    function capacity(value) {
        value = String(value || '').trim(); if (!value) return null;
        if (!/^\d+$/.test(value) || BigInt(value) <= 0n) throw new Error('容量必须是正整数。');
        return BigInt(value) > BigInt(Number.MAX_SAFE_INTEGER) ? value : Number(value);
    }
    function resetField(parent, id, name, local) {
        const value = currentCatalog();
        if (!local || !own(value.overrides, id) || !own(value.overrides[id], name)) return;
        const action = btn('跟随自动', '', () => {
            const next = currentCatalog();
            delete next.overrides[id][name];
            setCatalog(next, true);
        });
        action.title = '清除本字段的本地设置，继续使用自动发现值';
        parent.appendChild(action);
    }
    function modelEditor(parent, item) {
        const value = currentCatalog(), local = own(value.overrides, item.id), update = action => { if (local) updateOverride(item.id, action); };
        selectField(parent, '配置', local ? 'local' : 'auto', [['auto', '跟随自动发现'], ['local', '本地覆盖']], selected => {
            const next = currentCatalog(); if (selected === 'auto') delete next.overrides[item.id]; else if (!own(next.overrides, item.id)) next.overrides[item.id] = { id: item.id }; setCatalog(next, true);
        }, false);
        textField(parent, '显示名称', item.displayName, input => update(next => { next.displayName = input.trim() || undefined; }), !local); resetField(parent, item.id, 'displayName', local);
        textField(parent, '上下文容量', item.contextWindow, input => { try { update(next => { next.contextWindow = capacity(input); }); } catch (error) { setMessage(error.message, true); } }, !local); resetField(parent, item.id, 'contextWindow', local);
        textField(parent, '最大输出', item.maxOutputTokens, input => { try { update(next => { next.maxOutputTokens = capacity(input); }); } catch (error) { setMessage(error.message, true); } }, !local); resetField(parent, item.id, 'maxOutputTokens', local);
        capabilities.forEach(name => { selectField(parent, ({ supportsImages: '图片', supportsTools: '工具', supportsReasoning: '推理', supportsResponses: 'Responses' })[name], item[name] == null ? '' : String(item[name]), [['', '待补充'], ['true', '支持'], ['false', '不支持']], input => update(next => { next[name] = input === 'true' ? true : input === 'false' ? false : null; }), !local); resetField(parent, item.id, name, local); });
        const effortGroup = el('div', 'vp-full'); effortGroup.appendChild(el('span', '', Array.isArray(item.reasoningEfforts) ? '推理档位' : '推理档位（待补充）')); const checks = el('div', 'vp-efforts'); const available = Array.isArray(item.reasoningEfforts) ? item.reasoningEfforts : []; let defaultInput;
        efforts.forEach(effort => { const label = el('label'), check = el('input'); check.type = 'checkbox'; check.value = effort; check.checked = available.includes(effort); check.disabled = !local; const updateEffort = () => {
            const selected = Array.from(checks.querySelectorAll('input')).filter(input => input.checked).map(input => input.value);
            update(next => { next.reasoningEfforts = selected; if (!selected.includes(next.defaultReasoningEffort)) delete next.defaultReasoningEffort; });
            Array.from(defaultInput?.options || []).forEach(option => { if (efforts.includes(option.value)) option.disabled = !selected.includes(option.value); });
            if (defaultInput && !selected.includes(defaultInput.value)) defaultInput.value = '';
        }; check.addEventListener('input', updateEffort); check.addEventListener('change', updateEffort); label.append(check, document.createTextNode(effort)); checks.appendChild(label); });
        effortGroup.appendChild(checks); parent.appendChild(effortGroup);
        defaultInput = selectField(parent, '默认推理档位', item.defaultReasoningEffort || '', [['', '待补充']].concat(efforts.map(effort => [effort, effort, !available.includes(effort)])), input => update(next => { if (input) next.defaultReasoningEffort = input; else delete next.defaultReasoningEffort; }), !local);
        resetField(parent, item.id, 'reasoningEfforts', local); resetField(parent, item.id, 'defaultReasoningEffort', local);
        const reset = btn('重置本地覆盖', '', () => { const next = currentCatalog(); delete next.overrides[item.id]; setCatalog(next, true); }); reset.disabled = !local; parent.appendChild(reset);
    }
    function renderModels() {
        if (!fields) return; const value = currentCatalog(), needle = fields.search.value.trim().toLowerCase();
        const models = allModels(value).filter(item => !needle || item.id.toLowerCase().includes(needle) || String(item.displayName || '').toLowerCase().includes(needle));
        fields.table.replaceChildren();
        if (!models.length) { fields.table.appendChild(el('p', 'vp-empty', needle ? '没有匹配的模型。' : '尚未添加模型；可刷新或批量导入。')); controls(); return; }
        const table = el('table'), head = el('thead'), row = el('tr'); ['模型', '能力', '来源', '操作'].forEach(label => row.appendChild(el('th', '', label))); head.appendChild(row); const body = el('tbody');
        models.forEach(item => {
            const hidden = value.hiddenModels.includes(item.id), line = el('tr'); line.dataset.hidden = String(hidden);
            const name = el('td'); name.appendChild(el('strong', '', item.displayName || item.id)); if (item.displayName) name.appendChild(el('div', 'vp-note', item.id));
            const details = el('details'); details.appendChild(el('summary', '', '详情')); const editor = el('div', 'vp-meta'); modelEditor(editor, item); details.appendChild(editor); name.appendChild(details);
            const labels = { supportsImages: '图', supportsTools: '工具', supportsReasoning: '推理', supportsResponses: 'Responses' };
            const capabilityText = capabilities.map(name => item[name] === true ? labels[name] : item[name] === false ? '不支持' + labels[name] : null).filter(Boolean);
            if (!capabilityText.length) capabilityText.push('能力待补充');
            else if (capabilities.some(name => item[name] == null)) capabilityText.push('其余待补充');
            const actions = el('td', 'vp-model-actions');
            actions.append(btn(hidden ? '显示' : '隐藏', '', () => { const next = currentCatalog(); next.hiddenModels = hidden ? next.hiddenModels.filter(id => id !== item.id) : ids(next.hiddenModels.concat(item.id)); setCatalog(next, true); }));
            if (value.manualModels.includes(item.id)) actions.append(btn('移除', 'vp-danger', () => { const next = currentCatalog(); next.manualModels = next.manualModels.filter(id => id !== item.id); delete next.overrides[item.id]; next.hiddenModels = next.hiddenModels.filter(id => id !== item.id); setCatalog(next, true); }));
            if (item.maxOutputTokens != null) name.appendChild(el('div', 'vp-note', '最大输出为服务能力记录，不会设置 Codex CLI 的硬性输出上限。'));
            line.append(name, el('td', '', capabilityText.join('、')), el('td', '', value.manualModels.includes(item.id) ? '手动' : '发现'), actions); body.appendChild(line);
        }); table.append(head, body); fields.table.appendChild(table); controls();
    }
    function importModels() {
        const incoming = ids(fields.importText.value.split(/\r?\n/));
        if (!incoming.length) { setMessage('请每行填写一个模型 ID。', true); fields.importText.focus(); return; }
        const value = currentCatalog(); value.manualModels = ids(value.manualModels.concat(incoming)); fields.importText.value = ''; setCatalog(value, true); setMessage('已加入 ' + incoming.length + ' 个手动模型，保存后生效。', false);
    }
    function draft(requireKey, requireModels) {
        const name = fields.name.value.trim(), baseUrl = fields.url.value.trim(); if (!name) throw new Error('请填写服务名称。');
        let parsed; try { parsed = new URL(baseUrl); } catch (_) { /* Explained below. */ }
        if (!parsed || !['http:', 'https:'].includes(parsed.protocol) || parsed.username || parsed.password || parsed.hash || parsed.search || baseUrl.includes('?') || baseUrl.includes('#')) throw new Error('请填写有效的 HTTP 或 HTTPS Base URL，地址中不要包含账号、密码、查询参数或片段。');
        const value = currentCatalog(); value.source = fields.source.value;
        if (requireModels && !visibleModels(value).length) throw new Error('请刷新或至少导入一个未隐藏的模型 ID。');
        const existing = providers.find(item => item.id === selectedId), apiKey = fields.key.value.trim();
        if (requireKey && !apiKey && !existing?.hasApiKey) throw new Error('请填写该服务的 API Key。');
        const result = { name, baseUrl, apiKey, models: visibleModels(value), catalog: value };
        if (selectedId) result.id = selectedId; if (fields.discoveryToken) result.discoveryToken = fields.discoveryToken;
        return result;
    }
    function save(event) {
        event.preventDefault(); if (!loaded || busy || writeRequest) return;
        let value; try { value = draft(true, false); } catch (error) { setMessage(error.message, true); return; }
        fields.key.value = ''; const id = nextId(); writeRequest = { requestId: id, kind: 'providers-save', apiKey: value.apiKey };
        try { window.acquireVsCodeApi().postMessage({ type: 'providers-save', requestId: id, provider: value }); } catch (_) { writeRequest = null; setMessage('未能连接到扩展，请关闭窗口后重试。', true); }
        value.apiKey = ''; controls();
    }
    function deleteProvider(id) {
        const request = nextId(); writeRequest = { requestId: request, kind: 'providers-delete' };
        try { window.acquireVsCodeApi().postMessage({ type: 'providers-delete', requestId: request, id }); } catch (_) { writeRequest = null; setMessage('未能连接到扩展，请关闭窗口后重试。', true); } controls();
    }
    function discover() {
        if (writeRequest || discoveryRequest) return;
        let value; try { value = draft(false, false); } catch (error) { setMessage(error.message, true); return; }
        const id = nextId(); discoveryRequest = { requestId: id, revision: editRevision, selectedId };
        try { window.acquireVsCodeApi().postMessage({ type: 'providers-discover', requestId: id, provider: { id: value.id, name: value.name, baseUrl: value.baseUrl, apiKey: value.apiKey, catalog: value.catalog } }); } catch (_) { discoveryRequest = null; setMessage('未能连接到扩展，请关闭窗口后重试。', true); }
        value.apiKey = ''; controls();
    }
    function addField(form, id, label, type, placeholder) {
        const caption = el('label', '', label), input = el('input'); caption.htmlFor = id; input.id = id; input.type = type; input.autocomplete = type === 'password' ? 'new-password' : 'off'; input.spellcheck = false; input.placeholder = placeholder || ''; form.append(caption, input); return input;
    }
    function close() {
        if (!dialog?.open) return; fields.key.value = ''; dialog.close(); confirmDeleteId = null; discoveryRequest = null; editRevision++;
        if (returnFocus?.isConnected) returnFocus.focus({ preventScroll: true }); returnFocus = null;
    }
    function createDialog() {
        dialog = el('dialog'); dialog.id = 'vsai-providers-dialog'; dialog.setAttribute('aria-labelledby', 'vsai-providers-title');
        const header = el('div', 'vp-header'), headerText = el('div', 'vp-header-text'), titleHeading = el('h1', '', '模型与服务'); titleHeading.id = 'vsai-providers-title'; headerText.append(titleHeading, el('p', 'vp-subtitle', '点击“刷新”获取服务模型与能力；检查后保存，模型目录才会更新。')); const dismiss = btn('×', 'vp-close', close); dismiss.setAttribute('aria-label', '关闭模型与服务'); header.append(headerText, dismiss);
        const body = el('div', 'vp-body'), sidebar = el('aside', 'vp-sidebar');
        const officialTitle = el('div', 'vp-list-title'), officialCard = el('div', 'vp-card vp-official-card'), officialStatus = el('p', 'vp-info'), officialActions = el('div', 'vp-actions');
        const officialRefresh = btn('刷新登录状态', '', requestOfficialAccount), officialLogin = btn('登录官方账号', '', loginOfficialAccount), officialCancel = btn('取消登录', '', cancelOfficialAccountLogin);
        officialStatus.setAttribute('role', 'status'); officialStatus.setAttribute('aria-live', 'polite'); officialActions.append(officialRefresh, officialLogin, officialCancel); officialCard.append(el('h3', '', 'ChatGPT 官方订阅'), officialStatus, officialActions); officialTitle.appendChild(el('h2', '', '官方账号'));
        const listTitle = el('div', 'vp-list-title vp-account-title'), newButton = btn('＋ 添加', '', () => select(null)), list = el('div', 'vp-list'); listTitle.append(el('h2', '', '第三方服务'), newButton); sidebar.append(officialTitle, officialCard, listTitle, list);
        const form = el('form'); form.autocomplete = 'off'; form.noValidate = true; const title = el('h2', 'vp-form-title', '添加服务'); form.appendChild(title);
        const name = addField(form, 'vsai-provider-name', '服务名称', 'text', '例如：我的模型服务'); const url = addField(form, 'vsai-provider-url', 'Base URL', 'url', 'https://api.example.com/v1'); form.appendChild(el('p', 'vp-hint', '填写服务商提供的 API 基础地址，需要支持 Responses API。')); const key = addField(form, 'vsai-provider-key', 'API Key', 'password', '请输入 API Key'); form.appendChild(el('p', 'vp-hint', '密钥仅用于该服务；编辑时留空会保留已保存的密钥。'));
        const section = el('section', 'vp-catalog'); section.appendChild(el('h3', '', '模型目录')); const sync = el('div', 'vp-inline'); const source = el('select'); [['auto', '自动识别'], ['openai', 'OpenAI 兼容'], ['midas', 'Midas']].forEach(choice => { const option = el('option', '', choice[1]); option.value = choice[0]; source.appendChild(option); }); const refresh = btn('刷新', '', discover), syncStatus = el('span', 'vp-hint'); sync.append(source, refresh, syncStatus); section.appendChild(sync);
        const search = el('input'); search.type = 'search'; search.placeholder = '搜索模型 ID 或名称'; search.setAttribute('aria-label', '搜索模型'); section.appendChild(search); const table = el('div', 'vp-table'); section.appendChild(table);
        const importBox = el('details', 'vp-import'); importBox.appendChild(el('summary', '', '手动添加 / 批量导入模型')); const importText = el('textarea'); importText.rows = 3; importText.placeholder = '每行一个精确模型 ID'; const importButton = btn('加入模型', '', importModels); importBox.append(importText, importButton); section.appendChild(importBox); section.appendChild(el('p', 'vp-hint', '“最大输出”记录服务能力；Codex CLI 没有独立的硬性 max-output 设置。')); form.appendChild(section);
        const status = el('p', 'vp-status'), footer = el('div', 'vp-footer'), saveButton = el('button', 'vp-save', '保存服务'); status.setAttribute('role', 'status'); status.setAttribute('aria-live', 'polite'); saveButton.type = 'submit'; footer.append(btn('关闭', '', close), saveButton); form.append(status, footer); form.addEventListener('submit', save);
        name.addEventListener('input', () => dirty(false)); url.addEventListener('input', () => dirty(true)); key.addEventListener('input', () => dirty(true)); source.addEventListener('change', () => dirty(true)); search.addEventListener('input', renderModels);
        const warningBox = el('p', 'vp-status'); warningBox.hidden = true; form.insertBefore(warningBox, status);
        body.append(sidebar, form); dialog.append(header, body); fields = { list, newButton, name, url, key, source, refresh, syncStatus, search, table, importText, importButton, title, status, warningBox, save: saveButton, officialStatus, officialRefresh, officialLogin, officialCancel, catalog: catalog({}, []), runtimeWarnings: [], discoveryToken: null };
        dialog.addEventListener('cancel', event => { event.preventDefault(); close(); }); document.body.appendChild(dialog);
    }
    function requestProviders() {
        if (stateRequest || writeRequest) return; const id = nextId(); stateRequest = { requestId: id };
        try { window.acquireVsCodeApi().postMessage({ type: 'providers-request', requestId: id }); } catch (_) { stateRequest = null; setMessage('未能连接到扩展，请关闭窗口后重试。', true); } controls();
    }
    function open(source) {
        if (!document.body) return; if (!dialog) createDialog(); if (dialog.open) return;
        returnFocus = source || document.activeElement; dialog.showModal(); select(null); requestOfficialAccount(); requestProviders();
    }
    function loginRoot(node) {
        const root = node.closest('div.fixed.inset-0.overflow-hidden.bg-token-side-bar-background');
        if (!root || root.querySelector('[data-codex-intelligence-trigger]') || root.querySelector('h1')?.textContent.trim() !== 'Codex') return null;
        const column = Array.from(root.querySelectorAll('div')).find(item => item.classList.contains('max-w-[360px]')), options = node.closest('div.mx-auto.inline-flex.w-max.flex-col.items-stretch');
        return column?.contains(node) && options && column.contains(options) ? root : null;
    }
    function mount() {
        scheduled = false;
        for (const [node, aria] of loginButtons) if (!node.isConnected || !loginRoot(node) || !loginLabels.has(node.textContent.trim())) { node.classList.remove('vsai-providers-login'); if (aria === null) node.removeAttribute('aria-label'); else node.setAttribute('aria-label', aria); loginButtons.delete(node); }
        document.querySelectorAll('div.fixed.inset-0.overflow-hidden.bg-token-side-bar-background button').forEach(node => { if (loginButtons.has(node) || !loginLabels.has(node.textContent.trim()) || !loginRoot(node)) return; loginButtons.set(node, node.getAttribute('aria-label')); node.classList.add('vsai-providers-login'); node.setAttribute('aria-label', '服务 / API Key'); });
        const nav = document.querySelector('[data-settings-panel-slug]')?.closest('nav') || (settingsSurface ? document.querySelector('.app-shell-left-panel nav') : null);
        if (nav && !nav.querySelector('#vsai-providers-settings-entry')) { const list = nav.querySelector('.overflow-y-auto'); if (list) { const entry = el('a', 'vsai-providers-entry', '◇  模型与服务'); entry.id = 'vsai-providers-settings-entry'; entry.setAttribute('role', 'button'); entry.setAttribute('aria-haspopup', 'dialog'); entry.tabIndex = 0; entry.addEventListener('click', event => { event.preventDefault(); event.stopPropagation(); open(entry); }); entry.addEventListener('keydown', event => { if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); event.stopPropagation(); open(entry); } }); list.appendChild(entry); } }
    }
    document.addEventListener('click', event => { const target = event.target instanceof Element ? event.target.closest('button.vsai-providers-login') : null; if (!target || !loginButtons.has(target) || !loginRoot(target)) return; event.preventDefault(); event.stopImmediatePropagation(); open(target); }, true);
    window.addEventListener('codex-host-message', event => {
        const state = event.detail;
        if (state?.type === 'official-account-state') {
            const completedState = officialAccountRequest && state.requestId === officialAccountRequest.requestId ? officialAccountRequest : null;
            const completedLogin = officialLoginRequest && state.requestId === officialLoginRequest.requestId ? officialLoginRequest : null;
            const completedCancel = officialCancelRequest && state.requestId === officialCancelRequest.requestId ? officialCancelRequest : null;
            if (state.requestId && !completedState && !completedLogin && !completedCancel) return;
            const status = ['signed-in', 'signed-out', 'signing-in', 'error'].includes(state.status) ? state.status : 'error';
            officialAccount = { status: status, accountType: typeof state.accountType === 'string' ? state.accountType : null, error: typeof state.error === 'string' ? state.error : null };
            if (completedState || !state.requestId) officialAccountRequest = null;
            if (status !== 'signing-in') {
                officialLoginRequest = null;
                officialCancelRequest = null;
            } else if (completedCancel) {
                officialCancelRequest = null;
            }
            controls(); return;
        }
        if (state?.type === 'providers-discovery') {
            const request = discoveryRequest;
            if (!request || state.requestId !== request.requestId || !dialog?.open || request.revision !== editRevision || request.selectedId !== selectedId) return;
            discoveryRequest = null; const value = currentCatalog(); value.lastAttemptUtc = typeof state.attemptedUtc === 'string' ? state.attemptedUtc : value.lastAttemptUtc; value.status = typeof state.status === 'string' ? state.status : 'error'; value.error = typeof state.error === 'string' ? state.error : null;
            if (typeof state.discoveryToken === 'string') fields.discoveryToken = state.discoveryToken;
            if ((state.status === 'success' || state.status === 'partial') && Array.isArray(state.models)) { value.discoveredModels = state.models.map(model).filter(Boolean); value.lastSuccessUtc = value.lastAttemptUtc; setMessage(state.status === 'partial' ? '已刷新部分模型；请检查后保存。' : '模型目录已刷新；请检查后保存。', false); }
            else setMessage(value.error || '模型发现未完成；保留现有目录。', true);
            fields.catalog = value; renderModels(); controls(); return;
        }
        if (state?.type !== 'providers-state') return;
        const completedState = stateRequest && state.requestId === stateRequest.requestId ? stateRequest : null, completedWrite = writeRequest && state.requestId === writeRequest.requestId ? writeRequest : null;
        if (state.requestId && !completedState && !completedWrite) return;
        if (Array.isArray(state.providers)) { providers = state.providers.map(profile).filter(Boolean); loaded = true; }
        busy = state.isBusy === true; if (completedState) stateRequest = null; if (completedWrite) writeRequest = null;
        if (completedWrite && !state.error && (state.saved || completedWrite.kind === 'providers-delete')) { select(null); setMessage(completedWrite.kind === 'providers-delete' ? '服务已删除。' : '服务已保存，可在聊天的模型列表中选择。', false); }
        else if (state.error && (completedState || completedWrite)) {
            if (completedWrite?.kind === 'providers-save' && completedWrite.apiKey) fields.key.value = completedWrite.apiKey;
            setMessage(String(state.error), true);
        }
        renderList(); controls();
    });
    installStyles();
    const observer = new MutationObserver(() => { if (!scheduled) { scheduled = true; window.requestAnimationFrame(mount); } });
    observer.observe(document.documentElement, { childList: true, subtree: true }); mount();
})();
