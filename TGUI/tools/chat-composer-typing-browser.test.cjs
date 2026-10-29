// Exercise typing activity through the production composer and native bridge.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const http = require('node:http');
const path = require('node:path');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const root = path.resolve(__dirname, '../../Resources/Web/DeepLagoon');

(async () => {
  const server = http.createServer((request, response) => {
    const file = path.resolve(root, '.' + new URL(request.url, 'http://localhost').pathname);
    if (!file.startsWith(root + path.sep) || !fs.existsSync(file)) {
      response.writeHead(404).end();
      return;
    }
    response.setHeader('Content-Type', ({ '.html': 'text/html', '.js': 'text/javascript',
      '.css': 'text/css', '.woff2': 'font/woff2' })[path.extname(file)] || 'application/octet-stream');
    response.end(fs.readFileSync(file));
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  let browser;
  try {
    browser = await chromium.launch({ channel: process.env.BROWSER_CHANNEL || 'msedge', headless: true });
    const page = await browser.newPage();
    const errors = [];
    page.on('pageerror', error => errors.push(String(error)));
    await page.addInitScript(() => {
      window.__sent = [];
      const NativeImage = window.Image;
      window.Image = class extends NativeImage {
        set src(value) {
          if (!value.startsWith('res://deeplagoon/Web/DeepLagoon/bridge?')) { super.src = value; return; }
          const [type, payload] = value.split('?')[1].split('&');
          window.__sent.push({ type: decodeURIComponent(type), payload: JSON.parse(decodeURIComponent(payload)) });
          queueMicrotask(() => this.onload?.(new Event('load')));
        }
      };
    });
    await page.goto(`http://127.0.0.1:${server.address().port}/interface.html`);
    await page.waitForFunction(() => window.__tguiAppBooted__);
    await page.evaluate(() => window.update(JSON.stringify({ type: 'update', payload: {
      replaceData: true,
      config: { interface: 'ChatComposer', title: 'Chat', status: 2, window: { key: 'typing', fancy: false } },
      data: { session: 1, revision: 1, text: '', channel: '1', emoteChannel: '512', maxLength: 1000,
        channels: [{ id: '1', name: 'Say' }, { id: '512', name: 'Emote' }, { id: '32', name: 'LOOC' }] },
    } })));
    const input = page.getByRole('textbox', { name: 'Сообщение', exact: true });
    await input.waitFor();
    await page.waitForFunction(() => window.__sent.some(message => message.type === 'act/focus'));
    await page.evaluate(() => { window.__sent.length = 0; });
    for (let index = 1; index <= 60; index++) {
      await input.fill('a'.repeat(index));
      await page.waitForTimeout(100);
      if (index % 20 === 0) {
        const drafts = await page.evaluate(() => window.__sent.filter(message => message.type === 'act/draft'));
        assert(drafts.length >= 5, 'Uninterrupted input must flush activity before the typing timeout');
        assert(drafts.at(-1).payload.text.length >= index - 3);
      }
    }
    await page.waitForTimeout(250);
    const beforeIdle = await page.evaluate(() => window.__sent.filter(message => message.type === 'act/draft').length);
    await page.waitForTimeout(600);
    assert.equal(await page.evaluate(() => window.__sent.filter(message => message.type === 'act/draft').length), beforeIdle);
    await input.fill('');
    await page.waitForTimeout(250);
    assert.equal(await page.evaluate(() => window.__sent.filter(message => message.type === 'act/draft').at(-1).payload.text), '');
    await input.evaluate(element => element.blur());
    await page.waitForFunction(() => window.__sent.some(message => message.type === 'act/blur'));
    await input.focus();
    for (const [revision, channel] of [[2, '512'], [3, '32']]) {
      await page.evaluate(data => window.update(JSON.stringify({ type: 'update', payload: { data } })),
        { revision, channel, text: 'draft' });
      await input.fill(`channel ${channel}`);
      await page.waitForFunction(channel => window.__sent.some(message => message.type === 'act/draft'
        && message.payload.channel === channel && message.payload.text === `channel ${channel}`), channel);
    }
    assert.deepEqual(errors, []);
    console.log('PASS: continuous typing, idle silence, empty drafts, focus and channel switches reach the native bridge');
  } finally {
    await browser?.close();
    await new Promise(resolve => server.close(resolve));
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
