export const SCENE_IDLE_MS = 30_000;
export const SCENE_SURFACE_BYTES = 96 * 1024 * 1024;

// One active map and at most one warm background map; cold maps keep their PNGs.
export class GridEditorScenePool {
  caches = new Map();
  warm = new Map();
  active = null;
  timer = null;

  constructor({ now = () => performance.now(), schedule = (callback, delay) => setTimeout(callback, delay), cancel = timer => clearTimeout(timer),
    budget = SCENE_SURFACE_BYTES } = {}) {
    Object.assign(this, { now, schedule, cancel, budget });
  }

  setActive(token) {
    if (token === this.active) return;
    if (this.active != null) this.warm.set(this.active, this.now() + SCENE_IDLE_MS);
    this.active = token;
    this.warm.delete(token);
    for (const [cache, owner] of this.caches) cache.setActive(owner === token);
    while (this.warm.size > 1) this.evict(this.warm.keys().next().value);
    this.trim(); this.arm();
  }

  add(token, cache) {
    this.caches.set(cache, token);
    cache.onSurfaceChange = () => this.trim();
    cache.setActive(token === this.active);
  }

  remove(cache) {
    cache.onSurfaceChange = null;
    this.caches.delete(cache);
  }

  evict(token) {
    this.warm.delete(token);
    for (const [cache, owner] of this.caches) if (owner === token) cache.releaseSurfaces();
  }

  expire() {
    this.timer = null;
    for (const [token, expiry] of this.warm) if (expiry <= this.now()) this.evict(token);
    this.arm();
  }

  arm() {
    if (this.timer !== null) this.cancel(this.timer);
    this.timer = null;
    if (!this.warm.size) return;
    this.timer = this.schedule(() => this.expire(), Math.max(1, Math.min(...this.warm.values()) - this.now()));
  }

  get surfaceBytes() { return [...this.caches.keys()].reduce((sum, cache) => sum + cache.surfaceBytes, 0); }

  trim() {
    if (this.surfaceBytes <= this.budget) return;
    for (const token of [...this.warm.keys()]) {
      this.evict(token);
      if (this.surfaceBytes <= this.budget) break;
    }
    for (const cache of this.caches.keys())
      while (this.surfaceBytes > this.budget && cache.evictDetail()) { /* Keep compressed chunks. */ }
  }

  dispose() {
    if (this.timer !== null) this.cancel(this.timer);
    this.timer = null;
    for (const cache of this.caches.keys()) cache.dispose();
    this.caches.clear(); this.warm.clear();
  }
}
