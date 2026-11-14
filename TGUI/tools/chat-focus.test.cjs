const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { test } = require('node:test');

const bridge = fs.readFileSync(path.resolve(__dirname,
  '../../Resources/Web/DeepLagoon/bridge.js'), 'utf8');

function createPage(name = 'chat.html', options = {}) {
  const listeners = {};
  const messages = [];
  const transports = [];
  const record = (url, requestOptions) => {
    const [type, payload] = requestOptions?.method === 'POST'
      ? [requestOptions.body.slice(0, requestOptions.body.indexOf('\n')), requestOptions.body.slice(requestOptions.body.indexOf('\n') + 1)]
      : url.split('?')[1].split('&');
    if (requestOptions?.method === 'POST') {
      assert.equal(url, 'res://deeplagoon/Web/DeepLagoon/bridge');
      messages.push({ type, ...JSON.parse(payload) });
      return;
    }
    messages.push({ type: decodeURIComponent(type), ...JSON.parse(decodeURIComponent(payload)) });
  };
  const document = {
    activeElement: null,
    addEventListener: (type, callback) => (listeners[type] ??= []).push(callback),
  };
  class Image {
    set src(url) {
      transports.push('image');
      record(url);
      this.onload();
    }
  }
  const windowListeners = {};
  const window = {
    addEventListener: (type, callback) => (windowListeners[type] ??= []).push(callback),
    __deeplagoonFetchBridge: Boolean(options.native),
    __deeplagoonPostBridge: Boolean(options.post),
    fetch: async (url, requestOptions) => {
      transports.push('fetch');
      assert.equal(requestOptions.mode, 'same-origin');
      assert.equal(requestOptions.credentials, 'omit');
      if (options.failFetch) throw new Error('Unsupported custom scheme');
      record(url, requestOptions);
      return { ok: true, arrayBuffer: async () => new ArrayBuffer(0) };
    },
  };
  vm.runInNewContext(bridge, {
    window, document, Image, location: { pathname: `/Web/DeepLagoon/${name}` }, queueMicrotask,
    setTimeout: options.failFetch ? callback => queueMicrotask(callback) : setTimeout, clearTimeout, console,
  });
  return {
    messages,
    transports,
    async topic(value) {
      window.Byond.topic(value);
      await new Promise(resolve => setImmediate(resolve));
    },
    async ping(booted) {
      window.__tguiAppBooted__ = booted;
      window.DeepLagoonHealth.ping();
      await new Promise(resolve => setImmediate(resolve));
    },
    async click(node) {
      await this.pointerDown(node);
      await this.pointerUp();
    },
    async pointerDown(node) {
      for (const callback of listeners.pointerdown ?? []) callback();
      await this.focus(node);
    },
    async pointerUp(onClick) {
      for (const callback of listeners.pointerup ?? []) callback();
      onClick?.();
      await new Promise(resolve => setTimeout(resolve, 0));
      await new Promise(resolve => setImmediate(resolve));
    },
    async blurWindowAndClickSameInput() {
      for (const callback of windowListeners.blur ?? []) callback();
      await new Promise(resolve => setImmediate(resolve));
      await this.click(document.activeElement);
    },
    async focus(node) {
      document.activeElement = node;
      for (const callback of listeners.focusout ?? []) callback();
      for (const callback of listeners.focusin ?? []) callback();
      // Flush the focus microtask and serialized bridge transport.
      await new Promise(resolve => setImmediate(resolve));
    },
  };
}

const input = (options = {}) => ({ matches: () => true, ...options });
const background = { matches: () => false };

test('reading chat never requests keyboard focus', async () => {
  const page = createPage();
  await page.focus(background);
  assert.deepEqual(page.messages, []);
});

test('native bridge sends a large ASCII state in one fetch request', async () => {
  const page = createPage('interface.html', { native: true });
  await page.topic({ type: 'panel/state_set', panel_state: JSON.stringify({ value: 'a'.repeat(16000) }) });
  assert.deepEqual(page.transports, ['fetch']);
  assert.equal(page.messages[0].type, 'panel/state_set');
  assert.equal(page.messages[0].value.length, 16000);
});

test('POST bridge transmits large Unicode settings without URL encoding or chunks', async () => {
  const page = createPage('interface.html', { native: true, post: true });
  const value = 'абв🙂'.repeat(4000);
  await page.topic({ type: 'panel/state_set', panel_state: JSON.stringify({ value }) });
  assert.deepEqual(page.transports, ['fetch']);
  assert.equal(page.messages[0].type, 'panel/state_set');
  assert.equal(page.messages[0].value, value);
});

test('failed POST bridge safely chunks large Unicode settings for the image fallback', async () => {
  const page = createPage('interface.html', { native: true, post: true, failFetch: true });
  const value = 'абв🙂'.repeat(4000);
  await page.topic({ type: 'panel/state_set', panel_state: JSON.stringify({ value }) });
  assert.deepEqual(page.transports.slice(0, 4), ['fetch', 'fetch', 'fetch', 'fetch']);
  assert.ok(page.messages.every(message => message.type === 'bridge/chunk'));
  assert.equal(JSON.parse(page.messages.map(message => message.text).join('')).value, value);
});

