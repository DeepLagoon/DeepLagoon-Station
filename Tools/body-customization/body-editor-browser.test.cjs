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
    const feature=(part,name)=>({part,name,enabled:true,shape:'pair',size:'1',shapes:[{id:'pair',name:'Обычная'}],sizes:[{id:'1',name:'1'}],visibility:1,matchSkin:true,color:'#ffffff',secondaryEnabled:true,secondaryAvailable:true,secondaryColor:'#FF3300',womb:true,anus:true});
    const state={available:true,mode:'appearance',name:'Test',age:25,sex:0,gender:0,spawn:0,species:'Human',height:1,width:1,minAge:18,maxAge:100,skinColor:'#ffffff',eyeColor:'#ffffff',appearance:{},tabs:[],previewSlots:[],speciesOptions:[{id:'Human',name:'Human'}],hairOptions:[],beardOptions:[],bodyFeatures:[feature('butt','Ягодицы / бёдра'),feature('breasts','Грудь'),feature('vagina','Вагина'),feature('anus','Анус'),feature('testicles','Яички'),feature('penis','Пенис')]};
    for(const width of [1100,800,480]) {
      await page.setViewportSize({width,height:760});
      await publish('CharacterEditor',state);
      await page.evaluate(()=>{window.__sent=[];});
      const panel=page.locator('.CharacterEditor__bodyFeatures');
      await panel.locator('> summary').click();
      for(const name of ['Ягодицы / бёдра','Грудь','Пенис']) await panel.locator('> details > summary').filter({hasText:name}).click();
      const nested=panel.locator('details details');
      assert.equal(await nested.locator('> summary').textContent(),'Яички ✓');
      assert.equal(await panel.locator('> details > summary').filter({hasText:/^Анус/}).count(),0);
      assert.equal(await panel.locator('> details > summary').filter({hasText:/^Яички/}).count(),0);
      const toggles=panel.locator('.CharacterEditor__grid > .Button');
      for(const button of await toggles.all()) {
        const box=await button.boundingBox();
        assert.ok(box.height<45,`Toggle height ${box.height} at width ${width}`);
        assert.ok(box.width<180,`Toggle width ${box.width} at width ${width}`);
      }
      const breastsPanel=panel.locator('> details').filter({has:page.locator('summary').filter({hasText:/^Грудь/})});
      const clothToggle=breastsPanel.getByRole('checkbox',{name:'Деформировать одежду',exact:true});
      assert.equal(await clothToggle.isChecked(),true);
      await clothToggle.click();
      assert.ok(await page.evaluate(()=>window.__sent.some(m=>m.type==='act/body-feature'&&m.payload.part==='breasts'&&m.payload.field==='showThroughClothing'&&m.payload.value==='false')));
      await publish('CharacterEditor',{...state,bodyFeatures:state.bodyFeatures.map(feature=>feature.part==='breasts'?{...feature,showThroughClothing:false}:feature)});
      await breastsPanel.getByRole('checkbox',{name:'Деформировать одежду',exact:true,checked:false}).waitFor();
      assert.equal(await clothToggle.isChecked(),false);
      const penisPanel=panel.locator('> details').filter({has:page.locator('summary').filter({hasText:/^Пенис/})});
      await penisPanel.locator('details > summary').click();
      assert.equal(await penisPanel.locator('details').getByRole('checkbox',{name:'Деформировать одежду',exact:true}).count(),1);
      const anusToggle=panel.getByRole('checkbox',{name:'Анус: Деформировать одежду',exact:true});
      await anusToggle.click();
      assert.ok(await page.evaluate(()=>window.__sent.some(m=>m.type==='act/body-feature'&&m.payload.part==='anus'&&m.payload.field==='showThroughClothing'&&m.payload.value==='false')));
      assert.equal(await panel.evaluate(e=>e.scrollWidth>e.clientWidth),false);
      await panel.getByText('Анус: есть',{exact:true}).click();
      assert.ok(await page.evaluate(()=>window.__sent.some(m=>m.type==='act/body-feature'&&m.payload.part==='butt'&&m.payload.field==='anus'&&m.payload.value==='false')));
      await panel.getByText('Цветные соски',{exact:true}).click();
      assert.ok(await page.evaluate(()=>window.__sent.some(m=>m.type==='act/body-feature'&&m.payload.part==='breasts'&&m.payload.field==='secondaryEnabled')));
      // Reset details to their initial state for the next viewport.
      await page.evaluate(()=>document.querySelectorAll('.CharacterEditor__bodyFeatures details, .CharacterEditor__bodyFeatures').forEach(e=>e.open=false));
    }
    await page.setViewportSize({width:420,height:560});
    const labels={'tab-actions':'Действия','tab-custom':'Свои','tab-body':'Части тела','body-own':'Ваш персонаж','body-hidden':'Скрыто','body-under':'Под одеждой','body-over':'Поверх одежды'};
    await publish('InteractionPanel',{name:'Другой персонаж',self:false,humanoid:true,interval:3,labels,regions:[],categories:[],actions:[],custom:[],sounds:[],bodyFeatures:[{part:'breasts',name:'Грудь',visibility:1}]});
    await page.getByRole('button',{name:'Части тела',exact:true}).click();
    await page.getByRole('combobox',{name:'Грудь',exact:true}).click();
    await page.getByRole('option',{name:'Поверх одежды',exact:true}).click();
    assert.ok(await page.evaluate(()=>window.__sent.some(m=>m.type==='act/body-visibility'&&m.payload.id==='breasts'&&m.payload.value===2)));
    assert.equal(await page.locator('.InteractionPanel__bodySettings').isVisible(),true);
    assert.equal(await page.locator('.InteractionPanel').evaluate(e=>e.scrollWidth>e.clientWidth),false);
    assert.deepEqual(errors,[]);
    if(process.env.BODY_SCREENSHOT) await page.screenshot({path:process.env.BODY_SCREENSHOT});
    console.log('Body editor: nesting, compact buttons and actions pass at 1100/800/480px; third interaction tab and visibility action pass at 420px.');
  } finally { await browser?.close(); await new Promise(resolve=>server.close(resolve)); }
})().catch(error=>{console.error(error);process.exitCode=1;});
