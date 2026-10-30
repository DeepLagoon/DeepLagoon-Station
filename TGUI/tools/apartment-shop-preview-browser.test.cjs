// Run ApartmentShopPreviewTests with APARTMENT_SHOP_BROWSER_FIXTURE first.
// This test uses that real map, catalogue and PNG sprite data against the production TGUI bundle.
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const assert = require('node:assert/strict');
const { chromium } = require(process.env.TATTOO_PLAYWRIGHT || 'playwright');
const assets = path.resolve(__dirname, '../../Resources/Web/DeepLagoon');
const fixturePath = process.env.APARTMENT_SHOP_BROWSER_FIXTURE || path.resolve(__dirname, '../../.local/codex/apartment-preview-walls/preview-fixture.json');
const output = process.env.APARTMENT_SHOP_BROWSER_OUTPUT || path.resolve(__dirname, '../../.local/codex/apartment-preview-walls/browser');
const fixture = JSON.parse(fs.readFileSync(fixturePath, 'utf8'));
const actions = [], errors = [];
const server = http.createServer((req, res) => {
  if (req.url === '/') {
    res.setHeader('Content-Type', 'text/html');
    res.end(`<!doctype html><meta charset="utf-8"><link rel="stylesheet" href="/tgui.bundle.css"><div id="react-root"></div><script>
      window.__windowId__='preview';window.__updateQueue__=[];window.update=m=>window.__updateQueue__.push(m);
      window.Byond={parseJson:v=>typeof v==='string'?JSON.parse(v):v,topic:v=>window.testTopic(v),winset:()=>{},
      winget:(_id,key)=>Promise.resolve(key==='size'?'1000,780':'0,0'),command:()=>{},loadCss:()=>{},loadJs:()=>{}};
    </script><script src="/tgui.bundle.js"></script>`);
    return;
  }
  const file = path.resolve(assets, '.' + req.url);
  if (!file.startsWith(assets + path.sep) || !fs.existsSync(file)) { res.writeHead(404).end(); return; }
  res.setHeader('Content-Type', file.endsWith('.js') ? 'text/javascript' : 'text/css');
  fs.createReadStream(file).pipe(res);
});
(async () => {
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const browser = await chromium.launch({ headless: true, channel: process.platform === 'win32' ? 'msedge' : undefined });
  try {
    const page = await browser.newPage({ viewport: { width: 1000, height: 780 } });
    page.on('pageerror', error => errors.push(error.message));
    await page.exposeFunction('testTopic', value => { if (value.type?.startsWith('act/') && value.type !== 'act/focus') actions.push(value); });
    await page.goto(`http://127.0.0.1:${server.address().port}`);
    await page.evaluate(data => window.update(JSON.stringify({ type: 'update', payload: {
      replaceData: true, config: { interface: 'ApartmentShop', title: 'Магазин квартир', status: 2, window: { key: 'preview', fancy: false } }, data,
    } })), fixture);
    await page.getByRole('button', { name: 'Предпросмотр', exact: true }).click();
    const plan = page.getByRole('img', { name: 'Планировка квартиры с мебелью', exact: true });
    await plan.waitFor();
    await page.waitForFunction(() => [...document.querySelectorAll('.LagoonStore__plan img')].every(img => img.complete && img.naturalWidth > 0));
    fs.mkdirSync(output, { recursive: true });
    await plan.screenshot({ path: path.join(output, 'apartment-plan.png') });
    const visibleWalls = await page.evaluate(scene => {
      const nodes = [...document.querySelectorAll('[data-prototype="WallPlastitaniumIndestructible"]')];
      const isolated = nodes.filter(node => {
        const rect = node.getBoundingClientRect();
        const x = rect.x + rect.width / 2, y = rect.y + rect.height / 2;
        return document.elementFromPoint(x, y)?.closest('[data-prototype]')?.dataset.prototype === 'WallPlastitaniumIndestructible';
      });
      return { total: nodes.length, visible: isolated.length, negativeEngineDepth: scene.objects.some(item => item.prototype === 'WallPlastitaniumIndestructible' && item.depth < 0) };
    }, fixture.preview);
    assert.equal(visibleWalls.negativeEngineDepth, true, 'The real map must exercise negative engine draw depths');
    assert.ok(visibleWalls.total > 0);
    assert.ok(visibleWalls.visible > 0, `Walls must be visible above the floor: ${JSON.stringify(visibleWalls)}`);
    await page.getByRole('button', { name: 'Назад к квартирам' }).click();
    await page.getByRole('tab', { name: 'Предметы', exact: true }).click();
    const images = page.getByRole('img', { name: /^Внешний вид:/ });
    assert.equal(await images.count(), fixture.items.length);
    await page.waitForFunction(() => [...document.querySelectorAll('.ApartmentShop__itemPreview img')].every(img => img.complete && img.naturalWidth > 0));
    assert.equal(await page.getByText('Изображение недоступно', { exact: true }).count(), 0);
    await page.screenshot({ path: path.join(output, 'items.png') });
    const product = fixture.items[0];
    await page.getByRole('button', { name: `Предпросмотр предмета: ${product.name}`, exact: true }).click();
    await page.getByRole('dialog', { name: product.name, exact: true }).waitFor();
    assert.equal(actions.filter(a => a.type === 'act/buyItem').length, 0, 'Opening a product preview never purchases it');
    await page.getByRole('dialog').screenshot({ path: path.join(output, 'item-preview.png') });
    await page.getByRole('button', { name: 'Назад к предметам' }).click();
    await page.getByRole('button', { name: `Предпросмотр предмета: ${product.name}`, exact: true }).click();
    await page.getByRole('dialog', { name: product.name, exact: true }).getByRole('button', { name: 'Купить предмет', exact: true }).click();
    await page.getByRole('dialog', { name: 'Подтверждение выбора' }).waitFor();
    assert.equal(actions.filter(a => a.type === 'act/buyItem').length, 0, 'Purchase still requires explicit confirmation');
    assert.equal(await page.getByRole('dialog').getByRole('img', { name: `Внешний вид: ${product.name}`, exact: true }).count(), 1);
    await page.getByRole('button', { name: 'Отмена', exact: true }).click();
    await page.setViewportSize({ width: 520, height: 420 });
    await page.getByRole('button', { name: `Предпросмотр предмета: ${product.name}`, exact: true }).click();
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
    await page.getByRole('button', { name: 'Назад к предметам' }).click();
    assert.deepEqual(errors, []);
    console.log(`PASS: ${visibleWalls.visible}/${visibleWalls.total} real-map walls visible above floors, ${fixture.items.length} real product sprites, enlarged preview, confirmation and narrow layout`);
  } finally { await browser.close(); server.close(); }
})().catch(error => { console.error(error); server.close(); process.exitCode = 1; });
