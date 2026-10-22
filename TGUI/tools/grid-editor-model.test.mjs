import test from 'node:test';
import assert from 'node:assert/strict';
import { collaborativeDraft, discardSuperseded, cellsBetween, trackLocalEdits, overlayLocalEdits, rebaseExpansion, resizeExpansion, previewPatch, peerPreview, stamp, copyItems, validateDraft, changeCount, makeTemplate, projectTemplate, insertTemplate, makePatch, itemsAtPoint, editorKey, indexItems, nearbyItems, itemsInRect, expansionCount, expansionRects, ensureWallFloors, cellRectangle, eraseRectangle, moveSelection, replaceConflicts, mirrorChanges, reflectedItem, reflectSelection } from '../packages/tgui/interfaces/gridEditorModel.mjs';
const options = { rotation: 90, color: '#ffffff', z: 0, cleanable: false, replace: true, isWall: (id) => id === 'Wall' };
let id = 0;
const nextId = () => `new_${id++}`;
const area = { x: -4, y: -4, width: 16, height: 16 };

test('wall foundations fill missing cells once and preserve existing floors and their variants', () => {
  const wall = { id: 'w', kind: 'entity', prototype: 'Wall', x: -1.5, y: .5, anchored: true };
  const supported = ensureWallFloors([wall, { ...wall, id: 'second' }], id => id === 'Wall');
  assert.equal(supported.filter(i => i.kind === 'tile').length, 1);
  assert.equal(supported.find(i => i.kind === 'tile').prototype, 'Plating');
  assert.equal(validateDraft(supported, area), '');
  const original = { id: 't:-2:0', kind: 'tile', prototype: 'FloorSteel', x: -2, y: 0, variant: 3, flags: 2, mirror: 4 };
  assert.deepEqual(ensureWallFloors([wall], id => id === 'Wall', [original]).find(i => i.kind === 'tile'), original);
  assert.deepEqual(ensureWallFloors([wall, original], id => id === 'Wall'), [wall, original]);
  assert.deepEqual(ensureWallFloors([{ ...wall, prototype: 'Chair' }], id => id === 'Wall'), [{ ...wall, prototype: 'Chair' }]);
});

test('group movement translates all layers, replaces destination tiles, and retains entity IDs and offsets', () => {
  const items = [{ id: 't:0:0', kind: 'tile', prototype: 'Steel', x: 0, y: 0, variant: 2 },
    { id: 'wall', kind: 'entity', prototype: 'Wall', x: .5, y: .5, anchored: true },
    { id: 'decal', kind: 'decal', x: .125, y: .75, rotation: 90 },
    { id: 't:2:1', kind: 'tile', prototype: 'OldFloor', x: 2, y: 1 },
    { id: 'untouched', kind: 'entity', x: 4.5, y: 4.5 }];
  const moved = moveSelection(items, ['t:0:0', 'wall', 'decal'], 2, 1, area);
  assert.deepEqual(moved.ids, ['t:2:1', 'wall', 'decal']);
  assert.equal(moved.items.filter(i => i.id === 't:2:1').length, 1);
  assert.equal(moved.items.find(i => i.id === 't:2:1').variant, 2);
  assert.equal(moved.items.find(i => i.id === 'wall').x, 2.5);
  assert.equal(moved.items.find(i => i.id === 'decal').x, 2.125);
  assert.equal(moved.items.find(i => i.id === 'decal').y, 1.75);
  assert.deepEqual(moved.items.find(i => i.id === 'untouched'), items[4]);
  assert.deepEqual(items[0], { id: 't:0:0', kind: 'tile', prototype: 'Steel', x: 0, y: 0, variant: 2 });
});

test('group bounds clamp the whole selection and never separate its members', () => {
  const items = [{ id: 'a', kind: 'entity', x: 10.5, y: 1.5 }, { id: 'b', kind: 'entity', x: 11.5, y: 2.5 }];
  const moved = moveSelection(items, ['a', 'b'], 20, 0, area);
  assert.equal(moved.items[0].x, 10.5); assert.equal(moved.items[1].x, 11.5);
});

