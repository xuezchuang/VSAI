// VSAI-owned settings page. The frozen Codex frontend remains unchanged.
(function () {
    'use strict';

    if (document.querySelector('meta[name="vsai-settings-surface"]')?.content !== 'true') return;

    const navId = 'vsai-project-settings-nav';
    const contentId = 'vsai-project-settings-content';
    let currentRoute = document.querySelector('meta[name="initial-route"]')?.content || '/settings';
    let selected = false;
    let waiting = false;
    let state = null;
    let navigationList = null;
    let contentParent = null;
    let observer = null;
    let scheduled = false;
    const suspendedCurrent = new Map();

    function post(type) {
        window.acquireVsCodeApi().postMessage({ type: type });
    }

    function isSettingsRoute() {
        return /^\/settings(?:[/?#]|$)/i.test(currentRoute);
    }

    function installStyles() {
        if (document.getElementById('vsai-project-settings-styles')) return;
        const style = document.createElement('style');
        style.id = 'vsai-project-settings-styles';
        style.textContent = `
            #${navId} {
                display:flex; align-items:center; gap:8px; width:100%; min-height:30px;
                padding:5px 8px; border:0; border-radius:8px; color:inherit;
                background:transparent; font:inherit; text-align:start; cursor:pointer;
            }
            #${navId} svg { width:16px; height:16px; flex-shrink:0; }
            #${navId}:hover, #${navId}[aria-current="page"] {
                background:var(--color-token-bg-tertiary, var(--vscode-list-hoverBackground, rgba(127,127,127,.16)));
            }
            .vsai-project-nav-selected button:not(#${navId}) { background:transparent !important; }
            .vsai-project-nav-selected button:not(#${navId}),
            .vsai-project-nav-selected button:not(#${navId}) * {
                color:var(--color-token-text-primary, var(--vscode-foreground, #eee)) !important;
            }
            .vsai-project-nav-selected button:not(#${navId}):hover {
                background:var(--vscode-list-hoverBackground, rgba(127,127,127,.12)) !important;
            }
            .vsai-project-content-selected > :not(#${contentId}) { display:none !important; }
            #${contentId} {
                height:100%; min-height:0; overflow-y:auto; box-sizing:border-box;
                padding:calc(var(--height-toolbar-sm, 32px) + var(--padding-panel, 24px)) var(--padding-panel, 24px) 24px;
                background:var(--color-token-main-surface-primary, var(--vscode-editor-background, #262626));
                color:var(--color-token-text-primary, var(--vscode-foreground, #eee));
            }
            #${contentId} .vsai-project-inner { max-width:672px; margin:0 auto; }
            #${contentId} h1 { margin:0 0 24px; font-size:14px; font-weight:600; }
            #${contentId} .vsai-project-card {
                border:1px solid var(--color-token-border, var(--vscode-widget-border, rgba(127,127,127,.24)));
                border-radius:8px; padding:16px;
                background:var(--color-background-panel, var(--color-token-bg-fog, rgba(127,127,127,.04)));
            }
            #${contentId} .vsai-project-row { display:flex; align-items:flex-start; flex-wrap:wrap; gap:16px; }
            #${contentId} .vsai-project-label { flex:1 1 200px; min-width:0; }
            #${contentId} h2 { margin:0; font-size:13px; font-weight:500; }
            #${contentId} .vsai-project-description, #${contentId} .vsai-project-status {
                margin:6px 0 0; font-size:12px; line-height:1.5;
                color:var(--color-token-text-tertiary, var(--vscode-descriptionForeground, #aaa));
            }
            #${contentId} .vsai-project-actions { display:flex; flex-wrap:wrap; gap:8px; }
            #${contentId} button {
                min-height:30px; padding:5px 10px; border:1px solid transparent; border-radius:6px;
                background:var(--color-token-bg-tertiary, rgba(127,127,127,.2)); color:inherit;
                font:inherit; font-size:12px; cursor:pointer;
            }
            #${contentId} button:hover { background:var(--vscode-list-hoverBackground, rgba(127,127,127,.3)); }
            #${contentId} button:disabled { cursor:default; opacity:.5; }
            #${contentId} button:focus-visible, #${navId}:focus-visible {
                outline:2px solid var(--vscode-focusBorder, #007fd4); outline-offset:2px;
            }
            #${contentId} .vsai-project-directory {
                margin:14px 0 0; padding:10px 12px; border-radius:6px;
                background:var(--color-token-bg-secondary, rgba(127,127,127,.1));
                font-size:13px; line-height:1.6; overflow-wrap:anywhere; user-select:text;
            }
        `;
        (document.head || document.documentElement).appendChild(style);
    }

    function text(node, value) {
        if (node && node.textContent !== value) node.textContent = value;
    }

    function updateState() {
        const content = document.getElementById(contentId);
        if (!content) return;
        text(content.querySelector('.vsai-project-directory'), state?.directory || '正在读取工作目录…');
        text(content.querySelector('.vsai-project-status'), state?.isBusy
            ? '当前任务正在运行，完成后可以更改目录。'
            : state ? (state.followSolutionDirectory ? '当前跟随解决方案目录。' : '当前使用自选文件夹。') : '');
        content.querySelectorAll('button').forEach(button => {
            button.disabled = waiting || !state || state.isBusy === true;
        });
    }

    function changeDirectory(type) {
        if (waiting || !state || state.isBusy) return;
        waiting = true;
        updateState();
        post(type);
    }

    function createContent() {
        const content = document.createElement('section');
        content.id = contentId;
        content.setAttribute('aria-labelledby', 'vsai-project-settings-title');
        content.innerHTML = `
            <div class="vsai-project-inner">
                <h1 id="vsai-project-settings-title">项目设置</h1>
                <div class="vsai-project-card">
                    <div class="vsai-project-row">
                        <div class="vsai-project-label">
                            <h2>工作目录</h2>
                            <p class="vsai-project-description">选择新会话使用的文件夹。更改目录会切换到新会话。</p>
                        </div>
                        <div class="vsai-project-actions">
                            <button type="button" data-action="choose">选择文件夹…</button>
                            <button type="button" data-action="solution">使用解决方案目录</button>
                        </div>
                    </div>
                    <div class="vsai-project-directory" aria-label="当前工作目录"></div>
                    <p class="vsai-project-status" role="status" aria-live="polite"></p>
                </div>
            </div>`;
        content.querySelector('[data-action="choose"]').addEventListener('click', () => changeDirectory('project-settings-choose-directory'));
        content.querySelector('[data-action="solution"]').addEventListener('click', () => changeDirectory('project-settings-use-solution-directory'));
        return content;
    }

    function hideContent() {
        document.getElementById(contentId)?.remove();
        contentParent?.classList.remove('vsai-project-content-selected');
        contentParent = null;
        navigationList?.classList.remove('vsai-project-nav-selected');
        document.getElementById(navId)?.removeAttribute('aria-current');
        suspendedCurrent.forEach((value, button) => {
            if (button.isConnected && !button.hasAttribute('aria-current')) button.setAttribute('aria-current', value);
        });
        suspendedCurrent.clear();
    }

    function mount() {
        scheduled = false;
        if (!isSettingsRoute()) return;
        const nav = document.querySelector('[data-settings-panel-slug]')?.closest('nav')
            || document.querySelector('.app-shell-left-panel nav');
        if (!nav) {
            hideContent();
            return;
        }

        const buttons = Array.from(nav.querySelectorAll('button')).filter(button => button.id !== navId);
        const anchor = buttons[buttons.length - 1];
        if (!anchor) return;
        const list = anchor.closest('.overflow-y-auto');
        if (!list || !nav.contains(list)) return;
        navigationList = list;
        let button = document.getElementById(navId);
        if (!button) {
            button = document.createElement('button');
            button.id = navId;
            button.type = 'button';
            button.setAttribute('aria-label', '项目设置');
            button.setAttribute('aria-controls', contentId);
            button.innerHTML = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" aria-hidden="true"><path d="M3 7V5a1 1 0 0 1 1-1h5l2 3h9a1 1 0 0 1 1 1v11a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1V7Z"/></svg><span>项目设置</span>';
            button.addEventListener('click', () => {
                selected = true;
                waiting = false;
                mount();
                post('project-settings-request');
            });
            anchor.insertAdjacentElement('afterend', button);
        }
        button.querySelector('span').hidden = !anchor.textContent.trim();
        if (!selected) {
            hideContent();
            return;
        }

        // Mount alongside the frozen settings content so its React tree stays intact.
        const surface = Array.from(document.querySelectorAll('.main-surface'))
            .find(element => !nav.contains(element) && !element.contains(nav)
                && !element.closest('#' + contentId) && element.querySelector(':scope > .scrollbar-stable'));
        if (!surface?.parentElement) return;
        const parent = surface.parentElement;
        if (parent.contains(nav)) return;
        if (parent !== contentParent) hideContent();
        contentParent = parent;
        parent.classList.add('vsai-project-content-selected');
        navigationList.classList.add('vsai-project-nav-selected');
        suspendedCurrent.forEach((_, item) => {
            if (!item.isConnected) suspendedCurrent.delete(item);
        });
        navigationList.querySelectorAll('button[aria-current]').forEach(item => {
            if (item.id === navId) return;
            if (!suspendedCurrent.has(item)) suspendedCurrent.set(item, item.getAttribute('aria-current'));
            item.removeAttribute('aria-current');
        });
        if (button.getAttribute('aria-current') !== 'page') button.setAttribute('aria-current', 'page');
        if (!document.getElementById(contentId)) parent.appendChild(createContent());
        updateState();
    }

    function scheduleMount() {
        if (scheduled) return;
        scheduled = true;
        window.requestAnimationFrame(mount);
    }

    function observeRoute() {
        if (!isSettingsRoute()) {
            observer?.disconnect();
            observer = null;
            selected = false;
            hideContent();
            document.getElementById(navId)?.remove();
            return;
        }
        if (!observer) {
            observer = new MutationObserver(scheduleMount);
            observer.observe(document.documentElement, {
                childList: true, subtree: true, attributes: true, attributeFilter: ['aria-current']
            });
        }
        scheduleMount();
    }

    document.addEventListener('click', event => {
        const button = event.target instanceof Element ? event.target.closest('button') : null;
        if (selected && button && button.id !== navId && navigationList?.contains(button)) {
            selected = false;
            hideContent();
        }
    }, true);

    window.addEventListener('codex-host-message', event => {
        const message = event.detail;
        if (message?.type === 'project-settings-state') {
            state = message;
            waiting = false;
            updateState();
        } else if (message?.type === 'navigate-to-route' && typeof message.path === 'string') {
            currentRoute = message.path;
            selected = false;
            hideContent();
            observeRoute();
        }
    });

    installStyles();
    observeRoute();
})();
