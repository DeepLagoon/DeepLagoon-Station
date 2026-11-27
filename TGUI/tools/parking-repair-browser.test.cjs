// Shipped bundle/bridge interaction test; real world/financial validation lives in the C# suites.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const http = require('node:http');
const path = require('node:path');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const root = path.resolve(__dirname, '../..');
const resources = path.join(root, 'Resources/Web/DeepLagoon');
const bundle = path.resolve(process.env.PARKING_REPAIR_BUNDLE_DIR || resources);
const image = 'data:image/png;base64,' + fs.readFileSync(path.join(root, 'Resources/Textures/Objects/Materials/Sheets/metal.rsi/steel.png')).toString('base64');
(async () => {
  const server = http.createServer((req, res) => {
    const relative = new URL(req.url, 'http://localhost').pathname;
    const built = path.resolve(bundle, '.' + relative);
    const fallback = path.resolve(resources, '.' + relative);
    const file = built.startsWith(bundle + path.sep) && fs.existsSync(built) ? built : fallback;
    if (!file.startsWith(bundle + path.sep) && !file.startsWith(resources + path.sep) || !fs.existsSync(file) || !fs.statSync(file).isFile()) return res.writeHead(404).end();
    res.setHeader('Content-Type', ({ '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.woff2': 'font/woff2', '.svg': 'image/svg+xml' })[path.extname(file)] || 'application/octet-stream');
    res.end(fs.readFileSync(file));
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  let browser;
  try {
    browser = await chromium.launch({ channel: process.env.BROWSER_CHANNEL || 'msedge', headless: true });
    const page = await browser.newPage({ viewport: { width: 1050, height: 760 } });
    const errors = []; page.on('pageerror', error => errors.push(String(error)));
    await page.addInitScript(() => {
      window.__sent = []; const Original = window.Image;
      window.Image = class extends Original { set src(value) {
        if (!value.startsWith('res://deeplagoon/Web/DeepLagoon/bridge?')) { super.src = value; return; }
        const [type, payload] = value.split('?')[1].split('&');
        window.__sent.push({ type: decodeURIComponent(type), payload: JSON.parse(decodeURIComponent(payload)) });
        queueMicrotask(() => this.onload?.(new Event('load')));
      } };
    });
    await page.goto(`http://127.0.0.1:${server.address().port}/interface.html`);
    await page.waitForFunction(() => window.__tguiAppBooted__);
    const repair = { ship: 'owned-ship', parked: true, working: false, remaining: 0, token: '', selectionVersion: 'v1', selected: '',
      parts: [{ id: 't:0:0', name: 'Палуба', kind: 'tiles', geometry: 'floor', x: .5, y: .5, rotation: 0, tile: true, visual: 'steel' },
        { id: 'e:0:0:0', name: 'Стена шаттла', kind: 'structures', geometry: 'wall', x: .5, y: 1.5, rotation: 0, visual: 'steel' }],
      current: [{ id: '', x: 1.5, y: 1.5, rotation: 0, tile: true, geometry: 'floor', visual: 'steel' },
        { id: '', x: 2.5, y: 1.5, rotation: 0, geometry: 'equipment', visual: 'steel' }], visuals: [{ id: 'steel', images: [{ url: image }] }] };
    const data = { id: true, hasShip: true, parked: true, busy: false, shipName: 'Повреждённый корабль', credits: '50000', coins: '100', repairEnabled: false, repair };
    const publish = async () => page.evaluate(data => window.update(JSON.stringify({ type: 'update', payload: {
      replaceData: true, replaceStaticData: true, resetPartialData: true,
      config: { interface: 'ShuttleParking', title: 'Парковка шаттлов', status: 2, window: { key: 'deeplagoon', fancy: false } }, static_data: {}, data,
    } })), data);
    const sent = async action => page.evaluate(action => {
      const { _sequence, _page, ...payload } = window.__sent.filter(m => m.type === 'act/' + action).at(-1)?.payload || {};
      return payload;
    }, action);
    await publish();
    assert.equal(await page.getByRole('button', { name: 'Ремонт шаттла', exact: true }).count(), 0);
    data.repairEnabled = true;
    await publish();
    await page.getByRole('button', { name: 'Ремонт шаттла', exact: true }).click();
    await page.locator('.ParkingRepair__viewport svg image').first().waitFor();
    assert.equal(await page.locator('.ParkingRepair__part.wall image').count(), 0, 'Walls use neutral navigation geometry rather than sprites or department fills.');
    await page.getByRole('checkbox', { name: 'Палуба · 1', exact: true }).check();
    await page.getByRole('button', { name: 'Рассчитать выбранный ремонт', exact: true }).click();
    assert.deepEqual(await sent('repairQuote'), { ranges: '0', selectionVersion: 'v1' });
    Object.assign(repair, { token: 'quote-1', selected: 't:0:0', materials: '1000', credits: '1250', coins: '2', seconds: 90 });
    await publish();
    await page.getByRole('dialog', { name: 'Подтверждение ремонта' }).getByRole('radio', { name: /2 LC/ }).check();
    await page.getByRole('button', { name: 'Подтвердить ремонт', exact: true }).click();
    assert.deepEqual(await sent('repairConfirm'), { token: 'quote-1', coins: true });
    await page.getByRole('checkbox', { name: 'Стена шаттла · 0.5, 1.5', exact: true }).check();
    assert.equal(await page.getByRole('button', { name: 'Оплатить кредитами…', exact: true }).isDisabled(), true);
    await page.getByRole('button', { name: 'Рассчитать выбранный ремонт', exact: true }).click();
    assert.equal((await sent('repairQuote')).ranges, '0-1');
    Object.assign(repair, { token: 'quote-2', selectionVersion: 'v2', selected: 't:0:0|e:0:0:0' });
    await publish();
    await page.getByRole('dialog', { name: 'Подтверждение ремонта' }).waitFor();
    await page.getByRole('button', { name: 'Отмена', exact: true }).click();
    const confirmedBefore = await page.evaluate(() => window.__sent.filter(m => m.type === 'act/repairConfirm').length);
    await page.getByRole('button', { name: 'Починить всё', exact: true }).click();
    assert.deepEqual(await sent('repairQuote'), { ranges: '0-1', selectionVersion: 'v2' });
    assert.equal(await page.evaluate(() => window.__sent.filter(m => m.type === 'act/repairConfirm').length), confirmedBefore, 'Repair all only requests a quote.');
    Object.assign(repair, { token: 'quote-all', selectionVersion: 'v3', credits: '9999', coins: '10', seconds: 3600 });
    await publish();
    const dialog = page.getByRole('dialog', { name: 'Подтверждение ремонта' });
    await dialog.waitFor();
    assert.equal(await dialog.getByRole('button', { name: 'Подтвердить ремонт', exact: true }).isDisabled(), true, 'Currency must be chosen before confirmation.');
    assert.match(await dialog.innerText(), /9999 кр\./);
    assert.match(await dialog.innerText(), /60 мин\. 0 с\./);
    assert.match(await dialog.innerText(), /10 LC/);
    for (const width of [1050, 580, 360]) {
      await page.setViewportSize({ width, height: 760 });
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), true, `No horizontal document overflow at ${width}px`);
      const box = await dialog.boundingBox();
      assert.ok(box.x >= 0 && box.x + box.width <= width, `Floating confirmation stays inside ${width}px viewport`);
    }
    await dialog.getByRole('radio', { name: /9999 кр/ }).check();
    await dialog.getByRole('button', { name: 'Подтвердить ремонт', exact: true }).click();
    assert.deepEqual(await sent('repairConfirm'), { token: 'quote-all', coins: false });
    data.repairEnabled = false;
    await publish();
    await page.getByRole('button', { name: 'Ремонт шаттла', exact: true }).waitFor({ state: 'detached' });
    assert.equal(await page.getByRole('button', { name: 'Ремонт шаттла', exact: true }).count(), 0);
    assert.equal(await page.locator('.ParkingRepair').count(), 0);
    assert.deepEqual(errors, []);
    console.log('Parking repair shipped bundle: selection, compact ranges, exact confirmation, stale-choice lock and responsive layout passed.');
  } finally { if (browser) await browser.close(); await new Promise(resolve => server.close(resolve)); }
})().catch(error => { console.error(error); process.exitCode = 1; });