test('rubber-band erasing works backwards, includes boundary cells, and protects hidden layers', () => {
  const rect = cellRectangle({ x: 2, y: 2 }, { x: 0, y: 0 });
  assert.deepEqual(rect, { x: 0, y: 0, width: 3, height: 3 });
  const items = [{ id: 'floor', kind: 'tile', x: 0, y: 0 }, { id: 'wall', kind: 'entity', x: 2.5, y: 2.5 },
    { id: 'decal', kind: 'decal', x: 1, y: 1 }, { id: 'outside', kind: 'tile', x: 3, y: 2 }];
  assert.deepEqual(eraseRectangle(items, rect, { tile: true, entity: true, decal: false }).map(i => i.id), ['decal', 'outside']);
  assert.equal(items.length, 4);
});

test('a complete 250x250 grid remains valid beyond the former 12000 item limit', () => {
  const items = [];
  for (let x = -125; x < 125; x++) for (let y = -125; y < 125; y++)
    items.push({ id: `t:${x}:${y}`, kind: 'tile', prototype: 'Plating', x, y });
  assert.equal(validateDraft(items, { x: -129, y: -129, width: 258, height: 258 }), '');
  const visible = itemsInRect(indexItems(items), { x: 120, y: 120, width: 5, height: 5 });
  assert.ok(visible.some(i => i.id === 't:124:124'));
  assert.ok(!visible.some(i => i.id === 't:-125:-125'));
  assert.ok(visible.length < 100);
});

test('viewport lookup preserves draw order and handles sparse grids with distant islands', () => {
  const items = [{ id: 'a', x: -10000, y: -10000 }, { id: 'b', x: 10000, y: 10000 }, { id: 'c', x: -9999.5, y: -9999.5 }];
  const index = indexItems(items);
  assert.deepEqual(itemsInRect(index, { x: -10001, y: -10001, width: 3, height: 3 }).map(i => i.id), ['a', 'c']);
  assert.deepEqual(itemsInRect(index, { x: -11000, y: -11000, width: 22000, height: 22000 }).map(i => i.id), ['a', 'b', 'c']);
});

test('picker hits the opaque sprite above a floor and passes through transparent table items', () => {
  const items = [{ id: 'floor', kind: 'tile', x: 0, y: 0 }, { id: 'table', kind: 'entity', x: .5, y: .5, rotation: 0 }, { id: 'cup', kind: 'entity', x: .5, y: .5, rotation: 0 }];
  const layers = { tile: true, entity: true, decal: true };
  const visual = item => ({ drawDepth: item.id === 'cup' ? 10 : 2, images: [item.id] });
  const opaque = (images, u, v) => images[0] !== 'cup' || u > .4 && u < .6 && v > .4 && v < .6;
  assert.equal(itemsAtPoint(items, { x: .2, y: .2 }, layers, visual, opaque)[0].id, 'table');
  assert.equal(itemsAtPoint(items, { x: .5, y: .5 }, layers, visual, opaque)[0].id, 'cup');
  assert.deepEqual(itemsAtPoint(items, { x: .5, y: .5 }, layers, visual, opaque).map(i => i.id), ['cup', 'table', 'floor']);
  assert.equal(itemsAtPoint(items, { x: .5, y: .5 }, { ...layers, entity: false }, visual, opaque)[0].id, 'floor');
});

test('picker respects fractional sprite positions and rotation', () => {
  const item = { id: 'object', kind: 'entity', x: 1.25, y: .5, rotation: 90 };
  const visual = () => ({ images: ['object'] });
  const opaque = (_, u) => u < .5;
  assert.equal(itemsAtPoint([item], { x: 1.25, y: .25 }, { entity: true }, visual, opaque).length, 1);
  assert.equal(itemsAtPoint([item], { x: 1.25, y: .75 }, { entity: true }, visual, opaque).length, 0);
  assert.equal(itemsAtPoint([item], { x: .5, y: .25 }, { entity: true }, visual, opaque).length, 0);
});

test('shortcuts use physical keys with Russian and native CEF key events', () => {
  assert.equal(editorKey({ key: 'я', code: 'KeyZ', ctrlKey: true }), 'z');
  assert.equal(editorKey({ code: 'KeyI' }), 'i');
  assert.equal(editorKey({ code: 'Digit1' }), '1');
  assert.equal(editorKey({ code: 'Digit2', key: '"', shiftKey: true }), '2');
  assert.equal(editorKey({ code: 'Escape' }), 'escape');
  assert.equal(editorKey({ code: 'ShiftLeft' }), 'shift');
});

