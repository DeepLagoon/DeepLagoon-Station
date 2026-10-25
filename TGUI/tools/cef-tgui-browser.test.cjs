// Production bundle smoke test. PLAYWRIGHT_MODULE may point at an installed Playwright package.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const http = require('node:http');
const path = require('node:path');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const root = path.resolve(__dirname, '../../Resources/Web/DeepLagoon');
const png = 'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/l9sAAAAASUVORK5CYII=';

(async () => {
  const server = http.createServer((request, response) => {
    const file = path.resolve(root, '.' + new URL(request.url, 'http://localhost').pathname);
    if (!file.startsWith(root + path.sep) || !fs.existsSync(file)) { response.writeHead(404).end(); return; }
    const ext = path.extname(file);
    response.setHeader('Content-Type', ({ '.html': 'text/html', '.js': 'application/javascript', '.css': 'text/css', '.woff2': 'font/woff2' })[ext] || 'application/octet-stream');
    response.end(fs.readFileSync(file));
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  let browser;
  try {
    browser = await chromium.launch({ channel: process.env.BROWSER_CHANNEL || 'msedge', headless: true });
    const page = await browser.newPage({ viewport: { width: 1100, height: 760 } });
    const errors = [];
    page.on('pageerror', error => errors.push(String(error)));
    const transport = process.env.BRIDGE_TRANSPORT || 'image';
    await page.addInitScript(({ png, transport }) => {
      window.__sent = [];
      window.__fetchBridgeRequests = 0;
      window.__deeplagoonFetchBridge = transport === 'fetch' || transport === 'post';
      window.__deeplagoonPostBridge = transport === 'post';
      const respond = message => {
        if (message.type === 'act/icons') {
          window.update(JSON.stringify({ type: 'update', payload: { data: { iconBatch: { icons:
            message.payload.ids.split(',').map(id => ({ id, layers: [{ url: png, color: '#ffffff' }] })) } } } }));
        }
      };
      const nativeFetch = window.fetch.bind(window);
      window.fetch = async (url, options) => {
        if (!url.startsWith('res://deeplagoon/Web/DeepLagoon/bridge')) return nativeFetch(url, options);
        const posted = options?.method === 'POST';
        const [type, payload] = posted ? [options.body.slice(0, options.body.indexOf('\n')), options.body.slice(options.body.indexOf('\n') + 1)] : url.split('?')[1].split('&');
        const message = { type: posted ? type : decodeURIComponent(type), payload: JSON.parse(posted ? payload : decodeURIComponent(payload)) };
        window.__fetchBridgeRequests++;
        window.__sent.push(message);
        queueMicrotask(() => respond(message));
        return { ok: true, arrayBuffer: async () => new ArrayBuffer(0) };
      };
      const NativeImage = window.Image;
      window.Image = class extends NativeImage {
        set src(value) {
          if (!value.startsWith('res://deeplagoon/Web/DeepLagoon/bridge?')) { super.src = value; return; }
          const [type, payload] = value.split('?')[1].split('&');
          const message = { type: decodeURIComponent(type), payload: JSON.parse(decodeURIComponent(payload)) };
          window.__sent.push(message);
          queueMicrotask(() => {
            this.onload?.(new Event('load'));
            respond(message);
          });
        }
      };
    }, { png, transport });
    await page.goto(`http://127.0.0.1:${server.address().port}/interface.html`);
    await page.waitForFunction(() => window.__tguiAppBooted__);
    const state = { available: true, mode: 'appearance', name: 'Test', age: 25, sex: 0, gender: 0, spawn: 0,
      species: 'Human', height: 1, width: 1, minAge: 18, maxAge: 100, hairColor: '#ffffff', beardColor: '#ffffff',
      skinColor: '#ffffff', eyeColor: '#ffffff', hairStyle: 'Hair0', beardStyle: 'FacialHairShaved',
      appearance: {}, tabs: [], previewSlots: [], speciesOptions: [{ id: 'Human', name: 'Human' }],
      hairOptions: Array.from({ length: 100 }, (_, i) => ({ id: `Hair${i}`, name: `Hair ${i}`, iconId: `marking/Hair${i}` })),
      beardOptions: [] };
    const publish = data => page.evaluate(data => {
      const dynamic = { ...data };
      const catalogue = {};
      for (const key of ['hairOptions', 'beardOptions', 'speciesOptions', 'departments', 'traits', 'markingOptions']) {
        catalogue[key] = dynamic[key];
        delete dynamic[key];
      }
      window.update(JSON.stringify({ type: 'update', payload: {
        replaceData: true, replaceStaticData: true, resetPartialData: true,
        static_data: catalogue,
        config: { interface: 'CharacterEditor', status: 2, window: { key: 'deeplagoon', fancy: false } }, data: dynamic } }));
    }, data);
    await publish(state);
    await page.getByText('Выбор причёски', { exact: true }).waitFor();
    assert.equal(await page.evaluate(() => window.__sent.filter(m => m.type === 'act/icons').length), 0, 'Collapsed hair catalogue must not decode icons');
    await page.getByText('Выбор причёски', { exact: true }).click();
    await page.locator('.HairCarousel__track').first().scrollIntoViewIfNeeded();
    await page.waitForFunction(() => window.__sent.some(m => m.type === 'act/icons'));
    const visibleIds = await page.evaluate(() => window.__sent.filter(m => m.type === 'act/icons').flatMap(m => m.payload.ids.split(',')));
    assert.ok(visibleIds.length > 0 && visibleIds.length <= 8, `Only visible hair icons requested: ${visibleIds.length}`);
    await page.locator('.HairCarousel__track').first().evaluate(track => { track.scrollLeft = track.clientWidth * 2; });
    await page.waitForFunction(count => window.__sent.filter(m => m.type === 'act/icons').flatMap(m => m.payload.ids.split(',')).length > count, visibleIds.length);
    await page.evaluate(() => window.update(JSON.stringify({ type: 'update', payload: { data: { hairColor: '#123456' } } })));
    assert.equal(await page.locator('.HairCarousel').count(), 1, 'Incremental colour update must preserve the static catalogue');
    await page.evaluate(() => window.DeepLagoonHealth.ping());
    await page.waitForFunction(() => window.__sent.some(m => m.type === 'health/pong'));
    await publish({ ...state, mode: 'jobs', departments: [{ id: 'med', name: 'Medical', jobs: [{ id: 'Doctor', name: 'Doctor', allowed: true, priority: 0 }] }], jobState: { Doctor: { allowed: true, priority: 3 } } });
    await page.getByText('Высокий', { exact: true }).waitFor();
    await page.evaluate(() => window.update(JSON.stringify({ type: 'update', payload: { data: { jobState: { Doctor: { allowed: true, priority: 0 } } } } })));
    await page.getByText('Никогда', { exact: true }).waitFor();
    await publish({ ...state, mode: 'traits', traits: [{ id: 'A', name: 'Trait A', category: 'Traits', cost: 1 }, { id: 'B', name: 'Trait B', category: 'Traits', cost: 1 }], traitState: { A: { visible: false, selected: false }, B: { visible: true, selected: false } } });
    await page.getByText('Trait B (1)', { exact: true }).waitFor();
    assert.equal(await page.getByText('Trait A (1)', { exact: true }).count(), 0);
    await page.evaluate(() => window.update(JSON.stringify({ type: 'update', payload: { data: { traitState: { A: { visible: true, selected: true }, B: { visible: false, selected: false } } } } })));
    await page.getByText('Trait A (1)', { exact: true }).waitFor();
    assert.equal(await page.getByText('Trait B (1)', { exact: true }).count(), 0);

    await publish(state);
    const height = page.getByRole('slider', { name: 'Рост', exact: true });
    await height.waitFor();
    await page.evaluate(() => { window.__sent = []; });
    await height.evaluate(input => {
      input.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true }));
      const set = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;
      for (let i = 0; i <= 40; i++) {
        set.call(input, String(0.8 + i / 100));
        input.dispatchEvent(new Event('input', { bubbles: true }));
      }
      window.dispatchEvent(new PointerEvent('pointerup'));
    });
    await page.waitForFunction(() => window.__sent.some(message => message.type === 'act/height' && message.payload.value === 1.2));
    const heights = await page.evaluate(() => window.__sent.filter(message => message.type === 'act/height'));
    assert.ok(heights.length <= 3, `41 slider changes should be coalesced: ${heights.length}`);
    assert.equal(heights.at(-1).payload.value, 1.2, 'Release must flush the final size');
    await page.evaluate(() => { window.__sent = []; });
    await page.locator('.CharacterEditor__rotation .Button').first().evaluate(button => { button.click(); button.click(); });
    await page.waitForFunction(() => window.__sent.filter(message => message.type === 'act/rotate').length === 2);
    const zoom = await page.evaluate(() => document.body.style.zoom);
    await page.evaluate(() => document.dispatchEvent(new WheelEvent('wheel', { ctrlKey: true, deltaY: -100, bubbles: true, cancelable: true })));
    assert.equal(await page.evaluate(() => document.body.style.zoom), zoom, 'Embedded editor must not install page zoom');

    const show = (name, data) => page.evaluate(({ name, data }) => window.update(JSON.stringify({ type: 'update', payload: {
      replaceData: true, replaceStaticData: true, resetPartialData: true, static_data: {},
      config: { interface: name, status: 2, window: { key: 'deeplagoon', fancy: false } }, data,
    } })), { name, data });
    await show('CharacterColorPicker', { id: 'color-test', value: '#ff0000', appearance: { theme: 'light' } });
    await page.getByRole('button', { name: 'HSV', exact: true }).click();
    await page.evaluate(() => { window.__sent = []; });
    await page.getByRole('slider', { name: 'H', exact: true }).evaluate(input => {
      input.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true }));
      const set = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;
      for (let i = 0; i <= 120; i++) {
        set.call(input, String(i));
        input.dispatchEvent(new Event('input', { bubbles: true }));
      }
      window.dispatchEvent(new PointerEvent('pointerup'));
    });
    await page.waitForFunction(() => window.__sent.some(message => message.type === 'act/color-picker-change' && message.payload.value === '#00ff00'));
    assert.ok(await page.evaluate(() => window.__sent.filter(message => message.type === 'act/color-picker-change').length <= 3), 'Color changes must be throttled');
    assert.ok(await page.locator('.CharacterColorDialog.theme-light').count(), 'Theme object reaches the color dialog');

    await show('ChatComposer', { session: 1, revision: 1, text: 'first', channel: '1', emoteChannel: '2', maxLength: 1000, channels: [] });
    await page.getByRole('textbox', { name: 'Сообщение', exact: true }).waitFor();
    await page.waitForFunction(() => window.__sent.some(message => message.type === 'act/presented' && message.payload.session === 1));
    await show('ChatComposer', { session: 2, revision: 2, text: '', channel: '1', emoteChannel: '2', maxLength: 1000, channels: [] });
    await page.keyboard.type('second opening');
    await page.getByRole('textbox', { name: 'Сообщение', exact: true }).press('Enter');
    await page.waitForFunction(() => window.__sent.some(message => message.type === 'act/submit' && message.payload.session === 2 && message.payload.text === 'second opening'));

    await publish({ available: false, mode: 'appearance' });
    await page.getByText('Выберите персонажа.', { exact: false }).waitFor();
    assert.equal(await page.locator('.HairCarousel').count(), 0);
    assert.deepEqual(errors, []);

    await page.waitForTimeout(1050);
    await page.evaluate(() => {
      window.__sent = [];
      for (let i = 0; i < 20; i++) Byond.topic({ type: 'log', level: 'warn', ns: 'test', message: 'x'.repeat(5000) });
    });
    await page.waitForFunction(() => window.__sent.filter(message => message.type === 'log').length === 5);
    assert.ok(await page.evaluate(() => window.__sent.filter(message => message.type === 'log').every(message => message.payload.message.length === 2048)));
    await page.waitForTimeout(1050);
    await publish({ ...state, departments: {} });
    await page.getByRole('alert').waitFor();
    await page.getByRole('button', { name: 'Перезагрузить окно / Reload window', exact: true }).waitFor();
    await page.waitForFunction(() => window.__sent.some(message => message.type === 'log' && message.payload.level === 'error' && message.payload.ns === 'render'));
    await page.getByRole('button', { name: 'Перезагрузить окно / Reload window', exact: true }).click();
    await page.waitForFunction(() => window.__tguiAppBooted__);
    await publish(state);
    await page.getByRole('slider', { name: 'Рост', exact: true }).waitFor();
    assert.equal(await page.evaluate(() => window.__fetchBridgeRequests > 0), transport === 'fetch' || transport === 'post');
    console.log(`PASS: production boot, ${visibleIds.length} lazy icons, incremental catalogues, size/color throttle and release, repeated clicks, embedded zoom, composer reuse, bounded page logs, error fallback and reload`);
  } finally {
    await browser?.close();
    await new Promise(resolve => server.close(resolve));
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
