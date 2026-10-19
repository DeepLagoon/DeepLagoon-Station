import { memo, useEffect, useMemo, useRef } from 'react';

// A separate viewport overlay leaves the map's prepared source chunks intact.
export const GridEditorAtmos = memo(({ cells, area, view, cellSize }) => {
  const canvas = useRef(null);
  const index = useMemo(() => new Map(cells.map(cell => [`${cell.x}:${cell.y}`, cell])), [cells]);
  useEffect(() => {
    const context = canvas.current.getContext('2d');
    context.clearRect(0, 0, view.width, view.height);
    const left = Math.floor(area.x + view.left / cellSize), right = Math.ceil(area.x + (view.left + view.width) / cellSize);
    const top = Math.ceil(area.y + area.height - view.top / cellSize), bottom = Math.floor(area.y + area.height - (view.top + view.height) / cellSize);
    const colors = { sealed: '#35e88855', leaking: '#ff455566', blocked: '#9bacbd33', space: '#ffae2455' };
    const draw = cell => {
      const { x, y } = cell;
      if (x < left || x > right || y < bottom || y > top) return;
      const px = (x - area.x) * cellSize - view.left, py = (area.y + area.height - y - 1) * cellSize - view.top;
      context.fillStyle = colors[cell.status]; context.fillRect(px, py, cellSize, cellSize);
      if (!cell.leaks) return;
      context.strokeStyle = '#fff06a'; context.lineWidth = Math.max(2, Math.min(4, cellSize / 8));
      context.beginPath();
      for (const [bit, x1, y1, x2, y2] of [[1,0,0,1,0],[2,0,1,1,1],[4,1,0,1,1],[8,0,0,0,1]])
        if (cell.leaks & bit) { context.moveTo(px+x1*cellSize,py+y1*cellSize); context.lineTo(px+x2*cellSize,py+y2*cellSize); }
      context.stroke();
    };
    // At extreme zoom or with a huge empty extension, iterate occupied cells rather than empty screen coordinates.
    if ((right - left + 1) * (top - bottom + 1) > index.size) {
      for (const cell of index.values()) draw(cell);
    } else for (let x = left; x <= right; x++) for (let y = bottom; y <= top; y++) {
      const cell = index.get(`${x}:${y}`); if (cell) draw(cell);
    }
  }, [index, area, view, cellSize]);
  return <canvas ref={canvas} className="GridEditor__scene GridEditor__atmos" width={Math.max(1, view.width)} height={Math.max(1, view.height)}
    style={{ left: view.left, top: view.top, width: view.width, height: view.height }} aria-hidden="true" />;
});