test('line brush has no gaps in either direction, including negative grid coordinates', () => {
  const a = { x: -3, y: -2 }, b = { x: 4, y: 2 };
  const cells = cellsBetween(a, b);
  assert.deepEqual(cells[0], a); assert.deepEqual(cells.at(-1), b);
  assert.equal(cells.length, 8);
  for (let i = 1; i < cells.length; i++) {
    assert.ok(Math.abs(cells[i].x - cells[i - 1].x) <= 1);
    assert.ok(Math.abs(cells[i].y - cells[i - 1].y) <= 1);
  }
  assert.deepEqual(cellsBetween(b, a), cells.toReversed());
});
test('rectangle works when dragged backwards; repaint replaces tiles rather than duplicating them', () => {
  const cells = cellsBetween({ x: 2, y: 2 }, { x: 0, y: 0 }, true);
  assert.equal(cells.length, 9);
  const first = stamp([], cells, { kind: 'tile', id: 'Steel' }, options, nextId);
  const before = copyItems(first);
  const second = stamp(first, cells, { kind: 'tile', id: 'Wood' }, options, nextId);
  assert.equal(second.length, 9); assert.equal(changeCount(first, second), 9);
  assert.deepEqual(first, before);
  assert.equal(validateDraft(second, area), '');
});
test('wall replacement preserves furniture and avoids repeat-stroke duplicates', () => {
  const old = [{ id: 'wall', kind: 'entity', prototype: 'Wall', x: .5, y: .5 },
    { id: 'chair', kind: 'entity', prototype: 'Chair', x: .5, y: .5 }];
  const brush = { id: 'NewWall', kind: 'entity', category: 'wall', anchored: true };
  const first = stamp(old, [{ x: 0, y: 0 }], brush, options, nextId);
  const second = stamp(first, [{ x: 0, y: 0 }], brush, options, nextId);
  assert.equal(first.length, 2); assert.equal(second.length, 2);
  assert.equal(second[0].prototype, 'Chair'); assert.equal(second[1].rotation, 90);
  assert.deepEqual(old.map((i) => i.prototype), ['Wall', 'Chair']);
});
test('draft validation rejects unsupported decals, anchored objects and out-of-area moves', () => {
  const decal = { id: 'new_decal', kind: 'decal', x: -.25, y: .25 };
  assert.match(validateDraft([decal], area), /пол/);
  const tile = { id: 't:-1:0', kind: 'tile', x: -1, y: 0 };
  assert.equal(validateDraft([tile, decal], area), '');
  assert.match(validateDraft([tile, { ...decal, x: 12 }], area), /границ/);
  assert.match(validateDraft([tile, tile], area), /ID/);
});
test('one drag history entry restores fractional entity coordinates', () => {
  const saved = [{ id: 'e:1', kind: 'entity', prototype: 'Chair', x: .625, y: -.125, rotation: 45 }];
  const history = copyItems(saved);
  const moved = saved.map((i) => ({ ...i, x: i.x + 3, y: i.y + 2 }));
  assert.equal(changeCount(saved, moved), 1);
  assert.equal(moved[0].x % 1, .625);
  assert.deepEqual(history, saved);
});
test('a building template retains all layers and can be inserted repeatedly with unique entity IDs', () => {
  const original = [
    { id: 't:-2:3', kind: 'tile', prototype: 'Steel', x: -2, y: 3, rotation: 0, mirror: 0 },
    { id: 'e:1', kind: 'entity', prototype: 'Wall', x: -1.5, y: 3.5, rotation: 90, anchored: true },
    { id: 'd:1', kind: 'decal', prototype: 'Line', x: -2, y: 3, rotation: 0, color: '#ff0000', z: 4 },
  ];
  const template = makeTemplate(original);
  assert.equal(template[0].x, 0); assert.equal(template[1].x, .5);
  let draft = insertTemplate([], template, { x: 1, y: 1 }, 0, nextId);
  draft = insertTemplate(draft, template, { x: 4, y: 1 }, 0, nextId);
  assert.equal(draft.length, 6); assert.equal(new Set(draft.map(i => i.id)).size, 6);
  assert.equal(validateDraft(draft, area), '');
  assert.equal(draft.find(i => i.kind === 'decal').color, '#ff0000');
  assert.equal(original[0].x, -2);
});
test('rotated templates keep anchored centers on tiles and return to the original after four turns', () => {
  const template = [
    { id: 't:0:0', kind: 'tile', x: 0, y: 0, rotation: 0, mirror: 0 },
    { id: 't:1:0', kind: 'tile', x: 1, y: 0, rotation: 0, mirror: 0 },
    { id: 'e:1', kind: 'entity', x: 1.5, y: .5, rotation: 0, anchored: true },
  ];
  const rotated = projectTemplate(template, { x: -1, y: -1 }, 1);
  assert.equal(rotated[2].x, -.5); assert.equal(rotated[2].y, .5); assert.equal(rotated[2].rotation, 90);
  for (const item of rotated.filter(i => i.kind === 'entity')) {
    assert.equal(Math.abs(item.x % 1), .5); assert.equal(Math.abs(item.y % 1), .5);
  }
  assert.equal(validateDraft(insertTemplate([], template, { x: -1, y: -1 }, 1, nextId), area), '');
  assert.deepEqual(projectTemplate(template, { x: 0, y: 0 }, 4), template);
});
test('editing one element sends only its change, while deletions produce explicit tombstones', () => {
  const original = Array.from({ length: 1000 }, (_, index) => ({ id: 'e:' + index, kind: 'entity', prototype: 'Chair', x: index, y: .5 }));
  const draft = original.slice(1).map(i => i.id === 'e:10' ? { ...i, y: 1.5 } : i);
  const patch = makePatch(original, draft);
  assert.equal(patch.length, 2); assert.equal(changeCount(original, draft), 2);
  assert.deepEqual(patch[1], { id: 'e:0', kind: 'remove' });
  const rebuilt = new Map(original.map(i => [i.id, i]));
  for (const item of patch) item.kind === 'remove' ? rebuilt.delete(item.id) : rebuilt.set(item.id, item);
  assert.deepEqual([...rebuilt.values()], draft);
});


