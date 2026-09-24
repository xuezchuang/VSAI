import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';

class FakeElement {
    constructor(tagName) {
        this.tagName = tagName.toUpperCase();
        this.children = [];
        this.parentNode = null;
        this.listeners = new Map();
        this.attributes = new Map();
        this.dataset = {};
        this.hidden = false;
        this.value = '';
        this.textContent = '';
        this.type = '';
        this.disabled = false;
    }

    get isConnected() {
        return this.parentNode !== null;
    }

    set id(value) {
        this._id = value;
    }

    get id() {
        return this._id || '';
    }

    set className(value) {
        this._className = value;
    }

    get className() {
        return this._className || '';
    }

    set textContent(value) {
        this._textContent = String(value ?? '');
        this.children = [];
    }

    get textContent() {
        return (this._textContent || '') + this.children.map(child => child.textContent).join('');
    }

    appendChild(child) {
        if (child.tagName === '#DOCUMENT-FRAGMENT') {
            for (const nested of [...child.children]) this.appendChild(nested);
            child.children = [];
            return child;
        }
        child.parentNode = this;
        this.children.push(child);
        return child;
    }

    remove() {
        if (!this.parentNode) return;
        this.parentNode.children = this.parentNode.children.filter(child => child !== this);
        this.parentNode = null;
    }

    replaceChildren(...children) {
        this.children = [];
        for (const child of children) this.appendChild(child);
    }

    setAttribute(name, value) {
        this.attributes.set(name, String(value));
    }

    getAttribute(name) {
        return this.attributes.get(name) ?? null;
    }

    addEventListener(type, listener) {
        const listeners = this.listeners.get(type) ?? [];
        listeners.push(listener);
        this.listeners.set(type, listeners);
    }

    dispatchEvent(event) {
        event.target ??= this;
        for (const listener of this.listeners.get(event.type) ?? []) listener(event);
        return true;
    }

    focus() {}

    closest() {
        return null;
    }

    querySelector() {
        return null;
    }
}

class FakeDocument {
    constructor() {
        this.listeners = new Map();
        this.documentElement = new FakeElement('html');
        this.documentElement.lang = 'en-US';
        this.head = new FakeElement('head');
        this.body = new FakeElement('body');
    }

    createElement(tagName) {
        return new FakeElement(tagName);
    }

    createDocumentFragment() {
        return new FakeElement('#document-fragment');
    }

    getElementById(id) {
        return findNode(this.body, node => node.id === id) ?? findNode(this.head, node => node.id === id);
    }

    querySelector(selector) {
        if (selector.startsWith('meta[name="')) return null;
        return null;
    }

    addEventListener(type, listener) {
        const listeners = this.listeners.get(type) ?? [];
        listeners.push(listener);
        this.listeners.set(type, listeners);
    }

    dispatchEvent(event) {
        for (const listener of this.listeners.get(event.type) ?? []) listener(event);
    }
}

function findNode(node, predicate) {
    if (predicate(node)) return node;
    for (const child of node.children) {
        const match = findNode(child, predicate);
        if (match) return match;
    }
    return null;
}

function findByClass(node, className) {
    const matches = [];
    walk(node, current => {
        if (current.className.split(/\s+/).includes(className)) matches.push(current);
    });
    return matches;
}

function walk(node, visit) {
    visit(node);
    for (const child of node.children) walk(child, visit);
}

function event(type, data = {}) {
    return {
        type,
        ...data,
        preventDefault() {},
        stopImmediatePropagation() {}
    };
}

const document = new FakeDocument();
const messages = [];
const windowListeners = new Map();
const window = {
    document,
    location: { origin: 'https://codex-shell.local' },
    navigator: { language: 'en-US' },
    parent: { postMessage: message => messages.push(message) },
    addEventListener(type, listener) {
        const listeners = windowListeners.get(type) ?? [];
        listeners.push(listener);
        windowListeners.set(type, listeners);
    },
    dispatchEvent(value) {
        const current = typeof value === 'string' ? event(value) : value;
        for (const listener of windowListeners.get(current.type) ?? []) listener(current);
    },
    requestAnimationFrame(callback) {
        callback();
    }
};

