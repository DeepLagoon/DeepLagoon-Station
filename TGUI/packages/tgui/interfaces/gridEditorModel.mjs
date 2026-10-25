import { spriteLayers, spriteBounds } from './gridEditorSprites.mjs';
export const editableItem = (item, layers) => !!layers[item.kind] &&
  (layers.decalZ === undefined || item.kind === 'decal' && (item.z || 0) === layers.decalZ);

export function scopeDecalChanges(previous, next, z) {
  const before = new Map(previous.map(item => [item.id, item]));
  const editable = item => item.kind === 'decal' && (item.z || 0) === z;
  const result = next.filter(item => before.has(item.id) ? editable(before.get(item.id)) && item.kind === 'decal' : editable(item));
  return [...previous.filter(item => !editable(item)), ...result];
}

export const decalTemplate = (items, z) => items.filter(item => item.kind === 'decal').map(item => ({ ...item, z }));

export const copyItems = (items) => items.map((item) => ({ ...item }));
export const tileId = (x, y) => `t:${x}:${y}`;
export const cellOf = (item) => ({ x: Math.floor(item.x), y: Math.floor(item.y) });
export const inside = (cell, area) => cell.x >= area.x && cell.y >= area.y &&
  cell.x < area.x + area.width && cell.y < area.y + area.height;

export function cellsBetween(a, b, rectangle = false) {
  const result = [];
  if (rectangle) {
    for (let x = Math.min(a.x, b.x); x <= Math.max(a.x, b.x); x++)
      for (let y = Math.min(a.y, b.y); y <= Math.max(a.y, b.y); y++) result.push({ x, y });
    return result;
  }
  let x = a.x, y = a.y;
  const dx = Math.abs(b.x - x), dy = -Math.abs(b.y - y);
  const sx = x < b.x ? 1 : -1, sy = y < b.y ? 1 : -1;
  let error = dx + dy;
  while (true) {
    result.push({ x, y });
    if (x === b.x && y === b.y) return result;
    const twice = 2 * error;
    if (twice >= dy) { error += dy; x += sx; }
    if (twice <= dx) { error += dx; y += sy; }
  }
}

export function stamp(items, cells, brush, options, nextId) {
  const targets = new Set(cells.map(cell => `${cell.x}:${cell.y}`));
  const kind = brush.kind;
  const result = copyItems(items).filter(item => {
    if (!targets.has(`${Math.floor(item.x)}:${Math.floor(item.y)}`)) return true;
    if (kind === 'tile' && item.kind === 'tile') return false;
    return !(options.replace && brush.category === 'wall' && item.kind === 'entity' && options.isWall(item.prototype));
  });
  const placementKey = item => `${item.kind}:${item.prototype}:${Math.floor(item.x)}:${Math.floor(item.y)}` +
    (item.kind === 'decal' ? `:${item.z || 0}:${item.color}:${item.rotation}:${!!item.cleanable}` : '');
  const occupied = new Set(result.map(placementKey));
  for (const cell of cells) {
    const key = placementKey({ kind, prototype: brush.id, x: cell.x, y: cell.y, ...options });
    if (occupied.has(key)) continue;
    occupied.add(key);
    result.push({ id: kind === 'tile' ? tileId(cell.x, cell.y) : nextId(), kind, prototype: brush.id,
      x: cell.x + (kind === 'entity' ? .5 : 0), y: cell.y + (kind === 'entity' ? .5 : 0),
      rotation: kind === 'tile' ? 0 : options.rotation, color: options.color, z: options.z,
      cleanable: options.cleanable, anchored: !!brush.anchored, variant: 0, flags: 0, mirror: 0 });
  }
  return result;
}

// Only cells receiving a construction are normalized; untouched legacy map stacks stay intact.
export function replaceConflicts(previous, next, isExclusive) {
  const before = new Map(previous.map(item => [item.id, item]));
  const winners = new Map();
  for (const item of next) {
    if (item.kind !== 'entity' || !isExclusive(item.prototype)) continue;
    const old = before.get(item.id);
    if (!old || old.prototype !== item.prototype || old.x !== item.x || old.y !== item.y)
      winners.set(`${Math.floor(item.x)}:${Math.floor(item.y)}`, item.id);
  }
  return next.filter(item => item.kind !== 'entity' || !isExclusive(item.prototype) ||
    !winners.has(`${Math.floor(item.x)}:${Math.floor(item.y)}`) || winners.get(`${Math.floor(item.x)}:${Math.floor(item.y)}`) === item.id);
}

