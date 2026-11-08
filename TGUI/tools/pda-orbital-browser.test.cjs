const assert = require('node:assert/strict');
const fs = require('node:fs');
const http = require('node:http');
const path = require('node:path');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const repo = path.resolve(__dirname, '../..');
const resources = path.join(repo, 'Resources/Web/DeepLagoon');
const output = path.join(repo, '.local/codex/pda-orbital/browser');
fs.mkdirSync(output, { recursive: true });
const labels = language => {
  const locale = path.join(repo, 'Resources/Locale', language, '_DeepLagoon');
  const labels = Object.fromEntries([...fs.readFileSync(path.join(locale, 'pda.ftl'), 'utf8')
    .matchAll(/^dl-pda-([\w-]+)\s*=\s*(.+)$/gm)].map(m => [m[1], m[2].trim()]));
  labels.messenger = fs.readFileSync(path.join(locale, 'chat/pda-messenger.ftl'), 'utf8')
    .match(/^dl-messenger-title\s*=\s*(.+)$/m)[1].trim();
  return labels;
};
const messengerLabels = language => Object.fromEntries([...fs.readFileSync(
  path.join(repo, 'Resources/Locale', language, '_DeepLagoon/chat/pda-messenger.ftl'), 'utf8')
  .matchAll(/^dl-messenger-([\w-]+)\s*=\s*(.+)$/gm)].map(m => [m[1], m[2].trim()]));
const programs = [
  { id: '42', name: 'Заметки', kind: 'notekeeper-program-name', installed: true, canInstall: false, canUninstall: true },
  { id: '43', name: 'Манифест экипажа', kind: 'crew-manifest-program-name', installed: true, canInstall: false, canUninstall: false },
  { id: '44', name: 'Новости станции', kind: 'news-read-program-name', installed: false, canInstall: true, canUninstall: false },
  { id: '45', name: 'АстроНав', kind: 'astro-nav-program-name', installed: true, canInstall: false, canUninstall: true },
  { id: '46', name: 'МедТек', kind: 'med-tek-program-name', installed: true, canInstall: false, canUninstall: false },
];
const initial = { ready: true, view: 0, time: '01:23:45', owner: 'Владелец устройства', idOwner: 'Мария Орлова',
  job: 'Инженер', company: 'Nanotrasen', companyColor: '#91b6b4', station: 'Станция Тестовая', alert: 'Зелёный',
  alertColor: '#67ab70', instructions: 'Угроза отсутствует. Соблюдайте стандартные процедуры.', war: '',
  balance: '1 234', ship: 'Исследователь', date: '31/10/2566', address: 'ABCD', programTitle: '',
  hasId: true, hasPen: true, hasPai: true, flashlight: false, canPlayMusic: true, hasUplink: false, programs };
