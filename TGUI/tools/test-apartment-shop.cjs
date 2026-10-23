// Production-bundle browser regression. Build TGUI first; requires Playwright and Edge on Windows.
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const assert = require('node:assert/strict');
const playwright = require(process.env.TATTOO_PLAYWRIGHT || 'playwright');
const assets = path.resolve(__dirname, '../../Resources/Web/DeepLagoon');
const output = path.resolve(__dirname, '../../.local/codex/apartment-templates/shop-qa');
const actions = [], errors = [];
const server = http.createServer((req, res) => {
  if (req.url === '/') {
    res.setHeader('Content-Type', 'text/html');
    res.end(`<!doctype html><meta charset="utf-8"><link rel="stylesheet" href="/tgui.bundle.css"><div id="react-root"></div><script>
      window.__windowId__='shop';window.__updateQueue__=[];window.update=m=>window.__updateQueue__.push(m);
      window.Byond={parseJson:v=>typeof v==='string'?JSON.parse(v):v,topic:v=>window.testTopic(v),winset:()=>{},
      winget:(_id,key)=>Promise.resolve(key==='size'?'920,720':'0,0'),command:()=>{},loadCss:()=>{},loadJs:()=>{}};
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
  const browser = await playwright.chromium.launch({ headless: true, channel: process.platform === 'win32' ? 'msedge' : undefined });
  try {
    const page = await browser.newPage({ viewport: { width: 920, height: 760 } });
    page.on('pageerror', error => errors.push(error.message));
    await page.exposeFunction('testTopic', value => { if (value.type?.startsWith('act/')) actions.push(value); });
    await page.goto(`http://127.0.0.1:${server.address().port}`);
    const state = { number: 12345678, lobby: true, legacy: false, templates: [
      { id: 'Basic', name: 'Базовая квартира', price: 0, owned: true, selected: true },
      { id: 'Paid', name: 'Большая квартира', price: 40, owned: false, selected: false },
    ] };
    const publish = () => page.evaluate(data => window.update(JSON.stringify({ type: 'update', payload: {
      replaceData: true, config: { interface: 'ApartmentShop', title: 'Шаблоны квартир', status: 2, window: { key: 'shop', fancy: false } }, data,
    } })), state);
    await publish();
    await page.getByRole('button', { name: 'Выбрано', exact: true }).waitFor();
    assert.equal(await page.getByRole('button', { name: 'Выбрано', exact: true }).isDisabled(), true);
    await page.getByRole('button', { name: 'Купить и выбрать', exact: true }).click();
    await page.getByRole('dialog').waitFor(); assert.equal(actions.length, 0);
    await page.getByRole('button', { name: 'Отмена', exact: true }).click(); assert.equal(actions.length, 0);
    await page.getByRole('button', { name: 'Купить и выбрать', exact: true }).click();
    await page.getByRole('button', { name: 'Подтвердить', exact: true }).click();
    await page.waitForTimeout(100);
    assert.equal(actions.length, 1); assert.equal(actions[0].type, 'act/select');
    const payload = typeof actions[0].payload === 'string' ? JSON.parse(actions[0].payload) : actions[0].payload;
    assert.equal(payload.template, 'Paid');
    assert.equal(await page.getByRole('button', { name: 'Подтвердить', exact: true }).isDisabled(), true);
    state.number = 87654321; state.lobby = false; state.legacy = true;
    state.templates[0].selected = false; state.templates[1].selected = true; state.templates[1].owned = true;
    state.message = 'Шаблон выбран. Спуститесь на остановку и введите новый номер: 87654321.';
    await publish();
    await page.getByRole('status').waitFor(); assert.match(await page.locator('main').innerText(), /87654321/);
    await page.getByRole('button', { name: 'Обустройство квартиры', exact: true }).click();
    await page.getByRole('button', { name: 'Покинуть старую квартиру', exact: true }).click();
    await page.waitForTimeout(100);
    assert.deepEqual(actions.map(a => a.type), ['act/select', 'act/edit', 'act/legacyExit']);
    assert.deepEqual(errors, []);
    fs.mkdirSync(output, { recursive: true });
    await page.screenshot({ path: path.join(output, 'shop.png'), fullPage: true });
    console.log('PASS: free default, purchase confirmation/cancel, one pending request, new number, editor and legacy exit');
  } finally { await browser.close(); server.close(); }
})().catch(error => { console.error(error); server.close(); process.exitCode = 1; });
