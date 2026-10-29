import test from 'node:test';
import assert from 'node:assert/strict';
import { gridEditorUpdate } from '../packages/tgui/interfaces/gridEditorTabs.mjs';

test('interleaved tab patches retain each baseline and acknowledgements independently', () => {
  let data = {};
  const receive = payload => { data = { ...data, ...gridEditorUpdate(data, payload) }; };
  receive({ gridEditorToken: 'A', data: { token: 'A', items: [{ id: 'a' }], patchVersion: 1 } });
  receive({ gridEditorToken: 'B', data: { token: 'B', items: [{ id: 'b' }] } });
  receive({ gridEditorToken: 'A', data: { uploadId: 'upload-a', uploadNext: 2 } });
  receive({ gridEditorToken: 'B', data: { patchVersion: 4 } });
  assert.equal(data.editorTabs.A.items[0].id, 'a');
  assert.equal(data.editorTabs.A.uploadNext, 2);
  assert.equal(data.editorTabs.B.items[0].id, 'b');
  assert.equal(data.editorTabs.B.patchVersion, 4);
  receive({ gridEditorClosed: 'A' });
  receive({ gridEditorToken: 'A', data: { previewFinished: 'late-ack' } });
  assert.deepEqual(Object.keys(data.editorTabs), ['B']);
});

test('baseline and recovery are published atomically, with bounded ordered draft chunks', () => {
  let data = { editorTabs: { A: { token: 'A', items: [{ id: 'old' }], previewFinished: 'ack' } } };
  const receive = (message, fields) => { data = { ...data, ...gridEditorUpdate(data, { gridEditorToken: 'A', gridEditorSnapshot: message, data: fields }) }; };
  const recovery = JSON.stringify({ edits: [{ id: 'new', entityData: 'имя 🐟'.repeat(12000) }], expansion: { left: 2 } });
  receive({ id: 'reload', phase: 'start' }, { token: 'A', snapshotPatchVersion: 7 });
  receive({ id: 'reload', phase: 'chunk', index: 0 }, { items: [{ id: 'world' }] });
  receive({ id: 'reload', phase: 'draft', index: 1 }, { text: 'out of order' });
  const chunks = Math.ceil(recovery.length / 32768);
  for (let index = 0; index < chunks; index++) receive({ id: 'reload', phase: 'draft', index }, { text: recovery.slice(index * 32768, (index + 1) * 32768) });
  receive({ id: 'reload', phase: 'complete', chunks: 1, draftChunks: chunks + 1 });
  assert.equal(data.editorTabs.A.items[0].id, 'old');
  receive({ id: 'reload', phase: 'complete', chunks: 1, draftChunks: chunks });
  assert.equal(data.editorTabs.A.items[0].id, 'world');
  assert.deepEqual(data.editorTabs.A.draftRecovery, JSON.parse(recovery));
  assert.equal(data.editorTabs.A.previewFinished, 'ack');
  assert.equal(data.editorTabs.A.snapshotId, 'reload');
  assert.equal(data.editorTabs.A.snapshotPatchVersion, 7);
});

test('a stale snapshot, invalid recovery or closed tab cannot replace the current document', () => {
  let data = {};
  const receive = payload => { data = { ...data, ...gridEditorUpdate(data, payload) }; };
  const send = (id, phase, extra = {}, fields = {}) => receive({ gridEditorToken: 'A', gridEditorSnapshot: { id, phase, ...extra }, data: fields });
  send('old', 'start', {}, { token: 'A' });
  send('new', 'start', {}, { token: 'A' });
  send('old', 'complete', { chunks: 0 });
  assert.equal(data.editorTabs, undefined);
  send('new', 'draft', { index: 0 }, { text: '{invalid' });
  send('new', 'complete', { chunks: 0, draftChunks: 1 });
  assert.equal(data.editorTabs, undefined);
  receive({ gridEditorClosed: 'A' });
  send('late', 'start', {}, { token: 'A' });
  send('late', 'complete', { chunks: 0 });
  assert.deepEqual(data.editorTabs, {});
});
