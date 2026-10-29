export const SIZE = 768;
export const MAX_LAYERS = 8;
const surface = () => { const c = document.createElement('canvas'); c.width = c.height = SIZE; return c; };
let serial = 0;
export const makeLayer = (name) => ({ id: ++serial, name, visible: true, opacity: 1, ink: surface(), mask: null });
export const copySurface = source => { const c = surface(); c.getContext('2d').drawImage(source, 0, 0); return c; };
export const snapshot = state => ({ layers: state.layers.map(l => ({ ...l })), active: state.active, target: state.target, title: state.title });
export const restore = (state, saved) => { state.layers = saved.layers.map(l => ({ ...l })); state.active = saved.active; state.target = saved.target; state.title = saved.title; };
export function historyCost(entries) {
  const images = new Set();
  entries.forEach(s => s.layers.forEach(l => { images.add(l.ink); if (l.mask) images.add(l.mask); }));
  return images.size * SIZE * SIZE * 4;
}
export function checkpoint(state, target = null) {
  state.undo.push(snapshot(state)); state.redo = [];
  while (state.undo.length > 1 && (state.undo.length > 32 || historyCost(state.undo) > 64 * 1024 * 1024)) state.undo.shift();
  if (target) {
    const l = state.layers.find(l => l.id === state.active);
    l[target] = l[target] ? copySurface(l[target]) : surface();
  }
}
export function undo(state, forward = false) {
  const from = forward ? state.redo : state.undo, to = forward ? state.undo : state.redo;
  if (!from.length) return false;
  to.push(snapshot(state)); restore(state, from.pop()); return true;
}
export function addMask(state) {
  const l = state.layers.find(l => l.id === state.active);
  if (!l.mask) { checkpoint(state); l.mask = surface(); const c = l.mask.getContext('2d'); c.fillStyle = '#fff'; c.fillRect(0, 0, SIZE, SIZE); }
  state.target = 'mask';
}
export function compose(layers, paper = false) {
  const c = surface(), ctx = c.getContext('2d');
  if (paper) { ctx.fillStyle = '#f7f0dd'; ctx.fillRect(0, 0, SIZE, SIZE); }
  layers.forEach(l => {
    if (!l.visible) return;
    let ink = l.ink;
    if (l.mask) {
      ink = copySurface(l.ink); const ic = ink.getContext('2d'), mask = copySurface(l.mask), mc = mask.getContext('2d');
      const pixels = mc.getImageData(0, 0, SIZE, SIZE);
      for (let i = 0; i < pixels.data.length; i += 4) pixels.data[i + 3] = pixels.data[i];
      mc.putImageData(pixels, 0, 0); ic.globalCompositeOperation = 'destination-in'; ic.drawImage(mask, 0, 0);
    }
    ctx.globalAlpha = l.opacity; ctx.drawImage(ink, 0, 0);
  });
  ctx.globalAlpha = 1; return c;
}
export const encode = canvas => canvas.toDataURL('image/png').split(',')[1];
export const serialize = state => JSON.stringify({ version: 1, title: state.title, layers: state.layers.map(l => ({ name: l.name, visible: l.visible, opacity: l.opacity, png: encode(l.ink), mask: l.mask ? encode(l.mask) : '' })) });
export async function loadProject(text) {
  const p = JSON.parse(text);
  if (p.version !== 1 || !Array.isArray(p.layers) || !p.layers.length || p.layers.length > MAX_LAYERS) throw new Error('invalid');
  const layers = await Promise.all(p.layers.map(async l => {
    const layer = makeLayer(l.name); layer.visible = l.visible; layer.opacity = l.opacity;
    const load = async (png, target) => { if (!png) return; const img = new Image(); img.src = 'data:image/png;base64,' + png; await img.decode(); if (img.width !== SIZE || img.height !== SIZE) throw new Error('size'); target.getContext('2d').drawImage(img, 0, 0); };
    await load(l.png, layer.ink); if (l.mask) { layer.mask = surface(); await load(l.mask, layer.mask); } return layer;
  }));
  return { layers, active: layers.at(-1).id, target: 'ink', title: p.title || '', undo: [], redo: [] };
}
export function brushSegment(ctx, a, b, options) {
  const { size, flow, color, shape, erase, mask } = options;
  ctx.save(); ctx.globalCompositeOperation = !mask && erase ? 'destination-out' : 'source-over';
  const paint = mask ? erase ? '#fff' : '#000' : color;
  ctx.fillStyle = ctx.strokeStyle = paint;
  const d = Math.hypot(b.x - a.x, b.y - a.y), steps = Math.max(1, Math.ceil(d / Math.max(1, size * .12)));
  if (shape === 'round') {
    ctx.globalAlpha = flow; ctx.lineWidth = size; ctx.lineJoin = ctx.lineCap = 'round';
    ctx.beginPath(); if (d < .01) { ctx.arc(b.x, b.y, size / 2, 0, Math.PI * 2); ctx.fill(); }
    else { ctx.moveTo(a.x, a.y); ctx.lineTo(b.x, b.y); ctx.stroke(); }
  } else for (let n = 1; n <= steps; n++) {
    const x = a.x + (b.x - a.x) * n / steps, y = a.y + (b.y - a.y) * n / steps, r = size / 2;
    if (shape === 'soft') {
      const g = ctx.createRadialGradient(x, y, 0, x, y, r); g.addColorStop(0, paint); g.addColorStop(1, '#0000');
      ctx.fillStyle = g; ctx.globalAlpha = flow * .25; ctx.fillRect(x - r, y - r, size, size);
    } else if (shape === 'flat') { ctx.globalAlpha = flow; ctx.fillRect(x - r, y - size * .15, size, size * .3); }
    else if (shape === 'square') { ctx.globalAlpha = flow; ctx.fillRect(x - r, y - r, size, size); }
    else {
      for (let j = 0; j < 14; j++) {
        const angle = Math.random() * Math.PI * 2, radius = Math.sqrt(Math.random()) * r;
        ctx.globalAlpha = flow * (shape === 'chalk' ? .3 : .15);
        const dot = shape === 'chalk' ? 2.5 : 1.5;
        ctx.fillRect(x + Math.cos(angle) * radius, y + Math.sin(angle) * radius, dot, dot);
      }
    }
  }
  ctx.restore();
}

