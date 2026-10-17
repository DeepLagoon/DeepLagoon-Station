// Browser layout/action fixture. Native radar rendering and flight physics are
// covered by the game integration checks, not simulated in this fixture.
import fs from 'node:fs';
import path from 'node:path';
import http from 'node:http';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { build } from 'esbuild';
import * as sass from 'sass';
const require = createRequire(import.meta.url);
const { chromium } = require(process.env.PLAYWRIGHT_PATH || 'playwright');
const root = path.resolve(import.meta.dirname, '..');
const output = path.join(root, 'node_modules/.cache/shuttle-console-preview');
fs.mkdirSync(output, { recursive: true });
const fixture = path.join(output, 'fixture.jsx');
fs.writeFileSync(fixture, `
import React, { createContext, useContext, useState } from '${path.join(root, 'node_modules/react/index.js').replaceAll('\\', '/')}';
import { createRoot } from '${path.join(root, 'node_modules/react-dom/client.js').replaceAll('\\', '/')}';
import { ShuttleConsole } from '${path.join(root, 'packages/tgui/interfaces/ShuttleConsole.jsx').replaceAll('\\', '/')}';
import { ShipRadarConsole } from '${path.join(root, 'packages/tgui/interfaces/ShipRadarConsole.jsx').replaceAll('\\', '/')}';
export const Context = createContext(null);
window.actions = [];
function App() {
 const [data, setData] = useState({ mode: 0,
  nav: { name:'Пелагея', designation:'DL-014', position:'X: 124.8 / Y: −40.2', azimuth:'124.0°', velocity:'X: 18.5 / Y: 0.0', angular:'0.0°/s', search:'', range:3000, speed:'60', iff:true, docks:true, dampener:1,
    ports:Array.from({length:8},(_,i)=>({id:i+1,name:'Порт '+(i+1)})) },
  weapons:{status:'Сервер орудий подключён',hasWeapons:true,entries:Array.from({length:24},(_,i)=>({id:'gun'+i,name:(i%2?'Лазер':'Корабельная пушка')+' №'+(i+1)+' · '+(40-i)+' снарядов',selected:i<2}))},
  map:{status:'FTL готов',progress:.4,destinations:[{id:'target',name:'Станция DeepLagoon'}]},
  dock:{ports:[{id:'dock1',name:'Шлюз №1',connected:true,selected:true}]} });
 window.telemetry = () => setData(d=>({...d,nav:{...d.nav,velocity:'X: 19.0 / Y: 0.0'}}));
 window.detectWeapons = found => setData(d=>({...d,weapons:{...d.weapons,hasWeapons:found}}));
 window.previewWeaponCount = count => setData(d=>({...d,weapons:{...d.weapons,hasWeapons:count>0,entries:d.weapons.entries.slice(0,count)}}));
 window.previewConsole = kind => setData(d=>({...d,kind,online:true}));
 return <Context.Provider value={{data,act:(action,payload)=>{window.actions.push({action,payload});if(action==='mode')setData(d=>({...d,mode:payload.value}));}}}>{data.kind ? <ShipRadarConsole/> : <ShuttleConsole/>}</Context.Provider>;
}
createRoot(document.getElementById('root')).render(<App/>);
`);
await build({ entryPoints: [fixture], outfile: path.join(output, 'preview.js'), bundle: true, jsx: 'automatic',
  nodePaths: [path.join(root, 'node_modules')], plugins: [{ name: 'fixture', setup(build) {
    build.onResolve({ filter: /^\.\.\/backend$/ }, () => ({ path: 'fixture-backend', namespace: 'fixture' }));
    build.onLoad({ filter: /.*/, namespace: 'fixture' }, () => ({ contents: `import {useContext} from 'react';import {Context} from ${JSON.stringify(fixture)};export const useBackend=()=>useContext(Context);`, resolveDir: root }));
    build.onLoad({ filter: /\.scss$/ }, () => ({ contents: '', loader: 'css' }));
  } }] });