test('large-grid picking scans local buckets and preserves rotated overlap order', () => {
  const items = [];
  for (let x = 0; x < 250; x++) for (let y = 0; y < 250; y++)
    items.push({ id: `t:${x}:${y}`, kind: 'tile', x, y, rotation: 0 });
  items.push({ id: 'rotated', kind: 'decal', x: 100.2, y: 100.4, rotation: 45 });
  items.push({ id: 'later', kind: 'decal', x: 99.8, y: 100.1, rotation: -45 });
  items.push({ id: 'fractional-edge', kind: 'decal', x: 99.9, y: 100.9, rotation: 45 });
  const index = indexItems(items), layers = { tile: true, decal: true }, visual = () => ({});
  for (const point of [{ x: 100.4, y: 100.8 }, { x: 100, y: 100 }, { x: 101.05, y: 101.4 }, { x: 249.9, y: 249.9 }]) {
    const local = nearbyItems(index, point);
    assert.ok(local.length <= 28, 'Pointer work must be independent of total grid size');
    assert.deepEqual(itemsAtPoint(local, point, layers, visual), itemsAtPoint(items, point, layers, visual));
  }
});

test('expansion counts only new borders, including corners, on a 250 by 250 grid', () => {
  const bounds = { width: 250, height: 250 };
  assert.equal(expansionCount(bounds, { left: 0, right: 2, bottom: 0, top: 2 }), 1004);
  assert.equal(expansionCount(bounds, { left: 1, right: 1, bottom: 1, top: 1 }), 1004);
  assert.equal(expansionCount(bounds, { left: 0, right: 0, bottom: 0, top: 0 }), 0);
});


