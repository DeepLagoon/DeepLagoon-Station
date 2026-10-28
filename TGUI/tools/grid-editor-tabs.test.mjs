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
