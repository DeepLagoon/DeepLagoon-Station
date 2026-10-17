const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const repo = path.resolve(__dirname,'../..');
const resources = path.join(repo,'Resources/Web/DeepLagoon');
const output = path.join(repo,'TGUI/node_modules/.cache/civilian-console-production');
fs.mkdirSync(output,{recursive:true});

(async()=>{
  const server = http.createServer((req,res)=>{
    const file=path.resolve(resources,'.'+new URL(req.url,'http://localhost').pathname);
    if(!file.startsWith(resources+path.sep)||!fs.existsSync(file)){res.writeHead(404).end();return;}
    res.setHeader('Content-Type',({'.html':'text/html','.css':'text/css','.js':'application/javascript','.webp':'image/webp','.png':'image/png','.woff2':'font/woff2'})[path.extname(file)]||'application/octet-stream');
    res.end(fs.readFileSync(file));
  });
  await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
  let browser;
  try {
    browser=await chromium.launch({channel:'msedge',headless:true});
    const page=await browser.newPage({viewport:{width:780,height:560}});
    const errors=[];page.on('pageerror',e=>errors.push(String(e)));
    await page.addInitScript(()=>{
      window.__sent=[];const NativeImage=window.Image;
      window.Image=class extends NativeImage{
        set src(value){
          if(!value.startsWith('res://deeplagoon/Web/DeepLagoon/bridge?')){super.src=value;return;}
          const [type,payload]=value.split('?')[1].split('&');
          window.__sent.push({type:decodeURIComponent(type),payload:JSON.parse(decodeURIComponent(payload))});
          queueMicrotask(()=>this.onload?.(new Event('load')));
        }
      };
    });
    await page.goto(`http://127.0.0.1:${server.address().port}/interface.html`);
    await page.waitForFunction(()=>window.__tguiAppBooted__);
    const publish=(name,data)=>page.evaluate(({name,data})=>window.update(JSON.stringify({type:'update',payload:{
      replaceData:true,replaceStaticData:true,resetPartialData:true,
      config:{interface:name,status:2,title:data.title||name,window:{key:'civilian',fancy:false}},data,
    }})),{name,data});
    const sent=()=>page.evaluate(()=>window.__sent.filter(m=>m.type.startsWith('act/')).map(m=>{
      const {_page,_sequence,...payload}=m.payload;return {type:m.type,payload};
    }));
    const data={title:'Терминал дисков исследований',serverLabel:'Список серверов',closeLabel:'Закрыть',waitingLabel:'Ожидание данных сервера…',
      totalLabel:'На выбранном сервере имеется 17000 очков',ready:true,
      entries:[{id:'1k',label:'Печать диска очков (1000)',disabled:false},{id:'5k',label:'Печать диска очков (5000)',disabled:true},{id:'10k',label:'Печать диска очков (10000)',disabled:false}]};
    await publish('DiskConsole',{...data,ready:false,totalLabel:''});
    await page.locator('.DiskConsole').waitFor();
    assert.equal(await page.locator('.DiskConsole__operation:disabled').count(),3);
    await publish('DiskConsole',data);
    // Both native (gameplay/radar) and DOM input animate the physical caps.
    // Visual feedback must not manufacture a print/server action.
    const beforeKeys=(await sent()).length;
    // Compare only the physical key deck; live screen contents are irrelevant.
    const keyboardClip={x:43,y:468,width:694,height:92};
    const keyboardBefore=await page.screenshot({clip:keyboardClip});
    await page.keyboard.down('a');
    await page.locator('.ConsoleKeyboard [data-key="KeyA"]').waitFor();
    await page.screenshot({path:path.join(output,'keyboard-a-held.png')});
    assert.notDeepEqual(await page.screenshot({clip:keyboardClip}),keyboardBefore,'The artwork must visibly depress, not just set a DOM attribute');
    await page.keyboard.up('a');
    await page.waitForFunction(()=>!document.querySelector('.ConsoleKeyboard [data-pressed]'));
    assert.deepEqual(await page.screenshot({clip:keyboardClip}),keyboardBefore,'The cap must return to its exact original appearance');
    const nativeKey=(code,down)=>page.evaluate(({code,down})=>window.update(JSON.stringify({type:'console/key',payload:{code,down}})),{code,down});
    await nativeKey('KeyW',true);
    await page.locator('.ConsoleKeyboard [data-key="KeyW"]').waitFor();
    await nativeKey('KeyW',false);
    await page.waitForFunction(()=>!document.querySelector('.ConsoleKeyboard [data-pressed]'));
    await nativeKey('Pointer',true);
    await page.locator('.ConsoleKeyboard [data-key="Enter"]').waitFor();
    await nativeKey('Pointer',false);
    await page.waitForFunction(()=>!document.querySelector('.ConsoleKeyboard [data-pressed]'));
    await page.keyboard.down('s');
    await page.evaluate(()=>window.dispatchEvent(new Event('blur')));
    await page.waitForFunction(()=>!document.querySelector('.ConsoleKeyboard [data-pressed]'));
    await page.keyboard.up('s');
    assert.equal((await sent()).length,beforeKeys,'Keyboard feedback must never send game actions');
    const printButton=page.getByRole('button',{name:data.entries[0].label,exact:true});
    await printButton.hover();await page.mouse.down();
    await page.locator('.ConsoleKeyboard [data-key="Enter"]').waitFor();
    await page.screenshot({path:path.join(output,'keyboard-click-held.png')});
    await page.mouse.up();
    await page.waitForFunction(()=>!document.querySelector('.ConsoleKeyboard [data-pressed]'));
    // The real click above prints exactly once, without an animated duplicate.
    await page.waitForFunction(()=>window.__sent.some(m=>m.type==='act/print'));
    assert.equal((await sent()).filter(m=>m.type==='act/print').length,1);
    await page.getByRole('button',{name:data.entries[0].label,exact:true}).click();
    await page.waitForFunction(()=>window.__sent.some(m=>m.type==='act/print'));
    assert.deepEqual((await sent()).at(-1),{type:'act/print',payload:{id:'1k'}});
    assert.equal(await page.getByRole('button',{name:data.entries[1].label,exact:true}).isDisabled(),true);
    await page.getByRole('button',{name:'Список серверов',exact:true}).click();
    await page.waitForFunction(()=>window.__sent.some(m=>m.type==='act/server'));
    for(const [width,height] of [[780,560],[520,400],[1100,740],[560,1000]]){
      await page.setViewportSize({width,height});
      await page.locator('.DiskConsole__operation').last().scrollIntoViewIfNeeded();
      const dimensions=await page.evaluate(()=>{
        const frame=document.querySelector('.CivilianConsoleFrame').getBoundingClientRect();
        const last=document.querySelector('.DiskConsole__operations').lastElementChild.getBoundingClientRect();
        const body=document.querySelector('.DiskConsole__body').getBoundingClientRect();
        return {overflow:document.documentElement.scrollWidth>innerWidth,frame:frame.toJSON(),last:last.toJSON(),body:body.toJSON()};
      });
      assert.equal(dimensions.overflow,false);
      assert.ok(dimensions.last.bottom<=dimensions.body.bottom+1,'Every print option remains reachable inside the screen');
      assert.equal(dimensions.frame.width,width);assert.equal(dimensions.frame.height,height);
      assert.equal(await page.evaluate(()=>getComputedStyle(document.body).backgroundColor),'rgb(13, 19, 24)');
      await page.screenshot({path:path.join(output,`disks-${width}.png`)});
    }
    await publish('DiskConsole',{...data,title:'Терминал технологических дисков',entries:[{id:'standard',label:'Печать диска (1000)'},{id:'rare',label:'Печать редкого диска (10000)'}]});
    assert.equal(await page.locator('.DiskConsole__operation').count(),2);
    await page.getByRole('button',{name:'Печать редкого диска (10000)',exact:true}).click();
    await page.waitForFunction(()=>window.__sent.some(m=>m.type==='act/print'&&m.payload.id==='rare'));
    const labels=Object.fromEntries([...fs.readFileSync(path.join(repo,'Resources/Locale/ru-RU/_DeepLagoon/research-console.ftl'),'utf8')
      .matchAll(/^(dl-research-[\w-]+)\s*=\s*(.*)$/gm)].map(m=>[m[1],m[2]]));
    const base={id:'basic',name:'Базовые исследования',discipline:'science',tier:1,cost:500,prerequisites:[],recipes:[],effects:[]};
    const research={labels,disciplines:[{id:'science',name:'Наука',shortName:'Наука',color:'#8ab9d0',icon:''}],
      points:17000,hasAccess:true,techs:[base,{...base,id:'advanced',name:'Продвинутые исследования',cost:1000,prerequisites:[{id:'basic',name:base.name}]}],
      states:[{id:'basic',state:'researched'},{id:'advanced',state:'available'}]};
    for(const [width,height] of [[1100,740],[760,480],[1280,960]]){
      await page.setViewportSize({width,height});
      await publish('ResearchConsole',research);
      await page.locator('.CivilianConsoleFrame .ResearchConsole .TechNode').first().waitFor();
      const bounds=await page.evaluate(()=>{
        const screen=document.querySelector('.ResearchConsole').getBoundingClientRect();
        const body=document.querySelector('.ResearchConsole__body').getBoundingClientRect();
        return {screen:screen.toJSON(),body:body.toJSON(),overflow:document.documentElement.scrollWidth>innerWidth};
      });
      assert.equal(bounds.overflow,false);assert.ok(bounds.screen.bottom<height&&bounds.body.height>100);
      await page.locator('.TopBar').getByLabel(labels['dl-research-servers'],{exact:true}).click();
      await page.waitForFunction(()=>window.__sent.some(m=>m.type==='act/servers'));
      await page.locator('.TopBar__points').hover();
      await page.locator('.Tip').waitFor();
      const tip=await page.locator('.Tip').boundingBox();
      assert.ok(tip.x>=bounds.screen.left&&tip.y>=bounds.screen.top);
      assert.ok(tip.x+tip.width<=bounds.screen.right+1&&tip.y+tip.height<=bounds.screen.bottom+1,'Research tips must stay inside the glass');
      await page.screenshot({path:path.join(output,`research-${width}.png`)});
    }
    assert.deepEqual(errors,[]);
    console.log('PASS: shipped DiskConsole (both types), state gates, action bridge, shell, 4 sizes; ResearchConsole shell/tree/server action at 3 sizes.');
  } finally {await browser?.close();await new Promise(resolve=>server.close(resolve));}
})().catch(error=>{console.error(error);process.exitCode=1;});
