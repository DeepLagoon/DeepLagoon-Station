const fs = require('node:fs'), path = require('node:path'), http = require('node:http'), assert = require('node:assert/strict');
const root = path.resolve(__dirname, '..').replaceAll('\\', '/');
const { build } = require(root + '/node_modules/esbuild'), sass = require(root + '/node_modules/sass');
const { chromium } = require('playwright');
const output = process.env.GRID_EDITOR_OUTPUT || path.resolve(root, '../.local/codex/grid-editor-resources');
fs.mkdirSync(output, { recursive: true });

async function main() {
  const fixture = path.join(output, 'fixture.jsx').replaceAll('\\', '/');
  fs.writeFileSync(fixture, `
import React,{createContext,useState}from'react';import{createRoot}from'react-dom/client';
import{GridEditor}from'${root}/packages/tgui/interfaces/GridEditor.jsx';
import{gridEditorUpdate}from'${root}/packages/tgui/interfaces/gridEditorTabs.mjs';
import{GridSceneCache}from'${root}/packages/tgui/interfaces/gridEditorSceneCache.mjs';
import{GridEditorScenePool}from'${root}/packages/tgui/interfaces/gridEditorScenePool.mjs';
import{indexItems,updateItemIndex}from'${root}/packages/tgui/interfaces/gridEditorModel.mjs';
export const Context=createContext(null);
window.stats={pending:0,prepares:0,caches:[],errors:[]};
const prepare=GridSceneCache.prototype.prepare,setActive=GridEditorScenePool.prototype.setActive;
GridEditorScenePool.prototype.setActive=function(...args){window.pool=this;return setActive.apply(this,args);};
GridSceneCache.prototype.prepare=async function(...args){const stats=window.stats;stats.prepares++;stats.pending++;
if(!stats.caches.some(ref=>ref.deref()===this))stats.caches.push(new WeakRef(this));
try{return await prepare.apply(this,args);}finally{stats.pending--;}};
const svg='data:image/svg+xml;base64,'+btoa('<svg xmlns="http://www.w3.org/2000/svg" width="32" height="32"><rect width="32" height="32" fill="#80a0b0"/><path d="M0 0h32v32H0Z" stroke="#345" fill="none"/></svg>');
const visuals=[{id:'Plating',kind:'tile',name:'Plating',images:[{url:svg}]},{id:'ChairWood',kind:'entity',category:'entity',name:'ChairWood',drawDepth:10,images:[{url:svg}]}];
const size=Number(new URLSearchParams(location.search).get('size'));
const defaults={rotation:0,color:'#FFFFFFFF',z:0,cleanable:false,anchored:false,variant:0,flags:0,mirror:0};
window.checkHiddenLayers=async()=>{
const items=[{...defaults,id:'t:0:0',kind:'tile',prototype:'Plating',x:0,y:0},{...defaults,id:'e:chair',kind:'entity',prototype:'ChairWood',x:.5,y:.5}];
const visualMap=new Map(visuals.map(v=>[v.kind+':'+v.id,v])),layers={tile:true,entity:false,decal:false},cache=new GridSceneCache(()=>{});
try{const index=indexItems(items);await cache.prepare(index,layers,visualMap,()=>{},()=>false);
const next=updateItemIndex(index,[{...items[0],variant:1},items[1]]);await cache.prepare(next,layers,visualMap,()=>{},()=>false);
return[...cache.chunks.values()].every(chunk=>chunk.items.every(item=>item.kind==='tile'));}finally{cache.dispose();}};
const state=token=>{const items=[];for(let x=0;x<size;x++)for(let y=0;y<size;y++)items.push({...defaults,id:'t:'+x+':'+y,kind:'tile',prototype:'Plating',x,y});
items.push({...defaults,id:'e:chair',kind:'entity',prototype:'ChairWood',x:size/2+.5,y:size/2+.5});
return{token,grid:token,gridName:'Grid '+token,x:0,y:0,width:size,height:size,gridX:0,gridY:0,gridWidth:size,gridHeight:size,items,visuals,catalog:visuals.slice(0,1),catalogCount:1,catalogPage:0,catalogCategory:'tile',catalogQuery:'',hotbar:[],favorites:[],templates:[]};};
function App(){const[data,setData]=useState({editorTabs:{},activeEditorTab:null});
const send=payload=>setData(d=>({...d,...gridEditorUpdate(d,payload)}));const update=(token,fields)=>send({gridEditorToken:token,data:fields});
window.fixtureData=()=>data;
window.openTab=token=>{const world=state(token),id='snapshot-'+token,{items,...header}=world;
send({gridEditorToken:token,gridEditorSnapshot:{id,phase:'start'},data:header});
for(let offset=0;offset<items.length;offset+=1024)send({gridEditorToken:token,gridEditorSnapshot:{id,phase:'chunk',index:offset/1024},data:{items:items.slice(offset,offset+1024)}});
send({gridEditorToken:token,gridEditorSnapshot:{id,phase:'complete',chunks:Math.ceil(items.length/1024)}});setData(d=>({...d,activeEditorTab:token}));};
window.metadata=token=>update(token,{statusTick:performance.now()});
window.patch=token=>{const first=data.editorTabs[token].items[0];update(token,{patchVersion:1,patchItems:[{...first,variant:1}],patchRemoved:[]});};
window.activateTab=token=>setData(d=>({...d,activeEditorTab:token}));window.closeTab=token=>send({gridEditorClosed:token});
const act=(action,p={})=>{if(action==='activateTab')window.activateTab(p.tabToken);if(action==='close')window.closeTab(p.tabToken);
if(action==='search')update(p.tabToken,{catalog:visuals.filter(v=>p.category==='all'||v.kind===p.category),catalogCount:1,catalogPage:p.page,catalogCategory:p.category,catalogQuery:p.query});};
return<Context.Provider value={{data,act}}><GridEditor/></Context.Provider>;}
createRoot(document.getElementById('root')).render(<App/>);
`);
  await build({ entryPoints: [fixture], outfile: path.join(output, 'fixture.js'), bundle: true, jsx: 'automatic',
    nodePaths: [root + '/node_modules'], define: { 'process.env.NODE_ENV': '"production"' }, plugins: [{ name: 'fixture', setup(b) {
      b.onResolve({ filter: /^\.\.\/backend$/ }, () => ({ path: 'backend', namespace: 'fixture' }));
      b.onLoad({ filter: /.*/, namespace: 'fixture' }, () => ({ contents: `import{useContext}from'react';import{Context}from'${fixture}';export const useBackend=()=>useContext(Context);`, resolveDir: root }));
      b.onLoad({ filter: /\.scss$/ }, () => ({ contents: '', loader: 'css' }));
    } }] });
  const css = sass.compile(root + '/packages/tgui/styles/reset.scss').css + sass.compile(root + '/packages/tgui/interfaces/GridEditor.scss').css;
  const server = http.createServer((req, res) => {
    res.setHeader('Content-Type', req.url === '/fixture.js' ? 'text/javascript' : 'text/html');
    res.end(req.url === '/fixture.js' ? fs.readFileSync(path.join(output, 'fixture.js')) : `<html><meta charset="utf-8"><style>html,body,#root{height:100%;margin:0}${css}</style><div id="root"></div><script src="/fixture.js"></script></html>`);
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  const report = [];
  try {
    for (const size of [96, 250]) {
      const page = await browser.newPage({ viewport: { width: 1440, height: 900 } }), errors = [];
      page.on('pageerror', error => errors.push(error.message));
      await page.goto('http://127.0.0.1:' + server.address().port + '/?size=' + size);
      const editor = page.locator('.GridEditor:visible');
      const settle = async () => {
        await page.waitForTimeout(220);
        await page.waitForFunction(() => window.stats.pending === 0 && window.stats.caches.every(ref => {
          const cache = ref.deref(); return !cache || cache.disposed || !cache.active || cache.decoding === 0 &&
            [...cache.wanted].every(key => cache.chunks.get(key)?.levels.size);
        }) && !document.querySelector('.GridEditor:not([hidden]) .GridEditor__loading.blocking'), null, { timeout: 120000 });
      };
      for (let count = 1; count <= 8; count++) {
        await page.evaluate(token => window.openTab(token), 'T' + count); await settle();
        assert.equal(await page.getByRole('tab').count(), count, 'Only one tab bar exists');
        assert.ok(await page.evaluate(() => window.pool.surfaceBytes <= window.pool.budget));
        assert.ok(await page.evaluate(() => window.pool.warm.size <= 1));
        if (![5, 8].includes(count)) continue;
        const before = await page.evaluate(() => window.stats.prepares);
        for (let update = 0; update < 4; update++) { await page.evaluate(token => window.metadata(token), 'T' + count); await settle(); }
        assert.equal(await page.evaluate(() => window.stats.prepares), before, 'Metadata must not prepare any scene');
      }
      const rasterized = () => page.evaluate(() => window.stats.caches.reduce((sum, ref) => sum + (ref.deref()?.stats.rasterized || 0), 0));
      const preparedBefore = await rasterized();
      await page.evaluate(() => window.activateTab('T1')); await settle();
      assert.equal(await rasterized(), preparedBefore, 'Cold scenes decode PNGs without rasterizing sprites');
      const target = async (x, y) => editor.locator('.GridEditor__canvas').evaluate((node, { x, y, size }) => {
        const rect = node.getBoundingClientRect(); return { x: rect.left + x * rect.width / size, y: rect.top + (size - y) * rect.height / size };
      }, { x, y, size });
      const click = async (x, y) => { const position = await target(x, y); await page.mouse.click(position.x, position.y); };
      await page.keyboard.press('e'); await click(size / 2 + .5, size / 2 + .5);
      assert.match(await editor.locator('.GridEditor__currentBrush').innerText(), /ChairWood/);
      await click(size / 2 + 3.5, size / 2 + .5); await settle();
      assert.equal(await editor.locator('[aria-label="Есть изменения"]').count(), 1);
      await page.keyboard.press('Control+z'); await settle(); assert.equal(await editor.locator('[aria-label="Есть изменения"]').count(), 0);
      await page.keyboard.press('Control+Shift+z'); await settle(); assert.equal(await editor.locator('[aria-label="Есть изменения"]').count(), 1);
      await page.evaluate(() => window.activateTab('T8')); await settle();
      await page.evaluate(() => window.patch('T1')); await settle();
      await page.evaluate(() => window.activateTab('T1')); await settle();
      assert.equal(await editor.locator('[aria-label="Есть изменения"]').count(), 1, 'Background patches preserve local edits/history');
      const beforeExpiry = await rasterized();
      await page.evaluate(() => {
        const pool = window.pool, now = pool.now;
        if (pool.timer !== null) clearTimeout(pool.timer);
        pool.now = () => now() + 31000; pool.expire(); pool.now = now;
      });
      assert.equal(await page.evaluate(() => window.pool.warm.size), 0);
      await page.evaluate(() => window.activateTab('T8')); await settle();
      assert.equal(await rasterized(), beforeExpiry, 'Expired surfaces restore from compressed chunks');
      const retainedBytes = await page.evaluate(() => window.pool.surfaceBytes);
      for (let index = 1; index < 8; index++) await page.evaluate(token => window.closeTab(token), 'T' + index);
      await settle(); assert.equal(await page.getByRole('tab').count(), 1);
      assert.ok(await page.evaluate(() => window.checkHiddenLayers()), 'Incremental chunks must not reveal disabled layers');
      assert.deepEqual(errors, []);
      report.push({ size, tabs: 8, metadataPrepares: 0, retainedMiB: retainedBytes / 1048576,
        pngRestore: true, dirtyUndoRedo: true, backgroundPatch: true, expiry: true, rapidClose: true });
      await page.screenshot({ path: path.join(output, 'tabs-' + size + '.png') }); await page.close();
    }
  } finally { await browser.close(); await new Promise(resolve => server.close(resolve)); }
  fs.writeFileSync(path.join(output, 'report.json'), JSON.stringify(report, null, 2));
  console.log(JSON.stringify(report, null, 2));
}
main().catch(error => { console.error(error); process.exitCode = 1; });
