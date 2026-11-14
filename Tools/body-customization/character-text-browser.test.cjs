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
    const state = { available: true, mode: 'flavor', dirty: true, name: 'Test', appearance: {}, previewSlots: [], tabs: [], maxFlavorLength: 4096, flavorText: 'abcdef', oocNotes: '' };
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
    await input.evaluate(e => e.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', code: 'Enter', shiftKey: true, bubbles: true, cancelable: true })));
    await page.waitForFunction(() => document.querySelector('textarea').value === 'abcXY\ndef');
    await page.waitForFunction(() => document.querySelector('textarea').selectionStart === 6);
    await page.keyboard.press('Enter');
    assert.equal(await input.inputValue(), 'abcXY\n\ndef', 'Native browser Enter inserts exactly one newline');
    await page.keyboard.press('Shift+Enter');
    assert.equal(await input.inputValue(), 'abcXY\n\n\ndef', 'Native browser Shift+Enter inserts exactly one newline');
    assert.equal(await input.evaluate(e => e.selectionStart), 8);
    const lastText = await input.inputValue();
    await page.waitForFunction(value => window.__sent.some(m => m.type === 'act/flavor' && m.payload.value === value), lastText);
    await publish({ flavorText: lastText });
    await page.getByRole('button', { name: 'Сохранить', exact: true }).click();
    await page.waitForFunction(() => window.__sent.filter(m => m.type === 'act/save').length === 1);
    await input.focus();
    await input.evaluate(e => { e.setSelectionRange(0, 3); e.dispatchEvent(new MouseEvent('mouseup', { bubbles: true })); });
    const editor = page.locator('.CharacterTextEditor').first();
    const applyColor = editor.getByRole('button', { name: 'Цвет текста', exact: true });
    await applyColor.click();
    const white = '-=#ffffff(abc)=-' + lastText.slice(3);
    assert.equal(await input.inputValue(), white, 'The label applies white without opening the picker');
    assert.equal(await page.evaluate(() => window.__sent.some(m => m.type === 'act/color-picker-open')), false);
    await editor.getByRole('button', { name: 'Изменить цвет текста', exact: true }).click();
    await page.waitForFunction(() => window.__sent.some(m => m.type === 'act/color-picker-open'));
    const picker = await page.evaluate(() => window.__sent.findLast(m => m.type === 'act/color-picker-open').payload.id);
    await publish({ flavorText: white, colorPickerOpen: true, colorPickerResult: { id: picker, sequence: 1, value: '#00ff00' } });
    await page.waitForFunction(() => document.querySelector('[aria-label="Изменить цвет текста"]').style.backgroundColor === 'rgb(0, 255, 0)');
    assert.equal(await input.inputValue(), white, 'Choosing a color leaves text intact');
    await applyColor.click();
    const colored = await input.inputValue();
    assert.equal(colored, '-=#00ff00(abc)=-' + lastText.slice(3), 'Applying a new color replaces the same markup');
    assert.equal(colored.split('-=').length - 1, 1);
    await publish({ flavorText: colored, setup: { selected: 1, characters: [] } });
    await input.focus();
    await page.keyboard.insertText('ещё не подтверждено');
    const otherCharacter = 'Другой персонаж';
    await publish({ flavorText: otherCharacter, maxFlavorLength: otherCharacter.length, setup: { selected: 2, characters: [] } });
    await page.waitForFunction(value => document.querySelector('textarea').value === value, otherCharacter);
    await input.focus();
    await page.keyboard.press('End');
    await page.keyboard.press('Shift+Enter');
    assert.equal(await input.inputValue(), otherCharacter, 'A newline stays within the length limit');
    await input.evaluate(e => e.setSelectionRange(6, 7));
    await page.keyboard.press('Shift+Enter');
    assert.equal(await input.inputValue(), 'Другой\nперсонаж', 'A newline can replace the selected text at the length limit');
    assert.deepEqual(errors, []);
    console.log('PASS: delayed acknowledgements, caret, Enter and Shift+Enter, first button click after editing, white and custom text colors, character switch.');
  } finally { await browser?.close(); await new Promise(resolve => server.close(resolve)); }
})().catch(error => { console.error(error); process.exitCode = 1; });
