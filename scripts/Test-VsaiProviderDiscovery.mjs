import assert from 'node:assert/strict';
import { existsSync, mkdirSync, readdirSync } from 'node:fs';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import { dirname, join, resolve } from 'node:path';
import { homedir } from 'node:os';

const require = createRequire(import.meta.url);
const here = dirname(fileURLToPath(import.meta.url));
const editor = resolve(here, '..', 'CodexVsix', 'UI', 'CodexWebview', 'vsai-providers.js');
const screenshotPath = process.env.VSAI_PROVIDER_SCREENSHOT || null;

function loadPlaywright() {
  const bundled = join(process.env.USERPROFILE || homedir(), '.cache', 'codex-runtimes', 'codex-primary-runtime', 'dependencies', 'node', 'node_modules', 'playwright');
  const candidates = [process.env.VSAI_PLAYWRIGHT_MODULE, 'playwright', bundled].filter(Boolean);
  for (const candidate of candidates) {
    try { return require(candidate); } catch (_) { /* Try the next supported location. */ }
  }
  throw new Error('Playwright was not found. Install it normally, set VSAI_PLAYWRIGHT_MODULE, or provide the bundled Codex runtime.');
}
const { chromium } = loadPlaywright();

function chromiumExecutable() {
  const preferred = chromium.executablePath();
  if (preferred && existsSync(preferred)) return preferred;
  const root = join(process.env.LOCALAPPDATA || join(homedir(), 'AppData', 'Local'), 'ms-playwright');
  if (!existsSync(root)) throw new Error('No Playwright Chromium installation was found.');
  for (const entry of readdirSync(root, { withFileTypes: true }).filter(entry => entry.isDirectory()).sort((a, b) => b.name.localeCompare(a.name))) {
    const names = entry.name.startsWith('chromium_headless_shell-') ? ['headless_shell.exe']
      : entry.name.startsWith('chromium-') ? ['chrome.exe'] : [];
    for (const name of names) {
      const candidate = join(root, entry.name, 'chrome-win', name);
      if (existsSync(candidate)) return candidate;
    }
  }
  throw new Error('No usable Playwright Chromium executable was found.');
}
const browserPath = chromiumExecutable();

function service(id, name, catalog = {}, extra = {}) {
  return { id, name, baseUrl: 'https://' + id + '.example.test/v1', models: [], hasApiKey: true,
    catalog: { autoSync: true, source: 'auto', manualModels: [], discoveredModels: [], overrides: {}, hiddenModels: [], ...catalog }, ...extra };
}

