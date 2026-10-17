const assert = require('node:assert/strict');
const fs = require('node:fs');
const http = require('node:http');
const path = require('node:path');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const root = path.resolve(__dirname, '../../Resources/Web/DeepLagoon');

(async () => {
  const server = http.createServer((req, res) => {
    const file = path.resolve(root, '.' + new URL(req.url, 'http://localhost').pathname);
    if (!file.startsWith(root + path.sep) || !fs.existsSync(file)) { res.writeHead(404).end(); return; }
    res.setHeader('Content-Type', ({ '.html':'text/html', '.css':'text/css', '.js':'application/javascript', '.woff2':'font/woff2' })[path.extname(file)] || 'application/octet-stream');
    res.end(fs.readFileSync(file));
  });
  await new Promise(r => server.listen(0, '127.0.0.1', r));
  let browser;
  try {
    browser = await chromium.launch({channel:process.env.BROWSER_CHANNEL || 'msedge',headless:true});
    const page = await browser.newPage({viewport:{width:440,height:730}});
    const errors=[];page.on('pageerror',e=>errors.push(String(e)));
    await page.addInitScript(() => {
      window.__sent=[];
      const NativeImage=window.Image;
      window.Image=class extends NativeImage {
        set src(url) {
          if (!url.startsWith('res://deeplagoon/Web/DeepLagoon/bridge?')) { super.src=url; return; }
          const [type,payload]=url.split('?')[1].split('&');
          window.__sent.push({type:decodeURIComponent(type),payload:JSON.parse(decodeURIComponent(payload))});
          queueMicrotask(()=>this.onload?.(new Event('load')));
        }
      };
    });
    await page.goto(`http://127.0.0.1:${server.address().port}/interface.html`);
    await page.waitForFunction(()=>window.__tguiAppBooted__);
    const locale=fs.readFileSync(path.resolve(__dirname,'../../Resources/Locale/ru-RU/_DeepLagoon/jukebox.ftl'),'utf8');
    const labels=Object.fromEntries([...locale.matchAll(/^dl-jukebox-([\w-]+) = (.*)$/gm)].map(m=>[m[1],m[2]]));
    const tracks=Array.from({length:2000},(_,i)=>({id:`track${i}`,name:i===0?'Вечер в баре':`Track ${i}`,length:222+i%100}));
    const data={tracks,labels,selected:'track0',position:83,gain:.7,playing:true,paused:false,powered:true,repeat:0,revision:7,
      queue:[{id:'entry1',track:'track0'},{id:'entry2',track:'track0'},{id:'entry3',track:'track1'}],
      favorites:[{id:'track0'},{id:'missing'}],playlists:[{name:'Вечер',tracks:[{id:'track0'}]}],libraryLoaded:true,libraryBusy:false,status:''};
    await page.evaluate(data=>window.update(JSON.stringify({type:'update',payload:{replaceData:true,config:{interface:'Jukebox',status:2,window:{key:'deeplagoon',fancy:false}},data}})),data);
    await page.locator('.Jukebox__row').first().waitFor();
    assert.equal(await page.locator('.Jukebox__row').count(),25,'Bounded catalog DOM');
    assert.equal(await page.getByText('Общая очередь автомата · личное избранное').count(),0);
    const wood=await page.locator('.Jukebox').evaluate(el=>getComputedStyle(el).backgroundImage);
    assert.ok(wood.includes('repeating-radial-gradient'),'Static wood grain');
    const action=async(type)=>page.evaluate(type=>window.__sent.filter(m=>m.type===`act/${type}`).at(-1),type);
    await page.getByRole('button',{name:'Очередь · 3',exact:true}).click();
    await page.locator('.Jukebox__row').nth(1).getByRole('button',{name:'Переместить выше',exact:true}).click();
    await page.waitForFunction(()=>window.__sent.some(m=>m.type==='act/move'));
    const moved=(await action('move')).payload;
    assert.deepEqual({revision:moved.revision,id:moved.id,index:moved.index},{revision:7,id:'entry2',index:0});
    await page.getByRole('button',{name:'Повтор: выкл.',exact:true}).click();
    await page.waitForFunction(()=>window.__sent.some(m=>m.type==='act/repeat'));
    assert.equal((await action('repeat')).payload.index,1);
    const gain=page.getByRole('slider',{name:'Громкость',exact:true});
    await gain.evaluate(el=>{el.value='35';el.dispatchEvent(new Event('input',{bubbles:true}));el.dispatchEvent(new PointerEvent('pointerup',{bubbles:true}));});
    await page.waitForFunction(()=>window.__sent.some(m=>m.type==='act/gain'));
    assert.equal((await action('gain')).payload.value,.35);
    await page.getByRole('button',{name:'Избранное',exact:true}).click();
    assert.equal(await page.getByText('Трек недоступен',{exact:true}).count(),1);
    await page.getByRole('button',{name:'Каталог',exact:true}).click();
    await page.getByRole('searchbox').fill('Вечер');
    assert.equal(await page.locator('.Jukebox__row').count(),1);
    await page.locator('.Jukebox__row').getByRole('button',{name:'В конец очереди',exact:true}).click();
    await page.waitForFunction(()=>window.__sent.some(m=>m.type==='act/add'));
    assert.equal((await action('add')).payload.id,'track0');
    await page.getByRole('button',{name:'Новый плейлист',exact:true}).click();
    await page.getByRole('textbox',{name:'Название плейлиста',exact:true}).fill('Мой вечер');
    await page.getByRole('button',{name:'Создать',exact:true}).click();
    await page.waitForFunction(()=>window.__sent.some(m=>m.type==='act/playlist-create'));
    assert.equal((await action('playlist-create')).payload.value,'Мой вечер');
    await page.evaluate(()=>window.update(JSON.stringify({type:'update',payload:{data:{libraryBusy:false,status:'save-error'}}})));
    assert.equal(await page.getByRole('textbox',{name:'Название плейлиста',exact:true}).inputValue(),'Мой вечер','Save failures retain draft');
    await page.getByRole('button',{name:'Закрыть',exact:true}).click();
    await page.getByRole('button',{name:'Импорт и экспорт списков',exact:true}).click();
    await page.getByRole('button',{name:'Экспорт',exact:true}).click();
    const exported=JSON.parse(await page.getByRole('textbox',{name:'Экспорт',exact:true}).inputValue());
    assert.deepEqual(exported,{Favorites:['track0','missing'],Playlists:{'Вечер':['track0']}});
    await page.getByRole('button',{name:'Закрыть',exact:true}).click();
    await page.evaluate(()=>window.update(JSON.stringify({type:'update',payload:{data:{libraryLoaded:false,libraryBusy:false,status:'save-error'}}})));
    await page.getByRole('button',{name:'Загрузить списки',exact:true}).click();
    await page.waitForFunction(()=>window.__sent.some(m=>m.type==='act/library-load'));
    await page.evaluate(()=>window.update(JSON.stringify({type:'update',payload:{data:{libraryLoaded:true,libraryBusy:false,status:''}}})));
    await page.getByRole('button',{name:'Очередь · 3',exact:true}).click();
    await page.getByRole('button',{name:'Очистить очередь',exact:true}).click();
    await page.getByRole('dialog').getByRole('button',{name:'Очистить очередь',exact:true}).click();
    await page.waitForFunction(()=>window.__sent.some(m=>m.type==='act/clear'));
    assert.equal((await action('clear')).payload.revision,7);
    await page.getByRole('button',{name:'Каталог',exact:true}).click();
    for(const size of [{width:440,height:730},{width:360,height:540},{width:620,height:800}]) {
      await page.setViewportSize(size);
      const layout=await page.evaluate(()=>({scroll:document.documentElement.scrollWidth>innerWidth||document.documentElement.scrollHeight>innerHeight,
        panel:document.querySelector('.Jukebox__panel').getBoundingClientRect().bottom,base:document.querySelector('.Jukebox__speaker').getBoundingClientRect().top,
        buttons:[...document.querySelectorAll('.Jukebox button')].filter(b=>{const r=b.getBoundingClientRect();return r.width>0&&(r.right>innerWidth+1||r.bottom>innerHeight+1);}).length}));
      assert.equal(layout.scroll,false,JSON.stringify({size,layout}));assert.ok(layout.panel<=layout.base,JSON.stringify({size,layout}));assert.equal(layout.buttons,0);
    }
    await page.setViewportSize({width:440,height:730});
    await page.getByRole('searchbox').fill('');
    await page.evaluate(()=>window.update(JSON.stringify({type:'update',payload:{data:{status:''}}})));
    await page.screenshot({path:process.env.JUKEBOX_SCREENSHOT || path.join(require('node:os').tmpdir(),'deeplagoon-jukebox.png'),omitBackground:true});
    assert.deepEqual(errors,[]);
    console.log('PASS: production Jukebox, 2000-track pagination, duplicate-entry reorder/revision, repeat, volume, search, enqueue, unavailable tracks, Cyrillic playlist draft/error, 3 sizes, wood grain, removed footer.');
  } finally { await browser?.close(); await new Promise(r=>server.close(r)); }
})().catch(e=>{console.error(e);process.exitCode=1;});
