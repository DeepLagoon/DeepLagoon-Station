const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const assert = require('node:assert/strict');
const root = path.resolve(__dirname, '..').replaceAll('\\', '/');
const { build } = require(root + '/node_modules/esbuild');
const sass = require(root + '/node_modules/sass');
const { chromium } = require(process.env.PLAYWRIGHT_PATH || 'playwright');
const output = process.env.GRID_EDITOR_OUTPUT || path.join(root, 'node_modules/.cache/grid-editor-browser'); fs.mkdirSync(output, { recursive: true });
const portable = p => p.replaceAll('\\', '/');
async function main() {
  const preview = fs.readFileSync(root + '/tools/grid-editor-preview.mjs', 'utf8');
  let source = preview.slice(preview.indexOf('const source = `') + 16, preview.indexOf('`;\nfs.writeFileSync') > 0 ? preview.indexOf('`;\nfs.writeFileSync') : preview.indexOf('`;\r\nfs.writeFileSync'));
  source = source.replace("'../packages/tgui/interfaces/GridEditor.jsx'", JSON.stringify(root + '/packages/tgui/interfaces/GridEditor.jsx')).replace("'../packages/tgui/interfaces/gridEditorRouting.mjs'",JSON.stringify(root+'/packages/tgui/interfaces/gridEditorRouting.mjs'));
  source += `\nimport { GridSceneCache, itemChunks } from '${root}/packages/tgui/interfaces/gridEditorSceneCache.mjs';
import * as model from '${root}/packages/tgui/interfaces/gridEditorModel.mjs';
import {tintSprite} from '${root}/packages/tgui/interfaces/gridEditorTint.mjs';
window.testing = {GridSceneCache, itemChunks, model, tintSprite};
window.sceneTimings = []; window.sceneCaches = [];
const paint = GridSceneCache.prototype.paint, prepare = GridSceneCache.prototype.prepare;
GridSceneCache.prototype.paint = function(...args) { const start=performance.now(); const result=paint.apply(this,args); window.sceneTimings.push(performance.now()-start); return result; };
GridSceneCache.prototype.prepare = async function(...args) {
  if(!window.sceneCaches.includes(this))window.sceneCaches.push(this);
  window.preparingScenes=(window.preparingScenes||0)+1;
  try {
  const delay=Number(new URLSearchParams(location.search).get('delay')||0);
  if(delay){args[3]({label:'Проверочная загрузка',done:0,total:2});await new Promise(r=>setTimeout(r,delay/2));if(args[4]())return;args[3]({label:'Проверочная загрузка',done:1,total:2});await new Promise(r=>setTimeout(r,delay/2));if(args[4]())return;}
  return await prepare.apply(this,args);
  } finally {window.preparingScenes--;}
};
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
    await page.addInitScript(()=>document.addEventListener('dragstart',event=>event.preventDefault(),true));
    page.on('pageerror',e=>errors.push(e.message));
    const url='http://127.0.0.1:'+server.address().port;
    const report={};
    const openCatalog=async target=>{if(!await target.locator('.GridEditor__catalog').isVisible())await target.getByRole('button',{name:'Каталог',exact:true}).click();};
    const openPanel=async(target,name)=>{const button=target.locator('.GridEditor__sections').getByRole('button',{name,exact:true});if(await button.getAttribute('aria-expanded')!=='true')await button.click();};
    await page.goto(url+'/');
    await page.waitForFunction(()=>window.sceneCaches?.some(cache=>cache.stats.rasterized>0));
    await page.waitForSelector('.GridEditor__loading',{state:'hidden',timeout:60000});
    assert.equal(await page.locator('.GridEditor__catalog').isVisible(),false);
    assert.equal(await page.locator('.GridEditor__properties').isVisible(),false);
    for(const width of [1440,736,320]){
      await page.setViewportSize({width,height:900});
      const map=await page.locator('.GridEditor__viewport').boundingBox();
      assert.ok(map.width>=width-54,'Focus layout must reserve only the tool rail');
      await openCatalog(page);
      assert.deepEqual(await page.locator('.GridEditor__viewport').boundingBox(),map,'Catalog must overlay the map without moving it');
      await page.getByRole('button',{name:'Закрыть каталог',exact:true}).click();
      for(const name of ['Свойства','Атмосфера','Сети','Зеркало','Подложка','Слои','Шаблоны','Грид','Файлы']){
        await openPanel(page,name);
        assert.equal(await page.locator('.GridEditor__properties').isVisible(),true);
        assert.deepEqual(await page.locator('.GridEditor__viewport').boundingBox(),map,'Settings must not move the map');
        const panel=await page.locator('.GridEditor__properties').boundingBox();assert.ok(panel.x>=0&&panel.x+panel.width<=width);
        assert.doesNotMatch(await page.locator('.GridEditor__properties').innerText(),/Тяните|клик по|нажмите место|автоматически|сохраняется сразу|Примените черновик|после завершения/i,'Panels must contain controls and live data without usage instructions');
        await page.getByRole('button',{name:'Вписать',exact:true}).click({trial:true});
        await page.getByRole('button',{name:'Закрыть панель',exact:true}).click();
      }
      const atmosphere=await page.locator('.GridEditor__sections').getByRole('button',{name:'Атмосфера',exact:true}).boundingBox();
      const apply=await page.getByRole('button',{name:'Применить',exact:true}).boundingBox();
      assert.ok(atmosphere.x>=0&&atmosphere.x+atmosphere.width<=width);
      assert.ok(apply.x>=0&&apply.x+apply.width<=width&&apply.y+apply.height<=900);
      assert.equal(await page.locator('.GridEditor').evaluate(e=>e.scrollWidth>e.clientWidth),false);
      await page.screenshot({path:path.join(output,`focus-${width}.png`)});
    }
    await page.setViewportSize({width:1440,height:900});
    const slots=page.locator('.GridEditor__quickEntries > button');
    await page.waitForFunction(()=>document.querySelectorAll('.GridEditor__quickEntries > button').length>8);
    const wideCount=await slots.count();assert.ok(wideCount>8);
    await page.evaluate(()=>window.fixtureSetData(data=>({...data,favorites:[{kind:'tile',id:'FloorSteel'}]})));
    assert.equal(await slots.locator('img,canvas').count(),0,'Favorites must not populate an empty hotbar');
    assert.equal(await page.getByText('Изменения отправляются кнопкой «Применить»',{exact:true}).count(),0);
    await openCatalog(page);
    const dragSource=await page.locator('.GridEditor__catalog button[title="FloorSteel"]').boundingBox(),dropTarget=await slots.nth(1).boundingBox();
    await page.mouse.move(dragSource.x+dragSource.width/2,dragSource.y+dragSource.height/2);await page.mouse.down();
    await page.mouse.move(dropTarget.x+dropTarget.width/2,dropTarget.y+dropTarget.height/2,{steps:12});
    assert.equal(await page.locator('.GridEditor__draggedBrush').count(),1);
    assert.equal(await slots.nth(1).evaluate(button=>button.classList.contains('drop')),true);
    await page.mouse.up();
    await page.waitForFunction(()=>window.fixtureGetData().hotbar?.some(entry=>entry.slot===1&&entry.id==='FloorSteel'));
    assert.equal(await page.locator('.GridEditor__catalog').isVisible(),true,'Dragging must not select the catalog brush or close its drawer');
    await slots.nth(1).click();
    assert.equal(await page.locator('.GridEditor__catalog').isVisible(),false);
    assert.equal(await page.locator('.GridEditor__quickAccess').getByRole('button',{name:'Слот 2: Стальной пол',exact:true}).count(),1);
    const quickBrush=await slots.nth(1).boundingBox();
    assert.ok(quickBrush.width>=38&&quickBrush.height>=38,'Quick access must display the sprite at a usable size');
    const image=await slots.nth(1).locator('img').boundingBox();
    assert.ok(image.x>=quickBrush.x&&image.y>=quickBrush.y&&image.x+image.width<=quickBrush.x+quickBrush.width&&image.y+image.height<=quickBrush.y+quickBrush.height);
    assert.ok(Math.abs(image.x+image.width/2-quickBrush.x-quickBrush.width/2)<1,'Thumbnail must be centered in its own slot');
    await page.setViewportSize({width:736,height:900});
    await page.waitForFunction(count=>document.querySelectorAll('.GridEditor__quickEntries > button').length<count,wideCount);
    assert.ok(await slots.count()>8);
    await page.setViewportSize({width:1440,height:900});
    await page.waitForFunction(count=>document.querySelectorAll('.GridEditor__quickEntries > button').length===count,wideCount);
    assert.equal(await slots.nth(1).getAttribute('aria-label'),'Слот 2: Стальной пол');
    await page.screenshot({path:path.join(output,'hotbar-populated.png')});
    await slots.nth(1).click({button:'right'});
    await page.waitForFunction(()=>!window.fixtureGetData().hotbar.some(entry=>entry.slot===1));
    await openCatalog(page);await page.locator('.GridEditor__catalog button[title="FloorSteel"]').click();
    await page.screenshot({path:path.join(output,'hotbar.png')});
    report.hotbar={slotsByWindowWidth:true,wideCount,catalogPointerDragDropWithNativeDragDisabled:true,initiallyEmptyWithFavorites:true,selection:true,clearSlot:true,thumbnailCentered:true,hintRemoved:true};
    report.focusLayout={defaultFullMap:true,floatingPanelsKeepCamera:true,allSectionsAccessible:true,atmosphereAlwaysVisible:true,quickBrushes:true,widths:[1440,736,320]};
    await openPanel(page,'Грид');
    const gridName='Жилой модуль «Маяк»: палуба 2 #1';
    await page.getByRole('textbox',{name:'Название грида',exact:true}).fill(gridName);
    await page.getByRole('textbox',{name:'Название грида',exact:true}).press('Enter');
    await page.waitForFunction(name=>window.fixtureGetData().gridName===name,gridName);
    assert.ok((await page.locator('header').innerText()).includes(gridName));
    assert.equal(await page.getByRole('button',{name:'Сохранить название',exact:true}).isDisabled(),true);
    await page.getByRole('button',{name:'Вписать',exact:true}).click();
    await page.screenshot({path:path.join(output,'grid-name.png')});
    await page.getByRole('textbox',{name:'Название грида',exact:true}).fill('Несохранённое имя');
    await page.evaluate(()=>window.fixtureSetData(d=>({...d,alwaysPowered:true,gridName:'Имя другого маппера'})));
    assert.equal(await page.getByRole('textbox',{name:'Название грида',exact:true}).inputValue(),'Несохранённое имя');
    report.gridName={editable:true,enterSaves:true,headerUpdated:true,unrelatedAndPeerUpdatesPreserveInput:true};
    await page.setViewportSize({width:320,height:900});
    await page.evaluate(()=>window.fixtureSetData(d=>({...d,gridName:'Я'.repeat(128)})));
    await page.waitForFunction(()=>document.querySelector('.GridEditor__gridName').textContent.length===128);
    assert.equal(await page.locator('.GridEditor').evaluate(e=>e.scrollWidth>e.clientWidth),false);
    report.gridName.longNameFitsNarrowViewport=true;
    await page.setViewportSize({width:1440,height:900});
    for(const delay of [600,2800]){
      await page.goto(url+'/?delay='+delay);
      await page.waitForFunction(()=>window.sceneCaches?.length>0);
      await page.waitForTimeout(1100);
      assert.equal(await page.locator('progress').count(),0,'Progress must stay hidden during the first two seconds');
      if(delay>2000){await page.waitForSelector('progress',{timeout:2000});await page.screenshot({path:path.join(output,'delayed-progress.png')});}
      await page.waitForFunction(()=>window.sceneCaches.some(cache=>cache.stats.rasterized>0));
      await page.waitForSelector('.GridEditor__loading',{state:'hidden',timeout:10000});
    }
    report.progress={shortUpdateHidden:true,slowUpdateShownAfterTwoSeconds:true,intermediateUpdatesDoNotRestartDelay:true};
    if(process.env.GRID_EDITOR_LAYOUT_ONLY){fs.writeFileSync(path.join(output,'focus-report.json'),JSON.stringify(report,null,2));console.log(JSON.stringify(report,null,2));return;}
    for(const legacy of modes) {
      await page.goto(url+'/?large'+(legacy?'&legacy':''));
      await page.waitForFunction(()=>window.fixtureGetData?.().items.length===62500);
      if(!legacy) {
        await page.waitForFunction(()=>window.preparingScenes===0&&window.sceneCaches.some(c=>c.stats.rasterized>0),null,{timeout:60000});
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
        const ctx=expected.getContext('2d');ctx.translate((origin+.5-start)*32,(start+5-origin-.5)*32);ctx.rotate(-Math.PI/4);ctx.globalAlpha=1;ctx.fillStyle='#ff0044';ctx.fillRect(-16,-16,32,32);
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
    assert.match(await page.locator('.GridEditor__properties').textContent(),/Выбрано/);
    const selected=await page.locator('.GridEditor__properties').textContent();
    assert.match(selected,/Выбрано: 14/);
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
      assert.match(await page.locator('.GridEditor__properties').textContent(),/Выбрано: 14/);
    };
    await selectCorner();await page.keyboard.press('m');
    await page.waitForFunction(()=>window.fixtureActions.some(a=>a.action==='previewChunk'&&a.payload.items.some(i=>i.id==='e:-5:5'&&i.x===-2.5)));
    assert.match(await page.locator('.GridEditor__properties').textContent(),/Выбрано: 14/);
    await page.keyboard.press('Control+z');assert.match(await page.locator('header').innerText(),/0 изменений/);
    await page.keyboard.press('Control+y');assert.doesNotMatch(await page.locator('header').innerText(),/0 изменений/);
    await selectCorner();await page.keyboard.press('d');
    assert.doesNotMatch(await page.locator('.GridEditor__properties').textContent(),/Выбрано:/);
    await page.waitForFunction(()=>window.fixtureActions.some(a=>a.action==='previewChunk'&&a.payload.items.filter(i=>i.kind==='remove').length===14));
    await page.keyboard.press('Control+z');await selectCorner();
    // The game forwards physical codes regardless of the active keyboard layout.
    await page.evaluate(()=>window.dispatchEvent(new CustomEvent('grid-editor-key',{detail:{code:'KeyM',key:'ь',down:true}})));
    await page.evaluate(()=>window.dispatchEvent(new CustomEvent('grid-editor-key',{detail:{code:'KeyD',key:'в',down:true}})));
    assert.doesNotMatch(await page.locator('.GridEditor__properties').textContent(),/Выбрано:/);
    await page.keyboard.press('Control+z');await selectCorner();
    await openCatalog(page);await page.getByRole('textbox',{name:'Поиск прототипов'}).fill('m d');
    assert.match(await page.locator('.GridEditor__properties').textContent(),/Выбрано: 14/);
    report.selectionKeys={heldS:true,reflectM:true,deleteD:true,undoRedo:true,nativeBridge:true,textInputProtected:true};
    await page.goto(url+'/');await page.getByRole('button',{name:'Вписать',exact:true}).click();
    await page.waitForSelector('.GridEditor__loading',{state:'hidden',timeout:60000});
    // Hovering S copies only the visible cup above its table, never the floor/table below.
    const cupPoint=await point(2,1);await page.mouse.move(cupPoint.x,cupPoint.y);await page.keyboard.press('s');
    await page.waitForFunction(()=>document.querySelector('.GridEditor__properties').textContent.includes('Выбрано: 1'));
    assert.match(await page.locator('.GridEditor__properties').textContent(),/Выбрано: 1/);
    assert.match(await page.locator('.GridEditor__properties').textContent(),/FixtureCup/);
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
    const shrinkEdge = await edge.boundingBox();
    await page.mouse.move(shrinkEdge.x+shrinkEdge.width/2,shrinkEdge.y+shrinkEdge.height/2);
    await page.mouse.down(); await page.mouse.move(shrinkEdge.x-64,shrinkEdge.y+shrinkEdge.height/2,{steps:8}); await page.mouse.up();
    const smallerWidth = await page.locator('.GridEditor__canvas').evaluate(c => c.style.width);
    assert.ok(parseFloat(smallerWidth) < parseFloat(originalWidth));
    await page.keyboard.press('Control+z');
    assert.equal(await page.locator('.GridEditor__canvas').evaluate(c => c.style.width), originalWidth);
    await page.keyboard.press('Control+y');
    assert.equal(await page.locator('.GridEditor__canvas').evaluate(c => c.style.width), smallerWidth);
    await page.getByRole('button',{name:'Применить',exact:true}).click();
    await page.waitForFunction(() => window.fixtureGetData().patchVersion === 1);
    assert.equal(await page.locator('.GridEditor__canvas').evaluate(c => c.style.width), smallerWidth);
    await page.goto(url+'/'); await page.getByRole('button',{name:'Вписать',exact:true}).click();
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
    report.resizeAndPatch={mouseDrag:true,shrink:true,undoRedo:true,emptyResizeRasterizations:0,sameSession:true,sameScene:true,singleTileChangedChunks:1};
    await page.screenshot({path:path.join(output,'resize.png')});
    await openPanel(page,'Атмосфера');await page.getByRole('checkbox', {name:'Диагностика утечек',exact:true}).check();
    await page.evaluate(()=>window.fixtureSetData(d=>({...d,atmosToken:d.token,atmosPatchVersion:d.patchVersion||0,
      atmosVersion:1,atmosSealed:1,atmosLeaking:2,atmosFilled:0,atmosCells:[
        {x:0,y:0,status:'sealed',region:1,leaks:0,pressure:101.3},
        {x:1,y:0,status:'leaking',region:2,leaks:0,pressure:0},
        {x:2,y:0,status:'leaking',region:2,leaks:4,pressure:0},
      ]})));
    await page.waitForSelector('.GridEditor__atmos');
    assert.match(await page.locator('.GridEditor__properties').textContent(),/Герметичных клеток: 1/);
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
    await openCatalog(page);await page.locator('.GridEditor__catalog button[title="FloorSteel"]').click();const overlayEdit=await point(1,1);await page.mouse.click(overlayEdit.x,overlayEdit.y);
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
    await openCatalog(page);await openCatalog(peer);
    await page.getByRole('button',{name:'Добавить в избранное Стальной пол',exact:true}).click();
    await peer.getByRole('button',{name:'Добавить в избранное Обшивка',exact:true}).click();
    await page.getByRole('textbox',{name:'Поиск прототипов'}).fill('FloorSteel');
    await openCatalog(peer);await peer.getByRole('textbox',{name:'Поиск прототипов'}).fill('Plating');
    await page.locator('button[title="FloorSteel"]').click();
    const cell=await point(0,0);await page.mouse.click(cell.x,cell.y);
    await peer.waitForFunction(()=>window.fixtureGetData().peerDrafts?.some(p=>p.name==='MapperA'&&p.items.some(i=>i.id==='t:0:0'&&i.prototype==='FloorSteel')));
    await peer.waitForSelector('.GridEditor__peerLayer');
    assert.match(await peer.locator('header').innerText(),/0 изменений/);
    assert.equal(await peer.evaluate(()=>window.fixtureGetData().items.find(i=>i.id==='t:0:0').prototype),'Plating');
    await peer.screenshot({path:path.join(output,'foreign-draft.png')});
    // B edits a different cell while A commits. B's pending edit must survive.
    await openCatalog(peer);await peer.getByRole('textbox',{name:'Поиск прототипов'}).fill('FloorSteel');
    await peer.locator('button[title="FloorSteel"]').click();
    const peerCell=await peer.evaluate(()=>{const c=document.querySelector('.GridEditor__canvas'),r=c.getBoundingClientRect(),d=window.fixtureGetData(),size=r.width/d.width;return{x:r.left+(1-d.x+.5)*size,y:r.top+(d.y+d.height-.5)*size};});
    await peer.mouse.click(peerCell.x,peerCell.y);
    await openCatalog(peer);await peer.getByRole('textbox',{name:'Поиск прототипов'}).fill('Plating');
    await page.waitForFunction(()=>window.fixtureGetData().peerDrafts?.some(p=>p.name==='MapperB'&&p.items.some(i=>i.id==='t:1:0')));
    await page.getByRole('button',{name:'Применить',exact:true}).click();
    await peer.waitForFunction(()=>window.fixtureGetData().items.find(i=>i.id==='t:0:0').prototype==='FloorSteel');
    assert.doesNotMatch(await peer.locator('header').innerText(),/0 изменений/);
    assert.equal(await page.locator('.GridEditor__catalog input[aria-label="Поиск прототипов"]').inputValue(),'FloorSteel');
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
    assert.match(await peer.locator('.GridEditor__properties').textContent(),/растягивание:/);
    report.collaboration.liveResizePreview=true;
    await peer.close();
    // A peer-only creation must be selectable and deletable through the real UI.
    await page.goto(url+'/');
    await page.waitForSelector('.GridEditor__loading',{state:'hidden',timeout:60000});
    await page.getByRole('button',{name:'Вписать',exact:true}).click();
    await page.evaluate(()=>window.fixtureSetData(d=>({...d,peerDrafts:[{player:'remote',name:'Remote',expansion:{},removedItems:[],
      items:[{id:'new_peer_only',kind:'entity',prototype:'ChairWood',x:.5,y:.5,rotation:0,color:'#ffffff',z:0,cleanable:false,anchored:false,variant:0,flags:0,mirror:0}]}]})));
    await page.waitForTimeout(150);
    const remoteCell=await point(0,0);
    await page.keyboard.down('s');await page.mouse.click(remoteCell.x,remoteCell.y);await page.keyboard.up('s');
    await page.keyboard.press('d');
    await page.waitForFunction(()=>window.fixtureActions.some(a=>a.action==='previewChunk'&&a.payload.items.some(i=>i.id==='new_peer_only'&&i.kind==='remove')));
    await page.getByRole('button',{name:'Применить',exact:true}).click();
    await page.waitForFunction(()=>window.fixtureActions.some(a=>a.action==='applyChunk'&&a.payload.items.some(i=>i.id==='new_peer_only'&&i.kind==='remove')));
    report.collaboration.editAndApplyPeerCreation=true;
    // Decal mode must scope real UI operations, including history, to the selected Z layer.
    await page.goto(url+'/');
    await page.waitForSelector('.GridEditor__loading',{state:'hidden',timeout:60000});
    const originalWorld=await page.evaluate(()=>{
      const d=window.fixtureGetData(),base=d.items.filter(item=>item.kind!=='decal');
      const decal={id:'d:layer0',kind:'decal',prototype:'DecalCorner',x:0,y:0,rotation:90,color:'#80808080',z:0,cleanable:true,anchored:false};
      window.fixtureSetData({...d,token:'decal-layer-test',items:[...base,decal,{...decal,id:'d:layer2',z:2,color:'#ff0000ff'}],peerDrafts:[]});
      return base;
    });
    await page.waitForSelector('.GridEditor__loading',{state:'hidden',timeout:60000});
    await openCatalog(page);await page.getByRole('button',{name:'Декали',exact:true}).click();await openPanel(page,'Слои');
    await page.getByRole('button',{name:'Выделить слой',exact:true}).click();
    assert.match(await page.locator('.GridEditor__properties').textContent(),/Выбрано: 1/);
    await page.getByLabel('Цвет декали',{exact:true}).fill('#2080a0');
    await page.getByRole('button',{name:'Применить',exact:true}).click();
    await page.waitForFunction(()=>window.fixtureGetData().items.find(item=>item.id==='d:layer0')?.color==='#2080a080');
    assert.deepEqual(await page.evaluate(()=>window.fixtureGetData().items.filter(item=>item.kind!=='decal')),originalWorld);
    assert.equal(await page.evaluate(()=>window.fixtureGetData().items.find(item=>item.id==='d:layer2').color),'#ff0000ff');
    await page.getByRole('button',{name:'Слой 2 · 1',exact:true}).click();
    await page.getByRole('button',{name:'Удалить слой',exact:true}).click();
    await page.keyboard.press('Control+z');
    assert.match(await page.locator('[aria-label="Слои декалей"]').innerText(),/Слой 2 · 1/);
    await page.keyboard.press('Control+Shift+z');
    assert.match(await page.locator('[aria-label="Слои декалей"]').innerText(),/Слой 2 · 0/);
    await page.getByRole('button',{name:'Применить',exact:true}).click();
    await page.waitForFunction(()=>!window.fixtureGetData().items.some(item=>item.id==='d:layer2'));
    assert.deepEqual(await page.evaluate(()=>window.fixtureGetData().items.filter(item=>item.kind!=='decal')),originalWorld);
    assert.equal(await page.evaluate(()=>window.fixtureGetData().items.find(item=>item.id==='d:layer0').color),'#2080a080');
    report.decalTint=await page.evaluate(()=>{
      const source=document.createElement('canvas');source.width=source.height=1;
      const ctx=source.getContext('2d');const pixels=ctx.createImageData(1,1);pixels.data.set([128,64,32,128]);ctx.putImageData(pixels,0,0);
      const actual=window.testing.tintSprite(source,'#80402080').getContext('2d').getImageData(0,0,1,1).data;
      const expected=[64,16,4,64];
      if(expected.some((value,index)=>Math.abs(actual[index]-value)>2))throw Error('Tint must multiply texture RGB and alpha: '+actual);
      return [...actual];
    });
    report.decalLayers={colorAndAlpha:true,selectLayer:true,deleteLayer:true,scopedUndoRedo:true,worldProtected:true};
    await page.screenshot({path:path.join(output,'decal-layers.png')});
    assert.deepEqual(errors,[]);
    fs.writeFileSync(path.join(output,'report.json'),JSON.stringify(report,null,2));
    console.log(JSON.stringify(report,null,2));
  } finally {await browser.close();server.close();}
}
main().catch(error=>{console.error(error);process.exitCode=1;});
