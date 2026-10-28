const fs=require('node:fs'), path=require('node:path'), assert=require('node:assert/strict');
const root=path.resolve(__dirname,'..');
const yaml=require(path.join(root,'TGUI/node_modules/js-yaml'));
const source=JSON.parse(fs.readFileSync(path.join(root,'Resources/_DeepLagoon/Economy/apartment-catalog.json'),'utf8'));
function files(dir){return fs.readdirSync(dir,{withFileTypes:true}).flatMap(e=>e.isDirectory()?files(path.join(dir,e.name)):[path.join(dir,e.name)]);}
const sources=files(path.join(root,'Resources/Prototypes')).filter(p=>p.endsWith('.yml')&&!p.endsWith('catalog.generated.yml')&&!p.endsWith('classes.generated.yml'));
const texts=sources.map(p=>[p,fs.readFileSync(p,'utf8').replace(/^\uFEFF/,'')]);
const tags=[...new Set(texts.flatMap(([,t])=>t.match(/!type:[A-Za-z0-9_.+]+/g)||[]))];
class Tagged{constructor(tag,value){this.tag=tag;this.value=value;}}
const schema=yaml.DEFAULT_SCHEMA.extend(tags.map(tag=>new yaml.Type(tag,{kind:'mapping',construct:data=>new Tagged(tag,data),predicate:data=>data instanceof Tagged&&data.tag===tag,represent:data=>data.value})));
const entities=new Map(), failures=[];
for(const [file,text] of texts){try{const all=yaml.load(text,{schema,json:true});if(Array.isArray(all))for(const entry of all)if(entry?.type==='entity'&&entry.id)entities.set(entry.id,entry);}catch(e){failures.push(path.relative(root,file));}}
function clone(v){if(v instanceof Tagged)return new Tagged(v.tag,clone(v.value));if(Array.isArray(v))return v.map(clone);if(v&&typeof v==='object')return Object.fromEntries(Object.entries(v).map(([k,x])=>[k,clone(x)]));return v;}
function merge(a,b){const r=clone(a||{});for(const [k,v]of Object.entries(b||{}))r[k]=v&&typeof v==='object'&&!Array.isArray(v)&&!(v instanceof Tagged)?merge(r[k],v):clone(v);return r;}
const cache=new Map();
function components(id,stack=[]){if(cache.has(id))return clone(cache.get(id));assert(!stack.includes(id),'Inheritance cycle '+id);const e=entities.get(id);assert(e,'Missing entity '+id);const c=new Map();for(const parent of [].concat(e.parent||[]))for(const part of components(parent,[...stack,id]))c.set(part.type,part);for(const part of e.components||[])c.set(part.type,merge(c.get(part.type),part));const result=[...c.values()];cache.set(id,result);return clone(result);}
const permitted=new Set(['Sprite','Appearance','GenericVisualizer','Icon','IconSmooth','SpriteFade','PointLight','Strap','Instrument','ActivatableUI','UserInterface','ApcPowerReceiver','ExtensionCableReceiver','Jukebox','BlockGameArcade','SpaceVillainArcade','WallMount','Animateable']);
const free=['DLAptSofa','DLAptChair','DLAptTable','DLAptBed','DLAptPlant'];
const generated=[],loc=[],ids=[...free,'DLAptAtmosphere'],manifest=[];
for(let i=0;i<source.catalog.length;i++){
 const item=source.catalog[i];
 if(i<5){manifest.push({...item,furniture:free[i],available:true});continue;}
 if(!item.base_entity){manifest.push({...item,available:false,reason:'Новая графика и интерактивные функции — отдельный этап; товар не продаётся.'});continue;}
 const id='DLAptDecor'+String(i+1).padStart(3,'0'),entity=id+'Entity',key='dl-apt-decor-'+String(i+1).padStart(3,'0');
 const layer=item.category==='Ковры'?'Floor':['Картины','Баннеры'].includes(item.category)?'Wall':'Furniture';
 let parts=components(item.base_entity).filter(c=>permitted.has(c.type));
 assert(parts.some(p=>p.type==='Sprite'),'Missing sprite '+item.base_entity);
 const sprite=parts.find(c=>c.type==='Sprite');
 if(layer==='Floor'){parts=parts.filter(c=>!['IconSmooth','Icon'].includes(c.type));sprite.state='full';}
 if(layer==='Wall')sprite.drawdepth='WallMountedItems';
 if(item.category==='Освещение'){
  parts=parts.filter(c=>!['GenericVisualizer','ApcPowerReceiver','ExtensionCableReceiver'].includes(c.type));
  for(const l of sprite.layers||[])if(l.map?.includes('light'))l.visible=true;
  const light=parts.find(c=>c.type==='PointLight');if(light)Object.assign(light,{enabled:true,radius:3,energy:1});
 }
 if(item.category==='Музыка и досуг'){
  for(const p of parts)if(p.type==='SpaceVillainArcade')Object.assign(p,{rewardMinAmount:0,rewardMaxAmount:0,possibleRewards:[]});
 }
 const passable=['Растения','Ковры','Картины','Баннеры'].includes(item.category)||parts.some(c=>c.type==='Strap');
 const size=item.base_entity==='PianoInstrument'?'2,2':'1,1';
 generated.push({type:'entity',id:entity,parent:'BaseStructure',name:item.name,categories:['HideSpawnMenu'],components:[
  {type:'Transform',anchored:true},...parts,{type:'NoDeconstruct'},
  {type:'Physics',bodyType:'Static',canCollide:!passable},
  {type:'Fixtures',fixtures:passable?{}:{fix1:{shape:new Tagged('!type:PhysShapeAabb',{bounds:'-0.4,-0.4,0.4,0.4'}),density:100,layer:['LowImpassable','BulletImpassable'],mask:['MachineMask']}}}
 ]});
 const product=({15:'DLAptSofa',16:'DLAptSofa',17:'DLAptSofa',19:'DLAptDecor018',20:'DLAptDecor018',21:'DLAptDecor018'})[i+1];
 generated.push({type:'apartmentFurniture',id,name:key,entity,price:item.price_lc,category:item.category,layer,...(product?{product}:{}),
  size,maxCount:item.placement_limit,blocksMovement:!passable,allowsOverlap:layer!=='Furniture'});
 loc.push(key+' = '+(id==='DLAptDecor018'?'Корпоративный модульный диван':item.name));ids.push(id);manifest.push({...item,furniture:id,entity,layer,size,available:true});
}
assert.equal(ids.length,112);
const dump=v=>yaml.dump(v,{schema,noRefs:true,lineWidth:120,quotingType:'"',forceQuotes:false});
function write(rel,data){const p=path.join(root,rel);fs.mkdirSync(path.dirname(p),{recursive:true});fs.writeFileSync(p,data);}
write('Resources/Prototypes/_DeepLagoon/Apartments/catalog.generated.yml','# Generated by Scripts/generate-apartment-catalog.cjs. Curated visual/interaction components only.\n'+dump(generated));
write('Resources/Locale/ru-RU/_DeepLagoon/apartment-catalog.ftl',loc.join('\n')+'\n');
write('Resources/Locale/en-US/_DeepLagoon/apartment-catalog.ftl',loc.join('\n')+'\n');
write('Resources/_DeepLagoon/Economy/apartment-catalog.json',JSON.stringify({version:1,catalog:manifest},null,2)+'\n');
console.log(JSON.stringify({free:5,paid:107,planned:10,unparsedSourceFiles:failures}));
