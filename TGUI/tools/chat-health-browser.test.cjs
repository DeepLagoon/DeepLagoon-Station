// Exercise the standalone production chat entry point and its native health bridge.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const http = require('node:http');
const path = require('node:path');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const root = path.resolve(__dirname, '../../Resources/Web/DeepLagoon');
const bridgeAck = Buffer.from('R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7', 'base64');

(async () => {
  const received = [];
  const server = http.createServer((request, response) => {
    const url = new URL(request.url, 'http://localhost');
    if (url.pathname === '/bridge') {
      const type = decodeURIComponent(url.search.slice(1).split('&', 1)[0]);
      received.push(type);
      // Exercise the legacy Image transport with real browser caching.
      response.writeHead(200, { 'Content-Type': 'image/gif', 'Cache-Control': 'max-age=3600' }).end(bridgeAck);
      return;
    }
    const file = path.resolve(root, '.' + url.pathname);
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
      const NativeImage = window.Image;
      window.Image = class extends NativeImage {
        set src(value) {
          const prefix = 'res://deeplagoon/Web/DeepLagoon/bridge?';
          super.src = value.startsWith(prefix) ? '/bridge?' + value.slice(prefix.length) : value;
        }
      };
    });
    for (let reload = 0; reload < 2; reload++) {
      received.length = 0;
      if (reload) await page.reload();
      else await page.goto(`http://127.0.0.1:${server.address().port}/chat.html`);
      await page.waitForFunction(() => window.__tguiAppBooted__ === true, null, { timeout: 5000 });
      const waitForRequests = async (type, count) => {
        const deadline = Date.now() + 3000;
        while (received.filter(message => message === type).length < count && Date.now() < deadline) {
          await new Promise(resolve => setTimeout(resolve, 10));
        }
        assert.equal(received.filter(message => message === type).length, count, `Requests reaching the bridge for ${type}`);
      };
      await waitForRequests('ready', 1);
      for (let tick = 0; tick < 18; tick++) {
        await page.evaluate(tick => {
          window.update(JSON.stringify({ type: 'chat/replace', payload: [
            { type: 'system', html: `Chat health message ${tick}` },
          ] }));
          window.DeepLagoonHealth.ping();
        }, tick);
        await waitForRequests('health/pong', tick + 1);
      }
      await page.getByText('Chat health message 17', { exact: true }).waitFor();
      assert(!received.includes('health/error'));
    }
    assert.deepEqual(errors, []);
    console.log('PASS: production chat renders history and answers 36 health pings across reload without script errors');
  } finally {
    await browser?.close();
    await new Promise(resolve => server.close(resolve));
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
