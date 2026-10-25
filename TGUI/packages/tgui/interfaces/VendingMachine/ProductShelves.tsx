import { type ReactNode, useCallback, useLayoutEffect, useRef, useState } from 'react';

type Product = { key: string };
const OVERSCAN_ROWS = 1;

/** Keep the full scroll range, but mount only the shelves around the viewport. */
export function ProductShelves<T extends Product>({ items, resetKey, renderItem, empty }: {
  items: T[];
  resetKey: string;
  renderItem: (item: T) => ReactNode;
  empty: ReactNode;
}) {
  const shelf = useRef<HTMLDivElement>(null);
  const grid = useRef<HTMLDivElement>(null);
  const frame = useRef(0);
  const [view, setView] = useState({ top: 0, height: 480, columns: 3, stride: 179 });
  const measure = useCallback(() => {
    frame.current = 0;
    if (!shelf.current || !grid.current) return;
    const style = getComputedStyle(grid.current);
    const next = {
      top: shelf.current.scrollTop,
      height: shelf.current.clientHeight,
      columns: style.gridTemplateColumns.split(' ').length,
      stride: parseFloat(style.getPropertyValue('--vm-product-height')) + parseFloat(style.rowGap),
    };
    setView(previous => previous.top === next.top && previous.height === next.height &&
      previous.columns === next.columns && previous.stride === next.stride ? previous : next);
  }, []);
  const schedule = useCallback(() => {
    if (!frame.current) frame.current = requestAnimationFrame(measure);
  }, [measure]);
  useLayoutEffect(() => {
    measure();
    const observer = new ResizeObserver(schedule);
    observer.observe(shelf.current!);
    observer.observe(grid.current!);
    return () => { observer.disconnect(); cancelAnimationFrame(frame.current); frame.current = 0; };
  }, [measure, schedule]);
  useLayoutEffect(() => {
    shelf.current!.scrollTop = 0;
    measure();
  }, [resetKey, measure]);

  const rows = Math.ceil(items.length / view.columns);
  const first = Math.min(Math.max(0, rows - 1), Math.max(0, Math.floor(view.top / view.stride) - OVERSCAN_ROWS));
  const end = Math.min(rows, Math.ceil((view.top + view.height) / view.stride) + OVERSCAN_ROWS);
  return <div className="VendingMachine__shelves" ref={shelf} onScroll={schedule}>
    <div className="VendingMachine__grid" ref={grid} style={{
      paddingTop: first * view.stride,
      paddingBottom: Math.max(0, rows - end) * view.stride,
    }}>
      {items.slice(first * view.columns, end * view.columns).map(renderItem)}
      {!items.length && empty}
    </div>
  </div>;
}
