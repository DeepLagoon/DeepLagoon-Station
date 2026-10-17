// Exercise the standalone production chat entry point and its native health bridge.
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
    response.setHeader('Content-Type', ({ '.html': 'text/html', '.js': 'application/javascript',
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
          if (!value.startsWith('res://deeplagoon/Web/DeepLagoon/bridge?')) {
            super.src = value;
            return;
          }
          const [type, payload] = value.split('?')[1].split('&');
          window.__sent.push({ type: decodeURIComponent(type), payload: JSON.parse(decodeURIComponent(payload)) });
          queueMicrotask(() => this.onload?.(new Event('load')));
        }
      };
    });
    for (let reload = 0; reload < 2; reload++) {
      if (reload) await page.reload();
      else await page.goto(`http://127.0.0.1:${server.address().port}/chat.html`);
      await page.waitForFunction(() => window.__tguiAppBooted__ === true, null, { timeout: 5000 });
      await page.waitForFunction(() => window.__sent.some(message => message.type === 'ready'));
      for (let tick = 0; tick < 18; tick++) {
        await page.evaluate(tick => {
          window.update(JSON.stringify({ type: 'chat/replace', payload: [
            { type: 'system', html: `Chat health message ${tick}` },
          ] }));
          window.DeepLagoonHealth.ping();
        }, tick);
        await page.waitForFunction(count => window.__sent.filter(message => message.type === 'health/pong').length === count, tick + 1);
      }
      await page.getByText('Chat health message 17', { exact: true }).waitFor();
      assert.equal(await page.evaluate(() => window.__sent.some(message => message.type === 'health/error')), false);
    }
    assert.deepEqual(errors, []);
    console.log('PASS: production chat renders history and answers 36 health pings across reload without script errors');
  } finally {
    await browser?.close();
    await new Promise(resolve => server.close(resolve));
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
