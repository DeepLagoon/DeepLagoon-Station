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
  source = source.replace("'../packages/tgui/interfaces/GridEditor.jsx'", JSON.stringify(root + '/packages/tgui/interfaces/GridEditor.jsx'));
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
    const page=await browser.newPage({viewport:{width:1440,height:900}});
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
      await cache.prepare(model.indexItems(removal),{...layers,tile:false},visuals,onProgress,()=>false);
      check(cache.chunks.size===0,'Hidden layers retained chunks');
      const before=cache.stats.rasterized;
      await cache.prepare(model.indexItems(small),layers,visuals,onProgress,()=>true);
      check(cache.stats.rasterized===before,'Cancelled preparation rasterized map');
      cache.dispose();check(cache.chunks.size===0&&cache.details.size===0,'Cache not released');
      return {initialChunks:count,changedChunks:2,progressUpdates:progress,rotatedBoundaryChunks:crossings.length,pixelDifferences,cancelledAndDisposed:true};
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
    assert.deepEqual(errors,[]);
    fs.writeFileSync(path.join(output,'report.json'),JSON.stringify(report,null,2));
    console.log(JSON.stringify(report,null,2));
  } finally {await browser.close();server.close();}
}
main().catch(error=>{console.error(error);process.exitCode=1;});
