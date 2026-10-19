const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const assert = require('node:assert/strict');
const root = path.resolve(__dirname, '..').replaceAll('\\', '/');
const { build } = require(root + '/node_modules/esbuild');
const sass = require(root + '/node_modules/sass');
const { chromium } = require(process.env.PLAYWRIGHT_PATH || 'playwright');
const output = path.join(root, 'node_modules/.cache/grid-editor-browser'); fs.mkdirSync(output, { recursive: true });
const portable = p => p.replaceAll('\\', '/');
async function main() {
  const preview = fs.readFileSync(root + '/tools/grid-editor-preview.mjs', 'utf8');
  let source = preview.slice(preview.indexOf('const source = `') + 16, preview.indexOf('`;\nfs.writeFileSync') > 0 ? preview.indexOf('`;\nfs.writeFileSync') : preview.indexOf('`;\r\nfs.writeFileSync'));
  source = source.replace("'../packages/tgui/interfaces/GridEditor.jsx'", JSON.stringify(root + '/packages/tgui/interfaces/GridEditor.jsx')).replace("'../packages/tgui/interfaces/gridEditorRouting.mjs'",JSON.stringify(root+'/packages/tgui/interfaces/gridEditorRouting.mjs'));
  source += `\nimport { GridSceneCache, itemChunks } from '${root}/packages/tgui/interfaces/gridEditorSceneCache.mjs';
import * as model from '${root}/packages/tgui/interfaces/gridEditorModel.mjs';
window.testing = {GridSceneCache, itemChunks, model};
window.sceneTimings = []; window.sceneCaches = [];
const paint = GridSceneCache.prototype.paint, prepare = GridSceneCache.prototype.prepare;
GridSceneCache.prototype.paint = function(...args) { const start=performance.now(); const result=paint.apply(this,args); window.sceneTimings.push(performance.now()-start); return result; };
GridSceneCache.prototype.prepare = function(...args) { if(!window.sceneCaches.includes(this))window.sceneCaches.push(this); return prepare.apply(this,args); };
`;
  const fixture = path.join(output, 'fixture.jsx'); fs.writeFileSync(fixture, source);
  const modes = process.env.GRID_EDITOR_BASELINE ? [true, false] : [false];
  for (const legacy of modes) await build({ entryPoints:[fixture], outfile:path.join(output, legacy?'old.js':'new.js'), bundle:true, jsx:'automatic', nodePaths:[root+'/node_modules'], define:{'process.env.NODE_ENV':'"production"'}, plugins:[{name:'fixture',setup(build){
    build.onResolve({filter:/^\.\.\/backend$/},()=>({path:'backend',namespace:'fixture'}));
    build.onLoad({filter:/.*/,namespace:'fixture'},()=>({contents:`import {useContext} from 'react';import {Context} from ${JSON.stringify(portable(fixture))};export const useBackend=()=>useContext(Context);`,resolveDir:root}));
    build.onLoad({filter:/\.scss$/},()=>({contents:'',loader:'css'}));
    if(legacy) build.onLoad({filter:/[/\\](GridEditor|GridEditorScene)\.jsx$/}, args=>{
      let contents=fs.readFileSync(path.join(process.env.GRID_EDITOR_BASELINE,path.basename(args.path)),'utf8');
      if(path.basename(args.path)==='GridEditorScene.jsx') contents=contents.replace('let cancelled = false;','let cancelled = false; const started = performance.now();').replace(/\r?\n    \}\);\r?\n    return/, '\n      window.sceneTimings.push(performance.now()-started);\n    });\n    return');
      return {contents,loader:'jsx',resolveDir:root+'/packages/tgui/interfaces'};
    });
  }}] });
  const css = sass.compile(root+'/packages/tgui/styles/reset.scss').css+sass.compile(root+'/packages/tgui/interfaces/GridEditor.scss').css;
  const server=http.createServer((req,res)=>{if(req.url.endsWith('.js')){res.setHeader('Content-Type','text/javascript');res.end(fs.readFileSync(path.join(output,req.url.includes('old')?'old.js':'new.js')));}else{res.setHeader('Content-Type','text/html');res.end(`<html><meta charset="utf-8"><style>html,body,#root{height:100%;margin:0;font-family:Arial,sans-serif}*{box-sizing:border-box}${css}</style><div id="root"></div><script src="/${req.url.includes('legacy')?'old':'new'}.js"></script></html>`);}});
  await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
  const browser=await chromium.launch({channel:'msedge',headless:true});
  const errors=[];
  try {
    const context=await browser.newContext({viewport:{width:1440,height:900}});
    const page=await context.newPage();
    page.on('pageerror',e=>errors.push(e.message));
    const url='http://127.0.0.1:'+server.address().port;
    const report={};
    for(const legacy of modes) {
      await page.goto(url+'/?large'+(legacy?'&legacy':''));
      await page.waitForFunction(()=>window.fixtureGetData?.().items.length===62500);
      if(!legacy) {
        await page.waitForSelector('progress');
        await page.screenshot({path:path.join(output,'loading.png')});
        await page.waitForSelector('.GridEditor__loading',{state:'hidden',timeout:60000}); await page.waitForFunction(()=>window.sceneCaches.some(c=>c.stats.rasterized>0));
      }
      await page.waitForTimeout(500);
      report[legacy?'old':'new']=await page.evaluate(async()=>{
        const view=document.querySelector('.GridEditor__viewport');
        window.sceneTimings=[];
        let calls=0;const draw=CanvasRenderingContext2D.prototype.drawImage;
        CanvasRenderingContext2D.prototype.drawImage=function(...args){calls++;return draw.apply(this,args);};
        const rasterized=window.sceneCaches.reduce((n,c)=>n+c.stats.rasterized,0);
        for(let i=0;i<90;i++){
          view.scrollLeft=70+i*2;view.scrollTop=50+i;
          if(i%10===0)view.dispatchEvent(new WheelEvent('wheel',{deltaY:i<40?-120:120,bubbles:true,cancelable:true}));
          await new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)));
        }
        CanvasRenderingContext2D.prototype.drawImage=draw;
        const times=window.sceneTimings.slice().sort((a,b)=>a-b);
        return {frames:times.length,medianMs:times[Math.floor(times.length*.5)],p95Ms:times[Math.floor(times.length*.95)],drawImageCalls:calls,additionalRasterized:window.sceneCaches.reduce((n,c)=>n+c.stats.rasterized,0)-rasterized};
      });
      if(!legacy) assert.equal(report.new.additionalRasterized,0);
    }
    await page.screenshot({path:path.join(output,'large-grid.png')});
    report.cache=await page.evaluate(async()=>{
      const {GridSceneCache,model,itemChunks}=window.testing;
      const check=(v,m)=>{if(!v)throw Error(m);};
      const cache=new GridSceneCache(()=>{}), data=window.fixtureGetData(), visuals=new Map(data.visuals.map(v=>[v.kind+':'+v.id,v]));
      let progress=0;const onProgress=p=>{if(p)progress++;};
      const layers={tile:true,entity:true,decal:true};
      const small=data.items.filter(i=>i.x<40&&i.y<40);
      await cache.prepare(model.indexItems(small),layers,visuals,onProgress,()=>false);
      const count=cache.stats.rasterized;
      await cache.prepare(model.indexItems(small.map(i=>({...i}))),layers,new Map(visuals),onProgress,()=>false);
      check(cache.stats.rasterized===count,'Cloned unchanged draft rebuilt chunks');
      // A budget/LOD change must not rebuild unchanged source sprites.
      for (const chunk of cache.chunks.values()) {
        for (const [size, level] of chunk.levels) if (size > 1) { level.width=level.height=0; chunk.levels.delete(size); }
        chunk.overview=1;
      }
      await cache.prepare(model.indexItems(small),layers,visuals,onProgress,()=>false);
      check(cache.stats.rasterized===count,'Overview budget change rebuilt untouched chunks');
      const changed=small.map(i=>i.x===10&&i.y===10?{...i,prototype:'FloorSteel'}:i);
      await cache.prepare(model.indexItems(changed),layers,visuals,onProgress,()=>false);
      check(cache.stats.rasterized===count+1,'One tile change did not rebuild exactly one chunk');
      const removal=changed.filter(i=>i.x!==10||i.y!==10);
      await cache.prepare(model.indexItems(removal),layers,visuals,onProgress,()=>false);
      check(cache.stats.rasterized===count+2,'Deletion did not rebuild exactly one chunk');
      const crossings=itemChunks({kind:'decal',x:15.9,y:15.9,rotation:45});
      check(crossings.length===4,'Rotated fractional decal misses chunk boundary');
      // Compare a rotated tinted decal crossing four chunk edges to an unclipped
      // reference canvas. This catches clipping, coordinates, tint and alpha loss.
      const decalVisuals=new Map([['decal:Square',{images:[{url:'data:image/svg+xml;base64,'+btoa('<svg xmlns="http://www.w3.org/2000/svg" width="32" height="32"><rect width="32" height="32" fill="white"/></svg>')}]}]]);
      let pixelDifferences=0;
      for(const origin of [15.9,-.1]) {
        const decal={id:'decal',kind:'decal',prototype:'Square',x:origin,y:origin,rotation:45,z:0,color:'#ff0044'};
        const pixelCache=new GridSceneCache(()=>{});
        await pixelCache.prepare(model.indexItems([decal]),layers,decalVisuals,()=>{},()=>false);
        for(const [key,chunk] of pixelCache.chunks)pixelCache.details.set(key,await createImageBitmap(chunk.blob));
        const actual=document.createElement('canvas'),expected=document.createElement('canvas');actual.width=expected.width=160;actual.height=expected.height=160;
        const start=Math.floor(origin)-2,area={x:start,y:start,width:5,height:5};
        pixelCache.paint(actual,area,{left:0,top:0,width:160,height:160},32);
        const ctx=expected.getContext('2d');ctx.translate((origin+.5-start)*32,(start+5-origin-.5)*32);ctx.rotate(-Math.PI/4);ctx.globalAlpha=.85;ctx.fillStyle='#ff0044';ctx.fillRect(-16,-16,32,32);
        const a=actual.getContext('2d').getImageData(0,0,160,160).data,b=ctx.getImageData(0,0,160,160).data;
        for(let i=0;i<a.length;i++)if(Math.abs(a[i]-b[i])>2)pixelDifferences++;
        pixelCache.dispose();
      }
      check(pixelDifferences<50,'Chunk clipping changed decal pixels: '+pixelDifferences);
      // Native map sprites must keep their natural size and engine origin across chunk edges.
      let nativeSpriteDifferences=0;
      const lampUrl='data:image/svg+xml;base64,'+btoa('<svg xmlns="http://www.w3.org/2000/svg" width="32" height="64"><rect width="32" height="64" fill="white"/></svg>');
      const lampVisual={images:[{url:lampUrl,native:true,width:32,height:64,m11:1,m12:0,m21:0,m22:1,m31:0,m32:1}]};
      for(const rotation of [0,90]){
        const lamp={id:'lamp',kind:'entity',prototype:'Lamp',x:15.5,y:15.5,rotation};
        const lampCache=new GridSceneCache(()=>{});await lampCache.prepare(model.indexItems([lamp]),layers,new Map([['entity:Lamp',lampVisual]]),()=>{},()=>false);
        for(const [key,chunk]of lampCache.chunks)lampCache.details.set(key,await createImageBitmap(chunk.blob));
        const actual=document.createElement('canvas'),expected=document.createElement('canvas');actual.width=expected.width=160;actual.height=expected.height=160;
        const area=rotation===0?{x:14,y:14,width:5,height:5}:{x:12,y:12,width:5,height:5};
        lampCache.paint(actual,area,{left:0,top:0,width:160,height:160},32);
        const ctx=expected.getContext('2d');ctx.fillStyle='white';
        if(rotation===0)ctx.fillRect(32,48,32,64);else ctx.fillRect(48,32,64,32);
        const a=actual.getContext('2d').getImageData(0,0,160,160).data,b=ctx.getImageData(0,0,160,160).data;
        for(let i=0;i<a.length;i++)if(Math.abs(a[i]-b[i])>2)nativeSpriteDifferences++;
        lampCache.dispose();
      }
      check(nativeSpriteDifferences<10,'Native sprite origin/size differs from engine coordinates: '+nativeSpriteDifferences);

      await cache.prepare(model.indexItems(removal),{...layers,tile:false},visuals,onProgress,()=>false);
      check(cache.chunks.size===0,'Hidden layers retained chunks');
      const before=cache.stats.rasterized;
      await cache.prepare(model.indexItems(small),layers,visuals,onProgress,()=>true);
      check(cache.stats.rasterized===before,'Cancelled preparation rasterized map');
      cache.dispose();check(cache.chunks.size===0&&cache.details.size===0,'Cache not released');
      return {initialChunks:count,changedChunks:2,progressUpdates:progress,rotatedBoundaryChunks:crossings.length,pixelDifferences,nativeSpriteDifferences,cancelledAndDisposed:true};
    });
    await page.goto(url+'/');
    await page.waitForSelector('.GridEditor__loading',{state:'hidden',timeout:60000});
    await page.waitForTimeout(300);
    // Physical shortcuts work for DOM input and the native bridge alike.
    for(const [key,label] of [['1','Перемещение · 1'],['2','Кисть · 2'],['3','Линия · 3'],['e','Пипетка · E']]) {
      await page.keyboard.press(key); assert.ok((await page.getByRole('button',{name:label,exact:true}).getAttribute('class')).includes('active'));
    }
    await page.evaluate(()=>window.dispatchEvent(new CustomEvent('grid-editor-key',{detail:{code:'Digit1',key:'!',down:true}})));
    assert.ok((await page.getByRole('button',{name:'Перемещение · 1',exact:true}).getAttribute('class')).includes('active'));
    await page.keyboard.press('Escape');
    const point=async(x,y)=>page.evaluate(({x,y})=>{const c=document.querySelector('.GridEditor__canvas'),r=c.getBoundingClientRect(),d=window.fixtureGetData(),size=r.width/d.width;return{x:r.left+(x-d.x+.5)*size,y:r.top+(d.y+d.height-1-y+.5)*size};},{x,y});
    const a=await point(-5,5),b=await point(-3,3);
    await page.mouse.move(a.x,a.y);await page.mouse.down();await page.mouse.move(b.x,b.y,{steps:8});await page.mouse.up();
    assert.match(await page.locator('header').innerText(),/0 изменений/);
    assert.match(await page.locator('.GridEditor__properties').innerText(),/Выбрано/);
    const selected=await page.locator('.GridEditor__properties').innerText();
    assert.match(selected,/Выбрано: 14\n/);
    const end=await point(-2,2);
    const rasterBeforeDrag=await page.evaluate(()=>window.sceneCaches.reduce((n,c)=>n+c.stats.rasterized,0));
    await page.mouse.move(a.x,a.y);await page.mouse.down({button:'right'});await page.mouse.move(end.x,end.y,{steps:8});
    assert.match(await page.locator('header').innerText(),/0 изменений/);
    assert.equal(await page.evaluate(()=>window.sceneCaches.reduce((n,c)=>n+c.stats.rasterized,0)),rasterBeforeDrag);
    await page.mouse.up({button:'right'});
    assert.equal(await page.locator('.GridEditor__radial').count(),0);
    assert.doesNotMatch(await page.locator('header').innerText(),/0 изменений/);
    await page.keyboard.press('Control+z');
    assert.match(await page.locator('header').innerText(),/0 изменений/);
    await page.keyboard.press('Control+y');
    assert.doesNotMatch(await page.locator('header').innerText(),/0 изменений/);
    await page.keyboard.press('Control+z');
    await page.mouse.click(a.x,a.y,{button:'right'});
    await page.waitForSelector('.GridEditor__radial');
    await page.keyboard.press('Escape');
    await page.screenshot({path:path.join(output,'selection.png')});
    report.interaction={leftDragSelectsWithoutEditing:true,selectedItems:14,rightDragMovesWithoutMenu:true,dragOnlyCommitsOnRelease:true,undoRedo:true,rightClickMenu:true,shortcuts:true};
    // S selects an area; physical M reflects it once, D deletes it once.
    await page.goto(url+'/');
    await page.getByRole('button',{name:'Вписать',exact:true}).click();
    const selectCorner=async()=>{
      const start=await point(-5,5),end=await point(-3,3);
      await page.keyboard.down('s');await page.mouse.move(start.x,start.y);await page.mouse.down();
      await page.mouse.move(end.x,end.y,{steps:8});await page.mouse.up();await page.keyboard.up('s');
      assert.match(await page.locator('.GridEditor__properties').innerText(),/Выбрано: 14\n/);
    };
    await selectCorner();await page.keyboard.press('m');
    await page.waitForFunction(()=>window.fixtureActions.some(a=>a.action==='previewChunk'&&a.payload.items.some(i=>i.id==='e:-5:5'&&i.x===-2.5)));
    assert.match(await page.locator('.GridEditor__properties').innerText(),/Выбрано: 14\n/);
    await page.keyboard.press('Control+z');assert.match(await page.locator('header').innerText(),/0 изменений/);
    await page.keyboard.press('Control+y');assert.doesNotMatch(await page.locator('header').innerText(),/0 изменений/);
    await selectCorner();await page.keyboard.press('d');
    assert.doesNotMatch(await page.locator('.GridEditor__properties').innerText(),/Выбрано:/);
    await page.waitForFunction(()=>window.fixtureActions.some(a=>a.action==='previewChunk'&&a.payload.items.filter(i=>i.kind==='remove').length===14));
    await page.keyboard.press('Control+z');await selectCorner();
    // The game forwards physical codes regardless of the active keyboard layout.
    await page.evaluate(()=>window.dispatchEvent(new CustomEvent('grid-editor-key',{detail:{code:'KeyM',key:'ь',down:true}})));
    await page.evaluate(()=>window.dispatchEvent(new CustomEvent('grid-editor-key',{detail:{code:'KeyD',key:'в',down:true}})));
    assert.doesNotMatch(await page.locator('.GridEditor__properties').innerText(),/Выбрано:/);
    await page.keyboard.press('Control+z');await selectCorner();
    await page.getByRole('textbox',{name:'Поиск прототипов'}).fill('m d');
    assert.match(await page.locator('.GridEditor__properties').innerText(),/Выбрано: 14\n/);
    report.selectionKeys={heldS:true,reflectM:true,deleteD:true,undoRedo:true,nativeBridge:true,textInputProtected:true};
    await page.goto(url+'/');await page.getByRole('button',{name:'Вписать',exact:true}).click();
    await page.waitForSelector('.GridEditor__loading',{state:'hidden',timeout:60000});
    // Hovering S copies only the visible cup above its table, never the floor/table below.
    const cupPoint=await point(2,1);await page.mouse.move(cupPoint.x,cupPoint.y);await page.keyboard.press('s');
    await page.waitForFunction(()=>document.querySelector('.GridEditor__properties').textContent.includes('Выбрано: 1'));
    assert.match(await page.locator('.GridEditor__properties').innerText(),/Выбрано: 1/);
    assert.match(await page.locator('.GridEditor__properties').innerText(),/FixtureCup/);
    const silhouette=await page.evaluate(async point=>{
      const canvas=document.querySelector('.GridEditor__selection'),rect=canvas.getBoundingClientRect();
      const x=Math.round(point.x-rect.left),y=Math.round(point.y-rect.top),ctx=canvas.getContext('2d');
      for(let i=0;i<20&&ctx.getImageData(x,y,1,1).data[3]===0;i++)await new Promise(r=>requestAnimationFrame(r));
      return {center:[...ctx.getImageData(x,y,1,1).data],corner:[...ctx.getImageData(x+14,y+14,1,1).data]};
    },cupPoint);
    assert.ok(silhouette.center[1]>silhouette.center[0]&&silhouette.center[3]>0);
    assert.equal(silhouette.corner[3],0,'Transparent sprite corners must remain unselected');
    assert.equal(await page.locator('.GridEditor__cursor').count(),0);
    await page.screenshot({path:path.join(output,'green-silhouette.png')});
    const copyCount=await page.evaluate(()=>window.fixtureActions.filter(a=>a.action==='copySelection').length);assert.ok(copyCount>0);
    const pastePoint=await point(3,1);await page.mouse.move(pastePoint.x,pastePoint.y);
    await page.evaluate(()=>window.dispatchEvent(new CustomEvent('grid-editor-key',{detail:{code:'KeyV',ctrlKey:true,down:true}})));
    await page.waitForFunction(()=>window.fixtureActions.some(a=>a.action==='previewChunk'&&a.payload.items.some(i=>i.prototype==='FixtureCup'&&i.id.startsWith('new_'))));
    await page.keyboard.press('Control+z');
    // Draw and adjust a cable route, finish to draft, then reopen the existing applied network.
    await page.getByRole('button',{name:'Трасса · 4',exact:true}).click();
    for(const [x,y] of [[-2,0],[0,0],[0,2]]){const p=await point(x,y);await page.mouse.click(p.x,p.y);}
    assert.equal(await page.getByRole('button',{name:/Точка трассы/}).count(),5);
    const handle=page.getByRole('button',{name:'Точка трассы 2',exact:true});const box=await handle.boundingBox();const target=await point(1,0);
    await page.mouse.move(box.x+box.width/2,box.y+box.height/2);await page.mouse.down();await page.mouse.move(target.x,target.y,{steps:8});await page.mouse.up();
    assert.equal(await handle.getAttribute('title'),'1, 0');
    await page.screenshot({path:path.join(output,'route-handles.png')});
    const observer=await page.context().newPage();await observer.goto(url+'/?peer=RouteObserver');
    await observer.waitForFunction(()=>window.fixtureGetData().peerDrafts?.some(peer=>peer.items.some(i=>i.prototype==='CableHV')));
    assert.equal(await observer.evaluate(()=>window.fixtureGetData().items.some(i=>i.prototype==='CableHV')),false);
    await observer.close();
    await page.getByRole('button',{name:'Трассу в черновик · Enter',exact:true}).click();
    await page.getByRole('button',{name:'Применить',exact:true}).click();
    await page.waitForFunction(()=>window.fixtureGetData().items.some(i=>i.prototype==='CableHV'));
    const oldCable=await page.evaluate(()=>window.fixtureGetData().items.filter(i=>i.prototype==='CableHV').map(i=>({id:i.id,x:i.x,y:i.y})));
    const existingPoint=await point(-2,0);await page.mouse.click(existingPoint.x,existingPoint.y);
    assert.ok(await page.getByRole('button',{name:/Точка трассы/}).count()>=2);
    const routeEnd=page.getByRole('button',{name:'Точка трассы 1',exact:true});const endBox=await routeEnd.boundingBox();const movedEnd=await point(-2,-1);
    await page.mouse.move(endBox.x+endBox.width/2,endBox.y+endBox.height/2);await page.mouse.down();await page.mouse.move(movedEnd.x,movedEnd.y,{steps:6});await page.mouse.up();
    await page.getByRole('button',{name:'Трассу в черновик · Enter',exact:true}).click();
    await page.getByRole('button',{name:'Применить',exact:true}).click();
    await page.waitForFunction(()=>window.fixtureGetData().items.some(i=>i.prototype==='CableHV'&&i.y===-.5));
    await page.getByRole('checkbox',{name:'Показать сети поверх пола',exact:true}).uncheck();
    assert.equal(await page.locator('.GridEditor__networks').count(),0);
    await page.getByRole('checkbox',{name:'Показать сети поверх пола',exact:true}).check();
    await page.waitForSelector('.GridEditor__networks');
    // Gas routes automatically choose the elbow and do not create another voltage cable.
    await page.getByRole('combobox',{name:'Режим трассы',exact:true}).selectOption('gas');
    await page.getByRole('combobox',{name:'Тип сети',exact:true}).selectOption('gas0');
    for(const [x,y]of [[2,0],[4,2]]){const p=await point(x,y);await page.mouse.click(p.x,p.y);}
    await page.getByRole('button',{name:'Трассу в черновик · Enter',exact:true}).click();
    await page.getByRole('button',{name:'Применить',exact:true}).click();
    await page.waitForFunction(()=>window.fixtureGetData().items.some(i=>i.prototype==='GasPipeBend'));
    await page.getByRole('combobox',{name:'Режим трассы',exact:true}).selectOption('disposal');
    for(const [x,y]of [[-3,-1],[-1,1]]){const p=await point(x,y);await page.mouse.click(p.x,p.y);}
    assert.equal(await page.getByRole('button',{name:/Точка трассы/}).count(),5);
    await page.getByRole('button',{name:'Трассу в черновик · Enter',exact:true}).click();
    await page.getByRole('button',{name:'Применить',exact:true}).click();
    await page.waitForFunction(()=>window.fixtureGetData().items.some(i=>i.prototype==='DisposalBend'));
    report.routes={newCable:true,draggablePoints:true,existingCableEditable:true,visibilitySwitch:true,gasElbow:true,disposalElbow:true,perCellHandles:true,hoverSCopiesTopEntity:true,greenSpriteSilhouette:true,livePeerRoutePreview:true};
    await page.goto(url+'/');await page.getByRole('button',{name:'Вписать',exact:true}).click();
    // Resize the empty work area, apply without replacing the scene, then edit one tile.
    await page.getByRole('button', {name:'Вписать',exact:true}).click();
    const originalWidth = await page.locator('.GridEditor__canvas').evaluate(c => c.style.width);
    const edge = page.getByRole('button', {name:'Растянуть grid: right',exact:true});
    const bounds = await edge.boundingBox();
    const rasterBeforeResize = await page.evaluate(() => window.sceneCaches.reduce((n,c)=>n+c.stats.rasterized,0));
    await page.mouse.move(bounds.x+bounds.width/2,bounds.y+bounds.height/2);
    await page.mouse.down(); await page.mouse.move(bounds.x+65,bounds.y+bounds.height/2,{steps:8}); await page.mouse.up();
    assert.notEqual(await page.locator('.GridEditor__canvas').evaluate(c => c.style.width), originalWidth);
    await page.keyboard.press('Control+z');
    assert.equal(await page.locator('.GridEditor__canvas').evaluate(c => c.style.width), originalWidth);
    await page.keyboard.press('Control+y');
    await page.getByRole('button',{name:'Применить',exact:true}).click();
    await page.waitForFunction(() => window.fixtureGetData().patchVersion === 1);
    await page.waitForTimeout(500);
    assert.equal(await page.evaluate(()=>window.sceneCaches.reduce((n,c)=>n+c.stats.rasterized,0)),rasterBeforeResize);
    assert.equal(await page.evaluate(()=>window.fixtureGetData().token),'fixture');
    const cacheCount = await page.evaluate(()=>window.sceneCaches.length);
    const rasterBeforePatch = await page.evaluate(()=>window.sceneCaches.reduce((n,c)=>n+c.stats.rasterized,0));
    await page.evaluate(()=>window.fixtureSetData(d=>({...d,patchVersion:d.patchVersion+1,patchApplied:false,
      patchItems:[{...d.items.find(i=>i.id==='t:0:0'),prototype:'FloorSteel'}],patchRemoved:[]})));
    await page.waitForFunction(before=>window.sceneCaches.reduce((n,c)=>n+c.stats.rasterized,0)>before,rasterBeforePatch);
    await page.waitForTimeout(300);
    assert.equal(await page.evaluate(()=>window.sceneCaches.length),cacheCount);
    assert.equal(await page.evaluate(()=>window.sceneCaches.reduce((n,c)=>n+c.stats.rasterized,0)),rasterBeforePatch+1);
    report.resizeAndPatch={mouseDrag:true,undoRedo:true,emptyResizeRasterizations:0,sameSession:true,sameScene:true,singleTileChangedChunks:1};
    await page.screenshot({path:path.join(output,'resize.png')});
    await page.getByRole('checkbox', {name:'Диагностика утечек',exact:true}).check();
    await page.evaluate(()=>window.fixtureSetData(d=>({...d,atmosToken:d.token,atmosPatchVersion:d.patchVersion||0,
      atmosVersion:1,atmosSealed:1,atmosLeaking:2,atmosFilled:0,atmosCells:[
        {x:0,y:0,status:'sealed',region:1,leaks:0,pressure:101.3},
        {x:1,y:0,status:'leaking',region:2,leaks:0,pressure:0},
        {x:2,y:0,status:'leaking',region:2,leaks:4,pressure:0},
      ]})));
    await page.waitForSelector('.GridEditor__atmos');
    assert.match(await page.locator('.GridEditor__properties').innerText(),/Герметичных клеток: 1/);
    assert.equal(await page.getByRole('button',{name:'Выход наружу: 2, 0',exact:true}).count(),1);
    const beforeAtmosFill=await page.evaluate(()=>window.sceneCaches.reduce((n,c)=>n+c.stats.rasterized,0));
    await page.getByRole('button',{name:'Заполнить герметичные области',exact:true}).click();
    assert.equal(await page.evaluate(()=>window.fixtureActions.some(entry=>entry.action==='fillAtmos')),true);
    await page.evaluate(()=>window.fixtureSetData(d=>({...d,atmosVersion:2,atmosFilled:1})));
    await page.waitForTimeout(300);
    assert.equal(await page.evaluate(()=>window.sceneCaches.reduce((n,c)=>n+c.stats.rasterized,0)),beforeAtmosFill);
    await page.screenshot({path:path.join(output,'atmos-diagnostics.png')});
    await page.evaluate(()=>{window.fixtureAtmosCanvas=document.querySelector('.GridEditor__atmos');window.fixtureDiagnoses=window.fixtureActions.filter(a=>a.action==='diagnoseAtmos').length;});
    for(let i=0;i<4;i++){
      await page.evaluate(()=>window.fixtureSetData(d=>({...d,patchVersion:(d.patchVersion||0)+1,patchApplied:false,patchItems:[],patchRemoved:[]})));
      await page.waitForTimeout(120);
      assert.equal(await page.evaluate(()=>document.querySelector('.GridEditor__atmos')===window.fixtureAtmosCanvas),true);
      assert.equal(await page.evaluate(()=>window.fixtureActions.filter(a=>a.action==='diagnoseAtmos').length),await page.evaluate(()=>window.fixtureDiagnoses));
    }
    await page.waitForFunction(()=>window.fixtureActions.filter(a=>a.action==='diagnoseAtmos').length===window.fixtureDiagnoses+1);
    assert.equal(await page.evaluate(()=>document.querySelector('.GridEditor__atmos')===window.fixtureAtmosCanvas),true);
    const continuousBefore=await page.evaluate(()=>window.fixtureActions.filter(a=>a.action==='diagnoseAtmos').length);
    for(let i=0;i<12;i++){
      await page.evaluate(()=>window.fixtureSetData(d=>({...d,patchVersion:(d.patchVersion||0)+1,patchApplied:false,patchItems:[],patchRemoved:[]})));
      await page.waitForTimeout(200);
      assert.equal(await page.evaluate(()=>document.querySelector('.GridEditor__atmos')===window.fixtureAtmosCanvas),true);
    }
    assert.ok(await page.evaluate(()=>window.fixtureActions.filter(a=>a.action==='diagnoseAtmos').length)>continuousBefore,'Continuous edits must not starve atmospheric refresh');
    // New diagnostics replace the colours on the same canvas after the burst settles.
    await page.evaluate(()=>window.fixtureSetData(d=>({...d,atmosVersion:3,atmosPatchVersion:d.patchVersion,atmosCells:d.atmosCells.map(c=>({...c,pressure:100}))})));
    await page.waitForTimeout(100);
    assert.equal(await page.evaluate(()=>document.querySelector('.GridEditor__atmos')===window.fixtureAtmosCanvas),true);
    await page.locator('button[title="FloorSteel"]').click();const overlayEdit=await point(1,1);await page.mouse.click(overlayEdit.x,overlayEdit.y);
    assert.equal(await page.evaluate(()=>document.querySelector('.GridEditor__atmos')===window.fixtureAtmosCanvas),true);
    await page.keyboard.press('Control+z');
    report.atmos={overlay:true,leakCoordinates:true,fillAction:true,mapChunkRebuilds:0,noFlickerAcrossPatches:true,visibleDuringLocalEditing:true,debouncedRefresh:true,refreshDuringContinuousEdits:true};
    await page.goto(url+'/?source');await page.waitForSelector('.GridEditor__sourcePath');
    assert.equal(await page.locator('.GridEditor__sourcePath').innerText(),'Resources/Maps/Test/empty.yml');
    await page.evaluate(()=>window.fixtureSetData(d=>({...d,patchVersion:1,patchApplied:false,patchItems:[],patchRemoved:[]})));
    assert.equal(await page.locator('.GridEditor__sourcePath').innerText(),'Resources/Maps/Test/empty.yml');
    report.mapSource={exactRepositoryPath:true,retainedAcrossPatches:true};
    // Two actual browser windows share previews and world deltas, never preferences.
    await page.goto(url+'/?peer=MapperA');
    const peer=await page.context().newPage();peer.on('pageerror',e=>errors.push(e.message));
    await peer.goto(url+'/?peer=MapperB');
    await page.getByRole('button',{name:'Вписать',exact:true}).click();
    await peer.getByRole('button',{name:'Вписать',exact:true}).click();
    await page.getByRole('button',{name:'Добавить в избранное Стальной пол',exact:true}).click();
    await peer.getByRole('button',{name:'Добавить в избранное Обшивка',exact:true}).click();
    await page.getByRole('textbox',{name:'Поиск прототипов'}).fill('FloorSteel');
    await peer.getByRole('textbox',{name:'Поиск прототипов'}).fill('Plating');
    await page.locator('button[title="FloorSteel"]').click();
    const cell=await point(0,0);await page.mouse.click(cell.x,cell.y);
    await peer.waitForFunction(()=>window.fixtureGetData().peerDrafts?.some(p=>p.name==='MapperA'&&p.items.some(i=>i.id==='t:0:0'&&i.prototype==='FloorSteel')));
    await peer.waitForSelector('.GridEditor__peerLayer');
    assert.match(await peer.locator('header').innerText(),/0 изменений/);
    assert.equal(await peer.evaluate(()=>window.fixtureGetData().items.find(i=>i.id==='t:0:0').prototype),'Plating');
    await peer.screenshot({path:path.join(output,'foreign-draft.png')});
    // B edits a different cell while A commits. B's pending edit must survive.
    await peer.getByRole('textbox',{name:'Поиск прототипов'}).fill('FloorSteel');
    await peer.locator('button[title="FloorSteel"]').click();
    const peerCell=await peer.evaluate(()=>{const c=document.querySelector('.GridEditor__canvas'),r=c.getBoundingClientRect(),d=window.fixtureGetData(),size=r.width/d.width;return{x:r.left+(1-d.x+.5)*size,y:r.top+(d.y+d.height-.5)*size};});
    await peer.mouse.click(peerCell.x,peerCell.y);
    await peer.getByRole('textbox',{name:'Поиск прототипов'}).fill('Plating');
    await page.waitForFunction(()=>window.fixtureGetData().peerDrafts?.some(p=>p.name==='MapperB'&&p.items.some(i=>i.id==='t:1:0')));
    await page.getByRole('button',{name:'Применить',exact:true}).click();
    await peer.waitForFunction(()=>window.fixtureGetData().items.find(i=>i.id==='t:0:0').prototype==='FloorSteel');
    assert.doesNotMatch(await peer.locator('header').innerText(),/0 изменений/);
    assert.equal(await page.getByRole('textbox',{name:'Поиск прототипов'}).inputValue(),'FloorSteel');
    assert.equal(await peer.getByRole('textbox',{name:'Поиск прототипов'}).inputValue(),'Plating');
    assert.deepEqual(await page.evaluate(()=>window.fixtureGetData().favorites.map(f=>f.id)),['FloorSteel']);
    assert.deepEqual(await peer.evaluate(()=>window.fixtureGetData().favorites.map(f=>f.id)),['Plating']);
    // Undo clears B's preview without rolling back A's committed tile.
    await peer.locator('.GridEditor__canvas').focus();await peer.keyboard.press('Control+z');
    assert.match(await peer.locator('header').innerText(),/0 изменений/);
    await page.waitForFunction(()=>window.fixtureGetData().peerDrafts?.every(p=>p.name!=='MapperB'||p.items.length===0));
    await peer.keyboard.press('Control+y');await peer.getByRole('button',{name:'Применить',exact:true}).click();
    await page.waitForFunction(()=>window.fixtureGetData().items.find(i=>i.id==='t:1:0').prototype==='FloorSteel');
    assert.equal(await page.evaluate(()=>window.fixtureGetData().items.find(i=>i.id==='t:0:0').prototype),'FloorSteel');
    report.collaboration={twoWindows:true,liveUnsavedPreview:true,previewDoesNotApply:true,localEditsSurvivePeerApply:true,undoKeepsExternalChanges:true,privateSearchAndFavorites:true};
    const resize=await page.getByRole('button',{name:'Растянуть grid: right',exact:true}).boundingBox();
    await page.mouse.move(resize.x+resize.width/2,resize.y+resize.height/2);
    await page.mouse.down();await page.mouse.move(resize.x+65,resize.y+resize.height/2,{steps:8});await page.mouse.up();
    await peer.waitForFunction(()=>window.fixtureGetData().peerDrafts?.some(p=>p.name==='MapperA'&&p.expansion?.right>0));
    assert.match(await peer.locator('.GridEditor__properties').innerText(),/растягивание:/);
    report.collaboration.liveResizePreview=true;
    await peer.close();
    assert.deepEqual(errors,[]);
    fs.writeFileSync(path.join(output,'report.json'),JSON.stringify(report,null,2));
    console.log(JSON.stringify(report,null,2));
  } finally {await browser.close();server.close();}
}
main().catch(error=>{console.error(error);process.exitCode=1;});
