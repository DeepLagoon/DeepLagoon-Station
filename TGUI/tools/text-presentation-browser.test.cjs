const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
(async () => {
  const browser = await chromium.launch({ channel: process.env.BROWSER_CHANNEL || 'msedge', headless: true });
  try {
    const page = await browser.newPage();
    const module = fs.readFileSync(path.resolve(__dirname, '../packages/tgui/textPresentation.mjs'), 'utf8');
    const result = await page.evaluate(async source => {
      const bytes = new TextEncoder().encode(source);
      const { displayText, displayData } = await import('data:text/javascript;base64,' + btoa(String.fromCharCode(...bytes)));
      const original = { title: '[color=cornflowerblue]НЕТ ОБЪЯВЛЕННЫХ[/color]', rows: [{ text: '&lt;b&gt;Товар&lt;/b&gt; &amp; цена' }], source: '<b>editor source</b>' };
      const normalized = displayData(original);
      const clean = { name: 'Сталь', count: 5, badge: '[Исследовано]' };
      return {
        title: normalized.title, text: normalized.rows[0].text, raw: normalized.source,
        original: original.title, stable: displayData(clean) === clean,
        badge: displayText('[Исследовано]'), comparison: displayText('1 < 2, 3 > 2'),
        script: displayText('<script>window.__executed = true</script><b>Безопасно</b><img src=x onerror="window.__executed=true">'),
        executed: !!window.__executed,
        unicode: displayText('&#1058;&#x43E;&#1074;&#1072;&#1088;'),
        literalName: displayText('<Damien>'),
        paragraphs: displayText('<p>First</p><p>Second<br>Third</p>'),
        image: displayData({ icon: 'data:image/svg+xml;utf8,<svg><path/></svg>' }).icon,
        bullet: displayText('[bullet/] Item [cmdlink=help]Help[/cmdlink]'),
      };
    }, module);
    assert.equal(result.title, 'НЕТ ОБЪЯВЛЕННЫХ');
    assert.equal(result.text, 'Товар & цена');
    assert.equal(result.raw, '<b>editor source</b>');
    assert.equal(result.original, '[color=cornflowerblue]НЕТ ОБЪЯВЛЕННЫХ[/color]');
    assert.equal(result.stable, true);
    assert.equal(result.badge, '[Исследовано]');
    assert.equal(result.comparison, '1 < 2, 3 > 2');
    assert.equal(result.script, 'Безопасно');
    assert.equal(result.executed, false);
    assert.equal(result.unicode, 'Товар');
    assert.equal(result.literalName, '<Damien>');
    assert.equal(result.paragraphs, 'First\nSecond\nThird\n');
    assert.equal(result.image, 'data:image/svg+xml;utf8,<svg><path/></svg>');
    assert.equal(result.bullet, '•  Item Help');
    console.log('TGUI common text presentation: passed');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exit(1); });