export function validateDraft(items, area, editedIds = null) {
  const ids = new Set();
  const tiles = new Set(items.filter((i) => i.kind === 'tile').map((i) => i.id));
  for (const item of items) {
    if (ids.has(item.id)) return 'Повторяющийся ID элемента.';
    ids.add(item.id);
    if ((!editedIds || editedIds.has(item.id)) && item.kind === 'entity' && item.anchored && (item.x !== Math.floor(item.x) + .5 || item.y !== Math.floor(item.y) + .5))
      return 'Закреплённые объекты должны находиться в центре тайла.';
    if (!Number.isFinite(item.x) || !Number.isFinite(item.y) || !inside(item, area)) return 'Элемент вышел за границы области редактирования.';
    if ((!editedIds || editedIds.has(item.id)) && (item.kind === 'decal' || item.anchored) && !tiles.has(tileId(Math.floor(item.x), Math.floor(item.y))))
      return `Под ${item.kind === 'decal' ? 'декалью' : 'закреплённым объектом'} ${item.prototype} (${Math.floor(item.x)}, ${Math.floor(item.y)}) должен быть пол.`;
  }
  return '';
}

export const changeCount = (original, draft) => {
  const before = new Map(original.map((i) => [i.id, JSON.stringify(i)]));
  const after = new Map(draft.map((i) => [i.id, JSON.stringify(i)]));
  return [...after].filter(([id, item]) => before.get(id) !== item).length + [...before.keys()].filter((id) => !after.has(id)).length;
};

export function ensureWallFloors(items, isWall, original = [], isDiagonal = () => false, previousDraft = null) {
  const tiles = new Map(items.filter(item => item.kind === 'tile').map(item => [item.id, item]));
  const previous = new Map(original.filter(item => item.kind === 'tile').map(item => [item.id, item]));
  const walls = items.filter(item => item.kind === 'entity' && isWall(item.prototype));
  const occupied = new Set([...tiles.keys(), ...walls.map(item => tileId(Math.floor(item.x), Math.floor(item.y)))]);
  const changes = new Map();
  let touched = null;
  if (previousDraft) {
    const before = new Map(previousDraft.map(item => [item.id, item]));
    const after = new Map(items.map(item => [item.id, item]));
    const changed = item => JSON.stringify(before.get(item.id)) !== JSON.stringify(after.get(item.id));
    touched = new Set([...previousDraft, ...items].filter(item => changed(item) &&
      (item.kind === 'tile' || item.kind === 'entity' && isWall(item.prototype))).map(item => tileId(Math.floor(item.x), Math.floor(item.y))));
  }
  for (const wall of walls) {
    const x = Math.floor(wall.x), y = Math.floor(wall.y), id = tileId(x, y);
    if (!Number.isFinite(x) || !Number.isFinite(y)) continue;
    if (touched && ![[0, 0], [-1, 0], [1, 0], [0, -1], [0, 1]].some(([dx, dy]) => touched.has(tileId(x + dx, y + dy)))) continue;
    const existing = tiles.get(id) || previous.get(id);
    const diagonal = isDiagonal(wall.prototype);
    const neighbors = [[-1, 0], [1, 0], [0, -1], [0, 1]].filter(([dx, dy]) => occupied.has(tileId(x + dx, y + dy))).length;
    // Preserve authored floors and full inner corners. Only auto support uses a clipped tile.
    const authoredFull = existing && existing.prototype !== 'DLPlatingDiagonal' && existing.prototype !== 'Plating';
    let floor = existing;
    // The base floor texture occupies NW; a south-facing diagonal wall occupies SE.
    if (!authoredFull && diagonal) floor = { ...(existing || {}), prototype: neighbors >= 3 ? 'Plating' : 'DLPlatingDiagonal',
      variant: 0, mirror: neighbors >= 3 ? 0 : ((Math.round(wall.rotation / 90) + 2) % 4 + 4) % 4 };
    else if (existing?.prototype === 'DLPlatingDiagonal' && !diagonal) floor = { ...existing, prototype: 'Plating', mirror: 0, variant: 0 };
    if (!floor) floor = { prototype: 'Plating' };
    if (tiles.has(id) && existing.prototype === floor.prototype && (existing.mirror || 0) === (floor.mirror || 0) && (existing.variant || 0) === (floor.variant || 0)) continue;
    floor = existing ? { ...existing, ...floor, id, kind: 'tile', x, y } : { id, kind: 'tile', prototype: floor.prototype, x, y, rotation: 0, color: '#FFFFFFFF', z: 0, cleanable: false, anchored: false, variant: 0, flags: 0, mirror: 0, ...floor };
    if (JSON.stringify(tiles.get(id)) !== JSON.stringify(floor)) changes.set(id, floor);
  }
  if (!changes.size) return items;
  return [...items.map(item => changes.get(item.id) || item), ...[...changes.values()].filter(item => !tiles.has(item.id))];
}

