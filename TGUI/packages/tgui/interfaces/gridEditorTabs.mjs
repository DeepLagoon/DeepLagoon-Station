// Merge at receipt time so consecutive patches for different tabs survive a batched render.
export function gridEditorUpdate(data, payload) {
  if (payload.gridEditorClosed) {
    const editorTabs = { ...data.editorTabs };
    delete editorTabs[payload.gridEditorClosed];
    return { editorTabs };
  }
  if (!payload.gridEditorToken) return payload.data;
  const token = payload.gridEditorToken;
  const previous = data.editorTabs?.[token];
  // A late acknowledgement must never reopen a closed tab.
  if (!previous && !payload.data?.items) return {};
  return { editorTabs: { ...data.editorTabs, [token]: { ...previous, ...payload.data } } };
}