test('expansion preview rectangles cover borders exactly once at negative coordinates', () => {
  const bounds = { x: -10, y: -15, width: 20, height: 30 };
  const expansion = { left: 2, right: 3, bottom: 1, top: 4 };
  const seen = new Set();
  for (const rect of expansionRects(bounds, expansion)) {
    for (let x = rect.x; x < rect.x + rect.width; x++) for (let y = rect.y; y < rect.y + rect.height; y++) {
      assert.ok(!insideCell(x, y), 'Do not fill old interior holes');
      const key = `${x}:${y}`; assert.ok(!seen.has(key), 'Corners must appear only once'); seen.add(key);
    }
  }
  function insideCell(x, y) { return x >= bounds.x && x < bounds.x + bounds.width && y >= bounds.y && y < bounds.y + bounds.height; }
  assert.equal(seen.size, expansionCount(bounds, expansion));
});


const testTile = (x, y, prototype = 'Plating') => ({ id: tileIdForTest(x,y), kind: 'tile', prototype, x, y, rotation:0, color:'#FFFFFFFF',z:0,cleanable:false,anchored:false,variant:0,flags:0,mirror:0 });
const tileIdForTest = (x,y) => `t:${x}:${y}`;
const testWall = (id, x, y, rotation=0, prototype='Diagonal') => ({id,kind:'entity',prototype,x:x+.5,y:y+.5,rotation,anchored:true});
const wallPrototype = id => ['Wall','Diagonal'].includes(id);

test('diagonal supports rotate with walls, become square inside corners, and preserve authored floors', () => {
  const wall = testWall('w',0,0);
  let items = ensureWallFloors([wall], wallPrototype, [], id => id === 'Diagonal');
  assert.equal(items.find(i=>i.kind==='tile').prototype,'DLPlatingDiagonal');
  for(const rotation of [90,180,270,0]) {
    items=ensureWallFloors(items.map(i=>i.id==='w'?{...i,rotation}:i),wallPrototype,[],id=>id==='Diagonal');
    assert.equal(items.find(i=>i.kind==='tile').mirror,(rotation/90+2)%4);
  }
  const inner = ensureWallFloors([wall,testTile(0,1),testTile(1,0),testTile(0,-1),testTile(-1,0)],wallPrototype,[],id=>id==='Diagonal');
  assert.equal(inner.find(i=>i.id==='t:0:0').prototype,'Plating');
  const plating=testTile(0,0);
  assert.equal(ensureWallFloors([wall,plating],wallPrototype,[plating],id=>id==='Diagonal').find(i=>i.id===plating.id).prototype,'DLPlatingDiagonal');
  const authored=testTile(0,0,'FloorSteel');
  assert.strictEqual(ensureWallFloors([wall,authored],wallPrototype,[authored],id=>id==='Diagonal').find(i=>i.id===authored.id),authored);
});

test('incoming walls, windows, tables and closets replace cell occupancy and preserve loose items and decals', () => {
  const exclusive=id=>['Wall','Window','Table','Closet'].includes(id);
  for(const prototype of ['Wall','Window','Table','Closet']) {
    const old=testWall('old',2,2,0,'Wall'), loose={...old,id:'loose',prototype:'Cup'}, decal={...old,id:'decal',kind:'decal'};
    const incoming=testWall('incoming',2,2,0,prototype);
    const result=replaceConflicts([old,loose,decal],[old,loose,decal,incoming],exclusive);
    assert.deepEqual(result.map(i=>i.id),['loose','decal','incoming']);
  }
});

test('mirroring retains pairs while moving, deleting and rotating and does not duplicate the axis', () => {
  const bounds={x:-10,y:-10,width:20,height:20}, settings={enabled:true,axis:'x',center:0}, pairs=new Map();
  const wall=testWall('source',-3,0,0,'Wall');
  const added=mirrorChanges([], [wall],settings,bounds,nextId,pairs);
  const clone=added.find(i=>i.id!=='source'); assert.equal(clone.x,2.5);
  const moved=mirrorChanges(added,added.map(i=>i.id==='source'?{...i,x:-1.5,rotation:90}:i),settings,bounds,nextId,pairs);
  assert.equal(moved.length,2);assert.equal(moved.find(i=>i.id===clone.id).x,1.5);assert.equal(moved.find(i=>i.id===clone.id).rotation,270);
  assert.equal(mirrorChanges(moved,moved.filter(i=>i.id!=='source'),settings,bounds,nextId,pairs).length,0);
  const onAxis={...wall,x:0};assert.equal(mirrorChanges([],[onAxis],settings,bounds,nextId).length,1);
  const tile=testTile(-2,0);const reflected=reflectedItem(tile,settings);assert.equal(reflected.x,1);
  assert.equal(reflectedItem(reflected,settings).mirror,tile.mirror);
});

