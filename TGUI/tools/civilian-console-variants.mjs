import fs from 'node:fs';
import path from 'node:path';
import http from 'node:http';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { build } from 'esbuild';
import * as sass from 'sass';
const require = createRequire(import.meta.url);
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const root = path.resolve(import.meta.dirname, '..');
const output = path.join(root,'node_modules/.cache/civilian-console-preview');
const destination = path.resolve(process.argv[2]);
fs.mkdirSync(output,{recursive:true});fs.mkdirSync(destination,{recursive:true});
const bundle = path.join(output,'variants.js');
await build({entryPoints:[path.join(root,'tools/civilian-console-variants.jsx')],outfile:bundle,bundle:true,minify:true,
  jsx:'automatic',define:{'process.env.NODE_ENV':'"production"'},plugins:[{name:'preview',setup(build){
    build.onResolve({filter:/^\.\.\/backend$/},()=>({path:'mock-backend',namespace:'mock'}));
    build.onLoad({filter:/.*/,namespace:'mock'},()=>({contents:'export const useBackend=()=>({});'}));
    build.onLoad({filter:/\.scss$/},()=>({contents:'',loader:'css'}));
  }}]});
const css = sass.compile(path.join(root,'packages/tgui/components/CivilianConsoleFrame.scss')).css
  + sass.compile(path.join(root,'packages/tgui/interfaces/DiskConsole.scss')).css;
const fragmentPath = path.join(destination,'civilian-console-styles.html');
const wrapper = path.join(output,'preview.html');
const source = fs.readFileSync(path.join(root,'tools/civilian-console-variants.html'),'utf8');
// The preview uses the same asset; JPEG/WebP compression is display-only.
// The production asset remains lossless and pixel-identical to the approved PNG.
let script = fs.readFileSync(bundle,'utf8');
const previewAsset = fs.readFileSync(path.join(output,'shell-preview.webp')).toString('base64');
script = script.replaceAll('civilian-console-shell.webp',`data:image/webp;base64,${previewAsset}`);
const fragment = source.replace('/*__CIVILIAN_CSS__*/',css)
  .replace('/*__CIVILIAN_SCRIPT__*/',script.replaceAll('</script','<\\/script'));
assert.ok(Buffer.byteLength(fragment)<1_000_000,'Inline comparison must fit the conversation limit');
fs.writeFileSync(fragmentPath,fragment);
fs.writeFileSync(wrapper,`<!doctype html><html><meta charset="utf-8"><style>body{margin:0;background:#20262b}[hidden]{display:none!important}</style>${fragment}</html>`);
const server = http.createServer((req,res)=>{res.setHeader('Content-Type','text/html;charset=utf-8');res.end(fs.readFileSync(wrapper));});
await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
let browser;
try {
  browser=await chromium.launch({channel:'msedge',headless:true});
  const page=await browser.newPage();const errors=[];page.on('pageerror',e=>errors.push(String(e)));
  await page.goto(`http://127.0.0.1:${server.address().port}`);
  await page.locator('#civilian-ledger .DiskConsole').waitFor();
  for(const [name,index] of [['ledger',0],['compact',1],['instrument',2]]) {
    await page.evaluate(index=>document.querySelectorAll('#civilian-console-comparison > section').forEach((e,i)=>e.hidden=i!==index),index);
    for(const width of [820,560,320]) {
      await page.setViewportSize({width,height:960});
      const overflow=await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth);
      assert.equal(overflow,false,`${name} at ${width}px must not overflow`);
    }
    await page.setViewportSize({width:820,height:650});
    await page.getByRole('button',{name:'Печать диска очков (1000)',exact:true}).click();
    await page.locator(`#civilian-${name}`).getByText('Демонстрация: выбран диск 1k',{exact:true}).waitFor();
    await page.screenshot({path:path.join(output,`${name}.png`),fullPage:true});
  }
  assert.deepEqual(errors,[]);
  console.log(`PASS: three interactive layouts at 820/560/320px; ${Buffer.byteLength(fragment)} bytes; ${fragmentPath}`);
} finally {await browser?.close();await new Promise(resolve=>server.close(resolve));}
