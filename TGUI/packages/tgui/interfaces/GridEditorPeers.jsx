import { memo, useEffect, useMemo, useRef, useState } from 'react';
import { GridEditorScene } from './GridEditorScene';
import { indexItems, peerPreview, previewPatch } from './gridEditorModel.mjs';
import { gridEditorTransfer } from './gridEditorTransfer.mjs';

const ignoreProgress = () => {};
const previewLayers = { tile: true, entity: true, decal: true };
const noPeers = [];

export function useGridEditorPeers({ data, act, edits, visuals, expansion }) {
  const sentExpansion = useRef(null);
  const transfer = useRef(null), sent = useRef([]), serial = useRef(0), actRef = useRef(act);
  const [revision, setRevision] = useState(0);
  actRef.current = act;
  const generation = data.previewGeneration || 0;
  const latest = useRef(null); latest.current = { edits, expansion, generation };
  const timer = useRef(null);
  const restored = useRef(null);
  useEffect(() => {
    const snapshot = `${data.token}:${data.snapshotId || ''}`;
    const recovery = restored.current !== snapshot ? data.draftRecovery : null;
    restored.current = snapshot;
    transfer.current = null; sent.current = recovery?.edits || []; sentExpansion.current = recovery?.expansion || null;
  }, [data.token, generation, data.snapshotId]);
  useEffect(() => () => { clearTimeout(timer.current); timer.current = null; }, [data.token, generation, data.snapshotId]);
  useEffect(() => {
    if (transfer.current || timer.current !== null) return;
    const changes = previewPatch(sent.current, edits);
    if (!changes.length && JSON.stringify(sentExpansion.current) === JSON.stringify(expansion)) return;
    if (!changes.length && !sentExpansion.current && !Object.values(expansion).some(Boolean)) return;
    timer.current = setTimeout(() => {
      timer.current = null;
      const { edits, expansion, generation } = latest.current;
      const changes = previewPatch(sent.current, edits);
      let encoded;
      try { encoded = gridEditorTransfer(changes); } catch { return; }
      const id = `p_${Date.now()}_${serial.current++}`, {encoding, chunks} = encoded;
      transfer.current = { id, chunks, encoding, target: edits, expansion };
      actRef.current('previewStart', { id, count: changes.length, chunks: chunks.length, encoding, generation, expansion });
    }, 100);
  }, [edits, data.token, generation, data.snapshotId, revision, expansion]);
  useEffect(() => {
    const pending = transfer.current;
    if (pending && data.previewId === pending.id && data.previewNext < pending.chunks.length)
      actRef.current('previewChunk', { id: pending.id, index: data.previewNext, [pending.encoding ? 'text' : 'items']: pending.chunks[data.previewNext] });
  }, [data.previewId, data.previewNext]);
  useEffect(() => {
    const pending = transfer.current;
    if (!pending || pending.id !== data.previewFinished) return;
    sent.current = pending.target; sentExpansion.current = pending.expansion; transfer.current = null; setRevision(value => value + 1);
  }, [data.previewFinished]);
  const peers = data.peerDrafts || noPeers;
  const foreign = useMemo(() => peerPreview(peers, edits, id => !!visuals.get(`entity:${id}`)?.exclusive || visuals.get(`entity:${id}`)?.category === 'wall'), [peers, edits, visuals]);
  return { peers, ...foreign };
}

export const GridEditorPeers = memo(({ items, removed, area, view, cellSize, visuals }) => {
  const index = useMemo(() => indexItems(items), [items]);
  const canvas = useRef(null);
  useEffect(() => {
    const context = canvas.current.getContext('2d');
    context.clearRect(0, 0, view.width, view.height);
    context.strokeStyle = '#ff6677'; context.lineWidth = Math.max(1, Math.min(3, cellSize / 8));
    for (const item of removed) {
      const offset = item.kind === 'entity' ? .5 : 0;
      const x = (item.x - offset - area.x) * cellSize - view.left;
      const y = (area.y + area.height - item.y + offset - 1) * cellSize - view.top;
      if (x + cellSize < 0 || y + cellSize < 0 || x > view.width || y > view.height) continue;
      context.strokeRect(x, y, cellSize, cellSize); context.beginPath();
      context.moveTo(x, y); context.lineTo(x + cellSize, y + cellSize);
      context.moveTo(x + cellSize, y); context.lineTo(x, y + cellSize); context.stroke();
    }
  }, [removed, area, view, cellSize]);
  return <div className="GridEditor__peerLayer">
    {!!items.length && <GridEditorScene index={index} area={area} layers={previewLayers}
      visuals={visuals} view={view} cellSize={cellSize} onProgress={ignoreProgress} />}
    <canvas ref={canvas} className="GridEditor__scene" width={Math.max(1, view.width)} height={Math.max(1, view.height)}
      style={{ left: view.left, top: view.top, width: view.width, height: view.height }} aria-hidden="true" />
  </div>;
});
