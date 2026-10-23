// Production-bundle browser check. Run after building TGUI; requires Playwright and a browser.
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const assert = require('node:assert/strict');
const { randomUUID } = require('node:crypto');
const playwright = require(process.env.TATTOO_PLAYWRIGHT || 'playwright');
const root = path.resolve(__dirname, '../..');
const assets = path.join(root, 'Resources/Web/DeepLagoon');
const output = path.join(root, '.local/codex/apartment-templates/tattoo-qa');
fs.mkdirSync(output, { recursive: true });
const torsoPng = fs.readFileSync(path.join(root, 'Resources/Textures/Mobs/Species/Human/parts.rsi/torso_m.png')).toString('base64');
let masks;
let state = { tattoos: [], parts: [{ id: 'Chest', name: 'Грудь', color: '#D6AB91FF', covered: false,
  frames: [] }], maxTattoos: 8, maxStrokes: 64, maxPoints: 512, machine: false };
const messages = [];
const errors = []; let bakeChunks = [];
let exportedTattoo;
let testInterface = 'TattooStudio';
const server = http.createServer((request, response) => {
  if (request.url === '/') {
    response.setHeader('Content-Type', 'text/html');
    response.end(`<!doctype html><meta charset="utf-8"><link rel="stylesheet" href="/tgui.bundle.css">
      <div id="react-root"></div><script>
      window.__windowId__='test';window.__updateQueue__=[];window.update=m=>window.__updateQueue__.push(m);
      window.Byond={parseJson:v=>typeof v==='string'?JSON.parse(v):v,topic:v=>window.testTopic(v),winset:()=>{},
        winget:(_id,key)=>Promise.resolve(key==='size'?'920,720':'0,0'),command:()=>{},loadCss:()=>{},loadJs:()=>{}};
      </script><script src="/tgui.bundle.js"></script>`);
    return;
  }
  const file = path.resolve(assets, '.' + request.url);
  if (!file.startsWith(assets + path.sep) || !fs.existsSync(file)) { response.writeHead(404).end(); return; }
  response.setHeader('Content-Type', file.endsWith('.js') ? 'text/javascript' : 'text/css');
  fs.createReadStream(file).pipe(response);
});

