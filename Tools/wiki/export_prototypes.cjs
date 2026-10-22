const fs=require('node:fs');
const path=require('node:path');
const station=path.resolve(process.argv[2]);
const output=path.resolve(process.argv[3]);
const yaml=require(path.join(station,'TGUI/node_modules/js-yaml'));
const types=['mapping','sequence','scalar'].map(kind=>new yaml.Type('!type:',{
 kind,multi:true,construct:(value,tag)=>kind==='mapping'?{effectType:tag.slice(6),...(value||{})}:value
}));
const schema=yaml.DEFAULT_SCHEMA.extend(types);
function files(dir){return fs.readdirSync(dir,{withFileTypes:true}).flatMap(x=>x.isDirectory()?files(path.join(dir,x.name)):/\.ya?ml$/.test(x.name)?[path.join(dir,x.name)]:[])}
const raw={},sources={},duplicates=[],manifest=[];
for(const file of files(path.join(station,'Resources/Prototypes')).sort()){
 const text=fs.readFileSync(file,'utf8');
 let entries;
 try{entries=yaml.load(text.replace(/\t/g,'  '),{schema,filename:file})}catch(e){console.error(file+': '+e.message);process.exit(1)}
 if(!Array.isArray(entries))continue;
 const relative=path.relative(station,file).replaceAll('\\','/');
 manifest.push([relative,fs.statSync(file).mtimeMs,Buffer.byteLength(text)]);
 for(const value of entries){
  if(!value?.type||!value?.id)continue;
  const kind=value.type,id=String(value.id),key=kind+':'+id;
  raw[kind]??={};
  if(raw[kind][id])duplicates.push([key,sources[key],relative]);
  raw[kind][id]=value;sources[key]=relative;
 }
}
fs.writeFileSync(output,JSON.stringify({raw,sources,duplicates,manifest}));
console.log('Indexed '+manifest.length+' YAML files; '+Object.keys(raw.entity||{}).length+' entities; '+Object.keys(raw.reaction||{}).length+' reactions; '+duplicates.length+' duplicate declarations.');
