const fs=require('node:fs'),path=require('node:path'),http=require('node:http'),assert=require('node:assert/strict');
const root=path.resolve(__dirname,'..').replaceAll('\\','/'),{build}=require(root+'/node_modules/esbuild'),sass=require(root+'/node_modules/sass'),{chromium}=require('playwright');
const output=process.env.GRID_EDITOR_OUTPUT||path.resolve(root,'../.local/codex/grid-editor-transfer/browser');
fs.mkdirSync(output,{recursive:true});
(async()=>{
 const fixture=path.join(output,'fixture.jsx').replaceAll('\\','/');
 fs.writeFileSync(fixture,`
import React,{createContext,useState}from'react';import{createRoot}from'react-dom/client';
import{GridEditor}from'${root}/packages/tgui/interfaces/GridEditor.jsx';export const Context=createContext(null);
const sprite='data:image/svg+xml;base64,'+btoa('<svg xmlns="http://www.w3.org/2000/svg" width="32" height="32"><rect width="32" height="32" fill="white"/></svg>');
const devices=['GasVentPump','GasVentScrubberVox','GasOutletInjector','DisposalUnit','MailingUnit'].map(id=>({id,name:id,kind:id.startsWith('Gas')?'gas':'disposal',ports:2}));
const visuals=['Plating','ChairWood','GasPipeHalfAlt2','GasPipeHalf','DisposalPipe','DisposalTrunk',...devices.map(d=>d.id)].map(id=>({id,name:id,kind:id==='Plating'?'tile':'entity',images:[{url:sprite}]}));
const item=(id,prototype,x,y,fields={})=>({id,prototype,kind:'entity',x,y,rotation:0,anchored:true,color:'#FFFFFFFF',z:0,flags:0,variant:0,mirror:0,...fields});
const initial={token:'transfer',grid:'test',x:0,y:0,width:8,height:8,copySupported:true,routeDevices:devices,
items:[...Array.from({length:64},(_,i)=>({...item('t:'+i,'Plating',i%8,Math.floor(i/8)),kind:'tile'})),
item('e:chair','ChairWood',1.5,5.5,{anchored:false}),item('gas-end','GasPipeHalfAlt2',3.5,2.5,{pipeLayer:2}),
item('gas-neighbor','GasPipeHalfAlt2',3.5,1.5,{rotation:180,pipeLayer:2}),item('parallel','GasPipeHalf',3.5,2.5,{pipeLayer:0}),
item('disposal-end','DisposalPipe',5.5,2.5),item('disposal-neighbor','DisposalPipe',5.5,1.5)],
visuals,catalog:[],catalogCount:0,hotbar:[],favorites:[],templates:[]};
const native='native: "state"\\n'+('SettingsWithUnicodeЖ\\n'.repeat(5000));window.actions=[];window.submissions=[];
function App(){const[data,setData]=useState(initial);window.data=()=>data;const update=fields=>setData(d=>({...d,...fields}));
 const uploads=React.useRef({});const act=(action,p={})=>{window.actions.push({action,...p});
  if(action==='routeVisuals')update({routeVisuals:visuals});
  if(action==='search')update({catalog:[],catalogCount:0,catalogPage:p.page,catalogCategory:p.category,catalogQuery:p.query});
  if(action==='applyStart'||action==='previewStart'){const preview=action==='previewStart',key=preview?'preview':'upload';uploads.current[key]={...p,items:[],text:''};update({[key+'Id']:p.id,[key+'Next']:0});}
  if(action==='applyChunk'||action==='previewChunk'){const preview=action==='previewChunk',key=preview?'preview':'upload',u=uploads.current[key];
    if(u.encoding)u.text+=p.text;else u.items.push(...p.items);
    if(p.index+1<u.chunks){update({[key+'Next']:p.index+1});return;}
    const items=u.encoding?JSON.parse(u.text):u.items;
    if(preview){update({previewFinished:p.id});return;}
    if(u.purpose==='copy'){update({copyItems:items.map(i=>({...i,entityData:i.kind==='entity'?native:'',entityGroup:'captured'})),copyVersion:p.id,resultVersion:p.id,success:true});return;}
    window.submissions.push(items);update({resultVersion:p.id,success:true});
  }
 };return <Context.Provider value={{data,act}}><GridEditor/></Context.Provider>;
}createRoot(document.getElementById('root')).render(<App/>);
 `);
 await build({entryPoints:[fixture],outfile:path.join(output,'test.js'),bundle:true,jsx:'automatic',nodePaths:[root+'/node_modules'],define:{'process.env.NODE_ENV':'"production"'},plugins:[{name:'fixture',setup(build){build.onResolve({filter:/^\.\.\/backend$/},()=>({path:'backend',namespace:'fixture'}));build.onLoad({filter:/.*/,namespace:'fixture'},()=>({contents:`import{useContext}from'react';import{Context}from${JSON.stringify(fixture)};export const useBackend=()=>useContext(Context);`,resolveDir:root}));build.onLoad({filter:/\.scss$/},()=>({contents:'',loader:'css'}));}}]});
 const css=sass.compile(root+'/packages/tgui/styles/reset.scss').css+sass.compile(root+'/packages/tgui/interfaces/GridEditor.scss').css;
 const server=http.createServer((req,res)=>{res.setHeader('Content-Type',req.url==='/test.js'?'text/javascript':'text/html');res.end(req.url==='/test.js'?fs.readFileSync(path.join(output,'test.js')):`<html><meta charset="utf-8"><style>html,body,#root{height:100%;margin:0}*{box-sizing:border-box}${css}</style><div id="root"></div><script src="/test.js"></script></html>`);});
 await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));const browser=await chromium.launch({channel:'msedge',headless:true}),errors=[];
 try{
  const page=await browser.newPage({viewport:{width:1440,height:900}});page.on('pageerror',error=>errors.push(error.message));await page.goto('http://127.0.0.1:'+server.address().port);await page.waitForSelector('.GridEditor__loading',{state:'hidden'});
  await page.locator('.GridEditor__canvas').waitFor();await page.evaluate(()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve))));
  const point=async(x,y)=>page.locator('.GridEditor__canvas').evaluate((element,{x,y})=>{const b=element.getBoundingClientRect();return{x:b.left+(x+.5)*b.width/8,y:b.top+(8-y-.5)*b.height/8};},{x,y});
  const click=async(x,y,button='left')=>{const p=await point(x,y);await page.mouse.click(p.x,p.y,{button});};
  await click(1,5);await page.keyboard.press('Control+c');await page.waitForFunction(()=>window.data().copyVersion);
  await page.mouse.move((await point(1,4)).x,(await point(1,4)).y);await page.locator('.GridEditor__canvas').focus();
  await page.evaluate(()=>window.dispatchEvent(new CustomEvent('grid-editor-key',{detail:{code:'KeyV',ctrlKey:true,down:true}})));
  await page.getByRole('button',{name:'Применить',exact:true}).click();await page.waitForFunction(()=>window.submissions.length===1);
  const copied=await page.evaluate(()=>window.submissions[0].find(item=>item.prototype==='ChairWood'));
  assert.ok(copied.entityData.length>100000);assert.ok(copied.entityGroup!=='captured');
  assert.ok(await page.evaluate(()=>window.actions.filter(a=>a.action==='applyChunk'||a.action==='previewChunk').every(a=>JSON.stringify(a).length<8192)));
  await page.getByRole('button',{name:'Сети',exact:true}).click();
  const select=async(name,value)=>{await page.getByRole('combobox',{name,exact:true}).click();await page.getByRole('option',{name:value,exact:true}).click();};
  await select('Режим трассы','Газовые трубы');await select('Устройство на СКМ','GasVentScrubberVox');
  await page.getByRole('button',{name:'Трасса · 4',exact:true}).click();await click(3,1);await click(3,2,'middle');
  await page.getByRole('button',{name:'Применить',exact:true}).click();await page.waitForFunction(()=>window.submissions.length===2);
  const gas=await page.evaluate(()=>window.submissions[1]);assert.ok(gas.some(item=>item.prototype==='GasVentScrubberVox'&&item.pipeLayer===2&&item.rotation===0));assert.ok(!gas.some(item=>item.id==='parallel'&&item.kind==='remove'));
  await select('Режим трассы','Мусоросброс');await select('Устройство на СКМ','MailingUnit');await click(5,2,'middle');
  await page.getByRole('button',{name:'Применить',exact:true}).click();await page.waitForFunction(()=>window.submissions.length===3);
  const disposal=await page.evaluate(()=>window.submissions[2]);assert.ok(disposal.some(item=>item.prototype==='DisposalTrunk'&&item.rotation===0));assert.ok(disposal.some(item=>item.prototype==='MailingUnit'));
  assert.deepEqual(errors,[]);await page.screenshot({path:path.join(output,'result.png')});console.log('PASS: asynchronous native-state copy, 100k+ CEF upload, gas MMB on route overlay uses layer 3, parallel layer preserved, mailing unit plus trunk, zero browser errors.');
 }finally{await browser.close();await new Promise(resolve=>server.close(resolve));}
})().catch(error=>{console.error(error);process.exitCode=1;});