(async () => {
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const browser = await playwright.chromium.launch({ headless: true,
    channel: process.env.TATTOO_BROWSER_CHANNEL || (process.platform === 'win32' ? 'msedge' : undefined) });
  const page = await browser.newPage({ viewport: { width: 920, height: 760 } });
  page.on('pageerror', error => errors.push(error.message));
  const publish = () => page.evaluate(({ data, interfaceName }) => window.update(JSON.stringify({ type: 'update', payload: {
    replaceData: true, config: { interface: interfaceName, title: 'Тату-машинка', status: 2, window: { key: 'test', fancy: false } }, data,
  } })), { data: state, interfaceName: testInterface });
  await page.exposeFunction('testTopic', async value => {
    if (!value.type?.startsWith('act/')) return;
    const action = value.type.slice(4), args = typeof value.payload === 'string' ? JSON.parse(value.payload) : value.payload;
    messages.push({ action, args });
    const tattooData = state.tattooEditor || state;
    const tattoo = tattooData.tattoos.find(t => t.id === args.id);
    if (action === 'tattoo-export' && tattoo) exportedTattoo = { normal: tattoo.normalBitmap, glow: tattoo.glowBitmap };
    else if (action === 'tattoo-import' && tattoo && exportedTattoo) {
      tattoo.normalBitmap = exportedTattoo.normal; tattoo.glowBitmap = exportedTattoo.glow; tattoo.strokes = [];
    }
    else if (action === 'tattoo-clear') tattooData.tattoos = [];
    else if (action === 'tattoo-add') tattooData.tattoos.push({ id: randomUUID().replaceAll('-', ''), part: args.part, name: 'Грудь', direction: args.direction,
      x: .5, y: .5, scale: 1, rotation: 0, opacity: 1, strokes: [], normalBitmap: "", glowBitmap: "", mirrored: false });
    else if (action === 'tattoo-bake-chunk' && tattoo) {
      if (args.index === 0) bakeChunks = [];
      assert.equal(args.index, bakeChunks.length); bakeChunks.push(args.text);
      if (bakeChunks.length !== args.count) return;
      const baked = JSON.parse(bakeChunks.join('')); assert.equal(baked.id, tattoo.id);
      assert.equal(typeof baked.recipe, 'string'); assert.equal(baked.normal, undefined);
      // Browser fixture emulates the server reply; server security/rasterization has separate C# tests.
      const channels = await page.evaluate(async ({ recipe, basis }) => {
        const { makeLayer, loadBitmap, paint, flatten, encodeBitmap } = window.tattooTools;
        const layers = [];
        for (const data of JSON.parse(recipe)) {
          const layer = makeLayer(); Object.assign(layer, { visible: data.visible, opacity: data.opacity });
          if (data.useBase) await Promise.all([loadBitmap(layer.normal, basis.normalBitmap), loadBitmap(layer.glow, basis.glowBitmap)]);
          for (const s of data.strokes) [layer.normal, layer.glow].forEach((c, i) => paint(c.getContext('2d'), s, !!i));
          layers.push(layer);
        }
        return flatten(layers).map(encodeBitmap);
      }, { recipe: baked.recipe, basis: tattoo });
      [tattoo.normalBitmap, tattoo.glowBitmap] = channels; tattoo.strokes = []; tattoo.artworkProof = randomUUID();
      for (const key of ['x', 'y', 'scale', 'rotation', 'opacity']) if (baked[key] !== undefined) tattoo[key] = baked[key];
    }
    else if (action === 'tattoo-stroke' && tattoo) tattoo.strokes.push({ color: args.color, width: args.width, glow: args.glow, erase: args.erase, points: args.points });
    else if (action === 'tattoo-transform' && tattoo) Object.assign(tattoo, args);
    else if (action === 'tattoo-mirror' && tattoo) {
      tattoo.x = 1 - tattoo.x; tattoo.rotation = -tattoo.rotation; tattoo.mirrored = !tattoo.mirrored;
      tattoo.strokes.forEach(stroke => { stroke.points = stroke.points.split(';').map(p => {
        const [x, y] = p.split(',').map(Number); return [1 - x, y].join(',');
      }).join(';'); });
    }
    else if (action === 'tattoo-undo' && tattoo) tattoo.strokes.pop();
    else if (action === 'tattoo-copy' && tattoo) state.tattoos.push({ ...JSON.parse(JSON.stringify(tattoo)), id: randomUUID().replaceAll('-', ''), direction: args.direction });
    else if (action === 'tattoo-remove' && tattoo) state.tattoos = state.tattoos.filter(t => t.id !== tattoo.id);
    await publish();
    await page.evaluate(mask => { window.tattooMask = mask; }, masks[0]);
  });
  try {
    await page.goto(`http://127.0.0.1:${server.address().port}/`);
    await page.addScriptTag({ content: fs.readFileSync(path.join(root, 'TGUI/packages/tgui/interfaces/tattooBitmapUtils.js'), 'utf8').replaceAll('export ', '') + '; window.tattooTools = { makeLayer, loadBitmap, paint, flatten, encodeBitmap };' });
    await page.waitForFunction(() => window.__tguiAppBooted__);
    masks = await page.evaluate(async png => {
      const img = new Image(); img.src = 'data:image/png;base64,' + png; await img.decode();
      return Array.from({ length: 4 }, (_, direction) => {
        const canvas = document.createElement('canvas'); canvas.width = canvas.height = 32;
        const columns = img.width / 32;
        canvas.getContext('2d').drawImage(img, direction % columns * 32, Math.floor(direction / columns) * 32, 32, 32, 0, 0, 32, 32);
        return canvas.toDataURL();
      });
    }, torsoPng);
    const previews = await page.evaluate(async frames => Promise.all(frames.map(async png => {
      const img = new Image(); img.src = png; await img.decode();
      const c = document.createElement('canvas'); c.width = c.height = 32; const ctx = c.getContext('2d'); ctx.drawImage(img, 0, 0);
      ctx.globalCompositeOperation = 'source-atop'; ctx.fillStyle = '#d6ab91'; ctx.fillRect(0, 0, 32, 32);
      // A visible existing marking in the drawing surface, independent of the clipping mask.
      ctx.fillStyle = '#00ccff'; ctx.fillRect(15, 15, 2, 2); return c.toDataURL();
    })), masks);
    state.parts[0].frames = masks.map((image, direction) => ({ image, direction, previewImage: previews[direction] }));
    await publish();
    await page.getByText('Не смотря на неограниченные возможности импорта татуировок, они должны соблюдать атмосферу и логику игры. За нарушение атмосферы вы будете наказаны', { exact: true }).waitFor();
    await page.getByText('Импорт сторонних PNG доступен с третьего тира доната. Собственные PNG, подписанные сервером для вашего аккаунта, можно загрузить без доната.', { exact: true }).waitFor();
    await page.getByRole('button', { name: 'Завершить рисунок', exact: true }).waitFor();
    assert.equal(state.tattoos.length, 1, 'Opening an empty editor creates its first editable drawing');
    await page.waitForFunction(() => document.querySelector('.TattooStudio__list button')?.getAttribute('aria-pressed') === 'true');
    const drawing = page.getByLabel('Рисование татуировки на выбранной части тела');
    const bounds = await drawing.boundingBox();
    await page.waitForFunction(() => {
      const canvas = document.querySelector('.TattooStudio__canvas'); const p = canvas.getContext('2d').getImageData(250, 250, 1, 1).data;
      return p[0] === 0 && p[1] === 204 && p[2] === 255;
    });
    async function stroke(x1, y1, x2, y2) {
      await page.mouse.move(bounds.x + bounds.width * x1, bounds.y + bounds.height * y1);
      await page.mouse.down();
      await page.mouse.move(bounds.x + bounds.width * x2, bounds.y + bounds.height * y2, { steps: 30 });
      await page.mouse.up();
    }
    await page.waitForFunction(() => document.body.textContent.includes('Завершить рисунок'));
    await stroke(.15, .48, .85, .48);
    assert.equal(state.tattoos[0].strokes.length, 0, 'Strokes stay local until finishing');
    await page.getByRole('button', { name: 'Добавить слой', exact: true }).click();
    await page.getByRole('combobox', { name: 'Слой рисунка', exact: true }).click();
    assert.equal(await page.getByRole('option').count(), 2); await page.keyboard.press('Escape');
    await page.getByLabel('Светящиеся чернила').check();
    await stroke(.45, .2, .55, .8);
    await page.getByRole('button', { name: 'Отменить', exact: true }).click();
    await page.getByRole('button', { name: 'Повторить', exact: true }).click();
    const countBefore = messages.length;
    // More than the former 64-stroke limit, with no growth of persistent history.
    for (let i = 0; i < 70; i++) {
      await page.mouse.move(bounds.x + bounds.width * (.4 + i % 10 * .01), bounds.y + bounds.height * (.4 + Math.floor(i / 10) * .01));
      await page.mouse.down(); await page.mouse.up();
    }
    assert.equal(messages.length, countBefore, 'Painting does not send individual strokes');
    await page.mouse.move(bounds.x + bounds.width * .5, bounds.y + bounds.height * .5);
    await page.mouse.wheel(0, -460);
    await page.waitForFunction(() => {
      const label = document.querySelector('.TattooStudio__viewportTools span');
      return Number(label?.textContent.match(/(\d+)%/)?.[1]) > 150;
    });
    assert.equal(await page.getByRole('button', { name: 'Завершить рисунок', exact: true }).isVisible(), true);
    await page.mouse.down({ button: 'middle' });
    await page.mouse.move(bounds.x + bounds.width * .58, bounds.y + bounds.height * .52, { steps: 5 });
    await page.mouse.up({ button: 'middle' });
    assert.equal(messages.length, countBefore, 'Zoom and pan never commit artwork');
    await page.getByRole('button', { name: 'Сбросить вид', exact: true }).click();
    await page.getByRole('button', { name: 'Переместить', exact: true }).click();
    await stroke(.5, .5, .56, .5);
    assert.equal(state.tattoos[0].x, .5, 'Unfinished placement remains local');
    await page.getByRole('button', { name: 'Кисть', exact: true }).click();
    await page.getByLabel('Видимость слоя').uncheck();
    await page.getByLabel('Видимость слоя').check();
    await page.getByRole('button', { name: 'Слой ниже', exact: true }).click();
    await page.getByRole('button', { name: 'Слой выше', exact: true }).click();
    await page.getByRole('button', { name: 'Завершить рисунок', exact: true }).click();
    await page.waitForFunction(() => document.querySelector('button') && [...document.querySelectorAll('button')].some(b => b.textContent === 'Редактировать рисунок'));
    assert.ok(state.tattoos[0].normalBitmap && state.tattoos[0].glowBitmap);
    assert.ok(state.tattoos[0].x > .55, 'Finishing atomically saves unfinished placement and artwork');
    await page.getByRole('button', { name: 'Сбросить размещение', exact: true }).click();
    assert.deepEqual(state.tattoos[0].strokes, []);
    assert.equal(state.tattoos[0].layers, undefined);
    assert.equal(await page.getByRole('button', { name: 'Отменить', exact: true }).isDisabled(), true);
    const normalBefore = state.tattoos[0].normalBitmap;
    await page.getByRole('button', { name: 'Сохранить PNG', exact: true }).click();
    await page.waitForTimeout(50);
    assert.deepEqual(exportedTattoo, { normal: normalBefore, glow: state.tattoos[0].glowBitmap });
    await page.getByRole('button', { name: 'Редактировать рисунок', exact: true }).click();
    await page.waitForFunction(() => document.body.textContent.includes('Завершить рисунок'));
    await page.getByRole('combobox', { name: 'Слой рисунка', exact: true }).click();
    assert.equal(await page.getByRole('option').count(), 1, 'Reopening starts from a flattened image'); await page.keyboard.press('Escape');
    assert.equal(await page.getByRole('button', { name: 'Отменить', exact: true }).isDisabled(), true);
    assert.equal(await page.getByRole('button', { name: 'Сохранить PNG', exact: true }).isDisabled(), true);
    assert.equal(await page.getByRole('button', { name: 'Загрузить PNG', exact: true }).isDisabled(), true);
    await page.getByRole('button', { name: 'Ластик', exact: true }).click();
    await stroke(.2, .48, .4, .48);
    await page.getByRole('button', { name: 'Завершить рисунок', exact: true }).click();
    await page.waitForFunction(() => [...document.querySelectorAll('button')].some(b => b.textContent === 'Редактировать рисунок'));
    assert.notEqual(state.tattoos[0].normalBitmap, normalBefore, 'Eraser changes baked pixels');
    await page.getByRole('button', { name: 'Загрузить PNG', exact: true }).click();
    await page.waitForTimeout(80);
    assert.equal(state.tattoos[0].normalBitmap, normalBefore, 'PNG import action restores the exported artwork');
    await page.getByRole('button', { name: 'Переместить', exact: true }).click();
    await stroke(.5, .5, .58, .52); await page.waitForTimeout(100);
    assert.ok(state.tattoos[0].x > .55);
    const originalX = state.tattoos[0].x, originalBitmap = state.tattoos[0].normalBitmap;
    await page.getByRole('button', { name: 'Отразить по горизонтали', exact: true }).click(); await page.waitForTimeout(80);
    await page.getByRole('button', { name: 'Отразить по горизонтали', exact: true }).click(); await page.waitForTimeout(80);
    assert.ok(Math.abs(state.tattoos[0].x - originalX) < .00001);
    assert.equal(state.tattoos[0].normalBitmap, originalBitmap);
    await page.screenshot({ path: path.join(output, 'studio.png'), fullPage: true });
    const clipped = await drawing.evaluate(async canvas => {
      const pixels = canvas.getContext('2d').getImageData(0, 0, 512, 512).data;
      const img = new Image(); img.src = window.tattooMask; await img.decode();
      const mask = document.createElement('canvas'); mask.width = mask.height = 512;
      const mc = mask.getContext('2d'); mc.imageSmoothingEnabled = false; mc.drawImage(img, 0, 0, 512, 512);
      const mp = mc.getImageData(0, 0, 512, 512).data;
      let leaks = 0; for (let i = 3; i < pixels.length; i += 4) if (!mp[i] && pixels[i]) leaks++; return leaks;
    });
    assert.equal(clipped, 0);
    await page.getByRole('button', { name: 'Копировать на следующий ракурс', exact: true }).click();
    await page.waitForFunction(() => document.querySelectorAll('.TattooStudio__list button').length === 2);
    assert.equal(state.tattoos[1].normalBitmap, originalBitmap);
    state.machine = true; state.parts[0].covered = true; await publish();
    await page.waitForFunction(() => document.querySelector('.TattooStudio__canvas--blocked') !== null);
    assert.equal(await page.getByRole('button', { name: /Завершить рисунок|Редактировать рисунок/, exact: true }).isDisabled(), true);
    state.machine = false; state.parts[0].covered = false; state.disabled = true; await publish();
    await page.waitForFunction(() => document.body.textContent.includes('Нанесение татуировок выключено в настройках.'));
    assert.equal(await page.getByRole('button', { name: 'Новая татуировка', exact: true }).isDisabled(), true);
    assert.equal(await page.getByRole('button', { name: 'Загрузить PNG', exact: true }).isDisabled(), true);
    assert.equal(await page.getByRole('button', { name: /Завершить рисунок|Редактировать рисунок/, exact: true }).isDisabled(), true);
    state.disabled = false; await publish();
    await page.waitForFunction(() => [...document.querySelectorAll('button')].some(b => /Завершить рисунок|Редактировать рисунок/.test(b.textContent) && !b.disabled));
    testInterface = 'CharacterEditor'; state = { available: true, mode: 'markings', tattooEditor: state };
    await publish();
    const panel = page.locator('details.CharacterEditor__tattoos');
    await panel.waitFor();
    assert.equal(await panel.evaluate(node => node.open), false, 'Tattoo section starts collapsed');
    assert.equal(await panel.getByRole('button', { name: 'Завершить рисунок', exact: true, includeHidden: true }).count(), 0,
      'A collapsed editor must not start a hidden drawing session');
    await panel.locator('summary').click();
    assert.equal(await panel.evaluate(node => node.open), true);
    await page.getByRole('button', { name: 'Очистить все татуировки', exact: true }).click();
    await page.waitForFunction(() => document.querySelector('details.CharacterEditor__tattoos summary')?.textContent === 'Татуировки (0)');
    assert.deepEqual(state.tattooEditor.tattoos, []);
    await page.getByRole('button', { name: 'Новая татуировка', exact: true }).click();
    await page.waitForFunction(() => document.querySelector('details.CharacterEditor__tattoos summary')?.textContent === 'Татуировки (1)');
    await page.waitForFunction(() => document.body.textContent.includes('Завершить рисунок'));
    await page.getByRole('button', { name: 'Очистить все татуировки', exact: true }).click();
    await page.waitForFunction(() => !document.body.textContent.includes('Завершить рисунок'));
    assert.equal(await page.getByRole('button', { name: 'Новая татуировка', exact: true }).isDisabled(), false, 'Clear also discards the active drawing session');
    state.tattooEditor.parts.push({ ...state.tattooEditor.parts[0], id: 'Head', name: 'Голова' });
    await publish();
    const region = page.getByRole('combobox', { name: 'Область татуировки', exact: true });
    await region.click();
    assert.equal(await page.locator('select[aria-label="Область татуировки"]').count(), 0);
    await page.getByRole('option', { name: 'Голова', exact: true }).click();
    assert.match(await region.innerText(), /Голова/);
    await page.getByRole('button', { name: 'Новая татуировка', exact: true }).click();
    await page.getByRole('button', { name: 'Завершить рисунок', exact: true }).waitFor();
    const head = state.tattooEditor.tattoos.at(-1);
    assert.equal(head.part, 'Head');
    const draw = await page.locator('.TattooStudio__canvas').boundingBox();
    await page.mouse.move(draw.x + draw.width / 2, draw.y + draw.height / 2);
    await page.mouse.down(); await page.mouse.move(draw.x + draw.width * .55, draw.y + draw.height * .55); await page.mouse.up();
    await region.click(); await page.keyboard.press('Home'); await page.keyboard.press('Enter');
    await page.getByRole('alertdialog', { name: 'Смена области татуировки' }).waitFor();
    await page.getByRole('button', { name: 'Остаться', exact: true }).click();
    assert.match(await region.innerText(), /Голова/);
    await region.click(); await page.getByRole('option', { name: 'Грудь', exact: true }).click();
    await page.getByRole('button', { name: 'Сохранить и перейти', exact: true }).click();
    await page.waitForFunction(() => document.querySelector('[role="combobox"][aria-label="Область татуировки"]').textContent.includes('Грудь'));
    assert.ok(head.normalBitmap);
    await region.click(); await page.keyboard.press('Escape');
    assert.equal(await page.getByRole('listbox', { name: 'Область татуировки' }).count(), 0);
    await page.screenshot({ path: path.join(output, 'body-part-dropdown.png'), fullPage: true });
    assert.deepEqual(errors, []);
    fs.writeFileSync(path.join(output, 'browser-results.json'), JSON.stringify({ passed: true, autoEditing: true, wheelZoom: true, panDuringEditing: true, unfinishedPlacement: true, temporaryLayers: true, painting70Strokes: true, reopenLayers: 1, noSavedHistory: true, maskLeaks: clipped, errors }, null, 2));
    console.log('PASS: auto editing, wheel zoom, pan, unfinished placement, temporary layers, 70 strokes, bake, reopen, erase, undo reset, glow, mirror, copy, clipping, covered skin');
  } catch (error) {
    console.error('Page errors:', errors);
    console.error('Rendered page:', await page.locator('body').innerText());
    await page.screenshot({ path: path.join(output, 'studio-failure.png'), fullPage: true });
    throw error;
  } finally { await browser.close(); server.close(); }
})().catch(error => { console.error(error); server.close(); process.exitCode = 1; });
