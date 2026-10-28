import { tintSprite } from './gridEditorTint.mjs';
import { spriteBounds, spriteLayers } from './gridEditorSprites.mjs';
import { drawOrder } from './gridEditorModel.mjs';

export const CHUNK_CELLS = 16;
const PIXELS = 32;
const DETAIL_LIMIT = 64; // 64 MiB of decoded 512x512 images, independent of grid size.
const yieldFrame = () => new Promise(resolve => setTimeout(resolve, 0));
const surface = size => {
  const canvas = document.createElement('canvas'); canvas.width = canvas.height = size;
  return canvas;
};
const visualKey = item => `${item.kind}:${item.prototype}`;
const fields = ['id', 'kind', 'prototype', 'x', 'y', 'rotation', 'mirror', 'variant', 'flags', 'color', 'z'];
const sameItems = (a, b) => a.length === b.length && a.every((item, i) =>
  item === b[i] || fields.every(field => item[field] === b[i][field]));
const release = chunk => {
  for (const canvas of chunk.levels.values()) canvas.width = canvas.height = 0;
  chunk.levels.clear();
};

// Include the rotated footprint in every intersected chunk. Adjacent chunks clip
// the same globally ordered sprites, including fractional decal origins.
export function itemChunks(item, visual) {
  const bounds=spriteBounds(item,visual);
  const result = [];
  for (let cx = Math.floor((bounds.left) / CHUNK_CELLS); cx <= Math.floor((bounds.right - 1e-8) / CHUNK_CELLS); cx++)
    for (let cy = Math.floor((bounds.bottom) / CHUNK_CELLS); cy <= Math.floor((bounds.top - 1e-8) / CHUNK_CELLS); cy++) result.push([cx, cy]);
  return result;
}

export class GridSceneCache {
  chunks = new Map();
  images = new Map();
  tints = new Map();
  definitions = new Map();
  revision = 0;
  details = new Map();
  wanted = new Set();
  decoding = 0;
  disposed = false;
  stats = { rasterized: 0, composites: 0, blits: 0 };

  constructor(invalidate) { this.invalidate = invalidate; }

  load(url) {
    if (!this.images.has(url)) this.images.set(url, new Promise(resolve => {
      const image = new Image();
      image.onload = () => resolve(image); image.onerror = () => resolve(null); image.src = url;
    }));
    return this.images.get(url);
  }