(async () => {
  const server = http.createServer((request, response) => {
    const file = path.resolve(resources, '.' + new URL(request.url, 'http://localhost').pathname);
    if (!file.startsWith(resources + path.sep) || !fs.existsSync(file) || !fs.statSync(file).isFile()) { response.writeHead(404).end(); return; }
    response.setHeader('Content-Type', ({ '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css' })[path.extname(file)] || 'application/octet-stream');
    response.end(fs.readFileSync(file));
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  let browser;
  try {
    browser = await chromium.launch({ channel: process.env.BROWSER_CHANNEL || 'msedge', headless: true });
    const page = await browser.newPage({ viewport: { width: 760, height: 680 } });
    const errors = []; page.on('pageerror', error => errors.push(String(error)));
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
      config: { interface: 'Pda', title: 'КПК', status: 2, window: { key: 'pda', fancy: false } }, static_data: { labels }, data,
    } })), { data, labels: labels(language) });
    const update = data => page.evaluate(data => window.update(JSON.stringify({ type: 'update', payload: { data } })), data);
    const action = async (locator, name, fields = {}) => {
      await page.evaluate(() => { window.__sent = []; });
      await locator.click();
      await page.waitForFunction(name => window.__sent.some(m => m.type === 'act/' + name), name);
      const message = await page.evaluate(name => window.__sent.find(m => m.type === 'act/' + name), name);
      for (const [key, value] of Object.entries(fields)) assert.equal(message.payload[key], value);
    };
    await publish({ ...initial, ready: false });
    await page.locator('.Pda').waitFor();
    assert(await page.getByRole('button', { name: 'Фонарик', exact: true }).isDisabled());
    await publish(initial);
    await page.getByText('Мария Орлова', { exact: true }).waitFor();
    assert.equal(await page.locator('.Pda__appLaunch').count(), 4, 'Home shows the actual first four cartridges');
    await action(page.getByRole('button', { name: 'Сообщения', exact: true }), 'messenger');
    assert(!/DeepLagoon\s*\/\s*Локальная сеть|86%|Алексей Волков/i.test(await page.locator('.Pda').innerText()));
    await action(page.locator('.Pda__appLaunch').first(), 'program', { id: '42' });
    for (const [name, command] of [['Фонарик', 'flashlight'], ['Вставить или извлечь ID-карту', 'id'], ['Вставить или извлечь ручку', 'pen'], ['Вставить или извлечь пИИ', 'pai']])
      await action(page.getByRole('button', { name, exact: true }), command);
    await update({ flashlight: true });
    assert.equal(await page.getByRole('button', { name: 'Фонарик', exact: true }).getAttribute('aria-pressed'), 'true');
    await action(page.locator('.Pda__dock').getByRole('button', { name: 'Приложения', exact: true }), 'view', { view: 1 });
    await update({ view: 1 });
    assert.equal(await page.locator('.Pda__appLaunch').count(), programs.length + 1);
    const messenger = page.locator('.Pda__app').filter({ hasText: 'Сообщения' });
    await action(messenger.locator('.Pda__appLaunch'), 'messenger');
    assert.equal(await messenger.locator('.Pda__install').count(), 0);
    await action(page.locator('.Pda__app').filter({ hasText: 'Новости станции' }).getByRole('button', { name: 'Установить', exact: true }), 'install', { id: '44' });
    await action(page.locator('.Pda__app').filter({ hasText: 'Заметки' }).getByRole('button', { name: 'Удалить', exact: true }), 'uninstall', { id: '42' });
    assert.equal(await page.locator('.Pda__app').filter({ hasText: 'Манифест экипажа' }).locator('.Pda__install').count(), 0);
    await update({ view: 2 });
    assert.equal(await page.getByRole('button', { name: 'Открыть аплинк', exact: true }).count(), 0);
    await action(page.getByRole('button', { name: 'Мелодия звонка', exact: true }), 'ringtone');
    await action(page.getByRole('button', { name: 'Музыка', exact: true }), 'music');
    await update({ hasUplink: true });
    await action(page.getByRole('button', { name: 'Открыть аплинк', exact: true }), 'uplink');
    await action(page.getByRole('button', { name: 'Заблокировать аплинк', exact: true }), 'lockUplink');
    await update({ hasUplink: false, canPlayMusic: false, hasId: false, hasPen: false, hasPai: false });
    assert.equal(await page.getByRole('button', { name: 'Музыка', exact: true }).count(), 0);
    for (const [name, command] of [['Вставить или извлечь ID-карту', 'id'], ['Вставить или извлечь ручку', 'pen'], ['Вставить или извлечь пИИ', 'pai']]) {
      const slot = page.getByRole('button', { name, exact: true });
      assert(await slot.isEnabled());
      assert.equal(await slot.getAttribute('aria-pressed'), 'false');
      await action(slot, command);
    }
    for (const [width, height] of [[760, 680], [600, 500], [520, 440]]) {
      await page.setViewportSize({ width, height });
      await publish({ ...initial, idOwner: 'Мария Орлова-ОченьДлинноеИмяДляПроверкиПереноса' });
      await page.screenshot({ path: path.join(output, `home-${width}.png`) });
      const size = await page.evaluate(() => ({ width: innerWidth, scroll: document.documentElement.scrollWidth, height: innerHeight, scrollHeight: document.documentElement.scrollHeight }));
      assert(size.scroll <= width + 1 && size.scrollHeight <= height + 1, JSON.stringify(size));
      for (const selector of ['.Pda__dock', '.Pda__hardware']) {
        const rect = await page.locator(selector).boundingBox();
        assert(rect.y >= 0 && rect.y + rect.height <= height + 1, `${selector} stays visible at ${width}x${height}`);
      }
      await page.evaluate(() => { window.__sent = []; });
      await publish({ ...initial, view: 3, programTitle: 'Заметки' });
      await page.waitForFunction(() => window.__sent.some(m => m.type === 'act/viewport'));
      const bounds = await page.evaluate(() => window.__sent.filter(m => m.type === 'act/viewport').at(-1).payload);
      const rect = await page.locator('.Pda__programArea').boundingBox();
      assert(bounds.x >= 0 && bounds.y >= 0 && bounds.w > 0 && bounds.h > 0 && bounds.x + bounds.w <= 1.001 && bounds.y + bounds.h <= 1.001);
      assert(Math.abs(rect.x - bounds.x * width) < 1 && Math.abs(rect.height - bounds.h * height) < 1);
      const dock = await page.locator('.Pda__dock').boundingBox();
      assert(rect.y + rect.height < dock.y, 'Native cartridge aperture cannot cover navigation');
      await action(page.getByRole('button', { name: 'Закрыть приложение', exact: true }), 'closeProgram');
    }
    await publish({ ...initial, programs: [], hasId: false });
    await page.getByText('Нет доступных приложений', { exact: true }).waitFor();
    await page.getByText('Нет ID-карты', { exact: true }).waitFor();
    await update({ view: 1 });
    assert.equal(await page.locator('.Pda__appLaunch').count(), 1, 'Messages stays available without cartridges');
    await action(messenger.locator('.Pda__appLaunch'), 'messenger');
    const addressBook = { labels: messengerLabels('ru-RU'), ready: true, number: 123456789, peer: 0,
      contacts: [], messages: [] };
    await update({ view: 4, messenger: addressBook });
    const chat = page.locator('.Pda__device .PdaMessenger');
    await chat.getByText('Сохранённых контактов пока нет.').waitFor();
    assert.match(await chat.innerText(), /«Осмотреть» на чужом КПК.*«Номер КПК»/);
    assert.equal(await chat.locator('.PdaMessenger__contacts button').count(), 0);
    await action(page.getByRole('button', { name: 'Ваш номер КПК 123456789' }), 'messenger-copy-number');
    await chat.getByRole('textbox', { name: 'Номер КПК (9 цифр)' }).fill('234-567-890');
    await action(chat.getByRole('button', { name: 'Добавить контакт' }), 'messenger-add', { number: '234-567-890' });
    const peer = { id: 234567890, saved: false, unread: true, available: true };
    await update({ messenger: { ...addressBook, contacts: [peer], peer: peer.id,
      messages: [{ id: 1, text: 'На связи?', outgoing: false, hasImage: false }] } });
    await chat.getByText('Входящие с новых номеров').waitFor();
    assert.equal(await chat.getByRole('textbox', { name: 'Написать сообщение' }).count(), 0);
    await action(chat.getByRole('button', { name: 'Добавить контакт' }).last(), 'messenger-add', { number: '234567890' });
    await update({ messenger: { ...addressBook, contactRevision: 1, contacts: [{ ...peer, saved: true }], peer: peer.id } });
    await chat.getByRole('textbox', { name: 'Написать сообщение' }).fill('На связи.');
    await action(chat.getByRole('button', { name: 'Отправить' }), 'messenger-send', { text: 'На связи.' });
    for (const [width, height] of [[760, 680], [520, 440]]) {
      await page.setViewportSize({ width, height });
      const shell = await page.locator('.Pda__device').boundingBox();
      const dock = await page.locator('.Pda__dock').boundingBox();
      assert(shell.height <= height && dock.y + dock.height <= height, 'Messenger uses the PDA shell and dock');
      const composer = await chat.getByRole('textbox', { name: 'Написать сообщение' }).boundingBox();
      assert(composer.y + composer.height <= dock.y, 'Composer fits above the PDA dock at minimum size');
      await page.screenshot({ path: path.join(output, `messenger-${width}.png`) });
    }
    await publish({ ...initial, idOwner: '<img src=x onerror=alert(1)>', company: '<script>test</script>' });
    assert.equal(await page.locator('.Pda img, .Pda script').count(), 0);
    await page.setViewportSize({ width: 760, height: 680 });
    await publish(initial);
    await page.screenshot({ path: path.join(output, 'orbital.png') });
    await publish(initial, 'en-US');
    await page.getByRole('heading', { name: 'Shift overview', exact: true }).waitFor();
    assert.deepEqual(errors, []);
    console.log(JSON.stringify({ passed: true, checks: 'device and cartridge actions, native aperture, contact entry and chat inside the PDA shell, empty states, localization, safe text, 760/600/520px', screenshots: output }));
  } finally {
    if (browser) await browser.close();
    await new Promise(resolve => server.close(resolve));
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
