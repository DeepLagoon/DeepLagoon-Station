import { memo, useEffect, useRef } from 'react';
import { GridSceneCache } from './gridEditorSceneCache.mjs';

// Only the viewport is uploaded to CEF. Pan/zoom composite prepared chunks;
// source sprites are rasterized only on load or when their chunk changes.
export const GridEditorScene = memo(({ index, area, layers, visuals, view, cellSize, onProgress }) => {
  const canvas = useRef(null), cache = useRef(null), frame = useRef(null);
  const ready = useRef(false);
  const camera = useRef(null); camera.current = { area, view, cellSize };
  const schedule = () => {
    if (frame.current !== null) return;
    frame.current = requestAnimationFrame(() => {
      frame.current = null;
      const { area, view, cellSize } = camera.current;
      if (canvas.current) cache.current?.paint(canvas.current, area, view, cellSize);
    });
  };
  useEffect(() => {
    cache.current = new GridSceneCache(schedule);
    return () => {
      cache.current.dispose(); cache.current = null;
      if (frame.current !== null) cancelAnimationFrame(frame.current);
      frame.current = null;
    };
  }, []);
  useEffect(() => {
    let cancelled = false;
    cache.current.prepare(index, layers, visuals, value => {
      if (!value) ready.current = true;
      onProgress(value && { ...value, blocking: !ready.current });
    }, () => cancelled)
      .catch(error => { if (!cancelled) onProgress({ error: error.message, blocking: true }); });
    return () => { cancelled = true; };
  }, [index, layers, visuals, onProgress]);
  useEffect(schedule, [area, view, cellSize]);
  return <canvas ref={canvas} className="GridEditor__scene" width={Math.max(1, view.width)} height={Math.max(1, view.height)}
    style={{ left: view.left, top: view.top, width: view.width, height: view.height }} aria-hidden="true" />;
});

// A large selection must not create tens of thousands of DOM overlays.
export const GridEditorSelection = memo(({ items, area, layers, view, cellSize, offset }) => {
  const canvas = useRef(null);
  useEffect(() => {
    const context = canvas.current.getContext('2d');
    context.clearRect(0, 0, canvas.current.width, canvas.current.height);
    context.fillStyle = '#33cbdf44'; context.strokeStyle = '#54e8ef'; context.lineWidth = Math.min(2, cellSize / 3);
    for (const item of items) {
      if (!layers[item.kind]) continue;
      const origin = item.kind === 'entity' ? .5 : 0;
      const x = (item.x + (offset?.dx || 0) - origin - area.x) * cellSize - view.left;
      const y = (area.y + area.height - 1 - item.y - (offset?.dy || 0) + origin) * cellSize - view.top;
      if (x + cellSize < 0 || y + cellSize < 0 || x > view.width || y > view.height) continue;
      context.fillRect(x, y, cellSize, cellSize);
      const inset = context.lineWidth / 2;
      context.strokeRect(x + inset, y + inset, cellSize - inset * 2, cellSize - inset * 2);
    }
  }, [items, area, layers, view, cellSize, offset]);
  return <canvas ref={canvas} className="GridEditor__scene GridEditor__selection" width={Math.max(1, view.width)} height={Math.max(1, view.height)}
    style={{ left: view.left, top: view.top, width: view.width, height: view.height }} aria-hidden="true" />;
});