  async prepare(index, layers, visuals, progress, cancelled) {
    const stopped = () => this.disposed || cancelled();
    const groups = new Map(), definitions = new Map(), usedTints = new Set();
    let processed = 0, deadline = performance.now() + 6;
    progress({ label: 'Подготовка элементов', done: 0, total: index.order.size });
    await yieldFrame();
    for (const bucket of index.values()) for (const item of bucket) {
      if (stopped()) return;
      if (layers[item.kind]) {
        if (item.kind === 'decal' || visuals.get(visualKey(item))?.pipeColor) for (const image of visuals.get(visualKey(item))?.images || []) usedTints.add(`${image.url}:${item.color}`);
        const key = visualKey(item);
        if (!definitions.has(key)) {
          const visual = visuals.get(key);
          const signature = JSON.stringify([visual?.drawDepth, visual?.allowRotationMirror, visual?.noRotation, visual?.images]);
          if (this.definitions.get(key)?.signature !== signature)
            this.definitions.set(key, { signature, revision: ++this.revision });
          definitions.set(key, this.definitions.get(key).revision);
        }
        for (const [x, y] of itemChunks(item,visuals.get(visualKey(item)))) {
          const key = `${x}:${y}`;
          if (!groups.has(key)) groups.set(key, { x, y, items: [] });
          groups.get(key).items.push(item);
        }
      }
      processed++;
      if (performance.now() > deadline) {
        progress({ label: 'Подготовка элементов', done: processed, total: index.order.size });
        await yieldFrame(); deadline = performance.now() + 6;
      }
    }
    // Include the mip pyramid in the 32 MiB overview budget. Empty space costs no textures.
    let overview = 8;
    while (overview > 1 && groups.size * (CHUNK_CELLS * overview) ** 2 * 4 * 4 / 3 > 32 * 1024 * 1024) overview /= 2;
    const urls = new Set();
    for (const key of definitions.keys()) for (const { url } of visuals.get(key)?.images || []) if (url) urls.add(url);
    const sources = new Map(); let loaded = 0;
    progress({ label: 'Загрузка спрайтов', done: 0, total: urls.size });
    const queue = [...urls];
    await Promise.all(Array.from({ length: Math.min(8, queue.length) }, async () => {
      while (queue.length && !stopped()) {
        const url = queue.pop(); sources.set(url, await this.load(url)); loaded++;
        if (!stopped()) progress({ label: 'Загрузка спрайтов', done: loaded, total: urls.size });
      }
    }));
    if (stopped()) return;
    let completed = 0;
    progress({ label: 'Подготовка карты', done: 0, total: groups.size });
    await yieldFrame();
    for (const [key, group] of groups) {
      if (stopped()) return;
      group.items.sort((a, b) => drawOrder(a, visuals.get(visualKey(a))) - drawOrder(b, visuals.get(visualKey(b))) || index.order.get(a.id) - index.order.get(b.id));
      const signature = [...new Set(group.items.map(visualKey))].map(key => definitions.get(key)).join('|');
      const previous = this.chunks.get(key);
      // Changing the overview budget must not rasterize untouched map chunks.
      // Existing pyramids already contain all smaller levels; release excess ones.
      if (previous && previous.overview > overview) {
        for (const [size, level] of previous.levels) if (size > overview) {
          level.width = level.height = 0; previous.levels.delete(size);
        }
        previous.overview = overview;
      }
      if (!previous || previous.signature !== signature || !sameItems(previous.items, group.items)) {
        const chunk = await this.bake(group, visuals, sources, overview, stopped);
        if (!chunk) return;
        if (stopped()) { release(chunk); return; }
        if (previous) release(previous);
        this.details.get(key)?.close(); this.details.delete(key);
        this.chunks.set(key, { ...chunk, signature });
      }
      completed++;
      if (performance.now() > deadline || completed === groups.size) {
        progress({ label: 'Подготовка карты', done: completed, total: groups.size });
        await yieldFrame(); deadline = performance.now() + 6;
      }
    }
    if (stopped()) return;
    for (const [key, chunk] of this.chunks) if (!groups.has(key)) {
      release(chunk); this.chunks.delete(key); this.details.get(key)?.close(); this.details.delete(key);
    }
    for (const key of this.tints.keys()) if (!usedTints.has(key)) this.tints.delete(key);
    progress(null); this.invalidate();
  }

  async bake(group, visuals, sources, overview, stopped) {
    const canvas = surface(CHUNK_CELLS * PIXELS), context = canvas.getContext('2d');
    context.imageSmoothingEnabled = false;
    let deadline = performance.now() + 6;
    for (const item of group.items) {
      if (stopped()) { canvas.width = 0; return null; }
      const visual = visuals.get(visualKey(item));
      if (item.kind === 'tile' && !(visual?.images || []).some(image => sources.get(image.url))) {
        context.fillStyle = '#3b454e';
        context.fillRect((item.x-group.x*CHUNK_CELLS)*PIXELS, ((group.y+1)*CHUNK_CELLS-1-item.y)*PIXELS, PIXELS, PIXELS);
      }
      for (const layer of spriteLayers(item, visual)) {
        let image = sources.get(layer.image.url); if (!image) continue;
        if (item.kind === 'decal' || visual?.pipeColor) {
          const key = `${layer.image.url}:${item.color}`;
          if (!this.tints.has(key)) this.tints.set(key, tintSprite(image, item.color));
          image = this.tints.get(key);
        }
        const x=(layer.x-group.x*CHUNK_CELLS)*PIXELS, y=((group.y+1)*CHUNK_CELLS-layer.y)*PIXELS;
        context.save(); context.transform(layer.a,-layer.b,-layer.c,layer.d,x,y);
        if (item.kind==='tile' && visual?.allowRotationMirror && (item.mirror&4)) context.scale(-1,1);
        context.drawImage(image,-layer.width*PIXELS/2,-layer.height*PIXELS/2,layer.width*PIXELS,layer.height*PIXELS);
        context.restore();
      }
      if (performance.now() > deadline) { await yieldFrame(); deadline = performance.now() + 6; }
    }
    const levels = new Map();
    for (let size = overview; size >= 1; size /= 2) {
      const level = surface(CHUNK_CELLS * size), ctx = level.getContext('2d');
      ctx.imageSmoothingEnabled = false; ctx.drawImage(canvas, 0, 0, level.width, level.height); levels.set(size, level);
    }
    // Full-detail snapshots survive eviction in compressed form. Revisiting a
    // chunk decodes its image, without sorting/rasterizing the sprites again.
    const blob = await new Promise(resolve => canvas.toBlob(resolve, 'image/png'));
    canvas.width = canvas.height = 0;
    if (!blob) { for (const level of levels.values()) level.width = 0; throw Error('Не удалось подготовить изображение карты.'); }
    this.stats.rasterized++;
    return { ...group, overview, levels, blob, decoding: false, failed: false };
  }

