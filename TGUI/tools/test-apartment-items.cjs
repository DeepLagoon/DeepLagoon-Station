// Regression against the shipped TGUI bundle; requires Playwright and Edge on Windows.
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const assert = require('node:assert/strict');
const playwright = require(process.env.TATTOO_PLAYWRIGHT || 'playwright');
const assets = path.resolve(__dirname, '../../Resources/Web/DeepLagoon');
const output = path.resolve(__dirname, '../../.local/codex/apartment-items/ui');
const actions = [], errors = [];
const server = http.createServer((req, res) => {
  if (req.url === '/') {
    res.setHeader('Content-Type', 'text/html');
    res.end(`<!doctype html><meta charset="utf-8"><link rel="stylesheet" href="/tgui.bundle.css"><div id="react-root"></div><script>
      window.__windowId__='items';window.__updateQueue__=[];window.update=m=>window.__updateQueue__.push(m);
      window.Byond={parseJson:v=>typeof v==='string'?JSON.parse(v):v,topic:v=>window.testTopic(v),winset:()=>{},
      winget:(_id,key)=>Promise.resolve(key==='size'?'920,760':'0,0'),command:()=>{},loadCss:()=>{},loadJs:()=>{}};
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
    await page.exposeFunction('testTopic', value => {
      if (value.type?.startsWith('act/') && value.type !== 'act/focus') actions.push(value);
    });
    await page.goto(`http://127.0.0.1:${server.address().port}`);
    await page.evaluate(() => Object.defineProperty(window.crypto, 'randomUUID', { value: undefined }));
    let revision = 0;
    const state = { balance: 200, number: 12345678, lobby: true, legacy: false, pendingItemOrder: '', items: [
      { id: 'DLApartmentFrame', name: 'Квартирная рамка', price: 35, category: 'Картины' },
      { id: 'DLApartmentCanvas', name: 'Долговечный холст', price: 100, category: 'Картины' },
      { id: 'DLApartmentFigurineClown', name: 'Фигурка клоуна', price: 15, category: 'Фигурки' },
    ], templates: [{ id: 'Basic', name: 'Базовая квартира', category: 'Классика', price: 0, owned: true, selected: true }] };
    const publish = () => page.evaluate(data => window.update(JSON.stringify({ type: 'update', payload: {
      replaceData: true, config: { interface: 'ApartmentShop', title: 'Магазин квартир', status: 2, window: { key: 'items', fancy: false } }, data,
    } })), { ...state, token: String(++revision) });
    const payload = action => typeof action.payload === 'string' ? JSON.parse(action.payload) : action.payload;
    await publish();
    await page.getByRole('tab', { name: 'Предметы', exact: true }).click();
    assert.doesNotMatch(await page.locator('main').innerText(), /Обычный холст/);
    await page.getByRole('textbox', { name: 'Поиск предметов' }).fill('холст');
    assert.equal(await page.locator('article').count(), 1);
    await page.getByRole('button', { name: 'Купить предмет', exact: true }).click();
    await page.getByRole('button', { name: 'Отмена', exact: true }).click();
    assert.equal(actions.length, 0);
    await page.getByRole('button', { name: 'Купить предмет', exact: true }).click();
    await page.getByRole('button', { name: 'Подтвердить', exact: true }).evaluate(button => { button.click(); button.click(); });
    await page.waitForTimeout(100);
    assert.equal(actions.length, 1);
    assert.equal(actions[0].type, 'act/buyItem');
    const order = payload(actions[0]);
    assert.equal(order.template, 'DLApartmentCanvas');
    assert.equal(Number(order.unitPrice), 100);
    assert.match(order.order, /^[0-9a-f-]{36}$/);
    assert.equal(await page.getByRole('button', { name: 'Обработка…', exact: true }).isDisabled(), true);
    state.pendingItemOrder = order.order; state.message = 'Оплата учтена, ожидается доставка.';
    await publish();
    await page.getByRole('tab', { name: /^Мои квартиры/ }).click();
    await page.getByRole('button', { name: 'Завершить доставку', exact: true }).click();
    await page.waitForTimeout(100);
    assert.equal(actions.length, 2);
    assert.equal(actions[1].type, 'act/retryItem');
    assert.equal(payload(actions[1]).order, order.order);
    state.pendingItemOrder = ''; state.balance = 65; state.message = 'Предмет доставлен ко входу в вашу квартиру.';
    await publish();
    await page.getByRole('tab', { name: 'Предметы', exact: true }).click();
    assert.equal(await page.getByRole('button', { name: 'Купить предмет', exact: true }).isDisabled(), true);
    await page.getByRole('textbox', { name: 'Поиск предметов' }).fill('');
    assert.equal(await page.locator('article').count(), 3);
    await page.getByRole('tab', { name: 'Галерея', exact: true }).click();
    await page.waitForTimeout(700);
    assert.equal(actions.at(-1).type, 'act/gallery');
    assert.equal(payload(actions.at(-1)).page, 0);
    assert.match(await page.locator('main').innerText(), /Обычный холст.*нельзя/);
    assert.match(await page.locator('main').innerText(), /без публикации в библиотеке/);
    state.gallery = { loaded: true, page: 0, pageSize: 12, total: 0, paintings: [], error: '' };
    await publish();
    await page.getByText('В квартире пока нет нарисованных картин.', { exact: true }).waitFor();
    const png = 'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jZdwAAAAASUVORK5CYII=';
    state.gallery = { loaded: true, page: 0, pageSize: 12, total: 13, error: '', paintings: [
      { id: 'one', title: 'Личная картина', image: png, framed: true },
      { id: 'two', title: 'Черновик', image: png, framed: false },
    ] };
    await publish();
    await page.getByRole('button', { name: 'Предпросмотр: Личная картина', exact: true }).click();
    await page.getByRole('dialog', { name: 'Личная картина', exact: true }).waitFor();
    assert.equal(await page.getByRole('dialog').getByRole('img', { name: 'Личная картина', exact: true }).count(), 1);
    await page.getByRole('button', { name: 'Назад в галерею', exact: true }).click();
    await page.getByRole('button', { name: 'Следующие картины', exact: true }).click();
    await page.waitForTimeout(700);
    assert.equal(actions.at(-1).type, 'act/gallery');
    assert.equal(payload(actions.at(-1)).page, 1);
    state.gallery = { loaded: true, page: 1, pageSize: 12, total: 13, error: '', paintings: [{ id: 'last', title: 'Последняя картина', image: png, framed: true }] };
    await publish();
    assert.equal(await page.getByRole('button', { name: 'Следующие картины', exact: true }).isDisabled(), true);
    assert.deepEqual(errors, []);
    fs.mkdirSync(output, { recursive: true });
    await page.screenshot({ path: path.join(output, 'gallery.png'), fullPage: true });
    await page.setViewportSize({ width: 620, height: 700 });
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
    await page.screenshot({ path: path.join(output, 'gallery-narrow.png'), fullPage: true });
    console.log('PASS: item section, instructions, search, confirmation/cancel, one purchase per double click, stock CEF UUID fallback, durable delivery retry, balance guard, narrow layout, private gallery, moved guide, empty gallery, painting preview and pagination');
  } finally { await browser.close(); server.close(); }
})().catch(error => { console.error(error); server.close(); process.exitCode = 1; });
