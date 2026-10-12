import { createContext, useCallback, useContext, useEffect, useRef, useState } from 'react';
import './PlayerColorPicker.scss';

export const ColorTransferContext = createContext(null);

/**
 * DeepLagoon: a drag (color wheel, sliders) sends at most one change per `interval` ms, the first one
 * at once, and `flush` sends the last value right away (on release). The host rebuilds and republishes
 * the editor state for every change it gets. A pending value is also sent when the control goes away.
 */
export const useThrottledChange = (callback, interval = 50) => {
  const latest = useRef(callback);
  latest.current = callback;
  const state = useRef({ timer: 0, pending: undefined, last: 0 });
  const flush = useCallback(() => {
    const current = state.current;
    clearTimeout(current.timer);
    current.timer = 0;
    if (current.pending === undefined) return;
    const value = current.pending;
    current.pending = undefined;
    current.last = Date.now();
    latest.current(value);
  }, []);
  const send = useCallback(value => {
    const current = state.current;
    current.pending = value;
    const wait = interval - (Date.now() - current.last);
    if (wait <= 0) flush();
    else if (!current.timer) current.timer = setTimeout(flush, wait);
  }, [flush, interval]);
  useEffect(() => flush, [flush]);
  return [send, flush];
};

export const rgbToHsv = hex => {
  const [r,g,b] = [1,3,5].map(i => (parseInt(hex.slice(i,i+2),16)||0)/255);
  const max=Math.max(r,g,b), min=Math.min(r,g,b), d=max-min;
  let h=d===0 ? 0 : max===r ? ((g-b)/d)%6 : max===g ? (b-r)/d+2 : (r-g)/d+4;
  return [(h*60+360)%360,max===0?0:d/max,max];
};
export const hsvToHex = ([h,s,v]) => {
  const c=v*s, x=c*(1-Math.abs((h/60)%2-1)), m=v-c;
  const rgb=h<60?[c,x,0]:h<120?[x,c,0]:h<180?[0,c,x]:h<240?[0,x,c]:h<300?[x,0,c]:[c,0,x];
  return '#'+rgb.map(n=>Math.round((n+m)*255).toString(16).padStart(2,'0')).join('');
};
export const PlayerColorPicker = ({value='#ffffff', onChange, channels=false}) => {
  const [hsv,setHsv]=useState(()=>rgbToHsv(value));
  const [hex,setHex]=useState(value);
  const dragging=useRef(false);
  const [send,flush]=useThrottledChange(onChange);
  // While a drag is on, the host echoes older values (sends are throttled): keep the local color.
  useEffect(()=>{if(dragging.current)return;if(value.toLowerCase()!==hsvToHex(hsv))setHsv(rgbToHsv(value));setHex(value);},[value]);
  const commit = next => {setHsv(next);const color=hsvToHex(next);setHex(color);send(color);};
  const endDrag = () => {dragging.current=false;flush();};
  // Native range inputs do not reliably get pointerup themselves; the window does.
  const startRange = () => {dragging.current=true;window.addEventListener('pointerup',endDrag,{once:true});};
  const pick = e => {
    const bounds=e.currentTarget.getBoundingClientRect(), radius=bounds.width/2;
    const x=e.clientX-bounds.left-radius, y=e.clientY-bounds.top-radius;
    commit([(Math.atan2(y,x)*180/Math.PI+360)%360,Math.min(1,Math.hypot(x,y)/radius),hsv[2]]);
  };
  return <div className="PlayerColorPicker">
    <div className="PlayerColorPicker__wheel" role="slider" tabIndex={0} aria-label="Цветовой круг"
      aria-valuemin={0} aria-valuemax={360} aria-valuenow={Math.round(hsv[0])}
      style={{filter:`brightness(${hsv[2]})`}}
      onPointerDown={e=>{dragging.current=true;e.currentTarget.setPointerCapture(e.pointerId);pick(e);}}
      onPointerMove={e=>{if(dragging.current)pick(e);}}
      onPointerUp={endDrag} onPointerCancel={endDrag}
      onKeyDown={e=>{if(['ArrowLeft','ArrowRight'].includes(e.key)){e.preventDefault();commit([(hsv[0]+(e.key==='ArrowRight'?1:359))%360,hsv[1],hsv[2]]);}}}>
      <span style={{left:`${50+Math.cos(hsv[0]*Math.PI/180)*hsv[1]*50}%`,top:`${50+Math.sin(hsv[0]*Math.PI/180)*hsv[1]*50}%`}} />
    </div>
    <label>Яркость <input type="range" min="0" max="1" step="0.01" value={hsv[2]} onPointerDown={startRange} onChange={e=>commit([hsv[0],hsv[1],Number(e.target.value)])} /></label>
    <label>HEX <input aria-label="HEX" value={hex} maxLength={7} onChange={e=>setHex(e.target.value)} onBlur={()=>{if(/^#[0-9a-f]{6}$/i.test(hex)){setHsv(rgbToHsv(hex));onChange(hex);}else setHex(value);}} /></label>
    {channels && <>
      {['H','S','V'].map((label,i)=><label key={label}>{label}<input aria-label={label} type="range" min="0" max={i===0?360:1} step={i===0?1:0.01} value={hsv[i]} onPointerDown={startRange} onChange={e=>{const next=[...hsv];next[i]=Number(e.target.value);commit(next);}} /></label>)}
      <div className="PlayerColorPicker__rgb">{['R','G','B'].map((label,i)=><label key={label}>{label}<input aria-label={label} type="number" min="0" max="255" value={parseInt(hsvToHex(hsv).slice(1+i*2,3+i*2),16)} onChange={e=>{const rgb=[1,3,5].map(n=>parseInt(hsvToHex(hsv).slice(n,n+2),16));rgb[i]=Math.min(255,Math.max(0,Number(e.target.value)));commit(rgbToHsv('#'+rgb.map(n=>n.toString(16).padStart(2,'0')).join('')));}} /></label>)}</div>
    </>}
  </div>;
};
export const ColorSquare = ({label,value,disabled,onChange,hint}) => {
  const [open,setOpen]=useState(false);
  const transfer = useContext(ColorTransferContext);
  const picking = transfer?.phase === 'pick';
  const applying = transfer?.phase === 'apply';
  const title = picking ? 'Скопировать цвет: ' + (label || value)
    : applying ? 'Вставить цвет: ' + (label || value) : label;
  const click = () => {
    if (picking || applying) {
      setOpen(false);
      transfer.useSquare(value, onChange);
    } else setOpen(!open);
  };
  return <div className={'ColorSquare' + (picking || applying ? ' ColorSquare--transfer' : '')}><button type="button" disabled={disabled && !picking} aria-label={title} title={title} aria-expanded={open} style={{backgroundColor:value}} onClick={click} /> <span>{label}</span>
    {hint && <small>{hint}</small>}
    {open && !disabled && <div className="ColorSquare__popover"><PlayerColorPicker value={value} onChange={onChange} /><button type="button" onClick={()=>setOpen(false)}>Закрыть</button></div>}
  </div>;
};
