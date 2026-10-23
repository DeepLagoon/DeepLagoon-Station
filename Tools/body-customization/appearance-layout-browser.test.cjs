const assert = require('node:assert/strict');
const fs = require('node:fs');
const http = require('node:http');
const path = require('node:path');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const root = path.resolve(__dirname, '../../Resources/Web/DeepLagoon');
(async () => {
 const server = http.createServer((request,response)=>{
  const file=path.resolve(root,'.'+new URL(request.url,'http://localhost').pathname);
  if(!file.startsWith(root+path.sep)||!fs.existsSync(file))return response.writeHead(404).end();
  response.setHeader('Content-Type',({'.html':'text/html','.js':'application/javascript','.css':'text/css'})[path.extname(file)]||'application/octet-stream');response.end(fs.readFileSync(file));
 });
 await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));let browser;
 try {
  browser=await chromium.launch({channel:'msedge',headless:true});
  const page=await browser.newPage({viewport:{width:1280,height:860}});const errors=[];
  page.on('pageerror',e=>errors.push(String(e)));
  await page.addInitScript(()=>{window.__sent=[];const NativeImage=window.Image;window.Image=class extends NativeImage{set src(value){if(!value.startsWith('res://deeplagoon/Web/DeepLagoon/bridge?')){super.src=value;return;}const[type,payload]=value.split('?')[1].split('&');window.__sent.push({type:decodeURIComponent(type),payload:JSON.parse(decodeURIComponent(payload))});queueMicrotask(()=>this.onload?.(new Event('load')));}};});
  await page.goto(`http://127.0.0.1:${server.address().port}/interface.html`);await page.waitForFunction(()=>window.__tguiAppBooted__);
  const publish=(iface,data)=>page.evaluate(({iface,data})=>window.update(JSON.stringify({type:'update',payload:{replaceData:true,replaceStaticData:true,resetPartialData:true,config:{interface:iface,status:2,window:{key:'appearance-test',fancy:false}},data}})),{iface,data});
  const option=(id,part,category)=>({id,part,category,name:id});
  const row=(index,part,name)=>({index,part,name,id:name,colors:[{name:'#ff0000'}],locked:false});
  const state={available:true,mode:'markings',appearance:{},tabs:[{id:0,name:'Основное',mode:'appearance'},{id:4,name:'Внешность',mode:'markings'}],markings:[row(0,'LArm','Левая рука'),row(1,'RArm','Правая рука'),row(2,'Chest','Торс')],markingOptions:[option('Другой левый протез','LArm','Левая рука'),option('Другой правый протез','RArm','Правая рука'),option('Другой торс','Chest','Грудь')],previewSlots:[{id:'jumpsuit',name:'Комбинезон',images:[]}],tattooEditor:{tattoos:[]}};
  for(const width of [1280,800,480,420]){
   await page.setViewportSize({width,height:860});await publish('CharacterEditor',state);
   await page.waitForSelector('.CharacterEditor__anatomySpace');
   const left=await page.getByRole('region',{name:'Левые части тела'}).boundingBox();const center=await page.locator('.CharacterEditor__anatomySpace').boundingBox();const right=await page.getByRole('region',{name:'Правые части тела'}).boundingBox();
   assert.ok(left.x+left.width<=center.x+1&&center.x+center.width<=right.x+1,`Side order at ${width}`);
   assert.equal(await page.locator('.CharacterEditor__slots').count(),0);
   assert.equal(await page.locator('.CharacterEditor__rotation').count(),0);
   assert.equal(await page.locator('.CharacterEditor').evaluate(e=>e.scrollWidth>e.clientWidth),false,`overflow ${width}`);
   const name=await page.getByRole('region',{name:'Левые части тела'}).locator('strong').boundingBox();const swatch=await page.getByRole('button',{name:'Цвет 1: Левая рука',exact:true}).boundingBox();
   assert.ok(Math.abs(name.y+name.height/2-swatch.y-swatch.height/2)<3,`Swatch same row ${width}`);
   const lower=await page.locator('.CharacterEditor__anatomyLower').boundingBox();assert.ok(lower.y>=center.y+center.height);
   await page.getByRole('button',{name:'Цвет 1: Левая рука',exact:true}).click();
   assert.equal(await page.locator('.ColorSquare__popover').count(),0);
   assert.ok(await page.evaluate(()=>window.__sent.some(e=>e.type==='act/color-picker-open')));
   await page.waitForFunction(()=>window.__sent.some(e=>e.type==='act/preview-bounds'));
   if(process.env.APPEARANCE_SCREENSHOT&&width===1280)await page.screenshot({path:process.env.APPEARANCE_SCREENSHOT});
  }
  const open=await page.evaluate(()=>window.__sent.filter(e=>e.type==='act/color-picker-open').at(-1).payload);
  await publish('CharacterEditor',{...state,colorPickerOpen:true,colorPickerResult:{id:open.id,value:'#123456',sequence:1}});
  await page.waitForFunction(()=>window.__sent.some(e=>e.type==='act/marking-color'&&e.payload.value==='#123456'&&e.payload.index===0));
  await page.setViewportSize({width:380,height:430});await publish('CharacterColorPicker',{id:'test',title:'Цвет хвоста',value:'#ff0000',appearance:{}});
  await page.getByRole('slider',{name:'Цветовой круг',exact:true}).waitFor();
  await page.getByRole('button',{name:'HSV',exact:true}).click();
  assert.equal(await page.locator('.PlayerColorPicker__wheel').count(),0);
  assert.equal(await page.getByLabel('H',{exact:true}).count(),1);assert.equal(await page.getByLabel('R',{exact:true}).count(),0);
  await page.getByLabel('H',{exact:true}).focus();await page.keyboard.press('ArrowRight');
  const hsvHex=await page.getByLabel('HEX',{exact:true}).inputValue();
  await page.getByRole('button',{name:'RGB',exact:true}).click();
  assert.equal(await page.getByLabel('H',{exact:true}).count(),0);assert.equal(await page.getByLabel('R',{exact:true}).count(),1);
  assert.equal(await page.getByLabel('HEX',{exact:true}).inputValue(),hsvHex);
  await page.getByLabel('R',{exact:true}).fill('64');
  const rgbHex=await page.getByLabel('HEX',{exact:true}).inputValue();
  await page.getByRole('button',{name:'Цветовой круг',exact:true}).click();
  assert.equal(await page.getByLabel('HEX',{exact:true}).inputValue(),rgbHex);
  assert.equal(await page.getByLabel('R',{exact:true}).count(),0);
  await page.getByRole('button',{name:'Закрыть выбор цвета'}).click();
  assert.ok(await page.evaluate(()=>window.__sent.some(e=>e.type==='act/color-picker-close'&&e.payload.id==='test')));
  assert.deepEqual(errors,[]);
  console.log('PASS: 1280/800/480/420 layouts; no clothing slots; inline color buttons; native dialog dispatch and color relay; separate wheel/HSV/RGB modes retain color.');
 }finally{await browser?.close();await new Promise(resolve=>server.close(resolve));}
})().catch(error=>{console.error(error);process.exitCode=1;});
