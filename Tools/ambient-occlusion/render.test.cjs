const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const { chromium } = require(process.env.PLAYWRIGHT_PATH || 'playwright');
const root = path.resolve(__dirname, '../..');
const output = path.join(root, '.local/codex/ao-lighting');
const shaders = Object.fromEntries(['subtract', 'add', 'composite'].map(mode => [mode,
  fs.readFileSync(path.join(root, 'Resources/Textures/_DeepLagoon/Shaders',
    mode === 'composite' ? 'ambient_occlusion.swsl' : `ambient_occlusion_${mode}.swsl`), 'utf8')]));

async function main() {
  fs.mkdirSync(output, { recursive: true });
  const browser = await chromium.launch({ channel: 'msedge', headless: true,
    args: ['--use-angle=swiftshader', '--enable-unsafe-swiftshader'] });
  try {
    const page = await browser.newPage();
    const results = await page.evaluate(shaders => {
      const canvas = document.createElement('canvas');
      canvas.width = canvas.height = 32;
      const gl = canvas.getContext('webgl', { antialias: false, preserveDrawingBuffer: true });
      if (!gl) throw Error('WebGL unavailable');
      const size = 32, count = size * size;
      function compile(type, source) {
        const shader = gl.createShader(type);
        gl.shaderSource(shader, source); gl.compileShader(shader);
        if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) throw Error(gl.getShaderInfoLog(shader));
        return shader;
      }
      function program(source, unshaded) {
        const body = source.slice(source.indexOf('void fragment()') + 'void fragment()'.length).trim();
        const result = gl.createProgram();
        gl.attachShader(result, compile(gl.VERTEX_SHADER, `attribute vec2 position; varying highp vec2 UV;
          void main() { UV = position * 0.5 + 0.5; gl_Position = vec4(position, 0.0, 1.0); }`));
        // The same default fragment wrapper and modulation order as Clyde, with linear RGBA8 targets.
        gl.attachShader(result, compile(gl.FRAGMENT_SHADER, `precision highp float;
          varying highp vec2 UV; uniform sampler2D TEXTURE; uniform sampler2D lightMap;
          uniform highp vec4 tint;
          vec4 zTexture(vec2 uv) { return texture2D(TEXTURE, uv); }
          void fragmentBody(inout vec4 COLOR, inout vec4 MODULATE, inout vec4 LIGHT, inout vec3 lightSample) ${body}
          void main() { lowp vec4 COLOR; highp vec4 MODULATE = tint;
            lowp vec4 LIGHT = ${unshaded ? 'vec4(1.0)' : 'texture2D(lightMap, UV)'};
            lowp vec3 lightSample = LIGHT.rgb;
            fragmentBody(COLOR, MODULATE, LIGHT, lightSample);
            LIGHT.rgb = lightSample; gl_FragColor = COLOR * MODULATE * LIGHT; }`));
        gl.linkProgram(result);
        if (!gl.getProgramParameter(result, gl.LINK_STATUS)) throw Error(gl.getProgramInfoLog(result));
        return result;
      }
      const programs = Object.fromEntries(Object.entries(shaders).map(([mode, source]) =>
        [mode, program(source, mode !== 'composite')]));
      const buffer = gl.createBuffer(); gl.bindBuffer(gl.ARRAY_BUFFER, buffer);
      gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1,-1, 1,-1, -1,1, -1,1, 1,-1, 1,1]), gl.STATIC_DRAW);
      function texture(data) {
        const result = gl.createTexture(); gl.bindTexture(gl.TEXTURE_2D, result);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
        gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, size, size, 0, gl.RGBA, gl.UNSIGNED_BYTE, data);
        return result;
      }
      function target() {
        const tex = texture(null), framebuffer = gl.createFramebuffer();
        gl.bindFramebuffer(gl.FRAMEBUFFER, framebuffer);
        gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, tex, 0);
        if (gl.checkFramebufferStatus(gl.FRAMEBUFFER) !== gl.FRAMEBUFFER_COMPLETE) throw Error('Invalid framebuffer');
        return { tex, framebuffer };
      }
      const mask = target(), composite = target();
      function draw(mode, tex, opacity, light) {
        const p = programs[mode]; gl.useProgram(p);
        const position = gl.getAttribLocation(p, 'position');
        gl.enableVertexAttribArray(position); gl.vertexAttribPointer(position, 2, gl.FLOAT, false, 0, 0);
        gl.activeTexture(gl.TEXTURE0); gl.bindTexture(gl.TEXTURE_2D, tex);
        gl.uniform1i(gl.getUniformLocation(p, 'TEXTURE'), 0);
        gl.activeTexture(gl.TEXTURE1); gl.bindTexture(gl.TEXTURE_2D, light || tex);
        gl.uniform1i(gl.getUniformLocation(p, 'lightMap'), 1);
        gl.uniform4f(gl.getUniformLocation(p, 'tint'), 1, 1, 1, opacity);
        // Exact stock Clyde blend factors, including its special subtraction alpha factors.
        if (mode === 'subtract') {
          gl.blendEquation(gl.FUNC_REVERSE_SUBTRACT);
          gl.blendFuncSeparate(gl.SRC_ALPHA, gl.DST_ALPHA, gl.ZERO, gl.DST_ALPHA);
        } else if (mode === 'add') {
          gl.blendEquation(gl.FUNC_ADD); gl.blendFunc(gl.SRC_ALPHA, gl.DST_ALPHA);
        } else {
          gl.blendEquation(gl.FUNC_ADD);
          gl.blendFuncSeparate(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA, gl.ONE, gl.ONE_MINUS_SRC_ALPHA);
        }
        gl.drawArrays(gl.TRIANGLES, 0, 6);
      }
      function read() {
        const pixels = new Uint8Array(count * 4);
        gl.readPixels(0, 0, size, size, gl.RGBA, gl.UNSIGNED_BYTE, pixels);
        return pixels;
      }
      gl.viewport(0, 0, size, size); gl.enable(gl.BLEND); gl.enable(gl.DITHER);
      const layers = [];
      function layer(opacity, alpha) {
        const pixels = new Uint8Array(count * 4);
        for (let i = 0; i < count; i++) pixels[i * 4 + 3] = alpha(i % size, Math.floor(i / size));
        return { tex: texture(pixels), pixels, opacity };
      }
      layers.push(layer(0.48, (x,y) => x >= 6 && x <= 20 && y >= 6 && y <= 22 ? 255 : 0));
      layers.push(layer(0.48 * 0.35, (x,y) => x >= 14 && y <= 24 ? 128 : 0));
      for (let shift = 0; shift < 3; shift++) layers.push(layer(0.198, (x,y) => {
        const falloff = Math.max(0, 1 - ((x - 17 - shift) / 10) ** 2 - ((y - 15) / 6) ** 2);
        return Math.round(Math.round(falloff * falloff * 8) / 8 * 255);
      }));
      const expected = Array.from({ length: count }, (_, i) =>
        Math.round(Math.max(...layers.map(l => l.pixels[i * 4 + 3] / 255 * l.opacity)) * 255));
      function render(items) {
        gl.bindFramebuffer(gl.FRAMEBUFFER, mask.framebuffer); gl.clearColor(0,0,0,1); gl.clear(gl.COLOR_BUFFER_BIT);
        for (const l of items) { draw('subtract', l.tex, l.opacity); draw('add', l.tex, l.opacity); }
        return read();
      }
      const checks = [];
      function check(name, pixels, values, tolerance = 1) {
        let maxError = 0;
        for (let i = 0; i < count; i++) {
          for (let c = 0; c < 3; c++) maxError = Math.max(maxError, Math.abs(pixels[i * 4 + c] - values[i]));
          if (pixels[i * 4 + 3] !== 255) throw Error(`${name}: alpha changed at ${i}`);
        }
        if (maxError > tolerance) throw Error(`${name}: error ${maxError} exceeds ${tolerance}`);
        checks.push({ name, maxError });
      }
      check('structures, sprite layers and overlapping mob spots', render(layers), expected);
      check('reversed draw order', render([...layers].reverse()), expected);
      check('32 repeated copies do not accumulate', render(Array.from({ length: 32 }, () => layers).flat()), expected);
      gl.disable(gl.DITHER);
      check('repeated overlap without framebuffer dithering', render(Array.from({ length: 32 }, () => layers).flat()), expected);
      gl.enable(gl.DITHER);
      check('transparent layers do not change existing shadows', render([...layers, layer(0.66, () => 0)]), expected);
      check('removed casters leave no stale shadow', render([]), Array(count).fill(0));
      render(layers);
      const lighting = new Uint8Array(count * 4);
      const colors = [[0,0,0], [64,64,64], [128,128,128], [255,255,255], [255,0,0], [0,255,0], [0,0,255]];
      const litExpected = [];
      for (let i = 0; i < count; i++) {
        const rgb = colors[Math.floor(i % size / size * colors.length)];
        lighting.set([...rgb, 255], i * 4);
        const l = Math.max(...rgb) / 255, fade = 1 - l*l*(3-2*l);
        litExpected.push(Math.round(255 - expected[i] * fade));
      }
      const light = texture(lighting);
      gl.bindFramebuffer(gl.FRAMEBUFFER, composite.framebuffer); gl.clearColor(1,1,1,1); gl.clear(gl.COLOR_BUFFER_BIT);
      draw('composite', mask.tex, 1, light);
      check('darkness, partial light, white and RGB lights', read(), litExpected, 2);
      if (gl.getError() !== gl.NO_ERROR) throw Error('WebGL error');
      return { version: gl.getParameter(gl.VERSION), checks };
    }, shaders);
    assert.equal(results.checks.length, 7);
    fs.writeFileSync(path.join(output, 'gpu-results.json'), JSON.stringify(results, null, 2) + '\n');
    console.log(JSON.stringify(results, null, 2));
  } finally { await browser.close(); }
}
main().catch(error => { console.error(error); process.exitCode = 1; });
