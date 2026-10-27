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
  const geometry = useRef({ height: 480, columns: 3, stride: 179 });
  const [view, setView] = useState({ first: 0, end: 4, columns: 3, stride: 179 });
  const currentView = useRef(view);
  const updateRange = useCallback(() => {
    if (!shelf.current) return;
    const { height, columns, stride } = geometry.current;
    const top = shelf.current.scrollTop;
    const rows = Math.ceil(items.length / columns);
    const next = {
      first: Math.min(Math.max(0, rows - 1), Math.max(0, Math.floor(top / stride) - OVERSCAN_ROWS)),
      end: Math.min(rows, Math.ceil((top + height) / stride) + OVERSCAN_ROWS),
      columns, stride,
    };
    const previous = currentView.current;
    if (previous.first === next.first && previous.end === next.end &&
      previous.columns === next.columns && previous.stride === next.stride) return;
    currentView.current = next;
    setView(next);
  }, [items.length]);
  // CSS and viewport dimensions only change on resize, never because of scrolling.
  const measure = useCallback(() => {
    if (!shelf.current || !grid.current) return;
    const style = getComputedStyle(grid.current);
    geometry.current = {
      height: shelf.current.clientHeight,
      columns: style.gridTemplateColumns.split(' ').length,
      stride: parseFloat(style.getPropertyValue('--vm-product-height')) + parseFloat(style.rowGap),
    };
    updateRange();
  }, [updateRange]);
  const schedule = useCallback(() => {
    if (!frame.current) frame.current = requestAnimationFrame(() => { frame.current = 0; updateRange(); });
  }, [updateRange]);
  useLayoutEffect(() => {
    measure();
    const observer = new ResizeObserver(measure);
    observer.observe(shelf.current!);
    observer.observe(grid.current!);
    return () => { observer.disconnect(); cancelAnimationFrame(frame.current); frame.current = 0; };
  }, [measure]);
  useLayoutEffect(() => {
    shelf.current!.scrollTop = 0;
    measure();
  }, [resetKey, measure]);

  const rows = Math.ceil(items.length / view.columns);
  const first = Math.min(Math.max(0, rows - 1), view.first);
  const end = Math.min(rows, view.end);
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
