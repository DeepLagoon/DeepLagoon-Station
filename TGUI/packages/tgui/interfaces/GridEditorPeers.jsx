import { memo, useEffect, useMemo, useRef, useState } from 'react';
import { GridEditorScene } from './GridEditorScene';
import { indexItems, peerPreview, previewPatch } from './gridEditorModel.mjs';

const ignoreProgress = () => {};
const previewLayers = { tile: true, entity: true, decal: true };
const noPeers = [];

export function useGridEditorPeers({ data, act, edits, visuals, expansion }) {
  const sentExpansion = useRef(null);
  const transfer = useRef(null), sent = useRef([]), serial = useRef(0), actRef = useRef(act);
  const [revision, setRevision] = useState(0);
  actRef.current = act;
  const generation = data.previewGeneration || 0;
  useEffect(() => { transfer.current = null; sent.current = []; sentExpansion.current = null; }, [data.token, generation]);
  useEffect(() => {
    if (transfer.current) return;
    const changes = previewPatch(sent.current, edits);
    if (!changes.length && JSON.stringify(sentExpansion.current) === JSON.stringify(expansion)) return;
    if (!changes.length && !sentExpansion.current && !Object.values(expansion).some(Boolean)) return;
    const timer = setTimeout(() => {
      const id = `p_${Date.now()}_${serial.current++}`, chunks = [];
      for (let i = 0; i < changes.length; i += 16) chunks.push(changes.slice(i, i + 16));
      if (!chunks.length) chunks.push([]);
      transfer.current = { id, chunks, target: edits, expansion };
      actRef.current('previewStart', { id, count: changes.length, chunks: chunks.length, generation, expansion });
    }, 100);
    return () => clearTimeout(timer);
  }, [edits, data.token, generation, revision, expansion]);
  useEffect(() => {
    const pending = transfer.current;
    if (pending && data.previewId === pending.id && data.previewNext < pending.chunks.length)
      actRef.current('previewChunk', { id: pending.id, index: data.previewNext, items: pending.chunks[data.previewNext] });
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