  paint(canvas, area, view, cellSize) {
    if (this.disposed) return;
    const context = canvas.getContext('2d');
    context.clearRect(0, 0, canvas.width, canvas.height); context.imageSmoothingEnabled = false;
    this.stats.composites++;
    this.wanted.clear();
    const left = area.x + view.left / cellSize, top = area.y + area.height - view.top / cellSize;
    const right = left + view.width / cellSize, bottom = top - view.height / cellSize;
    const draw = (key, chunk) => {
      if ((chunk.x + 1) * CHUNK_CELLS < left || chunk.x * CHUNK_CELLS > right ||
          (chunk.y + 1) * CHUNK_CELLS < bottom || chunk.y * CHUNK_CELLS > top) return;
      let size = 1;
      while (size < cellSize && size < chunk.overview) size *= 2;
      let image = chunk.levels.get(size);
      if (cellSize > chunk.overview) {
        // Do not churn the LRU when a very large viewport contains >64 chunks.
        if (this.wanted.size < DETAIL_LIMIT) this.wanted.add(key);
        if (this.details.has(key)) {
          image = this.details.get(key); this.details.delete(key); this.details.set(key, image);
        }
      }
      const x = (chunk.x * CHUNK_CELLS - area.x) * cellSize - view.left;
      const y = (area.y + area.height - (chunk.y + 1) * CHUNK_CELLS) * cellSize - view.top;
      // Shared rounded edges prevent hairline seams at fractional zoom levels.
      const x1 = Math.round(x), y1 = Math.round(y), x2 = Math.round(x + CHUNK_CELLS * cellSize), y2 = Math.round(y + CHUNK_CELLS * cellSize);
      if (x2 > x1 && y2 > y1) { context.drawImage(image, x1, y1, x2 - x1, y2 - y1); this.stats.blits++; }
    };
    const x0 = Math.floor(left / CHUNK_CELLS), x1 = Math.floor(right / CHUNK_CELLS);
    const y0 = Math.floor(bottom / CHUNK_CELLS), y1 = Math.floor(top / CHUNK_CELLS);
    if ((x1 - x0 + 1) * (y1 - y0 + 1) > this.chunks.size) {
      for (const [key, chunk] of this.chunks) draw(key, chunk);
    } else for (let x = x0; x <= x1; x++) for (let y = y0; y <= y1; y++) {
      const key = `${x}:${y}`, chunk = this.chunks.get(key); if (chunk) draw(key, chunk);
    }
    this.decodeVisible();
  }

  decodeVisible() {
    if (this.disposed) return;
    for (const key of this.wanted) {
      if (this.decoding >= 4) break;
      const chunk = this.chunks.get(key);
      if (!chunk || chunk.decoding || chunk.failed || this.details.has(key)) continue;
      chunk.decoding = true; this.decoding++;
      createImageBitmap(chunk.blob).then(image => {
        if (this.disposed || this.chunks.get(key) !== chunk || !this.wanted.has(key)) { image.close(); return; }
        this.details.set(key, image);
        while (this.details.size > DETAIL_LIMIT) {
          const oldest = this.details.keys().next().value;
          this.details.get(oldest).close(); this.details.delete(oldest);
        }
        this.invalidate();
      }).catch(() => { chunk.failed = true; }).finally(() => {
        chunk.decoding = false; this.decoding--; this.decodeVisible();
      });
    }
  }

  dispose() {
    this.disposed = true;
    for (const chunk of this.chunks.values()) release(chunk);
    for (const image of this.details.values()) image.close();
    this.chunks.clear(); this.details.clear(); this.wanted.clear(); this.images.clear(); this.tints.clear(); this.definitions.clear();
  }
}
