const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const {chromium} = require('C:/Users/iamasay/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright');

(async () => {
  const folder = path.resolve(process.argv[2]);
  const live = process.argv.includes('--live');
  const origin = 'https://wiki.deep-lagoon-ss14.ru';
  const browser = await chromium.launch({channel: 'msedge', headless: true});
  const pages = JSON.parse(fs.readFileSync(path.join(folder, 'pages.json'), 'utf8'));
  const before = fs.readdirSync(path.join(folder, 'before')).map(file => JSON.parse(fs.readFileSync(path.join(folder, 'before', file), 'utf8')));
  const results = [], errors = [], failures = [];
  try {
    const page = await browser.newPage();
    page.on('pageerror', error => errors.push(error.message));
    page.on('response', response => {
      if (response.url().includes('/guidebook/in_game/assets/') && response.status() >= 400) failures.push({url: response.url(), status: response.status()});
    });
    if (!live) await page.route(origin + '/**', async route => {
      const url = new URL(route.request().url());
      const asset = /\/assets\/(dl-[^/]+)$/.exec(url.pathname);
      if (asset) return route.fulfill({path: path.join(folder, 'assets', asset[1])});
      const name = decodeURIComponent(url.pathname.replace(/^\/ru\//, ''));
      const planned = pages.find(item => item.path === name);
      assert.ok(planned, 'Unknown preview route: ' + name);
      return route.fulfill({contentType: 'text/html; charset=utf-8', body: '<!doctype html><html lang="ru"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><style>body{margin:0}' + planned.scriptCss + '</style>' + planned.content + '</html>'});
    });
    const cases = [
      ['home', 'home'], ['guides', 'guidebook/in_game/guides'],
      ['controls', 'guidebook/in_game/new-player/controls/controls'],
      ['feroxi', 'guidebook/in_game/mobs/feroxi'], ['harpy', 'guidebook/in_game/mobs/dv/harpy'],
      ['medicine', 'guidebook/in_game/medical/medicine'],
      ['botany', 'guidebook/in_game/service/botany'],
      ['ballistics', 'guidebook/in_game/imported/mono/specifications/ballistics'],
      ['energy', 'guidebook/in_game/imported/mono/specifications/energy'],
      ['missiles', 'guidebook/in_game/imported/mono/specifications/missiles'],
      ['automation', 'guidebook/in_game/imported/goobstation/engineering/automation'],
      ['medical-recipes', 'guidebook/in_game/imported/nf/medical/medical-recipes'],
      ['arc-furnace', 'guidebook/in_game/imported/mono/economy/arc-furnace'],
      ['precision-assembler', 'guidebook/in_game/imported/mono/economy/precision-assembler'],
      ['book', 'guidebook/in_game/books/bartenders-manual'],
    ];
    for (const [name, article] of cases.filter(([name]) => !process.argv.includes('--pilot') || ['home', 'guides', 'controls', 'feroxi', 'harpy'].includes(name))) {
      for (const width of [1440, 390]) {
        await page.setViewportSize({width, height: 1000});
        for (let attempt = 0; ; attempt++) {
          try { await page.goto(origin + '/ru/' + article, {waitUntil: 'domcontentloaded', timeout: 60000}); break; }
          catch (error) { if (attempt === 2) throw error; }
        }
        await page.locator('.dlwiki').first().waitFor();
        await page.locator('.dlwiki img').evaluateAll(images => Promise.all(images.map(image => image.complete ? null : new Promise(resolve => { image.addEventListener('load', resolve, {once:true}); image.addEventListener('error', resolve, {once:true}); }))));
        const facts = await page.evaluate(() => {
          const roots = [...document.querySelectorAll('.dlwiki')];
          const root = roots[0];
          return {width: innerWidth, scrollWidth: document.documentElement.scrollWidth,
            roots: roots.length, nestedRoots: root.querySelectorAll('.dlwiki').length,
            mastheads: root.querySelectorAll('.masthead').length,
            missingImages: [...root.querySelectorAll('img')].filter(img => !img.complete || !img.naturalWidth).map(img => img.src),
            h1: [...root.querySelectorAll('h1')].map(x => x.textContent),
            figurines: [...root.querySelectorAll('.dl-entity strong')].filter(x => /фигурк/i.test(x.textContent)).length,
            cards: root.querySelectorAll('.dl-entity').length};
        });
        assert.ok(facts.scrollWidth <= width, name + ': horizontal overflow ' + JSON.stringify(facts));
        assert.equal(facts.nestedRoots, 0, name + ': nested wiki wrappers');
        assert.equal(facts.mastheads, 1, name + ': repeated site header');
        assert.deepEqual(facts.missingImages, [], name + ': missing images');
        assert.equal(facts.figurines, 0, name + ': useless figurines');
        if (name === 'home') {
          const guide = page.locator('.quickstrip a').filter({hasText: 'Практические гайды'});
          assert.equal(await guide.getAttribute('href'), '/ru/guidebook/in_game/guides');
          await guide.click();
          await page.locator('#guides-medical').waitFor();
          assert.ok(page.url().endsWith('/ru/guidebook/in_game/guides'));
          await page.goBack({waitUntil: 'domcontentloaded'});
        }
        if (name === 'guides') {
          assert.equal(await page.locator('.dl-guide-section').count(), 15);
          await page.locator('.dl-guide-nav a[href="#guides-medical"]').click();
          assert.ok(await page.locator('#guides-medical').isVisible());
          await page.evaluate(() => scrollTo(0, 0));
        }
        if (name === 'controls') {
          assert.equal(await page.locator('.dlwiki kbd').count() >= 34, true);
          assert.equal(await page.locator('.dlwiki .prose code').count(), 0);
          const old = before.find(p => p.path === article).content;
          const planned = pages.find(p => p.path === article).content;
          const movement = text => text.match(/<h2\b[^>]*>Движение<\/h2>[\s\S]*?(?=<h2\b)/)[0];
          assert.equal(movement(planned), movement(old));
        }
        if (['feroxi', 'harpy'].includes(name)) {
          assert.equal(await page.locator('.dl-species-portrait').count(), 1);
          assert.equal(await page.locator('.dlwiki .dl-entity').count(), 0);
          assert.ok((await page.locator('.dl-species-portrait .dl-sprite>span').count()) > 3);
          assert.equal(await page.locator('.dlwiki').evaluate(root => /Urist McHands|A dummy|Feroxi Dummy/.test(root.textContent)), false);
        }
        if (['ballistics', 'energy', 'missiles'].includes(name)) {
          assert.equal(await page.locator('.dl-weapon-reference tbody tr').count(), {ballistics:11, energy:7, missiles:4}[name]);
          assert.equal(await page.locator('.dlwiki pre').count(), 0);
        }
        if (['medical-recipes', 'arc-furnace', 'precision-assembler'].includes(name)) {
          assert.equal(await page.locator('tr[data-lathe-recipe-id]').count(), {'medical-recipes':20, 'arc-furnace':12, 'precision-assembler':27}[name]);
        }
        if (name === 'book') {
          await page.locator('.dl-reagent summary').first().click();
          assert.equal(await page.locator('.dl-reagent').first().getAttribute('open'), '');
          await page.evaluate(() => scrollTo(0, 0));
        }
        await page.screenshot({path: path.join(folder, (live ? 'live-' : 'preview-') + name + '-' + width + '.png')});
        results.push({name, article, ...facts});
      }
    }
    const report = {mode: live ? 'live' : 'preview', results, errors, failures};
    if (live && process.argv.includes('--public')) {
      const catalogue = new Map([...before, ...pages].map(item => [item.path, item]));
      const paths = [...catalogue.values()].filter(item => item.isPublished && !item.isPrivate).map(item => ({path:item.path, locale:item.locale || 'ru'}));
      report.publicPages = await page.evaluate(async paths => {
        let cursor = 0;
        const results = [];
        await Promise.all([0, 1, 2, 3].map(async () => {
          while (cursor < paths.length) {
            const {path, locale} = paths[cursor++];
            let result;
            for (let attempt = 0; attempt < 3; attempt++) {
              try {
                const response = await fetch('/' + encodeURIComponent(locale) + '/' + path.split('/').map(encodeURIComponent).join('/'), {method: 'HEAD', cache: 'no-store', signal: AbortSignal.timeout(30000)});
                result = {path, locale, status: response.status};
                if (response.status === 200) break;
              } catch (error) { result = {path, error: error.message}; }
            }
            results.push(result);
          }
        }));
        return results;
      }, paths);
      report.publicFailures = report.publicPages.filter(item => item.status !== 200);
    }
    fs.writeFileSync(path.join(folder, live ? 'editorial-browser-live.json' : 'editorial-browser-preview.json'), JSON.stringify(report, null, 2));
    assert.deepEqual(errors, [], 'Browser errors');
    assert.deepEqual(failures, [], 'Asset errors');
    assert.deepEqual(report.publicFailures || [], [], 'Public article errors');
    console.log(JSON.stringify({mode: report.mode, viewportChecks: results.length, publicPages: report.publicPages?.length, errors, failures, publicFailures: report.publicFailures}, null, 2));
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exit(1); });
