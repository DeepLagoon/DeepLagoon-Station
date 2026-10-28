const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const repo = path.resolve(__dirname, '../..');
const root = path.join(repo, 'Resources/Web/DeepLagoon');
const labels = Object.fromEntries(['interaction_panel.ftl', 'body-customization.ftl'].flatMap(file =>
  [...fs.readFileSync(path.join(repo, 'Resources/Locale/ru-RU/_DeepLagoon', file), 'utf8')
    .matchAll(/^dl-interaction-panel-([\w-]+)\s*=\s*(.+)$/gm)].map(match => [match[1], match[2].trim()])));

// A native popup can overwrite the entire CEF view buffer. Keep this guard
// across all interfaces so new HTML select elements cannot reintroduce it.
for (const entry of fs.readdirSync(path.join(repo, 'TGUI/packages/tgui'), { recursive: true })) {
  if (!/\.(jsx?|tsx?)$/.test(entry)) continue;
  const source = fs.readFileSync(path.join(repo, 'TGUI/packages/tgui', entry), 'utf8');
  assert.ok(!/<select\b|createElement\(['"]select['"]/.test(source), `Native CEF popup in ${entry}`);
}
(async () => {
  const { createViteConfig } = require('../vite.base.config.cjs');
  const plugin = createViteConfig({ entry: '', bundleName: 'test', globalName: 'test' })({ mode: 'production' }).plugins[0];
  await assert.rejects(() => plugin.transform.call({ error: message => { throw new Error(message); } },
    '<select><option>Regression</option></select>', '/packages/tgui/interfaces/Regression.tsx'), /Use EmbeddedSelect or Dropdown/);
  const server = http.createServer((req, res) => {
    const file = path.resolve(root, '.' + new URL(req.url, 'http://localhost').pathname);
    if (!file.startsWith(root + path.sep) || !fs.existsSync(file)) { res.writeHead(404).end(); return; }
    res.setHeader('Content-Type', ({ '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.woff2': 'font/woff2' })[path.extname(file)] || 'application/octet-stream');
    res.end(fs.readFileSync(file));
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
        set src(url) {
          if (!url.startsWith('res://deeplagoon/Web/DeepLagoon/bridge?')) { super.src = url; return; }
          const [type, payload] = url.split('?')[1].split('&');
          window.__sent.push({ type: decodeURIComponent(type), payload: JSON.parse(decodeURIComponent(payload)) });
          queueMicrotask(() => this.onload?.(new Event('load')));
        }
      };
    });
    await page.goto(`http://127.0.0.1:${server.address().port}/interface.html`);
    await page.waitForFunction(() => window.__tguiAppBooted__);
    const data = { name: 'Персонаж', self: true, humanoid: true, interval: 3, status: 'Готово', labels,
      regions: [], categories: [], actions: [], custom: [], sounds: [], libraryLoaded: true,
      bodyFeatures: [{ part: 'breasts', name: 'Грудь', visibility: 1 }], arousalCurrent: 0, arousalMaximum: 100 };
    await page.evaluate(data => window.update(JSON.stringify({ type: 'update', payload: { replaceData: true,
      config: { interface: 'InteractionPanel', status: 2, window: { key: 'select-regression', fancy: false } }, data } })), data);
    await page.getByRole('button', { name: labels['tab-body'], exact: true }).click();
    const combo = page.getByRole('combobox', { name: 'Грудь', exact: true });
    for (const width of [420, 360, 800]) {
      await page.setViewportSize({ width, height: 560 });
      for (let repeat = 0; repeat < 3; repeat++) {
        await combo.click();
        const menu = page.getByRole('listbox');
        const anchor = await combo.boundingBox();
        const box = await menu.boundingBox();
        assert.ok(box.x >= 0 && box.x + box.width <= width + 1);
        assert.ok(box.y >= anchor.y + anchor.height && box.y <= anchor.y + anchor.height + 4, 'Menu must open beside the clicked control');
        await page.getByRole('option', { name: labels['body-over'], exact: true }).hover();
        if (width === 420 && repeat === 0) {
          const output = path.join(repo, '.local/codex/tgui-fixes/menu-420.png');
          fs.mkdirSync(path.dirname(output), { recursive: true });
          await page.screenshot({ path: output });
        }
        await page.getByRole('option', { name: labels['body-over'], exact: true }).click();
        assert.equal(await page.getByRole('listbox').count(), 0);
        assert.ok(await page.locator('.InteractionPanel__title').isVisible());
        assert.ok(await page.locator('.InteractionPanel__bodySettings').isVisible());
        assert.equal(await page.locator('select').count(), 0);
      }
    }
    assert.ok(await page.evaluate(() => window.__sent.some(message => message.type === 'act/body-visibility'
      && message.payload.id === 'breasts' && message.payload.value === 2)));
    await combo.focus(); await page.keyboard.press('ArrowDown');
    await page.keyboard.press('Home'); await page.keyboard.press('Enter');
    assert.ok(await page.evaluate(() => window.__sent.some(message => message.type === 'act/body-visibility' && message.payload.value === 0)));
    assert.equal(await combo.evaluate(node => node === document.activeElement), true);
    await combo.click(); await page.keyboard.press('Escape');
    assert.equal(await page.getByRole('listbox').count(), 0);
    await combo.click();
    await page.getByRole('button', { name: labels['tab-actions'], exact: true }).click();
    assert.equal(await page.getByRole('listbox').count(), 0, 'Unmounted controls must remove their portals');
    await page.getByRole('button', { name: labels['tab-body'], exact: true }).click();
    await combo.click(); await page.setViewportSize({ width: 360, height: 420 });
    await page.waitForFunction(() => !document.querySelector('[role=listbox]'));
    assert.deepEqual(errors, []);
    console.log('PASS: no native selects across TGUI; anchored menus, repeated hover/click, action payloads, keyboard, Escape, resize and tab cleanup at 360/420/800px.');
  } finally { await browser?.close(); await new Promise(resolve => server.close(resolve)); }
})().catch(error => { console.error(error); process.exitCode = 1; });