export const cellRectangle = (a, b) => ({ x: Math.min(a.x, b.x), y: Math.min(a.y, b.y), width: Math.abs(a.x - b.x) + 1, height: Math.abs(a.y - b.y) + 1 });

export function eraseRectangle(items, rectangle, layers, mirror = null) {
  const reflected = mirror?.enabled ? { ...rectangle, [mirror.axis]: 2 * mirror.center - rectangle[mirror.axis] - rectangle[mirror.axis === 'x' ? 'width' : 'height'] } : null;
  return items.filter(item => !editableItem(item, layers) || !inside(cellOf(item), rectangle) && !(reflected && inside(cellOf(item), reflected)));
}

export function moveSelection(items, ids, dx, dy, area) {
  const selected = new Set(ids), moved = items.filter(item => selected.has(item.id));
  if (!moved.length) return { items, ids };
  let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
  for (const item of moved) { const cell = cellOf(item); minX = Math.min(minX, cell.x); minY = Math.min(minY, cell.y); maxX = Math.max(maxX, cell.x); maxY = Math.max(maxY, cell.y); }
  dx = Math.max(area.x - minX, Math.min(dx, area.x + area.width - 1 - maxX));
  dy = Math.max(area.y - minY, Math.min(dy, area.y + area.height - 1 - maxY));
  const targets = new Set(moved.filter(item => item.kind === 'tile').map(item => tileId(item.x + dx, item.y + dy)));
  const translated = new Map(moved.map(item => [item.id, { ...item, x: item.x + dx, y: item.y + dy,
    id: item.kind === 'tile' ? tileId(item.x + dx, item.y + dy) : item.id }]));
  return { items: items.filter(item => selected.has(item.id) || item.kind !== 'tile' || !targets.has(item.id)).map(item => translated.get(item.id) || item),
    ids: moved.map(item => translated.get(item.id).id) };
}
export function makePatch(original, draft) {
  const before = new Map(original.map(i => [i.id, JSON.stringify(i)]));
  const after = new Set(draft.map(i => i.id));
  return [...draft.filter(i => before.get(i.id) !== JSON.stringify(i)),
    ...original.filter(i => !after.has(i.id)).map(i => ({ id: i.id, kind: 'remove' }))];
}

export function makeTemplate(items) {
  if (!items.length) return [];
  const x = Math.min(...items.map((i) => Math.floor(i.x))), y = Math.min(...items.map((i) => Math.floor(i.y)));
  return items.map((i) => ({ ...i, x: i.x - x, y: i.y - y }));
}

export function projectTemplate(items, target, turns = 0) {
  let result = makeTemplate(items);
  for (let turn = 0; turn < ((turns % 4) + 4) % 4; turn++) {
    const height = Math.max(...result.map((i) => Math.floor(i.y))) + 1;
    result = result.map((i) => {
      const origin = i.kind === 'entity' ? 0 : .5;
      return { ...i, x: height - (i.y + origin) - origin, y: i.x,
        rotation: i.kind === 'tile' ? 0 : (i.rotation + 90) % 360,
        mirror: i.kind === 'tile' ? (i.mirror & 4) | ((i.mirror + 1) % 4) : i.mirror };
    });
  }
  return result.map((i) => ({ ...i, x: i.x + target.x, y: i.y + target.y }));
}

