const assert = require('node:assert/strict');
const fs = require('node:fs');
const http = require('node:http');
const path = require('node:path');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const root = path.resolve(__dirname, '../../Resources/Web/DeepLagoon');
(async () => {
  const server = http.createServer((request, response) => {
    const file = path.resolve(root, '.' + new URL(request.url, 'http://localhost').pathname);
    if (!file.startsWith(root + path.sep) || !fs.existsSync(file)) return response.writeHead(404).end();
    response.setHeader('Content-Type', ({ '.html': 'text/html', '.js': 'application/javascript', '.css': 'text/css' })[path.extname(file)] || 'application/octet-stream');
    response.end(fs.readFileSync(file));
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  let browser;
  try {
    browser = await chromium.launch({ channel: 'msedge', headless: true });
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
    const state = { available: true, mode: 'flavor', name: 'Test', appearance: {}, previewSlots: [], tabs: [], maxFlavorLength: 4096, flavorText: 'abcdef', oocNotes: '' };
    const publish = patch => page.evaluate(data => window.update(JSON.stringify({ type: 'update', payload: {
      replaceData: true, replaceStaticData: true, resetPartialData: true,
      config: { interface: 'CharacterEditor', status: 2, window: { key: 'text-test', fancy: false } }, data,
    } })), { ...state, ...patch });
    await publish();
    const input = page.getByRole('textbox', { name: 'Описание персонажа', exact: true });
    await input.focus();
    await input.evaluate(e => e.setSelectionRange(3, 3));
    await page.keyboard.insertText('X');
    await page.keyboard.insertText('Y');
    assert.equal(await input.inputValue(), 'abcXYdef', 'Typing must survive delayed native acknowledgements');
    await publish({ flavorText: 'abcXdef' });
    assert.equal(await input.inputValue(), 'abcXYdef', 'An older acknowledgement must not overwrite newer typing');
    assert.equal(await input.evaluate(e => e.selectionStart), 5);
    await publish({ flavorText: 'abcXYdef' });
    await input.evaluate(e => e.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', code: 'Enter', keyCode: 13, bubbles: true, cancelable: true })));
    await page.waitForFunction(() => document.querySelector('textarea').value === 'abcXY\ndef');
    await page.waitForFunction(() => document.querySelector('textarea').selectionStart === 6);
    await page.keyboard.press('Enter');
    assert.equal(await input.inputValue(), 'abcXY\n\ndef', 'Native browser Enter inserts exactly one newline');
    const lastText = await input.inputValue();
    await publish({ flavorText: lastText });
    await input.evaluate(e => { e.setSelectionRange(0, 3); e.dispatchEvent(new MouseEvent('mouseup', { bubbles: true })); });
    const editor = page.locator('.CharacterTextEditor').first();
    await editor.getByRole('button', { name: 'Цвет текста', exact: true }).click();
    await page.waitForFunction(() => window.__sent.some(m => m.type === 'act/color-picker-open'));
    const picker = await page.evaluate(() => window.__sent.findLast(m => m.type === 'act/color-picker-open').payload.id);
    await publish({ flavorText: lastText, colorPickerOpen: true, colorPickerResult: { id: picker, sequence: 1, value: '#ff0000' } });
    await page.waitForTimeout(100);
    assert.equal(await input.inputValue(), '-=#ff0000(abc)=-' + lastText.slice(3));
    await publish({ flavorText: lastText, colorPickerOpen: true, colorPickerResult: { id: picker, sequence: 2, value: '#00ff00' } });
    await page.waitForFunction(() => document.querySelector('textarea').value.startsWith('-=#00ff00(abc)=-'));
    const colored = await input.inputValue();
    assert.equal(colored, '-=#00ff00(abc)=-' + lastText.slice(3), 'Dragging/changing color replaces the same markup');
    assert.equal(colored.split('-=').length - 1, 1);
    await publish({ flavorText: colored, setup: { selected: 1, characters: [] } });
    await input.focus();
    await page.keyboard.insertText('ещё не подтверждено');
    await publish({ flavorText: 'Другой персонаж', setup: { selected: 2, characters: [] } });
    await page.waitForFunction(() => document.querySelector('textarea').value === 'Другой персонаж');
    assert.deepEqual(errors, []);
    console.log('PASS: delayed acknowledgements, caret, synthetic/real Enter, repeated color edits, character switch.');
  } finally { await browser?.close(); await new Promise(resolve => server.close(resolve)); }
})().catch(error => { console.error(error); process.exitCode = 1; });
