const assert = require('node:assert/strict');
const fs = require('node:fs');
const http = require('node:http');
const path = require('node:path');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const repo=path.resolve(__dirname,'../..'), assets=path.join(repo,'Resources/Web/DeepLagoon');
const output=path.join(repo,'.local/codex/art-input');fs.mkdirSync(output,{recursive:true});
const locale=fs.readFileSync(path.join(repo,'Resources/Locale/ru-RU/_DeepLagoon/art.ftl'),'utf8');
const labels=Object.fromEntries([...locale.matchAll(/^dl-art-ui-([\w-]+) = (.*)$/gm)].map(m=>[m[1],m[2]]));
const ticket='a'.repeat(64), token='b'.repeat(64), requests=[];
let claimed=false, active=true, fail=false, result=0;
const data={document:JSON.stringify({version:1,title:'',layers:[{name:'Слой 1',visible:true,opacity:1,png:'',mask:''}]}),revision:1,tool:'brush',color:'#d04020',labels,maxDocument:8388608,chunkSize:5000,external:true,recoveryKey:'tablet:canvas'};
(async()=>{
 const server=http.createServer(async(req,res)=>{
  res.setHeader('Content-Security-Policy', "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self' data:; connect-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'none'");
  const route=new URL(req.url,'http://localhost').pathname.replace('/art-tablet/','');
  requests.push({url:req.url,auth:req.headers.authorization,route});
  if(['claim','session','save','draft','release','ping'].includes(route)){
   const json=body=>res.writeHead(200,{'Content-Type':'application/json','Cache-Control':'no-store'}).end(JSON.stringify(body));
   if(route==='claim'){
    if(claimed||req.headers.authorization!=='Bearer '+ticket)return res.writeHead(410).end();
    claimed=true;return json({token,data});
   }
   if(!active||req.headers.authorization!=='Bearer '+token)return res.writeHead(410).end();
   if(route==='session')return json({data});
   if(route==='ping')return json({active:true,revision:data.revision});
   if(route==='release'){active=false;return json({closed:true});}
   let body='';for await(const chunk of req)body+=chunk;
   if(route==='draft')return json({ack:{writer:req.headers['x-art-draft-writer'],
    sequence:Number(req.headers['x-art-draft-sequence']),epoch:0,version:1,success:true}});
   if(fail)return res.writeHead(503).end();
   assert.equal(req.headers['if-match'],String(data.revision));
   data.document=body;data.revision++;data.result=++result;data.status='Сохранено';return json({data});
  }
  const file=path.resolve(assets,route||'tablet.html');
  if(!file.startsWith(assets+path.sep)||!fs.existsSync(file))return res.writeHead(404).end();
  res.setHeader('Content-Type',({'.html':'text/html','.js':'application/javascript','.css':'text/css','.ttf':'font/ttf'})[path.extname(file)]||'application/octet-stream');
  fs.createReadStream(file).pipe(res);
 });
 await new Promise(r=>server.listen(0,'127.0.0.1',r));
 let browser;
 try{
  browser=await chromium.launch({channel:process.env.BROWSER_CHANNEL||'msedge',headless:true});
  const page=await browser.newPage({viewport:{width:980,height:720}}),errors=[];
  page.on('pageerror',e=>errors.push(String(e)));
  const url=`http://127.0.0.1:${server.address().port}/art-tablet/`;
  await page.goto(url+'#'+ticket);
  await page.waitForFunction(()=>!!document.querySelector('.ArtStudio canvas'));
  await page.waitForFunction(()=>!document.querySelector('.ArtStudio__save').disabled);
  assert.equal(new URL(page.url()).hash,'','Ticket is removed from history');
  assert.equal(await page.getByRole('button',{name:labels.tablet,exact:true}).count(),0);
  assert.ok(await page.evaluate(async()=>{
    const faces=await document.fonts.load('900 16px "Font Awesome 6 Free"', '\uf1fc');
    return faces.some(face=>face.status==='loaded');
  }), 'The brush icon font loads under the tablet CSP');
  const initial=await page.locator('canvas').evaluate(c=>c.toDataURL());
  const box=await page.locator('canvas').boundingBox(), cdp=await page.context().newCDPSession(page);
  async function penStroke(y,force){
   await cdp.send('Input.dispatchMouseEvent',{type:'mousePressed',x:box.x+box.width*.25,y:box.y+box.height*y,button:'left',buttons:1,clickCount:1,pointerType:'pen',force});
   await cdp.send('Input.dispatchMouseEvent',{type:'mouseMoved',x:box.x+box.width*.7,y:box.y+box.height*y,buttons:1,pointerType:'pen',force});
   await cdp.send('Input.dispatchMouseEvent',{type:'mouseReleased',x:box.x+box.width*.7,y:box.y+box.height*y,button:'left',buttons:0,clickCount:1,pointerType:'pen',force:0});
  }
  const pixel=(y,offset=0)=>page.locator('canvas').evaluate((c,[y,offset])=>
    [...c.getContext('2d').getImageData(c.width/2,Math.round(c.height*y)+offset,1,1).data],[y,offset]);
  await page.getByRole('slider',{name:labels.size,exact:true}).fill('40');
  await penStroke(.25,.2);
  await penStroke(.45,.8);
  await page.waitForFunction(()=>document.querySelector('canvas').getContext('2d').getImageData(384,346,1,1).data[2]===32);
  assert.deepEqual(await pixel(.25),[208,64,32,255],'A light pen stroke is opaque at 100% brush opacity');
  assert.deepEqual(await pixel(.25,10),[247,240,221,255],'Default pressure still narrows the brush');
  assert.deepEqual(await pixel(.45,10),[208,64,32,255]);
  await page.getByRole('combobox',{name:labels['pen-pressure'],exact:true}).click();
  await page.getByRole('option',{name:labels['pressure-off'],exact:true}).click();
  await penStroke(.7,.2);
  await page.waitForFunction(()=>document.querySelector('canvas').getContext('2d').getImageData(384,548,1,1).data[2]===32);
  assert.deepEqual(await pixel(.7,10),[208,64,32,255],'Disabling pressure keeps the selected width and opacity');
  await cdp.detach();
  const save=async()=>{await page.keyboard.press('Control+s');await page.getByRole('dialog').getByRole('textbox',{name:labels.name,exact:true}).fill('Рисунок планшетом');await page.keyboard.press('Control+s');};
  fail=true;await save();await page.waitForFunction(text=>document.querySelector('footer').textContent===text,labels['tablet-error']);
  assert.notEqual(await page.locator('canvas').evaluate(c=>c.toDataURL()),initial,'Failure retains the pen draft');
  fail=false;await save();await page.waitForFunction(()=>document.querySelector('footer').textContent==='Сохранено');
  const saved=await page.locator('canvas').evaluate(c=>c.toDataURL());
  assert.equal(JSON.parse(data.document).title,'Рисунок планшетом');
  assert.ok(JSON.parse(data.document).layers[0].png.length>0);
  await page.screenshot({path:path.join(output,'tablet-browser.png')});
  await page.reload();await page.waitForFunction(()=>!!document.querySelector('canvas'));
  await page.waitForFunction(expected=>document.querySelector('canvas').toDataURL()===expected,saved);
  assert.equal(await page.getByRole('combobox',{name:labels['pen-pressure'],exact:true}).locator('span').first().textContent(),labels['pressure-off']);
  assert.equal(await page.getByRole('slider',{name:labels.size,exact:true}).inputValue(),'40');
  assert.equal(requests.filter(r=>r.route==='claim').length,1,'Reload uses the exchanged session rather than replaying the ticket');
  await page.getByRole('button',{name:labels['return-game'],exact:true}).click();
  await page.getByRole('status').waitFor();assert.equal(active,false);
  assert.ok(requests.every(r=>!r.url.includes(ticket)&&!r.url.includes(token)),'Credentials never enter HTTP URLs');
  await page.goto('about:blank');await page.goto(url+'#'+ticket);await page.getByRole('status').filter({hasText:'Доступ к холсту завершён'}).waitFor();
  assert.equal(await page.locator('canvas').count(),0,'A consumed ticket cannot reopen the editor');
  assert.deepEqual(errors,[]);
  console.log('PASS: standalone production editor, one-use fragment ticket, authenticated requests, pen pressure input, failed-save draft retention, save/reload, release and replay rejection.');
 }finally{await browser?.close();await new Promise(r=>server.close(r));}
})().catch(e=>{console.error(e);process.exitCode=1;});