export function insertTemplate(draft, items, target, turns, nextId) {
  let result = copyItems(draft);
  for (const item of projectTemplate(items, target, turns)) {
    if (item.kind === 'tile') {
      item.id = tileId(item.x, item.y); result = result.filter((i) => i.id !== item.id);
    } else {
      if (result.some((i) => i.kind === item.kind && i.prototype === item.prototype && i.x === item.x && i.y === item.y && i.rotation === item.rotation && (item.kind !== 'decal' || i.z === item.z && i.color === item.color && i.cleanable === item.cleanable))) continue;
      item.id = nextId();
    }
    result.push(item);
  }
  return result;
}
// Match the rendered sprite, including rotation and transparent pixels, rather than its tile.
export function indexItems(items, visual = null) {
  const buckets = new Map();
  buckets.radius = 2;
  buckets.order = new Map(items.map((item, index) => [item.id, index]));
  for (const item of items) {
    if(visual){const bounds=spriteBounds(item,visual(item));buckets.radius=Math.max(buckets.radius,Math.ceil(Math.max(Math.abs(bounds.left-item.x),Math.abs(bounds.right-item.x),Math.abs(bounds.top-item.y),Math.abs(bounds.bottom-item.y)))+1);}
    const key = `${Math.floor(item.x)}:${Math.floor(item.y)}`;
    if (!buckets.has(key)) buckets.set(key, []);
    buckets.get(key).push(item);
  }
  return buckets;
}

export function nearbyItems(index, point) {
  const result = [];
  // A rotated decal with a fractional origin can reach two buckets beyond its origin.
  const radius=index.radius||2;
  for (let x = Math.floor(point.x) - radius; x <= Math.floor(point.x) + radius; x++)
    for (let y = Math.floor(point.y) - radius; y <= Math.floor(point.y) + radius; y++)
      result.push(...(index.get(`${x}:${y}`) || []));
  return result.sort((a, b) => index.order.get(a.id) - index.order.get(b.id));
}

export function itemsInRect(index, rect) {
  const left = Math.floor(rect.x) - 2, bottom = Math.floor(rect.y) - 2;
  const right = Math.ceil(rect.x + rect.width) + 2, top = Math.ceil(rect.y + rect.height) + 2;
  const result = [];
  if ((right - left + 1) * (top - bottom + 1) > index.size) {
    for (const [key, items] of index) {
      const [x, y] = key.split(':').map(Number);
      if (x >= left && x <= right && y >= bottom && y <= top) result.push(...items);
    }
  } else {
    for (let x = left; x <= right; x++) for (let y = bottom; y <= top; y++)
      result.push(...(index.get(`${x}:${y}`) || []));
  }
  return result.sort((a, b) => index.order.get(a.id) - index.order.get(b.id));
}

export function expansionCount(bounds, expansion) {
  return (bounds.width + expansion.left + expansion.right) * (bounds.height + expansion.bottom + expansion.top) - bounds.width * bounds.height;
}

export function expansionRects(bounds, expansion) {
  const x = bounds.x - expansion.left, width = bounds.width + expansion.left + expansion.right;
  return [
    { x, y: bounds.y - expansion.bottom, width, height: expansion.bottom },
    { x, y: bounds.y + bounds.height, width, height: expansion.top },
    { x, y: bounds.y, width: expansion.left, height: bounds.height },
    { x: bounds.x + bounds.width, y: bounds.y, width: expansion.right, height: bounds.height },
  ].filter(rect => rect.width && rect.height);
}

