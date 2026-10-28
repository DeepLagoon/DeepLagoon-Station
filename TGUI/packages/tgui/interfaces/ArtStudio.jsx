import { EmbeddedSelect } from '../components/EmbeddedSelect';
import { useInterfaceStyleGuard } from './useInterfaceStyleGuard';
import { useEffect, useRef, useState } from 'react';
import { useBackend } from '../backend';
import { Icon } from '../components';
import { SIZE, MAX_LAYERS, makeLayer, snapshot, restore, checkpoint, undo, addMask, compose, serialize, loadProject, brushSegment, encode } from './artStudioModel.mjs';
import './ArtStudio.scss';

export const ArtStudio = () => {
  const { data, act } = useBackend();
  const t = key => data.labels?.[key] || key;
  const editor = useRef(null), canvas = useRef(null), viewport = useRef(null), gesture = useRef(null), saved = useRef(null);
  const api = useRef(null), previewTimer = useRef(null), renderTimer = useRef(null), alive = useRef(true), generation = useRef(0), transfer = useRef(0);
  const [ready, setReady] = useState(false), [dirty, setDirty] = useState(false), [busy, setBusy] = useState(false), [, refresh] = useState(0);
  useInterfaceStyleGuard(dirty || busy);
  const [tool, setTool] = useState('brush'), [shape, setShape] = useState(data.tool === 'brush' ? 'round' : data.tool || 'round');
  const [color, setColor] = useState('#202020'), [size, setSize] = useState(18), [flow, setFlow] = useState(1);
  const [status, setStatus] = useState(''), [dialog, setDialog] = useState(''), [closeAfterSave, setCloseAfterSave] = useState(false);
  const [view, setView] = useState({ zoom: 1, x: 0, y: 0 });
  const space = useRef(false), paintOptions = useRef(null), dirtyRef = useRef(false), busyRef = useRef(false);
  dirtyRef.current = dirty; busyRef.current = busy;
  paintOptions.current = { tool, shape, color, size, flow, view };
  const changed = () => { setDirty(true); dirtyRef.current = true; refresh(n => n + 1); draw(); queuePreview(); };
  function draw() {
    if (renderTimer.current) return;
    renderTimer.current = requestAnimationFrame(() => {
      renderTimer.current = null;
      if (!canvas.current || !editor.current) return;
      const ctx = canvas.current.getContext('2d'); ctx.clearRect(0, 0, SIZE, SIZE); ctx.drawImage(compose(editor.current.layers, true), 0, 0);
    });
  }
  function queuePreview() {
    if (previewTimer.current) return;
    previewTimer.current = setTimeout(() => {
      previewTimer.current = null;
      if (!alive.current || !editor.current || busyRef.current) return;
      const small = document.createElement('canvas'); small.width = small.height = 128;
      small.getContext('2d').drawImage(compose(editor.current.layers, true), 0, 0, 128, 128);
      const png = encode(small), chunk = data.chunkSize || 5000, count = Math.ceil(png.length / chunk);
      for (let index = 0; index < count; index++) act('preview', { text: png.slice(index * chunk, (index + 1) * chunk), index, count });
    }, 450);
  }
  useEffect(() => {
    alive.current = true;
    const token = ++generation.current;
    loadProject(data.document || '{"version":1,"title":"","layers":[{"name":"Слой 1","visible":true,"opacity":1,"png":"","mask":""}]}').then(state => {
      if (!alive.current || token !== generation.current) return;
      editor.current = state; saved.current = snapshot(state); setReady(true); draw();
    }).catch(() => setStatus(t('loading')));
    return () => { alive.current = false; transfer.current++; if (previewTimer.current) clearTimeout(previewTimer.current); if (renderTimer.current) cancelAnimationFrame(renderTimer.current); };
  }, []);
  useEffect(() => {
    if (!data.result || !busyRef.current) return;
    transfer.current++; setBusy(false); busyRef.current = false;
    setStatus(data.status || '');
    if (data.result > 0) {
      saved.current = snapshot(editor.current); setDirty(false); dirtyRef.current = false;
      if (closeAfterSave) act('close');
    }
    setCloseAfterSave(false);
  }, [data.result]);
  useEffect(() => { if (data.closeRequest) requestClose(); }, [data.closeRequest]);
  function requestClose() {
    if (busyRef.current) return;
    if (dirtyRef.current) setDialog('close'); else act('close');
  }
  function discard() {
    restore(editor.current, saved.current); editor.current.undo = []; editor.current.redo = [];
    setDirty(false); dirtyRef.current = false; setDialog(''); setStatus(''); act('discard'); refresh(n => n + 1); draw();
  }
  async function save(close = false) {
    if (!ready || busyRef.current || gesture.current) return;
    setBusy(true); busyRef.current = true; setCloseAfterSave(close); setDialog(''); setStatus(t('saving'));
    if (previewTimer.current) { clearTimeout(previewTimer.current); previewTimer.current = null; }
    const token = ++transfer.current;
    try {
      const json = serialize(editor.current);
      if (json.length > data.maxDocument) throw new Error(t('too-large'));
      const chunk = data.chunkSize || 5000, count = Math.ceil(json.length / chunk);
      for (let index = 0; index < count; index++) {
        if (!alive.current || token !== transfer.current) return;
        act('save', { text: json.slice(index * chunk, (index + 1) * chunk), index, count });
        if (index + 1 < count) await new Promise(resolve => setTimeout(resolve, 25));
      }
      setTimeout(() => { if (alive.current && token === transfer.current && busyRef.current) { transfer.current++; setBusy(false); busyRef.current = false; setCloseAfterSave(false); setStatus(t('loading')); } }, 120000);
    } catch (e) { setBusy(false); busyRef.current = false; setCloseAfterSave(false); setStatus(e.message); }
  }
  function history(forward = false) { if (!busyRef.current && !gesture.current && undo(editor.current, forward)) changed(); }
  const mutate = fn => { if (busy || gesture.current) return; checkpoint(editor.current); fn(editor.current); changed(); };
  function selectMask() { if (!ready || busyRef.current) return; addMask(editor.current); changed(); }
  api.current = { save, history, requestClose, selectMask };
  useEffect(() => {
    const key = e => {
      const custom = e.type === 'grid-editor-key', k = custom ? e.detail : e;
      if (custom && !k.down) { if (k.code === 'Space') space.current = false; return; }
      if (!custom && /INPUT|TEXTAREA|SELECT/.test(e.target?.tagName)) return;
      if (!editor.current || e.defaultPrevented) return;
      const ctrl = k.ctrlKey || k.metaKey;
      if (ctrl && ['KeyZ', 'KeyY', 'KeyS'].includes(k.code)) {
        e.preventDefault(); if (k.code === 'KeyS') api.current.save(); else api.current.history(k.code === 'KeyY' || k.shiftKey); return;
      }
      if (ctrl || k.altKey) return;
      if (k.code === 'KeyB') setTool('brush');
      else if (k.code === 'KeyE') setTool('erase');
      else if (k.code === 'KeyM') api.current.selectMask();
      else if (k.code === 'Space') { e.preventDefault(); space.current = true; }
      else if (k.code === 'Escape') { e.preventDefault(); api.current.requestClose(); }
      else if (k.code === 'BracketLeft') setSize(n => Math.max(1, n - 2));
      else if (k.code === 'BracketRight') setSize(n => Math.min(160, n + 2));
    };
    const up = e => { if (e.code === 'Space') space.current = false; };
    window.addEventListener('keydown', key); window.addEventListener('keyup', up); window.addEventListener('grid-editor-key', key);
    return () => { window.removeEventListener('keydown', key); window.removeEventListener('keyup', up); window.removeEventListener('grid-editor-key', key); };
  }, []);
  const position = e => { const r = canvas.current.getBoundingClientRect(); return { x: Math.max(0, Math.min(SIZE, (e.clientX - r.left) * SIZE / r.width)), y: Math.max(0, Math.min(SIZE, (e.clientY - r.top) * SIZE / r.height)) }; };
  const down = e => {
    if (!ready || busy || dialog || (e.button !== 0 && e.button !== 1)) return;
    e.preventDefault(); canvas.current.focus(); canvas.current.setPointerCapture(e.pointerId);
    if (space.current || e.button === 1) { gesture.current = { pan: true, x: e.clientX, y: e.clientY, view }; return; }
    const state = editor.current, layer = state.layers.find(l => l.id === state.active);
    if (!layer?.visible) return;
    const target = state.target; checkpoint(state, target);
    const ctx = layer[target].getContext('2d'), p = position(e), options = { ...paintOptions.current, erase: tool === 'erase', mask: target === 'mask' };
    brushSegment(ctx, p, p, options); gesture.current = { ctx, point: p, options }; changed();
  };
  const move = e => {
    const g = gesture.current; if (!g) return;
    if (g.pan) { setView({ ...g.view, x: g.view.x + e.clientX - g.x, y: g.view.y + e.clientY - g.y }); return; }
    const p = position(e); if (Math.hypot(p.x - g.point.x, p.y - g.point.y) < .5) return;
    brushSegment(g.ctx, g.point, p, g.options); g.point = p; draw(); queuePreview();
  };
  const end = e => { const g = gesture.current; gesture.current = null; if (canvas.current?.hasPointerCapture(e.pointerId)) canvas.current.releasePointerCapture(e.pointerId); if (g && !g.pan) changed(); };
  const state = editor.current, layer = state?.layers.find(l => l.id === state.active);
  const focus = value => act('editing-text', { value });
  return <div className="ArtStudio" onFocusCapture={e => { if (/INPUT|SELECT/.test(e.target.tagName)) focus(true); }} onBlurCapture={e => { if (/INPUT|SELECT/.test(e.target.tagName)) focus(false); }}>
    <header className="ArtStudio__header"><span>{t('editor')}</span><button aria-label={t('close')} onClick={requestClose}>×</button></header>
    <div className="ArtStudio__bar"><input aria-label={t('name')} maxLength={80} value={state?.title || ''} disabled={!ready || busy} onChange={e => mutate(s => { s.title = e.target.value; })} />
      <button disabled={!ready || busy || !state.undo.length} onClick={() => history()}>{t('undo')}</button><button disabled={!ready || busy || !state.redo.length} onClick={() => history(true)}>{t('redo')}</button>
      <button disabled={!ready || busy || !dirty} onClick={() => save()}>{t('save')}</button><button disabled={!ready || busy || !dirty} onClick={() => setDialog('discard')}>{t('discard')}</button></div>
    <main className="ArtStudio__main"><aside className="ArtStudio__tools">
      <button aria-label={t('brush')} title={t('brush') + ' · B'} aria-pressed={tool === 'brush'} onClick={() => setTool('brush')}><Icon name="paintbrush" /></button>
      <button aria-label={t('eraser')} title={t('eraser') + ' · E'} aria-pressed={tool === 'erase'} onClick={() => setTool('erase')}><Icon name="eraser" /></button>
      <button aria-label={t('mask')} title={t('mask') + ' · M'} aria-pressed={state?.target === 'mask'} disabled={!ready || busy} onClick={selectMask}><Icon name="mask" /></button>
      <input type="color" aria-label={t('color')} value={color} onChange={e => setColor(e.target.value)} />
      <button aria-label={t('reset-view')} title={t('reset-view')} onClick={() => setView({ zoom: 1, x: 0, y: 0 })}><Icon name="expand" /></button>
    </aside><div className="ArtStudio__stage"><div className="ArtStudio__viewport" ref={viewport} onWheel={e => {
      e.preventDefault(); const r = viewport.current.getBoundingClientRect(), next = Math.max(.25, Math.min(8, view.zoom * Math.exp(-e.deltaY * .001)));
      const dx = e.clientX - r.left - r.width / 2, dy = e.clientY - r.top - r.height / 2, ratio = next / view.zoom;
      setView({ zoom: next, x: dx - (dx - view.x) * ratio, y: dy - (dy - view.y) * ratio });
    }}><canvas ref={canvas} width={SIZE} height={SIZE} tabIndex={0} aria-label={t('editor')} style={{ transform: `translate(${view.x}px,${view.y}px) scale(${view.zoom})` }} onPointerDown={down} onPointerMove={move} onPointerUp={end} onPointerCancel={end} /></div>
      <div className="ArtStudio__meta"><span>{SIZE} × {SIZE}</span><span>{Math.round(view.zoom * 100)}%</span><span>{state?.target === 'mask' ? t('mask') + ': ' : ''}{layer?.name}</span></div></div>
      <aside className="ArtStudio__panels"><section><label>{t('shape')}<EmbeddedSelect aria-label={t('shape')} value={shape} disabled={busy} onChange={e => setShape(e.target.value)}>{['round','soft','flat','square','chalk','spray'].map(s => <option key={s} value={s}>{s === 'square' ? '■' : t(s)}</option>)}</EmbeddedSelect></label>
      <label>{t('size')} <output>{size} px</output><input type="range" aria-label={t('size')} min="1" max="160" value={size} onChange={e => setSize(Number(e.target.value))} /></label>
      <label>{t('opacity')} <output>{Math.round(flow * 100)}%</output><input type="range" aria-label={t('opacity')} min="1" max="100" value={Math.round(flow * 100)} onChange={e => setFlow(Number(e.target.value) / 100)} /></label></section>
      <section><div className="ArtStudio__section-title">{t('layers')}</div><div className="ArtStudio__layers">{[...(state?.layers || [])].reverse().map(l => <div key={l.id} className={l.id === state.active ? 'active' : ''}>
        <button aria-label={t('visible') + ': ' + l.name} aria-pressed={l.visible} disabled={busy} onClick={() => mutate(() => { l.visible = !l.visible; })}><Icon name={l.visible ? 'eye' : 'eye-slash'} /></button>
        <button className="ArtStudio__layer-name" onClick={() => { state.active = l.id; state.target = 'ink'; refresh(n => n + 1); }} onDoubleClick={() => { state.active = l.id; setDialog('rename'); }} disabled={busy}>{l.name}</button>
        {l.mask && <button className="ArtStudio__mask" aria-label={t('mask') + ': ' + l.name} aria-pressed={state.active === l.id && state.target === 'mask'} disabled={busy} onClick={() => { state.active = l.id; state.target = 'mask'; refresh(n => n + 1); }}>◐</button>}
      </div>)}</div><div className="ArtStudio__layer-actions">
        <button aria-label={t('add-layer')} title={t('add-layer')} disabled={!ready || busy || state.layers.length >= MAX_LAYERS} onClick={() => mutate(s => { const l = makeLayer(t('layers') + ' ' + (s.layers.length + 1)); s.layers.push(l); s.active = l.id; s.target = 'ink'; })}><Icon name="plus" /></button>
        <button aria-label={t('duplicate-layer')} title={t('duplicate-layer')} disabled={!ready || busy || state.layers.length >= MAX_LAYERS} onClick={() => mutate(s => { const l = { ...layer, id: makeLayer('').id, name: layer.name.slice(0, 35) + ' (2)' }; s.layers.push(l); s.active = l.id; })}><Icon name="copy" /></button>
        <button aria-label={t('add-mask')} title={t('add-mask')} disabled={!ready || busy} onClick={selectMask}><Icon name="mask" /></button>
        <button aria-label={t('delete-layer')} title={t('delete-layer')} disabled={!ready || busy || state.layers.length < 2} onClick={() => mutate(s => { s.layers = s.layers.filter(l => l.id !== s.active); s.active = s.layers.at(-1).id; s.target = 'ink'; })}><Icon name="trash" /></button>
        {[-1, 1].map(d => <button key={d} aria-label={t(d < 0 ? 'down' : 'up')} disabled={!ready || busy || state.layers.findIndex(l => l.id === state.active) + d < 0 || state.layers.findIndex(l => l.id === state.active) + d >= state.layers.length} onClick={() => mutate(s => { const i = s.layers.findIndex(l => l.id === s.active); [s.layers[i], s.layers[i + d]] = [s.layers[i + d], s.layers[i]]; })}><Icon name={d < 0 ? 'arrow-down' : 'arrow-up'} /></button>)}
      </div><label>{t('layer-opacity')} <output>{Math.round((layer?.opacity || 0) * 100)}%</output><input type="range" aria-label={t('layer-opacity')} min="0" max="100" disabled={!ready || busy} value={Math.round((layer?.opacity || 0) * 100)} onPointerDown={() => { if (state) checkpoint(state); }} onChange={e => { layer.opacity = Number(e.target.value) / 100; changed(); }} /></label>
      {layer?.mask && <button disabled={busy} onClick={() => mutate(s => { layer.mask = null; s.target = 'ink'; })}>{t('remove-mask')}</button>}</section></aside></main>
    <footer>{busy ? t('saving') : status || data.status || (dirty ? t('unsaved') : '')}</footer>
    {dialog && <div className="ArtStudio__shade"><div className="ArtStudio__dialog" role="dialog" aria-modal="true">
      {dialog === 'rename' ? <><label>{t('rename')}<input autoFocus maxLength={40} defaultValue={layer?.name} onKeyDown={e => { if (e.key === 'Enter') { const name = e.currentTarget.value.trim(); if (name) mutate(() => { layer.name = name; }); setDialog(''); } }} /></label><button onClick={() => setDialog('')}>{t('close')}</button></>
        : <><p>{dialog === 'close' ? t('unsaved') : t('confirm-discard')}</p><div>{dialog === 'close' && <button onClick={() => save(true)}>{t('save')}</button>}<button onClick={() => { const closing = dialog === 'close'; discard(); if (closing) act('close'); }}>{t('discard')}</button><button onClick={() => setDialog('')}>{t('cancel')}</button></div></>}
    </div></div>}
  </div>;
};