test('large rectangle stamping avoids duplicate tiles and supports 250 by 250 fills', () => {
  const cells=cellsBetween({x:0,y:0},{x:249,y:249},true);
  const result=stamp([testTile(0,0)],cells,{kind:'tile',id:'Plating'},options,nextId);
  assert.equal(result.length,62500);assert.equal(new Set(result.map(i=>i.id)).size,62500);
});


test('live updates preserve local changes, deletions and supporting floors until explicit reset or apply', () => {
  const floor = testTile(0, 0), chair = { ...testWall('chair', 0, 0, 0, 'Chair'), anchored: true };
  const previous = [floor, chair, testTile(1, 0)];
  const next = previous.filter(item => item.id !== 't:1:0').map(item => item.id === 'chair' ? { ...item, rotation: 90 } : item);
  const edits = trackLocalEdits(previous, next);
  assert.ok(edits.some(item => item.id === floor.id), 'The floor under an edited anchored object is protected');
  const live = [testTile(1, 0, 'FloorSteel'), testTile(5, 5)];
  const merged = overlayLocalEdits(live, edits);
  assert.equal(merged.find(item => item.id === 'chair').rotation, 90);
  assert.ok(merged.some(item => item.id === floor.id));
  assert.ok(!merged.some(item => item.id === 't:1:0'));
  assert.ok(merged.some(item => item.id === 't:5:5'));
  // A matching update must not erase the local tombstone before an item reappears.
  assert.ok(!overlayLocalEdits([...live, testTile(1, 0)], edits).some(item => item.id === 't:1:0'));
  assert.deepEqual(overlayLocalEdits(live, []), live);
});

test('undo can restore only local intent over the latest live map instead of restoring a stale snapshot', () => {
  const old = [testTile(0, 0)], local = [testTile(0, 0, 'FloorSteel')];
  const before = [], after = trackLocalEdits(old, local);
  const latest = [testTile(0, 0), testTile(4, 4)];
  assert.equal(overlayLocalEdits(latest, before).find(item => item.id === 't:0:0').prototype, 'Plating');
  assert.equal(overlayLocalEdits(latest, after).find(item => item.id === 't:0:0').prototype, 'FloorSteel');
  assert.ok(overlayLocalEdits(latest, before).some(item => item.id === 't:4:4'));
});

test('live area expansion retains absolute local empty-space bounds without duplicating remote growth', () => {
  assert.deepEqual(rebaseExpansion({x:0,y:0,width:10,height:10}, {x:-4,y:-4,width:18,height:18},
    {left:8,right:8,bottom:8,top:8}), {left:4,right:4,bottom:4,top:4});
});

test('preview deltas distinguish clearing an intent from deleting a world object', () => {
  const deletion = {id:'e:1',kind:'remove'}, tile = testTile(0,0);
  assert.deepEqual(previewPatch([], [deletion]), [deletion]);
  assert.deepEqual(previewPatch([deletion], []), [{id:'e:1',kind:'clear'}]);
  assert.deepEqual(previewPatch([tile], [tile]), []);
});

test('resize contracts every edge to occupied cells and keeps one cell on an empty grid', () => {
  const bounds = {x:-10,y:-10,width:20,height:20}, zero = {left:0,right:0,bottom:0,top:0};
  const items = [{x:-2,y:-3,kind:'tile'}, {x:3.5,y:4.5,kind:'entity'}];
  assert.deepEqual(resizeExpansion(bounds, zero, ['left','top'], 100, 100, items),
    {left:-8,right:0,bottom:0,top:-5});
  assert.deepEqual(resizeExpansion(bounds, zero, ['right','bottom'], -100, -100, items),
    {left:0,right:-6,bottom:-7,top:0});
  assert.deepEqual(resizeExpansion(bounds, zero, ['right','top'], -100, 100, []),
    {left:0,right:-19,bottom:0,top:-19});
  assert.deepEqual(resizeExpansion(bounds, { ...zero, right:5 }, ['right'], -7, 0, []),
    {...zero,right:-2});
});

