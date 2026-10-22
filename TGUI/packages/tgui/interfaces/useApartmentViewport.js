import { useEffect, useLayoutEffect, useRef, useState } from 'react';

export const useApartmentViewport = (viewport, width, height, token) => {
  const [view, setView] = useState({ width: 0, height: 0 });
  const [zoom, setZoom] = useState(1);
  const [panning, setPanning] = useState(false);
  const scale = useRef(1), autoFit = useRef(true), pan = useRef(null);
  const center = useRef({ x: width / 2, y: height / 2 });
  const cellSize = 40 * zoom;
  const restoreCenter = () => {
    viewport.current.scrollLeft = center.current.x * 40 * scale.current;
    viewport.current.scrollTop = center.current.y * 40 * scale.current;
  };
  const setScale = (value, fit = false) => {
    const node = viewport.current;
    center.current = fit ? { x: width / 2, y: height / 2 }
      : { x: node.scrollLeft / (40 * scale.current), y: node.scrollTop / (40 * scale.current) };
    autoFit.current = fit;
    const next = Math.max(.01, Math.min(4, value));
    const previous = scale.current;
    scale.current = next;
    setZoom(next);
    if (next === previous) restoreCenter();
  };
  const fit = () => setScale(Math.min((viewport.current.clientWidth - 32) / (width * 40),
    (viewport.current.clientHeight - 32) / (height * 40)), true);

  useEffect(() => {
    const node = viewport.current;
    const measure = () => setView(previous => previous.width === node.clientWidth && previous.height === node.clientHeight
      ? previous : { width: node.clientWidth, height: node.clientHeight });
    const observer = new ResizeObserver(measure);
    observer.observe(node); measure();
    return () => observer.disconnect();
  }, []);
  useLayoutEffect(() => {
    if (!view.width || !view.height) return;
    if (autoFit.current) {
      const next = Math.max(.01, Math.min(4, (view.width - 32) / (width * 40), (view.height - 32) / (height * 40)));
      center.current = { x: width / 2, y: height / 2 };
      if (next !== zoom) { scale.current = next; setZoom(next); return; }
    }
    restoreCenter();
  }, [zoom, view.width, view.height, width, height]);
  useEffect(() => { fit(); }, [token, width, height]);
  useEffect(() => {
    const node = viewport.current;
    const wheel = event => {
      event.preventDefault();
      if (!pan.current) setScale(scale.current * (event.deltaY < 0 ? 1.15 : 1 / 1.15));
    };
    node.addEventListener('wheel', wheel, { passive: false });
    return () => node.removeEventListener('wheel', wheel);
  }, [width, height]);

  const onPointerDownCapture = event => {
    if (event.button !== 1 && event.button !== 2) return;
    event.preventDefault(); event.stopPropagation();
    autoFit.current = false;
    const node = viewport.current;
    pan.current = { x: event.clientX, y: event.clientY, left: node.scrollLeft, top: node.scrollTop };
    node.setPointerCapture(event.pointerId); setPanning(true);
  };
  const onPointerMoveCapture = event => {
    if (!pan.current) return;
    event.preventDefault(); event.stopPropagation();
    const node = viewport.current, gesture = pan.current;
    node.scrollLeft = gesture.left - (event.clientX - gesture.x);
    node.scrollTop = gesture.top - (event.clientY - gesture.y);
    center.current = { x: node.scrollLeft / cellSize, y: node.scrollTop / cellSize };
  };
  const endPan = event => {
    if (!pan.current) return;
    event.preventDefault(); event.stopPropagation(); pan.current = null; setPanning(false);
    if (viewport.current.hasPointerCapture(event.pointerId)) viewport.current.releasePointerCapture(event.pointerId);
  };
  return { view, zoom, cellSize, panning, fit, setScale,
    events: { onPointerDownCapture, onPointerMoveCapture, onPointerUpCapture: endPan, onPointerCancelCapture: endPan,
      onContextMenu: event => event.preventDefault(),
      onScroll: () => {
        const node = viewport.current;
        if (!autoFit.current && node.clientWidth === view.width && node.clientHeight === view.height)
          center.current = { x: node.scrollLeft / cellSize, y: node.scrollTop / cellSize };
      } } };
};
