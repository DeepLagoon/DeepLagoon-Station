// Run against the rebuilt production bundle. PLAYWRIGHT_MODULE selects an installed Playwright package.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const http = require('node:http');
const path = require('node:path');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const repo = path.resolve(__dirname, '../..');
const assets = path.join(repo, 'Resources/Web/DeepLagoon');
const output = path.join(repo, '.local/codex/ahelp-main');

(async () => {
  const server = http.createServer((request, response) => {
    const file = path.resolve(assets, '.' + new URL(request.url, 'http://localhost').pathname);
    if (!file.startsWith(assets + path.sep) || !fs.existsSync(file)) return response.writeHead(404).end();
    response.setHeader('Content-Type', ({ '.html': 'text/html', '.js': 'application/javascript', '.css': 'text/css' })[path.extname(file)] || 'application/octet-stream');
    response.end(fs.readFileSync(file));
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  let browser;
  try {
    browser = await chromium.launch({ channel: process.env.BROWSER_CHANNEL || 'msedge', headless: true });
    const page = await browser.newPage({ viewport: { width: 850, height: 600 } });
    const errors = [];
    page.on('pageerror', error => errors.push(String(error)));
    await page.addInitScript(() => {
      window.__sent = [];
      window.__deeplagoonFetchBridge = true;
      const nativeFetch = window.fetch.bind(window);
      window.fetch = async (url, options) => {
        if (!url.startsWith('res://deeplagoon/Web/DeepLagoon/bridge')) return nativeFetch(url, options);
        const [type, payload] = url.split('?')[1].split('&');
        window.__sent.push({ type: decodeURIComponent(type), payload: JSON.parse(decodeURIComponent(payload)) });
        return { ok: true, arrayBuffer: async () => new ArrayBuffer(0) };
      };
    });
    await page.goto(`http://127.0.0.1:${server.address().port}/interface.html`);
    const data = { ticket: 'player/old-round', round: 'old-round', writable: true, busy: true, sent: 0, focusComposer: true, lines: [
      { id: 1, author: 'Игрок', color: '#ffffff', at: '12:00:00', text: 'Нужна помощь', file: 'image.png', loading: true },
    ], pending: [], thumbnails: [] };
    const publish = data => page.evaluate(data => window.update(JSON.stringify({ type: 'update', payload: {
      replaceData: true, config: { interface: 'AhelpTicket', status: 2, window: { key: 'deeplagoon', fancy: false } }, data,
    } })), data);
    await publish(data);
    const input = page.getByRole('textbox', { name: 'Сообщение' });
    await input.waitFor();
    for (const text of ['Первое', 'Второе', 'Третье']) {
      await input.fill(text);
      await input.press('Enter');
      assert.equal(await input.inputValue(), '');
    }
    await page.waitForFunction(() => window.__sent.filter(message => message.type === 'act/send').length === 3);
    assert.deepEqual(await page.evaluate(() => window.__sent.filter(message => message.type === 'act/send').map(message => message.payload.text)), ['Первое', 'Второе', 'Третье']);
    assert.deepEqual(await page.evaluate(() => window.__sent.filter(message => message.type === 'act/send').map(message => message.payload.round)), ['old-round', 'old-round', 'old-round']);
    await input.fill('Следующий черновик');
    await page.waitForFunction(() => window.__sent.some(message => message.type === 'act/typing' && message.payload.active === true && message.payload.round === 'old-round'));
    await input.blur();
    await page.waitForFunction(() => window.__sent.some(message => message.type === 'act/typing' && message.payload.active === false));
    await publish({ ...data, sent: 3 });
    assert.equal(await input.inputValue(), 'Следующий черновик', 'A delayed acknowledgement must preserve the next draft');
    assert.equal(await page.locator('.AhelpTicket__spinner').count(), 1);
    assert.equal(await page.locator('.AhelpTicket').evaluate(element => getComputedStyle(element).backgroundColor), 'rgb(36, 36, 36)');
    await publish({ ...data, sent: 3, lines: [{ ...data.lines[0], loading: false, previewUnavailable: true }],
      pending: [{ id: 'failed', text: 'Не потерять текст', error: 'Ошибка доставки' }] });
    assert.equal(await input.inputValue(), 'Следующий черновик');
    await page.getByRole('button', { name: 'Повторить', exact: true }).click();
    await page.waitForFunction(() => window.__sent.some(message => message.type === 'act/retry-send' && message.payload.id === 'failed'));
    await publish({ ...data, sent: 3, lines: [{ ...data.lines[0], file: 'voice.ogg', mime: 'audio/ogg', loading: false }],
      previewId: 1, preview: 'data:audio/ogg;base64,T2dnUw==' });
    assert.equal(await page.locator('audio[controls]').count(), 1);
    assert.equal(await input.inputValue(), 'Следующий черновик');
    await publish({ ...data, ticket: 'player/new-round', round: 'new-round', lines: [], pending: [] });
    await page.waitForFunction(() => document.querySelector('input[aria-label="Сообщение"]').value === '');
    assert.equal(await page.locator('.AhelpTicket__history article').count(), 0);
    assert.deepEqual(errors, []);
    fs.mkdirSync(output, { recursive: true });
    await page.screenshot({ path: path.join(output, 'ahelp-browser.png') });
    console.log('Ahelp production browser: rapid sends, draft preservation, media, private retry and round reset passed.');
  } finally {
    await browser?.close();
    await new Promise(resolve => server.close(resolve));
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