const context = vm.createContext({
    window,
    document,
    navigator: window.navigator,
    Element: FakeElement,
    URLSearchParams,
    CustomEvent: class CustomEvent { constructor(type, init = {}) { this.type = type; Object.assign(this, init); } },
    MessageEvent: class MessageEvent { constructor(type, init = {}) { this.type = type; Object.assign(this, init); } },
    console,
    setTimeout,
    clearTimeout
});
const shimPath = new URL('../CodexVsix/UI/CodexWebview/codex-acquire-vscode-api-shim.js', import.meta.url);
vm.runInContext(fs.readFileSync(shimPath, 'utf8'), context, { filename: shimPath.pathname });

function hostMessages(type) {
    return messages.map(item => item.message).filter(item => item?.type === type);
}

function sendHistoryResponse(request, body) {
    window.dispatchEvent(event('message', { data: { type: 'recent-history-response', requestId: request.requestId, ...body } }));
}

function openHistory() {
    window.dispatchEvent(event('open-recent-tasks-menu'));
    return document.getElementById('codex-vs-recent-history');
}

function waitForSearchDebounce() {
    return new Promise(resolve => setTimeout(resolve, 230));
}

const ready = hostMessages('webview-ready');
assert.equal(ready.length, 1, 'shim should announce webview readiness');

let requests = hostMessages('recent-history-request');
assert.equal(requests.length, 1, 'webview readiness should start one bounded history prefetch');
assert.equal(requests[0].cursor, null, 'the prefetch must load the first history page');
sendHistoryResponse(requests[0], {
    items: [{ id: 'a', title: 'A' }, { id: 'b', title: 'B' }],
    nextCursor: 'cursor-1',
    hasMore: true
});

let root = openHistory();
requests = hostMessages('recent-history-request');
assert.equal(requests.length, 2, 'opening after prefetch should refresh in the background');
assert.deepEqual(
    findByClass(root, 'codex-vs-history-row').map(row => row.children[0].textContent),
    ['A', 'B'],
    'the first opened history panel should render its completed prefetch immediately'
);
assert.equal(requests.at(-1).cursor, null, 'first history page must use a null cursor');
sendHistoryResponse(requests.at(-1), {
    items: [{ id: 'a', title: 'A' }, { id: 'b', title: 'B' }],
    nextCursor: 'cursor-1',
    hasMore: true
});
assert.equal(findByClass(root, 'codex-vs-history-row').length, 2);
const footer = document.getElementById('codex-vs-recent-history-panel').children.at(-1);
assert.match(footer.textContent, /More conversations are available/);
assert.doesNotMatch(footer.textContent, /50 most recent/);
let loadMore = findByClass(root, 'codex-vs-history-load-more')[0];
assert.ok(loadMore, 'a next cursor should render a load-more button');
loadMore.dispatchEvent(event('click'));
requests = hostMessages('recent-history-request');
assert.equal(requests.at(-1).cursor, 'cursor-1');
assert.equal(findByClass(root, 'codex-vs-history-row').length, 2, 'loaded rows remain visible while paging');
sendHistoryResponse(requests.at(-1), { error: 'page failed', items: [], nextCursor: null, hasMore: false });
assert.equal(findByClass(root, 'codex-vs-history-row').length, 2, 'a failed page keeps the first page visible');
assert.ok(findByClass(root, 'codex-vs-history-retry').length > 0);
findByClass(root, 'codex-vs-history-retry')[0].dispatchEvent(event('click'));
requests = hostMessages('recent-history-request');
assert.equal(requests.at(-1).cursor, 'cursor-1', 'retry should reuse the failed page cursor');
sendHistoryResponse(requests.at(-1), {
    items: [{ id: 'b', title: 'B duplicate' }, { id: 'c', title: 'C' }],
    nextCursor: null,
    hasMore: false
});
assert.deepEqual(findByClass(root, 'codex-vs-history-row').map(row => row.children[0].textContent), ['A', 'B', 'C']);
assert.equal(findByClass(root, 'codex-vs-history-load-more').length, 0);
findByClass(root, 'codex-vs-history-row')[2].dispatchEvent(event('click'));
assert.equal(hostMessages('navigate-to-route').at(-1).path, '/local/c', 'history rows should preserve local resume navigation');