test('live patches preserve a contracted draft and do not undo a remote contraction', () => {
  const previous = {x:-10,y:-10,width:20,height:20}, next = {x:-5,y:-5,width:10,height:10};
  assert.deepEqual(rebaseExpansion(previous, next, {left:0,right:0,bottom:0,top:0}),
    {left:0,right:0,bottom:0,top:0});
  assert.deepEqual(rebaseExpansion(previous, {x:-11,y:-10,width:21,height:20},
    {left:-2,right:-3,bottom:-4,top:-5}), {left:-3,right:-3,bottom:-4,top:-5});
});

test('foreign drafts are visible but local edits take priority and are never mutated', () => {
  const local = [testTile(0,0,'FloorSteel')];
  const peers = [{items:[testTile(0,0),testTile(2,2)],removedItems:[testTile(3,3)]}];
  const result = peerPreview(peers, local);
  assert.deepEqual(result.items.map(item=>item.id), ['t:2:2']);
  assert.deepEqual(result.removed.map(item=>item.id), ['t:3:3']);
  assert.equal(local[0].prototype, 'FloorSteel');
});

test('invalid external support does not block an unrelated valid local edit', () => {
  const unsupported = { ...testWall('old',0,0,0,'Chair'), anchored:true };
  assert.equal(validateDraft([unsupported,testTile(1,1)],area,new Set(['t:1:1'])), '');
  assert.notEqual(validateDraft([unsupported],area,new Set(['old'])), '');
});


test('support normalization touches only edited walls and their neighbors', () => {
  const wall = { id:'w',kind:'entity',prototype:'Diagonal',x:20.5,y:20.5,rotation:0,anchored:true };
  const oldFloor = { ...testTile(20,20),prototype:'DLPlatingDiagonal',mirror:0 };
  const original = [wall,oldFloor,testTile(0,0)];
  const edited = original.map(i=>i.id==='t:0:0'?{...i,prototype:'FloorSteel'}:i);
  const result = ensureWallFloors(edited,()=>true,original,()=>true,original);
  assert.strictEqual(result.find(i=>i.id===oldFloor.id),oldFloor);
  const rotated = result.map(i=>i.id==='w'?{...i,rotation:90}:i);
  assert.equal(ensureWallFloors(rotated,()=>true,result,()=>true,result).find(i=>i.id===oldFloor.id).mirror,3);
});

test('local construction hides conflicting external construction until undo', () => {
  const external = {id:'external',kind:'entity',prototype:'Wall',x:.5,y:.5};
  const own = {...external,id:'new_own',rotation:90};
  const cup = {...external,id:'cup',prototype:'Cup'};
  const baseline = [external,cup];
  const result = overlayLocalEdits(baseline,[own],p=>p==='Wall');
  assert.deepEqual(result,[cup,own]);
  assert.deepEqual(overlayLocalEdits(baseline,[],p=>p==='Wall'),baseline);
});


test('selection reflection swaps floor IDs and reflects entity/decal orientation in place', () => {
  const left=testTile(-2,1,'FloorSteel'), right=testTile(0,1), outside=testTile(5,1);
  const chair={id:'chair',kind:'entity',prototype:'Chair',x:-1.75,y:1.5,rotation:90};
  const decal={id:'decal',kind:'decal',prototype:'Mark',x:-1.9,y:1.2,rotation:45};
  const items=[left,right,outside,chair,decal], ids=items.filter(i=>i!==outside).map(i=>i.id);
  const result=reflectSelection(items,ids);
  assert.equal(result.items.find(i=>i.id==='t:0:1').prototype,'FloorSteel');
  assert.equal(result.items.find(i=>i.id==='t:-2:1').prototype,'Plating');
  assert.equal(result.items.find(i=>i.id==='chair').x,.75);
  assert.equal(result.items.find(i=>i.id==='chair').rotation,270);
  assert.ok(Math.abs(result.items.find(i=>i.id==='decal').x+.1)<.00001);
  assert.equal(result.items.find(i=>i.id==='decal').rotation,315);
  assert.strictEqual(result.items.find(i=>i.id===outside.id),outside);
  assert.equal(new Set(result.items.map(i=>i.id)).size,items.length);
  const restored=reflectSelection(result.items,result.ids).items;
  for(const original of items) {
    const item=restored.find(i=>i.id===original.id);
    assert.ok(Math.abs(item.x-original.x)<.00001);assert.equal(item.rotation,original.rotation);
    assert.equal(item.mirror||0,original.mirror||0);
  }
  assert.equal(items[0].x,-2);
});

