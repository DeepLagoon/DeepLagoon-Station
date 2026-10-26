const assert = require('node:assert/strict');
const fs = require('node:fs');
const http = require('node:http');
const path = require('node:path');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const root = path.resolve(__dirname, '../../Resources/Web/DeepLagoon');
const texture = file => 'data:image/png;base64,' + fs.readFileSync(path.resolve(__dirname, '../../Resources/Textures', file)).toString('base64');

(async () => {
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
    const page = await browser.newPage({ viewport: { width: 1280, height: 860 } });
    const errors = [];
    page.on('pageerror', error => errors.push(String(error)));
    await page.addInitScript(() => {
      window.__sent = [];
      const NativeImage = window.Image;
      window.Image = class extends NativeImage {
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
    // Source sheets are cropped in the browser, as the native host sends single south frames.
    const crop = async file => page.evaluate(async url => {
      const image = document.createElement('img'); image.src = url; await image.decode();
      const canvas = document.createElement('canvas'); canvas.width = canvas.height = 32;
      canvas.getContext('2d').drawImage(image, 0, 0, 32, 32, 0, 0, 32, 32);
      return canvas.toDataURL();
    }, texture(file));
    const headFrame = await crop('Mobs/Species/Human/parts.rsi/head_m.png');
    const eyeFrame = await crop('Mobs/Customization/eyes.rsi/eyes.png');
    const head = await page.evaluate(async ({ headFrame, eyeFrame }) => {
      const tint = async (url, color) => {
        const image = document.createElement('img'); image.src = url; await image.decode();
        const canvas = document.createElement('canvas'); canvas.width = canvas.height = 32;
        const context = canvas.getContext('2d'); context.drawImage(image, 0, 0);
        context.globalCompositeOperation = 'multiply'; context.fillStyle = color; context.fillRect(0, 0, 32, 32);
        context.globalCompositeOperation = 'destination-in'; context.drawImage(image, 0, 0);
        return canvas;
      };
      const face = await tint(headFrame, '#c69a7c');
      face.getContext('2d').globalCompositeOperation = 'source-over';
      face.getContext('2d').drawImage(await tint(eyeFrame, '#4477ee'), 0, 0);
      return face.toDataURL();
    }, { headFrame, eyeFrame });
    const hair = await crop('Mobs/Customization/human_hair.rsi/afro.png');
    const option = (id, name) => ({ id, name, images: [{ url: hair }] });
    const state = { available: true, mode: 'appearance', appearance: {}, name: 'Preview', species: 'Human',
      height: 1, width: 1, age: 30, minAge: 18, maxAge: 90, skinColor: '#c69a7c', eyeColor: '#4477ee',
      hairColor: '#cc2233', beardColor: '#44bb77', hairStyle: 'HumanHairAfro', beardStyle: 'BeardShort',
      hairPreviewImage: head, tabs: [], hairOptions: [{ id: 'HairBald', name: 'Без волос' }, option('HumanHairAfro', 'Афро'),
        option('BlueMoonHairNovaHair', 'Nova Ponytail'), ...Array.from({ length: 40 }, (_, i) => option('Style' + i, 'Причёска ' + i))],
      beardOptions: [{ id: 'FacialHairShaved', name: 'Без бороды' }, option('BeardShort', 'Короткая борода'), option('BeardLong', 'Длинная борода')] };
    const publish = data => page.evaluate(data => window.update(JSON.stringify({ type: 'update', payload: {
      replaceData: true, replaceStaticData: true, resetPartialData: true,
      config: { interface: 'CharacterEditor', status: 2, window: { key: 'hair-test', fancy: false } }, data } })), data);
    await publish(state);
    await page.getByText('Выбор причёски', { exact: true }).click();
    await page.getByText('Выбор бороды и усов', { exact: true }).click();
    const hairSearch = page.getByRole('searchbox', { name: 'Поиск причёски' });
    const beardSearch = page.getByRole('searchbox', { name: 'Поиск бороды и усов' });
    const hairBrowser = page.locator('.HairCarousel__browser').nth(0);
    const beardBrowser = page.locator('.HairCarousel__browser').nth(1);
    for (const width of [1280, 800, 480, 420]) {
      await page.setViewportSize({ width, height: 860 });
      await hairSearch.fill('аФрО');
      assert.equal(await hairBrowser.locator('.HairCarousel__track > button').count(), 1);
      assert.equal(await beardBrowser.locator('.HairCarousel__track > button').count(), 3);
      await hairSearch.fill('nova');
      await hairBrowser.getByRole('button', { name: 'Nova Ponytail', exact: true }).click();
      assert.ok(await page.evaluate(() => window.__sent.some(e => e.type === 'act/hair-style' && e.payload.value === 'BlueMoonHairNovaHair')));
      await beardSearch.fill('длинная');
      await beardBrowser.getByRole('button', { name: 'Длинная борода', exact: true }).click();
      assert.ok(await page.evaluate(() => window.__sent.some(e => e.type === 'act/beard-style' && e.payload.value === 'BeardLong')));
      await hairSearch.fill('zzz999небывает');
      assert.equal(await hairBrowser.getByRole('status').innerText(), 'Ничего не найдено');
      await hairSearch.fill(''); await beardSearch.fill('');
      assert.equal(await hairBrowser.locator('.HairCarousel__track > button').count(), 43);
      assert.equal(await hairBrowser.locator('.HairCarousel__face').count(), 43);
      const layering = await hairBrowser.locator('.CharacterEditor__sprite').first().evaluate(e => {
        const images = [...e.querySelectorAll('img')];
        return { faceFirst: images[0].classList.contains('HairCarousel__face'), tinted: getComputedStyle(images[0]).filter !== 'none' };
      });
      assert.deepEqual(layering, { faceFirst: true, tinted: false });
      assert.equal(await page.locator('.CharacterEditor').evaluate(e => e.scrollWidth > e.clientWidth), false, `overflow at ${width}`);
      await hairBrowser.getByRole('button', { name: 'Следующие четыре' }).click();
      await page.waitForFunction(() => document.querySelector('.HairCarousel__track').scrollLeft > 0);
      await hairSearch.fill('nova');
      assert.equal(await hairBrowser.locator('.HairCarousel__track').evaluate(e => e.scrollLeft), 0);
    }
    if (process.env.HAIR_SCREENSHOT) {
      await page.setViewportSize({ width: 1280, height: 1000 });
      await hairSearch.fill('');
      await page.waitForFunction(() => document.querySelector('.HairCarousel__track').scrollLeft === 0);
      await hairBrowser.scrollIntoViewIfNeeded();
      await page.screenshot({ path: process.env.HAIR_SCREENSHOT });
    }
    await publish({ ...state, species: 'Diona', hairOptions: [{ id: 'HairBald', name: 'Без волос' }], beardOptions: [{ id: 'FacialHairShaved', name: 'Без бороды' }] });
    assert.equal(await page.getByRole('searchbox').count(), 0);
    assert.deepEqual(errors, []);
    console.log('PASS: independent Cyrillic/Latin hair and beard searches, selection, clear/no-results, scroll reset, untinted face layering; 1280/800/480/420; species without styles.');
  } finally { await browser?.close(); await new Promise(resolve => server.close(resolve)); }
})().catch(error => { console.error(error); process.exitCode = 1; });
