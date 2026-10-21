const assert = require('node:assert/strict');
const fs = require('node:fs');
const http = require('node:http');
const path = require('node:path');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const root = path.resolve(__dirname, '../../Resources/Web/DeepLagoon');
(async () => {
  const server = http.createServer((request, response) => {
    const file = path.resolve(root, '.' + new URL(request.url, 'http://localhost').pathname);
    if (!file.startsWith(root + path.sep) || !fs.existsSync(file)) return response.writeHead(404).end();
    response.setHeader('Content-Type', ({'.html':'text/html','.js':'application/javascript','.css':'text/css','.woff2':'font/woff2'})[path.extname(file)] || 'application/octet-stream');
    response.end(fs.readFileSync(file));
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  let browser;
  try {
    browser = await chromium.launch({channel:'msedge',headless:true});
    const page = await browser.newPage();
    const errors = [];
    page.on('pageerror', error => errors.push(String(error)));
    await page.addInitScript(() => {
      window.__sent=[];
      const NativeImage=window.Image;
      window.Image=class extends NativeImage {
        set src(value) {
          if (!value.startsWith('res://deeplagoon/Web/DeepLagoon/bridge?')) { super.src=value; return; }
          const [type,payload]=value.split('?')[1].split('&');
          window.__sent.push({type:decodeURIComponent(type),payload:JSON.parse(decodeURIComponent(payload))});
          queueMicrotask(()=>this.onload?.(new Event('load')));
        }
      };
    });
    await page.goto(`http://127.0.0.1:${server.address().port}/interface.html`);
    await page.waitForFunction(() => window.__tguiAppBooted__);
    const state = {available:true,mode:'appearance',name:'Test',age:25,sex:0,gender:0,spawn:0,species:'Vulpkanin',height:1,width:1,minAge:18,maxAge:100,skinColor:'#985629',eyeColor:'#ffffff',appearance:{},tabs:[],previewSlots:[],speciesOptions:[{id:'Vulpkanin',name:'Вульпканин'}],hairOptions:[],beardOptions:[],bodyFeatures:[],canUsePlantigradeLegs:true,plantigradeLegs:false};
    const publish = data => page.evaluate(data => window.update(JSON.stringify({type:'update',payload:{replaceData:true,replaceStaticData:true,resetPartialData:true,config:{interface:'CharacterEditor',status:2,window:{key:'leg-shape-test',fancy:false}},data}})),data);
    for (const width of [480,800,1100]) {
      await page.setViewportSize({width,height:820});
      await publish(state);
      const box=page.getByRole('checkbox',{name:'Прямые ноги',exact:true});
      await page.waitForFunction(()=>document.querySelector('.CharacterEditor__legShape input')?.checked===false);
      assert.equal(await box.count(),1);
      assert.equal(await box.isChecked(),false);
      const bounds=await box.boundingBox();
      assert.ok(bounds.width<=24 && bounds.height<=24,`Checkbox size at ${width}: ${JSON.stringify(bounds)}`);
      await box.click();
      assert.ok(await page.evaluate(()=>window.__sent.some(m=>m.type==='act/plantigrade-legs'&&m.payload.value===true)));
      await publish({...state,plantigradeLegs:true,dirty:true});
      await page.waitForFunction(()=>document.querySelector('.CharacterEditor__legShape input')?.checked===true);
      assert.equal(await box.isChecked(),true);
    }
    await page.screenshot({path:path.resolve(__dirname,'../../.artifacts/plantigrade-tests/editor-checkbox.png')});
    await publish({...state,species:'Human',canUsePlantigradeLegs:false});
    await page.locator('.CharacterEditor__legShape input').waitFor({state:'detached'});
    assert.equal(await page.getByRole('checkbox',{name:'Прямые ноги',exact:true}).count(),0);
    assert.deepEqual(errors,[]);
    console.log('Straight-leg checkbox: 480/800/1100px, boolean action, saved state and unsupported race visibility passed.');
  } finally { if(browser) await browser.close(); await new Promise(resolve=>server.close(resolve)); }
})().catch(error=>{console.error(error);process.exitCode=1});
