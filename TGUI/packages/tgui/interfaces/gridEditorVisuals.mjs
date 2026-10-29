const fields = ['visuals', 'patchVisuals', 'peerVisuals', 'libraryVisuals', 'routeVisuals', 'catalog'];
const equal = (a, b) => a === b || !!a && !!b && typeof a === 'object' && typeof b === 'object' &&
  Object.keys(a).length === Object.keys(b).length && Object.keys(a).every(key => equal(a[key], b[key]));

// Keep definitions used by the shared clipboard even after their source tab closes.
export class GridEditorVisuals {
  sources = [];
  entries = new Map();

  update(tabs) {
    const sources = [...new Set(Object.values(tabs).flatMap(tab => fields.map(key => tab[key])).filter(Boolean))];
    if (sources.length === this.sources.length && sources.every((source, i) => source === this.sources[i])) return this.entries;
    this.sources = sources;
    let next = this.entries;
    for (const source of sources) for (const entry of source) {
      const key = `${entry.kind}:${entry.id}`, previous = next.get(key), merged = { ...previous, ...entry };
      if (equal(previous, merged)) continue;
      if (next === this.entries) next = new Map(next);
      next.set(key, merged);
    }
    this.entries = next;
    return next;
  }
}
