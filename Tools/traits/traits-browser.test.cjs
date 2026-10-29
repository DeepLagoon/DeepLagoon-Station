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
    response.setHeader('Content-Type', ({ '.html': 'text/html', '.js': 'application/javascript', '.css': 'text/css', '.woff2': 'font/woff2' })[path.extname(file)] || 'application/octet-stream');
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
    const publish = data => page.evaluate(data => window.update(JSON.stringify({ type: 'update', payload: {
      replaceData: true, replaceStaticData: true, resetPartialData: true,
      config: { interface: 'CharacterEditor', status: 2, window: { key: 'traits-test', fancy: false } }, data,
    } })), data);
    const state = { available: true, mode: 'traits', dirty: true, traitsValid: true, name: 'Тест', species: 'Human',
      appearance: {}, tabs: [], previewSlots: [], traitBudget: { points: 8, slots: 2, maxSlots: 14, valid: true, reasons: [] },
      traits: [
        { id: 'LatentPsychic', name: 'Скрытый псионик', description: 'Даёт псионический потенциал', category: 'Псионика', points: -4, slots: 1, selected: true, available: true, canSelect: true },
        { id: 'Singer', name: 'Певец', description: 'Позволяет петь', category: 'Слух и голос', points: -1, slots: 1, selected: false, available: true, canSelect: true },
        { id: 'HighPotential', name: 'Высокий потенциал', description: 'Повышает потенциал', category: 'Псионика', points: -10, slots: 1, selected: false, available: false, canSelect: false, reason: 'Недостаточно очков черт.' },
      ] };
    for (const width of [1100, 800, 480]) {
      await page.setViewportSize({ width, height: 800 });
      await publish(state);
      assert.ok(await page.getByRole('status').filter({ hasText: 'Осталось очков: 8' }).isVisible());
      assert.equal(await page.getByRole('button', { name: /^Высокий потенциал/ }).count(), 0);
      await page.getByRole('button', { name: 'Показать недоступные', exact: true }).click();
      const unavailable = page.getByRole('button', { name: /^Высокий потенциал/ });
      assert.equal(await unavailable.isDisabled(), true);
      await unavailable.hover();
      assert.ok(await page.locator('.Tooltip').filter({ hasText: 'Недостаточно очков' }).isVisible());
      await page.getByRole('searchbox', { name: 'Поиск черт' }).fill('Певец');
      await page.getByRole('button', { name: /^Певец/ }).click();
      assert.ok(await page.evaluate(() => window.__sent.some(message => message.type === 'act/trait' && message.payload.id === 'Singer')));
      await page.getByRole('searchbox', { name: 'Поиск черт' }).fill('');
      await publish({ ...state, traitsValid: false, traitBudget: { ...state.traitBudget, points: -2, valid: false, reasons: ['Недостаточно очков черт.'] } });
      assert.ok(await page.getByRole('alert').isVisible());
      assert.equal(await page.getByRole('button', { name: 'Сохранить', exact: true }).isDisabled(), true);
      assert.equal(await page.getByRole('button', { name: /^Скрытый псионик/ }).isDisabled(), false, 'Invalid selections remain removable');
      assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), `Horizontal overflow at ${width}`);
      await page.getByRole('button', { name: 'Показать недоступные', exact: true }).click();
    }
    assert.deepEqual(errors, []);
    console.log('PASS: trait budget, slot display, search, unavailable reasons, removal, save guard, 1100/800/480px, console');
  } finally {
    await browser?.close();
    await new Promise(resolve => server.close(resolve));
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
