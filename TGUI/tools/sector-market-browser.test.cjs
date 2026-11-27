// Test the shipped interface bundle, its real backend bridge and packaged game sprite images.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const http = require('node:http');
const path = require('node:path');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const root = path.resolve(__dirname, '../..'), resources = path.join(root, 'Resources/Web/DeepLagoon');
const output = path.join(root, '.local/codex/economy-market-client-20261126');
fs.mkdirSync(output, { recursive: true });
const labels = Object.fromEntries(['economy.ftl', 'sector-market.ftl'].flatMap(file => [...fs.readFileSync(path.join(root, 'Resources/Locale/ru-RU/_DeepLagoon', file), 'utf8')
  .matchAll(/^dl-trade-([\w-]+)\s*=\s*(.+)$/gm)].map(m => [m[1], m[2].trim()])));
const image = 'data:image/png;base64,' + fs.readFileSync(path.join(root, 'Resources/Textures/Objects/Materials/Sheets/metal.rsi/steel.png')).toString('base64');
const icons = { SheetSteel: [{ url: image, color: '#ffffff' }] };
const sale = { id: 'sale', name: 'Сталь', prototype: 'SheetSteel', mine: false, buy: false, price: 1000, money: '1 000 спессо', units: 50, condition: 100, charge: 0, remaining: 1, character: 'Тестовый продавец', description: 'Проверка текста', preview: 'Проверка текста' };
const buy = { ...sale, id: 'buy', buy: true, condition: 80, units: 20 };
const item = { id: 'item', name: 'Сталь', prototype: 'SheetSteel', units: 50, condition: 100, charge: 0 };
const draft = { buy: false, price: '1000', description: '', quantity: '1', units: '1', condition: 100, charge: 0, prototypes: [], valid: true, error: '', total: '1 000 спессо' };
(async () => {
  const server = http.createServer((req, res) => {
    const file = path.resolve(resources, '.' + new URL(req.url, 'http://localhost').pathname);
    if (!file.startsWith(resources + path.sep) || !fs.existsSync(file) || !fs.statSync(file).isFile()) return res.writeHead(404).end();
    res.setHeader('Content-Type', ({ '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.woff2': 'font/woff2', '.svg': 'image/svg+xml' })[path.extname(file)] || 'application/octet-stream');
    res.end(fs.readFileSync(file));
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  let browser;
  try {
    browser = await chromium.launch({ channel: process.env.BROWSER_CHANNEL || 'msedge', headless: true });
    const page = await browser.newPage({ viewport: { width: 1100, height: 760 } });
    const errors = []; page.on('pageerror', e => errors.push(String(e))); page.setDefaultTimeout(10000);
    await page.addInitScript(() => {
      window.__sent = []; const Original = window.Image;
      window.Image = class extends Original { set src(value) {
        if (!value.startsWith('res://deeplagoon/Web/DeepLagoon/bridge?')) { super.src = value; return; }
        const [type, payload] = value.split('?')[1].split('&');
        window.__sent.push({ type: decodeURIComponent(type), payload: JSON.parse(decodeURIComponent(payload)) });
        queueMicrotask(() => this.onload?.(new Event('load')));
      }};
    });
    await page.goto(`http://127.0.0.1:${server.address().port}/interface.html`);
    await page.waitForFunction(() => window.__tguiAppBooted__);
    let state = { section: 'catalog', cards: true, busy: false, ready: true, statusKey: 'ready', status: '', bank: '5 000 спессо', offers: [sale, buy], items: [item], deliveries: [{ id: 'delivery', name: 'Сталь', prototype: 'SheetSteel' }], selected: 'sale', item: 'item', confirm: '', total: 2, page: 0, pages: 1, search: '', filter: 0, category: 0, sort: 0, draft };
    const publish = async changes => { state = { ...state, ...changes }; await page.evaluate(({ state, labels, icons }) => window.update(JSON.stringify({ type: 'update', payload: {
      replaceData: true, replaceStaticData: true, resetPartialData: true,
      config: { interface: 'SectorMarket', title: labels.title, status: 2, window: { key: 'deeplagoon', fancy: false } },
      static_data: { labels, icons }, data: state,
    } })), { state, labels, icons }); };
    const sent = async type => page.evaluate(type => window.__sent.filter(m => m.type === 'act/' + type).at(-1)?.payload, type);
    await publish({});
    await page.locator('.SectorMarket__offer').first().waitFor();
    assert.equal(await page.getByText('DEEP LAGOON', { exact: true }).count(), 0);
    assert.equal(await page.getByText(labels['full-item'], { exact: true }).count(), 0);
    assert.equal(await page.getByText(labels['type-preview'], { exact: true }).count(), 0);
    assert.equal(await page.locator('.SectorMarket__offer').count(), 2);
    await page.waitForFunction(() => [...document.querySelectorAll('.SectorMarket__image img')].every(i => i.complete && i.naturalWidth > 0));
    assert.equal(await page.locator('.SectorMarket__description b, .SectorMarket__offer-excerpt b').count(), 0, 'Seller descriptions must remain plain text.');
    assert.equal(await page.locator('.SectorMarket__details').getByText(labels.battery, { exact: true }).count(), 0);
    const emptyCell = { ...sale, id: 'empty-cell', prototype: 'PowerCellSmall', name: 'Разряженный аккумулятор', hasBattery: true, charge: 0 };
    await publish({ offers: [emptyCell], selected: emptyCell.id });
    assert.equal(await page.locator('.SectorMarket__details').getByText(labels.battery, { exact: true }).count(), 1);
    assert.equal(await page.locator('.SectorMarket__details').getByText('0%', { exact: true }).count(), 1);
    await publish({ offers: [sale, buy], selected: sale.id });
    await page.screenshot({ path: path.join(output, 'tgui-market-cards.png') });
    await page.getByRole('button', { name: labels.list, exact: true }).click();
    assert.equal((await sent('layout')).value, 'list'); await publish({ cards: false });
    assert.equal(await page.locator('.SectorMarket__offers--list').count(), 1);
    await page.screenshot({ path: path.join(output, 'tgui-market-list.png') });
    assert.equal(await sent('confirm'), undefined);
    await page.locator('.SectorMarket__details').getByRole('button', { name: labels.buy }).click();
    assert.equal((await sent('prepare')).id, 'sale'); assert.equal(await sent('confirm'), undefined);
    await publish({ confirm: 'sale' });
    await page.getByRole('dialog').getByRole('button', { name: labels.confirm, exact: true }).click();
    assert.ok(await sent('confirm'));
    await publish({ confirm: 'buy', selected: 'buy', items: [] });
    assert.equal(await page.getByRole('dialog').getByRole('button', { name: labels.confirm, exact: true }).isDisabled(), true);
    await publish({ confirm: '', items: [item], section: 'create', draft: { ...draft, description: 'Черновик', price: '1700' } });
    assert.equal(await page.getByRole('textbox', { name: labels.description }).inputValue(), 'Черновик');
    await publish({ draft: { ...draft, buy: true, hasBattery: false } });
    assert.equal(await page.getByRole('combobox', { name: labels.charge, exact: true }).count(), 0);
    await publish({ draft: { ...draft, buy: true, hasBattery: true } });
    assert.equal(await page.getByRole('combobox', { name: labels.charge, exact: true }).count(), 1);
    await publish({ draft: { ...draft, description: 'Черновик', price: '1700' } });
    await page.getByRole('button', { name: labels.sell, exact: true }).click(); assert.ok(await sent('publish'));
    await publish({ section: 'delivery' });
    await page.getByRole('button', { name: 'Возврат товара', exact: true }).click(); assert.equal((await sent('claim')).id, 'delivery');
    await publish({ section: 'catalog', cards: true });
    await page.getByRole('combobox', { name: labels['category-all'], exact: true }).click();
    await page.getByRole('option', { name: labels['category-materials'], exact: true }).click();
    assert.equal((await sent('category')).value, '1');
    const local = { available: true, enabled: true, canPay: true, confirm: false, bank: '10 000 спессо', page: 0, pages: 1, entities: 1, fee: '600 спессо', total: '1000 спессо',
      stock: [{ prototype: 'SheetSteel', name: 'Сталь', quantity: 45, unitPrice: '80 спессо', money: '3600 спессо' }], cart: [{ prototype: 'SheetSteel', name: 'Сталь', quantity: 5, money: '400 спессо' }] };
    await publish({ section: 'local', local });
    assert.equal(await page.getByRole('tab', { name: labels.local, exact: true }).count(), 1);
    await page.locator('.SectorMarket__local-product').getByRole('button', { name: '+5', exact: true }).click();
    assert.equal((await sent('localAdd')).prototype, 'SheetSteel'); assert.equal((await sent('localAdd')).amount, 5);
    await page.getByRole('button', { name: labels['local-remove'], exact: true }).click();
    assert.equal((await sent('localRemove')).prototype, 'SheetSteel');
    await page.getByRole('button', { name: labels['local-checkout'], exact: true }).click();
    assert.ok(await sent('localCheckout')); assert.equal(await sent('localConfirm'), undefined);
    await publish({ local: { ...local, confirm: true } });
    await page.getByRole('dialog').getByRole('button', { name: labels.confirm, exact: true }).click();
    assert.ok(await sent('localConfirm'));
    await publish({ local, cards: true });
    for (const cards of [true, false]) {
      await publish({ cards });
      for (const width of [1100, 900]) {
        await page.setViewportSize({ width, height: 760 });
        assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), `Local listings overflow at ${width}, cards=${cards}`);
        assert.ok(await page.locator('.SectorMarket__local-product img').count());
        const cartInside = await page.locator('.SectorMarket__local-cart').evaluate(el => el.getBoundingClientRect().right <= innerWidth);
        assert.ok(cartInside, `Local cart leaves the window at ${width}, cards=${cards}`);
      }
    }
    await page.screenshot({ path: path.join(output, 'tgui-local-market.png') });
    const groups = [{ key: 'sale:SheetSteel', name: 'Сталь', prototype: 'SheetSteel', buy: false, material: true, money: '100 спессо' },
      { key: 'buy:SheetSteel', name: 'Сталь', prototype: 'SheetSteel', buy: true, material: true, money: '90 спессо' }];
    const many = Array.from({ length: 20 }, (_, i) => ({ ...sale, id: 'lot-' + i, character: 'Продавец ' + i, splittable: true, unitMoney: '100 спессо' }));
    await publish({ section: 'catalog', groups, group: groups[0].key, offers: many, selected: many[1].id, lotAmount: '20', autoAmount: '50', sellerPage: 0, sellerPages: 3, local: { available: false } });
    assert.equal(await page.locator('.SectorMarket__group-grid .SectorMarket__offer').count(), 2);
    for (const cards of [true, false]) {
      await publish({ cards, groupOpen: false });
      for (const width of [1100, 900]) {
        await page.setViewportSize({ width, height: 760 });
        assert.equal(await page.locator('.SectorMarket__catalogue--expanded').count(), 0);
        const bounds = await page.locator('.SectorMarket__group-grid').boundingBox();
        assert.ok(bounds.height > 350 && bounds.width > width - 60, 'The closed catalogue must use the full available area.');
      }
    }
    await page.screenshot({ path: path.join(output, 'tgui-full-catalogue.png') });
    await page.locator('.SectorMarket__group-grid .SectorMarket__offer').last().click();
    assert.equal((await sent('group')).key, groups[1].key);
    await publish({ groupOpen: true });
    await page.getByRole('button', { name: labels['close-offers'], exact: true }).click();
    assert.ok(await sent('closeGroup')); assert.equal(await sent('basketConfirm'), undefined);
    await publish({ groupOpen: false });
    assert.equal(await page.locator('.SectorMarket__catalogue--expanded').count(), 0);
    await page.locator('.SectorMarket__group-grid .SectorMarket__offer').first().click();
    await publish({ groupOpen: true });
    await page.locator('.SectorMarket__catalogue-left > .SectorMarket__offers').last().locator('button').nth(5).click();
    assert.equal((await sent('select')).id, many[5].id);
    await page.locator('.SectorMarket__details').getByRole('button', { name: labels['basket-add'], exact: true }).click();
    assert.ok(await sent('basketAdd')); assert.equal(await sent('basketConfirm'), undefined);
    await page.locator('.SectorMarket__details').getByRole('button', { name: labels['basket-auto'], exact: true }).click();
    assert.ok(await sent('basketAuto')); assert.equal(await sent('basketConfirm'), undefined);
    await publish({ section: 'basket', basket: [{ ...sale, character: 'Первый продавец', units: 30 }, { ...sale, id: 'second', character: 'Второй продавец', units: 20, money: '2800 спессо' }] });
    await page.getByRole('button', { name: labels['basket-quote'], exact: true }).click(); assert.ok(await sent('basketQuote'));
    await publish({ quote: { open: true, canPay: true, money: '5800 спессо', lines: [{ ...sale, units: 30, money: '3000 спессо' }, { ...sale, id: 'second', units: 20, money: '2800 спессо' }] } });
    assert.equal(await page.getByRole('dialog').getByText('5800 спессо', { exact: true }).count(), 1);
    await page.getByRole('dialog').getByRole('button', { name: labels.confirm, exact: true }).click(); assert.ok(await sent('basketConfirm'));
    await publish({ quote: { open: false }, section: 'catalog', groupOpen: true });
    await page.screenshot({ path: path.join(output, 'tgui-grouped-market.png') });
    await publish({ section: 'catalog', local: { available: false }, cards: true });
    const layout = [];
    for (const width of [1100, 900]) {
      await page.setViewportSize({ width, height: 700 });
      const size = await page.locator('.SectorMarket').evaluate(el => ({ width: el.clientWidth, scroll: el.scrollWidth }));
      assert.ok(size.scroll <= size.width + 1); layout.push({ width, ...size });
    }
    assert.deepEqual(errors, []);
    fs.writeFileSync(path.join(output, 'browser-validation.json'), JSON.stringify({ errors, layout, realSprite: true,
      localListings: { cardsAndList: true, widths: [1100, 900], cartAndConfirmation: true }, groupedOffers: true, combinedQuote: true,
      passed: ['cards-and-list-switch', 'real-resource-previews', 'description-escaping', 'battery-presence-and-empty-cell', 'battery-criteria-visibility', 'purchase-confirmation', 'supply-mismatch', 'draft', 'publish', 'return-item', 'category-filter', 'local-listings'] }, null, 2));
    console.log('Passed: SectorMarket shipped-bundle interactions, sprites and layouts.');
  } finally { await browser?.close(); await new Promise(resolve => server.close(resolve)); }
})().catch(e => { console.error(e); process.exitCode = 1; });
