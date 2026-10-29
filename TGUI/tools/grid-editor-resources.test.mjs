import test from 'node:test';
import assert from 'node:assert/strict';
import { GridEditorVisuals } from '../packages/tgui/interfaces/gridEditorVisuals.mjs';
import { GridEditorScenePool, SCENE_IDLE_MS } from '../packages/tgui/interfaces/gridEditorScenePool.mjs';
import { gridEditorUpdate, gridEditorTabEntries } from '../packages/tgui/interfaces/gridEditorTabs.mjs';
import { indexItems, updateItemIndex, collaborativeDraft } from '../packages/tgui/interfaces/gridEditorModel.mjs';

test('prioritizing the active snapshot preserves the original tab opening order', () => {
  assert.deepEqual(gridEditorTabEntries({ C: {}, A: {}, B: {} }, ['A', 'B', 'C']).map(([token]) => token), ['A', 'B', 'C']);
  assert.deepEqual(gridEditorTabEntries({ C: {}, B: {} }, ['A', 'B', 'C']).map(([token]) => token), ['B', 'C']);
});

test('single-item patches preserve untouched spatial buckets and tie-breaking order', () => {
  const items = [{ id: 'a', x: 0, y: 0 }, { id: 'b', x: 1, y: 0 }, { id: 'c', x: 0, y: 0 }];
  const before = indexItems(items), changed = [{ ...items[0], x: 2 }, items[1], items[2]];
  const after = updateItemIndex(before, changed);
  assert.equal(after.get('1:0'), before.get('1:0'));
  assert.deepEqual(after.get('0:0').map(item => item.id), ['c']);
  assert.equal(after.get('2:0')[0], changed[0]); assert.equal(before.get('0:0')[0], items[0]);
  assert.equal(after.order, before.order);
  assert.deepEqual([...updateItemIndex(after, [...changed].reverse()).order.keys()], ['c', 'b', 'a']);
  assert.equal(collaborativeDraft(items, [], []), items, 'An unchanged baseline must not be copied');
});

test('unrelated updates and duplicate sprite replies preserve the shared visual Map', () => {
  const registry = new GridEditorVisuals(), entries = [{ kind: 'tile', id: 'Plating', images: [{ url: 'sprite' }] }];
  const first = registry.update({ A: { visuals: entries } });
  assert.equal(registry.update({ A: { visuals: entries, status: 'ack' } }), first);
  assert.equal(registry.update({ A: { visuals: entries }, B: { visuals: structuredClone(entries) } }), first);
  const changed = registry.update({ A: { visuals: [{ ...entries[0], images: [{ url: 'changed' }] }] } });
  assert.notEqual(changed, first); assert.equal(changed.get('tile:Plating').images[0].url, 'changed');
  assert.equal(registry.update({}), changed, 'Clipboard definitions survive closing their source');
});

const fixture = (budget = 100) => {
  let now = 0, callback;
  const pool = new GridEditorScenePool({ budget, now: () => now,
    schedule: action => { callback = action; return 1; }, cancel: () => { callback = null; } });
  const cache = () => ({ active: false, surfaceBytes: 10, drops: 0,
    setActive(value) { this.active = value; }, releaseSurfaces() { this.surfaceBytes = 0; this.drops++; },
    evictDetail() { return false; }, dispose() { this.releaseSurfaces(); } });
  return { pool, cache, advance: time => { now = time; callback?.(); } };
};

test('many tabs retain one active and only one warm background scene', () => {
  const { pool, cache } = fixture(), caches = [];
  for (let i = 0; i < 8; i++) { pool.setActive(i); const item = cache(); pool.add(i, item); caches.push(item); }
  assert.equal(caches.filter(item => item.surfaceBytes).length, 2);
  assert.equal(caches.filter(item => item.active).length, 1);
  assert.equal(pool.warm.size, 1); pool.dispose();
});

test('background lease expires without extending on repeated activation notifications', () => {
  const { pool, cache, advance } = fixture(), first = cache();
  pool.setActive('A'); pool.add('A', first); pool.setActive('B');
  advance(SCENE_IDLE_MS - 1); pool.setActive('B'); assert.equal(first.drops, 0);
  advance(SCENE_IDLE_MS); assert.equal(first.drops, 1); assert.equal(pool.warm.size, 0); pool.dispose();
});

test('global surface budget evicts a warm scene before active resources', () => {
  const { pool, cache } = fixture(15), first = cache(), second = cache();
  pool.setActive('A'); pool.add('A', first); pool.setActive('B'); pool.add('B', second); pool.trim();
  assert.equal(first.surfaceBytes, 0); assert.equal(second.surfaceBytes, 10); assert.ok(pool.surfaceBytes <= 15); pool.dispose();
});

test('snapshot chunks publish atomically and reject old, duplicate and closed-tab packets', () => {
  let data = {};
  const receive = (phase, rest = {}, fields = {}) => {
    const payload = { gridEditorToken: 'A', gridEditorSnapshot: { id: 'new', phase, ...rest }, data: fields };
    data = { ...data, ...gridEditorUpdate(data, payload) };
  };
  receive('start', {}, { token: 'A', width: 250 });
  receive('chunk', { id: 'old', index: 0 }, { items: [{ id: 'old' }] });
  receive('chunk', { index: 0 }, { items: [{ id: 'one' }] });
  receive('chunk', { index: 0 }, { items: [{ id: 'duplicate' }] });
  assert.equal(data.editorTabs, undefined);
  receive('complete', { chunks: 2 }); assert.equal(data.editorTabs, undefined);
  receive('chunk', { index: 1 }, { items: [{ id: 'two' }] });
  receive('complete', { chunks: 2 }); assert.deepEqual(data.editorTabs.A.items.map(item => item.id), ['one', 'two']);
  data = { ...data, ...gridEditorUpdate(data, { gridEditorClosed: 'A' }) };
  receive('start', {}, { token: 'A' }); receive('chunk', { index: 0 }, { items: [] }); receive('complete', { chunks: 1 });
  assert.equal(data.editorTabs.A, undefined); assert.equal(data.editorSnapshots.A, undefined);
});