const css = sass.compile(path.join(root, 'packages/tgui/interfaces/ShuttleConsole.scss')).css;
const glassSource = fs.readFileSync(path.join(root, '../Resources/Textures/_DeepLagoon/Shaders/shuttle_radar_glass.swsl'), 'utf8');
const surfaceSource = fs.readFileSync(path.join(root, '../Resources/Textures/_DeepLagoon/Shaders/shuttle_radar_surface.swsl'), 'utf8');
const clydeLibrary = fs.readFileSync(path.join(root, '../RobustToolbox/Robust.Client/Graphics/Clyde/Shaders/z-library.glsl'), 'utf8');
const colorConversions = clydeLibrary.slice(clydeLibrary.indexOf('highp vec4 zFromSrgb'), clydeLibrary.indexOf('// -- uniforms --'));
const html = `<html><meta charset="utf-8"><style>html,body,#root{margin:0;height:100%}${css}</style><div id="root"></div><script src="/preview.js"></script></html>`;
const server = http.createServer((req, res) => {
  if (['/ship-console-shell.png', '/ship-console-instruments.png', '/ship-console-panel.svg'].includes(req.url)) {
    res.setHeader('Content-Type', req.url.endsWith('.svg') ? 'image/svg+xml' : 'image/png');
    res.end(fs.readFileSync(path.join(root, '../Resources/Web/DeepLagoon', req.url.slice(1))));
    return;
  }
  res.setHeader('Content-Type', req.url === '/preview.js' ? 'text/javascript' : 'text/html');
  res.end(req.url === '/preview.js' ? fs.readFileSync(path.join(output, 'preview.js')) : html);
});
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const browser = await chromium.launch({ channel: 'msedge', headless: true });
try {
  const page = await browser.newPage();
  await page.addInitScript(source => { window.shuttleSurfaceSource = source; }, surfaceSource);
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  const renderRadar = () => page.evaluate(async ({ source, colorConversions }) => {
      const viewport = document.querySelector('.ShuttleConsole__viewport');
      viewport.querySelectorAll('canvas').forEach(canvas => canvas.remove());
      const base = document.createElement('canvas');
      const overlay = document.createElement('canvas');
      const size = Math.round(viewport.getBoundingClientRect().width);
      for (const canvas of [base, overlay]) {
        canvas.width = canvas.height = size;
        Object.assign(canvas.style, { position: 'absolute', inset: '0', width: '100%', height: '100%', pointerEvents: 'none' });
        viewport.appendChild(canvas);
      }
      // Suppress the CSS placeholder so screenshots show the actual shader once.
      const style = document.createElement('style');
      style.textContent = '.ShuttleConsole__viewport::after{display:none}';
      document.head.appendChild(style);
      const ctx = base.getContext('2d');
      const gl = overlay.getContext('webgl', { preserveDrawingBuffer: true, premultipliedAlpha: false });
      if (!gl) throw new Error('WebGL unavailable for radar validation');
      const compile = (type, source) => {
        const shader = gl.createShader(type); gl.shaderSource(shader, source); gl.compileShader(shader);
        if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) throw new Error(gl.getShaderInfoLog(shader));
        return shader;
      };
      const vertexSource = 'attribute vec2 position; varying highp vec2 UV; void main(){UV=vec2(position.x*.5+.5,.5-position.y*.5);gl_Position=vec4(position,0.,1.);}';
      const buffer = gl.createBuffer(); gl.bindBuffer(gl.ARRAY_BUFFER, buffer);
      gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1,-1,1,-1,-1,1,-1,1,1,-1,1,1]), gl.STATIC_DRAW);
      const surfaceProgram = gl.createProgram();
      gl.attachShader(surfaceProgram, compile(gl.VERTEX_SHADER, vertexSource));
      const surfaceFragment = 'precision highp float; varying highp vec2 UV; uniform highp float TIME; vec4 surfaceResult;\n'
        + colorConversions + window.shuttleSurfaceSource.replace('light_mode unshaded;', '')
          .replace('void fragment()', 'void surface_fragment()').replaceAll('COLOR', 'surfaceResult')
        + '\nvoid main(){surface_fragment();gl_FragColor=zToSrgb(surfaceResult);}';
      gl.attachShader(surfaceProgram, compile(gl.FRAGMENT_SHADER, surfaceFragment)); gl.linkProgram(surfaceProgram);
      if (!gl.getProgramParameter(surfaceProgram, gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(surfaceProgram));
      gl.useProgram(surfaceProgram);
      const surfacePosition = gl.getAttribLocation(surfaceProgram, 'position');
      gl.enableVertexAttribArray(surfacePosition); gl.vertexAttribPointer(surfacePosition, 2, gl.FLOAT, false, 0, 0);
      gl.uniform2f(gl.getUniformLocation(surfaceProgram, 'viewport_size'), size, size);
      gl.uniform1f(gl.getUniformLocation(surfaceProgram, 'TIME'), 1);
      gl.viewport(0, 0, size, size); gl.drawArrays(gl.TRIANGLES, 0, 6);
      const noiseSamples = new Uint8Array(8 * 8 * 4);
      gl.readPixels(30, 30, 8, 8, gl.RGBA, gl.UNSIGNED_BYTE, noiseSamples);
      gl.uniform1f(gl.getUniformLocation(surfaceProgram, 'TIME'), 1.1);
      gl.drawArrays(gl.TRIANGLES, 0, 6);
      const nextNoiseSamples = new Uint8Array(noiseSamples.length);
      gl.readPixels(30, 30, 8, 8, gl.RGBA, gl.UNSIGNED_BYTE, nextNoiseSamples);
      const noiseLevels = [];
      let noiseKeepsColour = true;
      for (let i=0;i<noiseSamples.length;i+=4) {
        noiseLevels.push(noiseSamples[i+1]);
        noiseKeepsColour &&= noiseSamples[i+2] > noiseSamples[i] && noiseSamples[i+2] >= noiseSamples[i+1];
      }
      ctx.drawImage(overlay, 0, 0);
      ctx.strokeStyle = '#57818d'; ctx.lineWidth = 1;
      for (const fraction of [.25, .5, .75, .97]) {
        const radius = (size / 2 - 4) * fraction;
        ctx.save();
        ctx.globalAlpha = .025; ctx.strokeStyle = '#9b5964';
        ctx.beginPath(); ctx.arc(size / 2, size / 2, radius - .55, 0, Math.PI * 2); ctx.stroke();
        ctx.globalAlpha = .03; ctx.strokeStyle = '#4a9b80';
        ctx.beginPath(); ctx.arc(size / 2, size / 2, radius + .55, 0, Math.PI * 2); ctx.stroke();
        ctx.restore();
        ctx.beginPath(); ctx.arc(size / 2, size / 2, radius, 0, Math.PI * 2); ctx.stroke();
      }
      ctx.beginPath(); ctx.moveTo(size / 2, 4); ctx.lineTo(size / 2, size - 4);
      ctx.moveTo(4, size / 2); ctx.lineTo(size - 4, size / 2); ctx.stroke();
      const radius = size / 2 - 18;
      ctx.font = '10px monospace'; ctx.textAlign = 'center'; ctx.textBaseline = 'middle';
      for (let degrees = 0; degrees < 360; degrees += 10) {
        const angle = (degrees - 90) * Math.PI / 180;
        const major = degrees % 30 === 0;
        ctx.strokeStyle = '#99bac3'; ctx.fillStyle = '#99bac3';
        ctx.beginPath();
        ctx.moveTo(size / 2 + Math.cos(angle) * radius, size / 2 + Math.sin(angle) * radius);
        ctx.lineTo(size / 2 + Math.cos(angle) * (radius - (major ? 9 : 5)), size / 2 + Math.sin(angle) * (radius - (major ? 9 : 5)));
        ctx.stroke();
        if (major) ctx.fillText(String(degrees), size / 2 + Math.cos(angle) * (radius - 23), size / 2 + Math.sin(angle) * (radius - 23));
      }
      ctx.strokeStyle = '#99c9d1';
      ctx.strokeRect(size * .46, size * .44, size * .08, size * .12);
      for (const [x, y] of [[.22, .32], [.65, .23], [.76, .63], [.34, .74]]) {
        ctx.strokeRect(size * x, size * y, 6, 6);
      }
      const program = gl.createProgram();
      gl.attachShader(program, compile(gl.VERTEX_SHADER, vertexSource));
      // Match Clyde's linear framebuffer and final sRGB conversion. Without this,
      // the old incorrectly coloured square looked correct in browser previews.
      const fragment = 'precision highp float; varying highp vec2 UV; uniform sampler2D fixtureRadar; uniform bool inspectGlass; vec4 glassResult;\n'
        + colorConversions
        + source.replace('light_mode unshaded;', '').replace('void fragment()', 'void clyde_fragment()').replaceAll('COLOR', 'glassResult')
        + '\nvoid main(){clyde_fragment(); if(inspectGlass){gl_FragColor=zToSrgb(glassResult);}else{vec4 radar=zFromSrgb(texture2D(fixtureRadar,UV));gl_FragColor=zToSrgb(vec4(mix(radar.rgb,glassResult.rgb,glassResult.a),1.0));}}';
      gl.attachShader(program, compile(gl.FRAGMENT_SHADER, fragment)); gl.linkProgram(program);
      if (!gl.getProgramParameter(program, gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(program));
      gl.useProgram(program);
      const position = gl.getAttribLocation(program, 'position');
      gl.enableVertexAttribArray(position); gl.vertexAttribPointer(position, 2, gl.FLOAT, false, 0, 0);
      gl.uniform2f(gl.getUniformLocation(program, 'viewport_size'), size, size);
      gl.uniform1f(gl.getUniformLocation(program, 'rim_width'), 4);
      const origin = viewport.getBoundingClientRect();
      const textured = !document.querySelector('.HandheldMassScanner');
      gl.uniform1f(gl.getUniformLocation(program, 'panel_scale'), 1);
      gl.uniform2f(gl.getUniformLocation(program, 'panel_offset'), origin.left, origin.top);
      gl.uniform1f(gl.getUniformLocation(program, 'panel_texture'), textured ? 1 : 0);
      const texture = gl.createTexture(); gl.bindTexture(gl.TEXTURE_2D, texture);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
      gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, base);
      gl.uniform1i(gl.getUniformLocation(program, 'fixtureRadar'), 0);
      gl.uniform1i(gl.getUniformLocation(program, 'inspectGlass'), 1);
      gl.viewport(0, 0, size, size); gl.drawArrays(gl.TRIANGLES, 0, 6);
      const sample = (x, y) => {
        const pixel = new Uint8Array(4);
        gl.readPixels(Math.floor(x * size), Math.floor((1 - y) * size), 1, 1, gl.RGBA, gl.UNSIGNED_BYTE, pixel);
        return Array.from(pixel);
      };
      const tileImage = new Image(); tileImage.src = '/ship-console-panel.svg'; await tileImage.decode();
      const tile = document.createElement('canvas'); tile.width = tile.height = 64;
      const tileContext = tile.getContext('2d'); tileContext.drawImage(tileImage, 0, 0);
      const panelSamples = [];
      for (let y=3;y<12;y++) for (let x=3;x<12;x++) {
        const actual = new Uint8Array(4); gl.readPixels(x, size-y-1, 1, 1, gl.RGBA, gl.UNSIGNED_BYTE, actual);
        const expected = textured ? [...tileContext.getImageData(Math.floor(origin.left+x+.5)%64, Math.floor(origin.top+y+.5)%64,1,1).data] : [11,18,22,255];
        panelSamples.push({actual:[...actual],expected});
      }
      const result = { panelSamples, corner: sample(.02, .02), center: sample(.5, .5), highlight: sample(.285, .16), plain: sample(.715, .16), error: gl.getError(),
        noiseRange: Math.max(...noiseLevels) - Math.min(...noiseLevels), noiseKeepsColour,
        noiseAnimated: noiseSamples.some((value,i) => value !== nextNoiseSamples[i]) };
      gl.uniform1i(gl.getUniformLocation(program, 'inspectGlass'), 0);
      gl.drawArrays(gl.TRIANGLES, 0, 6);
      const rendered = document.createElement('canvas');
      rendered.width = rendered.height = size;
      rendered.style.cssText = base.style.cssText;
      rendered.getContext('2d').drawImage(base, 0, 0);
      rendered.getContext('2d').drawImage(overlay, 0, 0);
      base.remove(); overlay.remove(); viewport.appendChild(rendered);
      gl.getExtension('WEBGL_lose_context')?.loseContext();
      return result;
    }, { source: glassSource, colorConversions });
  for (const [width, height] of [[1060, 700], [760, 480], [900, 580], [1285, 916]]) {
    await page.setViewportSize({ width, height });
    await page.goto(`http://127.0.0.1:${server.address().port}`);
    await page.getByText('Управление орудиями', { exact: true }).waitFor();
    await page.waitForFunction(() => window.actions.some(entry => entry.action === 'viewport'));
    if (width === 1285) {
      await page.evaluate(() => window.previewWeaponCount(1));
      await page.waitForFunction(() => document.querySelectorAll('.ShuttleConsole__weapons button').length === 1);
    }
    const dimensions = await page.evaluate(() => {
      const scope = document.querySelector('.ShuttleConsole__viewport').getBoundingClientRect();
      const aside = document.querySelector('.ShuttleConsole aside');
      const bounds = window.actions.filter(entry => entry.action === 'viewport').at(-1).payload;
      return { overflow: document.documentElement.scrollWidth > innerWidth, radarWidth: scope.width,
        radarHeight: scope.height, scroll: aside.scrollHeight > aside.clientHeight, bounds };
    });
    assert.equal(dimensions.overflow, false);
    assert.ok(dimensions.radarWidth > 200 && dimensions.radarHeight > 250);
    assert.ok(Math.abs(dimensions.radarWidth - dimensions.radarHeight) < 1, 'The radar aperture must stay circular when resizing');
    if (width !== 1285) assert.ok(dimensions.scroll, 'Long weapon lists must scroll inside the panel');
    assert.ok(dimensions.bounds.x + dimensions.bounds.w <= 1 && dimensions.bounds.y + dimensions.bounds.h <= 1);
    const styling = await page.evaluate(() => {
      const app = document.querySelector('.ShuttleConsole');
      const title = app.querySelector('.ShuttleConsole__scope-title .ShuttleConsole__sticker');
      const indicator = app.querySelector('.ShuttleConsole__indicator');
      return { skin: app.dataset.skin, headingAlignment: getComputedStyle(title).textAlign,
        telemetryAlignment: getComputedStyle(app.querySelector('.ShuttleConsole__terminal-title')).textAlign,
        headingFont: getComputedStyle(title).fontSize, indicatorFont: getComputedStyle(indicator).fontSize,
        lamp: app.querySelector('.ShuttleConsole__lamp').getBoundingClientRect().height,
        telemetryGlow: getComputedStyle(app.querySelector('.ShuttleConsole__terminal dd')).textShadow };
    });
    assert.equal(styling.skin, 'dark-steel');
    assert.equal(styling.telemetryAlignment, 'left');
    assert.ok(styling.lamp >= 11);
    assert.equal(styling.telemetryGlow, 'none');
    assert.ok(await page.locator('.ShipConsoleFrame').evaluate(el => getComputedStyle(el).borderImageSource.includes('ship-console-shell.png')));
    const tabs = await page.locator('header nav button').evaluateAll(buttons => buttons.map(button=>button.getBoundingClientRect().toJSON()));
    const bezel = await page.locator('.ShipConsoleFrame').evaluate(el=>parseFloat(getComputedStyle(el).borderTopWidth));
    assert.equal(tabs.length,3);
    for(const tab of tabs) assert.ok(tab.y>=0&&tab.bottom<=bezel,'Working tabs must occupy the upper hardware slots');
    await page.keyboard.down('ArrowLeft');
    await page.locator('.ConsoleKeyboard [data-key="ArrowLeft"]').waitFor();
    const pressedBox=await page.locator('.ConsoleKeyboard [data-key="ArrowLeft"] > rect').first().boundingBox();
    await page.screenshot({path:path.join(output,`keyboard-${width}.png`)});
    assert.ok(pressedBox.y>height-65&&pressedBox.y+pressedBox.height<=height,`Animated cap must stay on the physical keyboard: ${JSON.stringify(pressedBox)}`);
    await page.keyboard.up('ArrowLeft');
    await page.waitForFunction(()=>!document.querySelector('.ConsoleKeyboard [data-pressed]'));
    assert.ok(await page.locator('.ShuttleConsole__lamp').evaluate(el => el.classList.contains('is-on')));
    await page.evaluate(() => window.detectWeapons(false));
    await page.waitForFunction(() => !document.querySelector('.ShuttleConsole__lamp').classList.contains('is-on'));
    assert.equal(await page.getByRole('status').getAttribute('aria-label'), 'Корабельные орудия не обнаружены');
    await page.evaluate(() => window.detectWeapons(true));
    await page.waitForFunction(() => document.querySelector('.ShuttleConsole__lamp').classList.contains('is-on'));
    const ports = await page.locator('.ShuttleConsole__port-list button').evaluateAll(buttons => buttons.map(button => {
      const rect = button.getBoundingClientRect(); return { x: rect.x, y: rect.y, width: rect.width, right: rect.right,
        writingMode: getComputedStyle(button).writingMode, textOrientation: getComputedStyle(button).textOrientation };
    }));
    assert.equal(ports.length, 8);
    for (let i = 1; i < ports.length; i++) {
      assert.ok(ports[i].x >= ports[i - 1].right, 'Ports must form a horizontal bank of keys');
      assert.equal(ports[i].y, ports[0].y);
      assert.equal(ports[i].width, ports[0].width);
      assert.equal(ports[i].writingMode, 'vertical-rl');
      assert.equal(ports[i].textOrientation, 'upright');
    }
    await page.getByRole('button', { name: 'Порт 8', exact: true }).click();
    assert.ok(await page.evaluate(() => window.actions.some(a => a.action === 'nav' && a.payload.id === 'port' && a.payload.value === '8')));
    await page.getByRole('button', { name: 'Баллистика', exact: true }).click();
    assert.ok(await page.evaluate(() => window.actions.some(a => a.action === 'weapon' && a.payload.id === 'ballistic')));
    const search = page.getByLabel('Название / компания');
    await page.locator('summary').click();
    await search.fill('Пелагея'); await page.evaluate(() => window.telemetry());
    assert.equal(await search.inputValue(), 'Пелагея');
    await search.press('Enter');
    assert.ok(await page.evaluate(() => window.actions.some(a => a.action === 'nav' && a.payload.id === 'search' && a.payload.value === 'Пелагея')));
    await page.locator('summary').click();
    await page.getByRole('button', { name: 'Карта / FTL', exact: true }).click();
    await page.getByRole('button', { name: 'Станция DeepLagoon', exact: true }).click();
    assert.ok(await page.evaluate(() => window.actions.some(a => a.action === 'map' && a.payload.id === 'target')));
    await page.getByRole('button', { name: 'Стыковка', exact: true }).click();
    await page.getByRole('button', { name: 'Блок FTL', exact: true }).click();
    assert.ok(await page.evaluate(() => window.actions.some(a => a.action === 'dock' && a.payload.id === 'lock')));
    await page.getByRole('button', { name: 'Пилот / орудия', exact: true }).click();
    // Compile the actual glass fragment in WebGL and inspect its alpha. The mock
    // radar underneath is only a visual fixture; it does not emulate game state.
    const glass = await renderRadar();
    assert.equal(glass.error, 0);
    assert.equal(glass.corner[3], 255, 'The native radar must be fully covered outside the circular aperture');
    for (const sample of glass.panelSamples) for (let i=0;i<4;i++)
      assert.ok(Math.abs(sample.actual[i]-sample.expected[i])<=1, 'Native grain must match the browser SVG tile after Clyde conversion');
    assert.ok(new Set(glass.panelSamples.map(sample=>sample.actual[0])).size>5,'The metal outside the radar must have visible grain');
    assert.ok(glass.noiseRange >= 6 && glass.noiseRange <= 14, 'White noise must remain faint against the radar background');
    assert.equal(glass.noiseKeepsColour, true);
    assert.equal(glass.noiseAnimated, true);
    assert.ok(glass.center[3] < 13, 'The reduced glass must keep radar information legible');
    assert.ok(glass.highlight[3] < 50, 'The upper reflection must be reduced by at least half');
    assert.ok(glass.center[2] > glass.center[0] && glass.center[2] >= glass.center[1], 'The glass haze must keep a restrained blue cast');
    assert.ok(glass.highlight[2] > glass.highlight[0] && glass.highlight[2] >= glass.highlight[1], 'Reflections must keep the steel palette');
    assert.ok(glass.highlight[3] > glass.plain[3] + 10, 'The glass must retain a small, localized upper reflection');
    const screenshot = path.join(output, width === 1285 ? 'shuttle-ports.png' : `shuttle-${width}.png`);
    await page.screenshot({ path: screenshot });
    console.log(`PASS ${width}x${height}: dark steel, shared shell, GPU mask/noise, ports/actions; ${screenshot}`);
    // Standalone consoles must share the measured aperture without acquiring
    // shuttle-only controls or losing their existing command channels.
    for (const kind of ['gunnery', 'mass', 'handheld']) {
      await page.evaluate(kind => window.previewConsole(kind), kind);
      await page.locator(`[data-kind="${kind}"]`).waitFor();
      await page.waitForTimeout(60);
      const box = await page.locator('.ShuttleConsole__viewport').boundingBox();
      assert.ok(box.width > 200 && Math.abs(box.width - box.height) < 1);
      assert.equal(await page.getByRole('button', {name:'Карта / FTL',exact:true}).count(), 0);
      const standaloneGlass = await renderRadar();
      for (const sample of standaloneGlass.panelSamples) for (let i=0;i<4;i++)
        assert.ok(Math.abs(sample.actual[i]-sample.expected[i])<=1, `${kind}: native aperture backing must match its browser surface`);
      if (kind === 'handheld') {
        assert.equal(await page.locator('aside, header, footer, .ShipConsoleFrame__hardware, .ConsoleKeyboard').count(), 0);
        assert.ok(box.width > Math.min(width, height) - 45, 'Handheld radar must use almost the entire window');
        await page.screenshot({path:path.join(output, `${kind}-${width}.png`)});
        continue;
      }
      await page.getByRole('button', {name:'IFF',exact:true}).click();
      assert.ok(await page.evaluate(() => window.actions.some(a=>a.action==='radar'&&a.payload.id==='iff')));
      if (kind === 'gunnery') {
        await page.getByRole('button', {name:'Баллистика',exact:true}).click();
        await page.locator('.ShuttleConsole__weapons button').first().click();
        assert.ok(await page.evaluate(() => window.actions.some(a=>a.action==='weapon'&&a.payload.id==='gun0')));
      } else assert.equal(await page.locator('.ShuttleConsole__weapons').count(), 0);
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false);
      await page.screenshot({path:path.join(output, `${kind}-${width}.png`)});
      if (kind === 'gunnery' && width === 1060) {
        await page.evaluate(()=>window.previewWeaponCount(0));
        await page.waitForFunction(()=>document.querySelectorAll('.ShuttleConsole__weapons button').length===0);
        await renderRadar();
        const backing=await page.locator('aside').evaluate(el=>({image:getComputedStyle(el).backgroundImage,height:el.clientHeight,content:el.lastElementChild.getBoundingClientRect().bottom-el.getBoundingClientRect().top}));
        assert.ok(backing.image.includes('ship-console-panel.svg')&&backing.content<backing.height-80,'Short sidebars must retain the textured backing beneath their final block');
        await page.screenshot({path:path.join(output,'gunnery-empty-1060.png')});
      }
    }
  }
  for (const [width,height] of [[560,540],[280,270]]) {
    await page.setViewportSize({width,height});
    await page.waitForTimeout(80);
    const box=await page.locator('.ShuttleConsole__viewport').boundingBox();
    assert.ok(box.width > Math.min(width,height)-45 && Math.abs(box.width-box.height)<1);
    await renderRadar();
    await page.screenshot({path:path.join(output, `handheld-${width}.png`)});
  }
  assert.deepEqual(errors, []);
} finally { await browser.close(); await new Promise(resolve => server.close(resolve)); }
