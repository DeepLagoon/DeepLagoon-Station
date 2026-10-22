const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const repo = path.resolve(__dirname, '../..');
const resources = path.join(repo, 'Resources/Web/DeepLagoon');
const labels = Object.fromEntries(['interaction_panel.ftl', 'lustwish-equipment.ftl'].flatMap(file =>
  [...fs.readFileSync(path.join(repo, 'Resources/Locale/ru-RU/_DeepLagoon', file), 'utf8')
    .matchAll(/^dl-interaction-panel-([\w-]+)\s*=\s*(.+)$/gm)].map(m => [m[1], m[2].trim()])));
(async () => {
  const server = http.createServer((req, res) => {
    const file = path.resolve(resources, '.' + new URL(req.url, 'http://localhost').pathname);
    if (!file.startsWith(resources + path.sep) || !fs.existsSync(file)) { res.writeHead(404).end(); return; }
    res.setHeader('Content-Type', ({'.html':'text/html','.js':'text/javascript','.css':'text/css','.woff2':'font/woff2'})[path.extname(file)] || 'application/octet-stream');
    res.end(fs.readFileSync(file));
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  let browser;
  try {
    browser = await chromium.launch({channel:'msedge',headless:true});
    const page = await browser.newPage({viewport:{width:420,height:560}});
    const errors=[]; page.on('pageerror', e => errors.push(String(e)));
    await page.addInitScript(() => {
      const NativeImage=window.Image;
      window.Image=class extends NativeImage { set src(value) {
        if (!value.startsWith('res://deeplagoon/Web/DeepLagoon/bridge?')) { super.src=value; return; }
        queueMicrotask(() => this.onload?.(new Event('load')));
      }};
    });
    await page.goto(`http://127.0.0.1:${server.address().port}/interface.html`);
    await page.waitForFunction(() => window.__tguiAppBooted__);
    const data={name:'Персонаж',self:true,humanoid:true,debug:false,interval:3,status:'Готово',labels,
      regions:[{id:'all',name:'Все'}],categories:[{id:3,name:'Нейтральные'}],actions:[],custom:[],sounds:[],
      libraryLoaded:true,waiting:false,libraryStatus:'',libraryRevision:0,librarySaved:false,
      targetDoll:{base:'',parts:{}},bodyFeatures:[],arousalCurrent:0,arousalMaximum:200, heldName:'Ошейник',heldSlot:'neck',gearColors:[{id:'#AC70D4',name:'#AC70D4'}],gear:[{slot:'neck',name:'Электрический ошейник',locked:true,active:false,electronic:true,shock:true,intensity:2,colorable:true,leashed:false}]};
    await page.evaluate(data=>window.update(JSON.stringify({type:'update',payload:{replaceData:true,config:{interface:'InteractionPanel',title:'Панель взаимодействий',status:2,window:{key:'meter',fancy:false}},data}})),data);
    const meter=page.getByRole('progressbar',{name:labels.arousal});
    await meter.waitFor();
    for (const [current,expected] of [[0,0],[60,30],[200,100],[250,100],[-10,0]]) {
      await page.evaluate(arousalCurrent=>window.update(JSON.stringify({type:'update',payload:{data:{arousalCurrent,arousalOnly:true}}})),current);
      await page.waitForFunction(expected=>document.querySelector('[role=progressbar]').getAttribute('aria-valuenow')===String(expected),expected);
      assert((await meter.innerText()).includes(expected+'%'));
    }
    for(const width of [420,360]) {
      await page.setViewportSize({width,height:420});
      const box=await meter.boundingBox(); assert(box.x>=0 && box.x+box.width<=width);
      assert(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth));
    }
    await page.getByRole('button',{name:labels['tab-custom'],exact:true}).click();
    await page.getByRole('button',{name:labels['editor-new'],exact:true}).click();
    const title=page.locator('.InteractionPanel__custom input').first(); await title.fill('Сохранённый черновик');
    await page.evaluate(()=>window.update(JSON.stringify({type:'update',payload:{data:{arousalCurrent:150}}})));
    await page.waitForFunction(()=>document.querySelector('[role=progressbar]').getAttribute('aria-valuenow')==='75');
    assert.equal(await title.inputValue(),'Сохранённый черновик');
    assert.equal(await page.getByRole('button',{name:labels['tab-custom'],exact:true}).getAttribute('aria-pressed'),'true');
    assert.equal(await page.locator('.InteractionPanel__arousalFill').evaluate(el=>getComputedStyle(el).backgroundImage),'linear-gradient(90deg, rgb(121, 66, 181), rgb(180, 117, 237))');
    await page.getByRole('button',{name:labels['tab-gear'],exact:true}).click();
    await page.locator('.InteractionPanel__gearItem').waitFor();
    assert.equal(await page.getByRole('button',{name:labels['gear-remove'],exact:true}).isDisabled(),true);
    await page.getByRole('button',{name:labels['gear-unlock'],exact:true}).click();
    await page.getByRole('button',{name:labels['gear-pair'],exact:true}).click();
    await page.getByRole('button',{name:labels['gear-pulse'],exact:true}).click();
    assert(await page.getByRole('button',{name:labels['gear-release-all'],exact:true}).isEnabled());
    await page.evaluate(()=>window.update(JSON.stringify({type:'update',payload:{data:{arousalCurrent:100,gearOnly:true}}})));
    assert.equal(await page.getByRole('button',{name:labels['tab-gear'],exact:true}).getAttribute('aria-pressed'),'true');
    assert(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth));
    const screenshot = path.join(repo,'.local/codex/lustwish/accessories-360.png');
    fs.mkdirSync(path.dirname(screenshot),{recursive:true});
    await page.screenshot({path:screenshot});
    await page.getByRole('button',{name:labels['tab-custom'],exact:true}).click();
    assert.equal(await title.inputValue(),'Сохранённый черновик');
    assert.deepEqual(errors,[]);
    console.log('PASS: live 0/30/100 percent, clamping, 420/360px layout, purple fill, draft and tab preservation, accessory controls and draft preservation, no browser exceptions');
  } finally {await browser?.close(); await new Promise(resolve=>server.close(resolve));}
})().catch(e=>{console.error(e);process.exitCode=1;});
