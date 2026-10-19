import { useEffect, useRef } from 'react';
import { useBackend } from '../backend';
import { CivilianConsoleFrame } from './CivilianConsoleFrame';

// Shared matte shell; all labels and controls remain live DOM.
export const ShipConsoleFrame = ({ children, className = '', ...props }) => {
  return <CivilianConsoleFrame className="ShipConsoleHousing">
    <div {...props} className={`ShipConsoleFrame ${className}`} data-skin="matte-graphite">{children}</div>
  </CivilianConsoleFrame>;
};

export const ConsoleKey = ({ children, selected, ...props }) => (
  <button className={selected ? 'is-selected' : ''} aria-pressed={selected} {...props}>{children}</button>
);
export const ConsolePlate = ({ children }) => <h3 className="ShuttleConsole__sticker">{children}</h3>;

export const RadarScope = ({ title, indicator, active, statusLabel, footer, compact = false }) => {
  const viewport = useRef(null);
  const area = useRef(null);
  return <section className="ShuttleConsole__scope">
    {!compact && <div className="ShuttleConsole__scope-title"><ConsolePlate>{title}</ConsolePlate>
      <span className="ShuttleConsole__indicator" role="status" aria-label={statusLabel}>
        <span className={`ShuttleConsole__lamp${active ? ' is-on' : ''}`} aria-hidden="true" />{indicator}</span>
    </div>}
    <RadarViewport viewport={viewport} area={area} compact={compact} />
    {!compact && <footer>{footer}</footer>}
  </section>;
};

// Reports the actual aperture after shell/layout changes, including UI scaling.
// Native radar keeps ownership of input, range, map geometry and weapon aiming.
const RadarViewport = ({ viewport, area, compact }) => {
  const { act } = useBackend();
  const send = useRef(act);
  send.current = act;
  useEffect(() => {
    let frame = 0, last = '';
    const report = () => {
      cancelAnimationFrame(frame);
      frame = requestAnimationFrame(() => {
        if (!area.current || !viewport.current) return;
        const box = area.current.getBoundingClientRect();
        const diameter = Math.max(0, Math.floor(Math.min(box.width, box.height) - 16));
        viewport.current.style.width = `${compact ? diameter : Math.floor(box.width)}px`;
        viewport.current.style.height = `${compact ? diameter : Math.floor(box.height)}px`;
        const rect = viewport.current.getBoundingClientRect();
        const bounds = { x: rect.left / innerWidth, y: rect.top / innerHeight, w: rect.width / innerWidth, h: rect.height / innerHeight };
        const key = JSON.stringify(bounds);
        if (key !== last && rect.width > 0 && rect.height > 0) { last = key; send.current('viewport', bounds); }
      });
    };
    const observer = new ResizeObserver(report);
    observer.observe(area.current);
    window.addEventListener('resize', report);
    report();
    return () => { observer.disconnect(); window.removeEventListener('resize', report); cancelAnimationFrame(frame); };
  }, [compact]);
  return <div className="ShuttleConsole__viewport-area" ref={area}>
    <div className="ShuttleConsole__viewport" ref={viewport} aria-label="Нативный радар" />
  </div>;
};
