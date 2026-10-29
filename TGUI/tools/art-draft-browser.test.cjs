const assert = require('node:assert/strict');
const fs = require('node:fs');
const http = require('node:http');
const path = require('node:path');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const repo = path.resolve(__dirname, '../..'), assets = path.join(repo, 'Resources/Web/DeepLagoon');
const locale = fs.readFileSync(path.join(repo, 'Resources/Locale/ru-RU/_DeepLagoon/art.ftl'), 'utf8');
const labels = Object.fromEntries([...locale.matchAll(/^dl-art-ui-([\w-]+) = (.*)$/gm)].map(m => [m[1], m[2]]));
const empty = JSON.stringify({version:1,title:'',layers:[{name:'Слой 1',visible:true,opacity:1,png:'',mask:''}]});
const ticket = 'a'.repeat(64), token = 'b'.repeat(64), key = 'canvas:account';
let rejectDraft = true, archive = 0, drafts = 0, discarding = false, overlap = false, releases = 0;
const data = {document:empty,savedDocument:empty,hasDraft:false,recoveryKey:key,draftEpoch:1,draftVersion:0,draftWriter:'',draftSequence:0,
  revision:1,tool:'brush',color:'#d04020',labels,maxDocument:8388608,chunkSize:5000,external:true};
const resetDraft = () => {data.hasDraft=false;data.draftEpoch++;data.draftVersion++;data.draftWriter='';data.draftSequence=0;};
(async () => {
 const server = http.createServer(async (req,res) => {
  const route = new URL(req.url,'http://localhost').pathname.replace('/art-tablet/','');
  const json = body => res.writeHead(200,{'Content-Type':'application/json','Cache-Control':'no-store'}).end(JSON.stringify(body));
  if (['claim','session','ping','draft','discard','save','release'].includes(route)) {
   if (req.headers.authorization !== 'Bearer ' + (route==='claim'?ticket:token)) return res.writeHead(410).end();
   if (route==='claim') return json({token,data});
   if (route==='session') return json({data});
   if (route==='ping') return json({active:true,revision:data.revision});
   if (route==='release') {releases++;overlap ||= discarding;return json({closed:true});}
   if (route==='discard') {discarding=true;await new Promise(r=>setTimeout(r,200));resetDraft();data.document=data.savedDocument;discarding=false;return json({data});}
   let body='';for await (const chunk of req) body+=chunk;
   assert.equal(req.headers['if-match'],String(data.revision));
   if (route==='draft') {
    drafts++;
    if (rejectDraft) return res.writeHead(503).end();
    const writer=req.headers['x-art-draft-writer'],sequence=Number(req.headers['x-art-draft-sequence']),epoch=Number(req.headers['x-art-draft-epoch']);
    const success=epoch===data.draftEpoch && (writer===data.draftWriter || Number(req.headers['x-art-draft-version'])===data.draftVersion);
    if(success && (writer!==data.draftWriter || sequence>data.draftSequence)) {
     data.document=body;data.hasDraft=true;data.draftVersion++;data.draftWriter=writer;data.draftSequence=sequence;
    }
    return json({ack:{writer,sequence,epoch:data.draftEpoch,version:data.draftVersion,success}});
   }
   archive++;resetDraft();data.document=data.savedDocument=body;data.revision++;data.result=archive;data.status='Сохранено';return json({data});
  }
  const file = route==='recovery-test.mjs'?path.join(repo,'TGUI/packages/tgui/interfaces/artDraftRecovery.mjs'):path.resolve(assets,route||'tablet.html');
  if (!fs.existsSync(file)) return res.writeHead(404).end();
  res.setHeader('Content-Type',({'.html':'text/html','.js':'application/javascript','.mjs':'application/javascript','.css':'text/css','.woff2':'font/woff2'})[path.extname(file)]||'application/octet-stream');
  fs.createReadStream(file).pipe(res);
 });
 await new Promise(r=>server.listen(0,'127.0.0.1',r));
 let browser;
 try {
  browser=await chromium.launch({channel:process.env.BROWSER_CHANNEL||'msedge',headless:true});
  const context=await browser.newContext({viewport:{width:980,height:720}}), errors=[];
  const url=`http://127.0.0.1:${server.address().port}/art-tablet/`;
  const open=async()=>{const p=await context.newPage();p.on('pageerror',e=>errors.push(String(e)));await p.goto(url+'#'+ticket);await p.waitForFunction(()=>document.querySelector('.ArtStudio__save')&&!document.querySelector('.ArtStudio__save').disabled);return p;};
  const readCache=p=>p.evaluate(async key=>{const m=await import('./recovery-test.mjs');return m.readArtDraft(key);},key);
  const cached=async p=>{await p.waitForFunction(async key=>{const m=await import('./recovery-test.mjs');return !!await m.readArtDraft(key);},key);return readCache(p);};
  const image=p=>p.locator('.ArtStudio__stage canvas').evaluate(c=>c.toDataURL());
  const stroke=async(p,y=.45)=>{const b=await p.locator('.ArtStudio__stage canvas').boundingBox();await p.mouse.move(b.x+b.width*.2,b.y+b.height*y);await p.mouse.down();await p.mouse.move(b.x+b.width*.75,b.y+b.height*y,{steps:8});await p.mouse.up();};
  let page=await open();const blank=await image(page);
  await stroke(page);
  await page.getByRole('button',{name:labels['add-layer'],exact:true}).click();
  await page.getByRole('button',{name:labels['add-mask'],exact:true}).click();
  await stroke(page,.65);
  const firstImage=await image(page), local=await cached(page);
  assert.equal(JSON.parse(local.document).layers.length,2);
  assert.ok(JSON.parse(local.document).layers.some(l=>l.mask.length>0));
  await page.waitForFunction(text=>document.querySelector('footer').textContent===text,labels['draft-failed']);
  assert.equal(data.document,empty,'Unconfirmed autosave cannot change the server baseline');
  await page.close();data.revision++;
  page=await open();await page.waitForFunction(expected=>document.querySelector('canvas').toDataURL()===expected,firstImage);
  assert.notEqual(await image(page),blank,'Forced closure recovers the newer local strokes, layers and masks');
  rejectDraft=false;
  await page.waitForFunction(text=>document.querySelector('footer').textContent===text,labels['draft-saved']);
  assert.equal(archive,0,'Draft autosave never publishes to the library');assert.equal(data.hasDraft,true);
  assert.equal(JSON.parse(data.document).layers.length,2);
  const accepted=data.document;
  await page.close();data.revision++;
  // Remove the browser fallback: reopening must recover from the canvas on the server alone.
  const helper=await context.newPage();await helper.goto(url);await helper.evaluate(async key=>{const m=await import('./recovery-test.mjs');await m.deleteArtDraft(key);},key);await helper.close();
  page=await open();await page.waitForFunction(expected=>document.querySelector('canvas').toDataURL()===expected,firstImage);
  assert.equal(data.savedDocument,empty);assert.equal(data.document,accepted);
  await page.getByRole('button',{name:labels.discard,exact:true}).click();
  await Promise.all([page.waitForResponse(r=>r.url().endsWith('/discard')),page.getByRole('dialog').getByRole('button',{name:labels.discard,exact:true}).click()]);
  await page.waitForFunction(expected=>document.querySelector('canvas').toDataURL()===expected,blank);
  await page.close();data.revision++;
  const stale=await context.newPage();await stale.goto(url);await stale.evaluate(async record=>{const m=await import('./recovery-test.mjs');await m.writeArtDraft(record);},local);await stale.close();
  page=await open();assert.equal(await image(page),blank,'Discard epoch rejects stale local backups');
  await stroke(page);await page.keyboard.press('Control+s');await page.getByRole('dialog').getByRole('textbox',{name:labels.name,exact:true}).fill('Защищённый рисунок');await page.keyboard.press('Control+s');
  await page.waitForFunction(()=>document.querySelector('footer').textContent==='Сохранено');
  assert.equal(archive,1);assert.equal(data.hasDraft,false);assert.equal(await readCache(page),null,'Explicit save clears the recovery copy');
  const savedImage=await image(page);
  await stroke(page,.7);await page.waitForFunction(text=>document.querySelector('footer').textContent===text,labels['draft-saved']);
  const newerImage=await image(page);assert.notEqual(newerImage,savedImage);
  await page.close();data.revision++;
  page=await open();await page.waitForFunction(expected=>document.querySelector('canvas').toDataURL()===expected,newerImage);
  await page.getByRole('button',{name:labels.discard,exact:true}).click();await Promise.all([page.waitForResponse(r=>r.url().endsWith('/discard')),page.getByRole('dialog').getByRole('button',{name:labels.discard,exact:true}).click()]);
  await page.waitForFunction(expected=>document.querySelector('canvas').toDataURL()===expected,savedImage);
  assert.equal(archive,1,'Recovering and discarding do not archive');
  // Exercise persistent storage across writers and its bounded retention contract.
  await page.evaluate(async()=>{
   const m=await import('./recovery-test.mjs'),writer='f'.repeat(32);
   const record={key:'order',epoch:2,baseVersion:0,writer,sequence:2,document:'new',updated:Date.now()};
   await m.writeArtDraft(record);await m.writeArtDraft({...record,epoch:1,sequence:99,document:'stale',updated:Date.now()+1});
   if((await m.readArtDraft('order')).document!=='new')throw Error('An older epoch replaced a new draft');
   await m.deleteArtDraft('order',{...record,writer:'a'.repeat(32),sequence:99});
   if(!(await m.readArtDraft('order')))throw Error('A stale tab deleted another writer\'s backup');
   for(let i=0;i<12;i++)await m.writeArtDraft({...record,key:'bounded'+i,updated:Date.now()+i});
   const db=await new Promise((ok,no)=>{const r=indexedDB.open('deeplagoon-art-drafts',1);r.onsuccess=()=>ok(r.result);r.onerror=()=>no(r.error);});
   const all=await new Promise(ok=>{const r=db.transaction('drafts').objectStore('drafts').getAll();r.onsuccess=()=>ok(r.result);});
   if(all.length>8)throw Error('Unbounded local draft retention');
   await m.writeArtDraft({...record,key:'expired',updated:Date.now()-8*24*60*60*1000});
   if(await m.readArtDraft('expired'))throw Error('Expired draft was retained');db.close();
  });
  // Intentional close must preserve the latest unsent stroke, even if its first upload fails.
  rejectDraft=true;await stroke(page,.8);const closeImage=await image(page);
  const rejected=page.waitForResponse(r=>r.url().endsWith('/draft')&&r.status()===503);
  await page.getByRole('button',{name:labels['return-game'],exact:true}).click();await rejected;
  assert.equal(releases,0,'Do not release the lease before the final draft is accepted');
  assert.equal(await page.getByRole('dialog').count(),0,'Close must not require publishing or discarding');
  rejectDraft=false;await page.locator('[role="status"]').waitFor();
  assert.equal(releases,1);assert.equal(overlap,false);assert.equal(data.hasDraft,true);assert.equal(archive,1);
  await page.close();data.revision++;
  page=await open();await page.waitForFunction(expected=>document.querySelector('canvas').toDataURL()===expected,closeImage);
  // Escape follows the same close path, retaining a confirmed draft without creating an archive entry.
  await page.keyboard.press('Escape');await page.locator('[role="status"]').waitFor();
  assert.equal(releases,2);assert.equal(archive,1);assert.equal(data.hasDraft,true);
  assert.deepEqual(errors,[]);assert.ok(drafts>0);
  console.log('PASS: production editor autosave, forced-close local recovery, server-only recovery, layers/masks, no archive on draft, discard replay protection, explicit save and separate saved baseline, bounded persistent cache, intentional close/return/Escape with final draft acknowledgment and no library publication.');
 } finally {await browser?.close();await new Promise(r=>server.close(r));}
})().catch(e=>{console.error(e);process.exitCode=1;});
