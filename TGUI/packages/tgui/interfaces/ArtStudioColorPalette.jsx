import { useEffect, useRef, useState } from 'react';
import { rgbToHsv, hsvToHex } from '../components/PlayerColorPicker';

export const ArtStudioColorPalette = ({ value, onChange, labels }) => {
  const [hsv, setHsv] = useState(() => rgbToHsv(value));
  const [hex, setHex] = useState(value);
  const pointer = useRef(null);
  useEffect(() => { setHex(value); }, [value]);
  const commit = next => { setHsv(next); const color = hsvToHex(next); setHex(color); onChange(color); };
  const pick = (e, hue) => {
    const r = e.currentTarget.getBoundingClientRect();
    const x = Math.max(0, Math.min(1, (e.clientX - r.left) / r.width));
    const y = Math.max(0, Math.min(1, (e.clientY - r.top) / r.height));
    commit(hue ? [(1 - y) * 360 % 360, hsv[1], hsv[2]] : [hsv[0], x, 1 - y]);
  };
  const events = hue => ({
    onPointerDown: e => { if (e.button !== 0 || pointer.current !== null) return; e.preventDefault(); pointer.current = e.pointerId; e.currentTarget.setPointerCapture(e.pointerId); pick(e, hue); },
    onPointerMove: e => { if (pointer.current === e.pointerId && e.buttons & 1) pick(e, hue); },
    onPointerUp: e => { if (pointer.current === e.pointerId) { pointer.current = null; e.currentTarget.releasePointerCapture(e.pointerId); } },
    onPointerCancel: () => { pointer.current = null; },
    onLostPointerCapture: () => { pointer.current = null; },
  });
  return <div className="ArtStudioPalette">
    <div className="ArtStudioPalette__square" role="slider" tabIndex={0} aria-label={labels['saturation-value']} aria-valuetext={`${Math.round(hsv[1] * 100)}%, ${Math.round(hsv[2] * 100)}%`}
      style={{ backgroundColor: `hsl(${hsv[0]}, 100%, 50%)` }} {...events(false)}
      onKeyDown={e => { if (!['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown'].includes(e.key)) return; e.preventDefault(); commit([hsv[0], Math.max(0, Math.min(1, hsv[1] + (e.key === 'ArrowRight' ? .01 : e.key === 'ArrowLeft' ? -.01 : 0))), Math.max(0, Math.min(1, hsv[2] + (e.key === 'ArrowUp' ? .01 : e.key === 'ArrowDown' ? -.01 : 0)))]); }}>
      <span style={{ left: `${hsv[1] * 100}%`, top: `${(1 - hsv[2]) * 100}%` }} />
    </div>
    <div className="ArtStudioPalette__hue" role="slider" tabIndex={0} aria-label={labels.hue} aria-valuemin={0} aria-valuemax={360} aria-valuenow={Math.round(hsv[0])} {...events(true)}
      onKeyDown={e => { if (!['ArrowUp', 'ArrowDown'].includes(e.key)) return; e.preventDefault(); commit([(hsv[0] + (e.key === 'ArrowUp' ? 1 : 359)) % 360, hsv[1], hsv[2]]); }}>
      <span style={{ top: `${(1 - hsv[0] / 360) * 100}%` }} />
    </div>
    <label className="ArtStudioPalette__hex">HEX <input aria-label="HEX" value={hex} maxLength={7} onChange={e => { const next = e.target.value; setHex(next); if (/^#[\da-f]{6}$/i.test(next)) { setHsv(rgbToHsv(next)); onChange(next); } }} onBlur={() => setHex(value)} /></label>
  </div>;
};
