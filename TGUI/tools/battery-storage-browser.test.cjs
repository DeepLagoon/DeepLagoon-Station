// Render the shipped interface bundle and exercise the real action bridge.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const http = require('node:http');
const path = require('node:path');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const repo = path.resolve(__dirname, '../..');
const resources = path.join(repo, 'Resources/Web/DeepLagoon');
const output = path.join(repo, 'TGUI/node_modules/.cache/battery-storage-browser');
fs.mkdirSync(output, { recursive: true });
const labelsFor = language => Object.fromEntries([...fs.readFileSync(path.join(repo,
  `Resources/Locale/${language}/_DeepLagoon/battery.ftl`), 'utf8').matchAll(/^dl-battery-([\w-]+)\s*=\s*(.+)$/gm)]
  .map(match => [match[1], match[2].trim()]));
const initial = {
  title: 'подстанция', smes: false, ready: true, input: true, output: true,
  inputPowered: true, outputPowered: true, fraction: 1, percentText: '100.0 %', energyText: '694.4 Вт·ч',
  etaLabel: 'Время (разрядка):', etaText: 'Н/Д', inputText: '13.5 кВт', outputText: '13.5 кВт',
  chargeText: '0.0 Вт', dischargeText: '0.0 Вт', chargeLimitText: '5.0 кВт', dischargeLimitText: '150.0 кВт',
  passthroughText: '13.5 кВт', passthrough: 13500, chargeLimit: 5000, chargeMin: 2500, chargeMax: 85000,
  dischargeLimit: 150000, dischargeMin: 10000, dischargeMax: 450000,
};