export function itemsAtPoint(items, point, layers, visual, opaque = () => true) {
  return items.map((item, index) => ({ item, index })).filter(({ item }) => {
    if (!editableItem(item, layers)) return false;
    if(item.kind==='decal' || item.kind==='entity' && visual(item)?.images?.some(image=>image.native)){
      return spriteLayers(item,visual(item)).some(layer=>{
        const x=point.x-layer.x,y=point.y-layer.y,det=layer.a*layer.d-layer.b*layer.c;
        if(Math.abs(det)<1e-8)return false;
        const u=(layer.d*x-layer.c*y)/det/layer.width+.5,v=.5-(-layer.b*x+layer.a*y)/det/layer.height;
        return u>=0&&u<1&&v>=0&&v<1&&opaque([{...layer.image,native:true}],u,v);
      });
    }
    const left = item.x - (item.kind === 'entity' ? .5 : 0);
    const bottom = item.y - (item.kind === 'entity' ? .5 : 0);
    const x = point.x - left - .5, y = point.y - bottom - .5;
    const rotateTile = item.kind === 'tile' && visual(item)?.allowRotationMirror;
    const angle = -(item.kind === 'tile' ? rotateTile ? (item.mirror & 3) * 90 : 0 : item.rotation || 0) * Math.PI / 180;
    let u = x * Math.cos(angle) - y * Math.sin(angle) + .5;
    const v = 1 - (x * Math.sin(angle) + y * Math.cos(angle) + .5);
    if (rotateTile && (item.mirror & 4)) u = 1 - u;
    return u >= 0 && u < 1 && v >= 0 && v < 1 && opaque(visual(item)?.images || [], u, v);
  }).sort((a, b) => drawOrder(b.item, visual(b.item)) - drawOrder(a.item, visual(a.item)) || b.index - a.index).map(({ item }) => item);
}

export function drawOrder(item, definition) {
  return item.kind === 'tile' ? 1 : item.kind === 'decal' ? 2 + (item.z || 0) / 1000 : 100 + (definition?.drawDepth || 0);
}

export function editorKey(event) {
  if (/^Key[A-Z]$/.test(event.code || '')) return event.code.slice(3).toLowerCase();
  if (/^Digit[0-9]$/.test(event.code || '')) return event.code.slice(5);
  return ({ Escape: 'escape', Delete: 'delete', Space: ' ', ShiftLeft: 'shift', ShiftRight: 'shift' })[event.code] || (event.key || '').toLowerCase();
}


export function reflectedItem(item, mirror, isDiagonal = () => false) {
  const axis = mirror.axis || 'x', offset = item.kind === 'entity' ? 0 : 1;
  const coordinate = 2 * mirror.center - item[axis] - offset;
  const rotation = ((axis === 'x' ? (isDiagonal(item.prototype) ? 270 : 0) : (isDiagonal(item.prototype) ? 90 : 180)) - (item.rotation || 0) + 3600) % 360;
  return { ...item, [axis]: coordinate, rotation: item.kind === 'tile' ? 0 : rotation,
    mirror: item.kind === 'tile' ? (((axis === 'x' ? 0 : 2) - (item.mirror & 3) + 4) % 4) | ((item.mirror || 0) ^ 4) & 4 : item.mirror };
}

// Reflect the selected cell footprint in place, retaining entity/decal identities.
export function reflectSelection(items, ids, axis = 'x', isDiagonal = () => false, rectangle = null) {
  const selected = new Set(ids), source = items.filter(item => selected.has(item.id));
  if (!source.length) return { items, ids: [] };
  let min = Infinity, max = -Infinity;
  for (const item of source) { const cell = Math.floor(item[axis]); min = Math.min(min, cell); max = Math.max(max, cell); }
  const center = rectangle ? rectangle[axis] + rectangle[axis === 'x' ? 'width' : 'height'] / 2 : (min + max + 1) / 2;
  const settings = { axis, center };
  const reflected = source.map(item => {
    const result = reflectedItem(item, settings, isDiagonal);
    if (item.kind === 'tile') result.id = tileId(result.x, result.y);
    return result;
  });
  const targets = new Set(reflected.filter(item => item.kind === 'tile').map(item => item.id));
  return { items: [...items.filter(item => !selected.has(item.id) && !(item.kind === 'tile' && targets.has(item.id))), ...reflected],
    ids: reflected.map(item => item.id) };
}

