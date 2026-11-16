const assert = require('node:assert/strict');
const fs = require('node:fs');
const http = require('node:http');
const path = require('node:path');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const root = path.resolve(__dirname, '../../Resources/Web/DeepLagoon');
(async () => {
 const server = http.createServer((request,response)=>{
  const file=path.resolve(root,'.'+new URL(request.url,'http://localhost').pathname);
  if(!file.startsWith(root+path.sep)||!fs.existsSync(file))return response.writeHead(404).end();
  response.setHeader('Content-Type',({'.html':'text/html','.js':'application/javascript','.css':'text/css'})[path.extname(file)]||'application/octet-stream');response.end(fs.readFileSync(file));
 });
 await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));let browser;
 try {
  browser=await chromium.launch({channel:'msedge',headless:true});
  const page=await browser.newPage({viewport:{width:1280,height:860}});const errors=[];
  page.on('pageerror',e=>errors.push(String(e)));
  await page.addInitScript(()=>{window.__sent=[];const NativeImage=window.Image;window.Image=class extends NativeImage{set src(value){if(!value.startsWith('res://deeplagoon/Web/DeepLagoon/bridge?')){super.src=value;return;}const[type,payload]=value.split('?')[1].split('&');window.__sent.push({type:decodeURIComponent(type),payload:JSON.parse(decodeURIComponent(payload))});queueMicrotask(()=>this.onload?.(new Event('load')));}};});
  await page.goto(`http://127.0.0.1:${server.address().port}/interface.html`);await page.waitForFunction(()=>window.__tguiAppBooted__);
  const publish=(iface,data)=>page.evaluate(({iface,data})=>window.update(JSON.stringify({type:'update',payload:{replaceData:true,replaceStaticData:true,resetPartialData:true,config:{interface:iface,status:2,window:{key:'appearance-test',fancy:false}},data}})),{iface,data});
  const option=(id,part,category,races=[{id:'common',name:'Общие'}],typeId='')=>({id,part,category,combinationCategory:category,races,typeId,name:id});
  const row=(index,part,name)=>({index,part,name,id:name,categoryId:part==='Chest'?'Chest':'Arms',category:part==='Chest'?'Грудь':'Руки',colors:[{name:'#ff0000'}],locked:false});
  const state={available:true,mode:'markings',appearance:{},canUndoAppearance:true,canRedoAppearance:true,tabs:[{id:0,name:'Основное',mode:'appearance'},{id:4,name:'Внешность',mode:'markings'}],markings:[row(0,'LArm','Левая рука'),row(1,'RArm','Правая рука'),row(2,'Chest','Торс')],markingOptions:[option('Другой левый протез','LArm','Левая рука'),option('Другой правый протез','RArm','Правая рука'),option('Другой торс','Chest','Грудь')],previewSlots:[{id:'jumpsuit',name:'Комбинезон',images:[]}],tattooEditor:{tattoos:[]}};
  for(const width of [1280,800,480,420]){
   await page.setViewportSize({width,height:860});await publish('CharacterEditor',state);
   await page.waitForSelector('.CharacterEditor__anatomySpace');
   assert.equal(await page.locator('[data-marking-option]').count(),0,'Editor contains selected features only');
   assert.equal(await page.locator('.CharacterEditor__categoryNav').count(),0,'Categories belong to the floating catalogue');
   assert.equal(await page.getByLabel('Поиск особенностей',{exact:true}).count(),0,'Search belongs to the floating catalogue');
   assert.equal(await page.getByRole('checkbox',{name:'Комбинировать выборы этой категории'}).count(),0,'Combination belongs in the catalogue');
   await page.getByRole('button',{name:'Каталог особенностей',exact:true}).click();
   assert.ok(await page.evaluate(()=>window.__sent.some(e=>e.type==='act/catalog-open'&&e.payload.kind==='markings')));
   const left=await page.getByRole('region',{name:'Выбор элементов',exact:true}).boundingBox();const center=await page.locator('.CharacterEditor__anatomySpace').boundingBox();
   assert.ok(left.x+left.width<=center.x+1,`Side order at ${width}`);
   assert.equal(await page.getByText('Левые части тела',{exact:true}).count(),0);
   assert.equal(await page.getByText('Правые части тела',{exact:true}).count(),0);
   assert.equal(await page.getByText('Торс, голова и особые детали',{exact:true}).count(),0);
   assert.equal(await page.getByRole('region',{name:'Выбор элементов',exact:true}).locator('[data-marking-id]').count(),3);
   assert.equal(await page.locator('[data-marking-category]').count(),2,'Selected elements are grouped by their real category');
   const arms=page.locator('[data-marking-category=Arms]');
   const armsToggle=arms.getByRole('button',{name:'Руки 2',exact:true});
   assert.equal(await arms.locator('[data-marking-id]').count(),2);
   await armsToggle.click();
   assert.equal(await armsToggle.getAttribute('aria-expanded'),'false');
   assert.equal(await arms.locator('[data-marking-id]').count(),0);
   assert.equal(await page.locator('[data-marking-category=Chest] [data-marking-id]').count(),1,'Collapsing a category leaves the others visible');
   await publish('CharacterEditor',{...state,canRedoAppearance:false});
   assert.equal(await armsToggle.getAttribute('aria-expanded'),'false','Backend updates preserve category collapse');
   await armsToggle.click();
   await publish('CharacterEditor',state);
   assert.equal(await arms.locator('[data-marking-id]').count(),2,'Expanding restores the selected elements and their controls');
   const catalogButton=await page.getByRole('button',{name:'Каталог особенностей',exact:true}).boundingBox();
   assert.ok(catalogButton.height>=34&&catalogButton.width>=145,'Large catalogue button');
   await page.getByRole('button',{name:'Отменить',exact:true}).click();
   await page.getByRole('button',{name:'Вернуть',exact:true}).click();
   assert.ok(await page.evaluate(()=>window.__sent.some(e=>e.type==='act/appearance-undo')&&window.__sent.some(e=>e.type==='act/appearance-redo')));
   const background=page.getByRole('button',{name:'Сменить фон превью',exact:true});
   const dark=await page.locator('.CharacterEditor__anatomySpace').evaluate(e=>getComputedStyle(e).backgroundColor);
   await background.click();
   const light=await page.locator('.CharacterEditor__anatomySpace').evaluate(e=>getComputedStyle(e).backgroundColor);
   assert.notEqual(light,dark,'Preview switches background');
   await background.click();
   assert.equal(await page.locator('.CharacterEditor__anatomySpace').evaluate(e=>getComputedStyle(e).backgroundColor),dark);
   assert.equal(await page.locator('.CharacterEditor__slots').count(),0);
   assert.equal(await page.locator('.CharacterEditor__rotation').count(),0);
   assert.equal(await page.locator('.CharacterEditor').evaluate(e=>e.scrollWidth>e.clientWidth),false,`overflow ${width}`);
   const name=await page.getByRole('group',{name:'Выбрано: Левая рука',exact:true}).locator('strong').boundingBox();const swatch=await page.getByRole('button',{name:'Цвет 1: Левая рука',exact:true}).boundingBox();
   assert.ok(Math.abs(name.y+name.height/2-swatch.y-swatch.height/2)<3,`Swatch same row ${width}`);
   await page.getByRole('button',{name:'Выше: Левая рука',exact:true}).click();
   assert.ok(await page.evaluate(()=>window.__sent.some(e=>e.type==='act/marking-move'&&e.payload.index===0&&e.payload.value===-1)));
   await page.getByRole('button',{name:'Ниже: Левая рука',exact:true}).click();
   assert.ok(await page.evaluate(()=>window.__sent.some(e=>e.type==='act/marking-move'&&e.payload.index===0&&e.payload.value===1)));
   const lower=await page.locator('.CharacterEditor__anatomyLower').boundingBox();assert.ok(lower.y>=center.y+center.height);
   await page.getByRole('button',{name:'Цвет 1: Левая рука',exact:true}).click();
   assert.equal(await page.locator('.ColorSquare__popover').count(),0);
   assert.ok(await page.evaluate(()=>window.__sent.some(e=>e.type==='act/color-picker-open')));
   await page.waitForFunction(()=>window.__sent.some(e=>e.type==='act/preview-bounds'));
   if(process.env.APPEARANCE_SCREENSHOT&&width===1280)await page.screenshot({path:process.env.APPEARANCE_SCREENSHOT});
  }
  for (const width of [1280,1000,800]) {
   await page.setViewportSize({width,height:860});
   await publish('CharacterEditor',{...state,mode:'appearance',canPreviewAroused:true,previewSlots:Array.from({length:16},(_,i)=>({id:'slot'+i,name:'Слот '+i,images:[]}))});
   const arousal=page.getByRole('button',{name:'Возбуждённое состояние',exact:true});
   await arousal.waitFor();
   const bounds=await arousal.boundingBox(), row=await page.locator('.CharacterEditor__rotation').boundingBox();
   const left=await page.locator('.CharacterEditor__slots > button').nth(0).boundingBox(), right=await page.locator('.CharacterEditor__slots > button').nth(1).boundingBox();
   assert.ok(bounds.y>=row.y+row.height+7,'Arousal toggle sits below all existing controls');
   assert.ok(bounds.x>=left.x+left.width+7&&bounds.x+bounds.width<=right.x-7,'Arousal toggle stays between equipment columns');
   assert.ok(bounds.height<=44&&bounds.width<=row.width,'Compact arousal toggle');
   const obstacles=await page.locator('.CharacterEditor__slots > button, .CharacterEditor__sizeRail, .CharacterEditor__rotation .Button').evaluateAll(elements=>elements.map(e=>{const r=e.getBoundingClientRect();return {x:r.x,y:r.y,width:r.width,height:r.height};}));
   for(const other of obstacles) assert.ok(bounds.x+bounds.width<=other.x||other.x+other.width<=bounds.x||bounds.y+bounds.height<=other.y||other.y+other.height<=bounds.y,'Arousal toggle must not overlap slots, sliders, rotation, Show or background controls');
   if(process.env.PREVIEW_CONTROLS_SCREENSHOT&&width===1280)await page.locator('.CharacterEditor__preview').screenshot({path:process.env.PREVIEW_CONTROLS_SCREENSHOT});
  }
  await page.setViewportSize({width:1280,height:860});
  await publish('CharacterEditor',{...state,mode:'appearance'});
  const basicBackground=page.getByRole('button',{name:'Сменить фон превью',exact:true});
  const basicDark=await page.locator('.CharacterEditor__floor').evaluate(e=>getComputedStyle(e).backgroundColor);
  await basicBackground.click();
  const basicLight=await page.locator('.CharacterEditor__floor').evaluate(e=>getComputedStyle(e).backgroundColor);
  assert.notEqual(basicLight,basicDark);
  await publish('CharacterEditor',state);
  assert.equal(await page.locator('.CharacterEditor__anatomySpace').evaluate(e=>getComputedStyle(e).backgroundColor),basicLight,'Background shared across previews');
  await publish('CharacterEditor',{...state,canUndoAppearance:false,canRedoAppearance:false});
  assert.equal(await page.getByRole('button',{name:'Отменить',exact:true}).getAttribute('aria-disabled'),'true');
  assert.equal(await page.getByRole('button',{name:'Вернуть',exact:true}).getAttribute('aria-disabled'),'true');
  for(const width of [1280,800,480,420]){
   await page.setViewportSize({width,height:860});
   const catalogue={...state,kind:'markings',markingOptions:[...state.markingOptions,option('Кошачий хвост','Tail','Хвост',[{id:'Tajaran',name:'Таяран'},{id:'Ariral',name:'Арирал'}],'Tail'),option('Шрам','Head','Общие')]};
   await publish('CharacterCatalog',catalogue);
   const nav=page.getByRole('navigation',{name:'Категории особенностей'});
   assert.equal(await nav.evaluate(e=>getComputedStyle(e).flexDirection),'row');
   const combine=page.getByRole('checkbox',{name:'Комбинировать выборы этой категории',exact:true});
   assert.equal(await combine.isDisabled(),true,'All categories has no global toggle');
   await nav.getByRole('button',{name:'Хвост',exact:true}).click();
   await combine.click();
   assert.ok(await page.evaluate(()=>window.__sent.some(e=>e.type==='act/marking-combine'&&e.payload.value==='Хвост')));
   await publish('CharacterCatalog',{...catalogue,combinedMarkingCategories:[{id:'Хвост'}]});
   await page.waitForFunction(()=>document.querySelector('.CharacterEditor__combineMarkings input').checked);
   assert.equal(await combine.isChecked(),true);
   const filterBox=await page.getByRole('button',{name:'Фильтр',exact:true}).boundingBox(), checkBox=await combine.boundingBox();
   assert.ok(checkBox.x>=filterBox.x+filterBox.width,'Category toggle is to the right of Filter');
   assert.equal(await page.locator('[data-marking-option]').count(),1);
   await page.getByRole('button',{name:'Кошачий хвост',exact:true}).click();
   assert.ok(await page.evaluate(()=>window.__sent.some(e=>e.type==='act/marking-toggle'&&e.payload.value==='Кошачий хвост')));
   await nav.getByRole('button',{name:'Общие',exact:true}).click();
   assert.equal(await combine.isChecked(),false,'Each category keeps its own state');
   assert.equal(await page.getByRole('button',{name:'Шрам',exact:true}).count(),1);
   const search=page.getByRole('searchbox',{name:'Поиск: Каталог особенностей'});
   await search.fill('zz999нет');assert.equal(await page.getByRole('status').innerText(),'Ничего не найдено');
   await search.fill('');await nav.getByRole('button',{name:'Все',exact:true}).click();
   assert.equal(await page.locator('[data-marking-option]').count(),5,'Catalogue also contains left and right limbs');
   const filter=page.getByRole('button',{name:'Фильтр',exact:true});
   assert.equal(await filter.getAttribute('aria-expanded'),'false');
   await filter.click();
   const race=page.getByRole('combobox',{name:'Расовая категория',exact:true});
   await race.click();await page.getByRole('option',{name:'Таяран',exact:true}).click();
   assert.equal(await page.locator('[data-marking-option]').count(),1,'Race filter uses declared ownership, including elements assigned to multiple races');
   assert.equal(await page.locator('.CharacterEditor').evaluate(e=>e.scrollWidth>e.clientWidth),false,`Open race filter overflow at ${width}`);
   const racialControl=await race.boundingBox();assert.ok(racialControl.x>=0&&racialControl.x+racialControl.width<=width,'Race selector fits the catalogue');
   if(process.env.CATALOG_FILTER_SCREENSHOT&&width===420)await page.screenshot({path:process.env.CATALOG_FILTER_SCREENSHOT});
   await search.fill('кошачий');assert.equal(await page.locator('[data-marking-option]').count(),1);
   await search.fill('торс');assert.equal(await page.getByRole('status').innerText(),'Ничего не найдено');
   await search.fill('');
   assert.equal(await nav.getByRole('button',{name:'Грудь',exact:true}).count(),0,'Categories without results are hidden by the race filter');
   await nav.getByRole('button',{name:'Все',exact:true}).click();
   await race.click();await page.getByRole('option',{name:'Арирал',exact:true}).click();
   assert.equal(await page.locator('[data-marking-option]').count(),1,'Shared racial elements appear in each assigned race');
   await race.click();await page.getByRole('option',{name:'Общие',exact:true}).click();
   assert.equal(await page.locator('[data-marking-option]').count(),4,'Common elements have their own racial category');
   await filter.click();assert.equal(await page.getByRole('combobox',{name:'Расовая категория',exact:true}).count(),0);
   assert.equal(await page.locator('[data-marking-option]').count(),4,'Closing the filter preserves its selection');
   await filter.click();await race.click();await page.keyboard.press('Escape');
   assert.equal(await page.getByRole('listbox').count(),0);
   const closes=await page.evaluate(()=>window.__sent.filter(e=>e.type==='act/catalog-close').length);
   assert.equal(closes,0,'Escape closes only the racial dropdown');
   await race.click();await page.getByRole('option',{name:'Все расы',exact:true}).click();
   assert.equal(await page.locator('[data-marking-option]').count(),5,'All races resets only the racial filter');
   await filter.click();
   assert.equal(await page.locator('.CharacterEditor').evaluate(e=>e.scrollWidth>e.clientWidth),false,`Catalogue overflow at ${width}`);
   if(process.env.CATALOG_SCREENSHOT&&width===1280)await page.screenshot({path:process.env.CATALOG_SCREENSHOT});
  }
  const vulp=[{id:'Vulpkanin',name:'Вульпканин'}],cat=[{id:'Tajaran',name:'Таяран'}];
  const typeOptions=[
   option('Лисий хвост','Tail','Хвост',vulp,'Tail'),option('Кошачий хвост','Tail','Хвост',cat,'Tail'),
   option('Полосы хвоста','TailExtras','Дополнения хвоста',cat,'Tail'),
   option('Лисья морда','Snout','Морда',vulp,'Snout'),option('Кошачья морда','Snout','Морда',cat,'Snout'),
   option('Уши сверху','HeadTop','Голова (верх)',cat,'Ears'),option('Уши сбоку','HeadSide','Голова (верх)',cat,'Ears'),
   option('Специальные уши','Special','Специальное',cat,'Ears'),option('Крылья виверны','Chest','Крылья',cat,'Wings'),
   option('Тонкие антенны','HeadSide','Антенны',cat,'Antennae'),
   option('Рога','HeadTop','Голова (верх)'),option('Жабры','HeadSide','Голова (бок)'),option('Экран синтетика','HeadSide','Голова (бок)'),
  ];
  for(const width of [1280,800,480,420]){
   await page.setViewportSize({width,height:860});
   const catalogue={...state,kind:'markings',markingOptions:typeOptions};
   await publish('CharacterCatalog',catalogue);
   const nav=page.getByRole('navigation',{name:'Категории особенностей'});
   await nav.getByRole('button',{name:'Все',exact:true}).click();
   const filter=page.getByRole('button',{name:'Фильтр',exact:true});await filter.click();
   const type=page.getByRole('combobox',{name:'Тип элемента',exact:true});
   const race=page.getByRole('combobox',{name:'Расовая категория',exact:true});
   const choose=async(control,name)=>{await control.click();await page.getByRole('option',{name,exact:true}).click();};
   for(const [name,count] of [['Хвост',3],['Морды',2],['Уши',3],['Крылья',1],['Антенны',1]]){
    await choose(type,name);assert.equal(await page.locator('[data-marking-option]').count(),count,`${name} uses explicit type metadata at ${width}`);
   }
   assert.deepEqual(await nav.getByRole('button').allTextContents(),['Все','Антенны'],'Only the category available for the chosen type appears');
   await nav.getByRole('button',{name:'Антенны',exact:true}).click();
   assert.equal(await page.getByRole('button',{name:'Тонкие антенны',exact:true}).count(),1);
   await nav.getByRole('button',{name:'Все',exact:true}).click();
   await choose(type,'Морды');await choose(race,'Вульпканин');
   assert.equal(await page.locator('[data-marking-option]').count(),1,'Race and type filters intersect');
   const search=page.getByRole('searchbox',{name:'Поиск: Каталог особенностей'});
   await search.fill('кошачья');assert.equal(await page.getByRole('status').innerText(),'Ничего не найдено');
   assert.deepEqual(await nav.getByRole('button').allTextContents(),['Все'],'Categories without search matches are hidden');
   await search.fill('');
   await choose(type,'Все типы');assert.equal(await page.locator('[data-marking-option]').count(),2,'All types preserves the selected race');
   await choose(type,'Хвост');await choose(race,'Все расы');
   await nav.getByRole('button',{name:'Дополнения хвоста',exact:true}).click();
   assert.equal(await page.locator('[data-marking-option]').count(),1,'Tail extras intersect the tail type');
   await nav.getByRole('button',{name:'Все',exact:true}).click();
   await filter.click();assert.equal(await page.locator('[data-marking-option]').count(),3,'Closing filters preserves the type');await filter.click();
   await choose(type,'Уши');
   assert.equal(await nav.getByRole('button',{name:'Голова (бок)',exact:true}).count(),0);
   await nav.getByRole('button',{name:'Голова (верх)',exact:true}).click();
   assert.equal(await page.locator('[data-marking-option]').count(),2,'Both ears use the top category and keep their render positions');
   await nav.getByRole('button',{name:'Все',exact:true}).click();
   assert.deepEqual(await page.locator('[data-marking-option]').evaluateAll(nodes=>nodes.map(node=>node.dataset.markingOption).sort()),
    ['Уши сверху','Уши сбоку','Специальные уши'].sort(),'Head render layers do not classify horns, gills or screens as ears');
   assert.equal(await page.locator('.CharacterEditor').evaluate(e=>e.scrollWidth>e.clientWidth),false,`Type filter overflow at ${width}`);
   if(process.env.CATALOG_TYPES_SCREENSHOT&&width===420)await page.screenshot({path:process.env.CATALOG_TYPES_SCREENSHOT});
   const restricted={...catalogue,species:'Vulpkanin',speciesName:'Вульпканин',markingSpeciesRestricted:true};
   await publish('CharacterCatalog',restricted);await type.waitFor({state:'detached'});await filter.click();
   assert.equal(await race.isDisabled(),true);assert.equal(await type.isEnabled(),true);
   assert.equal(await page.locator('[data-marking-option]').count(),3,'A type choice survives racial restriction changes');
   await choose(type,'Хвост');assert.equal(await page.locator('[data-marking-option]').count(),3,'Types remain usable with racial restrictions');
   await publish('CharacterCatalog',catalogue);await choose(type,'Уши');
   await publish('CharacterCatalog',{...catalogue,markingOptions:typeOptions.filter(option=>option.typeId!=='Ears')});
   await page.waitForFunction(()=>document.querySelector('[aria-label="Тип элемента"]').textContent.includes('Все типы'));
   assert.equal(await page.locator('[data-marking-option]').count(),10,'Unavailable types clear their stale selection');
   await publish('CharacterCatalog',catalogue);assert.equal(await page.locator('[data-marking-option]').count(),13);
   await filter.click();
  }
  const restrictedCatalogue={...state,kind:'markings',species:'Vulpkanin',speciesName:'Вульпканин',markingSpeciesRestricted:true};
  const restrictionText='Сейчас раса вашего персонажа это "Вульпканин", чтобы выбрать элементы других рас, вам нужно взять черту "Генная модификация"';
  for(const width of [1280,420]){
   await page.setViewportSize({width,height:860});await publish('CharacterCatalog',restrictedCatalogue);
   const warning=page.getByRole('note');assert.equal(await warning.innerText(),restrictionText);
   assert.equal(await warning.evaluate(e=>getComputedStyle(e).color),'rgb(255, 107, 107)');
   const warningBox=await warning.boundingBox(),searchBox=await page.getByRole('searchbox').boundingBox();
   assert.ok(warningBox.y+warningBox.height<=searchBox.y,'Restriction notice stays above the search field');
   const blockedFilter=page.getByRole('button',{name:'Фильтр',exact:true});
   assert.equal(await blockedFilter.isEnabled(),true,'Type filters remain available with racial restrictions');
   await blockedFilter.evaluate(element=>element.click());
   assert.equal(await blockedFilter.getAttribute('aria-expanded'),'true');
   assert.equal(await page.getByRole('combobox',{name:'Расовая категория',exact:true}).isDisabled(),true);
   assert.equal(await page.getByRole('combobox',{name:'Тип элемента',exact:true}).isEnabled(),true);
   await page.getByRole('searchbox').fill('нетэлемента');assert.equal(await warning.innerText(),restrictionText);
   await page.getByRole('searchbox').fill('');
   assert.equal(await page.locator('.CharacterEditor').evaluate(e=>e.scrollWidth>e.clientWidth),false);
   if(process.env.RESTRICTION_SCREENSHOT&&width===420)await page.screenshot({path:process.env.RESTRICTION_SCREENSHOT});
   await blockedFilter.click();
   await publish('CharacterCatalog',{...restrictedCatalogue,markingSpeciesRestricted:false});
   assert.equal(await page.getByRole('note').count(),0,'The notice disappears when the racial restriction is removed');
   await blockedFilter.click();assert.equal(await page.getByRole('combobox',{name:'Расовая категория',exact:true}).count(),1);
   const chooser=page.getByRole('combobox',{name:'Расовая категория',exact:true});await chooser.click();
   await page.getByRole('option',{name:'Общие',exact:true}).click();
   await publish('CharacterCatalog',restrictedCatalogue);
   await page.getByRole('combobox',{name:'Расовая категория',exact:true}).waitFor({state:'detached'});
   assert.equal(await page.getByRole('combobox',{name:'Расовая категория',exact:true}).count(),0,'Re-enabling restrictions closes an already-open filter');
   assert.equal(await page.locator('[data-marking-option]').count(),3,'Re-enabling restrictions clears the old racial selection');
  }
  await publish('CharacterCatalog',{...restrictedCatalogue,kind:'hair',hairOptions:[]});
  assert.equal(await page.getByRole('note').count(),0,'The notice belongs to the body-element catalogue');
  assert.equal(await page.getByRole('button',{name:'Фильтр',exact:true}).count(),0);
  await publish('CharacterEditor',{...state,canPreviewAroused:true,previewAroused:true,markings:[{...row(0,'LHand','Механическая кисть'),categoryId:'Hands',category:'Кисти',paired:true,side:'both'},{...row(1,'Tail','Хвост'),categoryId:'Tail',category:'Хвост',canGlow:true,glow:false}]});
  const side=page.getByRole('combobox',{name:'Сторона: Механическая кисть'});
  await side.click();await page.getByRole('option',{name:'Правая',exact:true}).click();
  assert.ok(await page.evaluate(()=>window.__sent.some(e=>e.type==='act/marking-side'&&e.payload.index===0&&e.payload.value==='right')));
  await page.getByRole('checkbox',{name:'Свечение',exact:true}).click();
  assert.ok(await page.evaluate(()=>window.__sent.some(e=>e.type==='act/marking-glow'&&e.payload.index===1)));
  await publish('CharacterEditor',{...state,canPreviewAroused:true,previewAroused:true,markings:[{...row(1,'Tail','Хвост'),categoryId:'Tail',category:'Хвост',canGlow:true,glow:true,glowLocked:true}]});
  const lockedGlow=page.getByRole('checkbox',{name:'🔒 Свечение',exact:true});
  await lockedGlow.waitFor();assert.equal(await lockedGlow.isDisabled(),true);assert.equal(await lockedGlow.isChecked(),false);
  assert.equal(await page.getByText('Доступно с подпиской «Импульс» и выше.',{exact:true}).count(),1);
  const glowActions=await page.evaluate(()=>window.__sent.filter(e=>e.type==='act/marking-glow').length);
  await lockedGlow.evaluate(input=>input.click());
  assert.equal(await page.evaluate(()=>window.__sent.filter(e=>e.type==='act/marking-glow').length),glowActions,'Locked glow cannot dispatch an action');
  const arousal=page.getByRole('button',{name:'Возбуждённое состояние',exact:true});
  assert.equal(await arousal.getAttribute('aria-pressed'),'true');await arousal.click();
  assert.ok(await page.evaluate(()=>window.__sent.some(e=>e.type==='act/preview-arousal')));
  const preview=await page.locator('.CharacterEditor__anatomySpace').boundingBox(), arousalBox=await arousal.boundingBox();
  assert.ok(arousalBox.y>=preview.y+preview.height,'Arousal control sits below the preview');
  await page.evaluate(()=>{const e=document.createElement('div');e.className='Tooltip';const p=document.querySelector('.CharacterEditor__anatomySpace').getBoundingClientRect();Object.assign(e.style,{position:'fixed',left:p.left+'px',top:p.top+'px',width:'160px',height:'80px',opacity:'1'});e.textContent='Подсказка';document.documentElement.appendChild(e);});
  await page.waitForFunction(()=>window.__sent.some(e=>e.type==='act/preview-occluded'&&e.payload.value==='true'));
  assert.equal(await page.locator('html > .Tooltip').evaluate(e=>getComputedStyle(e).fontSize),'16px');
  await page.locator('html > .Tooltip').evaluate(e=>e.remove());
  await page.waitForFunction(()=>window.__sent.some(e=>e.type==='act/preview-occluded'&&e.payload.value==='false'));
  await publish('CharacterEditor',state);
  await page.getByRole('button',{name:'Цвет 1: Левая рука',exact:true}).click();
  const open=await page.evaluate(()=>window.__sent.filter(e=>e.type==='act/color-picker-open').at(-1).payload);
  await publish('CharacterEditor',{...state,colorPickerOpen:true,colorPickerResult:{id:open.id,value:'#123456',sequence:1}});
  await page.waitForFunction(()=>window.__sent.some(e=>e.type==='act/marking-color'&&e.payload.value==='#123456'&&e.payload.index===0));
  await page.setViewportSize({width:1180,height:860});
  await page.evaluate(() => window.update(JSON.stringify({ type: 'update', payload: {
    replaceData: true, replaceStaticData: true, resetPartialData: true,
    static_data: {
      traitGroups: [{ id: 'speech', name: 'Речь' }, { id: 'quirks', name: 'Особенности' }],
      traits: [{ id: 'quirk', name: 'Особая привычка', description: 'Описание', group: 'quirks', points: 1, slots: 1 }],
    },
    data: { available: true, mode: 'traits', appearance: {}, traitState: { quirk: { available: true, canSelect: true } } },
  } })));
  await page.getByRole('tab', { name: 'Особенности' }).click();
  await page.getByRole('button', { name: 'Применить', exact: true }).click();
  await page.waitForFunction(() => window.__sent.some(e => e.type === 'act/trait' && e.payload.id === 'quirk'));

  const boots = { id: 'shoe', name: 'Чёрные ботинки', cost: 0, allowed: true, canSelect: true, selected: false };
  const wraps = { id: 'wrap', name: 'Тканевые бинты', cost: 1, allowed: true, canSelect: true, selected: true, canPaint: true };
  const gloves = { id: 'gloves', name: 'Рабочие перчатки', cost: 0, group: 'JobGloves', allowed: true, canSelect: true, selected: false };
  const helmet = { id: 'helmet', name: 'Каска', cost: 0, group: 'JobHead', allowed: true, canSelect: true, selected: true };
  const equipment = { items: [boots, wraps], jobItems: [gloves, helmet], jobs: [] };
  const publishEquipment = data => publish('CharacterEditor', { ...state, mode: 'equipment', equipment: data,
    previewSlots: [{ id: 'gloves', name: 'Перчатки', images: [], selected: !!data.slot }] });
  await publishEquipment(equipment);
  const equipmentSearch=page.getByRole('searchbox',{name:'Поиск снаряжения',exact:true});
  await equipmentSearch.waitFor();
  const equipped = page.getByRole('region', { name: 'Экипировано' });
  const equippedNames = equipped.locator('article strong');
  const equipmentNames = page.locator('[aria-label="Каталог снаряжения"] article strong');
  assert.equal(await page.getByRole('navigation', { name: 'Категории снаряжения' }).count(), 0);
  assert.deepEqual(await equippedNames.allTextContents(), [helmet.name, wraps.name]);
  assert.deepEqual(await equipmentNames.allTextContents(), [gloves.name, boots.name]);
  const compact = await equipped.locator('article').first().boundingBox();
  const regular = await page.locator('[aria-label="Каталог снаряжения"] article').first().boundingBox();
  assert.ok(compact.width <= regular.width * 0.6 && compact.height <= regular.height * 0.6);
  const equippedToggle = equipped.getByRole('button', { name: 'Экипировано', exact: true });
  await equippedToggle.click();
  assert.equal(await equippedToggle.getAttribute('aria-expanded'), 'false');
  assert.equal(await equipped.locator('article').first().isVisible(), false);
  await equippedToggle.press('Enter');
  assert.equal(await equipped.locator('article').first().isVisible(), true);
  await equipped.getByRole('button', { name: 'Покрасить', exact: true }).click();
  await page.getByRole('dialog', { name: 'Покраска предмета' }).waitFor();
  await page.getByRole('button', { name: 'Отмена', exact: true }).click();
  await page.getByText(gloves.name, { exact: true }).click();
  await page.waitForFunction(() => window.__sent.some(e => e.type === 'act/equipment/job-select' && e.payload.id === 'gloves'));
  const slot = page.getByRole('button', { name: 'Перчатки', exact: true });
  await slot.click();
  await page.waitForFunction(() => window.__sent.some(e => e.type === 'act/preview-slot' && e.payload.value === 'gloves'));
  await publishEquipment({ ...equipment, slot: 'Перчатки', items: [], jobItems: [{ ...gloves, selected: true }] });
  await page.getByText('Снаряжение · Перчатки', { exact: true }).waitFor();
  assert.deepEqual(await equippedNames.allTextContents(), [gloves.name]);
  assert.deepEqual(await equipmentNames.allTextContents(), []);
  await slot.click();
  await page.waitForFunction(() => window.__sent.some(e => e.type === 'act/equipment/all-slots'));
  await publishEquipment(equipment);
  await equipmentSearch.fill('ботинки');
  assert.deepEqual(await equipmentNames.allTextContents(), [boots.name]);
  assert.deepEqual(await equippedNames.allTextContents(), [helmet.name, wraps.name]);
  await equipmentSearch.fill('qwerty999');assert.equal(await page.getByRole('status').innerText(),'Ничего не найдено');
  await equipmentSearch.fill('');
  assert.deepEqual(await equipmentNames.allTextContents(), [gloves.name, boots.name]);
  const left = { id: 'left', name: 'Левая рука', selected: false };
  const right = { id: 'right', name: 'Правая рука', selected: false };
  const handItem = { id: 'guitar', handKey: 'personal/guitar/hand/0/Guitar',
    name: 'Гитара', cost: 5, allowed: true, canSelect: true, selected: false };
  await publish('CharacterEditor', { ...state, mode: 'equipment', equipment, previewHands: [left, right] });
  await page.getByRole('button', { name: right.name, exact: true }).click();
  await page.waitForFunction(() => window.__sent.some(e => e.type === 'act/preview-hand' && e.payload.value === 'right'));
  await publish('CharacterEditor', { ...state, mode: 'equipment', previewHands: [left, { ...right, selected: true }],
    equipment: { ...equipment, hand: right.name, handItems: [handItem] } });
  assert.deepEqual(await equipmentNames.allTextContents(), [handItem.name]);
  await page.getByText(handItem.name, { exact: true }).click();
  await page.waitForFunction(() => window.__sent.some(e => e.type === 'act/equipment/hand-select' && e.payload.key.includes('/hand/')));

  const tile = 'data:image/png;base64,' + fs.readFileSync(path.join(root, '../../Textures/Interface/Default/Storage/tile_empty.png')).toString('base64');
  const box = (left, bottom, right, top) => ({ left, bottom, right, top });
  const container = { key: 'survival-box', name: 'Коробка выживания' };
  const backpack = { name: 'Рюкзак', tile, grid: [box(0, 0, 3, 2)], containers: [container],
    items: [{ ...container, x: 3, y: 2, container: true, shape: [box(0, 0, 0, 0)] }],
    catalog: [{ key: 'book', id: 'book', name: 'Книга', canSelect: true, shape: [box(0, 0, 1, 0)] }] };
  await publish('CharacterEditor', { ...state, mode: 'equipment', equipment, backpack });
  await page.getByRole('button', { name: 'Редактирование содержимого рюкзака' }).click();
  const cell = page.getByRole('button', { name: 'Ячейка 2, 3', exact: true });
  const item = page.getByRole('button', { name: 'Книга', exact: true });
  assert.ok((await cell.evaluate(node => getComputedStyle(node).backgroundImage)).includes(tile));
  const from = await item.boundingBox();
  const to = await cell.boundingBox();
  await page.mouse.move(from.x + from.width / 2, from.y + from.height / 2);
  await page.mouse.down();
  await page.mouse.move(to.x + to.width / 2, to.y + to.height / 2, { steps: 8 });
  await page.mouse.click(to.x + to.width / 2, to.y + to.height / 2, { button: 'right' });
  await page.mouse.up();
  await page.waitForFunction(() => window.__sent.some(e => e.type === 'act/equipment/backpack-add'
    && e.payload.key === 'book' && e.payload.x === 1 && e.payload.y === 0 && e.payload.rotation === 90));
  await page.getByRole('group', { name: 'Сетка рюкзака' }).getByRole('button', { name: container.name }).dblclick();
  await page.waitForFunction(() => window.__sent.some(e => e.type === 'act/equipment/backpack-container'
    && e.payload.key === 'survival-box'));
  await publish('CharacterEditor', { ...state, mode: 'equipment', equipment, backpack: {
    ...backpack, name: container.name, container: container.key, grid: [box(0, 0, 2, 0)],
    items: [{ key: 'pen', name: 'Ручка', x: 0, y: 0, shape: [box(0, 0, 0, 0)] }],
    catalog: [{ key: 'book', name: 'Книга', canSelect: true, transfer: true, shape: [box(0, 0, 1, 0)] }],
  } });
  const nestedGrid = page.getByRole('group', { name: 'Сетка контейнера' });
  await page.getByRole('button', { name: 'Книга', exact: true }).click();
  await nestedGrid.getByRole('button', { name: 'Ячейка 2, 1', exact: true }).click();
  await page.waitForFunction(() => window.__sent.some(e => e.type === 'act/equipment/backpack-move'
    && e.payload.key === 'book' && e.payload.x === 1 && e.payload.y === 0));
  await nestedGrid.getByRole('button', { name: 'Ручка', exact: true }).click();
  await page.getByRole('button', { name: 'Убрать предмет', exact: true }).click();
  await page.waitForFunction(() => window.__sent.some(e => e.type === 'act/equipment/backpack-remove' && e.payload.key === 'pen'));
  await page.getByRole('button', { name: 'Рюкзак', exact: true }).click();
  await page.waitForFunction(() => window.__sent.some(e => e.type === 'act/equipment/backpack-container' && e.payload.key === ''));
  await page.getByRole('button', { name: 'Закрыть редактор рюкзака' }).click();
  await publish('BackpackReview', { backpack: { ...backpack, containers: [], items: [
    { key: 'wide', name: 'Широкий предмет', x: 0, y: 0, shape: [box(0, 0, 1, 0)] },
    { key: 'small', name: 'Соседний предмет', x: 2, y: 0, shape: [box(0, 0, 0, 0)] },
  ] } });
  const wide = page.getByRole('button', { name: 'Широкий предмет', exact: true });
  const small = page.getByRole('button', { name: 'Соседний предмет', exact: true });
  assert.equal(await wide.first().evaluate(node => getComputedStyle(node, '::after').borderRightWidth), '0px');
  assert.equal(await wide.last().evaluate(node => getComputedStyle(node, '::after').borderRightWidth), '1px');
  assert.equal(await small.evaluate(node => getComputedStyle(node, '::after').borderLeftWidth), '1px');
  assert.ok(await page.getByRole('dialog', { name: 'Каталог предметов' }).isVisible());
  const voice = {
    title: 'Голос', bubbleLabel: 'Стиль пузыря речи', bubblePreview: 'Предпросмотр пузыря речи',
    bubbleStyle: 0, bubbleStyles: [{ id: 0, name: 'По умолчанию' }, { id: 2, name: 'Зелёный' }],
    useSpeciesLabel: 'Отключить барки и использовать голос расы', useSpecies: false,
    search: 'Поиск голоса', timbreLabel: 'Тембр', voice: '',
    preview: 'Прослушать голос', random: 'Случайный голос', reset: 'Сбросить голос',
    parameters: [{ id: 'speed', label: 'Шаг речи', display: '4', min: 1, max: 8, value: 4 }],
  };
  await page.setViewportSize({ width: 780, height: 650 });
  await publish('CharacterEditor', { ...state, mode: 'voice', speechBarks: voice });
  const voiceRow = page.locator('.CharacterEditor__voiceParameter');
  const slider = page.getByRole('slider', { name: 'Шаг речи', exact: true });
  const rangeBounds = await slider.boundingBox();
  const voiceLabel = await voiceRow.getByText('Шаг речи', { exact: true }).boundingBox();
  assert.ok(rangeBounds.width <= 280 && rangeBounds.x > voiceLabel.x + voiceLabel.width - 1);
  assert.ok(Math.abs((rangeBounds.y + rangeBounds.height / 2) - (voiceLabel.y + voiceLabel.height / 2)) < 2);
  await page.getByLabel('Стиль пузыря речи', { exact: true }).click();
  await page.getByText('Зелёный', { exact: true }).click();
  await page.waitForFunction(() => window.__sent.some(e => e.type === 'act/speech-bubble-select' && e.payload.value === '2'));
  await page.getByRole('button', { name: 'Предпросмотр пузыря речи' }).click();
  await page.waitForFunction(() => window.__sent.some(e => e.type === 'act/speech-bubble-preview'));
  await page.getByLabel(voice.useSpeciesLabel, { exact: true }).click();
  await page.waitForFunction(() => window.__sent.some(e => e.type === 'act/speech-bark-use-species' && e.payload.value === true));
  await publish('CharacterEditor', { ...state, mode: 'voice', speechBarks: { ...voice, useSpecies: true } });
  assert.ok(await slider.isDisabled());
  await page.setViewportSize({width:380,height:430});await publish('CharacterColorPicker',{id:'test',title:'Цвет хвоста',value:'#ff0000',appearance:{}});
  await page.getByRole('slider',{name:'Цветовой круг',exact:true}).waitFor();
  await page.getByRole('button',{name:'HSV',exact:true}).click();
  assert.equal(await page.locator('.PlayerColorPicker__wheel').count(),0);
  assert.equal(await page.getByLabel('H',{exact:true}).count(),1);assert.equal(await page.getByLabel('R',{exact:true}).count(),0);
  await page.getByLabel('H',{exact:true}).focus();await page.keyboard.press('ArrowRight');
  const hsvHex=await page.getByLabel('HEX',{exact:true}).inputValue();
  await page.getByRole('button',{name:'RGB',exact:true}).click();
  assert.equal(await page.getByLabel('H',{exact:true}).count(),0);assert.equal(await page.getByLabel('R',{exact:true}).count(),1);
  assert.equal(await page.getByLabel('HEX',{exact:true}).inputValue(),hsvHex);
  await page.getByLabel('R',{exact:true}).fill('64');
  const rgbHex=await page.getByLabel('HEX',{exact:true}).inputValue();
  await page.getByRole('button',{name:'Цветовой круг',exact:true}).click();
  assert.equal(await page.getByLabel('HEX',{exact:true}).inputValue(),rgbHex);
  assert.equal(await page.getByLabel('R',{exact:true}).count(),0);
  await page.getByRole('button',{name:'Закрыть выбор цвета'}).click();
  assert.ok(await page.evaluate(()=>window.__sent.some(e=>e.type==='act/color-picker-close'&&e.payload.id==='test')));
  assert.deepEqual(errors,[]);
  console.log('PASS: 1280/800/480/420 selected elements left and preview right; scoped undo/redo actions; both preview backgrounds; large catalogue; category-specific catalogue checkbox, paired-side dropdown, glow card control, preview arousal, tooltip occlusion relay and equipment name search; floating catalogue with horizontal categories, Common group, all limbs, collapsible selected categories, racial and anatomy type filters, restricted race selector and red notice, live restriction changes, combined filters, multi-race elements, internal search and Escape; native color dialog and color relay.');
 }finally{await browser?.close();await new Promise(resolve=>server.close(resolve));}
})().catch(error=>{console.error(error);process.exitCode=1;});
