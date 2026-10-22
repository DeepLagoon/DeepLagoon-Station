const fs=require('node:fs');
const path=require('node:path');
const assert=require('node:assert/strict');
const {chromium}=require('C:/Users/iamasay/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright');
(async()=>{
 const folder=path.resolve(process.argv[2]);
 const browser=await chromium.launch({channel:'msedge',headless:true});
 const results=[];
 try{
  const page=await browser.newPage({viewport:{width:1440,height:1100}});
  const errors=[],badAssets=[];
  page.on('pageerror',error=>errors.push(error.message));
  page.on('response',response=>{if(response.url().includes('/guidebook/in_game/assets/')&&response.status()>=400)badAssets.push({url:response.url(),status:response.status()})});
  for(const [name,url,theme] of [['home','home','a'],['book','guidebook/in_game/books/bartenders-manual','b'],['guide','guidebook/in_game/chemistry/bicaridine','a']]){
   await page.setViewportSize({width:1440,height:1100});
   for(let attempt=0;;attempt++){
    try{await page.goto('https://wiki.deep-lagoon-ss14.ru/ru/'+url,{waitUntil:'domcontentloaded',timeout:60000});break}
    catch(e){if(attempt===2)throw e}
   }
   await page.locator('.dlwiki.theme-'+theme).waitFor({timeout:30000});
   await page.locator('.dlwiki .brand img').evaluate(img=>img.complete||new Promise((resolve,reject)=>{img.onload=resolve;img.onerror=reject}));
   const facts=await page.locator('.dlwiki').evaluate(root=>({background:getComputedStyle(root).backgroundColor,color:getComputedStyle(root).color,
     width:root.getBoundingClientRect().width,html:root.innerHTML.length,missingImages:[...root.querySelectorAll('img')].filter(img=>!img.complete||!img.naturalWidth).length,
     headings:[...document.querySelectorAll('h1')].map(x=>({text:x.textContent,classes:x.className,parent:x.parentElement.className})),
     bookColor:root.querySelector('.book')?getComputedStyle(root.querySelector('.book')).backgroundColor:null}));
   assert.notEqual(facts.background,'rgba(0, 0, 0, 0)','page CSS missing');
   assert.equal(facts.missingImages,0,'missing visible images');
   if(name==='book'){
    assert.equal(facts.bookColor,'rgb(255, 250, 240)');
    const colors=await page.locator('.book').evaluate(book=>({heading:getComputedStyle(book.querySelector('h1')).color,card:getComputedStyle(book.querySelector('.dl-reagent')).backgroundColor,summary:getComputedStyle(book.querySelector('.dl-reagent summary')).color}));
    assert.equal(colors.heading,'rgb(54, 52, 43)');
    assert.equal(colors.card,'rgb(245, 239, 223)');
    assert.equal(colors.summary,'rgb(54, 52, 43)');
    await page.locator('.dlwiki .dl-reagent summary').first().click();
    assert.equal(await page.locator('.dlwiki .dl-reagent').first().getAttribute('open'),'');
    await page.evaluate(()=>scrollTo(0,0));
   }
   await page.screenshot({path:path.join(folder,'live-'+name+'.png'),fullPage:false});
   await page.setViewportSize({width:390,height:950});
   await page.reload({waitUntil:'networkidle'});
   await page.locator('.dlwiki.theme-'+theme).waitFor();
   const mobile=await page.evaluate(()=>({scrollWidth:document.documentElement.scrollWidth,viewport:innerWidth}));
   assert.ok(mobile.scrollWidth<=mobile.viewport,'horizontal page overflow: '+name);
   await page.screenshot({path:path.join(folder,'live-'+name+'-mobile.png'),fullPage:false});
   results.push({name,url,theme,...facts,mobile});
  }
  fs.writeFileSync(path.join(folder,'browser-verification.json'),JSON.stringify({results,badAssets,browserErrors:errors},null,2));
  assert.deepEqual(badAssets,[],'asset errors');
  console.log(JSON.stringify({results,badAssets,browserErrors:errors},null,2));
 }finally{await browser.close()}
})().catch(e=>{console.error(e);process.exit(1)});