export function mirrorChanges(previous, next, mirror, area, nextId, pairs = new Map(), isDiagonal = () => false) {
  if (!mirror.enabled) return next;
  const before = new Map(previous.map(item => [item.id, item]));
  const result = new Map(next.map(item => [item.id, item]));
  const touched = new Set();
  const samePosition = (a, b) => a.kind === b.kind && a.prototype === b.prototype && (a.kind !== 'decal' || a.z === b.z) && Math.abs(a.x - b.x) < .0001 && Math.abs(a.y - b.y) < .0001;
  const positionKey = item => `${item.kind}:${item.prototype}:${item.x.toFixed(4)}:${item.y.toFixed(4)}:${item.kind === 'decal' ? item.z : ''}`;
  const positions = new Map(previous.map(item => [positionKey(item), item]));
  const counterpart = item => {
    const reflected = reflectedItem(item, mirror, isDiagonal);
    return before.get(pairs.get(item.id)) || positions.get(positionKey(reflected));
  };
  const removedCells = new Map();
  const cellKey = item => `${item.kind}:${Math.floor(item.x)}:${Math.floor(item.y)}:${item.kind === 'decal' ? item.z : ''}`;
  for (const item of previous) {
    const key = cellKey(item);
    if (!removedCells.has(key)) removedCells.set(key, []);
    removedCells.get(key).push(item);
  }
  const nextIds = new Set(next.map(item => item.id));
  const remainingCells = new Set(next.map(cellKey));
  const eraseCells = new Set();
  for (const [key, items] of removedCells) {
    if (remainingCells.has(key) || items.some(item => nextIds.has(item.id))) continue;
    const item = items[0], axis = mirror.axis;
    const cell = { x: Math.floor(item.x), y: Math.floor(item.y) };
    cell[axis] = Math.floor(2 * mirror.center - cell[axis] - 1);
    eraseCells.add(cellKey({ ...item, ...cell }));
  }
  for (const item of next) if (eraseCells.has(cellKey(item))) result.delete(item.id);
  for (const item of previous) if (!result.has(item.id)) {
    const target = item.kind === 'tile' ? before.get(tileId(reflectedItem(item, mirror).x, reflectedItem(item, mirror).y)) : counterpart(item);
    if (target) result.delete(target.id);
  }
  for (const item of next) {
    if (!result.has(item.id) || touched.has(item.id) || JSON.stringify(before.get(item.id)) === JSON.stringify(item)) continue;
    const reflected = reflectedItem(item, mirror, isDiagonal);
    if (!inside(reflected, area) || samePosition(item, reflected)) continue;
    const old = before.get(item.id), target = item.kind === 'tile' ? null : old ? counterpart(old) : counterpart(item);
    reflected.id = item.kind === 'tile' ? tileId(reflected.x, reflected.y) : target?.id || pairs.get(item.id) || nextId();
    result.set(reflected.id, reflected); touched.add(reflected.id);
    if (item.kind === 'decal' || item.kind === 'entity' && item.anchored) {
      const x = Math.floor(item.x), y = Math.floor(item.y);
      for (const [dx, dy] of [[0,0],[-1,0],[1,0],[0,-1],[0,1]]) {
        const floor = result.get(tileId(x + dx, y + dy));
        if (!floor) continue;
        const reflectedFloor = reflectedItem(floor, mirror);
        reflectedFloor.id = tileId(reflectedFloor.x, reflectedFloor.y);
        if (inside(reflectedFloor, area) && !result.has(reflectedFloor.id)) result.set(reflectedFloor.id, reflectedFloor);
      }
    }
    if (item.kind !== 'tile') { pairs.set(item.id, reflected.id); pairs.set(reflected.id, item.id); }
  }
  return [...result.values()];
}

// Local intent survives a matching live update, deletion or reappearance until Apply/Reset/Undo.
export function trackLocalEdits(previous, next, edits = []) {
  const result = new Map(edits.map(item => [item.id, item]));
  const changed = makePatch(previous, next);
  const floors = new Map(next.filter(item => item.kind === 'tile').map(item => [item.id, item]));
  for (const item of changed) {
    result.set(item.id, item);
    if (item.kind === 'decal' || item.kind === 'entity' && item.anchored) {
      const floor = floors.get(tileId(Math.floor(item.x), Math.floor(item.y)));
      if (floor) result.set(floor.id, floor);
    }
  }
  return [...result.values()];
}

