const fs = require('node:fs'), path = require('node:path'), http = require('node:http'), assert = require('node:assert/strict');
const { execFileSync } = require('node:child_process');
const root = path.resolve(__dirname, '..').replaceAll('\\', '/');
const { build } = require(root + '/node_modules/esbuild'), sass = require(root + '/node_modules/sass');
const { chromium } = require('playwright');
const output = path.resolve(process.env.GRID_EDITOR_OUTPUT || root + '/../.local/codex/grid-editor-recovery/browser');
fs.mkdirSync(output, { recursive: true });

async function main() {
  const fixture = path.join(output, 'fixture.jsx').replaceAll('\\', '/');
  fs.writeFileSync(fixture, `
import React,{createContext,useState}from'react';import{createRoot}from'react-dom/client';
import{GridEditor}from'${root}/packages/tgui/interfaces/GridEditor.jsx';
import{gridEditorUpdate}from'${root}/packages/tgui/interfaces/gridEditorTabs.mjs';
import{GridSceneCache}from'${root}/packages/tgui/interfaces/gridEditorSceneCache.mjs';
export const Context=createContext(null);
window.preparing=0;const prepare=GridSceneCache.prototype.prepare;
GridSceneCache.prototype.prepare=async function(...args){window.preparing++;try{return await prepare.apply(this,args);}finally{window.preparing--;}};
const defaults={rotation:0,color:'#FFFFFFFF',z:0,cleanable:false,anchored:false,variant:0,flags:0,mirror:0};
const image='data:image/svg+xml;base64,'+btoa('<svg xmlns="http://www.w3.org/2000/svg" width="32" height="32"><rect width="32" height="32" fill="#80a0b0"/></svg>');
const visuals=[{id:'Plating',kind:'tile',name:'Plating',images:[{url:image}]},{id:'ChairWood',kind:'entity',name:'ChairWood',drawDepth:10,images:[{url:image}]}];
const hosts=new Map();let serial=0,documentId=0;
window.actions=[];window.checkpoints=0;window.holdPreview=false;
const state=token=>{const items=[];for(let x=0;x<96;x++)for(let y=0;y<96;y++)items.push({...defaults,id:'t:'+x+':'+y,kind:'tile',prototype:'Plating',x,y});
items.push({...defaults,id:'e:chair',kind:'entity',prototype:'ChairWood',x:48.5,y:48.5});
return{token,grid:token,gridName:'Grid '+token,x:0,y:0,width:96,height:96,gridX:0,gridY:0,gridWidth:96,gridHeight:96,items,visuals,
snapshotPatchVersion:7,lastAppliedVersion:7,previewGeneration:0,catalog:visuals,catalogCount:2,hotbar:[],favorites:[],templates:[]};};
function App(){const[data,setData]=useState({editorTabs:{}});
const send=payload=>setData(d=>({...d,...gridEditorUpdate(d,payload)}));
const update=(token,fields)=>send({gridEditorToken:token,data:fields});
window.snapshot=token=>{const host=hosts.get(token),id='s'+serial++,{items,...header}=host.world;
send({gridEditorToken:token,gridEditorSnapshot:{id,phase:'start'},data:header});
for(let offset=0;offset<items.length;offset+=1024)send({gridEditorToken:token,gridEditorSnapshot:{id,phase:'chunk',index:offset/1024},data:{items:items.slice(offset,offset+1024)}});
const text=JSON.stringify({edits:[...host.saved.values()],expansion:host.expansion});let chunks=0;
for(let offset=0;offset<text.length;offset+=32768)send({gridEditorToken:token,gridEditorSnapshot:{id,phase:'draft',index:chunks++},data:{text:text.slice(offset,offset+32768)}});
send({gridEditorToken:token,gridEditorSnapshot:{id,phase:'complete',chunks:Math.ceil(items.length/1024),draftChunks:chunks}});
// An acknowledged apply can still be queued when a document restarts.
update(token,{patchVersion:7,patchBatches:[{version:7,applied:true,items:[],removed:[],x:0,y:0,width:96,height:96}]});};
window.open=token=>{hosts.set(token,{world:state(token),saved:new Map(),expansion:{left:0,right:0,top:0,bottom:0}});window.snapshot(token);window.activate(token);};
window.activate=token=>setData(d=>({...d,activeEditorTab:token}));
const act=(action,p={})=>{window.actions.push({action,p});const host=hosts.get(p.tabToken);
if(action==='activateTab')window.activate(p.tabToken);
if(action==='previewStart'){host.upload={...p,parts:[]};update(p.tabToken,{previewId:p.id,previewNext:0});}
if(action==='previewChunk'){
 const upload=host.upload;if(upload.id!==p.id||upload.parts.length!==p.index)return;
 upload.parts.push(upload.encoding?p.text:p.items);
 if(upload.parts.length<upload.chunks){update(p.tabToken,{previewId:p.id,previewNext:upload.parts.length});return;}
 const changes=upload.encoding?JSON.parse(upload.parts.join('')):upload.parts.flat();
 if(!window.holdPreview){for(const item of changes)if(item.kind==='clear')host.saved.delete(item.id);else host.saved.set(item.id,item);host.expansion=upload.expansion;window.checkpoints++;}
 update(p.tabToken,{previewFinished:p.id});
}
};return<Context.Provider value={{data,act}}><GridEditor/></Context.Provider>;}
const app=createRoot(document.getElementById('root'));app.render(<App key={documentId}/>);
window.restart=async()=>{app.render(<App key={++documentId}/>);await new Promise(r=>setTimeout(r,30));for(const token of hosts.keys())window.snapshot(token);window.activate('A');};
window.saved=token=>[...hosts.get(token).saved.values()];
`);
  await build({ entryPoints: [fixture], outfile: path.join(output, 'fixture.js'), bundle: true, jsx: 'automatic', nodePaths: [root + '/node_modules'],
    define: { 'process.env.NODE_ENV': '"production"' }, plugins: [{ name: 'fixture', setup(b) {
      b.onResolve({ filter: /^\.\.\/backend$/ }, () => ({ path: 'backend', namespace: 'fixture' }));
      b.onLoad({ filter: /.*/, namespace: 'fixture' }, () => ({ contents: `import{useContext}from'react';import{Context}from'${fixture}';export const useBackend=()=>useContext(Context);`, resolveDir: root }));
      b.onLoad({ filter: /\.scss$/ }, () => ({ contents: '', loader: 'css' }));
      if (process.env.GRID_EDITOR_RECOVERY_BASELINE) b.onLoad({ filter: /(?:GridEditor|GridEditorPeers|GridEditorScene)\.jsx$|gridEditorTabs\.mjs$/ }, args => {
        const relative = path.relative(path.resolve(root, '..'), args.path).replaceAll('\\', '/');
        return { contents: execFileSync('git', ['show', 'HEAD:' + relative], { cwd: root, encoding: 'utf8' }), loader: args.path.endsWith('.jsx') ? 'jsx' : 'js', resolveDir: path.dirname(args.path) };
      });
    } }] });
  const css = sass.compile(root + '/packages/tgui/styles/reset.scss').css + sass.compile(root + '/packages/tgui/interfaces/GridEditor.scss').css;
  const server = http.createServer((req, res) => {
    res.setHeader('Content-Type', req.url === '/fixture.js' ? 'text/javascript' : 'text/html');
    res.end(req.url === '/fixture.js' ? fs.readFileSync(path.join(output, 'fixture.js')) : `<html><meta charset="utf-8"><style>html,body,#root{height:100%;margin:0}${css}</style><div id="root"></div><script src="/fixture.js"></script></html>`);
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 1440, height: 900 } }), errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.goto('http://127.0.0.1:' + server.address().port);
    const editor = page.locator('.GridEditor:visible');
    const settle = async () => {
      await page.waitForTimeout(300);
      await page.waitForFunction(() => !!document.querySelector('.GridEditor:not([hidden]) .GridEditor__canvas') &&
        window.preparing === 0 &&
        !document.querySelector('.GridEditor:not([hidden]) .GridEditor__loading.blocking'), null, { timeout: 60000 });
    };
    const click = async (x, y) => {
      const position = await editor.locator('.GridEditor__canvas').evaluate((node, { x, y }) => {
        const rect = node.getBoundingClientRect(); return { x: rect.left + x * rect.width / 96, y: rect.top + (96 - y) * rect.height / 96 };
      }, { x, y });
      await page.mouse.click(position.x, position.y);
    };
    for (const token of ['A', 'B', 'C', 'D', 'E']) { await page.evaluate(token => window.open(token), token); await settle(); }
    await page.evaluate(() => window.activate('A')); await settle();
    await page.keyboard.press('e'); await click(48.5, 48.5);
    await click(51.5, 48.5); await settle();
    await page.waitForFunction(() => window.saved('A').length === 1);
    assert.equal(await editor.locator('[aria-label="Есть изменения"]').count(), 1);
    await page.evaluate(() => window.activate('B')); await settle();
    await click(51.5, 48.5); await settle();
    await page.waitForFunction(() => window.saved('B').length === 1);
    await page.evaluate(() => window.restart()); await settle();
    assert.equal(await editor.getByRole('tab', { name: 'Grid A' }).locator('[aria-label="Есть изменения"]').count(), 1, 'Cold document recovery must survive a queued old apply acknowledgement');
    assert.equal(await page.locator('[aria-label="Есть изменения"]').count(), 2, 'Each grid has its own recovered draft');
    await page.keyboard.press('e'); await click(48.5, 48.5);
    // Replay a warm snapshot before the latest edit has reached its checkpoint.
    await click(54.5, 48.5);
    await page.evaluate(() => window.snapshot('A')); await settle();
    await page.waitForFunction(() => window.saved('A').length === 2);
    await page.keyboard.press('Control+z'); await settle();
    await page.waitForFunction(() => window.saved('A').length === 1);
    await page.keyboard.press('Control+Shift+z'); await settle();
    await page.waitForFunction(() => window.saved('A').length === 2);
    const continuousStart = await page.evaluate(() => window.checkpoints);
    for (let index = 0; index < 15; index++) { await click(50.5 + index, 52.5); await page.waitForTimeout(25); }
    assert.ok(await page.evaluate(start => window.checkpoints > start + 1, continuousStart), 'Continuous editing must checkpoint before the user stops');
    await settle();
    const saved = await page.evaluate(() => window.saved('A'));
    assert.equal(saved.length, 17);
    await page.evaluate(() => window.restart()); await settle();
    await page.keyboard.press('e'); await click(48.5, 48.5); await click(67.5, 52.5); await settle();
    await page.waitForFunction(() => window.saved('A').length === 18);
    await page.keyboard.down('Space');
    await editor.getByRole('tab', { name: 'Grid B' }).click(); await settle();
    await page.keyboard.up('Space');
    await editor.getByRole('tab', { name: 'Grid A' }).click(); await settle();
    await click(70.5, 52.5); await settle();
    await page.waitForFunction(() => window.saved('A').length === 19, null, { timeout: 5000 });
    assert.deepEqual(errors, []);
    await page.screenshot({ path: path.join(output, 'recovered.png') });
    const report = { tabs: 5, coldRecovery: true, queuedApply: true, warmReplay: true, undoRedo: true, continuousCheckpoints: true, editableAfterRecovery: true, releasedKeysAcrossTabs: true, savedEdits: 19, errors };
    fs.writeFileSync(path.join(output, 'report.json'), JSON.stringify(report, null, 2)); console.log(JSON.stringify(report));
  } finally { await browser.close(); await new Promise(resolve => server.close(resolve)); }
}
main().catch(error => { console.error(error); process.exitCode = 1; });