const browser = await chromium.launch({ headless: true, executablePath: browserPath });
try {
  async function fixture() {
    const page = await browser.newPage({ viewport: { width: 1000, height: 850 } });
    await page.setContent('<meta name="vsai-settings-surface" content="true"><div class="app-shell-left-panel"><nav><div class="overflow-y-auto"></div></nav></div>');
    await page.evaluate(() => {
      window.__providerPosts = [];
      window.acquireVsCodeApi = () => ({ postMessage(message) { window.__providerPosts.push(JSON.parse(JSON.stringify(message))); } });
    });
    await page.addScriptTag({ path: editor });
    await page.waitForSelector('#vsai-providers-settings-entry');
    return page;
  }
  async function messages(page) { return page.evaluate(() => window.__providerPosts); }
  async function host(page, message) {
    await page.evaluate(value => window.dispatchEvent(new CustomEvent('codex-host-message', { detail: value })), message);
  }
  async function open(page, profiles, isBusy = false) {
    await page.locator('#vsai-providers-settings-entry').click();
    const request = (await messages(page)).at(-1);
    assert.equal(request.type, 'providers-request');
    await host(page, { type: 'providers-state', requestId: request.requestId, providers: profiles, isBusy });
    await page.waitForSelector('#vsai-providers-dialog[open]');
  }
  async function edit(page, index = 0) { await page.getByRole('button', { name: '编辑' }).nth(index).click(); }
  async function refresh(page) {
    await page.getByRole('button', { name: '刷新' }).click();
    const request = (await messages(page)).at(-1);
    assert.equal(request.type, 'providers-discover');
    return request;
  }

  // The native ChatGPT subscription remains separate from custom API services.
  // Its account state uses a correlated request, supports the asynchronous login
  // lifecycle, and never touches the custom service API fields.
  {
    const page = await fixture();
    await open(page, [service('custom', '自定义服务')]);
    const initialAccount = (await messages(page))[0];
    assert.equal(initialAccount.type, 'official-account-request');
    await host(page, { type: 'official-account-state', requestId: initialAccount.requestId, status: 'signed-out' });
    assert.equal(await page.locator('.vp-official-card .vp-info').innerText(), 'VSAI 尚未登录官方账号。登录一次后即可使用 GPT；桌面端登录不受影响。');
    assert.equal(await page.evaluate(() => {
      const account = document.querySelector('.vp-official-card');
      const custom = Array.from(document.querySelectorAll('.vp-card')).find(node => node.querySelector('h3')?.textContent === '自定义服务');
      return Boolean(account && custom && (account.compareDocumentPosition(custom) & Node.DOCUMENT_POSITION_FOLLOWING));
    }), true, 'official account card should precede custom service cards');

    await page.locator('#vsai-provider-name').fill('未保存服务');
    await page.locator('#vsai-provider-url').fill('https://local.example.test/v1');
    await page.locator('#vsai-provider-key').fill('custom-api-key');
    await page.getByRole('button', { name: '登录官方账号' }).click();
    const login = (await messages(page)).at(-1);
    assert.equal(login.type, 'official-account-login');
    await host(page, { type: 'official-account-state', requestId: login.requestId, status: 'signing-in' });
    await host(page, { type: 'official-account-state', requestId: 'stale-account-state', status: 'signed-out' });
    assert.equal(await page.locator('.vp-official-card .vp-info').innerText(), '正在等待官方账号登录完成。');
    await host(page, { type: 'providers-state', providers: [service('custom', '自定义服务')], isBusy: true });
    assert.equal(await page.getByRole('button', { name: '正在登录…' }).isDisabled(), true);
    assert.equal(await page.getByRole('button', { name: '取消登录' }).isDisabled(), false);
    assert.equal(await page.getByRole('button', { name: '取消登录' }).isVisible(), true);
    assert.equal(await page.locator('#vsai-provider-key').inputValue(), 'custom-api-key');
    await page.getByRole('button', { name: '取消登录' }).click();
    const cancel = (await messages(page)).at(-1);
    assert.equal(cancel.type, 'official-account-cancel');
    await host(page, { type: 'official-account-state', requestId: cancel.requestId, status: 'signed-out' });
    assert.equal(await page.locator('#vsai-provider-name').inputValue(), '未保存服务');
    assert.equal(await page.locator('#vsai-provider-url').inputValue(), 'https://local.example.test/v1');
    assert.equal(await page.locator('#vsai-provider-key').inputValue(), 'custom-api-key');
    await host(page, { type: 'official-account-state', status: 'error', error: '登录窗口已关闭。' });
    assert.match(await page.locator('.vp-official-card .vp-info').innerText(), /登录窗口已关闭。 请重试登录或刷新状态。/);

    assert.equal(await page.getByRole('button', { name: '刷新登录状态' }).isDisabled(), false);
    await page.getByRole('button', { name: '刷新登录状态' }).click();
    const refreshAccount = (await messages(page)).at(-1);
    assert.equal(refreshAccount.type, 'official-account-request');
    await host(page, { type: 'official-account-state', requestId: refreshAccount.requestId, status: 'signed-in', accountType: 'apiKey' });
    assert.match(await page.locator('.vp-official-card .vp-info').innerText(), /官方 API Key（不是 ChatGPT 官方订阅）/);
    await host(page, { type: 'providers-state', providers: [service('custom', '自定义服务')], isBusy: false });
    assert.equal(await page.getByRole('button', { name: '登录官方账号' }).isVisible(), true);
    assert.equal(await page.getByRole('button', { name: '登录官方账号' }).isDisabled(), false);
    await host(page, { type: 'official-account-state', status: 'signed-in', accountType: 'chatgpt' });
    assert.equal(await page.locator('.vp-official-card .vp-info').innerText(), 'ChatGPT 官方订阅已登录');
    assert.equal(await page.getByRole('button', { name: '登录官方账号' }).isHidden(), true);
    await page.close();
  }

  // Failed discovery of a new service remains saveable with the result token.
  {
    const page = await fixture();
    await open(page, []);
    await page.locator('#vsai-provider-name').fill('新服务');
    await page.locator('#vsai-provider-url').fill('https://new.example.test/v1');
    await page.locator('#vsai-provider-key').fill('secret-new');
    const discover = await refresh(page);
    await host(page, { type: 'providers-discovery', requestId: discover.requestId, status: 'auth-error', error: '认证失败', attemptedUtc: '2026-09-23T00:00:00Z', discoveryToken: 'failed-token' });
    await page.getByRole('button', { name: '保存服务' }).click();
    const save = (await messages(page)).at(-1);
    assert.equal(save.type, 'providers-save');
    assert.deepEqual(save.provider.models, []);
    assert.equal(save.provider.discoveryToken, 'failed-token');
    assert.equal(save.provider.catalog.status, 'auth-error');
    await page.close();
  }

  // Editing, switching providers, and close/reopen all abandon an older reply.
  {
    const page = await fixture();
    const profiles = [service('one', '一号'), service('two', '二号')];
    await open(page, profiles);
    await edit(page, 0);
    const stale = await refresh(page);
    await page.locator('#vsai-provider-url').fill('https://changed.example.test/v1');
    const newer = await refresh(page);
    await host(page, { type: 'providers-discovery', requestId: stale.requestId, status: 'success', attemptedUtc: '2026-09-23T00:30:00Z', discoveryToken: 'stale-token', models: [{ id: 'stale-model' }] });
    assert.equal(await page.locator('#vsai-provider-url').inputValue(), 'https://changed.example.test/v1');
    assert.doesNotMatch(await page.locator('.vp-table').innerText(), /stale-model/);
    await host(page, { type: 'providers-discovery', requestId: newer.requestId, status: 'success', attemptedUtc: '2026-09-23T00:31:00Z', discoveryToken: 'fresh-token', models: [{ id: 'fresh-model' }] });
    assert.match(await page.locator('.vp-table').innerText(), /fresh-model/);
    await edit(page, 1);
    await refresh(page);
    await page.getByRole('button', { name: '关闭', exact: true }).click();
    await page.locator('#vsai-providers-settings-entry').click();
    const request = (await messages(page)).at(-1);
    await host(page, { type: 'providers-state', requestId: request.requestId, providers: profiles });
    await page.locator('#vsai-provider-name').fill('可重开');
    await page.locator('#vsai-provider-url').fill('https://reopen.example.test/v1');
    await refresh(page);
    await page.close();
  }

  // Discovered snapshots retain local overrides and prototype-shaped model IDs.
  {
    const page = await fixture();
    const custom = service('custom', '自定义', {
      manualModels: ['__proto__', 'constructor'],
      discoveredModels: [{ id: 'shared', displayName: '服务名称', supportsTools: true, reasoningEfforts: null }],
      overrides: { shared: { id: 'shared', displayName: '本地名称', supportsTools: false, reasoningEfforts: ['high'], defaultReasoningEffort: 'high' } }
    }, { runtimeWarnings: ['max-output-tokens-metadata-only', 'runtime-catalog-user-config'] });
    await open(page, [custom]);
    await edit(page);
    assert.match(await page.locator('.vp-table').innerText(), /__proto__/);
    assert.match(await page.locator('.vp-table').innerText(), /constructor/);
    const first = await refresh(page);
    await host(page, { type: 'providers-discovery', requestId: first.requestId, status: 'success', attemptedUtc: '2026-09-23T01:00:00Z', discoveryToken: 'success-token', models: [{ id: 'shared', displayName: '新发现名称', supportsTools: true, reasoningEfforts: null }] });
    assert.match(await page.locator('.vp-table').innerText(), /本地名称/);
    assert.match(await page.locator('.vp-table').innerText(), /不支持工具/);
    assert.match(await page.locator('.vp-status').first().innerText(), /最大输出仅记录服务能力/);
    assert.deepEqual(await page.evaluate(() => {
      const dialog = document.querySelector('#vsai-providers-dialog');
      const table = document.querySelector('.vp-table table');
      return [getComputedStyle(table).color, getComputedStyle(dialog).color];
    }), ['rgb(238, 238, 238)', 'rgb(238, 238, 238)']);
    assert.deepEqual(await page.getByRole('button', { name: '刷新' }).evaluate(node => [getComputedStyle(node).whiteSpace, node.scrollWidth <= node.clientWidth]), ['nowrap', true]);
    if (screenshotPath) {
      mkdirSync(dirname(screenshotPath), { recursive: true });
      await page.screenshot({ path: screenshotPath, fullPage: true });
    }
    await page.setViewportSize({ width: 680, height: 850 });
    assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), 'provider dialog causes horizontal page overflow at 680px');
    await page.close();
  }

  // A local context edit is field-level: a later discovery can refresh image
  // and reasoning capabilities without overwriting that local capacity.
  {
    const page = await fixture();
    const scoped = service('scoped', '字段覆盖', {
      discoveredModels: [{ id: 'shared', contextWindow: 100, supportsImages: false, reasoningEfforts: ['low'], defaultReasoningEffort: 'low' }],
      overrides: {}
    });
    await open(page, [scoped]);
    await edit(page);
    await page.locator('.vp-table details summary').click();
    await page.locator('.vp-meta select').first().selectOption('local');
    await page.locator('.vp-table details summary').click();
    await page.locator('.vp-meta label').filter({ hasText: '上下文容量' }).locator('input').fill('999');
    const discover = await refresh(page);
    await host(page, { type: 'providers-discovery', requestId: discover.requestId, status: 'success', attemptedUtc: '2026-09-23T02:00:00Z', discoveryToken: 'field-token', models: [{ id: 'shared', contextWindow: 100, supportsImages: true, reasoningEfforts: ['high'], defaultReasoningEffort: 'high' }] });
    assert.match(await page.locator('.vp-table').innerText(), /图/);
    await page.getByRole('button', { name: '保存服务' }).click();
    const save = (await messages(page)).at(-1);
    assert.deepEqual(save.provider.catalog.overrides.shared, { id: 'shared', contextWindow: 999 });
    await page.close();
  }

  // Adding an effort starts from the effective checked list, so inherited low
  // remains present when this provider locally adds high.
  {
    const page = await fixture();
    const effortScoped = service('effort-scoped', '推理字段覆盖', {
      discoveredModels: [{ id: 'effort-model', contextWindow: 100, reasoningEfforts: ['low'], defaultReasoningEffort: 'low' }],
      overrides: { 'effort-model': { id: 'effort-model', contextWindow: 999 } }
    });
    await open(page, [effortScoped]);
    await edit(page);
    await page.locator('.vp-table details summary').click();
    await page.getByRole('checkbox', { name: 'high', exact: true }).check();
    await page.getByRole('button', { name: '保存服务' }).click();
    const save = (await messages(page)).at(-1);
    assert.deepEqual(save.provider.catalog.overrides['effort-model'].reasoningEfforts, ['low', 'high']);
    assert.equal(save.provider.catalog.overrides['effort-model'].defaultReasoningEffort, undefined);
    await page.close();
  }

  // Effort controls refresh their default choices in place, and each local
  // metadata field can independently return to its automatic value.
  {
    const page = await fixture();
    const resettable = service('resettable', '可重置', {
      discoveredModels: [{ id: 'reset-model', contextWindow: 100, reasoningEfforts: ['low', 'high'], defaultReasoningEffort: 'high' }],
      overrides: { 'reset-model': { id: 'reset-model', contextWindow: 999 } }
    });
    await open(page, [resettable]);
    await edit(page);
    await page.locator('.vp-table details summary').click();
    assert.equal(await page.locator('.vp-meta label').filter({ hasText: '上下文容量' }).locator('input').inputValue(), '999');
    const high = page.getByRole('checkbox', { name: 'high', exact: true });
    const defaultSelect = page.locator('.vp-meta label').filter({ hasText: '默认推理档位' }).locator('select');
    assert.equal(await defaultSelect.locator('option[value="high"]').isDisabled(), false);
    await high.uncheck();
    await high.dispatchEvent('input');
    assert.equal(await high.isChecked(), false);
    assert.equal(await defaultSelect.inputValue(), '');
    await high.check();
    assert.equal(await defaultSelect.inputValue(), '');
    await page.getByRole('button', { name: '跟随自动' }).click();
    await page.locator('.vp-table details summary').click();
    assert.equal(await page.locator('.vp-meta label').filter({ hasText: '上下文容量' }).locator('input').inputValue(), '100');
    await page.close();
  }

  // Busy work allows discovery but blocks writes.
  {
    const page = await fixture();
    await open(page, [service('busy', '忙碌')], true);
    await edit(page);
    assert.equal(await page.getByRole('button', { name: '保存服务' }).isDisabled(), true);
    assert.equal(await page.getByRole('button', { name: '删除' }).isDisabled(), true);
    assert.equal(await page.getByRole('button', { name: '刷新' }).isDisabled(), false);
    assert.equal(await page.getByRole('button', { name: '登录官方账号' }).isDisabled(), true);
    await page.close();
  }

  // A rejected save restores the submitted key into the edit box.
  {
    const page = await fixture();
    const saved = service('key', '密钥服务', { discoveredModels: [{ id: 'automatic-model', contextWindow: 100, supportsReasoning: false, reasoningEfforts: ['high'], defaultReasoningEffort: 'high' }] });
    await open(page, [saved]);
    await edit(page);
    await page.locator('.vp-table details summary').click();
    const automaticDisplayName = page.locator('.vp-meta label').filter({ hasText: '显示名称' }).locator('input');
    assert.equal(await automaticDisplayName.isDisabled(), true);
    assert.match(await page.locator('.vp-table').innerText(), /推理档位（待补充）/);
    await page.locator('#vsai-provider-key').fill('replacement-secret');
    await page.getByRole('button', { name: '保存服务' }).click();
    const save = (await messages(page)).at(-1);
    assert.equal(save.provider.apiKey, 'replacement-secret');
    assert.equal(await page.locator('.vp-table button').first().isDisabled(), true);
    await host(page, { type: 'providers-state', requestId: save.requestId, error: '保存失败', providers: [saved] });
    assert.equal(await page.locator('#vsai-provider-key').inputValue(), 'replacement-secret');
    assert.equal(await automaticDisplayName.isDisabled(), true);
    await page.close();
  }
} finally {
  await browser.close();
}
console.log('VSAI provider discovery browser tests passed.');