export function overlayLocalEdits(baseline, edits, isExclusive = () => false) {
  const winners = new Map(edits.filter(item => item.kind === 'entity' && isExclusive(item.prototype))
    .map(item => [tileId(Math.floor(item.x), Math.floor(item.y)), item.id]));
  const result = new Map(baseline.filter(item => item.kind !== 'entity' || !isExclusive(item.prototype) ||
    !winners.has(tileId(Math.floor(item.x), Math.floor(item.y))) || winners.get(tileId(Math.floor(item.x), Math.floor(item.y))) === item.id)
    .map(item => [item.id, item]));
  for (const item of edits) {
    if (item.kind === 'remove') result.delete(item.id); else result.set(item.id, item);
  }
  return [...result.values()];
}

export function rebaseExpansion(previous, next, expansion) {
  const retain = (value, rebased) => value === 0 ? 0 : value < 0 ? rebased : Math.max(0, rebased);
  return { left: retain(expansion.left, next.x - (previous.x - expansion.left)),
    right: retain(expansion.right, previous.x + previous.width + expansion.right - next.x - next.width),
    bottom: retain(expansion.bottom, next.y - (previous.y - expansion.bottom)),
    top: retain(expansion.top, previous.y + previous.height + expansion.top - next.y - next.height) };
}

export function resizeExpansion(bounds, expansion, sides, dx, dy, items) {
  const next = { ...expansion };
  let left = bounds.x + bounds.width + expansion.right - 1, right = bounds.x - expansion.left + 1;
  let bottom = bounds.y + bounds.height + expansion.top - 1, top = bounds.y - expansion.bottom + 1;
  for (const item of items) {
    left = Math.min(left, Math.floor(item.x)); right = Math.max(right, Math.floor(item.x) + 1);
    bottom = Math.min(bottom, Math.floor(item.y)); top = Math.max(top, Math.floor(item.y) + 1);
  }
  const minimum = { left: bounds.x - left, right: right - bounds.x - bounds.width,
    bottom: bounds.y - bottom, top: top - bounds.y - bounds.height };
  for (const side of sides) next[side] = Math.max(minimum[side], expansion[side] + ({ left: -dx, right: dx, top: -dy, bottom: dy })[side]);
  return next;
}

export function previewPatch(previous, next) {
  const before = new Map(previous.map(item => [item.id, item]));
  const after = new Map(next.map(item => [item.id, item]));
  return [...next.filter(item => JSON.stringify(before.get(item.id)) !== JSON.stringify(item)),
    ...previous.filter(item => !after.has(item.id)).map(item => ({ id: item.id, kind: 'clear' }))];
}

export function peerPreview(peers, ownEdits, isExclusive = () => false) {
  const protectedIds = new Set(ownEdits.map(item => item.id));
  const protectedCells = new Set(ownEdits.filter(item => item.kind === 'entity' && isExclusive(item.prototype)).map(item => `${Math.floor(item.x)}:${Math.floor(item.y)}`));
  const items = new Map(), removed = new Map();
  for (const peer of peers) {
    for (const item of peer.items) { items.set(item.id, item); removed.delete(item.id); }
    for (const item of peer.removedItems) { items.delete(item.id); removed.set(item.id, item); }
  }
  const visible = item => !protectedIds.has(item.id) && !(item.kind === 'entity' && isExclusive(item.prototype) && protectedCells.has(`${Math.floor(item.x)}:${Math.floor(item.y)}`));
  return { items: [...items.values()].filter(visible), removed: [...removed.values()].filter(visible) };
}

// Shared drafts take part in the same hit testing and editing as world items.
export function collaborativeDraft(baseline, peers, edits, isExclusive = () => false) {
  const foreign = peerPreview(peers, [], isExclusive);
  return overlayLocalEdits(overlayLocalEdits(baseline,
    [...foreign.items, ...foreign.removed.map(item => ({ id: item.id, kind: 'remove' }))], isExclusive), edits, isExclusive);
}

export function discardSuperseded(edits, superseded) {
  const old = new Map(superseded.map(item => [item.id, item]));
  return edits.filter(item => {
    const previous = old.get(item.id);
    if (!previous || previous.kind !== item.kind) return true;
    return item.kind !== 'remove' && Object.keys(item).some(key => item[key] !== previous[key]);
  });
}
