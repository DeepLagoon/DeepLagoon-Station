// Exercise the shipped TGUI bundle and its real lazy icon bridge; no interface or hook mocks.
// PLAYWRIGHT_MODULE points at Playwright when it is provided by the workspace runtime.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const http = require('node:http');
const path = require('node:path');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const repo = path.resolve(__dirname, '../..');
const resources = path.join(repo, 'Resources/Web/DeepLagoon');
const output = path.join(repo, 'TGUI/node_modules/.cache/vending-machine-browser');
fs.mkdirSync(output, { recursive: true });
const labels = Object.fromEntries([...fs.readFileSync(path.join(repo, 'Resources/Locale/ru-RU/_DeepLagoon/vending.ftl'), 'utf8')
  .matchAll(/^dl-vending-([\w-]+)\s*=\s*(.+)$/gm)].map(match => [match[1], match[2].trim()]));
const names = ['Чипсы', 'Боритос', 'Сырные хонкеры', 'Шоколадный батончик', 'Энергетический батончик', 'Попкорн', 'Конфеты', 'Изюм', 'Фисташки', 'Семечки', 'Питательный брикет', 'Вяленое мясо'];
const imageNames = ['chips','boritos','cheesiehonkers','chocolatebar','energybar','popcorn','cnds','raisins','pistachio','semki','nutribrick','susjerky'];
const images = imageNames.map(name => 'data:image/png;base64,' + fs.readFileSync(path.join(repo, 'Resources/Textures/Objects/Consumable/Food/snacks.rsi', name + '.png')).toString('base64'));
const items = Array.from({ length: 144 }, (_, index) => ({
  key: `0:FoodSnack${index}`, id: `FoodSnack${index}`, type: 0,
  name: names[index % names.length] + (index >= names.length ? ` №${index}` : ''),
  description: `Описание товара «${names[index % names.length]}». Цена и наличие приходят из текущего автомата.`,
  code: `${String.fromCharCode(65 + Math.floor(index / 3) % 26)}${index % 3 + 1}`,
  price: 20 + index, priceText: `${20 + index} $`,
}));
items.push({ ...items[0], key: '2:FoodSnack0', type: 2, name: 'Контрабандные чипсы', code: 'ZZ1' });
const stock = items.map((item, index) => ({ key: item.key, available: index !== 7, unlimited: index === 8, amount: index === 8 ? '4294967295' : index === 7 ? '0' : '12' }));
const initial = { title: 'Getmore — торговый автомат', hasBankAccount: true, balanceText: '850 $', acceptsCash: true, cashText: '40 $', requiresCash: true, status: 'ready', stock };

