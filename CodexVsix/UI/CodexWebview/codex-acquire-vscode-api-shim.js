// VS Code acquireVsCodeApi shim - bridges Codex official webview IPC to CyberVinci host.
(function () {
    'use strict';

    const CHANNEL = 'codex-webview-ipc';
    const EXTENSION_VERSION = '26.5527.31454';
    const BUILD_FLAVOR = 'prod';
    const query = new URLSearchParams(window.location.search);
    const webviewId = query.get('webviewId') || 'default';
    let state;

    function readMeta(name) {
        const node = document.querySelector('meta[name="' + name + '"]');
        return node && node.getAttribute('content') || undefined;
    }

    let diagnosticLoggingEnabled = readMeta('codex-diagnostic-logging-enabled') === 'true';
    const appSessionId = readMeta('codex-session-id') || ('theia-' + Math.random().toString(36).slice(2));
    const sharedObjects = new Map([
        ['host_config', { id: 'local', display_name: 'Local', kind: 'local' }]
    ]);

    function postToHost(message) {
        if (message && message.type === 'log-message' && !diagnosticLoggingEnabled) {
            return;
        }
        window.parent.postMessage({ channel: CHANNEL, webviewId: webviewId, message: message }, '*');
    }

    function redactDiagnosticText(value) {
        return String(value || '')
            .replace(/Bearer\s+[^\s"']+/gi, 'Bearer [redacted]')
            .replace(/\bsk-[A-Za-z0-9_-]{10,}\b/g, '[redacted-api-key]')
            .slice(0, 6000);
    }

    function formatDiagnosticValue(value) {
        if (value instanceof Error) {
            return value.stack || (value.name + ': ' + value.message);
        }
        if (typeof value === 'string' || typeof value === 'number' || typeof value === 'boolean') {
            return String(value);
        }
        if (value === null || value === undefined) {
            return String(value);
        }
        return Object.prototype.toString.call(value);
    }

    let isForwardingConsoleError = false;
    const originalConsoleError = console.error.bind(console);
    console.error = function () {
        const values = Array.prototype.slice.call(arguments);
        originalConsoleError.apply(console, values);
        if (!diagnosticLoggingEnabled || isForwardingConsoleError) return;
        isForwardingConsoleError = true;
        try {
            postToHost({
                type: 'log-message',
                level: 'error',
                message: 'console-error: ' + redactDiagnosticText(values.map(formatDiagnosticValue).join(' '))
            });
        } finally {
            isForwardingConsoleError = false;
        }
    };

    function logError(prefix, error) {
        const message = prefix + ': ' + ((error && (error.stack || error.message)) || String(error));
        if (diagnosticLoggingEnabled) {
            postToHost({ type: 'log-message', level: 'error', message: redactDiagnosticText(message) });
        }
        try {
            originalConsoleError(message, error);
        } catch {
            // no-op
        }
    }

    function getNavigationStrings() {
        const language = (document.documentElement.lang || navigator.language || 'en').toLowerCase();
        const strings = {
            historyTitle: 'Task history',
            historySearch: 'Search conversations',
            historyLoading: 'Loading history...',
            historyEmpty: 'No conversations found.',
            historyError: 'Could not load local task history.',
            historyRetry: 'Try again',
            historyClose: 'Close',
            historyUntitled: 'New conversation',
            historyLocalSingular: 'local conversation',
            historyLocalPlural: 'local conversations',
            historyMoreAvailable: 'More conversations are available.',
            historyLoadMore: 'Load more',
            historyLoadingMore: 'Loading more...',
            historyDelete: 'Delete',
            historyDeleteConfirm: 'Remove this conversation from task history?',
            historyDeleteAction: 'Remove',
            historyDeleteCancel: 'Cancel',
            historyDeleting: 'Removing...',
            historyDeleteError: 'Could not remove this conversation.'
        };
        if (language.startsWith('pt')) {
            return Object.assign(strings, {
                historyTitle: 'Hist\u00f3rico de tarefas',
                historySearch: 'Buscar conversas',
                historyLoading: 'Carregando hist\u00f3rico...',
                historyEmpty: 'Nenhuma conversa encontrada.',
                historyError: 'N\u00e3o foi poss\u00edvel carregar o hist\u00f3rico local.',
                historyRetry: 'Tentar novamente',
                historyClose: 'Fechar',
                historyUntitled: 'Nova conversa',
                historyLocalSingular: 'conversa local',
                historyLocalPlural: 'conversas locais',
                historyMoreAvailable: 'Há mais conversas disponíveis.',
                historyLoadMore: 'Carregar mais',
                historyLoadingMore: 'Carregando mais...',
                historyDelete: 'Excluir',
                historyDeleteConfirm: 'Remover esta conversa do histórico de tarefas?',
                historyDeleteAction: 'Remover',
                historyDeleteCancel: 'Cancelar',
                historyDeleting: 'Removendo...',
                historyDeleteError: 'Não foi possível remover esta conversa.'
            });
        }
        return strings;
    }

    function installHostUiStyles() {
        if (document.getElementById('codex-vs-host-ui-styles')) return;
        const style = document.createElement('style');
        style.id = 'codex-vs-host-ui-styles';
        style.textContent = [
            '#codex-vs-recent-history {',
            '  position: fixed;',
            '  inset: 0;',
            '  z-index: 2147483000;',
            '  color: var(--vscode-foreground, var(--token-foreground, inherit));',
            '  font: inherit;',
            '}',
            '#codex-vs-recent-history-panel {',
            '  position: absolute;',
            '  inset-block-start: 42px;',
            '  inset-inline-end: 10px;',
            '  display: flex;',
            '  width: min(420px, calc(100vw - 20px));',
            '  max-height: min(640px, calc(100vh - 54px));',
            '  flex-direction: column;',
            '  overflow: hidden;',
            '  border: 1px solid var(--vscode-widget-border, rgba(127,127,127,.28));',
            '  border-radius: 12px;',
            '  background: var(--vscode-sideBar-background, var(--token-main-surface-primary, #202020));',
            '  box-shadow: 0 18px 48px rgba(0,0,0,.35), 0 2px 8px rgba(0,0,0,.22);',
            '}',
            '.codex-vs-history-header {',
            '  display: flex;',
            '  align-items: center;',
            '  justify-content: space-between;',
            '  gap: 12px;',
            '  padding: 12px 12px 8px;',
            '}',
            '.codex-vs-history-title {',
            '  margin: 0;',
            '  font-size: 14px;',
            '  font-weight: 600;',
            '}',
            '.codex-vs-history-close {',
            '  display: inline-grid;',
            '  width: 28px;',
            '  height: 28px;',
            '  place-items: center;',
            '  border: 0;',
            '  border-radius: 7px;',
            '  color: inherit;',
            '  background: transparent;',
            '  cursor: pointer;',
            '  font: inherit;',
            '  font-size: 18px;',
            '}',
            '.codex-vs-history-close:hover, .codex-vs-history-row:hover {',
            '  background: var(--vscode-list-hoverBackground, rgba(127,127,127,.16));',
            '}',
            '.codex-vs-history-close:focus-visible, .codex-vs-history-row:focus-visible, .codex-vs-history-delete:focus-visible, .codex-vs-history-confirm button:focus-visible, .codex-vs-history-retry:focus-visible, .codex-vs-history-search:focus-visible {',
            '  outline: 2px solid var(--vscode-focusBorder, #007fd4);',
            '  outline-offset: -1px;',
            '}',
            '.codex-vs-history-search-wrap {',
            '  padding: 0 12px 10px;',
            '}',
            '.codex-vs-history-search {',
            '  box-sizing: border-box;',
            '  width: 100%;',
            '  min-height: 34px;',
            '  border: 1px solid var(--vscode-input-border, rgba(127,127,127,.24));',
            '  border-radius: 8px;',
            '  padding: 6px 10px;',
            '  color: var(--vscode-input-foreground, inherit);',
            '  background: var(--vscode-input-background, rgba(127,127,127,.08));',
            '  font: inherit;',
            '  font-size: 13px;',
            '}',
            '.codex-vs-history-content {',
            '  min-height: 128px;',
            '  overflow-y: auto;',
            '  padding: 0 6px 6px;',
            '  scrollbar-gutter: stable;',
            '}',
            '.codex-vs-history-list {',
            '  display: flex;',
            '  flex-direction: column;',
            '  gap: 2px;',
            '}',
            '.codex-vs-history-entry {',
            '  display: flex;',
            '  flex-wrap: wrap;',
            '  align-items: center;',
            '  border-radius: 8px;',
            '}',
            '.codex-vs-history-row {',
            '  display: flex;',
            '  flex: 1 1 0;',
            '  min-width: 0;',
            '  flex-direction: column;',
            '  align-items: stretch;',
            '  gap: 3px;',
            '  border: 0;',
            '  border-radius: 8px;',
            '  padding: 8px 9px;',
            '  color: inherit;',
            '  background: transparent;',
            '  cursor: pointer;',
            '  font: inherit;',
            '  text-align: start;',
            '}',
            '.codex-vs-history-delete, .codex-vs-history-confirm button {',
            '  border: 0;',
            '  border-radius: 6px;',
            '  padding: 5px 7px;',
            '  color: var(--vscode-descriptionForeground, var(--token-description-foreground, #aaa));',
            '  background: transparent;',
            '  cursor: pointer;',
            '  font: inherit;',
            '  font-size: 11px;',
            '}',
            '.codex-vs-history-delete:hover, .codex-vs-history-confirm button:hover {',
            '  color: inherit;',
            '  background: var(--vscode-list-hoverBackground, rgba(127,127,127,.16));',
            '}',
            '.codex-vs-history-delete:disabled, .codex-vs-history-confirm button:disabled {',
            '  cursor: default;',
            '  opacity: .55;',
            '}',
            '.codex-vs-history-confirm {',
            '  display: flex;',
            '  flex: 0 0 100%;',
            '  flex-wrap: wrap;',
            '  align-items: center;',
            '  gap: 4px;',
            '  padding: 0 9px 7px;',
            '  font-size: 11px;',
            '}',
            '.codex-vs-history-confirm button:first-of-type, .codex-vs-history-delete-error {',
            '  color: var(--vscode-errorForeground, #f48771);',
            '}',
            '.codex-vs-history-row-title {',
            '  overflow: hidden;',
            '  font-size: 13px;',
            '  font-weight: 500;',
            '  line-height: 1.35;',
            '  text-overflow: ellipsis;',
            '  white-space: nowrap;',
            '}',
            '.codex-vs-history-row-meta {',
            '  display: flex;',
            '  min-width: 0;',
            '  justify-content: space-between;',
            '  gap: 10px;',
            '  color: var(--vscode-descriptionForeground, var(--token-description-foreground, #aaa));',
            '  font-size: 11px;',
            '}',
            '.codex-vs-history-row-meta span {',
            '  overflow: hidden;',
            '  text-overflow: ellipsis;',
            '  white-space: nowrap;',
            '}',
            '.codex-vs-history-status {',
            '  display: flex;',
            '  min-height: 128px;',
            '  align-items: center;',
            '  justify-content: center;',
            '  gap: 9px;',
            '  padding: 18px;',
            '  color: var(--vscode-descriptionForeground, var(--token-description-foreground, #aaa));',
            '  font-size: 12px;',
            '  text-align: center;',
            '}',
            '.codex-vs-history-status[hidden], .codex-vs-history-list[hidden] {',
            '  display: none;',
            '}',
            '.codex-vs-history-spinner {',
            '  width: 14px;',
            '  height: 14px;',
            '  flex: 0 0 auto;',
            '  border: 2px solid currentColor;',
            '  border-inline-end-color: transparent;',
            '  border-radius: 50%;',
            '  animation: codex-vs-history-spin .8s linear infinite;',
            '}',
            '.codex-vs-history-retry {',
            '  border: 1px solid var(--vscode-button-border, rgba(127,127,127,.3));',
            '  border-radius: 7px;',
            '  padding: 5px 9px;',
            '  color: var(--vscode-button-foreground, inherit);',
            '  background: var(--vscode-button-secondaryBackground, rgba(127,127,127,.14));',
            '  cursor: pointer;',
            '  font: inherit;',
            '}',
            '.codex-vs-history-footer {',
            '  display: flex;',
            '  align-items: center;',
            '  flex-wrap: wrap;',
            '  gap: 7px;',
            '  min-height: 18px;',
            '  padding: 7px 12px 9px;',
            '  border-top: 1px solid var(--vscode-widget-border, rgba(127,127,127,.18));',
            '  color: var(--vscode-descriptionForeground, var(--token-description-foreground, #aaa));',
            '  font-size: 10px;',
            '}',
            '.codex-vs-history-load-more, .codex-vs-history-retry {',
            '  border: 1px solid var(--vscode-button-border, rgba(127,127,127,.3));',
            '  border-radius: 7px;',
            '  padding: 3px 7px;',
            '  color: var(--vscode-button-foreground, inherit);',
            '  background: var(--vscode-button-secondaryBackground, rgba(127,127,127,.14));',
            '  cursor: pointer;',
            '  font: inherit;',
            '  font-size: 10px;',
            '}',
            '.codex-vs-history-load-more:disabled { cursor: default; opacity: .6; }',
            '@keyframes codex-vs-history-spin { to { transform: rotate(360deg); } }',
            '@media (prefers-reduced-motion: reduce) { .codex-vs-history-spinner { animation: none; } }',
            '@media (max-width: 340px) {',
            '  #codex-vs-recent-history-panel { inset-inline: 6px; width: auto; }',
            '}'
        ].join('\n');
        (document.head || document.documentElement).appendChild(style);
    }

    let recentHistoryRoot = null;
    let recentHistoryTrigger = null;
    let recentHistoryRequestId = null;
    let recentHistoryRequestSequence = 0;
    let recentHistoryItems = [];
    let recentHistoryItemsSearchTerm = '';
    let recentHistoryLoading = false;
    let recentHistoryError = false;
    let recentHistoryHasMore = false;
    let recentHistoryNextCursor = null;
    let recentHistoryRequestCursor = null;
    let recentHistoryRequestAppend = false;
    let recentHistoryRequestWorkspaceEpoch = null;
    let recentHistoryRequestSearchTerm = '';
    let recentHistoryElements = null;
    let recentHistorySearchTerm = '';
    let recentHistorySearchDebounceTimer = null;
    let recentHistoryWorkspaceEpoch = 0;
    let recentHistoryCache = null;
    let recentHistoryPrefetchRequestId = null;
    let recentHistoryPrefetchWorkspaceEpoch = null;
    let recentHistoryPrefetchAttemptedWorkspaceEpoch = null;
    let recentHistoryArchiveRequestId = null;
    let recentHistoryArchiveThreadId = null;
    let recentHistoryArchiveWorkspaceEpoch = null;
    let recentHistoryArchiveConfirmId = null;
    let recentHistoryArchiveErrorId = null;

    function normalizeHistorySearchValue(value) {
        const text = String(value || '').toLowerCase();
        try {
            return text.normalize('NFD').replace(/[\u0300-\u036f]/g, '');
        } catch {
            return text;
        }
    }

    function normalizeRecentHistorySearchTerm(value) {
        return String(value || '').trim().slice(0, 240);
    }

    function formatHistoryTimestamp(value) {
        let timestamp = value;
        if (typeof timestamp === 'string' && /^\d+(?:\.\d+)?$/.test(timestamp)) {
            timestamp = Number(timestamp);
        }
        if (typeof timestamp === 'number' && Number.isFinite(timestamp) && Math.abs(timestamp) < 100000000000) {
            timestamp *= 1000;
        }
        const date = new Date(timestamp);
        if (!Number.isFinite(date.getTime())) return '';
        try {
            return date.toLocaleString(undefined, { dateStyle: 'short', timeStyle: 'short' });
        } catch {
            return date.toLocaleString();
        }
    }

    function cloneHistoryItems(items) {
        return items.slice(0, 50).map(function (item) {
            return {
                id: item.id,
                title: item.title,
                workspaceName: item.workspaceName,
                updatedAt: item.updatedAt
            };
        });
    }

    function restoreRecentHistoryCache() {
        if (!recentHistoryCache || recentHistoryCache.workspaceEpoch !== recentHistoryWorkspaceEpoch) return;
        recentHistoryItems = cloneHistoryItems(recentHistoryCache.items);
        recentHistoryItemsSearchTerm = '';
        recentHistoryHasMore = recentHistoryCache.hasMore;
        recentHistoryNextCursor = recentHistoryCache.nextCursor;
    }

    function cacheRecentHistoryFirstPage() {
        if (recentHistoryRequestAppend || recentHistoryRequestSearchTerm.length > 0) return;
        recentHistoryCache = {
            workspaceEpoch: recentHistoryWorkspaceEpoch,
            items: cloneHistoryItems(recentHistoryItems),
            hasMore: recentHistoryHasMore,
            nextCursor: recentHistoryNextCursor
        };
    }

    function invalidateRecentHistoryCache() {
        recentHistoryWorkspaceEpoch++;
        recentHistoryCache = null;
        recentHistoryPrefetchRequestId = null;
        recentHistoryPrefetchWorkspaceEpoch = null;
        recentHistoryPrefetchAttemptedWorkspaceEpoch = null;
    }

    function startRecentHistoryPrefetch() {
        if (recentHistoryPrefetchAttemptedWorkspaceEpoch === recentHistoryWorkspaceEpoch) return;
        recentHistoryPrefetchAttemptedWorkspaceEpoch = recentHistoryWorkspaceEpoch;
        recentHistoryPrefetchWorkspaceEpoch = recentHistoryWorkspaceEpoch;
        recentHistoryPrefetchRequestId = appSessionId + '-history-' + (++recentHistoryRequestSequence);
        postToHost({
            type: 'recent-history-request',
            requestId: recentHistoryPrefetchRequestId,
            cursor: null
        });
    }

    function closeRecentHistory(restoreFocus) {
        const root = recentHistoryRoot;
        const trigger = recentHistoryTrigger;
        recentHistoryRoot = null;
        recentHistoryTrigger = null;
        recentHistoryRequestId = null;
        recentHistoryItems = [];
        recentHistoryItemsSearchTerm = '';
        recentHistoryLoading = false;
        recentHistoryError = false;
        recentHistoryHasMore = false;
        recentHistoryNextCursor = null;
        recentHistoryRequestCursor = null;
        recentHistoryRequestAppend = false;
        recentHistoryRequestWorkspaceEpoch = null;
        recentHistoryRequestSearchTerm = '';
        recentHistorySearchTerm = '';
        recentHistoryArchiveConfirmId = null;
        recentHistoryArchiveErrorId = null;
        if (recentHistorySearchDebounceTimer !== null) {
            clearTimeout(recentHistorySearchDebounceTimer);
            recentHistorySearchDebounceTimer = null;
        }
        recentHistoryElements = null;
        if (root) root.remove();
        if (restoreFocus !== false && trigger && trigger.isConnected) {
            try {
                trigger.focus();
            } catch {
                // no-op
            }
        }
    }

    function requestRecentHistory(cursor, append, searchTerm) {
        if (!recentHistoryRoot || recentHistoryLoading) return;
        const pageCursor = typeof cursor === 'string' && cursor.length > 0 ? cursor : null;
        const appendPage = append === true;
        const requestSearchTerm = normalizeRecentHistorySearchTerm(searchTerm);
        const canUsePendingPrefetch = !appendPage && pageCursor === null &&
            requestSearchTerm.length === 0 &&
            recentHistoryPrefetchRequestId !== null &&
            recentHistoryPrefetchWorkspaceEpoch === recentHistoryWorkspaceEpoch;
        recentHistoryRequestId = canUsePendingPrefetch
            ? recentHistoryPrefetchRequestId
            : appSessionId + '-history-' + (++recentHistoryRequestSequence);
        if (canUsePendingPrefetch) {
            recentHistoryPrefetchRequestId = null;
            recentHistoryPrefetchWorkspaceEpoch = null;
        }
        recentHistoryRequestCursor = pageCursor;
        recentHistoryRequestAppend = appendPage;
        recentHistoryRequestWorkspaceEpoch = recentHistoryWorkspaceEpoch;
        recentHistoryRequestSearchTerm = requestSearchTerm;
        const preserveVisibleItems = !appendPage && pageCursor === null && recentHistoryItems.length > 0;
        if (!appendPage && !preserveVisibleItems) {
            recentHistoryItems = [];
            recentHistoryHasMore = false;
            recentHistoryNextCursor = null;
        }
        recentHistoryLoading = true;
        recentHistoryError = false;
        renderRecentHistory();
        if (!canUsePendingPrefetch) {
            const message = {
                type: 'recent-history-request',
                requestId: recentHistoryRequestId,
                cursor: pageCursor
            };
            if (requestSearchTerm.length > 0) {
                message.searchTerm = requestSearchTerm;
            }
            postToHost(message);
        }
    }

    function retryRecentHistory() {
        requestRecentHistory(recentHistoryRequestCursor, recentHistoryRequestAppend, recentHistoryRequestSearchTerm);
    }

    function scheduleRecentHistorySearch() {
        if (!recentHistoryElements) return;
        const nextSearchTerm = normalizeRecentHistorySearchTerm(recentHistoryElements.search.value);
        if (nextSearchTerm === recentHistorySearchTerm) {
            renderRecentHistory();
            return;
        }
        recentHistorySearchTerm = nextSearchTerm;
        recentHistoryArchiveConfirmId = null;
        recentHistoryArchiveErrorId = null;
        if (recentHistoryRequestId !== null && recentHistoryRequestSearchTerm !== nextSearchTerm) {
            recentHistoryRequestId = null;
            recentHistoryRequestWorkspaceEpoch = null;
            recentHistoryLoading = false;
            recentHistoryError = false;
        }
        if (recentHistorySearchDebounceTimer !== null) {
            clearTimeout(recentHistorySearchDebounceTimer);
        }
        if (nextSearchTerm.length === 0) {
            restoreRecentHistoryCache();
        }
        renderRecentHistory();
        recentHistorySearchDebounceTimer = setTimeout(function () {
            recentHistorySearchDebounceTimer = null;
            if (recentHistoryRoot) {
                requestRecentHistory(null, false, recentHistorySearchTerm);
            }
        }, 200);
    }

    function renderRecentHistory() {
        if (!recentHistoryElements) return;
        const strings = getNavigationStrings();
        const elements = recentHistoryElements;
        elements.status.replaceChildren();
        elements.list.replaceChildren();
        elements.footer.textContent = '';

        if (recentHistoryLoading && recentHistoryItems.length === 0) {
            const spinner = document.createElement('span');
            spinner.className = 'codex-vs-history-spinner';
            spinner.setAttribute('aria-hidden', 'true');
            const label = document.createElement('span');
            label.textContent = strings.historyLoading;
            elements.status.appendChild(spinner);
            elements.status.appendChild(label);
            elements.status.hidden = false;
            elements.list.hidden = true;
            return;
        }

        if (recentHistoryError && recentHistoryItems.length === 0) {
            const label = document.createElement('span');
            label.textContent = strings.historyError;
            const retry = document.createElement('button');
            retry.type = 'button';
            retry.className = 'codex-vs-history-retry';
            retry.textContent = strings.historyRetry;
            retry.addEventListener('click', retryRecentHistory);
            elements.status.appendChild(label);
            elements.status.appendChild(retry);
            elements.status.hidden = false;
            elements.list.hidden = true;
            return;
        }

        const queryValue = recentHistorySearchTerm;
        const filteredItems = recentHistoryItemsSearchTerm === queryValue
            ? recentHistoryItems
            : recentHistoryItems.filter(function (item) {
                const normalizedQuery = normalizeHistorySearchValue(queryValue);
                return normalizedQuery.length === 0 ||
                    normalizeHistorySearchValue(item.title).includes(normalizedQuery) ||
                    normalizeHistorySearchValue(item.workspaceName).includes(normalizedQuery);
            });

        if (filteredItems.length === 0) {
            elements.status.textContent = strings.historyEmpty;
            elements.status.hidden = false;
            elements.list.hidden = true;
        } else {
            const fragment = document.createDocumentFragment();
            filteredItems.forEach(function (item) {
                const entry = document.createElement('div');
                entry.className = 'codex-vs-history-entry';
                entry.setAttribute('role', 'listitem');
                const row = document.createElement('button');
                row.type = 'button';
                row.className = 'codex-vs-history-row';
                const title = document.createElement('span');
                title.className = 'codex-vs-history-row-title';
                title.textContent = item.title || strings.historyUntitled;
                row.appendChild(title);

                const formattedDate = formatHistoryTimestamp(item.updatedAt);
                if (item.workspaceName || formattedDate) {
                    const metadata = document.createElement('span');
                    metadata.className = 'codex-vs-history-row-meta';
                    const workspace = document.createElement('span');
                    workspace.textContent = item.workspaceName || '';
                    const date = document.createElement('span');
                    date.textContent = formattedDate;
                    metadata.appendChild(workspace);
                    metadata.appendChild(date);
                    row.appendChild(metadata);
                }

                row.addEventListener('click', function () {
                    closeRecentHistory(false);
                    postToHost({
                        type: 'navigate-to-route',
                        path: '/local/' + encodeURIComponent(item.id)
                    });
                });
                entry.appendChild(row);

                const remove = document.createElement('button');
                remove.type = 'button';
                remove.className = 'codex-vs-history-delete';
                remove.setAttribute('aria-label', strings.historyDelete + ': ' + (item.title || strings.historyUntitled));
                remove.textContent = recentHistoryArchiveThreadId === item.id
                    ? strings.historyDeleting : strings.historyDelete;
                remove.disabled = recentHistoryArchiveRequestId !== null;
                remove.addEventListener('click', function () {
                    recentHistoryArchiveConfirmId = item.id;
                    recentHistoryArchiveErrorId = null;
                    renderRecentHistory();
                });
                entry.appendChild(remove);

                if (recentHistoryArchiveConfirmId === item.id) {
                    const confirm = document.createElement('div');
                    confirm.className = 'codex-vs-history-confirm';
                    const prompt = document.createElement('span');
                    prompt.textContent = strings.historyDeleteConfirm;
                    confirm.appendChild(prompt);
                    const archive = document.createElement('button');
                    archive.type = 'button';
                    archive.textContent = strings.historyDeleteAction;
                    archive.disabled = recentHistoryArchiveRequestId !== null;
                    archive.addEventListener('click', function () {
                        if (recentHistoryArchiveRequestId !== null) return;
                        recentHistoryArchiveRequestId = appSessionId + '-archive-' + (++recentHistoryRequestSequence);
                        recentHistoryArchiveThreadId = item.id;
                        recentHistoryArchiveWorkspaceEpoch = recentHistoryWorkspaceEpoch;
                        postToHost({
                            type: 'recent-history-archive',
                            requestId: recentHistoryArchiveRequestId,
                            threadId: item.id
                        });
                        renderRecentHistory();
                    });
                    confirm.appendChild(archive);
                    const cancel = document.createElement('button');
                    cancel.type = 'button';
                    cancel.textContent = strings.historyDeleteCancel;
                    cancel.disabled = recentHistoryArchiveRequestId !== null;
                    cancel.addEventListener('click', function () {
                        recentHistoryArchiveConfirmId = null;
                        renderRecentHistory();
                    });
                    confirm.appendChild(cancel);
                    entry.appendChild(confirm);
                }
                if (recentHistoryArchiveErrorId === item.id) {
                    const error = document.createElement('div');
                    error.className = 'codex-vs-history-confirm codex-vs-history-delete-error';
                    error.setAttribute('role', 'alert');
                    error.textContent = strings.historyDeleteError;
                    entry.appendChild(error);
                }
                fragment.appendChild(entry);
            });
            elements.list.appendChild(fragment);
            elements.status.hidden = true;
            elements.list.hidden = false;
        }

        const count = recentHistoryItems.length;
        const countLabel = document.createElement('span');
        countLabel.textContent = count + ' ' +
            (count === 1 ? strings.historyLocalSingular : strings.historyLocalPlural) +
            (recentHistoryHasMore ? ' ' + strings.historyMoreAvailable : '');
        elements.footer.appendChild(countLabel);
        if (recentHistoryHasMore && recentHistoryItemsSearchTerm === queryValue) {
            const loadMore = document.createElement('button');
            loadMore.type = 'button';
            loadMore.className = 'codex-vs-history-load-more';
            loadMore.textContent = recentHistoryLoading && recentHistoryRequestAppend
                ? strings.historyLoadingMore
                : strings.historyLoadMore;
            loadMore.disabled = recentHistoryLoading;
            loadMore.addEventListener('click', function () {
                requestRecentHistory(recentHistoryNextCursor, true, recentHistoryItemsSearchTerm);
            });
            elements.footer.appendChild(loadMore);
        }
        if (recentHistoryError && count > 0) {
            const retry = document.createElement('button');
            retry.type = 'button';
            retry.className = 'codex-vs-history-retry';
            retry.textContent = strings.historyRetry;
            retry.addEventListener('click', retryRecentHistory);
            elements.footer.appendChild(retry);
        }
    }

    function openRecentHistory() {
        if (recentHistoryRoot || !document.body) return;
        installHostUiStyles();
        const strings = getNavigationStrings();
        const root = document.createElement('div');
        root.id = 'codex-vs-recent-history';

        const panel = document.createElement('section');
        panel.id = 'codex-vs-recent-history-panel';
        panel.setAttribute('role', 'dialog');
        panel.setAttribute('aria-modal', 'true');
        panel.setAttribute('aria-labelledby', 'codex-vs-recent-history-title');

        const header = document.createElement('header');
        header.className = 'codex-vs-history-header';
        const title = document.createElement('h2');
        title.id = 'codex-vs-recent-history-title';
        title.className = 'codex-vs-history-title';
        title.textContent = strings.historyTitle;
        const close = document.createElement('button');
        close.type = 'button';
        close.className = 'codex-vs-history-close';
        close.setAttribute('aria-label', strings.historyClose);
        close.title = strings.historyClose;
        close.textContent = '\u00d7';
        close.addEventListener('click', function () { closeRecentHistory(true); });
        header.appendChild(title);
        header.appendChild(close);

        const searchWrap = document.createElement('div');
        searchWrap.className = 'codex-vs-history-search-wrap';
        const search = document.createElement('input');
        search.type = 'search';
        search.className = 'codex-vs-history-search';
        search.placeholder = strings.historySearch;
        search.setAttribute('aria-label', strings.historySearch);
        search.addEventListener('input', scheduleRecentHistorySearch);
        searchWrap.appendChild(search);

        const content = document.createElement('div');
        content.className = 'codex-vs-history-content';
        const status = document.createElement('div');
        status.className = 'codex-vs-history-status';
        status.setAttribute('role', 'status');
        const list = document.createElement('div');
        list.className = 'codex-vs-history-list';
        list.setAttribute('role', 'list');
        content.appendChild(status);
        content.appendChild(list);

        const footer = document.createElement('footer');
        footer.className = 'codex-vs-history-footer';
        panel.appendChild(header);
        panel.appendChild(searchWrap);
        panel.appendChild(content);
        panel.appendChild(footer);
        root.appendChild(panel);
        root.addEventListener('pointerdown', function (event) {
            if (event.target === root) closeRecentHistory(true);
        });
        root.addEventListener('keydown', function (event) {
            if (event.key === 'Escape') {
                event.preventDefault();
                closeRecentHistory(true);
            }
        });

        recentHistoryRoot = root;
        recentHistoryElements = { search: search, status: status, list: list, footer: footer };
        recentHistorySearchTerm = '';
        restoreRecentHistoryCache();
        document.body.appendChild(root);
        requestRecentHistory(null, false, '');
        window.requestAnimationFrame(function () {
            if (recentHistoryRoot === root) search.focus();
        });
    }

    function handleRecentHistoryResponse(data) {
        if (recentHistoryPrefetchRequestId !== null && data.requestId === recentHistoryPrefetchRequestId) {
            const prefetchWorkspaceEpoch = recentHistoryPrefetchWorkspaceEpoch;
            recentHistoryPrefetchRequestId = null;
            recentHistoryPrefetchWorkspaceEpoch = null;
            if (prefetchWorkspaceEpoch === recentHistoryWorkspaceEpoch &&
                !(typeof data.error === 'string' && data.error.length > 0)) {
                const pageItems = normalizeRecentHistoryItems(data.items);
                recentHistoryCache = {
                    workspaceEpoch: recentHistoryWorkspaceEpoch,
                    items: cloneHistoryItems(pageItems),
                    hasMore: data.hasMore === true && typeof data.nextCursor === 'string' && data.nextCursor.length > 0,
                    nextCursor: typeof data.nextCursor === 'string' && data.nextCursor.length > 0 ? data.nextCursor : null
                };
            }
            return;
        }
        if (!recentHistoryRoot ||
            data.requestId !== recentHistoryRequestId ||
            recentHistoryRequestWorkspaceEpoch !== recentHistoryWorkspaceEpoch) return;
        recentHistoryLoading = false;
        recentHistoryError = typeof data.error === 'string' && data.error.length > 0;
        if (recentHistoryError) {
            renderRecentHistory();
            return;
        }
        const pageItems = normalizeRecentHistoryItems(data.items);
        const mergedItems = recentHistoryRequestAppend ? recentHistoryItems.concat(pageItems) : pageItems;
        const seen = new Set();
        recentHistoryItems = mergedItems.filter(function (item) {
            if (seen.has(item.id)) return false;
            seen.add(item.id);
            return true;
        });
        recentHistoryNextCursor = typeof data.nextCursor === 'string' && data.nextCursor.length > 0
            ? data.nextCursor
            : null;
        recentHistoryHasMore = data.hasMore === true && recentHistoryNextCursor !== null;
        recentHistoryItemsSearchTerm = recentHistoryRequestSearchTerm;
        cacheRecentHistoryFirstPage();
        renderRecentHistory();
    }

    function handleRecentHistoryArchiveResponse(data) {
        if (data.requestId !== recentHistoryArchiveRequestId ||
            data.threadId !== recentHistoryArchiveThreadId) return;
        const threadId = recentHistoryArchiveThreadId;
        const sameWorkspace = recentHistoryArchiveWorkspaceEpoch === recentHistoryWorkspaceEpoch;
        recentHistoryArchiveRequestId = null;
        recentHistoryArchiveThreadId = null;
        recentHistoryArchiveWorkspaceEpoch = null;
        recentHistoryArchiveConfirmId = null;
        if (!sameWorkspace) return;
        if (data.ok !== true) {
            recentHistoryArchiveErrorId = threadId;
            renderRecentHistory();
            return;
        }
        recentHistoryArchiveErrorId = null;
        recentHistoryItems = recentHistoryItems.filter(function (item) { return item.id !== threadId; });
        invalidateRecentHistoryCache();
        recentHistoryRequestId = null;
        recentHistoryRequestWorkspaceEpoch = null;
        recentHistoryLoading = false;
        if (recentHistoryRoot) {
            requestRecentHistory(null, false, recentHistorySearchTerm);
        } else {
            startRecentHistoryPrefetch();
        }
    }

    function normalizeRecentHistoryItems(items) {
        return Array.isArray(items) ? items.slice(0, 50).map(function (item) {
            if (!item || typeof item !== 'object' || typeof item.id !== 'string') return null;
            return {
                id: item.id.slice(0, 256),
                title: typeof item.title === 'string' ? item.title.slice(0, 240) : '',
                workspaceName: typeof item.workspaceName === 'string' ? item.workspaceName.slice(0, 260) : '',
                updatedAt: typeof item.updatedAt === 'string' || typeof item.updatedAt === 'number' ? item.updatedAt : null
            };
        }).filter(function (item) { return item && item.id.length > 0; }) : [];
    }

    function findRecentHistoryTrigger(target) {
        if (!(target instanceof Element)) return null;
        const button = target.closest('button');
        if (!button || button.closest('#codex-vs-recent-history')) return null;
        const label = normalizeHistorySearchValue(button.getAttribute('aria-label'));
        const knownLabel = label.includes('recent tasks') ||
            label.includes('task history') ||
            label.includes('tarefas recentes') ||
            label.includes('historico de tarefas') ||
            label.includes('tareas recientes') ||
            label.includes('taches recentes') ||
            label.includes('aufgaben');
        const iconPath = button.querySelector('svg[viewBox="0 0 21 21"] path');
        const pathData = iconPath && iconPath.getAttribute('d');
        const knownIcon = typeof pathData === 'string' && pathData.startsWith('M17.1348 10.5455');
        return knownLabel || knownIcon ? button : null;
    }

    function interceptRecentHistoryTrigger(event) {
        const trigger = findRecentHistoryTrigger(event.target);
        if (!trigger) return;
        event.preventDefault();
        event.stopImmediatePropagation();
        recentHistoryTrigger = trigger;
        openRecentHistory();
    }

    document.addEventListener('pointerdown', interceptRecentHistoryTrigger, true);
    document.addEventListener('click', interceptRecentHistoryTrigger, true);
    window.addEventListener('open-recent-tasks-menu', function (event) {
        event.preventDefault();
        event.stopImmediatePropagation();
        openRecentHistory();
    }, true);

    let hostThemeVariant;
    const themeVariantListeners = new Set();

    function normalizeThemeVariant(value) {
        return value === 'light' || value === 'dark' ? value : undefined;
    }

    function getSystemThemeVariant() {
        const hostVariant = normalizeThemeVariant(hostThemeVariant);
        return hostVariant || (window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light');
    }

    function notifyThemeVariantListeners() {
        const variant = getSystemThemeVariant();
        themeVariantListeners.forEach(function (listener) {
            listener(variant);
        });
    }

    function relayHostMessageForOfficialRouter(event, data) {
        if (data.__codexShimRelayed || event.origin === window.location.origin) {
            return;
        }
        const relayedData = Object.assign({}, data, { __codexShimRelayed: true });
        window.dispatchEvent(new MessageEvent('message', {
            data: relayedData,
            origin: window.location.origin,
            source: window
        }));
    }

    let nextCapnRpcImportId = 1;
    const pendingCapnRpcInstructions = new Map();
    const capnRpcServices = {};

    function postCapnRpcMessage(message) {
        window.postMessage({
            type: 'vscode-capn-rpc-message',
            message: JSON.stringify(message)
        }, window.location.origin || '*');
    }

    function devalueCapnRpcValue(value) {
        if (value === undefined) {
            return ['undefined'];
        }
        if (value === null || typeof value === 'boolean' || typeof value === 'number' || typeof value === 'string') {
            return value;
        }
        if (Array.isArray(value)) {
            return [value.map(devalueCapnRpcValue)];
        }
        if (typeof value === 'object') {
            const result = {};
            for (const key in value) {
                if (Object.prototype.hasOwnProperty.call(value, key)) {
                    result[key] = devalueCapnRpcValue(value[key]);
                }
            }
            return result;
        }
        return ['undefined'];
    }

    function getCapnRpcValueForPath(path) {
        if (!Array.isArray(path) || path.length === 0) {
            return { services: capnRpcServices };
        }
        if (path[0] !== 'services') {
            return undefined;
        }
        let value = capnRpcServices;
        for (const key of path.slice(1)) {
            if (!value || typeof value !== 'object' || !Object.prototype.hasOwnProperty.call(value, key)) {
                return undefined;
            }
            value = value[key];
        }
        return value;
    }

    function getCapnRpcPath(instruction) {
        if (Array.isArray(instruction) && instruction[0] === 'pipeline' && Array.isArray(instruction[2])) {
            return instruction[2];
        }
        return [];
    }

    function resolveCapnRpcImport(importId, instruction) {
        const value = getCapnRpcValueForPath(getCapnRpcPath(instruction));
        postCapnRpcMessage(['resolve', importId, devalueCapnRpcValue(value)]);
    }

    function handleCapnRpcMessage(msg) {
        if (!msg || typeof msg !== 'object' || msg.type !== 'vscode-capn-rpc-message' || typeof msg.message !== 'string') {
            return false;
        }
        let payload;
        try {
            payload = JSON.parse(msg.message);
        } catch {
            return true;
        }
        if (!Array.isArray(payload)) {
            return true;
        }
        switch (payload[0]) {
            case 'push': {
                pendingCapnRpcInstructions.set(nextCapnRpcImportId++, payload[1]);
                return true;
            }
            case 'pull': {
                const importId = payload[1];
                if (typeof importId !== 'number') {
                    return true;
                }
                resolveCapnRpcImport(importId, pendingCapnRpcInstructions.get(importId));
                return true;
            }
            case 'release': {
                const importId = payload[1];
                if (typeof importId === 'number') {
                    pendingCapnRpcInstructions.delete(importId);
                }
                return true;
            }
            case 'abort':
                pendingCapnRpcInstructions.clear();
                return true;
            default:
                return true;
        }
    }

    window.addEventListener('error', function (event) {
        logError('webview-error', event.error || event.message);
    });

    window.addEventListener('unhandledrejection', function (event) {
        logError('webview-unhandled-rejection', event.reason);
    });

    window.addEventListener('message', function (event) {
        const data = event.data;
        if (!data || typeof data !== 'object' || typeof data.type !== 'string') {
            return;
        }
        if (data.channel === CHANNEL) {
            return;
        }
        if (data.type === 'theme-updated') {
            const nextVariant = normalizeThemeVariant((data.theme && data.theme.variant) || data.variant);
            if (nextVariant && nextVariant !== hostThemeVariant) {
                hostThemeVariant = nextVariant;
                notifyThemeVariantListeners();
            }
        }
        if (data.type === 'diagnostic-logging-changed') {
            diagnosticLoggingEnabled = data.enabled === true;
        }
        if (data.type === 'shared-object-updated' && typeof data.key === 'string') {
            sharedObjects.set(data.key, data.value);
        }
        if (data.type === 'recent-history-response') {
            handleRecentHistoryResponse(data);
        } else if (data.type === 'recent-history-archive-response') {
            handleRecentHistoryArchiveResponse(data);
        } else if (data.type === 'active-workspace-roots-updated') {
            invalidateRecentHistoryCache();
            recentHistoryArchiveRequestId = null;
            recentHistoryArchiveThreadId = null;
            recentHistoryArchiveWorkspaceEpoch = null;
            closeRecentHistory(false);
            startRecentHistoryPrefetch();
        } else if (data.type === 'navigate-to-route' && recentHistoryRoot) {
            closeRecentHistory(false);
        }
        window.dispatchEvent(new CustomEvent('codex-host-message', { detail: data }));
        relayHostMessageForOfficialRouter(event, data);
    });

    const vscodeApi = {
        postMessage: function (msg) {
            if (handleCapnRpcMessage(msg)) {
                return;
            }
            postToHost(msg);
        },
        getState: function () {
            return state;
        },
        setState: function (next) {
            state = next;
        }
    };

    window.acquireVsCodeApi = function () {
        return vscodeApi;
    };

    const bridgeDefaults = {
        getSharedObjectSnapshotValue: function (key) {
            return sharedObjects.get(key);
        },
        getBuildFlavor: function () {
            return readMeta('codex-build-flavor') || BUILD_FLAVOR;
        },
        getAppSessionId: function () {
            return appSessionId;
        },
        getSentryInitOptions: function () {
            return {
                appVersion: EXTENSION_VERSION,
                buildFlavor: readMeta('codex-build-flavor') || BUILD_FLAVOR,
                codexAppSessionId: appSessionId,
                extensionVersion: EXTENSION_VERSION
            };
        },
        getPathForFile: function (file) {
            return file && (file.path || file.fsPath || file.name) || null;
        },
        getSystemThemeVariant: getSystemThemeVariant,
        subscribeToSystemThemeVariant: function (listener) {
            themeVariantListeners.add(listener);
            if (!window.matchMedia) {
                return function () {
                    themeVariantListeners.delete(listener);
                };
            }
            const media = window.matchMedia('(prefers-color-scheme: dark)');
            const wrapped = function () {
                if (!hostThemeVariant) {
                    listener(getSystemThemeVariant());
                }
            };
            if (media.addEventListener) {
                media.addEventListener('change', wrapped);
                return function () {
                    themeVariantListeners.delete(listener);
                    media.removeEventListener('change', wrapped);
                };
            }
            media.addListener(wrapped);
            return function () {
                themeVariantListeners.delete(listener);
                media.removeListener(wrapped);
            };
        }
    };

    window.electronBridge = Object.assign({}, bridgeDefaults, window.electronBridge || {});
    window.__CODEX_THEIA_BRIDGE__ = { appSessionId: appSessionId, buildFlavor: BUILD_FLAVOR, webviewId: webviewId };
    postToHost({ type: 'webview-ready' });
    startRecentHistoryPrefetch();
})();