root = openHistory();
requests = hostMessages('recent-history-request');
const staleRequest = requests.at(-1);
assert.deepEqual(
    findByClass(root, 'codex-vs-history-row').map(row => row.children[0].textContent),
    ['A', 'B'],
    'reopening in the same directory should render the bounded cached first page before its refresh finishes'
);
assert.equal(staleRequest.cursor, null, 'the background refresh must start from the first page');
sendHistoryResponse(staleRequest, { error: 'refresh failed', items: [], hasMore: false, nextCursor: null });
assert.deepEqual(
    findByClass(root, 'codex-vs-history-row').map(row => row.children[0].textContent),
    ['A', 'B'],
    'a background refresh failure must retain the cached rows'
);
window.dispatchEvent(event('message', { data: { type: 'active-workspace-roots-updated' } }));
assert.equal(document.getElementById('codex-vs-recent-history'), null, 'directory change should close the history dialog');
requests = hostMessages('recent-history-request');
const workspacePrefetchRequest = requests.at(-1);
assert.notEqual(workspacePrefetchRequest.requestId, staleRequest.requestId, 'a directory change should issue a new workspace prefetch');
window.dispatchEvent(event('message', {
    data: { type: 'recent-history-response', requestId: staleRequest.requestId, items: [{ id: 'stale' }] }
}));
root = openHistory();
requests = hostMessages('recent-history-request');
assert.equal(requests.at(-1).requestId, workspacePrefetchRequest.requestId, 'opening during prefetch must reuse the pending request');
assert.equal(requests.at(-1).cursor, null, 'new directory should start a fresh first page');
assert.equal(findByClass(root, 'codex-vs-history-row').length, 0, 'directory changes must not reuse the previous directory cache');
assert.match(findByClass(root, 'codex-vs-history-status')[0].textContent, /Loading history/);
sendHistoryResponse(workspacePrefetchRequest, { items: [{ id: 'new', title: 'New directory' }], hasMore: false, nextCursor: null });
sendHistoryResponse(staleRequest, { items: [{ id: 'stale', title: 'Old directory' }], hasMore: true, nextCursor: 'old-cursor' });
assert.deepEqual(findByClass(root, 'codex-vs-history-row').map(row => row.children[0].textContent), ['New directory']);
findByClass(root, 'codex-vs-history-row')[0].dispatchEvent(event('click'));
root = openHistory();
requests = hostMessages('recent-history-request');
const refreshRequest = requests.at(-1);
assert.deepEqual(
    findByClass(root, 'codex-vs-history-row').map(row => row.children[0].textContent),
    ['New directory'],
    'a cached first page should remain visible while the background refresh is pending'
);
sendHistoryResponse(refreshRequest, { items: [{ id: 'fresh', title: 'Fresh directory' }], hasMore: false, nextCursor: null });
assert.deepEqual(findByClass(root, 'codex-vs-history-row').map(row => row.children[0].textContent), ['Fresh directory']);

