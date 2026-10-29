const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const assert = require('node:assert/strict');
const root = path.resolve(__dirname, '..').replaceAll('\\', '/');
const { build } = require(root + '/node_modules/esbuild');
const sass = require(root + '/node_modules/sass');
const { chromium } = require('playwright');
const output = process.env.GRID_EDITOR_OUTPUT || path.resolve(root, '../.local/codex/grid-editor-tabs/tabs-browser');
fs.mkdirSync(output, { recursive: true });

async function main() {
  const fixture = path.join(output, 'tabs.jsx').replaceAll('\\', '/');
  fs.writeFileSync(fixture, `
import React,{createContext,useState} from 'react';
import {createRoot} from 'react-dom/client';
import {GridEditor} from '${root}/packages/tgui/interfaces/GridEditor.jsx';
import {gridEditorUpdate} from '${root}/packages/tgui/interfaces/gridEditorTabs.mjs';
export const Context=createContext(null);
const svg='data:image/svg+xml;base64,'+btoa('<svg xmlns="http://www.w3.org/2000/svg" width="32" height="32"><rect x="3" y="3" width="26" height="26" fill="white"/></svg>');
const catalog=['Plating','ChairWood','GasPipeStraight','GasPipeStraightAlt1','CableHV'].map((id,i)=>({id,name:id,kind:i?'entity':'tile',category:i?'entity':'tile',anchored:i>1,pipeColor:id.startsWith('GasPipe'),images:[{url:svg}]}));
const item=(id,prototype,x,y)=>({id,prototype,kind:'entity',x,y,rotation:90,anchored:true,color:'#FFFFFFFF',z:0,flags:0,variant:0,mirror:0});
const state=token=>({token,grid:token,gridName:'Grid '+token,x:-1,y:-1,width:8,height:8,gridX:0,gridY:0,gridWidth:6,gridHeight:6,
  items:[...Array.from({length:36},(_,i)=>({...item('t:'+i,'Plating',i%6,Math.floor(i/6)),kind:'tile'})),
  {...item('e:chair','ChairWood',1.5,1.5),rotation:0,anchored:false},
  ...[0,1,2].flatMap(x=>[item('e:pipe'+x,'GasPipeStraight',x+.5,3.5),item('e:alt'+x,'GasPipeStraightAlt1',x+.5,3.5),item('e:wire'+x,'CableHV',x+.5,4.5)])],
  visuals:catalog,catalog:catalog.filter(i=>i.kind==='tile'),catalogCount:1,hotbar:[],favorites:[],templates:[]});
const worlds={A:state('A'),B:state('B')},uploads={},previews={};
function App(){
 const [data,setData]=useState({editorTabs:{A:worlds.A},activeEditorTab:'A'});
 const send=payload=>setData(d=>({...d,...gridEditorUpdate(d,payload)}));
 const update=(token,fields)=>send({gridEditorToken:token,data:fields});
 window.fixtureData=()=>data;window.fixtureWorld=worlds;window.fixturePreviews=previews;
 window.fixtureOpen=token=>{update(token,worlds[token]);setData(d=>({...d,activeEditorTab:token}));};
 window.fixturePatch=(token,fields)=>update(token,fields);
 const act=(action,p={})=>{
  const token=p.tabToken;
  window.fixtureActions||=[];window.fixtureActions.push({action,...p});
  if(action==='activateTab'){setData(d=>({...d,activeEditorTab:token}));return;}
  if(action==='close'){send({gridEditorClosed:token});return;}
  if(action==='routeVisuals')update(token,{routeVisuals:catalog});
  if(action==='search'){const rows=catalog.filter(i=>p.category==='all'||i.category===p.category);update(token,{catalog:rows,catalogCount:rows.length,catalogPage:0,catalogCategory:p.category,catalogQuery:p.query});}
  if(action==='previewStart'){previews[token]={...p,items:[]};update(token,{previewId:p.id,previewNext:0});}
  if(action==='previewChunk'){const v=previews[token];v.items.push(...p.items);update(token,p.index+1<v.chunks?{previewNext:p.index+1}:{previewFinished:p.id});}
  if(action==='applyStart'){uploads[token]={...p,items:[]};update(token,{uploadId:p.id,uploadNext:0});}
  if(action==='applyChunk'){
   const u=uploads[token];u.items.push(...p.items);
   if(p.index+1<u.chunks){update(token,{uploadNext:p.index+1});return;}
   const world=worlds[token],items=new Map(world.items.map(i=>[i.id,i]));
   for(const i of u.items)if(i.kind==='remove')items.delete(i.id);else items.set(i.id,i);
   world.items=[...items.values()];world.patchVersion=(world.patchVersion||0)+1;
   update(token,{items:world.items,patchItems:u.items.filter(i=>i.kind!=='remove'),patchRemoved:u.items.filter(i=>i.kind==='remove').map(i=>({id:i.id})),
     patchVersion:world.patchVersion,patchApplied:true,success:true,resultVersion:world.patchVersion});
  }
 };
 return <Context.Provider value={{data,act}}><GridEditor/></Context.Provider>;
}
createRoot(document.getElementById('root')).render(<App/>);
`);
  await build({ entryPoints: [fixture], outfile: path.join(output, 'tabs.js'), bundle: true, jsx: 'automatic', nodePaths: [root + '/node_modules'],
    define: { 'process.env.NODE_ENV': '"production"' }, plugins: [{ name: 'fixture', setup(build) {
      build.onResolve({ filter: /^\.\.\/backend$/ }, () => ({ path: 'backend', namespace: 'fixture' }));
      build.onLoad({ filter: /.*/, namespace: 'fixture' }, () => ({ contents: `import {useContext} from 'react';import {Context} from ${JSON.stringify(fixture)};export const useBackend=()=>useContext(Context);`, resolveDir: root }));
      build.onLoad({ filter: /\.scss$/ }, () => ({ contents: '', loader: 'css' }));
    } }] });
  const css = sass.compile(root + '/packages/tgui/styles/reset.scss').css + sass.compile(root + '/packages/tgui/interfaces/GridEditor.scss').css;
  const server = http.createServer((req, res) => {
    res.setHeader('Content-Type', req.url === '/tabs.js' ? 'text/javascript' : 'text/html');
    res.end(req.url === '/tabs.js' ? fs.readFileSync(path.join(output, 'tabs.js')) : `<html><meta charset="utf-8"><style>html,body,#root{height:100%;margin:0}${css}</style><div id="root"></div><script src="/tabs.js"></script></html>`);
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  const errors = [];
  try {
    const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
    page.on('pageerror', e => errors.push(e.message));
    await page.goto('http://127.0.0.1:' + server.address().port);
    const editor = page.locator('.GridEditor:visible');
    const ready = async () => {
      await editor.locator('.GridEditor__canvas').waitFor({ state: 'visible' });
      await editor.locator('.GridEditor__loading.blocking').waitFor({ state: 'hidden' });
      await page.waitForFunction(() => document.querySelector('.GridEditor:not([hidden]) .GridEditor__canvas')?.getBoundingClientRect().width > 100);
    };
    const point = async (x, y) => editor.locator('.GridEditor__canvas').evaluate((node, { x, y }) => {
      const rect = node.getBoundingClientRect(), size = rect.width / 8;
      return { x: rect.left + (x + 1) * size, y: rect.top + (7 - y) * size };
    }, { x, y });
    const click = async (x, y, options) => { const p = await point(x, y); await page.mouse.click(p.x, p.y, options); };
    await ready();
    // Pick a prototype, then retain it in a new tab.
    await page.keyboard.press('e');
    await editor.locator('.GridEditor__canvas.tool-pick').waitFor();
    await click(1.5, 1.5);
    assert.match(await editor.locator('.GridEditor__currentBrush').innerText(), /ChairWood/);
    await page.keyboard.press('s'); await page.keyboard.up('s'); await click(1.5, 1.5); await page.keyboard.press('Control+c');
    await page.waitForFunction(() => window.fixtureActions?.some(a => a.action === 'copySelection'));
    await page.evaluate(() => window.fixtureOpen('B'));
    await page.waitForFunction(() => document.querySelector('.GridEditor:not([hidden]) [role=tab][aria-selected=true]')?.textContent === 'Grid B')
      .catch(async error => { console.error(JSON.stringify({ errors, tabs: await page.locator('[role=tab]').allTextContents(), editors: await page.locator('.GridEditor').count() })); throw error; });
    await ready();
    assert.equal(await page.getByRole('tab').count(), 2);
    assert.match(await editor.locator('.GridEditor__currentBrush').innerText(), /ChairWood/);
    await page.screenshot({ path: path.join(output, 'tabs-1440.png') });
    const destination = await point(4.5, 1.5); await page.mouse.move(destination.x, destination.y);
    await editor.locator('.GridEditor__canvas').focus();
    await page.evaluate(() => window.dispatchEvent(new CustomEvent('grid-editor-key', { detail: { code: 'KeyV', ctrlKey: true, down: true } })));
    await editor.getByRole('button', { name: 'Применить', exact: true }).click();
    await page.waitForFunction(() => window.fixtureWorld.B.items.filter(i => i.prototype === 'ChairWood').length === 2);
    assert.equal(await page.evaluate(() => window.fixtureWorld.A.items.filter(i => i.prototype === 'ChairWood').length), 1);
    // A background authoritative patch must update A without touching B's history.
    await page.evaluate(() => window.fixturePatch('A', { gridName: 'Renamed A' }));
    await page.getByRole('tab', { name: 'Renamed A', exact: true }).click();
    await ready();
    await page.keyboard.press('4'); await click(1.5, 3.5);
    await editor.getByLabel('Цвет pipenet', { exact: true }).fill('#d42831');
    await editor.getByRole('button', { name: 'Покрасить pipenet в черновике', exact: true }).click();
    await editor.getByRole('button', { name: 'Применить', exact: true }).click();
    await page.waitForFunction(() => window.fixtureWorld.A.items.filter(i => i.prototype === 'GasPipeStraight').every(i => i.color.toLowerCase() === '#d42831ff'));
    assert.ok(await page.evaluate(() => window.fixtureWorld.A.items.filter(i => i.prototype === 'GasPipeStraightAlt1').every(i => i.color === '#FFFFFFFF')));
    // Right-click a wire control point: only that cell disappears, and undo restores it.
    await click(1.5, 4.5);
    const handles = editor.getByRole('button', { name: /Точка трассы/ });
    assert.equal(await handles.count(), 3);
    await handles.nth(1).click({ button: 'right' });
    assert.equal(await handles.count(), 2);
    assert.equal(await editor.locator('.GridEditor__radialMenu:visible').count(), 0);
    await page.keyboard.press('Control+z'); assert.equal(await handles.count(), 3);
    await page.keyboard.press('Escape');
    // Dirty close is reviewable; cancelling keeps both tabs and the draft.
    await page.keyboard.press('e'); await click(1.5, 1.5); await click(4.5, 2.5);
    await page.getByRole('button', { name: 'Закрыть вкладку Renamed A', exact: true }).click();
    await editor.getByRole('button', { name: 'Продолжить', exact: true }).click();
    assert.equal(await page.getByRole('tab').count(), 2);
    await page.getByRole('tab', { name: 'Grid B', exact: true }).click();
    await page.getByRole('button', { name: 'Закрыть вкладку Grid B', exact: true }).click();
    await page.waitForFunction(() => Object.keys(window.fixtureData().editorTabs).length === 1);
    await page.setViewportSize({ width: 320, height: 900 });
    assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
    await page.screenshot({ path: path.join(output, 'tabs-320.png') });
    assert.deepEqual(errors, []);
    fs.writeFileSync(path.join(output, 'report.json'), JSON.stringify({ tabs: true, sharedPrototype: true, crossTabPaste: true, backgroundPatch: true, pipeColorIsolation: true, rightClickUndo: true, dirtyClose: true, narrowViewport: true }, null, 2));
    console.log('Grid editor tabs, shared paste/prototype, pipe colors and right-click passed.');
  } finally { await browser.close(); server.close(); }
}
main().catch(error => { console.error(error); process.exitCode = 1; });
