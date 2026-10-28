// Exercise the actual production TGUI bundle. The fixture acknowledges transport only;
// server composition, archive and migrations are covered by the C# tests.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const http = require('node:http');
const path = require('node:path');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const repo = path.resolve(__dirname, '../..'), assets = path.join(repo, 'Resources/Web/DeepLagoon');
const output = path.join(repo, 'bin/ArtQA'); fs.mkdirSync(output, { recursive: true });
const locale = fs.readFileSync(path.join(repo, 'Resources/Locale/ru-RU/_DeepLagoon/art.ftl'), 'utf8');
const labels = Object.fromEntries([...locale.matchAll(/^dl-art-ui-([\w-]+) = (.*)$/gm)].map(m => [m[1], m[2]]));
const state = { document: JSON.stringify({ version:1, title:'Тихая лагуна', layers:[{name:'Слой 1',visible:true,opacity:1,png:'',mask:''}] }),
  revision:0, result:0, tool:'brush', status:'', labels, maxLayers:8, maxDocument:8388608, chunkSize:5000, closeRequest:0 };
(async () => {
  const server = http.createServer((req, res) => {
    const file = path.resolve(assets, '.' + new URL(req.url, 'http://localhost').pathname);
    if (!file.startsWith(assets + path.sep) || !fs.existsSync(file)) return res.writeHead(404).end();
    res.setHeader('Content-Type', ({'.html':'text/html','.js':'application/javascript','.css':'text/css','.woff2':'font/woff2'})[path.extname(file)] || 'application/octet-stream');
    fs.createReadStream(file).pipe(res);
  });
  await new Promise(r => server.listen(0, '127.0.0.1', r));
  let browser;
  try {
    browser = await chromium.launch({ channel:process.env.BROWSER_CHANNEL || 'msedge', headless:true });
    const page = await browser.newPage({ viewport:{width:980,height:720} });
    const errors=[]; page.on('pageerror', e => errors.push(String(e)));
    await page.addInitScript(() => {
      window.__sent=[];
      const NativeImage=window.Image;
      window.Image=class extends NativeImage {
        set src(url) {
          if (!url.startsWith('res://deeplagoon/Web/DeepLagoon/bridge?')) { super.src=url; return; }
          const [type,payload]=url.split('?')[1].split('&');
          window.__sent.push({type:decodeURIComponent(type),payload:JSON.parse(decodeURIComponent(payload))});
          queueMicrotask(()=>this.onload?.(new Event('load')));
        }
      };
    });
    const publish=()=>page.evaluate(data=>window.update(JSON.stringify({type:'update',payload:{replaceData:true,
      config:{interface:'ArtStudio',status:2,window:{key:'deeplagoon',fancy:false}},data}})),state);
    const settled=()=>page.evaluate(()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve))));
    const boot=async()=>{ await page.goto(`http://127.0.0.1:${server.address().port}/interface.html`); await page.waitForFunction(()=>window.__tguiAppBooted__); await publish(); await page.getByRole('textbox',{name:labels.name,exact:true}).waitFor(); await page.waitForFunction(()=>!document.querySelector('.ArtStudio__bar input').disabled); await settled(); };
    await boot();
    const image=()=>page.locator('.ArtStudio canvas').evaluate(c=>c.toDataURL());
    const initial=await image();
    const stroke=async(dx=0,dy=0)=>{const r=await page.locator('.ArtStudio canvas').boundingBox(); await page.mouse.move(r.x+r.width*.25+dx,r.y+r.height*.4+dy); await page.mouse.down(); await page.mouse.move(r.x+r.width*.7+dx,r.y+r.height*.6+dy,{steps:12}); await page.mouse.up(); await settled();};
    await stroke();
    await page.waitForFunction(()=>!document.querySelector('.ArtStudio__bar button').disabled);
    assert.notEqual(await image(),initial,'Continuous stroke must change the canvas');
    const painted=await image();
    await page.waitForFunction(()=>window.__sent.some(m=>m.type==='act/preview'));
    await page.keyboard.press('Control+z'); await page.waitForFunction(expected=>document.querySelector('.ArtStudio canvas').toDataURL()===expected,initial);
    await page.keyboard.press('Control+Shift+z'); await page.waitForFunction(expected=>document.querySelector('.ArtStudio canvas').toDataURL()===expected,painted);
    await page.getByRole('button',{name:labels['add-layer'],exact:true}).click();
    await page.getByRole('button',{name:labels['add-mask'],exact:true}).click();
    await page.getByRole('combobox',{name:labels.shape,exact:true}).click(); await page.getByRole('option',{name:labels.soft,exact:true}).click(); await stroke(0,-20);
    await page.getByRole('button',{name:labels.save,exact:true}).click();
    await page.waitForFunction(()=>{const a=window.__sent.filter(m=>m.type==='act/save');return a.length&&a.at(-1).payload.index+1===a.at(-1).payload.count;});
    const chunks=await page.evaluate(()=>window.__sent.filter(m=>m.type==='act/save').map(m=>m.payload));
    chunks.forEach((c,i)=>{assert.equal(c.index,i);assert.ok(c.text.length<=5000);});
    const project=JSON.parse(chunks.map(c=>c.text).join(''));
    assert.equal(project.layers.length,2); assert.ok(project.layers[1].mask.length>0); assert.equal(project.title,'Тихая лагуна');
    state.document=JSON.stringify(project);state.revision++;state.result++;state.status='Сохранено';await publish();
    await page.waitForFunction(()=>document.querySelector('.ArtStudio footer').textContent==='Сохранено');
    assert.equal(await page.getByRole('button',{name:labels.save,exact:true}).isDisabled(),true);
    await page.waitForFunction(()=>window.__sent.some(m=>m.type==='act/preview'));
    const previews=await page.evaluate(()=>window.__sent.filter(m=>m.type==='act/preview'));
    assert.ok(previews.every(m=>m.payload.text.length<=5000),'World previews use bounded chunks');
    await page.getByRole('button',{name:labels['remove-mask'],exact:true}).click();
    await page.getByRole('button',{name:labels.close,exact:true}).click();
    await page.getByRole('dialog').waitFor(); await page.getByRole('dialog').getByRole('button',{name:labels.cancel,exact:true}).click();
    assert.equal(await page.getByRole('dialog').count(),0,'Cancel closing must retain the draft');
    await page.getByRole('button',{name:labels.discard,exact:true}).click();
    await page.getByRole('dialog').getByRole('button',{name:labels.discard,exact:true}).click();
    assert.equal(await page.getByRole('button',{name:labels['remove-mask'],exact:true}).count(),1,'Discard restores the saved mask');
    // Reopen with the saved server document, not an in-memory editing session.
    const savedImage=await image(); await boot();
    await page.waitForFunction(expected=>document.querySelector('.ArtStudio canvas').toDataURL()===expected,savedImage);
    const colors=await page.locator('.ArtStudio').evaluate(el=>[getComputedStyle(el).backgroundColor,getComputedStyle(el.querySelector('.ArtStudio__panels')).backgroundColor]);
    colors.forEach(c=>{const v=c.match(/\d+/g).map(Number);assert.equal(v[0],v[1]);assert.equal(v[1],v[2]);});
    for(const viewport of [{width:700,height:520},{width:1180,height:800}]){
      await page.setViewportSize(viewport);
      const overflow=await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1||document.documentElement.scrollHeight>innerHeight+1);
      assert.equal(overflow,false,JSON.stringify(viewport));
      const bounds=await page.evaluate(()=>{
        const c=document.querySelector('.ArtStudio canvas').getBoundingClientRect();
        const v=document.querySelector('.ArtStudio__viewport').getBoundingClientRect();
        const f=document.querySelector('.ArtStudio footer').getBoundingClientRect();
        return {square:Math.abs(c.width-c.height)<1,contained:c.left>=v.left&&c.right<=v.right&&c.top>=v.top&&c.bottom<=v.bottom,footer:f.bottom<=innerHeight};
      });
      assert.deepEqual(bounds,{square:true,contained:true,footer:true},'The whole canvas and status must fit at '+JSON.stringify(viewport));
    }
    await page.setViewportSize({width:980,height:720}); await page.screenshot({path:path.join(output,'studio.png')});
    assert.deepEqual(errors,[]);
    console.log('PASS: production ArtStudio; continuous paint, keyboard undo/redo, textured brush, mask, layers, bounded save/preview, close cancellation, discard, reload, gray styling and two window sizes.');
  } finally { await browser?.close(); await new Promise(r=>server.close(r)); }
})().catch(e=>{console.error(e);process.exitCode=1;});
