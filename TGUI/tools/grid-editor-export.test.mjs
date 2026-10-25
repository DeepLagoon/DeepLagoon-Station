import test from 'node:test';
import assert from 'node:assert/strict';
import { load } from 'js-yaml';
import { exportGridDraft, draftFileChunks } from '../packages/tgui/interfaces/gridEditorExport.mjs';

const bounds = { x: -1, y: -1, width: 3, height: 3 };
const noExpansion = { left: 0, right: 0, bottom: 0, top: 0, tile: 'Plating' };
const floor = (x, y, prototype = 'Plating') => ({ kind: 'tile', prototype, x, y });
const document = (items, expansion = noExpansion) => load(exportGridDraft(items, bounds, expansion));
const gridComponents = doc => doc.entities.find(group => group.proto === '').entities[0].components;
function tiles(doc) {
  const result = new Map();
  for (const chunk of Object.values(gridComponents(doc).find(c => c.type === 'MapGrid').chunks)) {
    assert.equal(chunk.version, 7);
    const [cx, cy] = chunk.ind.split(',').map(Number), bytes = Buffer.from(chunk.tiles, 'base64');
    assert.equal(bytes.length, 16 * 16 * 7);
    for (let y = 0; y < 16; y++) for (let x = 0; x < 16; x++) {
      const offset = (y * 16 + x) * 7, prototype = doc.tilemap[bytes.readInt32LE(offset)];
      assert.ok(prototype, 'Every tile, including empty cells, has a definition');
      if (prototype !== 'Space') result.set(`${cx * 16 + x},${cy * 16 + y}`, {
        prototype, flags: bytes[offset + 4], variant: bytes[offset + 5], mirror: bytes[offset + 6],
      });
    }
  }
  return result;
}

test('draft export preserves the grid name, including Cyrillic, quotes and YAML punctuation', () => {
  const gridName = 'Жилой модуль «Маяк»: палуба 2 #1';
  const doc = load(exportGridDraft([floor(0, 0)], bounds, noExpansion, { gridName }));
  assert.equal(gridComponents(doc).find(c => c.type === 'MetaData').name, gridName);
  assert.equal(gridComponents(document([floor(0, 0)])).find(c => c.type === 'MetaData').name, 'grid');
});

test('draft YAML round-trips negative chunk boundaries and all tile metadata', () => {
  const positions = [-17, -16, -1, 0, 15, 16, 257];
  const input = positions.flatMap(x => positions.map(y => ({ ...floor(x, y, 'FloorSteel'), flags: 255, variant: 3, mirror: 7 })));
  const result = tiles(document(input));
  assert.equal(result.size, input.length);
  for (const item of input) assert.deepEqual(result.get(`${item.x},${item.y}`), { prototype: item.prototype, flags: 255, variant: 3, mirror: 7 });
});

test('exports final snapshot: additions, removals, moved objects, exact rotations and decal properties', () => {
  const draft = [floor(-1, 0),
    { id: 'e:42', kind: 'entity', prototype: 'Table', x: -.5, y: .5, rotation: 90, anchored: true },
    { id: 'new_1', kind: 'entity', prototype: 'Beaker', x: -.37, y: .68, rotation: 17.25, anchored: false },
    { id: 'new_2', kind: 'decal', prototype: 'Hazard', x: -1, y: 0, rotation: 270, z: -3, color: '#11223344', cleanable: true }];
  const previous = structuredClone(draft), doc = document(draft);
  assert.equal(doc.meta.format, 7); assert.equal(doc.meta.category, 'Grid');
  assert.equal(doc.meta.entityCount, 3);
  assert.deepEqual(doc.grids, [1]); assert.deepEqual(doc.orphans, [1]); assert.deepEqual(doc.maps, []);
  assert.equal(tiles(doc).size, 1, 'Deleted floors are not restored from the baseline');
  const table = doc.entities.find(group => group.proto === 'Table').entities[0];
  assert.deepEqual(table.components[0], { type: 'Transform', parent: 1, pos: '-0.5,0.5', rot: 90, anchored: true });
  const beaker = doc.entities.find(group => group.proto === 'Beaker').entities[0];
  assert.equal(doc.gridEditorIds[table.uid], 'e:42', 'Existing native state must be matched by editor ID');
  assert.equal(doc.gridEditorIds[beaker.uid], 'new_1');
  assert.equal(beaker.components[0].rot, 17.25); assert.notEqual(beaker.uid, table.uid);
  const decal = gridComponents(doc).find(c => c.type === 'DecalGrid').chunkCollection;
  assert.equal(decal.version, 2);
  assert.deepEqual(decal.nodes, [{ node: { id: 'Hazard', color: '#11223344', angle: 270, zIndex: -3, cleanable: true }, decals: { 0: '-1,0' } }]);
  assert.deepEqual(draft, previous, 'Saving never modifies the draft or history');
});

test('expanding empty editing space exports only explicitly painted tiles', () => {
  const result = tiles(document([floor(-2, 0, 'FloorSteel')], { left: 100, right: 100, top: 100, bottom: 100 }));
  assert.equal(result.size, 1);
  assert.equal(result.get('-2,0').prototype, 'FloorSteel');
  assert.ok(!result.has('0,0'));
  assert.ok(!result.has('-2,-2'));
});

test('can back up an unfinished plan that exceeds apply limits or lacks support tiles', () => {
  const draft = Array.from({ length: 12500 }, (_, x) => floor(x, 0));
  draft.push({ kind: 'entity', prototype: 'Chair', x: -10.5, y: .5, anchored: true });
  assert.equal(tiles(document(draft)).size, 12500);
  assert.equal(document([]).meta.entityCount, 1);
});

test('file transfer reconstructs exact YAML with JSON messages below the CEF limit', () => {
  const yaml = exportGridDraft([floor(-17, -16), { kind: 'entity', prototype: 'Объект "A"', x: .5, y: .5 }], bounds, noExpansion);
  const chunks = draftFileChunks(yaml);
  assert.ok(chunks.length > 1);
  assert.equal(chunks.join(''), yaml);
  for (const [index, text] of chunks.entries()) {
    const payload = JSON.stringify({ id: 'test', index, text });
    assert.ok(payload.length < 8192);
    assert.equal(JSON.parse(payload).text, text);
  }
});

test('malformed coordinates produce an explicit error instead of a corrupt file', () => {
  assert.throws(() => document([floor(NaN, 0)]), /координат/);
  assert.throws(() => document([floor(.5, 0)]), /целыми/);
});

test('draft preserves autonomous power, forced gravity, safe air and residential protection', () => {
  const options = { alwaysPowered: true, forceGravity: true, safeAtmosphere: true, apartmentProtected: true };
  const result = load(exportGridDraft([floor(0, 0)], bounds, noExpansion, options));
  const components = gridComponents(result);
  assert.deepEqual(components.find(c => c.type === 'GridEnvironment'), { type: 'GridEnvironment', alwaysPowered: true, forceGravity: true, safeAtmosphere: true });
  assert.ok(components.some(c => c.type === 'ApartmentProtectedGrid'));
  const ordinary = gridComponents(document([floor(0, 0)]));
  assert.equal(ordinary.some(c => c.type === 'GridEnvironment'), false);
});
