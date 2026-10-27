import { memo, useEffect, useRef } from 'react';

export const GridEditorIssues = memo(({ issues, area, view, cellSize }) => {
  const canvas = useRef(null);
  useEffect(() => {
    const context = canvas.current.getContext('2d');
    context.clearRect(0, 0, view.width, view.height);
    context.lineWidth = 2;
    for (const issue of issues) {
      if (!issue.located || !Number.isFinite(issue.x) || !Number.isFinite(issue.y)) continue;
      const x = (Math.floor(issue.x) - area.x) * cellSize - view.left;
      const y = (area.y + area.height - Math.floor(issue.y) - 1) * cellSize - view.top;
      if (x + cellSize < 0 || y + cellSize < 0 || x > view.width || y > view.height) continue;
      context.fillStyle = '#ff334466'; context.fillRect(x, y, cellSize, cellSize);
      context.strokeStyle = '#ff5968'; context.strokeRect(x + 1, y + 1, Math.max(0, cellSize - 2), Math.max(0, cellSize - 2));
      if (cellSize >= 12) {
        context.fillStyle = '#fff'; context.font = `bold ${Math.min(20, cellSize)}px sans-serif`;
        context.textAlign = 'center'; context.textBaseline = 'middle'; context.fillText('!', x + cellSize / 2, y + cellSize / 2);
      }
    }
  }, [issues, area, view, cellSize]);
  return <canvas ref={canvas} className="GridEditor__scene GridEditor__issues" width={Math.max(1, view.width)} height={Math.max(1, view.height)}
    style={{ left: view.left, top: view.top, width: view.width, height: view.height }} aria-hidden="true" />;
});
