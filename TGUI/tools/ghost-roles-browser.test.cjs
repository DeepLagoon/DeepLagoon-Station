const assert = require('node:assert/strict');
const fs = require('node:fs');
const http = require('node:http');
const path = require('node:path');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const root = path.resolve(__dirname, '../../Resources/Web/DeepLagoon');

(async () => {
  const server = http.createServer((req, res) => {
    const file = path.resolve(root, '.' + new URL(req.url, 'http://localhost').pathname);
    if (!file.startsWith(root + path.sep) || !fs.existsSync(file)) { res.writeHead(404).end(); return; }
    res.setHeader('Content-Type', ({ '.html': 'text/html', '.css': 'text/css', '.js': 'application/javascript', '.woff2': 'font/woff2' })[path.extname(file)] || 'application/octet-stream');
    res.end(fs.readFileSync(file));
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  let browser;
  try {
    browser = await chromium.launch({ channel: process.env.BROWSER_CHANNEL || 'msedge', headless: true });
    const page = await browser.newPage({ viewport: { width: 540, height: 510 } });
    const errors = [];
    page.on('pageerror', error => errors.push(String(error)));
    await page.addInitScript(() => {
      window.__sent = [];
      const NativeImage = window.Image;
      window.Image = class extends NativeImage {
        set src(url) {
          if (!url.startsWith('res://deeplagoon/Web/DeepLagoon/bridge?')) { super.src = url; return; }
          const [type, payload] = url.split('?')[1].split('&');
          window.__sent.push({ type: decodeURIComponent(type), payload: JSON.parse(decodeURIComponent(payload)) });
          queueMicrotask(() => this.onload?.(new Event('load')));
        }
      };
    });
    await page.goto(`http://127.0.0.1:${server.address().port}/interface.html`);
    await page.waitForFunction(() => window.__tguiAppBooted__);
    const labels = { empty: 'Нет доступных ролей', follow: 'Следовать', available: 'Доступно', groups: 'типов', roles: 'ролей', locked: 'Доступ ограничен',
      select: 'Выберите роль в списке', instance: 'Персонаж', 'preview-spawner': 'Показан прототип', 'preview-unknown': 'Внешность определяется при появлении' };
    const role = (id, kind, request, allowed = true, preview = 'entity') => ({ id, kind, request, allowed, preview, reason: allowed ? '' : 'Требуется 2 часа игры' });
    const mice = { name: 'Мышь', description: 'Голодная и озорная мышь.', roles: [role('10', 0, 'Запросить'), role('11', 0, 'Запросить')] };
    const raffle = { name: 'Гигантский паук', description: 'Описание паука\nВторая строка.', roles: [role('20', 1, 'Участвовать в лотерее'),
      role('21', 2, 'Участвовать в лотерее (0:42, 3 игрока)'), role('22', 3, 'Покинуть лотерею (0:42, 3 игрока)')] };
    const locked = { name: 'TSFMC AI Core', description: '<script>window.bad = true</script>', roles: [role('30', 1, 'Участвовать в лотерее', false)] };
    const spawner = { name: 'Спавнер', description: 'Будущий персонаж', roles: [role('40', 0, 'Запросить', true, 'prototype')] };
    let groups = [mice, raffle, locked, spawner];
    const push = async () => page.evaluate(({ groups, labels }) => window.update(JSON.stringify({ type: 'update', payload: {
      replaceData: true, config: { interface: 'GhostRoles', status: 2, window: { key: 'ghost-roles', fancy: false } }, data: { groups, labels },
    } })), { groups, labels });
    const sent = type => page.evaluate(type => window.__sent.filter(message => message.type === `act/${type}`), type);
    const assertPreviewAlignment = async () => {
      await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
      const geometry = await page.evaluate(() => {
        const root = document.querySelector('.GhostRoles').getBoundingClientRect();
        const payload = window.__sent.filter(message => message.type === 'act/preview-layout').at(-1).payload;
        const slots = payload.layout ? payload.layout.split(';').map(entry => entry.split(',').map(Number)) : [];
        const previews = [...document.querySelectorAll('[data-role-preview]')].map(node => {
          const box = node.getBoundingClientRect();
          return [Number(node.dataset.rolePreview), box.left, box.top, box.width, box.height];
        });
        return { width: innerWidth, height: innerHeight, root: [root.left, root.top, root.width, root.height], payload, slots, previews };
      });
      assert.deepEqual(geometry.root, [0, 0, geometry.width, geometry.height], 'Role menu must fill the actual browser viewport, even with a short list');
      assert.equal(geometry.payload.width, geometry.width);
      assert.equal(geometry.payload.height, geometry.height, 'Native scaling must not use intrinsic content height');
      for (const slot of geometry.slots) {
        assert.ok(geometry.previews.some(box => box[0] === slot[0] && box.every((value, index) => Math.abs(value - slot[index]) <= 0.11)),
          `Native thumbnail must coincide with its DOM frame: ${JSON.stringify({ slot, geometry })}`);
      }
      return geometry;
    };
    await push();
    await page.locator('.GhostRoles__instance').first().waitFor();
    await page.waitForFunction(() => window.__sent.some(message => message.type === 'act/preview-layout'));
    const layout = (await sent('preview-layout')).at(-1).payload;
    assert.ok(layout.layout.startsWith('10,'), 'Selected portrait takes priority over catalogue thumbnails');
    assert.equal(await page.locator('.GhostRoles__instance').count(), 2);
    await page.locator('.GhostRoles__instance').nth(1).getByRole('button', { name: 'Запросить', exact: true }).click();
    assert.equal((await sent('request')).at(-1).payload.id, '11');
    await page.getByRole('button', { name: /Гигантский паук.*Доступно/ }).click();
    for (let index = 0; index < 3; index++) {
      await page.locator('.GhostRoles__request').nth(index).click();
      assert.equal((await sent('request')).at(-1).payload.id, String(20 + index));
    }
    assert.ok(await page.locator('.GhostRoles__request--joined').count());
    raffle.roles[1] = role('21', 2, 'Участвовать в лотерее (0:41, 4 игрока)');
    groups = [locked, mice, raffle, spawner];
    await push();
    await page.getByRole('button', { name: 'Участвовать в лотерее (0:41, 4 игрока)', exact: true }).waitFor();
    assert.equal(await page.locator('.GhostRoles__details h3').textContent(), 'Гигантский паук', 'Selection survives reorder and countdown updates');
    raffle.roles.shift();
    await push();
    assert.equal(await page.locator('.GhostRoles__details h3').textContent(), 'Гигантский паук', 'Selection survives removal of first group member');
    await page.getByRole('button', { name: /TSFMC AI Core.*Доступно/ }).click();
    assert.equal(await page.locator('.GhostRoles__request').isDisabled(), true);
    assert.equal(await page.locator('.GhostRoles__reason').textContent(), ' Требуется 2 часа игры');
    await page.getByRole('button', { name: 'Следовать', exact: true }).click();
    assert.equal((await sent('follow')).at(-1).payload.id, '30');
    assert.equal(await page.evaluate(() => window.bad), undefined, 'Description is text, never executable markup');
    await page.getByRole('button', { name: /Спавнер.*Доступно/ }).click();
    assert.equal(await page.locator('.GhostRoles__preview-note').textContent(), 'Показан прототип');
    for (const size of [{ width: 540, height: 510 }, { width: 420, height: 360 }, { width: 320, height: 380 }]) {
      await page.setViewportSize(size);
      await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
      const bounds = await page.locator('.GhostRoles').evaluate(element => ({ width: element.clientWidth, scroll: element.scrollWidth, height: element.clientHeight, content: element.scrollHeight }));
      assert.ok(bounds.scroll <= bounds.width && bounds.content <= bounds.height, JSON.stringify({ size, bounds }));
      const last = (await sent('preview-layout')).at(-1).payload;
      assert.ok(last.layout.split(';').length <= 32);
      await assertPreviewAlignment();
    }
    // Reproduce the reported five-role layout: an intrinsic-height root used to
    // end above the native host's bottom, stretching every sprite downwards.
    groups = ['Emotional Support Pet', 'Blood Cult Cat', 'Cappy', 'Clarpy', 'Clippy'].map((name, index) => ({
      name, description: index === 0 ? "You're an emotional support pet! Loyalty to your owner, make sure to cheer them up!" : 'A station pet.',
      roles: [role(String(50 + index), 0, 'Запросить')],
    }));
    await push();
    for (const size of [{ width: 540, height: 510 }, { width: 540, height: 720 }, { width: 420, height: 520 }]) {
      await page.setViewportSize(size);
      const geometry = await assertPreviewAlignment();
      if (size.width > 440)
        assert.equal(geometry.slots.length, 7, 'Five list thumbnails, one portrait and one concrete character thumbnail');
      else
        assert.ok(geometry.slots.length >= 3, 'Narrow layouts report the portrait, instance and fully visible list thumbnails');
    }
    await page.setViewportSize({ width: 540, height: 510 });
    await assertPreviewAlignment();
    if (process.env.GHOST_ROLES_SCREENSHOT) await page.screenshot({ path: process.env.GHOST_ROLES_SCREENSHOT });
    // Scrolled-away previews must disappear instead of painting over descriptions/actions.
    groups = Array.from({ length: 30 }, (_, index) => ({ name: `Роль ${index}`, description: 'Описание.', roles: [role(String(100 + index), 0, 'Запросить')] }));
    await push();
    await assertPreviewAlignment();
    await page.locator('.GhostRoles__list').evaluate(element => { element.scrollTop = element.scrollHeight; });
    const scrolled = await assertPreviewAlignment();
    assert.ok(scrolled.slots.some(slot => slot[0] === 129), 'Last visible list role has a native slot after scrolling');
    assert.equal(scrolled.slots.filter(slot => slot[0] === 100).length, 2, 'Scrolled-out first list role leaves only its selected portrait and instance');
    groups = [];
    await push();
    await page.getByText(labels.empty, { exact: true }).waitFor();
    await page.waitForFunction(() => window.__sent.filter(m => m.type === 'act/preview-layout').at(-1).payload.layout === '');
    assert.deepEqual(errors, []);
    console.log('PASS: role actions/states, viewport-height regression, five-role screenshot layout, exact native/DOM thumbnail bounds, resizing, scrolling, bounded slots and empty cleanup.');
  } finally {
    await browser?.close();
    await new Promise(resolve => server.close(resolve));
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