test('native bridge still chunks percent-encoded Unicode states within the URL limit', async () => {
  const page = createPage('interface.html', { native: true });
  const value = 'абв🙂'.repeat(4000);
  await page.topic({ type: 'panel/state_set', panel_state: JSON.stringify({ value }) });
  assert.ok(page.messages.length > 5);
  assert.ok(page.transports.every(transport => transport === 'fetch'));
  page.messages.forEach((message, index) => {
    assert.equal(message.type, 'bridge/chunk');
    assert.equal(message.index, index);
  });
  assert.equal(JSON.parse(page.messages.map(message => message.text).join('')).value, value);
});

test('native bridge preserves message order', async () => {
  const page = createPage('interface.html', { native: true });
  await Promise.all([
    page.topic({ type: 'act/first', payload: { value: 1 } }),
    page.topic({ type: 'act/second', payload: { value: 2 } }),
  ]);
  assert.deepEqual(page.messages.map(message => message.type), ['act/first', 'act/second']);
});

test('unsupported native fetch falls back to the old image bridge', async () => {
  const page = createPage('interface.html', { native: true, failFetch: true });
  await page.topic({ type: 'act/test', payload: { value: 1 } });
  assert.deepEqual(page.transports, ['fetch', 'fetch', 'fetch', 'fetch', 'image']);
  assert.equal(page.messages[0].type, 'act/test');
  await page.topic({ type: 'act/second', payload: { value: 2 } });
  assert.equal(page.transports.at(-1), 'image');
});

test('each background click releases the stock engines automatic keyboard focus', async () => {
  const page = createPage();
  await page.click(background);
  await page.click(background);
  assert.deepEqual(page.messages.map(m => [m.type, m.active]), [
    ['chat-input-focus', false], ['chat-input-focus', false],
  ]);
});

test('clicking an editable field keeps keyboard focus', async () => {
  const page = createPage();
  await page.click(input());
  await page.click(input());
  assert.deepEqual(page.messages.map(m => m.active), [true]);
});

test('editable fields acquire focus and leaving them returns gameplay keys', async () => {
  const page = createPage();
  await page.focus(input());
  await page.focus(input());
  await page.focus(background);
  assert.deepEqual(page.messages.map(m => [m.type, m.active]), [
    ['chat-input-focus', true], ['chat-input-focus', false],
  ]);
});

test('disabled and readonly fields leave gameplay keys available', async () => {
  const page = createPage();
  await page.focus(input({ disabled: true }));
  await page.focus(input({ readOnly: true }));
  assert.deepEqual(page.messages, []);
});

test('contenteditable receives keyboard focus', async () => {
  const page = createPage();
  await page.focus({ isContentEditable: true });
  await page.focus(background);
  assert.deepEqual(page.messages.map(m => m.active), [true, false]);
});

test('returning to the same input after clicking the game reacquires focus', async () => {
  const page = createPage();
  await page.focus(input());
  await page.blurWindowAndClickSameInput();
  assert.deepEqual(page.messages.map(m => m.active), [true, false, true]);
});

test('interface clicks return gameplay focus outside editable fields', async () => {
  const page = createPage('interface.html');
  await page.focus(input());
  await page.focus(background);
  assert.deepEqual(page.messages.map(m => [m.type, m.active]), [
    ['chat-input-focus', true], ['chat-input-focus', false],
  ]);
});

test('a click after editing keeps native input until the button action', async () => {
  const page = createPage('interface.html');
  await page.focus(input());
  await page.pointerDown(background);
  assert.deepEqual(page.messages.map(m => m.active), [true]);
  await page.pointerUp(() => page.topic({ type: 'act/confirm' }));
  assert.deepEqual(page.messages.map(m => [m.type, m.active]), [
    ['chat-input-focus', true], ['act/confirm', undefined], ['chat-input-focus', false],
  ]);
});

test('large Cyrillic settings are transmitted in bounded ordered chunks', async () => {
  const page = createPage();
  const value = 'абв🙂'.repeat(4000);
  await page.topic({ type: 'panel/state_set', panel_state: JSON.stringify({ value }) });
  assert.ok(page.messages.length > 5);
  assert.ok(page.messages.every(message => message.type === 'bridge/chunk' && message.text.length <= 2000));
  page.messages.forEach((message, index) => assert.equal(message.index, index));
  assert.equal(JSON.parse(page.messages.map(message => message.text).join('')).value, value);
});

test('health ping only succeeds after the application has booted', async () => {
  const page = createPage('interface.html');
  await page.ping(false);
  assert.deepEqual(page.messages, []);
  await page.ping(true);
  assert.equal(page.messages[0].type, 'health/pong');
});
