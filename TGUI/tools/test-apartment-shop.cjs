// Browser regression against the production bundle. Requires Playwright and Edge.
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const assert = require('node:assert/strict');
const playwright = require(process.env.TATTOO_PLAYWRIGHT || 'playwright');
const assets = process.env.LAGOON_STORE_ASSETS || path.resolve(__dirname, '../../Resources/Web/DeepLagoon');
const output = path.resolve(__dirname, '../../.local/codex/lagoon-stores/browser');
const actions = [], errors = [];
const server = http.createServer((req, res) => {
  if (req.url === '/') {
    res.setHeader('Content-Type', 'text/html');
    res.end(`<!doctype html><meta charset="utf-8"><link rel="stylesheet" href="/tgui.bundle.css"><div id="react-root"></div><script>
      window.__windowId__='shop';window.__updateQueue__=[];window.update=m=>window.__updateQueue__.push(m);
      window.Byond={parseJson:v=>typeof v==='string'?JSON.parse(v):v,topic:v=>window.testTopic(v),winset:()=>{},
      winget:(_id,key)=>Promise.resolve(key==='size'?'1000,720':'0,0'),command:()=>{},loadCss:()=>{},loadJs:()=>{}};
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
    const page = await browser.newPage({ viewport: { width: 1000, height: 720 } });
    page.on('pageerror', error => errors.push(error.message));
    await page.exposeFunction('testTopic', value => { if (value.type?.startsWith('act/') && value.type !== 'act/focus') actions.push(value); });
    await page.addInitScript(() => Object.defineProperty(window.crypto, 'randomUUID', { value: undefined }));
    await page.goto(`http://127.0.0.1:${server.address().port}`);
    const publish = (data, name = 'ApartmentShop') => page.evaluate(({ data, name }) => window.update(JSON.stringify({ type: 'update', payload: {
      replaceData: true, config: { interface: name, title: name, status: 2, window: { key: 'shop', fancy: false } }, data,
    } })), { data, name });
    const state = { token: 'initial', balance: 90, number: 12345678, lobby: true, legacy: false, templates: [
      { id: 'Basic', name: 'Базовая квартира', description: 'Уютная квартира для жизни.', category: 'Классика', width: 11, height: 16, price: 0, owned: true, selected: true, popularity: 30 },
      { id: 'Loft', name: 'Лофт', description: 'Просторная гостиная.', category: 'Индустриальная', width: 12, height: 16, price: 0, owned: true, popularity: 8 },
      { id: 'Gothic', name: 'Готический зал', description: 'Тёмные стены и витражи.', category: 'Готика', width: 12, height: 18, price: 50, popularity: 5 },
      { id: 'Industrial', name: 'Мастерская', description: 'Открытая индустриальная планировка.', category: 'Индустриальная', width: 14, height: 18, price: 80, popularity: 20 },
      { id: 'Space', name: 'Обсерватория', description: 'Панорама звёзд.', category: 'Космос', width: 20, height: 20, price: 100, popularity: 2 },
    ] };
    await publish(state);
    await page.getByRole('button', { name: 'Выбрано', exact: true }).waitFor();
    assert.equal(await page.getByRole('button', { name: 'Выбрано', exact: true }).isDisabled(), true);
    assert.equal(await page.locator('article').count(), 2);
    assert.equal(await page.getByRole('button', { name: 'Закрыть', exact: true }).count(), 0);
    const loft = page.locator('article').filter({ has: page.getByRole('heading', { name: 'Лофт', exact: true }) });
    await loft.getByRole('button', { name: 'Выбрать', exact: true }).click();
    await page.getByRole('button', { name: 'Отмена', exact: true }).click();
    assert.equal(actions.filter(a => a.type === 'act/select').length, 0);
    await page.getByRole('tab', { name: /Каталог/ }).click();
    assert.equal(await page.locator('article').count(), 3);
    assert.deepEqual(await page.locator('article h3').allTextContents(), ['Готический зал', 'Мастерская', 'Обсерватория']);
    await page.getByRole('combobox', { name: 'Сортировка' }).click();
    await page.getByRole('option', { name: 'Сначала популярные' }).click();
    assert.deepEqual(await page.locator('article h3').allTextContents(), ['Мастерская', 'Готический зал', 'Обсерватория']);
    await page.getByRole('combobox', { name: 'Сортировка' }).click();
    await page.getByRole('option', { name: 'Сначала дорогие' }).click();
    assert.deepEqual(await page.locator('article h3').allTextContents(), ['Обсерватория', 'Мастерская', 'Готический зал']);
    await page.getByRole('combobox', { name: 'Тематика' }).click();
    await page.getByRole('option', { name: 'Готика', exact: true }).click();
    assert.equal(await page.locator('article').count(), 1);
    await page.getByRole('textbox', { name: 'Поиск квартир' }).fill('витражи');
    assert.equal(await page.locator('article').count(), 1);
    await page.getByRole('spinbutton', { name: 'Максимальная цена' }).fill('40');
    assert.equal(await page.locator('article').count(), 0);
    await page.getByRole('button', { name: 'Сбросить фильтры' }).click();
    const gothic = page.locator('article').filter({ has: page.getByRole('heading', { name: 'Готический зал', exact: true }) });
    await gothic.locator('summary').click();
    assert.match(await gothic.innerText(), /Тёмные стены/);
    await gothic.getByRole('button', { name: 'Предпросмотр', exact: true }).click();
    await page.getByRole('dialog').waitFor();
    await page.waitForTimeout(50);
    assert.equal(actions.filter(a => a.type === 'act/select').length, 0, 'Preview must not purchase');
    assert.equal(actions.filter(a => a.type === 'act/preview').length, 1);
    state.preview = { id: 'Gothic', x: 0, y: 0, width: 12, height: 18, tiles: [], objects: [] };
    await publish(state);
    await page.getByRole('img', { name: 'Планировка квартиры с мебелью' }).waitFor();
    await page.getByRole('button', { name: 'Назад к квартирам' }).click();
    await gothic.getByRole('button', { name: 'Купить и выбрать', exact: true }).click();
    await page.getByRole('button', { name: 'Подтвердить', exact: true }).click();
    assert.equal(await page.getByRole('button', { name: 'Обработка…', exact: true }).isDisabled(), true);
    await page.waitForTimeout(100);
    const purchases = actions.filter(a => a.type === 'act/select');
    assert.equal(purchases.length, 1);
    const payload = typeof purchases[0].payload === 'string' ? JSON.parse(purchases[0].payload) : purchases[0].payload;
    assert.equal(payload.template, 'Gothic');
    state.token = 'purchased'; state.balance = 40; state.number = 87654321; state.lobby = false; state.legacy = true;
    state.templates[0].selected = false; state.templates[2].selected = true; state.templates[2].owned = true; state.templates[2].price = 0;
    state.message = 'Шаблон выбран. Новый номер: 87654321.';
    await publish(state);
    await page.getByRole('status').waitFor();
    await page.getByRole('tab', { name: /Мои квартиры/ }).click();
    assert.equal(await page.locator('article').count(), 3);
    await page.getByRole('button', { name: 'Обустройство квартиры', exact: true }).click();
    await page.getByRole('button', { name: 'Покинуть старую квартиру', exact: true }).click();
    fs.mkdirSync(output, { recursive: true });
    await page.screenshot({ path: path.join(output, 'apartments.png') });
    for (const size of [{ width: 740, height: 600 }, { width: 520, height: 420 }]) {
      await page.setViewportSize(size);
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true, 'No horizontal window overflow');
      await page.screenshot({ path: path.join(output, `apartments-${size.width}.png`) });
    }
    await page.setViewportSize({ width: 1000, height: 720 });
    const economy = { balance: 1600, tier: 2, color: '#7ABFA5', playerName: 'LagoonPlayer', referralCode: '0123456789ABCDEF0123', referrerBound: false, referrals: [{ name: 'Друг', stage: 1, reward: 25 }], passTier: 1, passDays: 15, subscriptions: [
      { id: 1, name: 'Искра', price: 25, rate: 2 }, { id: 2, name: 'Импульс', price: 600, rate: 4 }, { id: 3, name: 'Сияние', price: 1500, rate: 5 }, { id: 4, name: 'Сверхновая', price: 6000, rate: 6 },
    ] };
    await publish(economy, 'LagoonEconomy');
    await page.getByRole('tab', { name: 'Подписки', exact: true }).waitFor();
    assert.equal(await page.getByRole('textbox', { name: 'Цвет OOC HEX' }).count(), 0);
    const impulse = page.locator('article').filter({ has: page.getByRole('heading', { name: 'Импульс', exact: true }) });
    assert.match(await impulse.locator('.price').innerText(), /288/);
    await impulse.getByRole('button', { name: 'Повысить уровень' }).click();
    await page.getByRole('button', { name: 'Отмена', exact: true }).click();
    await impulse.getByRole('button', { name: 'Повысить уровень' }).click();
    await page.getByRole('button', { name: 'Подтвердить покупку' }).click();
    assert.equal(await page.getByRole('button', { name: 'Обработка…', exact: true }).isDisabled(), true);
    await page.waitForTimeout(50);
    assert.equal(actions.filter(a => a.type === 'act/pass').length, 1);
    economy.passTier = 2; economy.balance -= 288;
    await publish(economy, 'LagoonEconomy');
    await page.getByRole('button', { name: 'Продлить на 30 дней' }).waitFor();
    await page.screenshot({ path: path.join(output, 'subscriptions.png') });
    await page.getByRole('tab', { name: 'Настройки пользователя' }).click();
    assert.equal(await page.locator('input[type=color]').count(), 0, 'Never open a native CEF color popup');
    await page.getByRole('button', { name: 'Цвет #83C7ED', exact: true }).click();
    await page.getByRole('slider', { name: 'Канал R', exact: true }).press('ArrowRight');
    assert.notEqual(await page.getByRole('textbox', { name: 'Цвет OOC HEX' }).inputValue(), '#83C7ED');
    await page.getByRole('textbox', { name: 'Цвет OOC HEX' }).fill('#FFFFFF');
    assert.equal(await page.getByRole('button', { name: 'Сохранить цвет' }).isDisabled(), true);
    await page.getByRole('textbox', { name: 'Цвет OOC HEX' }).fill('#8AC0E0');
    await page.getByRole('button', { name: 'Сохранить цвет' }).click();
    assert.equal(await page.getByRole('button', { name: 'Сохранение…' }).isDisabled(), true);
    await page.waitForTimeout(100);
    const colorActions = actions.filter(a => a.type === 'act/color');
    assert.equal(colorActions.length, 1);
    assert.equal((typeof colorActions[0].payload === 'string' ? JSON.parse(colorActions[0].payload) : colorActions[0].payload).product, '#8AC0E0');
    economy.color = '#8AC0E0'; economy.message = 'Изменения сохранены.';
    await publish(economy, 'LagoonEconomy');
    await page.getByRole('button', { name: 'Сохранить цвет' }).waitFor();
    assert.equal(await page.getByRole('button', { name: 'Сохранить цвет' }).isDisabled(), true);
    await page.screenshot({ path: path.join(output, 'ooc-settings.png') });
    await page.getByRole('tab', { name: 'Приглашение друзей' }).click();
    assert.equal(await page.getByRole('textbox', { name: 'Ваш код приглашения', exact: true }).inputValue(), economy.referralCode);
    await page.getByRole('button', { name: 'Копировать код', exact: true }).click();
    await page.getByRole('button', { name: 'Код скопирован', exact: true }).waitFor();
    await page.waitForTimeout(50);
    const copies = actions.filter(a => a.type === 'act/copyReferral');
    assert.equal(copies.length, 1);
    assert.equal((typeof copies[0].payload === 'string' ? JSON.parse(copies[0].payload) : copies[0].payload).code, economy.referralCode);
    assert.match(await page.locator('.LagoonStore__refList').innerText(), /Друг.*25 LC/s);
    await page.getByRole('textbox', { name: 'Код пригласившего', exact: true }).fill('ABCDEF0123456789ABCD');
    await page.getByRole('button', { name: 'Закрепить код' }).click();
    await page.waitForTimeout(100);
    assert.equal(actions.filter(a => a.type === 'act/referral').length, 1);
    economy.referrerBound = true;
    await publish(economy, 'LagoonEconomy');
    assert.equal(await page.getByRole('textbox', { name: 'Код пригласившего', exact: true }).count(), 0);
    await page.screenshot({ path: path.join(output, 'referrals.png') });
    for (const size of [{ width: 740, height: 600 }, { width: 520, height: 420 }]) {
      await page.setViewportSize(size);
      for (const tab of ['Подписки', 'Настройки пользователя', 'Приглашение друзей']) {
        await page.getByRole('tab', { name: tab, exact: true }).click();
        assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true, 'No horizontal window overflow');
      }
    }
    assert.deepEqual(errors, []);
    console.log('PASS: owned/catalog switching, theme/text/price filters, all sorting modes, safe preview, one purchase, responsive layout, subscription prices, RGB/HEX/palette, validation, save, referrals and no custom window close buttons');
  } finally { await browser.close(); server.close(); }
})().catch(error => { console.error(error); server.close(); process.exitCode = 1; });
