const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const resources = path.resolve(__dirname, '../../Resources/Web/DeepLagoon');

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
    const page = await browser.newPage({ viewport: { width: 740, height: 600 } });
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
    const publish = (atHome, busy = false) => page.evaluate(({ atHome, busy }) => window.update(JSON.stringify({ type: 'update', payload: {
      replaceData: true, config: { interface: 'ApartmentElevator', status: 2, title: 'Лифт', window: { key: 'elevator', fancy: false } },
      data: { number: 72519483, atHome, busy, message: '' },
    } })), { atHome, busy });
    await publish(false);
    await page.locator('.ApartmentElevator__keys').waitFor();
    assert.equal(await page.locator('input').count(), 0);
    assert.equal(await page.locator('.ApartmentElevator__keys button').count(), 12);
    assert.equal(await page.getByRole('button', { name: 'Отправка', exact: true }).isDisabled(), true);
    for (const digit of '72519483') await page.getByRole('button', { name: digit, exact: true }).click();
    assert.equal(await page.locator('output').textContent(), '72519483');
    assert.equal(await page.getByRole('button', { name: '1', exact: true }).isDisabled(), true);
    await page.getByRole('button', { name: 'Удалить цифру', exact: true }).click();
    assert.equal(await page.locator('output').textContent(), '7251948');
    await page.getByRole('button', { name: '3', exact: true }).click();
    await page.getByRole('button', { name: 'Отправка', exact: true }).click();
    await page.waitForFunction(() => window.__sent.some(message => message.type === 'act/depart'));
    assert.equal(await page.evaluate(() => window.__sent.find(message => message.type === 'act/depart').payload.number), '72519483');
    await page.getByRole('button', { name: 'Сбросить номер', exact: true }).click();
    assert.equal(await page.getByRole('button', { name: 'Отправка', exact: true }).isDisabled(), true);
    const departures = await page.evaluate(() => window.__sent.filter(message => message.type === 'act/depart').length);
    await page.getByRole('button', { name: 'Ввести свой номер', exact: true }).click();
    assert.equal(await page.locator('output').textContent(), '72519483');
    assert.equal(await page.getByRole('button', { name: 'Отправка', exact: true }).isDisabled(), false);
    assert.equal(await page.evaluate(() => window.__sent.filter(message => message.type === 'act/depart').length), departures);
    for (const size of [{ width: 740, height: 600 }, { width: 520, height: 420 }]) {
      await page.setViewportSize(size);
      assert.equal(await page.evaluate(() => !!document.elementFromPoint(10, innerHeight - 2)?.closest('.ApartmentElevator')), true);
      assert.equal(await page.evaluate(() => document.querySelector('.ApartmentElevator').getBoundingClientRect().bottom), size.height);
    }
    await page.setViewportSize({ width: 740, height: 600 });
    await page.screenshot({ path: path.resolve(__dirname, '../../artifacts/apartment-elevator-keypad.png') });
    await publish(false, true);
    assert.equal(await page.locator('.ApartmentElevator__keys button:enabled').count(), 0);
    assert.equal(await page.getByRole('button', { name: 'Ввести свой номер', exact: true }).isDisabled(), true);
    await publish(true);
    assert.equal(await page.locator('.ApartmentElevator__keys').count(), 0);
    assert.equal(await page.getByRole('button', { name: 'Ввести свой номер', exact: true }).count(), 0);
    await page.getByRole('button', { name: 'Вернуться на жилую остановку' }).click();
    await page.waitForFunction(() => window.__sent.some(message => message.type === 'act/return'));
    assert.deepEqual(errors, []);
    console.log('PASS: keypad, editing, payload, busy/return states and full-height background at two window sizes');
  } finally {
    await browser?.close();
    await new Promise(resolve => server.close(resolve));
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
