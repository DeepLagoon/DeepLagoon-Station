const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const {chromium} = require('C:/Users/iamasay/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright');

(async () => {
  const folder = path.resolve(process.argv[2]);
  const live = process.argv.includes('--live');
  const origin = 'https://wiki.deep-lagoon-ss14.ru';
  const report = JSON.parse(fs.readFileSync(path.join(folder, 'book-links-plan.json')));
  const planned = JSON.parse(fs.readFileSync(path.join(folder, 'pages.json')));
  const changed = new Set(['BookSecurity', 'BookHowToRockAndStone', 'BookTSFSOP', 'BookUSSPLaw']);
  const browser = await chromium.launch({channel:'msedge', headless:true});
  const results = [];
  try {
    const page = await browser.newPage();
    if (!live) await page.route(origin + '/**', route => {
      const url = new URL(route.request().url());
      const asset = /\/assets\/(dl-[^/]+)$/.exec(url.pathname);
      if (asset) return route.fulfill({path:path.join(folder, 'assets', asset[1])});
      const article = planned.find(item => '/ru/' + item.path === url.pathname);
      assert.ok(article, url.pathname);
      return route.fulfill({contentType:'text/html; charset=utf-8', body:'<!doctype html><meta name="viewport" content="width=device-width,initial-scale=1"><style>body{margin:0}' + article.scriptCss + '</style>' + article.content});
    });
    for (const book of report.rows) {
      for (const width of changed.has(book.id) ? [1440,390] : [1440]) {
        await page.setViewportSize({width,height:1000});
        let response;
        for (let attempt = 0; ; attempt++) {
          try { response = await page.goto(origin + book.url, {waitUntil:'domcontentloaded',timeout:60000}); break; }
          catch (error) { if (attempt === 2) throw error; }
        }
        assert.equal(response.status(), 200, book.id);
        await page.locator('.dlwiki .book').waitFor();
        const facts = await page.locator('.dlwiki .book').evaluate(root => ({
          text:root.innerText.replaceAll('#','').replace(/\s+/g,' ').trim(),
          chapters:root.querySelectorAll('.dl-chapter').length,
          nested:root.querySelectorAll('.dlwiki').length,
          width:document.documentElement.scrollWidth,
        }));
        if (book.status !== 'deferred') {
          assert.equal(facts.chapters, book.chapters.length, book.id + ': chapter count');
          for (const chapter of book.chapters) assert.ok(facts.text.includes(chapter.sample), book.id + ': wrong chapter ' + chapter.guide);
        }
        assert.equal(facts.nested, 0, book.id + ': nested layout');
        assert.ok(facts.width <= width, book.id + ': overflow ' + facts.width);
        if (changed.has(book.id)) await page.screenshot({path:path.join(folder, `${live?'live':'preview'}-${book.id}-${width}.png`)});
        results.push({id:book.id,url:book.url,width,status:response.status(),chapters:facts.chapters,deferred:book.status === 'deferred'});
        console.log(book.id + ' ' + width + ': OK');
      }
    }
  } finally {
    fs.writeFileSync(path.join(folder, live?'book-browser-live.json':'book-browser-preview.json'), JSON.stringify(results,null,2));
    await browser.close();
  }
  console.log(`${results.length} browser checks passed (${live?'live':'preview'}).`);
})().catch(error => { console.error(error); process.exitCode = 1; });
