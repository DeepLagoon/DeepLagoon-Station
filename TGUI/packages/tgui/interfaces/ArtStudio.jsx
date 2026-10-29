import { EmbeddedSelect } from '../components/EmbeddedSelect';
import { useEffect, useRef, useState } from 'react';
import { useBackend } from '../backend';
import { Icon } from '../components';
import { useArtDraft } from './useArtDraft';
import { ArtStudioColorPalette } from './ArtStudioColorPalette';
import { SIZE, MAX_LAYERS, makeLayer, snapshot, restore, checkpoint, undo, addMask, compose, serialize, loadProject, beginStroke, extendStroke, encode } from './artStudioModel.mjs';
import './ArtStudio.scss';

export const ArtStudio = () => {
  return <ArtStudioEditor {...useBackend()} />;
};

export const ArtStudioEditor = ({ data, act }) => {
  const t = key => data.labels?.[key] || key;
  const editor = useRef(null), canvas = useRef(null), viewport = useRef(null), gesture = useRef(null), saved = useRef(null);
  const api = useRef(null), previewTimer = useRef(null), renderTimer = useRef(null), alive = useRef(true), generation = useRef(0), transfer = useRef(0);
  const [ready, setReady] = useState(false), [dirty, setDirty] = useState(false), [busy, setBusy] = useState(false), [, refresh] = useState(0);
  useEffect(() => { act('__style-blocked', { blocked: dirty || busy }); return () => act('__style-blocked', { blocked: false }); }, [dirty || busy]);
  const transferKind = useRef('save'), tabletRevision = useRef(data.tabletRevision || 0), locked = useRef(false);
  locked.current = !!data.tabletActive;
  const [tool, setTool] = useState('brush'), [shape, setShape] = useState(data.tool === 'brush' ? 'round' : data.tool || 'round');
  const [eraserSize, setEraserSize] = useState(18);
  const [color, setColor] = useState(data.color || '#202020'), [size, setSize] = useState(18), [flow, setFlow] = useState(1);
  const [status, setStatus] = useState(''), [dialog, setDialog] = useState('');
  const [saveTitle, setSaveTitle] = useState('');
  const [view, setView] = useState({ zoom: 1, x: 0, y: 0 });
  const space = useRef(false), paintOptions = useRef(null), dirtyRef = useRef(false), busyRef = useRef(false), closing = useRef(false);
  dirtyRef.current = dirty; busyRef.current = busy;
  paintOptions.current = { tool, shape, color, size: tool === 'erase' ? eraserSize : size, flow, view };
  const draft = useArtDraft({ data, act, document: () => editor.current && serialize(editor.current), blocked: () => busyRef.current && !closing.current || locked.current || !!data.draftDisabled, localBlocked: () => busyRef.current || locked.current, dirty: () => dirtyRef.current });
  const changed = (force = false) => { setStatus(''); setDirty(true); dirtyRef.current = true; refresh(n => n + 1); draw(); queuePreview(); draft.capture(force); };
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
    (async () => {
      const recovered = await draft.recover(data), text = recovered.document || data.document;
      let state;
      try { state = await loadProject(text); }
      catch (error) {
        if (!recovered.local) throw error;
        draft.clear(); recovered.local = false; state = await loadProject(data.document);
      }
      const baseline = text === (data.savedDocument || data.document) ? state : await loadProject(data.savedDocument || data.document);
      if (!alive.current || token !== generation.current) return;
      editor.current = state; saved.current = snapshot(baseline);
      const unsaved = !!data.hasDraft || recovered.local; setDirty(unsaved); dirtyRef.current = unsaved;
      setReady(true); draw(); if (unsaved) draft.capture(true);
    })().catch(() => setStatus(t('loading')));
    return () => { alive.current = false; transfer.current++; if (previewTimer.current) clearTimeout(previewTimer.current); if (renderTimer.current) cancelAnimationFrame(renderTimer.current); };
  }, []);
  useEffect(() => {
    if (!data.result || !busyRef.current || closing.current) return;
    transfer.current++; setBusy(false); busyRef.current = false;
    setStatus(data.status || '');
    if (data.result > 0 && transferKind.current !== 'tablet') {
      saved.current = snapshot(editor.current); setDirty(false); dirtyRef.current = false; draft.clear();
    }
  }, [data.result]);
  useEffect(() => {
    if (!data.tabletRevision || data.tabletRevision === tabletRevision.current) return;
    tabletRevision.current = data.tabletRevision;
    const token = ++generation.current;
    (async () => {
      const state = await loadProject(data.document), baseline = data.document === data.savedDocument ? state : await loadProject(data.savedDocument || data.document);
      if (!alive.current || token !== generation.current) return;
      draft.replace(data); editor.current = state; saved.current = snapshot(baseline);
      setDirty(!!data.hasDraft); dirtyRef.current = !!data.hasDraft; setReady(true); draw();
      if (data.hasDraft && !locked.current) draft.capture(true);
    })().catch(() => setStatus(t('loading')));
  }, [data.tabletRevision]);
  useEffect(() => { if (data.closeRequest) requestClose(); }, [data.closeRequest]);
  async function requestClose() {
    if (busyRef.current || closing.current) return;
    if (locked.current) { act('tablet-revoke'); act('close'); return; }
    if (gesture.current) end({ pointerId: gesture.current.pointerId });
    if (!dirtyRef.current) { act('close'); return; }
    // Capture before locking edits, then keep the draft transport running until acknowledged.
    const flushed = draft.flush();
    closing.current = true; setBusy(true); busyRef.current = true; setDialog(''); setStatus('');
    try {
      if (await flushed) { if (alive.current) act('close'); }
      else if (alive.current) setStatus(t('draft-failed'));
    } finally {
      closing.current = false;
      if (alive.current) { setBusy(false); busyRef.current = false; }
    }
  }
  function discard() {
    restore(editor.current, saved.current); editor.current.undo = []; editor.current.redo = [];
    draft.clear(); setDirty(false); dirtyRef.current = false; setDialog(''); setStatus(''); const result = act('discard'); refresh(n => n + 1); draw(); return result;
  }
  function requestSave() {
    if (!ready || busyRef.current || locked.current) return;
    if (gesture.current) end({ pointerId: gesture.current.pointerId });
    setSaveTitle(editor.current.title); setDialog('save');
  }
  function confirmSave() {
    if (!ready || busyRef.current || gesture.current || !saveTitle.trim()) return;
    mutate(s => { s.title = saveTitle.trim(); }); save();
  }
  function requestTablet() {
    if (!ready || busyRef.current || locked.current || !data.tabletAvailable) return;
    if (gesture.current) end({ pointerId: gesture.current.pointerId });
    save('tablet');
  }
  async function save(action = 'save') {
    if (!ready || busyRef.current || locked.current || gesture.current) return;
    if (action === 'tablet') draft.capture(true);
    draft.pause();
    transferKind.current = action;
    setBusy(true); busyRef.current = true; setDialog(''); setStatus(t('saving'));
    if (previewTimer.current) { clearTimeout(previewTimer.current); previewTimer.current = null; }
    const token = ++transfer.current;
    try {
      const json = serialize(editor.current);
      if (json.length > data.maxDocument) throw new Error(t('too-large'));
      const chunk = data.chunkSize || 5000, count = Math.ceil(json.length / chunk);
      for (let index = 0; index < count; index++) {
        if (!alive.current || token !== transfer.current) return;
        act(action, { text: json.slice(index * chunk, (index + 1) * chunk), index, count, ...draft.metadata() });
        if (index + 1 < count) await new Promise(resolve => setTimeout(resolve, 25));
      }
      setTimeout(() => { if (alive.current && token === transfer.current && busyRef.current) { transfer.current++; setBusy(false); busyRef.current = false; setStatus(t('loading')); } }, 120000);
    } catch (e) { setBusy(false); busyRef.current = false; setStatus(e.message); }
  }
  function history(forward = false) { if (!locked.current && !busyRef.current && !gesture.current && undo(editor.current, forward)) changed(true); }
  const mutate = fn => { if (busy || locked.current || gesture.current) return; checkpoint(editor.current); fn(editor.current); changed(true); };
  function selectMask() {
    if (!ready || busyRef.current || locked.current || gesture.current) return;
    const state = editor.current, layer = state.layers.find(l => l.id === state.active);
    if (state.target === 'mask') { state.target = 'ink'; refresh(n => n + 1); }
    else if (layer.mask) { state.target = 'mask'; refresh(n => n + 1); }
    else { addMask(state); changed(true); }
  }
  api.current = { save: dialog === 'save' ? confirmSave : requestSave, history, requestClose, selectMask, resize: delta => (tool === 'erase' ? setEraserSize : setSize)(n => Math.max(1, Math.min(160, n + delta))) };
  useEffect(() => {
    const key = e => {
      const custom = e.type === 'grid-editor-key', k = custom ? e.detail : e;
      if (custom && !k.down) { if (k.code === 'Space') space.current = false; return; }
      if (!editor.current || e.defaultPrevented) return;
      if (locked.current) { if ((k.ctrlKey || k.metaKey) && k.code === 'KeyS') e.preventDefault(); return; }
      const ctrl = k.ctrlKey || k.metaKey;
      if (ctrl && k.code === 'KeyS') { e.preventDefault(); api.current.save(); return; }
      if (!custom && /INPUT|TEXTAREA|SELECT/.test(e.target?.tagName)) return;
      if (ctrl && ['KeyZ', 'KeyY'].includes(k.code)) {
        e.preventDefault(); api.current.history(k.code === 'KeyY' || k.shiftKey); return;
      }
      if (ctrl || k.altKey) return;
      if (k.code === 'KeyB') setTool('brush');
      else if (k.code === 'KeyE') setTool('erase');
      else if (k.code === 'KeyM') api.current.selectMask();
      else if (k.code === 'Space') { e.preventDefault(); space.current = true; }
      else if (k.code === 'Escape') { e.preventDefault(); api.current.requestClose(); }
      else if (k.code === 'BracketLeft') api.current.resize(-2);
      else if (k.code === 'BracketRight') api.current.resize(2);
    };
    const up = e => { if (e.code === 'Space') space.current = false; };
    window.addEventListener('keydown', key); window.addEventListener('keyup', up); window.addEventListener('grid-editor-key', key);
    return () => { window.removeEventListener('keydown', key); window.removeEventListener('keyup', up); window.removeEventListener('grid-editor-key', key); };
  }, []);
  const position = e => { const r = canvas.current.getBoundingClientRect(); return { x: Math.max(0, Math.min(SIZE, (e.clientX - r.left) * SIZE / r.width)), y: Math.max(0, Math.min(SIZE, (e.clientY - r.top) * SIZE / r.height)) }; };
  const pressure = e => e.pointerType === 'pen' ? Math.max(.01, Math.min(1, e.pressure ?? 1)) : 1;
  const pickColor = e => {
    const p = position(e), pixel = canvas.current.getContext('2d').getImageData(Math.min(SIZE - 1, Math.floor(p.x)), Math.min(SIZE - 1, Math.floor(p.y)), 1, 1).data;
    setColor('#' + [...pixel].slice(0, 3).map(n => n.toString(16).padStart(2, '0')).join(''));
  };
  const down = e => {
    if (!ready || busy || locked.current || dialog || gesture.current || (e.button !== 0 && e.button !== 1 && e.button !== 2)) return;
    e.preventDefault(); canvas.current.focus(); canvas.current.setPointerCapture(e.pointerId);
    if (e.button === 2) { pickColor(e); gesture.current = { pointerId: e.pointerId, buttons: 2, pick: true }; return; }
    if (space.current || e.button === 1) { gesture.current = { pointerId: e.pointerId, buttons: e.button === 1 ? 4 : e.button === 2 ? 2 : 1, pan: true, x: e.clientX, y: e.clientY, view }; return; }
    const state = editor.current, layer = state.layers.find(l => l.id === state.active);
    if (!layer?.visible) return;
    const target = state.target; checkpoint(state, target);
    const ctx = layer[target].getContext('2d'), p = position(e), options = { ...paintOptions.current, ...(tool === 'erase' ? { shape: 'round', flow: 1 } : {}), erase: tool === 'erase', mask: target === 'mask' };
    const stroke = beginStroke(ctx), force = pressure(e);
    extendStroke(stroke, p, p, options, force, force); gesture.current = { pointerId: e.pointerId, buttons: 1, stroke, point: p, pressure: force, options }; changed();
  };
  const move = e => {
    const g = gesture.current; if (!g || g.pointerId !== e.pointerId) return;
    if (!(e.buttons & g.buttons)) { end(e); return; }
    if (g.pick) { pickColor(e); return; }
    if (g.pan) { setView({ ...g.view, x: g.view.x + e.clientX - g.x, y: g.view.y + e.clientY - g.y }); return; }
    const samples = e.nativeEvent?.getCoalescedEvents?.() || [];
    for (const sample of samples.length ? samples : [e]) {
      const p = position(sample), force = pressure(sample);
      if (Math.hypot(p.x - g.point.x, p.y - g.point.y) < .5 && Math.abs(force - g.pressure) < .01) continue;
      extendStroke(g.stroke, g.point, p, g.options, g.pressure, force); g.point = p; g.pressure = force;
    }
    draw(); queuePreview(); draft.progress();
  };
  const end = e => { const g = gesture.current; if (!g || g.pointerId !== e.pointerId) return; gesture.current = null; if (canvas.current?.hasPointerCapture(e.pointerId)) canvas.current.releasePointerCapture(e.pointerId); if (!g.pan && !g.pick) changed(true); };
  useEffect(() => {
    const cancel = () => { if (gesture.current) end({ pointerId: gesture.current.pointerId }); space.current = false; };
    window.addEventListener('blur', cancel);
    return () => window.removeEventListener('blur', cancel);
  }, []);
  const state = editor.current, layer = state?.layers.find(l => l.id === state.active);
  const focus = value => act('editing-text', { value });
  return <div className={'ArtStudio' + (data.external ? ' ArtStudio--external' : '')} onFocusCapture={e => { if (/INPUT|SELECT/.test(e.target.tagName)) focus(true); }} onBlurCapture={e => { if (/INPUT|SELECT/.test(e.target.tagName)) focus(false); }}>
    <header className="ArtStudio__header"><span>{t('editor')}</span><div className="ArtStudio__header-actions">
      {data.external ? <button onClick={requestClose}>{t('return-game')}</button> : <button aria-label={t('tablet')} title={t(data.tabletAvailable ? 'tablet' : 'tablet-unavailable')} disabled={!ready || busy || data.tabletActive || !data.tabletAvailable} onClick={requestTablet}><Icon name="pen-to-square" /> {t('tablet')}</button>}
      <button className="ArtStudio__save" aria-label={t('save')} title={t('save')} disabled={!ready || busy || data.tabletActive} onClick={() => requestSave()}><Icon name="floppy-disk" /> {t('save')}</button></div></header>
    <div className="ArtStudio__bar">
      <button disabled={!ready || busy || !state.undo.length} onClick={() => history()}>{t('undo')}</button><button disabled={!ready || busy || !state.redo.length} onClick={() => history(true)}>{t('redo')}</button>
      <button disabled={!ready || busy || !dirty} onClick={() => setDialog('discard')}>{t('discard')}</button></div>
    <main className="ArtStudio__main"><aside className="ArtStudio__tools">
      <button aria-label={t('brush')} title={t('brush') + ' · B'} aria-pressed={tool === 'brush'} onClick={() => setTool('brush')}><Icon name="paintbrush" /></button>
      <button aria-label={t('eraser')} title={t('eraser') + ' · E'} aria-pressed={tool === 'erase'} onClick={() => setTool('erase')}><Icon name="eraser" /></button>
      <button aria-label={t('mask')} title={t('mask') + ' · M'} aria-pressed={state?.target === 'mask'} disabled={!ready || busy} onClick={selectMask}><Icon name="mask" /></button>
      <button className="ArtStudio__color" aria-label={t('color')} title={t('color')} style={{ backgroundColor: color }} onClick={() => setDialog('color')} />
      <button aria-label={t('reset-view')} title={t('reset-view')} onClick={() => setView({ zoom: 1, x: 0, y: 0 })}><Icon name="expand" /></button>
    </aside><div className="ArtStudio__stage"><div className="ArtStudio__viewport" ref={viewport} onWheel={e => {
      e.preventDefault(); const r = viewport.current.getBoundingClientRect(), next = Math.max(.25, Math.min(8, view.zoom * Math.exp(-e.deltaY * .001)));
      const dx = e.clientX - r.left - r.width / 2, dy = e.clientY - r.top - r.height / 2, ratio = next / view.zoom;
      setView({ zoom: next, x: dx - (dx - view.x) * ratio, y: dy - (dy - view.y) * ratio });
    }}><canvas ref={canvas} width={SIZE} height={SIZE} tabIndex={0} aria-label={t('editor')} style={{ transform: `translate(${view.x}px,${view.y}px) scale(${view.zoom})` }} onContextMenu={e => e.preventDefault()} onPointerDown={down} onPointerMove={move} onPointerUp={end} onPointerCancel={end} onLostPointerCapture={end} /></div>
      <div className="ArtStudio__meta"><span>{SIZE} × {SIZE}</span><span>{Math.round(view.zoom * 100)}%</span><span>{state?.target === 'mask' ? t('mask') + ': ' : ''}{layer?.name}</span></div></div>
      <aside className="ArtStudio__panels"><section><label>{t('shape')}<EmbeddedSelect aria-label={t('shape')} value={tool === 'erase' ? 'round' : shape} disabled={busy || tool === 'erase'} onChange={e => setShape(e.target.value)}>{['round','soft','flat','square','chalk','spray'].map(s => <option key={s} value={s}>{s === 'square' ? '■' : t(s)}</option>)}</EmbeddedSelect></label>
      <label>{t(tool === 'erase' ? 'eraser-size' : 'size')} <output>{tool === 'erase' ? eraserSize : size} px</output><input type="range" aria-label={t(tool === 'erase' ? 'eraser-size' : 'size')} min="1" max="160" value={tool === 'erase' ? eraserSize : size} onChange={e => (tool === 'erase' ? setEraserSize : setSize)(Number(e.target.value))} /></label>
      <label>{t('opacity')} <output>{Math.round(flow * 100)}%</output><input type="range" aria-label={t('opacity')} min="1" max="100" value={Math.round(flow * 100)} onChange={e => setFlow(Number(e.target.value) / 100)} /></label></section>
      <section><div className="ArtStudio__section-title">{t('layers')}</div><div className="ArtStudio__layers">{[...(state?.layers || [])].reverse().map(l => <div key={l.id} className={l.id === state.active ? 'active' : ''}>
        <button aria-label={t('visible') + ': ' + l.name} aria-pressed={l.visible} disabled={busy} onClick={() => mutate(() => { l.visible = !l.visible; })}><Icon name={l.visible ? 'eye' : 'eye-slash'} /></button>
        <button className="ArtStudio__layer-name" onClick={() => { state.active = l.id; state.target = 'ink'; refresh(n => n + 1); }} onDoubleClick={() => { state.active = l.id; setDialog('rename'); }} disabled={busy}>{l.name}</button>
        {l.mask && <button className="ArtStudio__mask" aria-label={t('mask') + ': ' + l.name} aria-pressed={state.active === l.id && state.target === 'mask'} disabled={busy} onClick={() => { state.target = state.active === l.id && state.target === 'mask' ? 'ink' : 'mask'; state.active = l.id; refresh(n => n + 1); }}>◐</button>}
      </div>)}</div><div className="ArtStudio__layer-actions">
        <button aria-label={t('add-layer')} title={t('add-layer')} disabled={!ready || busy || state.layers.length >= MAX_LAYERS} onClick={() => mutate(s => { const l = makeLayer(t('layers') + ' ' + (s.layers.length + 1)); s.layers.unshift(l); s.active = l.id; s.target = 'ink'; })}><Icon name="plus" /></button>
        <button aria-label={t('duplicate-layer')} title={t('duplicate-layer')} disabled={!ready || busy || state.layers.length >= MAX_LAYERS} onClick={() => mutate(s => { const l = { ...layer, id: makeLayer('').id, name: layer.name.slice(0, 35) + ' (2)' }; s.layers.unshift(l); s.active = l.id; })}><Icon name="copy" /></button>
        <button aria-label={t('add-mask')} title={t('add-mask')} disabled={!ready || busy} onClick={selectMask}><Icon name="mask" /></button>
        <button aria-label={t('delete-layer')} title={t('delete-layer')} disabled={!ready || busy || state.layers.length < 2} onClick={() => mutate(s => { s.layers = s.layers.filter(l => l.id !== s.active); s.active = s.layers.at(-1).id; s.target = 'ink'; })}><Icon name="trash" /></button>
        {[-1, 1].map(d => <button key={d} aria-label={t(d < 0 ? 'down' : 'up')} disabled={!ready || busy || state.layers.findIndex(l => l.id === state.active) + d < 0 || state.layers.findIndex(l => l.id === state.active) + d >= state.layers.length} onClick={() => mutate(s => { const i = s.layers.findIndex(l => l.id === s.active); [s.layers[i], s.layers[i + d]] = [s.layers[i + d], s.layers[i]]; })}><Icon name={d < 0 ? 'arrow-down' : 'arrow-up'} /></button>)}
      </div><label>{t('layer-opacity')} <output>{Math.round((layer?.opacity || 0) * 100)}%</output><input type="range" aria-label={t('layer-opacity')} min="0" max="100" disabled={!ready || busy} value={Math.round((layer?.opacity || 0) * 100)} onPointerDown={() => { if (state) checkpoint(state); }} onChange={e => { layer.opacity = Number(e.target.value) / 100; changed(); }} /></label>
      {layer?.mask && <button disabled={busy} onClick={() => mutate(s => { layer.mask = null; s.target = 'ink'; })}>{t('remove-mask')}</button>}</section></aside></main>
    <footer>{busy ? t(closing.current ? 'draft-saving' : 'saving') : status || (dirty ? draft.status || t('unsaved') : data.status || '')}</footer>
    {data.tabletActive && <div className="ArtStudio__shade"><div className="ArtStudio__dialog" role="dialog" aria-modal="true"><p>{t('tablet-active')}</p><button onClick={() => act('tablet-revoke')}>{t('return-game')}</button></div></div>}
    {dialog && <div className="ArtStudio__shade"><div className="ArtStudio__dialog" role="dialog" aria-modal="true">
      {dialog === 'color' ? <><p>{t('color')}</p><ArtStudioColorPalette value={color} onChange={setColor} labels={data.labels || {}} /><button onClick={() => setDialog('')}>{t('close')}</button></> : dialog === 'save' ? <form onSubmit={e => { e.preventDefault(); confirmSave(); }}>
        <p>{t('save-title')}</p><label>{t('name')}<input autoFocus maxLength={80} value={saveTitle} onChange={e => setSaveTitle(e.target.value)} /></label>
        <button className="ArtStudio__save" aria-label={t('save')} type="submit" disabled={!saveTitle.trim()}><Icon name="floppy-disk" /> {t('save')}</button><button type="button" onClick={() => { setDialog(''); }}>{t('cancel')}</button>
      </form> : dialog === 'rename' ? <><label>{t('rename')}<input autoFocus maxLength={40} defaultValue={layer?.name} onKeyDown={e => { if (e.key === 'Enter') { const name = e.currentTarget.value.trim(); if (name) mutate(() => { layer.name = name; }); setDialog(''); } }} /></label><button onClick={() => setDialog('')}>{t('close')}</button></>
        : <><p>{t('confirm-discard')}</p><div><button onClick={discard}>{t('discard')}</button><button onClick={() => setDialog('')}>{t('cancel')}</button></div></>}
    </div></div>}
  </div>;
};