const search = findByClass(root, 'codex-vs-history-search')[0];
search.value = 'legacy native match';
search.dispatchEvent(event('input'));
await waitForSearchDebounce();
requests = hostMessages('recent-history-request');
const nativeSearchRequest = requests.at(-1);
assert.equal(nativeSearchRequest.cursor, null, 'a search begins from the first page');
assert.equal(nativeSearchRequest.searchTerm, 'legacy native match', 'the complete query must reach the native history list');
sendHistoryResponse(nativeSearchRequest, {
    items: [{ id: 'native', title: 'Untitled task', workspaceName: 'D:/workspace' }],
    hasMore: true,
    nextCursor: 'native-cursor'
});
assert.deepEqual(
    findByClass(root, 'codex-vs-history-row').map(row => row.children[0].textContent),
    ['Untitled task'],
    'a native search hit must remain visible even when its rendered metadata does not contain the query'
);
loadMore = findByClass(root, 'codex-vs-history-load-more')[0];
loadMore.dispatchEvent(event('click'));
requests = hostMessages('recent-history-request');
const nativeSearchMoreRequest = requests.at(-1);
assert.equal(nativeSearchMoreRequest.cursor, 'native-cursor');
assert.equal(nativeSearchMoreRequest.searchTerm, 'legacy native match', 'search pagination must retain its query');
sendHistoryResponse(nativeSearchMoreRequest, {
    items: [{ id: 'older-native', title: 'Older task', workspaceName: 'D:/workspace' }],
    hasMore: false,
    nextCursor: null
});
assert.deepEqual(
    findByClass(root, 'codex-vs-history-row').map(row => row.children[0].textContent),
    ['Untitled task', 'Older task'],
    'search page append must keep prior native hits visible'
);

search.value = 'obsolete search';
search.dispatchEvent(event('input'));
await waitForSearchDebounce();
requests = hostMessages('recent-history-request');
const obsoleteSearchRequest = requests.at(-1);
search.value = 'current search';
search.dispatchEvent(event('input'));
await waitForSearchDebounce();
requests = hostMessages('recent-history-request');
const currentSearchRequest = requests.at(-1);
assert.equal(currentSearchRequest.searchTerm, 'current search');
sendHistoryResponse(obsoleteSearchRequest, { items: [{ id: 'obsolete', title: 'Obsolete result' }], hasMore: false, nextCursor: null });
sendHistoryResponse(currentSearchRequest, { items: [{ id: 'current', title: 'Current result' }], hasMore: false, nextCursor: null });
assert.deepEqual(findByClass(root, 'codex-vs-history-row').map(row => row.children[0].textContent), ['Current result']);

search.value = '';
search.dispatchEvent(event('input'));
assert.deepEqual(
    findByClass(root, 'codex-vs-history-row').map(row => row.children[0].textContent),
    ['Fresh directory'],
    'clearing a search must immediately restore the unfiltered first-page cache'
);
await waitForSearchDebounce();
requests = hostMessages('recent-history-request');
const unfilteredRefreshRequest = requests.at(-1);
assert.equal(unfilteredRefreshRequest.searchTerm, undefined, 'unfiltered refreshes must not send a search term');
sendHistoryResponse(unfilteredRefreshRequest, { items: [{ id: 'fresh-after-search', title: 'Fresh after search' }], hasMore: false, nextCursor: null });
assert.deepEqual(findByClass(root, 'codex-vs-history-row').map(row => row.children[0].textContent), ['Fresh after search']);

window.dispatchEvent(event('message', { data: { type: 'active-workspace-roots-updated' } }));
root = openHistory();
requests = hostMessages('recent-history-request');
sendHistoryResponse(requests.at(-1), { error: 'failed', items: [], hasMore: false, nextCursor: null });
assert.ok(findByClass(root, 'codex-vs-history-retry').length > 0, 'a failed page should offer retry');
findByClass(root, 'codex-vs-history-retry')[0].dispatchEvent(event('click'));
requests = hostMessages('recent-history-request');
assert.equal(requests.at(-1).cursor, null, 'retry should repeat the failed page');
const requestCountBeforeClose = requests.length;
const closingSearch = findByClass(root, 'codex-vs-history-search')[0];
closingSearch.value = 'cancelled query';
closingSearch.dispatchEvent(event('input'));
findByClass(root, 'codex-vs-history-close')[0].dispatchEvent(event('click'));
await waitForSearchDebounce();
assert.equal(hostMessages('recent-history-request').length, requestCountBeforeClose, 'closing the dialog must cancel a pending search debounce');

console.log('Codex history shim pagination, deduplication, retry, and directory invalidation checks passed.');
