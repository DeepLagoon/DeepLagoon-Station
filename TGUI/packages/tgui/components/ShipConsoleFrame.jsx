import { useEffect, useRef } from 'react';
import { useBackend } from '../backend';
import { ConsoleKeyboard } from './ConsoleKeyboard';

// The shell is a separately cached local resource, shared by all three consoles.
// Text and controls stay live DOM; the artwork contains no functional labels.
export const ShipConsoleFrame = ({ children, className = '', ...props }) => {
  const frame = useRef(null);
  return <div {...props} ref={frame} className={`ShipConsoleFrame ${className}`} data-skin="dark-steel"
    style={{ borderImageSource: 'url("ship-console-shell.png")' }}>{children}
    <ConsoleKeyboard kind="ship" frame={frame} /></div>;
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
const HardwareSprite = ({ region, className }) => <svg className={className} viewBox={region} aria-hidden="true">
  <image href="ship-console-instruments.png" width="1536" height="1024" />
</svg>;

const RadarViewport = ({ viewport, area, compact }) => {
  const { act } = useBackend();
  const send = useRef(act);
  send.current = act;
  useEffect(() => {
    let frame = 0, last = '';
    const report = () => {
      cancelAnimationFrame(frame);
      frame = requestAnimationFrame(() => {
        const box = area.current.getBoundingClientRect();
        const diameter = Math.max(0, Math.floor(Math.min(box.width, box.height) - 16));
        viewport.current.style.width = `${diameter}px`;
        viewport.current.style.height = `${diameter}px`;
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
  }, []);
  return <div className="ShuttleConsole__viewport-area" ref={area}>
    <div className="ShuttleConsole__viewport" ref={viewport} aria-label="Нативный радар">
      {!compact && <div className="ShipConsoleFrame__hardware" aria-hidden="true">
        <span className="ShipConsoleFrame__plaque">DL / RADAR</span>
        <HardwareSprite className="ShipConsoleFrame__ribbon" region="140 730 164 123" />
        <HardwareSprite className="ShipConsoleFrame__harness" region="809 750 86 114" />
      </div>}
    </div>
  </div>;
};
