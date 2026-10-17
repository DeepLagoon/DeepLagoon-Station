// Exercise the shipped bundle and its native action bridge.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const http = require('node:http');
const path = require('node:path');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const repo = path.resolve(__dirname, '../..');
const resources = path.join(repo, 'Resources/Web/DeepLagoon');
const output = path.join(repo, 'TGUI/node_modules/.cache/portable-generator-browser');
fs.mkdirSync(output, { recursive: true });
const labelsFor = language => {
  const files = [`Resources/Locale/${language}/_DeepLagoon/generator.ftl`, language === 'ru-RU'
    ? 'Resources/Locale/ru-RU/_imported/power/components/generator.ftl'
    : 'Resources/Locale/en-US/power/components/generator.ftl'];
  const result = Object.fromEntries(files.flatMap((file, index) => [...fs.readFileSync(path.join(repo, file), 'utf8')
    .matchAll(/^(?:dl-generator-|portable-generator-ui-)([\w-]+)\s*=\s*(.+)$/gm)]
    .filter(match => index === 0 || ['start', 'stop', 'eject', 'clogged', 'unanchored', 'status-starting', 'status-stopped', 'status-running'].includes(match[1]))
    .map(match => [match[1], match[2].trim()])));
  return result;
};
const initial = {
  title: 'генератор S.U.P.E.R.P.A.C.M.A.N.', style: 'uranium', fuelLabel: 'ТОПЛИВНЫЙ УРАН',
  ready: true, anchored: true, starting: false, startProgress: 0, on: true, clogged: false,
  target: 30, min: 5, max: 60, efficiencyText: '100,0 %', etaText: '(~12 мин)', fuelFraction: .65,
  fuelText: '7', networkConnected: true, networkGood: false, networkText: '2,7 кВт / 7,2 кВт',
  canSwitch: true, voltage: 'HV', nextVoltage: 'MV', switchText: 'Переключить на MV',
};
(async () => {
  const server = http.createServer((request, response) => {
    const file = path.resolve(resources, '.' + new URL(request.url, 'http://localhost').pathname);
    if (!file.startsWith(resources + path.sep) || !fs.existsSync(file) || !fs.statSync(file).isFile()) {
      response.writeHead(404).end(); return;
    }
    response.setHeader('Content-Type', ({ '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css' })[path.extname(file)] || 'application/octet-stream');
    response.end(fs.readFileSync(file));
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  let browser;
  try {
    browser = await chromium.launch({ channel: process.env.BROWSER_CHANNEL || 'msedge', headless: true });
    const page = await browser.newPage({ viewport: { width: 460, height: 650 } });
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
      config: { interface: 'PortableGenerator', title: data.title, status: 2, window: { key: 'generator', fancy: false } },
      static_data: { labels }, data,
    } })), { data, labels: labelsFor(language) });
    const update = data => page.evaluate(data => window.update(JSON.stringify({ type: 'update', payload: { data } })), data);
    const lastAction = () => page.evaluate(() => {
      const last = window.__sent.filter(m => m.type.startsWith('act/')).at(-1);
      if (!last) return null;
      const { _page, _sequence, ...payload } = last.payload;
      return { type: last.type, payload };
    });
    const clickAction = async (name, action) => {
      await page.getByRole('button', { name, exact: true }).click();
      await page.waitForFunction(type => window.__sent.filter(m => m.type.startsWith('act/')).at(-1)?.type === type, 'act/' + action);
      assert.equal((await lastAction()).type, 'act/' + action);
    };
    await publish({ ...initial, ready: false });
    await page.locator('.PortableGenerator').waitFor();
    assert(await page.getByRole('slider').isDisabled());
    await publish(initial);
    await page.locator('.PortableGenerator__power').getByText('30.0', { exact: false }).waitFor();
    assert.equal(await page.locator('.PortableGenerator img, .PortableGenerator canvas').count(), 0);
    assert.equal(await page.locator('.PortableGenerator__nameplate strong').textContent(), initial.title);
    assert(await page.getByRole('button', { name: initial.switchText }).isDisabled());
    await clickAction('Стоп', 'stop');
    await update({ on: false });
    await clickAction(initial.switchText, 'switch');
    await clickAction('Старт', 'start');
    await clickAction('Извлечь', 'eject');
    await page.getByRole('slider').fill('42');
    await page.waitForFunction(() => window.__sent.filter(m => m.type.startsWith('act/')).at(-1)?.payload.value === 42);
    assert.deepEqual(await lastAction(), { type: 'act/power', payload: { value: 42 } });
    await page.getByRole('spinbutton').fill('0');
    await page.getByRole('spinbutton').blur();
    await page.waitForFunction(() => window.__sent.filter(m => m.type.startsWith('act/')).at(-1)?.payload.value === 0);
    await update({ target: 37 });
    await page.waitForFunction(() => document.querySelector('input[type=number]').value === '37');
    assert.equal(await page.getByRole('slider').inputValue(), '37', 'Server power snapshots replace completed edits');
    await update({ anchored: false });
    assert(await page.getByRole('button', { name: 'Старт', exact: true }).isDisabled());
    await update({ anchored: true, starting: true, startProgress: .4 });
    assert.equal(await page.locator('progress').getAttribute('value'), '0.4');
    assert.equal(await page.getByRole('button', { name: 'Старт', exact: true }).count(), 0);
    await update({ starting: false, clogged: true, networkConnected: false, networkText: 'Не подключён' });
    await page.getByRole('alert').getByText('В топливном баке обнаружено загрязнение!', { exact: false }).waitFor();
    await page.locator('.PortableGenerator__network').getByText('Не подключён').waitFor();
    await update({ canSwitch: false });
    assert.equal(await page.locator('.PortableGenerator__output').count(), 0);
    for (const [style, title, fuelLabel, target, max, canSwitch] of [
      ['plasma', 'генератор P.A.C.M.A.N.', 'ТОПЛИВНАЯ ПЛАЗМА', 15, 35, true],
      ['uranium', initial.title, initial.fuelLabel, 30, 60, true],
      ['welding', 'генератор J.R.P.A.C.M.A.N.', 'СВАРОЧНОЕ ТОПЛИВО', 6, 20, false],
      ['bananium', 'генератор D-K', 'ТОПЛИВНЫЙ БАНАНИУМ', 60, 120, true],
      ['bananium-jr', 'генератор D-K Jr.', 'ТОПЛИВНЫЙ БАНАНИУМ', 60, 100, true],
      ['pinch', 'термоядерный реактор SKR CR', 'ТОПЛИВНАЯ ПЛАЗМА', 105, 160, true],
      ['sterling', 'генератор Стирлинга ZQ-340', 'ТОПЛИВНАЯ ПЛАЗМА', 40, 50, true],
    ]) {
      await page.setViewportSize({ width: 460, height: 740 });
      await publish({ ...initial, style, title, fuelLabel, target, max, canSwitch });
      await page.locator(`.PortableGenerator--${style}`).waitFor();
      await page.screenshot({ path: path.join(output, `${style}.png`), fullPage: true });
    }
    for (const [width, height] of [[460, 650], [340, 450], [320, 640]]) {
      await page.setViewportSize({ width, height });
      await publish({ ...initial, title: '<img src=x onerror=alert(1)> Очень длинное название генератора с металлическим шильдиком' });
      assert.equal(await page.locator('.PortableGenerator img').count(), 0);
      const size = await page.evaluate(() => ({ width: innerWidth, scroll: document.documentElement.scrollWidth }));
      assert(size.scroll <= size.width + 1, `No overflow at ${width}: ${JSON.stringify(size)}`);
      await page.locator('.PortableGenerator__footer').scrollIntoViewIfNeeded();
      const footer = await page.locator('.PortableGenerator__footer').boundingBox();
      assert(footer.y >= 0 && footer.y + footer.height <= height + 1);
    }
    await publish({ ...initial, on: false }, 'en-US');
    await page.getByRole('button', { name: 'Start', exact: true }).waitFor();
    assert.deepEqual(errors, []);
    console.log(JSON.stringify({ passed: true, skins: 7, checks: 'bridge actions, power and zero, anchoring, starting, contamination, voltage lock, no-switch model, localization, safe nameplate, no sprite, 460/340/320px', screenshots: output }));
  } finally {
    if (browser) await browser.close();
    await new Promise(resolve => server.close(resolve));
  }
})().catch(e => { console.error(e); process.exitCode = 1; });