(async () => {
  const server = http.createServer((request, response) => {
    const file = path.resolve(resources, '.' + new URL(request.url, 'http://localhost').pathname);
    if (!file.startsWith(resources + path.sep) || !fs.existsSync(file) || !fs.statSync(file).isFile()) { response.writeHead(404).end(); return; }
    response.setHeader('Content-Type', ({ '.html':'text/html', '.js':'text/javascript', '.css':'text/css', '.woff2':'font/woff2', '.svg':'image/svg+xml' })[path.extname(file)] || 'application/octet-stream');
    response.end(fs.readFileSync(file));
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  let browser;
  const report = {};
  try {
    browser = await chromium.launch({ channel: process.env.BROWSER_CHANNEL || 'msedge', headless: true });
    const page = await browser.newPage({ viewport: { width: 760, height: 660 } });
    page.setDefaultTimeout(15000);
    const errors = [];
    page.on('pageerror', error => errors.push(String(error)));
    await page.addInitScript(images => {
      window.__sent = [];
      const NativeImage = window.Image;
      window.Image = class extends NativeImage {
        set src(value) {
          if (!value.startsWith('res://deeplagoon/Web/DeepLagoon/bridge?')) { super.src = value; return; }
          const [type, payload] = value.split('?')[1].split('&');
          const message = { type: decodeURIComponent(type), payload: JSON.parse(decodeURIComponent(payload)) };
          window.__sent.push(message);
          queueMicrotask(() => {
            this.onload?.(new Event('load'));
            if (message.type === 'act/icons') {
              window.update(JSON.stringify({ type: 'update', payload: { data: { iconBatch: { icons: message.payload.ids.split(',').map(id => ({ id,
                layers: [{ url: images[Number(id.replace('FoodSnack','')) % images.length], color: '#ffffff' }],
              })) } } } }));
            }
          });
        }
      };
    }, images);
    await page.goto(`http://127.0.0.1:${server.address().port}/interface.html`);
    await page.waitForFunction(() => window.__tguiAppBooted__);
    const publish = (dynamic, catalogue = items) => page.evaluate(({ dynamic, catalogue, labels }) => window.update(JSON.stringify({ type: 'update', payload: {
      replaceData: true, replaceStaticData: true, resetPartialData: true,
      config: { interface: 'VendingMachine', title: dynamic.title, status: 2, window: { key: 'deeplagoon', fancy: false } },
      static_data: { items: catalogue, labels }, data: dynamic,
    } })), { dynamic, catalogue, labels });
    const update = data => page.evaluate(data => window.update(JSON.stringify({ type: 'update', payload: { data } })), data);
    await publish(initial);
    await page.locator('.VendingMachine__sprite img').first().waitFor();
    await page.waitForFunction(() => document.querySelector('.VendingMachine__wear').style.backgroundImage.includes('data:image/svg+xml'));
    await page.getByRole('heading', { name: 'Getmore', exact: true }).waitFor();
    assert.equal(await page.locator('.VendingMachine__brand p').count(), 0, 'The brand screen has no explanatory subtitle');
    await page.waitForFunction(() => {
      const sign = document.querySelector('.VendingMachine__brand-bulbs').getBoundingClientRect();
      const screen = document.querySelector('.VendingMachine__brand-title').getBoundingClientRect();
      return sign.width > screen.width * .9 && sign.left >= screen.left && sign.right <= screen.right;
    }).catch(async error => {
      await page.screenshot({ path: path.join(output, 'brand-debug.png') });
      console.error(await page.locator('.VendingMachine__brand-title').evaluate(element => ({
        screen: element.getBoundingClientRect().toJSON(), sign: element.querySelector('g').getBoundingClientRect().toJSON(),
        svg: element.querySelector('svg').outerHTML.slice(0, 180),
        svgRect: element.querySelector('svg').getBoundingClientRect().toJSON(), box: element.querySelector('g').getBBox(),
        styles: ['width','height','padding','box-sizing','font-size'].map(key=>[key,getComputedStyle(element.querySelector('svg')).getPropertyValue(key)]),
      })));
      throw error;
    });
    assert(await page.locator('.VendingMachine__brand-bulbs circle').count() > 100, 'The brand uses individual lit bulbs');
    for (const title of ['торговый автомат «Газировка»', 'Getmore Chocolate Corp']) {
      await update({ title });
      await page.getByRole('heading', { name: title.includes('Газировка') ? 'Газировка' : title, exact: true }).waitFor();
    }
    await update({ title: initial.title });
    await page.getByRole('heading', { name: 'Getmore', exact: true }).waitFor();
    const wearBeforeHover = await page.locator('.VendingMachine__wear').evaluate(element => element.style.backgroundImage);
    const nicheSettings = await page.locator('.VendingMachine').evaluate(element => {
      const style=getComputedStyle(element);
      return ['--vm-niche-depth','--vm-niche-light','--vm-niche-dark','--vm-niche-lamp'].map(key=>style.getPropertyValue(key).trim());
    });
    assert.deepEqual(nicheSettings,['7px','.75','.6','#ffe7ac']);
    assert.equal(await page.locator('.VendingMachine__screw').count(),4);
    assert.equal(await page.locator('.VendingMachine__hinge').count(),2);
    const initialIcons = await page.evaluate(() => window.__sent.filter(message => message.type === 'act/icons').flatMap(message => message.payload.ids.split(',')));
    assert(initialIcons.length > 0 && initialIcons.length < 40, `Only visible icons requested, got ${initialIcons.length}`);
    report.initialIcons = initialIcons.length;
    const buttons = page.locator('.VendingMachine__product');
    await buttons.nth(1).hover();
    await page.locator('.Tooltip').filter({ hasText: 'Описание товара' }).waitFor();
    assert.equal(await page.locator('.VendingMachine h2').count(), 0, 'Hover must not select an initial preview');
    assert.equal(await page.locator('.VendingMachine__buy').count(), 0, 'Buying requires a clicked selection');
    assert.equal(await buttons.nth(1).locator('.VendingMachine__light-beam').evaluate(element=>getComputedStyle(element).opacity),'0');
    assert.equal(await page.locator('.VendingMachine__wear').evaluate(element=>element.style.backgroundImage),wearBeforeHover,'Hover must not move or regenerate scratches');
    assert.equal(await page.evaluate(() => window.__sent.filter(message => message.type === 'act/vend').length), 0, 'Hover must never buy');
    await buttons.nth(1).click();
    await page.waitForFunction(()=>getComputedStyle(document.querySelectorAll('.VendingMachine__light-beam')[1]).opacity==='0.75');
    assert.equal(await page.locator('.VendingMachine h2').textContent(), 'Боритос');
    assert(await page.locator('.VendingMachine__well .VendingMachine__sprite').evaluateAll(elements => elements.every(element => {
      const matrix = new DOMMatrix(getComputedStyle(element).transform);
      return matrix.is2D && matrix.a === 1 && matrix.b === 0 && matrix.c === 0 && matrix.d === 1;
    })), 'All products stay upright without rotation or perspective tilt');
    await buttons.first().hover();
    await page.locator('.Tooltip').filter({ hasText: 'Описание товара «Чипсы»' }).waitFor();
    await page.locator('.VendingMachine__toolbar input').focus();await page.keyboard.press('Tab');
    assert.equal(await page.locator('.VendingMachine h2').textContent(), 'Боритос', 'Hover and focus preserve the clicked selection');
    assert.equal(await buttons.first().locator('.VendingMachine__light-beam').evaluate(element=>getComputedStyle(element).opacity),'0','Hover and focus do not light another cell');
    await page.locator('.VendingMachine__brand').hover();
    assert.equal(await buttons.nth(1).locator('.VendingMachine__light-beam').evaluate(element=>getComputedStyle(element).opacity),'0.75','Selected cell stays lit after the cursor leaves');
    await page.getByRole('button', { name: 'Получить · 21 $', exact: true }).click();
    const vend = await page.evaluate(() => window.__sent.filter(message => message.type === 'act/vend').at(-1));
    assert.deepEqual({ id: vend.payload.id, type: vend.payload.type }, { id: 'FoodSnack1', type: 0 });
    await page.locator('.VendingMachine__toolbar input').fill('фисташк');
    await buttons.first().waitFor();
    assert.equal(await page.locator('.VendingMachine h2').textContent(), 'Боритос', 'Search must not automatically select another product');
    await buttons.filter({ hasText: 'C3' }).first().click();
    await page.waitForFunction(() => document.querySelector('.VendingMachine h2')?.textContent === 'Фисташки');
    assert.match(await page.locator('.VendingMachine__price').textContent(), /Без ограничений/);
    await page.locator('.VendingMachine__toolbar input').fill('Фисташик');
    await page.waitForFunction(() => document.querySelector('.VendingMachine h2')?.textContent === 'Фисташки');
    await page.locator('.VendingMachine__toolbar input').fill('Изюм');
    await buttons.filter({ hasText: 'C2' }).first().click();
    await page.getByRole('button', { name: 'Нет в наличии', exact: true }).waitFor();
    assert(await page.locator('.VendingMachine__buy').isDisabled());
    await page.locator('.VendingMachine__toolbar input').fill('ZZ1');
    await page.waitForFunction(() => document.querySelectorAll('.VendingMachine__product').length === 1);
    await buttons.first().click();
    await page.locator('.VendingMachine__buy').click();
    assert.deepEqual(await page.evaluate(() => { const {id,type}=window.__sent.filter(message => message.type === 'act/vend').at(-1).payload; return {id,type}; }), { id: 'FoodSnack0', type: 2 });
    await page.locator('.VendingMachine__toolbar input').fill('Боритос');
    await buttons.filter({ hasText: 'A2' }).first().click();
    await page.waitForFunction(() => document.querySelector('.VendingMachine h2')?.textContent === 'Боритос');
    for (const status of ['off', 'broken', 'busy']) {
      await update({ status }); await page.waitForFunction(() => document.querySelector('.VendingMachine__buy')?.disabled);
      assert.match(await page.locator('.VendingMachine__status').textContent(), new RegExp(labels[status]));
      assert.equal(await page.locator('.VendingMachine__brand-title').evaluate(element => element.classList.contains('is-off')), status !== 'busy');
    }
    await update({ status: 'denied', balanceText: '800 $', cashText: '0 $' });
    await page.waitForFunction(() => !document.querySelector('.VendingMachine__buy')?.disabled);
    assert.equal(await page.locator('.VendingMachine__balance').textContent(), '800 $');
    assert.equal(await page.locator('.VendingMachine h2').textContent(), 'Боритос', 'Balance updates preserve selection');
    await page.locator('.VendingMachine__toolbar input').fill('');
    await page.waitForFunction(() => document.querySelectorAll('.VendingMachine__product').length === 145);
    await page.locator('.VendingMachine__shelves').evaluate(element => { element.scrollTop = element.scrollHeight; });
    await page.waitForFunction(count => window.__sent.filter(message => message.type === 'act/icons').flatMap(message => message.payload.ids.split(',')).length > count, initialIcons.length);
    const loaded = await page.evaluate(() => window.__sent.filter(message => message.type === 'act/icons').flatMap(message => message.payload.ids.split(',')));
    assert.equal(new Set(loaded).size, loaded.length, 'Each icon is requested once, including the same prototype in different inventories');
    assert(loaded.length < 144, 'Scrolling directly to the bottom must not decode the entire catalogue');
    report.iconsAfterScroll = loaded.length;

    // Actual window sizes, including low resolutions and high UI scaling.
    const layouts = [];
    for (const [width, height] of [[760,660], [600,480], [420,420], [320,420]]) {
      await page.setViewportSize({ width, height });
      await page.locator('.VendingMachine__brand').hover();
      await page.waitForFunction(() => !document.querySelector('.Tooltip')?.textContent);
      const geometry = await page.evaluate(() => {
        const side=document.querySelector('.VendingMachine__side');
        const glass=document.querySelector('.VendingMachine__glass');
        const body=document.querySelector('.VendingMachine');
        return { overflow: document.documentElement.scrollWidth > innerWidth || body.scrollWidth > body.clientWidth,
          overlap: glass.getBoundingClientRect().right > side.getBoundingClientRect().left,
          buyClipped: document.querySelector('.VendingMachine__buy').getBoundingClientRect().bottom > side.getBoundingClientRect().bottom+1,
          nameClipped: document.querySelector('.VendingMachine h2').getBoundingClientRect().bottom > side.getBoundingClientRect().bottom+1,
          footer: document.querySelector('.VendingMachine__footer').getBoundingClientRect().bottom > innerHeight+1 };
      });
      if (Object.values(geometry).some(Boolean)) {
        await page.screenshot({path:path.join(output,`layout-${width}x${height}.png`)});
        console.error(await page.evaluate(()=>Object.fromEntries(['side','display','details','details-body','price','buy','status'].map(name=>{
          const element=document.querySelector('.VendingMachine__'+name);const rect=element.getBoundingClientRect();
          return [name,{top:rect.top,bottom:rect.bottom,height:rect.height,scroll:element.scrollHeight}];
        }))));
      }
      assert.deepEqual(geometry, { overflow:false, overlap:false, buyClipped:false, nameClipped:false, footer:false }, `Layout ${width}x${height}`);
      layouts.push([width,height]);
    }
    report.layouts = layouts;
    await page.setViewportSize({ width:760, height:660 });
    await publish(initial, items.slice(0,12));
    await page.locator('.VendingMachine__shelves').evaluate(element => element.scrollTop = 0);
    await page.locator('.VendingMachine__details-body').evaluate(element => element.scrollTop = 0);
    await buttons.nth(1).click();
    await page.waitForTimeout(240);
    // Keep the cell lit while removing only the transient tooltip from the comparison capture.
    await page.locator('.Tooltip').evaluateAll(elements=>elements.forEach(element=>element.style.visibility='hidden'));
    await page.screenshot({ path: path.join(output, 'catalogue.png') });
    const materials = await page.evaluate(() => ({ cabinet:getComputedStyle(document.querySelector('.VendingMachine')).backgroundImage,
      button:getComputedStyle(document.querySelector('.VendingMachine__buy')).backgroundImage,
      product:getComputedStyle(document.querySelector('.VendingMachine__well')).backgroundColor }));
    assert.match(materials.cabinet, /data:image\/svg\+xml/);
    assert.match(materials.button, /rgb\(146, 62, 48\)/);
    assert.equal(materials.product, 'rgb(12, 18, 9)', 'Product niches retain the dark display background');
    await publish(initial);
    await page.locator('.VendingMachine__toolbar input').fill('ZZ1');
    await page.waitForFunction(() => document.querySelectorAll('.VendingMachine__product').length === 1);
    await buttons.first().click();
    await publish(initial, items.slice(0,12));
    await page.getByText('Товары не найдены', { exact:true }).waitFor();
    assert.equal(await page.locator('.VendingMachine__buy').count(),0, 'Removing contraband cannot leave a stale buy button');
    await publish({ ...initial, stock:[] },[]);
    await page.getByText('Автомат пуст', { exact:true }).waitFor();
    // A new window gets a different wear pattern; plain data updates keep the current instance.
    await page.reload();await page.waitForFunction(() => window.__tguiAppBooted__);await publish(initial);
    await page.waitForFunction(() => document.querySelector('.VendingMachine__wear').style.backgroundImage.includes('data:image/svg+xml'));
    assert.notEqual(await page.locator('.VendingMachine__wear').evaluate(element=>element.style.backgroundImage),wearBeforeHover);
    assert.deepEqual(errors,[]);
    report.checks = ['glass brand screen with local lamp matrix and no redundant labels','upright products without tilt','click-only selection and persistent cell lighting','hover and focus never change selection','stable wear and new pattern per window','physical cabinet frame','hover tooltip','explicit purchase and inventory type','fuzzy search preserves clicked selection','unlimited and empty stock','power and dispensing state','incremental payment updates','hidden stock removal','metal texture and red buy button','no JS errors'];
    console.log(JSON.stringify(report));
  } finally {
    await browser?.close();
    await new Promise(resolve => server.close(resolve));
  }
})().catch(error => { console.error(error); process.exitCode=1; });
