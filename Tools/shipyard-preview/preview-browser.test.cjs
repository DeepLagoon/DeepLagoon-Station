const assert = require('node:assert/strict');
const fs = require('node:fs');
const http = require('node:http');
const path = require('node:path');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const root = path.resolve(__dirname, '../../Resources/Web/DeepLagoon');
(async () => {
  const { navMapGeometry } = await import('../../TGUI/packages/tgui/interfaces/navMapPlan.mjs');
  const geometry = navMapGeometry([{ x: -1, y: -1 }], [{ x: -.5, y: -.5 }, { x: .5, y: -.5 }], { x: -1, y: -1, width: 2, height: 1 });
  assert.equal(geometry.floors, 'M0,0h1v1h-1z');
  assert.equal(geometry.edges, undefined, 'Walls use prototype sprites instead of schematic borders.');
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
    const page = await browser.newPage({ viewport: { width: 1000, height: 720 } }), errors = [];
    page.on('pageerror', error => errors.push(String(error)));
    await page.addInitScript(() => {
      window.__sent = [];
      const ImageClass = window.Image;
      window.Image = class extends ImageClass {
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
    const publish = (iface, data) => page.evaluate(({ iface, data }) => window.update(JSON.stringify({ type: 'update', payload: {
      replaceData: true, replaceStaticData: true, resetPartialData: true,
      config: { interface: iface, status: 2, window: { key: iface, fancy: false } }, data,
    } })), { iface, data });
    const tiles = Array.from({ length: 80 }, (_, i) => ({ x: i % 10 - 5, y: Math.floor(i / 10) - 4 }));
    const structures = [...Array.from({ length: 10 }, (_, i) => ({ x: i - 4.5, y: 3.5 })), { x: -.5, y: -2.5, door: true, rotation: 90 }];
    const image = 'data:image/svg+xml,' + encodeURIComponent('<svg xmlns="http://www.w3.org/2000/svg" width="32" height="32"><rect x="3" y="3" width="26" height="26" fill="#d8a75e"/></svg>');
    structures.filter(item => !item.door).forEach(item => { item.images = [{ url: image }]; });
    structures[0].images[0].color = '#804020';
    const data = { token: 'test', name: 'Meteor', description: 'Предпросмотр корабля', x: -6, y: -5, width: 12, height: 10, tiles, structures,
      items: [{ id: 'e:1', kind: 'entity', prototype: 'Machine', x: -.5, y: -.5, rotation: 90 }],
      visuals: [{ kind: 'entity', id: 'Machine', images: [{ url: image }] }] };
    for (const width of [1000, 560]) {
      await page.setViewportSize({ width, height: 720 });
      await publish('ShipyardPreview', data);
      await page.locator('.ShipyardPreview__plan canvas').waitFor();
      await page.waitForFunction(() => !document.querySelector('.ShipyardPreview__progress'));
      assert.equal(await page.locator('.NavMapPlan > path').first().getAttribute('fill'), '#435257');
      assert.equal(await page.locator('.PlanWallSprites img').count(), 10);
      assert.ok((await page.locator('.PlanWallSprites img').first().getAttribute('style')).includes('filter: url('));
      assert.ok((await page.locator('.NavMapPlan feColorMatrix').getAttribute('values')).startsWith(`${128 / 255} 0 0 0 0`));
      assert.ok(await page.locator('.PlanWallSprites img').evaluateAll(images => images.every(image => image.complete && image.naturalWidth > 0)));
      assert.equal(await page.getByRole('button', { name: 'Применить', exact: true }).count(), 0);
      assert.equal(await page.locator('.ShipyardPreview').evaluate(node => node.scrollWidth > node.clientWidth), false);
      const scale = await page.locator('.ShipyardPreview__toolbar span').textContent();
      await page.locator('.ShipyardPreview__viewport').hover(); await page.mouse.wheel(0, -200);
      await page.waitForFunction(previous => document.querySelector('.ShipyardPreview__toolbar span').textContent !== previous, scale);
      await page.getByRole('button', { name: 'Уместить', exact: true }).click();
      const viewport = page.locator('.ShipyardPreview__viewport');
      const previousScroll = await viewport.evaluate(node => node.scrollLeft);
      const bounds = await viewport.boundingBox();
      await page.mouse.move(bounds.x + bounds.width / 2, bounds.y + bounds.height / 2);
      await page.mouse.down({ button: 'right' }); await page.mouse.move(bounds.x + bounds.width / 2 + 50, bounds.y + bounds.height / 2 + 20);
      await page.mouse.up({ button: 'right' });
      assert.notEqual(await viewport.evaluate(node => node.scrollLeft), previousScroll, 'Right drag pans the plan.');
      await page.getByRole('button', { name: 'Уместить', exact: true }).click();
      if (process.env.SHIPYARD_SCREENSHOTS) await page.screenshot({ path: path.join(process.env.SHIPYARD_SCREENSHOTS, `preview-${width}.png`) });
    }
    if (process.env.SHIPYARD_PREVIEW_DATA) {
      await page.setViewportSize({ width: 1000, height: 720 });
      await publish('ShipyardPreview', JSON.parse(fs.readFileSync(process.env.SHIPYARD_PREVIEW_DATA, 'utf8')));
      await page.waitForFunction(() => document.querySelector('.ShipyardPreview__plan canvas') && !document.querySelector('.ShipyardPreview__progress'));
      await page.waitForFunction(() => [...document.querySelectorAll('.PlanWallSprites img')].every(image => image.complete && image.naturalWidth > 0));
      if (process.env.SHIPYARD_SCREENSHOTS) await page.screenshot({ path: path.join(process.env.SHIPYARD_SCREENSHOTS, 'meteor.png') });
    }
    const scene = { x: -6, y: -5, tiles, objects: structures.map((object, i) => ({ ...object, id: `s:${i}`, wall: !object.door })), occupied: [], blocked: [] };
    await publish('ApartmentEditor', { width: 12, height: 10, token: 'apartment', template: 'test', name: 'Квартира', maxFurniture: 20,
      arrivalX: -4, arrivalY: -3, scene, layout: [], catalog: [{ id: 'table', name: 'Стол', width: 1, height: 1, maxCount: 4, images: [{ url: image }] }] });
    await page.locator('.ApartmentEditor .NavMapPlan').waitFor();
    assert.equal(await page.locator('.NavMapPlan > path').first().getAttribute('fill'), '#435257');
    assert.equal(await page.locator('.PlanWallSprites img').count(), 10);
    await page.getByRole('button', { name: /Стол/ }).click();
    await page.locator('[data-cell="-2,-1"]').click();
    assert.equal(await page.getByRole('button', { name: 'Применить', exact: true }).isEnabled(), true);
    if (process.env.SHIPYARD_SCREENSHOTS) await page.screenshot({ path: path.join(process.env.SHIPYARD_SCREENSHOTS, 'apartment.png') });
    await publish('ShipyardPreview', { name: 'Broken', error: 'Не удалось загрузить предпросмотр корабля.' });
    await page.getByRole('alert').waitFor();
    await page.getByRole('button', { name: 'Закрыть', exact: true }).click();
    assert.ok(await page.evaluate(() => window.__sent.some(event => event.type === 'act/close')));
    assert.deepEqual(errors, []);
    console.log('PASS: read-only preview, prototype wall images in both windows, zoom, small window, apartment placement and error/close.');
  } finally { await browser?.close(); await new Promise(resolve => server.close(resolve)); }
})().catch(error => { console.error(error); process.exitCode = 1; });
