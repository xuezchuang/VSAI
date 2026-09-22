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

const ready = hostMessages('webview-ready');
assert.equal(ready.length, 1, 'shim should announce webview readiness');

let root = openHistory();
let requests = hostMessages('recent-history-request');
assert.equal(requests.length, 1);
assert.equal(requests[0].cursor, null, 'first history page must use a null cursor');
sendHistoryResponse(requests[0], {
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
assert.equal(requests.length, 2);
assert.equal(requests[1].cursor, 'cursor-1');
assert.equal(findByClass(root, 'codex-vs-history-row').length, 2, 'loaded rows remain visible while paging');
sendHistoryResponse(requests[1], { error: 'page failed', items: [], nextCursor: null, hasMore: false });
assert.equal(findByClass(root, 'codex-vs-history-row').length, 2, 'a failed page keeps the first page visible');
assert.ok(findByClass(root, 'codex-vs-history-retry').length > 0);
findByClass(root, 'codex-vs-history-retry')[0].dispatchEvent(event('click'));
requests = hostMessages('recent-history-request');
assert.equal(requests.length, 3);
assert.equal(requests[2].cursor, 'cursor-1', 'retry should reuse the failed page cursor');
sendHistoryResponse(requests[2], {
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
window.dispatchEvent(event('message', { data: { type: 'active-workspace-roots-updated' } }));
assert.equal(document.getElementById('codex-vs-recent-history'), null, 'directory change should close the history dialog');
window.dispatchEvent(event('message', {
    data: { type: 'recent-history-response', requestId: staleRequest.requestId, items: [{ id: 'stale' }] }
}));
root = openHistory();
requests = hostMessages('recent-history-request');
assert.equal(requests.at(-1).cursor, null, 'new directory should start a fresh first page');
sendHistoryResponse(requests.at(-1), { items: [{ id: 'new', title: 'New directory' }], hasMore: false, nextCursor: null });
sendHistoryResponse(staleRequest, { items: [{ id: 'stale', title: 'Old directory' }], hasMore: true, nextCursor: 'old-cursor' });
assert.deepEqual(findByClass(root, 'codex-vs-history-row').map(row => row.children[0].textContent), ['New directory']);

window.dispatchEvent(event('message', { data: { type: 'active-workspace-roots-updated' } }));
root = openHistory();
requests = hostMessages('recent-history-request');
sendHistoryResponse(requests.at(-1), { error: 'failed', items: [], hasMore: false, nextCursor: null });
assert.ok(findByClass(root, 'codex-vs-history-retry').length > 0, 'a failed page should offer retry');
findByClass(root, 'codex-vs-history-retry')[0].dispatchEvent(event('click'));
requests = hostMessages('recent-history-request');
assert.equal(requests.at(-1).cursor, null, 'retry should repeat the failed page');

console.log('Codex history shim pagination, deduplication, retry, and directory invalidation checks passed.');
