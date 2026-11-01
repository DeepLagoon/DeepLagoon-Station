// The signed CEF drops the U+041B Char event. Exercise recovery using only
// its surviving keydown, against the production composer and action bridge.
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
      config: { interface: 'ChatComposer', status: 2, window: { key: 'unicode', fancy: false } },
      data: { session: 1, revision: 1, text: '', channel: '1', emoteChannel: '512', maxLength: 1000,
        channels: [{ id: '1', name: 'Say' }, { id: '512', name: 'Emote' }] },
    } })));
    const input = page.getByRole('textbox', { name: 'Сообщение', exact: true });
    await input.waitFor();
    await input.focus();
    const letter = (extra = {}) => input.evaluate((element, extra) => !element.dispatchEvent(new KeyboardEvent('keydown', {
      key: 'Л', keyCode: 75, which: 75, shiftKey: true, bubbles: true, cancelable: true, ...extra,
    })), extra);
    assert.equal(await letter(), true, 'Cancel native default to avoid duplicate text on fixed modules');
    assert.equal(await input.inputValue(), 'Л', 'Recover the letter when CEF sends no keypress/input');
    await page.waitForFunction(() => window.__sent.some(m => m.type === 'act/draft' && m.payload.text === 'Л'));
    assert.equal(await page.evaluate(() => window.__sent.some(m => m.type === 'act/cancel' || m.type === 'act/submit')), false);

    await input.fill('aBCz');
    await input.evaluate(element => element.setSelectionRange(1, 3));
    await letter();
    assert.equal(await input.inputValue(), 'aЛz');
    assert.deepEqual(await input.evaluate(element => [element.selectionStart, element.selectionEnd]), [2, 2]);
    await input.press('Control+z');
    assert.equal(await input.inputValue(), 'aBCz', 'Recovery participates in native undo');
    await input.press('Control+y');
    assert.equal(await input.inputValue(), 'aЛz', 'Recovery participates in native redo');

    for (const extra of [{ ctrlKey: true }, { altKey: true }, { metaKey: true }, { isComposing: true }]) {
      assert.equal(await letter(extra), false, 'Leave shortcuts and IME to their existing handlers');
      assert.equal(await input.inputValue(), 'aЛz');
    }
    await input.evaluate(element => element.dispatchEvent(new CompositionEvent('compositionstart', { bubbles: true })));
    assert.equal(await letter(), false);
    await input.evaluate(element => element.dispatchEvent(new CompositionEvent('compositionend', { bubbles: true })));
    await input.fill('');
    await letter(); await letter({ repeat: true });
    assert.equal(await input.inputValue(), 'ЛЛ', 'Key repeat must keep working');
    await page.keyboard.insertText('л Ёё English 😀');
    assert.equal(await input.inputValue(), 'ЛЛл Ёё English 😀');

    await page.evaluate(() => window.update(JSON.stringify({ type: 'update', payload: {
      data: { revision: 2, text: '1234', maxLength: 4 },
    } })));
    await input.evaluate(element => element.setSelectionRange(4, 4));
    await letter();
    assert.equal(await input.inputValue(), '1234', 'Do not bypass maxlength');
    await input.evaluate(element => element.setSelectionRange(1, 2));
    await letter();
    assert.equal(await input.inputValue(), '1Л34', 'Replacing a selection is allowed at maxlength');
    await input.press('Enter');
    await page.waitForFunction(() => window.__sent.some(m => m.type === 'act/submit' && m.payload.text === '1Л34'));
    await input.press('Escape');
    await page.waitForFunction(() => window.__sent.some(m => m.type === 'act/cancel'));
    assert.deepEqual(errors, []);
    console.log('PASS: dropped U+041B Char recovery, draft/submit, selection, caret, undo/redo, repeat, maxlength, shortcuts, IME, other text and real Escape');
  } finally {
    await browser?.close();
    await new Promise(resolve => server.close(resolve));
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