test('vertical reflection handles diagonal walls and empty selections', () => {
  const items=[testWall('a',0,-2,0),testWall('b',0,0,90)];
  const result=reflectSelection(items,['a','b'],'y',id=>id==='Diagonal');
  assert.equal(result.items.find(i=>i.id==='a').y,.5);
  assert.equal(result.items.find(i=>i.id==='a').rotation,90);
  assert.deepEqual(reflectSelection(items,[]),{items,ids:[]});
});

test('reflection uses the S rectangle including empty edge cells', () => {
  const wall=testWall('wall',0,0,0,'Wall');
  const result=reflectSelection([wall],['wall'],'x',()=>false,{x:0,y:0,width:4,height:1});
  assert.equal(result.items[0].x,3.5);
});


test('peer drafts are editable including creations, deletions and moves, with a local override', () => {
  const floor = testTile(0, 0), newFloor = testTile(2, 0);
  const chair = { id: 'new_peer', kind: 'entity', prototype: 'Chair', x: 2.5, y: .5, anchored: true };
  const peers = [{ items: [newFloor, chair], removedItems: [floor] }];
  const shared = collaborativeDraft([floor], peers, []);
  assert.deepEqual(shared, [newFloor, chair]);
  const moved = shared.map(item => item.id === chair.id ? { ...item, rotation: 90 } : item);
  const edits = trackLocalEdits(shared, moved);
  assert.equal(edits.find(item => item.id === chair.id).id, 'new_peer');
  assert.equal(collaborativeDraft([floor], peers, edits).find(item => item.id === chair.id).rotation, 90);
  assert.ok(!collaborativeDraft([floor], peers, [{ id: chair.id, kind: 'remove' }]).some(item => item.id === chair.id));
  const overridden = edits.find(item => item.id === chair.id);
  assert.ok(!discardSuperseded(edits, [overridden]).some(item => item.id === chair.id));
  const newer = { ...overridden, rotation: 180 };
  assert.deepEqual(discardSuperseded([newer], [overridden]), [newer], 'Unacknowledged newer local input survives');
});

test('mirrored cell erasure removes asymmetric decals and all duplicates while preserving hidden layers', () => {
  for (const axis of ['x', 'y']) {
    const bounds = { x: -10, y: -10, width: 20, height: 20 };
    const mirror = { enabled: true, axis, center: 0 };
    const source = { id: 'source', kind: 'decal', prototype: 'A', x: -2.125, y: -2.75 };
    const target = { ...source, id: 'target', prototype: 'B', [axis]: 2.1 };
    const duplicate = { ...target, id: 'duplicate', [axis]: 2.9 };
    const floor = testTile(Math.floor(target.x), Math.floor(target.y));
    const items = [source, target, duplicate, floor];
    const erased = eraseRectangle(items, { x: -3, y: -3, width: 1, height: 1 }, { decal: true });
    assert.deepEqual(mirrorChanges(items, erased, mirror, bounds, nextId), [floor]);
  }
});

test('mirrored rectangle erases destination-only decals even when the source rectangle is empty', () => {
  for (const axis of ['x', 'y']) {
    const mirror = { enabled: true, axis, center: .5 };
    const rectangle = { x: -3, y: -3, width: 2, height: 2 };
    const decal = { id: 'remote', kind: 'decal', prototype: 'A', x: -2.75, y: -2.5, [axis]: 2.125 };
    const chair = { ...decal, id: 'hidden', kind: 'entity' };
    assert.deepEqual(eraseRectangle([decal, chair], rectangle, { decal: true }, mirror), [chair]);
  }
});
