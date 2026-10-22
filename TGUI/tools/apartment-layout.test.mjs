import test from 'node:test';
import assert from 'node:assert/strict';
import { copyLayout, footprint, validateLayout } from '../packages/tgui/interfaces/apartmentLayout.mjs';

const room = { width: 8, height: 8, maxFurniture: 12, arrivalX: 1, arrivalY: 1,
  catalog: [{ id: 'table', width: 2, height: 1, maxCount: 10, blocksMovement: true }, { id: 'chair', width: 1, height: 1, maxCount: 2, blocksMovement: false }] };
const item = (id, furniture, x, y, rotation = 0) => ({ id, furniture, x, y, rotation });

test('draft can move repeatedly without changing the committed layout', () => {
  const saved = [item('base_chair', 'chair', 4, 4)];
  const draft = copyLayout(saved);
  for (let x = 2; x <= 5; x++) draft[0].x = x;
  assert.equal(saved[0].x, 4);
  assert.equal(draft[0].x, 5);
  assert.deepEqual(validateLayout(draft, room), {});
});
test('rotation swaps rectangular footprint', () => {
  assert.deepEqual(footprint(item('a', 'table', 2, 2, 1), room.catalog[0]), [[2, 2], [2, 3]]);
});
test('reserved exit, walls, collisions and stock give actionable errors', () => {
  assert.match(validateLayout([item('a', 'chair', 1, 1)], room).a, /прибытия/);
  assert.match(validateLayout([item('a', 'table', 6, 4)], room).a, /стеной/);
  const overlap = validateLayout([item('a', 'chair', 3, 3), item('b', 'chair', 3, 3)], room);
  assert.match(overlap.a, /пересекаются/); assert.match(overlap.b, /пересекаются/);
  assert.match(validateLayout([item('a', 'chair', 2, 2), item('b', 'chair', 3, 2), item('c', 'chair', 4, 2)], room).c, /экземпляров/);
});
test('preview detects a barrier cutting the room off from exit', () => {
  const wall = Array.from({ length: 6 }, (_, i) => item(`a${i}`, 'table', 3, i + 1));
  assert.match(validateLayout(wall, room).general, /проход/);
});

test('elevator and corridor cells remain unavailable for furniture', () => {
  const reservedRoom = { ...room, reservedCells: [{ x: 4, y: 4 }] };
  assert.match(validateLayout([item('a', 'chair', 4, 4)], reservedRoom).a, /лифта и коридор/);
});

test('map scene uses actual floor and fixtures instead of an invented rectangular room', () => {
  const mapped = { ...room, scene: { x: -1, y: 0,
    tiles: [{ x: 1, y: 1 }, { x: 2, y: 1 }, { x: 3, y: 1 }, { x: 3, y: 2 }, { x: 0, y: 1 }],
    occupied: [{ x: 3, y: 2 }], blocked: [{ x: 3, y: 2 }],
  } };
  assert.match(validateLayout([item('a', 'chair', 3, 3)], mapped).a, /стеной/);
  assert.match(validateLayout([item('a', 'chair', 3, 2)], mapped).a, /стационарным/);
  assert.deepEqual(validateLayout([item('a', 'chair', 0, 1)], mapped), {});
  assert.deepEqual(validateLayout([item('a', 'chair', 2, 1)], mapped), {});
});

test('map scene allows existing disconnected rooms but forbids new barriers in the accessible area', () => {
  const mapped = { ...room, catalog: [...room.catalog, { id: 'box', width: 1, height: 1, maxCount: 3, blocksMovement: true }], scene: {
    tiles: [{ x: 1, y: 1 }, { x: 2, y: 1 }, { x: 3, y: 1 }, { x: 6, y: 6 }], occupied: [], blocked: [],
  } };
  assert.deepEqual(validateLayout([], mapped), {});
  assert.match(validateLayout([item('a', 'box', 2, 1)], mapped).general, /проход/);
});

test('mapped originals can move without consuming stock but cannot be removed or cloned', () => {
  const mapped = { ...room, maxFurniture: 0, catalog: [...room.catalog,
    { id: 'map_73', placementId: 'map_73', width: 1, height: 1, maxCount: 1, existingOnly: true, blocksMovement: false, originalX: 4, originalY: 4, originalRotation: 0 }] };
  const original = item('map_73', 'map_73', 3, 4, 1);
  assert.deepEqual(validateLayout([original], mapped), {});
  assert.match(validateLayout([], mapped).general, /удалять/);
  assert.match(validateLayout([original, { ...original, id: 'clone', x: 5 }], mapped).clone, /экземпляров|копировать/);
});

test('a mapped countertop appliance can share a table cell', () => {
  const mapped = { ...room, catalog: [...room.catalog,
    { id: 'microwave', placementId: 'map_79', width: 1, height: 1, maxCount: 1, existingOnly: true, allowsOverlap: true, blocksMovement: false, originalX: 3, originalY: 4, originalRotation: 0 }] };
  assert.deepEqual(validateLayout([item('table', 'table', 3, 4), item('map_79', 'microwave', 3, 4)], mapped), {});
});

test('fixed-orientation mapped appliances cannot be rotated', () => {
  const mapped = { ...room, catalog: [{ id: 'fridge', placementId: 'map_77', width: 1, height: 1, maxCount: 1, existingOnly: true, canRotate: false, blocksMovement: false, originalX: 4, originalY: 4, originalRotation: 0 }] };
  assert.deepEqual(validateLayout([item('map_77', 'fridge', 3, 4)], mapped), {});
  assert.match(validateLayout([item('map_77', 'fridge', 3, 4, 1)], mapped).map_77, /нельзя поворачивать/);
});
