// Standalone browser fixture for layout and pointer QA; the production host remains the game client.
import fs from 'node:fs';
import path from 'node:path';
import http from 'node:http';
import { build } from 'esbuild';
import * as sass from 'sass';
const root = path.resolve(import.meta.dirname, '..');
const output = path.join(root, '.grid-editor-preview');
fs.mkdirSync(output, { recursive: true });
const source = `
import React, { createContext, useContext, useState, useEffect } from 'react';
import { createRoot } from 'react-dom/client';
import { GridEditor } from '../packages/tgui/interfaces/GridEditor.jsx';
import { routeProfiles, routePrototypes } from '../packages/tgui/interfaces/gridEditorRouting.mjs';
export const Context = createContext(null);
const svg = (body) => 'data:image/svg+xml;base64,' + btoa('<svg xmlns="http://www.w3.org/2000/svg" width="32" height="32">' + body + '</svg>');
const floor = svg('<rect width="32" height="32" fill="#647b86"/><path d="M0 0h32v32H0Z M0 16h32M16 0v32" fill="none" stroke="#415765"/>');
const wall = svg('<rect x="1" y="1" width="30" height="30" rx="3" fill="#acb7be" stroke="#435562" stroke-width="3"/><rect x="6" y="6" width="20" height="20" fill="#647582"/>');
const chair = svg('<path d="M7 5h18v17H7Z" fill="#b38657" stroke="#523f2e" stroke-width="2"/><path d="M8 22v7m16-7v7" stroke="#b38657" stroke-width="3"/>');
const decal = svg('<path d="M3 3h26v26H3Z" fill="none" stroke="#eac153" stroke-width="3"/>');
const catalog = [
  { id: 'Plating', name: 'Обшивка', kind: 'tile', category: 'tile', images: [{ url: floor }] },
  { id: 'FloorSteel', name: 'Стальной пол', kind: 'tile', category: 'tile', images: [{ url: floor }] },
  { id: 'WallSolid', name: 'Стальная стена', kind: 'entity', category: 'wall', anchored: true, images: [{ url: wall }] },
  { id: 'ChairWood', name: 'Деревянный стул', kind: 'entity', category: 'entity', anchored: false, images: [{ url: chair }] },
  { id: 'DecalCorner', name: 'Разметка угла', kind: 'decal', category: 'decal', images: [{ url: decal }] },
];
for (let i = 0; i < 1000; i++) catalog.push({ ...catalog[0], id: 'FixtureFloor' + i, name: 'Проверочный пол ' + i });
catalog.push({ id: 'FixtureTable', name: 'Проверочный стол', kind: 'entity', category: 'entity', drawDepth: 2, exclusive: true, images: [{url: svg('<rect x="2" y="2" width="28" height="28" fill="brown"/>')}] },
  { id: 'FixtureCup', name: 'Проверочная чашка', kind: 'entity', category: 'entity', drawDepth: 10, images: [{url: svg('<rect x="13" y="13" width="6" height="6" fill="white"/>')}] });
for(const profile of routeProfiles)for(const id of routePrototypes(profile))catalog.push({id,name:id,kind:'entity',category:'entity',anchored:true,images:[{url:decal}]});
const defaults = { rotation: 0, color: '#ffffff', z: 0, cleanable: false, anchored: false, variant: 0, flags: 0, mirror: 0 };
const large = new URLSearchParams(location.search).has('large');
const fail = new URLSearchParams(location.search).has('fail');
const items = [];
let upload = null, previewUpload = null;
const peerId = crypto.randomUUID(), peerName = new URLSearchParams(location.search).get('peer') || 'Другой маппер';
const channel = new BroadcastChannel('grid-editor-fixture');
let ownExpansion={left:0,right:0,bottom:0,top:0};
let ownPreview = [], foreignPreviews = new Map();
const mergeItems = (base, changes) => { const map = new Map(base.map(i=>[i.id,i])); for(const i of changes) { if(i.kind==='remove'||i.kind==='clear') map.delete(i.id); else map.set(i.id,i); } return [...map.values()]; };
const broadcastPreview = () => channel.postMessage({type:'preview',from:peerId,name:peerName,items:ownPreview,expansion:ownExpansion});
for (let x = -5; x <= 6; x++) for (let y = -3; y <= 5; y++) {
  items.push({ ...defaults, id: 't:'+x+':'+y, kind: 'tile', prototype: 'Plating', x, y });
  if (x === -5 || x === 6 || y === -3 || y === 5) items.push({ ...defaults, id: 'e:'+x+':'+y, kind: 'entity', prototype: 'WallSolid', x: x+.5, y: y+.5, anchored: true });
}
items.push({ ...defaults, id: 'e:chair', kind: 'entity', prototype: 'ChairWood', x: .5, y: .5 });
items.push({ ...defaults, id: 'e:table', kind: 'entity', prototype: 'FixtureTable', x: 2.5, y: 1.5 }, { ...defaults, id: 'e:cup', kind: 'entity', prototype: 'FixtureCup', x: 2.5, y: 1.5 });
if (large) { items.length = 0; for (let x=0; x<250; x++) for(let y=0; y<250; y++) items.push({ ...defaults, id: 't:'+x+':'+y, kind:'tile', prototype:'Plating', x,y }); }
function App() {
  const [data, setData] = useState({ grid: '142', token: 'fixture', sourcePath:new URLSearchParams(location.search).has('source')?'Resources/Maps/Test/empty.yml':'', x: -8, y: -6, width: 20, height: 16, gridX: -5, gridY: -3, gridWidth: 12, gridHeight: 9, ...(large ? { x:-4, y:-4, width:258, height:258, gridX:0, gridY:0, gridWidth:250, gridHeight:250 } : {}), items, visuals: catalog, catalog: catalog.filter(i => i.kind === 'tile'), catalogCount: 2, catalogPage: 0, favorites: [], templates: [], initialized: false });
  window.fixtureGetData = () => data; window.fixtureSetData = setData;
  useEffect(() => {
    const receive = ({data:message}) => {
      if(message.from===peerId) return;
      if(message.type==='hello') { broadcastPreview(); return; }
      if(message.type==='preview') {
        foreignPreviews.set(message.from,message);
        setData(d=>({...d, peerVisuals:catalog, peerDrafts:[...foreignPreviews.values()].map(p=>({player:p.from,name:p.name,expansion:p.expansion,
          items:p.items.filter(i=>i.kind!=='remove'),removedItems:p.items.filter(i=>i.kind==='remove').map(i=>d.items.find(old=>old.id===i.id)).filter(Boolean)}))}));
      }
      if(message.type==='world') setData(d=>({...d,items:mergeItems(d.items,message.changes),patchItems:message.changes.filter(i=>i.kind!=='remove'),patchRemoved:message.changes.filter(i=>i.kind==='remove'),patchVisuals:catalog,patchVersion:(d.patchVersion||0)+1,patchApplied:false}));
    };
    channel.addEventListener('message',receive); channel.postMessage({type:'hello',from:peerId});
    return ()=>channel.removeEventListener('message',receive);
  },[]);
  const act = (action, payload) => {
    window.fixtureActions ||= []; window.fixtureActions.push({action,payload});
    if (action === 'setGridName') setData(d=>({...d,gridName:payload.name,gridNameError:'',gridNameResultVersion:(d.gridNameResultVersion||0)+1}));
    if (action === 'referenceImport') { const input = document.createElement('input'); input.type='file'; input.accept='image/*'; input.onchange=()=>{const reader=new FileReader();reader.onload=()=>setData(d=>({...d,referenceImage:reader.result,referenceVersion:Date.now()}));reader.readAsDataURL(input.files[0]);}; input.click(); }

    if (fail && ['applyStart', 'download'].includes(action)) { setData(d => ({ ...d, status: 'Проверочный отказ сервера: карта была изменена другим маппером. Обновите grid.', success: false, resultVersion: Date.now() })); return; }
    if (action === 'routeVisuals') setData(d=>({...d,routeVisuals:catalog.filter(i=>payload.prototypes.includes(i.id))}));
    if (action === 'search') { const entries = catalog.filter(i => (payload.category === 'all' || i.category === payload.category || payload.category === 'favorites' && data.favorites.some(f => f.id === i.id && f.kind === i.kind)) && (i.name.toLowerCase().includes(payload.query.toLowerCase()) || i.id.toLowerCase().includes(payload.query.toLowerCase()))); setData(d => ({ ...d, catalog: entries.slice(payload.page * 48, (payload.page + 1) * 48), catalogCount: entries.length, catalogPage: payload.page, catalogCategory: payload.category, catalogQuery: payload.query })); }
    if (action === 'favorite') setData(d => ({ ...d, favorites: d.favorites.some(f => f.kind === payload.kind && f.id === payload.id) ? d.favorites.filter(f => f.kind !== payload.kind || f.id !== payload.id) : [...d.favorites, payload] }));
    if (action === 'hotbarSet') setData(d => ({ ...d, hotbar: [...(d.hotbar || []).filter(entry => entry.slot !== payload.slot), ...(payload.id ? [payload] : [])] }));
    if (action === 'deleteTemplate') setData(d => ({ ...d, templates: d.templates.filter(t => t.id !== payload.id) }));
    if (action === 'previewStart') { previewUpload={...payload,items:[]}; setData(d=>({...d,previewId:payload.id,previewNext:0})); }
    if (action === 'previewChunk') {
      previewUpload.items.push(...payload.items);
      if(payload.index+1<previewUpload.chunks) setData(d=>({...d,previewNext:payload.index+1}));
      else { ownPreview=(()=>{const values=new Map(ownPreview.map(i=>[i.id,i]));for(const i of previewUpload.items){if(i.kind==='clear')values.delete(i.id);else values.set(i.id,i);}return [...values.values()];})(); ownExpansion=previewUpload.expansion; broadcastPreview(); setData(d=>({...d,previewNext:previewUpload.chunks,previewFinished:previewUpload.id})); }
    }
    if (action === 'applyStart') { upload = { ...payload, items: [] }; setData(d => ({ ...d, uploadId: payload.id, uploadNext: 0 })); }
    if (action === 'applyChunk') {
      upload.items.push(...payload.items);
      if (payload.index + 1 < upload.chunks) setData(d => ({ ...d, uploadNext: payload.index + 1 }));
      else if (upload.purpose === 'template') setData(d => ({ ...d, templates: [...d.templates, { id: upload.id, name: upload.name, items: upload.items }], libraryVisuals: catalog, status: 'Шаблон сохранён (браузерный макет)', success: true, resultVersion: Date.now() }));
      else { ownPreview=[];ownExpansion={left:0,right:0,bottom:0,top:0};broadcastPreview();channel.postMessage({type:'world',from:peerId,changes:upload.items});
      setData(d => ({ ...d, items:mergeItems(d.items,upload.items),previewGeneration:(d.previewGeneration||0)+1, patchItems: upload.items.filter(i => i.kind !== 'remove'), patchRemoved: upload.items.filter(i => i.kind === 'remove').map(i => ({id:i.id})), patchVisuals: catalog, patchVersion:(d.patchVersion || 0)+1, patchApplied:true,
        ...(upload.expansion ? { x:d.x-upload.expansion.left, y:d.y-upload.expansion.bottom, width:d.width+upload.expansion.left+upload.expansion.right, height:d.height+upload.expansion.bottom+upload.expansion.top } : {}),
        status:'Изменения применены (браузерный макет)', success:true, resultVersion:Date.now() })); }
    }
    if (action === 'close') document.body.textContent = 'Редактор закрыт';
    if (action === 'region') setData(d => ({ ...d, x: payload.x, y: payload.y, width: 96, height: 96, items: [], token: String(Date.now()) }));
  };
  return <Context.Provider value={{ data, act }}><GridEditor /></Context.Provider>;
}
createRoot(document.getElementById('root')).render(<App />);
`;
fs.writeFileSync(path.join(output, 'fixture.jsx'), source);
await build({ entryPoints: [path.join(output, 'fixture.jsx')], outfile: path.join(output, 'preview.js'), bundle: true,
  jsx: 'automatic', plugins: [{ name: 'backend-fixture', setup(build) {
    build.onResolve({ filter: /^\.\.\/backend$/ }, () => ({ path: 'fixture-backend', namespace: 'fixture' }));
    build.onLoad({ filter: /.*/, namespace: 'fixture' }, () => ({ contents: `import { useContext } from 'react'; import { Context } from ${JSON.stringify(path.join(output, 'fixture.jsx'))}; export const useBackend = () => useContext(Context);`, resolveDir: root }));
    build.onLoad({ filter: /\.scss$/ }, () => ({ contents: '', loader: 'css' }));
  } }] });
const css = sass.compile(path.join(root, 'packages/tgui/styles/reset.scss')).css + sass.compile(path.join(root, 'packages/tgui/interfaces/GridEditor.scss')).css;
fs.writeFileSync(path.join(output, 'index.html'), `<html><meta charset="utf-8"><title>Редактор grid — проверка интерфейса</title><style>html,body,#root{height:100%;margin:0;font-family:Arial,sans-serif}*{box-sizing:border-box}${css}</style><div id="root"></div><script src="/preview.js"></script></html>`);
http.createServer((req, res) => { const file = req.url === '/preview.js' ? 'preview.js' : 'index.html'; res.setHeader('Content-Type', file.endsWith('.js') ? 'text/javascript' : 'text/html'); res.end(fs.readFileSync(path.join(output, file))); }).listen(5179, '127.0.0.1', () => console.log('Grid editor fixture: http://127.0.0.1:5179'));
