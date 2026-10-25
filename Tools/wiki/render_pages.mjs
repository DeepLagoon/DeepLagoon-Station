import fs from 'node:fs';
import path from 'node:path';
import {pathToFileURL} from 'node:url';
import {repairProseBlocks} from './render_prose.mjs';
const station=path.resolve(process.argv[2]);
const folder=path.resolve(process.argv[3]);
const {marked}=await import(pathToFileURL(path.join(station,'TGUI/node_modules/marked/lib/marked.esm.js')));
let headingCounts=new Map();
const esc=s=>String(s).replaceAll('&','&amp;').replaceAll('"','&quot;').replaceAll('<','&lt;').replaceAll('>','&gt;');
marked.use({renderer:{heading({tokens,depth,text}){
 const base=text.replace(/<[^>]+>/g,'').toLowerCase().trim().replace(/[^\p{L}\p{N}\s_-]/gu,'').replace(/\s+/g,'-')||'section';
 const count=headingCounts.get(base)||0;headingCounts.set(base,count+1);
 return `<h${depth} id="${esc(base+(count?'-'+count:''))}">${this.parser.parseInline(tokens)}</h${depth}>\n`;
}}});
const pages=JSON.parse(fs.readFileSync(path.join(folder,'pages-source.json'),'utf8'));
for(const page of pages){
 if(page.preserveExact){
  delete page.preserveExact;delete page.prefix;delete page.suffix;delete page.markdown;
  continue;
 }
 headingCounts=new Map();
 let body=page.content??marked.parse(page.markdown);
 if(!page.path.endsWith('/writing'))body=repairProseBlocks(body,text=>marked.parse(text));
 body=body.replace(/<script\b[^>]*>[\s\S]*?<\/script>/gi,'');
 const ids=new Set();
 page.content=(page.prefix+body+page.suffix).replace(/\sid="([^"]+)"/g,(full,id)=>{
  let unique=id,n=2;while(ids.has(unique))unique=id+'-'+n++;
  ids.add(unique);return ` id="${unique}"`;
 });
 delete page.prefix;delete page.suffix;delete page.markdown;
 const preview=path.join(folder,'preview',page.path+'.html');
 fs.mkdirSync(path.dirname(preview),{recursive:true});
 fs.writeFileSync(preview,`<!doctype html><html lang="ru"><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>${esc(page.title)}</title><style>body{margin:0}${page.scriptCss}</style>${page.content}</html>`);
}
fs.writeFileSync(path.join(folder,'pages.json'),JSON.stringify(pages));
console.log(`Rendered ${pages.length} HTML pages; maximum content size ${Math.max(...pages.map(p=>Buffer.byteLength(p.content)))} bytes.`);
