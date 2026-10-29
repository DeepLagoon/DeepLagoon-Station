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
    const page = await browser.newPage({viewport:{width:1100,height:760}});
    const errors = [];
    page.on('pageerror', error => errors.push(String(error)));
    await page.addInitScript(() => {
      window.__sent = [];
      const NativeImage = window.Image;
      window.Image = class extends NativeImage {
        set src(value) {
          if (!value.startsWith('res://deeplagoon/Web/DeepLagoon/bridge?')) { super.src=value; return; }
          const [type,payload]=value.split('?')[1].split('&');
          window.__sent.push({type:decodeURIComponent(type),payload:JSON.parse(decodeURIComponent(payload))});
          queueMicrotask(()=>this.onload?.(new Event('load')));
        }
      };
    });
    await page.goto(`http://127.0.0.1:${server.address().port}/interface.html`);
    await page.waitForFunction(()=>window.__tguiAppBooted__);
    const publish = (iface,data) => page.evaluate(({iface,data})=>window.update(JSON.stringify({type:'update',payload:{replaceData:true,replaceStaticData:true,resetPartialData:true,config:{interface:iface,status:2,window:{key:'body-test',fancy:false}},data}})),{iface,data});
    const ftl=fs.readFileSync(path.resolve(__dirname,'../../Resources/Locale/ru-RU/_DeepLagoon/bluemoon-physiology.ftl'),'utf8');
    const labels=Object.fromEntries([...ftl.matchAll(/^dl-arousal-pref-([\w-]+)\s*=\s*(.+)$/gm)].map(m=>[m[1],m[2]]));
    const toggles=['enabled','aphrodisiacs','hormones','receive-fluids','fluid-growth','pregnancy','messy','estrous'];
    const select=['penis','vagina','breasts','anus','fluid'];
    const physiology=Object.entries(labels).map(([id,label])=>({id,label,kind:toggles.includes(id)?'toggle':select.includes(id)?'select':'number',value:id==='tolerance'?100:id==='potency'?15:id==='enabled'||id==='aphrodisiacs'?1:0,min:id==='potency'?-1:0,max:300,options:[{id:0,name:'Обычная'},{id:1,name:'Гиперчувствительность'}]}));
    const state={available:true,mode:'appearance',name:'Test',age:25,sex:0,gender:0,spawn:0,species:'Human',height:1,width:1,minAge:18,maxAge:100,skinColor:'#ffffff',eyeColor:'#ffffff',appearance:{},tabs:[],previewSlots:[],speciesOptions:[{id:'Human',name:'Human'}],hairOptions:[],beardOptions:[],bodyFeatures:[],physiology};
    await publish('CharacterEditor',state);
    const panel=page.locator('.CharacterEditor__physiology');await panel.locator('>summary').click();
    assert.equal(await panel.locator('input, [role=combobox]').count(),physiology.length);
    for(const id of ['fluid-growth','pregnancy','hormones','receive-fluids'])assert.equal(await panel.getByRole('checkbox',{name:labels[id],exact:true}).isChecked(),false);
    await panel.getByRole('checkbox',{name:labels.pregnancy,exact:true}).click();
    assert(await page.evaluate(()=>window.__sent.some(m=>m.type==='act/physiology'&&m.payload.id==='pregnancy'&&m.payload.value===1)));
    const tolerance=panel.getByRole('spinbutton',{name:labels.tolerance,exact:true});await tolerance.fill('175');
    assert(await page.evaluate(()=>window.__sent.some(m=>m.type==='act/physiology'&&m.payload.id==='tolerance'&&m.payload.value===175)));
    await panel.getByRole('combobox',{name:labels.penis,exact:true}).click();await page.getByRole('option',{name:'Гиперчувствительность',exact:true}).click();
    assert(await page.evaluate(()=>window.__sent.some(m=>m.type==='act/physiology'&&m.payload.id==='penis'&&m.payload.value===1)));
    for(const width of [1100,800,480,420]){
      await page.setViewportSize({width,height:760});assert.equal(await panel.evaluate(e=>e.scrollWidth>e.clientWidth),false);assert(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth));
    }
    await page.screenshot({path:path.resolve(__dirname,'../../.local/codex/bluemoon-arousal/character-physiology-420.png')});
    assert.deepEqual(errors,[]);console.log(`PASS: ${physiology.length} character preferences, opt-in defaults, numeric/selector actions, 420/480/800/1100px, no browser errors`);
  } finally { await browser?.close(); await new Promise(resolve=>server.close(resolve)); }
})().catch(e=>{console.error(e);process.exitCode=1;});
