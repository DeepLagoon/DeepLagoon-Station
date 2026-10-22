const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const resources = path.resolve(__dirname, '../../Resources/Web/DeepLagoon');
const data = JSON.parse(fs.readFileSync(path.resolve(__dirname, '../../.local/codex/apartment-interior/apartment-room-preview.json'), 'utf8'));

(async () => {
  const server = http.createServer((req, res) => {
    const file = path.resolve(resources, '.' + new URL(req.url, 'http://localhost').pathname);
    if (!file.startsWith(resources + path.sep) || !fs.existsSync(file)) { res.writeHead(404).end(); return; }
    res.setHeader('Content-Type', ({ '.html': 'text/html', '.js': 'application/javascript', '.css': 'text/css' })[path.extname(file)] || 'application/octet-stream');
    res.end(fs.readFileSync(file));
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  let browser;
  try {
    browser = await chromium.launch({ channel: 'msedge', headless: true });
    const page = await browser.newPage({ viewport: { width: 940, height: 650 } });
    const errors = [];
    page.on('pageerror', error => errors.push(String(error)));
    await page.addInitScript(() => {
      const NativeImage = window.Image;
      window.Image = class extends NativeImage {
        set src(value) {
          if (!value.startsWith('res://deeplagoon/Web/DeepLagoon/bridge?')) { super.src = value; return; }
          queueMicrotask(() => this.onload?.(new Event('load')));
        }
      };
    });
    await page.goto(`http://127.0.0.1:${server.address().port}/interface.html`);
    await page.waitForFunction(() => window.__tguiAppBooted__);
    await page.evaluate(data => window.update(JSON.stringify({ type: 'update', payload: {
      replaceData: true, config: { interface: 'ApartmentEditor', status: 2, title: 'Квартира', window: { key: 'apartment-room', fancy: false } }, data,
    } })), data);
    await page.locator('.ApartmentEditor__fixture').first().waitFor({ state: 'attached' });
    const occupiedCells = new Set(data.scene.occupied.map(cell => `${cell.x},${cell.y}`));
    assert.equal(await page.locator('.ApartmentEditor__fixture').count(), data.scene.objects.filter(object => occupiedCells.has(`${Math.floor(object.x)},${Math.floor(object.y)}`)).length);
    assert.equal(await page.locator('.ApartmentEditor__ghost .ApartmentEditor__planSprite').count(), data.layout.length);
    await page.waitForFunction(() => [...document.querySelectorAll('.ApartmentEditor__planSprite img')].every(image => image.complete && image.naturalWidth > 0));
    for (const item of data.layout) {
      const definition = data.catalog.find(entry => entry.id === item.furniture);
      assert.ok(definition.images?.length > 0, `Missing sprite: ${definition.name}`);
      assert.ok(await page.locator(`[data-placement="${item.id}"] img`).count() > 0);
    }
    assert.equal(await page.locator('.ApartmentEditor__tile.void').count(), data.width * data.height - data.scene.tiles.length);
    assert.ok(await page.locator('.ApartmentEditor__tile.wall').count() > 0);
    assert.match(await page.locator('.ApartmentEditor header').innerText(), new RegExp(String(data.number)));
    const checkFit = async () => {
      await page.waitForFunction(() => {
        const view = document.querySelector('.ApartmentEditor__viewport');
        const plan = document.querySelector('.ApartmentEditor__plan').getBoundingClientRect();
        const box = view.getBoundingClientRect();
        return plan.width <= view.clientWidth - 28 && plan.height <= view.clientHeight - 28
          && Math.abs(plan.x + plan.width / 2 - box.x - view.clientWidth / 2) < 3
          && Math.abs(plan.y + plan.height / 2 - box.y - view.clientHeight / 2) < 3;
      });
    };
    for (const size of [{ width: 620, height: 440 }, { width: 1366, height: 768 }, { width: 1920, height: 1080 }, { width: 940, height: 650 }]) {
      await page.setViewportSize(size);
      await checkFit();
      const cell = await page.locator('[data-cell]').first().boundingBox();
      assert.ok(Math.abs(cell.width - cell.height) < 1);
      assert.ok(await page.locator('.ApartmentEditor__viewport').evaluate(node => node.clientHeight) > 40);
      await page.screenshot({ path: path.resolve(__dirname, `../../.local/codex/apartment-interior/apartment-${size.width}x${size.height}.png`) });
    }
    await page.getByRole('button', { name: 'Сетка', exact: true }).click();
    assert.equal(await page.locator('.ApartmentEditor__plan.grid').count(), 0);
    await page.getByRole('button', { name: 'Сетка', exact: true }).click();
    assert.equal(await page.locator('.ApartmentEditor__plan.grid').count(), 1);
    const rect = await page.locator('.ApartmentEditor__plan').boundingBox();
    assert.ok(Math.abs(rect.width / rect.height - data.width / data.height) < .01);
    await page.screenshot({ path: path.resolve(__dirname, '../../.local/codex/apartment-interior/apartment-editor.png') });
    const fixed = new Set(data.scene.occupied.map(cell => `${cell.x},${cell.y}`));
    const reserved = new Set(data.reservedCells.map(cell => `${cell.x},${cell.y}`));
    const free = data.scene.tiles.filter(cell => !fixed.has(`${cell.x},${cell.y}`) && !reserved.has(`${cell.x},${cell.y}`) && !data.layout.some(item => item.x === cell.x && item.y === cell.y) && (cell.x !== data.arrivalX || cell.y !== data.arrivalY));
    const mapped = data.catalog.find(item => item.id === 'map_80') || data.catalog.find(item => item.existingOnly);
    const original = data.layout.find(item => item.id === mapped.placementId);
    const mappedIndex = data.catalog.filter(item => !item.existingOnly).length + data.catalog.filter(item => item.existingOnly).findIndex(item => item.id === mapped.id);
    await page.locator('.ApartmentEditor aside button').nth(mappedIndex).click();
    assert.equal(await page.getByRole('button', { name: 'Убрать · Delete', exact: true }).isDisabled(), true);
    await page.getByRole('button', { name: 'Увеличить масштаб', exact: true }).click();
    const enlarged = await page.locator('.ApartmentEditor__plan').boundingBox();
    assert.ok(enlarged.width > rect.width);
    const viewport = page.locator('.ApartmentEditor__viewport');
    await viewport.hover();
    const zoomBeforeWheel = await page.getByLabel('Масштаб', { exact: true }).innerText();
    await page.mouse.wheel(0, -200);
    await page.waitForFunction(previous => document.querySelector('[aria-label="Масштаб"]').textContent !== previous, zoomBeforeWheel);
    const camera = () => page.locator('.ApartmentEditor__viewport').evaluate(node => {
      const plan = node.querySelector('.ApartmentEditor__plan').getBoundingClientRect();
      return { width: plan.width, height: plan.height, x: node.scrollLeft / plan.width, y: node.scrollTop / plan.height };
    });
    const manual = await camera();
    await page.setViewportSize({ width: 1200, height: 800 });
    await page.waitForFunction(previous => {
      const node = document.querySelector('.ApartmentEditor__viewport');
      const plan = node.querySelector('.ApartmentEditor__plan').getBoundingClientRect();
      return Math.abs(node.scrollLeft / plan.width - previous.x) < .01 && Math.abs(node.scrollTop / plan.height - previous.y) < .01
        && Math.abs(node.querySelector('.ApartmentEditor__surface').offsetWidth - plan.width - node.clientWidth) < 2;
    }, manual);
    const resized = await camera();
    assert.ok(Math.abs(resized.width - manual.width) < 1);
    assert.ok(Math.abs(resized.x - manual.x) < .01 && Math.abs(resized.y - manual.y) < .01);
    const panBox = await viewport.boundingBox();
    const previousScroll = await viewport.evaluate(node => ({ x: node.scrollLeft, y: node.scrollTop }));
    await page.mouse.move(panBox.x + panBox.width / 2, panBox.y + panBox.height / 2);
    await page.mouse.down({ button: 'right' });
    await page.mouse.move(panBox.x + panBox.width / 2 + 50, panBox.y + panBox.height / 2 + 35, { steps: 5 });
    await page.mouse.up({ button: 'right' });
    const nextScroll = await viewport.evaluate(node => ({ x: node.scrollLeft, y: node.scrollTop }));
    assert.ok(Math.abs(previousScroll.x - nextScroll.x - 50) < 2 && Math.abs(previousScroll.y - nextScroll.y - 35) < 2);
    assert.equal(await page.locator('.ApartmentEditor footer .primary').isDisabled(), true);
    const cell = page.locator(`[data-cell="${free[0].x},${free[0].y}"]`);
    await cell.scrollIntoViewIfNeeded();
    await cell.click();
    const movedBox = await page.locator(`[data-placement="${original.id}"]`).boundingBox();
    const cellBox = await cell.boundingBox();
    assert.ok(Math.abs(movedBox.x - cellBox.x) < 3 && Math.abs(movedBox.y - cellBox.y) < 3);
    assert.equal(await page.locator('.ApartmentEditor__ghost.invalid').count(), 0);
    assert.equal(await page.locator('.ApartmentEditor footer .primary').isEnabled(), true);
    await page.keyboard.press('Delete');
    assert.equal(await page.locator(`[data-placement="${original.id}"]`).count(), 1);
    await page.getByRole('button', { name: 'Отменить', exact: true }).click();
    assert.equal(await page.locator('.ApartmentEditor footer .primary').isDisabled(), true);
    await page.locator('.ApartmentEditor aside button').nth(data.catalog.findIndex(item => item.id === 'DLAptChair')).click();
    await page.locator(`[data-cell="${free[1].x},${free[1].y}"]`).click();
    assert.equal(await page.locator('.ApartmentEditor__ghost:not(.hover)').count(), data.layout.length + 1);
    const obstacle = data.scene.occupied.find(cell => !reserved.has(`${cell.x},${cell.y}`) && (cell.x !== data.arrivalX || cell.y !== data.arrivalY));
    await page.locator(`[data-cell="${obstacle.x},${obstacle.y}"]`).click();
    assert.equal(await page.locator('.ApartmentEditor__ghost.invalid').count(), 1);
    assert.equal(await page.locator('.ApartmentEditor footer .primary').isDisabled(), true);
    await page.getByRole('button', { name: 'Сбросить изменения', exact: true }).click();
    await page.getByRole('button', { name: 'Уместить', exact: true }).click();
    await checkFit();
    await page.screenshot({ path: path.resolve(__dirname, '../../.local/codex/apartment-interior/apartment-editor.png') });
    assert.deepEqual(errors, []);
    console.log(`PASS: real apartment snapshot, ${data.scene.tiles.length} tiles, ${data.scene.objects.length} fixtures, responsive square grid at 620x440 through 1920x1080, resize/zoom/pan, original furniture movement, undo and protected fixtures`);
  } finally {
    await browser?.close();
    await new Promise(resolve => server.close(resolve));
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
