import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import vm from 'node:vm';

const root = new URL('../', import.meta.url);
const read = path => readFileSync(new URL(path, root), 'utf8').replaceAll('\r\n', '\n');
const adapter = read('CodexVsix/Services/CodexConversationForkWebViewCompatibility.cs');
const guards = read('CodexVsix/Services/CodexReasoningEffortWebViewCompatibility.cs');
const asset = 'CodexVsix/UI/CodexWebview/webview/assets/app-server-manager-signals-B8yJv95l.js';
const original = read(asset);
const hash = guards.match(/\[CodexConversationForkWebViewCompatibility\.ManagerModule\] = "([a-f0-9]+)"/)[1];
assert.equal(createHash('sha256').update(original).digest('hex'), hash, 'The adapter must pin the frozen module.');

function constant(name) {
    const match = adapter.match(new RegExp(`const string ${name} = @"([\\s\\S]*?)";`));
    assert.ok(match, `Missing adapter constant ${name}`);
    return match[1].replaceAll('""', '"');
}

let adapted = original;
for (const name of ['ForkOptions', 'ForkRequest', 'ForkFromTurn']) {
    const before = constant(name + 'Before');
    assert.equal(adapted.split(before).length - 1, 1, `${name} must match exactly once.`);
    adapted = adapted.replace(before, constant(name + 'After'));
}
const syntax = spawnSync(process.execPath, ['--check', '--input-type=module'], { input: adapted, encoding: 'utf8' });
assert.equal(syntax.status, 0, syntax.stderr || 'Adapted module syntax validation failed.');

function load(source, fromTurn) {
    const begin = source.indexOf('async function bx(');
    const end = source.indexOf('function xx(', begin);
    assert.ok(begin >= 0 && end > begin);
    const annotations = [];
    const hydrations = [];
    const context = vm.createContext({
        f: id => id,
        V: { error: message => { throw new Error(message); } },
        Yf: result => ({ approvalPolicy: result.approvalPolicy, sandboxPolicy: result.sandbox }),
        cm: async (manager, options) => {
            hydrations.push(options);
            const thread = manager.threads.get(options.conversationId);
            thread.turns = structuredClone(manager.persisted.get(options.conversationId));
            thread.resumeState = 'resumed';
        },
        A_: (...args) => annotations.push(args.slice(1)),
        _x: source => source.title,
        xx: () => { throw new Error('Unexpected ephemeral fork.'); },
        Sx: () => { throw new Error('Unexpected rollback hydration.'); }
    });
    return { ...vm.runInContext(source.slice(begin, end) + '\n' + fromTurn + '\n({ bx, Cx })', context), annotations, hydrations };
}

function harness(runtime, { visibleTurns, active = false, failure = null, ignoresCutoff = false } = {}) {
    const history = Array.from({ length: 150 }, (_, i) => ({ turnId: `turn-${i}`, text: `history-${i}` }));
    const source = { id: 'source', title: 'Original title', cwd: 'D:/工程 空格', turns: visibleTurns ?? history };
    const originalSource = structuredClone(source);
    const requests = [];
    const manager = {
        threads: new Map([['source', source]]),
        persisted: new Map([['source', history]]),
        getConversation(id) { return this.threads.get(id); },
        updateConversationState(id, update) { update(this.threads.get(id)); },
        async buildThreadCodexConfig() { return null; },
        async sendRequest(method, params) {
            requests.push({ method, params: structuredClone(params) });
            if (method === 'thread/rollback') throw new Error('paginated threads do not support thread/rollback');
            assert.equal(method, 'thread/fork');
            if (failure) throw failure;
            if (active) throw new Error('The referenced turn cannot be in progress.');
            const cutoff = ignoresCutoff || params.lastTurnId == null ? history.length - 1 : history.findIndex(t => t.turnId === params.lastTurnId);
            assert.ok(cutoff >= 0);
            this.persisted.set('fork', structuredClone(history.slice(0, cutoff + 1)));
            this.threads.set('fork', { id: 'fork', turns: [], resumeState: 'resumed' });
            return { thread: { id: 'fork' }, cwd: source.cwd, approvalPolicy: 'never', sandbox: { type: 'readOnly' } };
        },
        forkConversationFromLatest(options) { return runtime.bx(this, options); }
    };
    return { manager, requests, originalSource, source };
}

// Reproduce the frozen client's failure against the paginated server contract.
const oldRuntime = load(original, constant('ForkFromTurnBefore'));
const old = harness(oldRuntime);
await assert.rejects(oldRuntime.Cx(old.manager, { sourceConversationId: 'source', targetTurnId: 'turn-20' }), /paginated threads/);
assert.deepEqual(old.requests.map(r => r.method), ['thread/fork', 'thread/rollback']);

const runtime = load(adapted, constant('ForkFromTurnAfter'));
const mode = { mode: 'plan', settings: { model: 'vsai:fixture/custom-model', reasoning_effort: 'max', developer_instructions: null } };
const current = harness(runtime, { visibleTurns: [{ turnId: 'turn-20' }, { turnId: 'turn-149' }] });
const result = await runtime.Cx(current.manager, {
    sourceConversationId: 'source', targetTurnId: 'turn-20', cwd: current.source.cwd,
    workspaceRoots: [current.source.cwd], collaborationMode: mode
});
assert.equal(result, 'fork');
assert.deepEqual(current.requests.map(r => r.method), ['thread/fork']);
assert.equal(current.requests[0].params.lastTurnId, 'turn-20');
assert.equal(current.manager.getConversation('fork').turns.length, 21);
assert.equal(current.manager.getConversation('fork').turns.at(-1).turnId, 'turn-20');
assert.equal(current.manager.getConversation('fork').resumeState, 'resumed');
assert.deepEqual(current.source, current.originalSource, 'The source history must remain unchanged.');
assert.deepEqual(runtime.hydrations[0].collaborationMode, mode);
assert.deepEqual(runtime.annotations[0], ['fork', 'source', 'Original title']);

// Fork latest keeps all history and must not receive an accidental cutoff.
const latest = harness(runtime);
await latest.manager.forkConversationFromLatest({ sourceConversationId: 'source', cwd: latest.source.cwd });
assert.equal(latest.requests[0].params.lastTurnId, undefined);
assert.equal(latest.manager.getConversation('fork').turns.length, 150);

for (const [sourceConversationId, targetTurnId, message] of [
    ['absent', 'turn-20', /Source conversation not found/],
    ['source', 'absent', /Target turn not found/]
]) {
    const invalid = harness(runtime);
    await assert.rejects(runtime.Cx(invalid.manager, { sourceConversationId, targetTurnId }), message);
    assert.equal(invalid.requests.length, 0);
}
for (const options of [{ active: true }, { failure: new Error('Synthetic fork failure') }]) {
    const rejected = harness(runtime, options);
    await assert.rejects(runtime.Cx(rejected.manager, { sourceConversationId: 'source', targetTurnId: 'turn-20' }));
    assert.equal(rejected.manager.getConversation('fork'), undefined);
    assert.deepEqual(rejected.source, rejected.originalSource);
}

const unsupported = harness(runtime, { ignoresCutoff: true });
await assert.rejects(runtime.Cx(unsupported.manager, { sourceConversationId: 'source', targetTurnId: 'turn-20' }), /history boundary/);
assert.deepEqual(unsupported.source, unsupported.originalSource);

assert.equal(read(asset), original, 'Checks must never rewrite the frozen bundle.');
console.log('Fork compatibility checks passed: native cutoff, hydration, full latest history, source preservation, and failures.');