(async () => {
  const server = http.createServer((request, response) => {
    const file = path.resolve(resources, '.' + new URL(request.url, 'http://localhost').pathname);
    if (!file.startsWith(resources + path.sep) || !fs.existsSync(file) || !fs.statSync(file).isFile()) {
      response.writeHead(404).end(); return;
    }
    response.setHeader('Content-Type', ({ '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.woff2': 'font/woff2' })[path.extname(file)] || 'application/octet-stream');
    response.end(fs.readFileSync(file));
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  let browser;
  try {
    browser = await chromium.launch({ channel: process.env.BROWSER_CHANNEL || 'msedge', headless: true });
    const page = await browser.newPage({ viewport: { width: 650, height: 660 } });
    const errors = []; page.on('pageerror', e => errors.push(String(e)));
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
    const publish = (data, language = 'ru-RU') => page.evaluate(({ data, labels }) => window.update(JSON.stringify({ type: 'update', payload: {
      replaceData: true, replaceStaticData: true, resetPartialData: true,
      config: { interface: 'BatteryStorage', title: data.title, status: 2, window: { key: 'battery', fancy: false } },
      static_data: { labels }, data,
    } })), { data, labels: labelsFor(language) });
    const update = data => page.evaluate(data => window.update(JSON.stringify({ type: 'update', payload: { data } })), data);
    const actions = () => page.evaluate(() => window.__sent.filter(m => m.type.startsWith('act/')).map(m => {
      const { _page, _sequence, ...payload } = m.payload;
      return { type: m.type, payload };
    }));

    await publish({ ...initial, ready: false });
    await page.getByRole('button', { name: 'ВВОД', exact: true }).waitFor();
    assert(await page.getByRole('button', { name: 'ВВОД', exact: true }).isDisabled());
    await publish(initial);
    await page.waitForFunction(() => document.querySelectorAll('.BatteryStorage__battery-strip .is-lit').length === 10);
    assert.equal(await page.locator('.BatteryStorage__screw').count(), 4);
    assert.equal(await page.locator('.BatteryStorage__cable-bay i').count(), 3);

    for (const smes of [false, true]) {
      await publish({ ...initial, smes, energyText: smes ? '4.4 кВт·ч' : initial.energyText });
      await page.locator('.BatteryStorage__readings').getByText(smes ? '4.4 кВт·ч' : initial.energyText, { exact: true }).waitFor();
      assert.equal(await page.locator('.BatteryStorage--smes').count(), smes ? 1 : 0);
      for (const side of ['input', 'output']) {
        const name = side === 'input' ? 'ВВОД' : 'ВЫВОД';
        const button = page.getByRole('button', { name, exact: true });
        const lever = button.locator('.BatteryStorage__lever');
        const on = await lever.boundingBox();
        const before = (await actions()).length;
        await button.click();
        await page.waitForFunction(n => window.__sent.filter(m => m.type.startsWith('act/')).length > n, before);
        assert.deepEqual((await actions()).at(-1), { type: 'act/' + side, payload: { on: false } });
        await update({ [side]: false });
        await page.waitForFunction(name => [...document.querySelectorAll('.BatteryStorage__toggle')]
          .find(b => b.getAttribute('aria-label') === name)?.getAttribute('aria-pressed') === 'false', name);
        await lever.evaluate(e => Promise.all(e.getAnimations().map(a => a.finished)));
        const off = await lever.boundingBox();
        assert(Math.abs(on.x - off.x) < .5 && off.y > on.y + 15, 'Switch moves down to O without sideways drift');
        await button.click();
        await update({ [side]: true });
        await page.waitForFunction(name => [...document.querySelectorAll('.BatteryStorage__toggle')]
          .find(b => b.getAttribute('aria-label') === name)?.getAttribute('aria-pressed') === 'true', name);
        await lever.evaluate(e => Promise.all(e.getAnimations().map(a => a.finished)));
      }
      await page.screenshot({ path: path.join(output, smes ? 'smes.png' : 'substation.png'), fullPage: true });
    }

    for (const [name, value, action] of [['Макс. заряд', '12345.5', 'charge-rate'], ['Макс. разряд', '275000', 'discharge-rate']]) {
      const slider = page.getByRole('slider', { name, exact: true });
      const before = (await actions()).length;
      await slider.fill(value);
      await page.waitForFunction(n => window.__sent.filter(m => m.type.startsWith('act/')).length > n, before);
      assert.deepEqual((await actions()).at(-1), { type: 'act/' + action, payload: { value: Number(value) } });
    }
    assert.equal(await page.getByRole('slider', { name: 'Макс. заряд', exact: true }).getAttribute('min'), '2500');
    assert.equal(await page.getByRole('slider', { name: 'Макс. разряд', exact: true }).getAttribute('max'), '450000');
    await update({ inputPowered: false, outputPowered: false });
    assert(await page.getByRole('button', { name: 'ВВОД', exact: true }).isEnabled(), 'Breakers remain usable on an unpowered grid');
    assert.equal(await page.locator('.BatteryStorage__network.is-powered').count(), 0);
    for (const [fraction, count] of [[0, 0], [.5, 5], [1, 10]]) {
      await update({ fraction, percentText: `${fraction * 100}.0 %` });
      await page.waitForFunction(count => document.querySelectorAll('.BatteryStorage__battery-strip .is-lit').length === count, count);
    }
    for (const [watts, unit] of [[0, 'Вт'], [13500, 'кВт'], [4500000, 'МВт'], [2e9, 'ГВт']]) {
      await update({ passthrough: watts });
      await page.locator('.BatteryStorage__unit').getByText(unit, { exact: true }).waitFor();
    }
    await publish({ ...initial, title: '<img src=x onerror=alert(1)>' });
    assert.equal(await page.locator('.BatteryStorage img').count(), 0);
    for (const [width, height] of [[650, 660], [400, 460], [320, 640]]) {
      await page.setViewportSize({ width, height });
      const size = await page.evaluate(() => ({ width: innerWidth, scroll: document.documentElement.scrollWidth }));
      assert(size.scroll <= size.width + 1, `No horizontal overflow at ${width}`);
      for (const slider of await page.getByRole('slider').all()) await slider.scrollIntoViewIfNeeded();
      await page.locator('.BatteryStorage__footer').scrollIntoViewIfNeeded();
      const footer = await page.locator('.BatteryStorage__footer').boundingBox();
      assert(footer.y >= 0 && footer.y + footer.height <= height + 1, `Footer remains reachable at ${width}`);
    }
    await publish(initial, 'en-US');
    await page.getByRole('button', { name: 'INPUT', exact: true }).waitFor();
    assert.deepEqual(errors, []);
    console.log(JSON.stringify({ passed: true, skins: 2, checks: 'loading, real bridge actions, switch geometry, server bounds, charge levels, units, grid status, localization, 650/400/320px', screenshots: output }));
  } finally {
    if (browser) await browser.close();
    await new Promise(resolve => server.close(resolve));
  }
})().catch(e => { console.error(e); process.exitCode = 1; });