// Keep coverage for a complete gesture. Overlapping segments must not compound opacity.
export const beginStroke = ctx => ({ ctx, base: copySurface(ctx.canvas), paint: surface(), scratch: surface() });
export function extendStroke(stroke, a, b, options, fromPressure = 1, toPressure = 1) {
  const { size, flow, shape, erase, mask } = options;
  const color = mask ? erase ? '#ffffff' : '#000000' : options.color;
  const radius = Math.max(1, size * Math.max(fromPressure, toPressure)) / 2;
  const x = Math.max(0, Math.floor(Math.min(a.x, b.x) - radius - 2)), y = Math.max(0, Math.floor(Math.min(a.y, b.y) - radius - 2));
  const right = Math.min(SIZE, Math.ceil(Math.max(a.x, b.x) + radius + 2)), bottom = Math.min(SIZE, Math.ceil(Math.max(a.y, b.y) + radius + 2));
  const width = right - x, height = bottom - y;
  if (width <= 0 || height <= 0) return;
  const scratch = stroke.scratch.getContext('2d', { willReadFrequently: true });
  scratch.clearRect(x, y, width, height);
  if (shape === 'round') {
    const dx = b.x - a.x, dy = b.y - a.y, distance = Math.hypot(dx, dy);
    const ra = Math.max(1, size * fromPressure) / 2, rb = Math.max(1, size * toPressure) / 2;
    const rgb = [1, 3, 5].map(n => parseInt(color.slice(n, n + 2), 16));
    if (distance > .01) {
      const gradient = scratch.createLinearGradient(a.x, a.y, b.x, b.y);
      gradient.addColorStop(0, `rgba(${rgb.join(',')},${fromPressure})`);
      gradient.addColorStop(1, `rgba(${rgb.join(',')},${toPressure})`);
      scratch.fillStyle = gradient;
    } else scratch.fillStyle = `rgba(${rgb.join(',')},${toPressure})`;
    scratch.beginPath();
    if (distance > .01) {
      const nx = -dy / distance, ny = dx / distance;
      scratch.moveTo(a.x - nx * ra, a.y - ny * ra); scratch.lineTo(b.x - nx * rb, b.y - ny * rb);
      scratch.lineTo(b.x + nx * rb, b.y + ny * rb); scratch.lineTo(a.x + nx * ra, a.y + ny * ra); scratch.closePath();
    }
    scratch.moveTo(a.x + ra, a.y); scratch.arc(a.x, a.y, ra, 0, Math.PI * 2);
    scratch.moveTo(b.x + rb, b.y); scratch.arc(b.x, b.y, rb, 0, Math.PI * 2);
    scratch.fill();
  } else brushSegment(scratch, a, b, { ...options, color, mask: false, erase: false, size: Math.max(1, size * toPressure), flow: toPressure });
  const paint = stroke.paint.getContext('2d', { willReadFrequently: true });
  const previous = paint.getImageData(x, y, width, height), next = scratch.getImageData(x, y, width, height);
  for (let i = 0; i < previous.data.length; i += 4) {
    if (next.data[i + 3] <= previous.data[i + 3]) continue;
    previous.data[i] = next.data[i]; previous.data[i + 1] = next.data[i + 1];
    previous.data[i + 2] = next.data[i + 2]; previous.data[i + 3] = next.data[i + 3];
  }
  paint.putImageData(previous, x, y);
  const ctx = stroke.ctx;
  ctx.save(); ctx.globalCompositeOperation = 'source-over'; ctx.globalAlpha = 1;
  ctx.clearRect(0, 0, SIZE, SIZE); ctx.drawImage(stroke.base, 0, 0);
  ctx.globalCompositeOperation = erase && !mask ? 'destination-out' : 'source-over'; ctx.globalAlpha = flow;
  ctx.drawImage(stroke.paint, 0, 0); ctx.restore();
}
