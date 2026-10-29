// Merge at receipt time so consecutive patches for different tabs survive a batched render.
export function gridEditorTabEntries(tabs, order = []) {
  const ranks = new Map(order.map((token, index) => [token, index]));
  return Object.entries(tabs).sort(([a], [b]) => (ranks.get(a) ?? order.length) - (ranks.get(b) ?? order.length));
}

export function gridEditorUpdate(data, payload) {
  if (payload.gridEditorClosed) {
    const editorTabs = { ...data.editorTabs };
    delete editorTabs[payload.gridEditorClosed];
    const editorSnapshots = { ...data.editorSnapshots };
    delete editorSnapshots[payload.gridEditorClosed];
    return { editorTabs, editorSnapshots, closedEditorTabs: { ...data.closedEditorTabs, [payload.gridEditorClosed]: true } };
  }
  if (payload.gridEditorSnapshot) {
    const token = payload.gridEditorToken, message = payload.gridEditorSnapshot;
    if (data.closedEditorTabs?.[token]) return {};
    const editorSnapshots = { ...data.editorSnapshots }, previous = editorSnapshots[token];
    if (message.phase === 'start') editorSnapshots[token] = { id: message.id, data: payload.data, chunks: [], draftChunks: [] };
    else if (!previous || previous.id !== message.id) return {};
    else if (message.phase === 'chunk') {
      if (message.index !== previous.chunks.length) return {};
      editorSnapshots[token] = { ...previous, chunks: [...previous.chunks, payload.data.items] };
    } else if (message.phase === 'draft') {
      if (message.index !== previous.draftChunks.length) return {};
      editorSnapshots[token] = { ...previous, draftChunks: [...previous.draftChunks, payload.data.text] };
    } else if (message.phase === 'complete') {
      if (previous.chunks.length !== message.chunks || previous.draftChunks.length !== (message.draftChunks || 0)) return {};
      let recovery;
      try { recovery = previous.draftChunks.length ? JSON.parse(previous.draftChunks.join('')) : { edits: [], expansion: {} }; }
      catch { return {}; }
      delete editorSnapshots[token];
      return { editorSnapshots, editorTabs: { ...data.editorTabs,
        [token]: { ...data.editorTabs?.[token], ...previous.data, items: previous.chunks.flat(), snapshotId: message.id, draftRecovery: recovery } } };
    }
    return { editorSnapshots };
  }
  if (!payload.gridEditorToken) return payload.data;
  const token = payload.gridEditorToken;
  const previous = data.editorTabs?.[token];
  // A late acknowledgement must never reopen a closed tab.
  if (!previous && !payload.data?.items) return {};
  return { editorTabs: { ...data.editorTabs, [token]: { ...previous, ...payload.data } } };
}
