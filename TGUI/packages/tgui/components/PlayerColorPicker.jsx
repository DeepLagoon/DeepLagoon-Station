import { createContext, useContext, useEffect, useRef, useState } from 'react';
import './PlayerColorPicker.scss';
import { Icon } from './Icon';
import { useThrottledChange } from './useThrottledChange';

export const ColorTransferContext = createContext(null);
export const ColorDialogContext = createContext(null);
let nextColorSquare = 0;

export const EditorColorDialogProvider = ({ data, act, children }) => {
  const active = useRef(null);
  const lastSequence = useRef(0);
  useEffect(() => {
    const result = data.colorPickerResult;
    if (!result?.sequence || result.sequence <= lastSequence.current) return;
    lastSequence.current = result.sequence;
    if (result.id === active.current?.id) active.current.change(result.value);
  }, [data.colorPickerResult]);
  useEffect(() => { if (!data.colorPickerOpen) active.current = null; }, [data.colorPickerOpen]);
  useEffect(() => { active.current = null; }, [data.mode, data.setup?.selected]);
  const dialog = {
    open: (id, label, value, change) => {
      active.current = { id, change };
      act('color-picker-open', { id, title: label || 'Цвет', value });
    },
    close: id => {
      if (active.current?.id !== id) return;
      active.current = null;
      act('color-picker-close', { id });
    },
  };
  return <ColorDialogContext.Provider value={dialog}>{children}</ColorDialogContext.Provider>;
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
export const PlayerColorPicker = ({value='#ffffff', onChange, channels=false, mode='wheel'}) => {
  const [hsv,setHsv]=useState(()=>rgbToHsv(value));
  const [hex,setHex]=useState(value);
  const dragging=useRef(false);
  const [send,flush]=useThrottledChange(onChange);
  useEffect(()=>{if(dragging.current)return;if(value.toLowerCase()!==hsvToHex(hsv))setHsv(rgbToHsv(value));setHex(value);},[value]);
  const commit = next => {setHsv(next);const color=hsvToHex(next);setHex(color);send(color);};
  const endDrag = () => {dragging.current=false;flush();};
  useEffect(() => {
    window.addEventListener('pointerup', endDrag);
    window.addEventListener('pointercancel', endDrag);
    window.addEventListener('blur', endDrag);
    return () => {
      window.removeEventListener('pointerup', endDrag);
      window.removeEventListener('pointercancel', endDrag);
      window.removeEventListener('blur', endDrag);
    };
  }, [flush]);
  const pick = e => {
    const bounds=e.currentTarget.getBoundingClientRect(), radius=bounds.width/2;
    const x=e.clientX-bounds.left-radius, y=e.clientY-bounds.top-radius;
    commit([(Math.atan2(y,x)*180/Math.PI+360)%360,Math.min(1,Math.hypot(x,y)/radius),hsv[2]]);
  };
  return <div className="PlayerColorPicker">
    {mode === 'wheel' && <div className="PlayerColorPicker__wheel" role="slider" tabIndex={0} aria-label="Цветовой круг"
      aria-valuemin={0} aria-valuemax={360} aria-valuenow={Math.round(hsv[0])}
      style={{filter:`brightness(${hsv[2]})`}}
      onPointerDown={e=>{dragging.current=true;e.currentTarget.setPointerCapture(e.pointerId);pick(e);}}
      onPointerMove={e=>{if(dragging.current)pick(e);}}
      onPointerUp={endDrag} onPointerCancel={endDrag}
      onKeyDown={e=>{if(['ArrowLeft','ArrowRight'].includes(e.key)){e.preventDefault();commit([(hsv[0]+(e.key==='ArrowRight'?1:359))%360,hsv[1],hsv[2]]);}}}>
      <span style={{left:`${50+Math.cos(hsv[0]*Math.PI/180)*hsv[1]*50}%`,top:`${50+Math.sin(hsv[0]*Math.PI/180)*hsv[1]*50}%`}} />
    </div>}
    {mode === 'wheel' && <label>Яркость <input type="range" min="0" max="1" step="0.01" value={hsv[2]} onPointerDown={()=>{dragging.current=true;}} onBlur={endDrag} onChange={e=>commit([hsv[0],hsv[1],Number(e.target.value)])} /></label>}
    <label>HEX <input aria-label="HEX" value={hex} maxLength={7} onChange={e=>setHex(e.target.value)} onBlur={()=>{if(/^#[0-9a-f]{6}$/i.test(hex)){setHsv(rgbToHsv(hex));send(hex);flush();}else setHex(value);}} /></label>
    {(channels || mode === 'hsv') && <>
      {['H','S','V'].map((label,i)=><label key={label}>{label}<input aria-label={label} type="range" min="0" max={i===0?360:1} step={i===0?1:0.01} value={hsv[i]} onPointerDown={()=>{dragging.current=true;}} onBlur={endDrag} onChange={e=>{const next=[...hsv];next[i]=Number(e.target.value);commit(next);}} /></label>)}
    </>}
    {(channels || mode === 'rgb') && <>
      <div className="PlayerColorPicker__rgb">{['R','G','B'].map((label,i)=><label key={label}>{label}<input aria-label={label} type="number" min="0" max="255" value={parseInt(hsvToHex(hsv).slice(1+i*2,3+i*2),16)} onChange={e=>{const rgb=[1,3,5].map(n=>parseInt(hsvToHex(hsv).slice(n,n+2),16));rgb[i]=Math.min(255,Math.max(0,Number(e.target.value)));commit(rgbToHsv('#'+rgb.map(n=>n.toString(16).padStart(2,'0')).join('')));}} /></label>)}</div>
    </>}
  </div>;
};
export const ColorSquare = ({label,value,disabled,onChange,hint,lockReason}) => {
  const [open,setOpen]=useState(false);
  const transfer = useContext(ColorTransferContext);
  const dialog = useContext(ColorDialogContext);
  const id = useRef(null);
  if (id.current === null) id.current = 'color-' + (++nextColorSquare);
  const change = useRef(onChange);
  change.current = onChange;
  useEffect(() => () => dialog?.close(id.current), []);
  const picking = transfer?.phase === 'pick';
  const applying = transfer?.phase === 'apply';
  const locked = disabled && !!lockReason && !picking;
  const title = picking ? 'Скопировать цвет: ' + (label || value)
    : applying ? 'Вставить цвет: ' + (label || value) : label;
  const click = () => {
    if (picking || applying) {
      setOpen(false);
      transfer.useSquare(value, onChange);
    } else if (dialog) dialog.open(id.current, label, value, next => change.current(next));
    else setOpen(!open);
  };
  return <div className={'ColorSquare' + (picking || applying ? ' ColorSquare--transfer' : '')}><button type="button" disabled={disabled && !picking} aria-label={title} title={locked ? lockReason : title} aria-description={locked ? lockReason : undefined} aria-expanded={open} className={locked ? 'ColorSquare__locked' : undefined} style={{backgroundColor:locked ? '#60646c' : value}} onClick={click}>{locked && <Icon name="lock" aria-hidden="true" />}</button> <span>{label}</span>
    {hint && <small>{hint}</small>}
    {open && !disabled && <div className="ColorSquare__popover"><PlayerColorPicker value={value} onChange={onChange} /><button type="button" onClick={()=>setOpen(false)}>Закрыть</button></div>}
  </div>;
};
