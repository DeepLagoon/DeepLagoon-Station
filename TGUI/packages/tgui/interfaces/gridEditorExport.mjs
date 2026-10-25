import { dump } from 'js-yaml';

// Geometry plan for the server export. Existing entity IDs let the native serializer
// retain component values and references before overlaying these unsaved transforms.
export function exportGridDraft(items, bounds, expansion, environment = {}) {
  const floors = new Map();
  for (const item of items) {
    if (!['tile', 'entity', 'decal'].includes(item.kind) || !item.prototype ||
        !Number.isFinite(item.x) || !Number.isFinite(item.y) || !Number.isFinite(item.rotation || 0))
      throw new Error('В черновике есть элемент с некорректным прототипом или координатами.');
    if (item.kind === 'tile') {
      if (!Number.isInteger(item.x) || !Number.isInteger(item.y)) throw new Error('Координаты пола должны быть целыми.');
      floors.set(`${item.x},${item.y}`, item);
    }
  }
  const tileIds = new Map([['Space', 0]]), chunks = new Map();
  for (const tile of floors.values()) {
    if (tile.prototype === 'Space') continue;
    if (!tileIds.has(tile.prototype)) tileIds.set(tile.prototype, tileIds.size);
    const cx = Math.floor(tile.x / 16), cy = Math.floor(tile.y / 16), key = `${cx},${cy}`;
    if (!chunks.has(key)) chunks.set(key, new Uint8Array(16 * 16 * 7));
    const bytes = chunks.get(key), offset = ((tile.y - cy * 16) * 16 + tile.x - cx * 16) * 7;
    // Robust MapChunkSerializer v7: little-endian int32 ID + flags, variant, mirroring.
    new DataView(bytes.buffer).setInt32(offset, tileIds.get(tile.prototype), true);
    bytes[offset + 4] = tile.flags || 0;
    bytes[offset + 5] = tile.variant || 0;
    bytes[offset + 6] = tile.mirror || 0;
  }
  const components = [
    { type: 'MetaData', name: environment.gridName || 'grid' },
    { type: 'Transform', parent: 'invalid' },
    { type: 'MapGrid', chunks: Object.fromEntries([...chunks].map(([ind, bytes]) =>
      [ind, { ind, tiles: btoa(String.fromCharCode(...bytes)), version: 7 }])) },
    { type: 'Shuttle' }, { type: 'Gravity' }, { type: 'ImplicitRoof' },
    { type: 'DecalGrid', chunkCollection: { version: 2, nodes: items.filter(i => i.kind === 'decal').map((item, id) => ({
      node: { id: item.prototype, color: item.color || '#FFFFFFFF', angle: item.rotation || 0,
        zIndex: item.z || 0, cleanable: !!item.cleanable },
      decals: { [id]: `${item.x},${item.y}` },
    })) } },
  ];
  if (environment.alwaysPowered || environment.forceGravity || environment.safeAtmosphere) components.push({
    type: 'GridEnvironment', alwaysPowered: !!environment.alwaysPowered,
    forceGravity: !!environment.forceGravity, safeAtmosphere: !!environment.safeAtmosphere,
  });
  if (environment.apartmentProtected) components.push({ type: 'ApartmentProtectedGrid' });
  const groups = new Map([['', [{ uid: 1, components }]]]);
  const gridEditorIds = {};
  let uid = 1;
  for (const item of items.filter(i => i.kind === 'entity')) {
    if (!groups.has(item.prototype)) groups.set(item.prototype, []);
    groups.get(item.prototype).push({ uid: ++uid, components: [{ type: 'Transform', parent: 1,
      pos: `${item.x},${item.y}`, rot: item.rotation || 0, anchored: !!item.anchored }] });
    gridEditorIds[uid] = item.id;
  }
  return dump({
    gridEditorIds,
    meta: { format: 7, category: 'Grid', entityCount: uid },
    maps: [], grids: [1], orphans: [1], nullspace: [],
    tilemap: Object.fromEntries([...tileIds].map(([name, id]) => [id, name])),
    entities: [...groups].map(([proto, entities]) => ({ proto, entities })),
  }, { noRefs: true, lineWidth: -1 });
}

// Keep each JSON action below the native CEF bridge limit, including escaped newlines/quotes.
export function draftFileChunks(yaml) {
  const chunks = [];
  for (let start = 0; start < yaml.length; start += 1024) chunks.push(yaml.slice(start, start + 1024));
  return chunks;
}
