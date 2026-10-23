import { useInterfaceStyleGuard } from './useInterfaceStyleGuard';
import { useEffect, useRef, useState } from 'react';
import { useBackend } from '../backend';
import { ColorSquare } from '../components/PlayerColorPicker';
import { simplifyPoints } from './tattooStrokeUtils';
import { makeCanvas, makeLayer, paint, flatten, cloneLayers, loadBitmap, encodeBitmap, strokeWork } from './tattooBitmapUtils';
import { initialView, canvasPoint, zoomAt } from './tattooViewportUtils';
import './TattooStudio.scss';
import { TattooSelect } from './TattooSelect';

const views = ['Спереди', 'Сзади', 'Справа', 'Слева'];
const clamp = (v, a, b) => Math.max(a, Math.min(b, v));
const defaults = { x: .5, y: .5, scale: 1, rotation: 0, opacity: 1 };
const decode = value => value.split(';').map(p => p.split(',').map(Number));

export const TattooEditor = ({ data = {}, act, active: editorActive = true }) => {
  const tattoos = data.tattoos || [], parts = data.parts || [];
  const [selected, setSelected] = useState(tattoos.at(-1)?.id || '');
  const [part, setPart] = useState(tattoos.at(-1)?.part || parts[0]?.id || 'Chest');
  const [direction, setDirection] = useState(tattoos.at(-1)?.direction || 0);
  const [color, setColor] = useState('#182030'), [width, setWidth] = useState(6), [glow, setGlow] = useState(false);
  const [mode, setMode] = useState('brush'), [transform, setTransform] = useState(defaults);
  const [draft, setDraft] = useState(null), [version, refresh] = useState(0), [status, setStatus] = useState('');
  const [partSwitch, setPartSwitch] = useState(null);
  const pendingPart = useRef(null);
  const [editing, setEditing] = useState(false), [loading, setLoading] = useState(false), [saving, setSaving] = useState(false);
  const [view, setView] = useState(initialView);
  const viewRef = useRef(view); viewRef.current = view;
  const opened = useRef(false), autoEditId = useRef('');
  const canvas = useRef(null), mask = useRef(null), bodyPreview = useRef(null), gesture = useRef(null), expectedNew = useRef(false);
  const work = useRef(null), images = useRef(new Map()), mounted = useRef(true);
  const tattoo = tattoos.find(t => t.id === selected && t.part === part && t.direction === direction);
  const currentTattooId = useRef(tattoo?.id); currentTattooId.current = tattoo?.id;
  const body = parts.find(p => p.id === part);
  useInterfaceStyleGuard(editorActive && (editing || loading || saving || data.pending || data.fileBusy));
  const blocked = !!data.disabled || !!data.pending || !!data.fileBusy || loading || saving || !!(data.machine && body?.covered);
  const image = body?.frames?.find(f => f.direction === direction)?.image;
  const previewImage = body?.frames?.find(f => f.direction === direction)?.previewImage;
  const transformKey = tattoo ? [tattoo.id, tattoo.x, tattoo.y, tattoo.scale, tattoo.rotation, tattoo.opacity].join('/') : '';
  const update = () => refresh(v => v + 1);
  useEffect(() => {
    if (work.current && !tattoos.some(t => t.id === work.current.id)) {
      work.current = null; gesture.current = null; setDraft(null); setEditing(false); setSaving(false); setStatus('');
    }
  }, [tattoos]);
  useEffect(() => { mounted.current = true; return () => { mounted.current = false; work.current = null; }; }, []);
  useEffect(() => {
    if (!saving) return;
    const timer = setTimeout(() => { setSaving(false); setStatus('Не получено подтверждение. Повторите завершение рисунка.'); }, 30000);
    return () => clearTimeout(timer);
  }, [saving]);
  useEffect(() => { setTransform(tattoo ? { x: tattoo.x, y: tattoo.y, scale: tattoo.scale, rotation: tattoo.rotation, opacity: tattoo.opacity } : defaults); }, [transformKey]);
  useEffect(() => {
    if (!expectedNew.current || !tattoos.length) return;
    const newest = tattoos.at(-1); expectedNew.current = false;
    setSelected(newest.id); setPart(newest.part); setDirection(newest.direction);
    act('tattoo-preview-direction', { direction: newest.direction });
  }, [tattoos.length]);
  useEffect(() => {
    let active = true; mask.current = null; bodyPreview.current = null;
    if (!image) { update(); return; }
    const img = new Image(); img.onload = () => { if (active) { mask.current = img; update(); } }; img.src = image;
    if (previewImage) { const preview = new Image(); preview.onload = () => { if (active) { bodyPreview.current = preview; update(); } }; preview.src = previewImage; }
    return () => { active = false; };
  }, [image, previewImage]);
  useEffect(() => {
    if (saving && tattoo && tattoo.strokes.length === 0 && tattoo.artworkProof && tattoo.artworkProof !== work.current?.savedProof) {
      work.current = null; setSaving(false); setEditing(false); setStatus('Рисунок завершён. Слои и история объединены.');
      if (pendingPart.current) { const next = pendingPart.current; pendingPart.current = null; setPartSwitch(null); switchPart(next); }
    } else if (saving && data.status && !data.pending && !data.fileBusy) { setSaving(false); setStatus(data.status); }
  }, [tattoos, data.pending, data.fileBusy, data.status, saving]);

  const bitmap = png => {
    if (!png) return null;
    if (!images.current.has(png)) {
      const img = new Image(); images.current.set(png, img); img.onload = () => mounted.current && update(); img.src = 'data:image/png;base64,' + png;
      // Cache only the current artwork, not every previous edit.
      if (images.current.size > 32) images.current.delete(images.current.keys().next().value);
    }
    const img = images.current.get(png); return img?.complete && img.naturalWidth ? img : null;
  };
  useEffect(() => {
    const ctx = canvas.current?.getContext('2d'); if (!ctx) return;
    ctx.clearRect(0, 0, 512, 512); if (!mask.current) return;
    ctx.save(); ctx.translate(256 + view.x * 512, 256 + view.y * 512);
    ctx.scale(view.zoom, view.zoom); ctx.translate(-256, -256);
    const skin = makeCanvas(), skinCtx = skin.getContext('2d'); skinCtx.imageSmoothingEnabled = false;
    if (bodyPreview.current) skinCtx.drawImage(bodyPreview.current, 0, 0, 256, 256);
    else {
      skinCtx.drawImage(mask.current, 0, 0, 256, 256); skinCtx.globalCompositeOperation = 'source-atop';
      skinCtx.fillStyle = body?.color || '#bc9579'; skinCtx.fillRect(0, 0, 256, 256);
    }
    ctx.imageSmoothingEnabled = false; ctx.drawImage(skin, 0, 0, 512, 512);
    for (const t of tattoos.filter(t => t.part === part && t.direction === direction)) {
      const p = t.id === selected ? transform : t;
      let channels;
      if (work.current?.id === t.id) channels = flatten(work.current.layers, work.current.active, draft);
      else {
        channels = [makeCanvas(), makeCanvas()];
        [t.normalBitmap, t.glowBitmap].forEach((png, i) => { const img = bitmap(png); if (img) channels[i].getContext('2d').drawImage(img, 0, 0); });
        (t.strokes || []).forEach(s => channels.forEach((c, i) => paint(c.getContext('2d'), { ...s, points: decode(s.points) }, !!i)));
      }
      for (const ink of channels) {
        const placed = document.createElement('canvas'); placed.width = placed.height = 512;
        const c = placed.getContext('2d'); c.globalAlpha = p.opacity; c.translate(p.x * 512, p.y * 512);
        c.rotate(p.rotation * Math.PI / 180); c.scale(p.scale * (t.mirrored ? -1 : 1), p.scale);
        c.drawImage(ink, -256, -256, 512, 512); c.resetTransform(); c.globalAlpha = 1;
        c.globalCompositeOperation = 'destination-in'; c.imageSmoothingEnabled = false; c.drawImage(mask.current, 0, 0, 512, 512);
        ctx.imageSmoothingEnabled = true; ctx.drawImage(placed, 0, 0);
      }
    }
    ctx.restore();
  }, [tattoos, part, direction, selected, transform, draft, version, body?.color, view]);

  const switchPart = value => {
    work.current = null; gesture.current = null; setDraft(null); setEditing(false); autoEditId.current = '';
    setPart(value); setSelected(tattoos.find(t => t.part === value && t.direction === direction)?.id || '');
  };
  const requestPart = value => {
    if (value === part) return;
    const changed = editing && (work.current?.undo.length > 0 || ['x', 'y', 'scale', 'rotation', 'opacity'].some(key => transform[key] !== tattoo?.[key]));
    if (changed) { setPartSwitch(value); return; }
    if (editing && tattoo && !(tattoo.strokes || []).length && !tattoo.normalBitmap && !tattoo.glowBitmap) act('tattoo-remove', { id: tattoo.id });
    switchPart(value);
  };
  const choose = t => { if (editing || blocked) return; setSelected(t.id); setPart(t.part); setDirection(t.direction); act('tattoo-preview-direction', { direction: t.direction }); };
  const startEditing = async () => {
    if (!tattoo || blocked || editing) return;
    setLoading(true); setStatus(''); const id = tattoo.id;
    try {
      const layer = makeLayer('Исходный рисунок');
      layer.useBase = true;
      await Promise.all([loadBitmap(layer.normal, tattoo.normalBitmap), loadBitmap(layer.glow, tattoo.glowBitmap)]);
      (tattoo.strokes || []).forEach(s => [layer.normal, layer.glow].forEach((c, i) => paint(c.getContext('2d'), { ...s, points: decode(s.points) }, !!i)));
      if (!mounted.current || currentTattooId.current !== id) return;
      work.current = { id, layers: [layer], active: 0, undo: [], redo: [] }; setMode('brush'); setEditing(true); update();
    } catch { setStatus('Не удалось открыть рисунок.'); }
    finally { if (mounted.current) setLoading(false); }
  };
  useEffect(() => {
    if (!editorActive) { opened.current = false; autoEditId.current = ''; return; }
    if (blocked || !body || !image) return;
    if (!opened.current) {
      opened.current = true;
      if (!tattoo && tattoos.length === 0 && tattoos.length < (data.maxTattoos || 8)) {
        expectedNew.current = true; act('tattoo-add', { part, direction }); return;
      }
    }
    if (tattoo && !editing && autoEditId.current !== tattoo.id) {
      autoEditId.current = tattoo.id; startEditing();
    }
  }, [editorActive, blocked, tattoo?.id, editing, image, body?.id]);
  useEffect(() => {
    const element = canvas.current;
    const wheel = event => {
      event.preventDefault();
      if (gesture.current) return;
      const bounds = element.getBoundingClientRect();
      const point = [(event.clientX - bounds.left) / bounds.width, (event.clientY - bounds.top) / bounds.height];
      const delta = event.deltaY * (event.deltaMode === 1 ? 16 : event.deltaMode === 2 ? 512 : 1);
      setView(current => zoomAt(current, point, Math.exp(-delta * .0015)));
    };
    element.addEventListener('wheel', wheel, { passive: false });
    return () => element.removeEventListener('wheel', wheel);
  }, []);
  const checkpoint = () => {
    const w = work.current; w.undo.push({ layers: cloneLayers(w.layers), active: w.active });
    if (w.undo.length > 16) w.undo.shift(); w.redo = [];
  };
  const undo = redo => {
    const w = work.current, from = redo ? w.redo : w.undo, to = redo ? w.undo : w.redo;
    if (!from.length) return; to.push({ layers: cloneLayers(w.layers), active: w.active });
    const state = from.pop(); w.layers = state.layers; w.active = state.active; update();
  };
  const finish = () => {
    if (!work.current || blocked || gesture.current) return;
    const [normal, luminous] = flatten(work.current.layers);
    const n = encodeBitmap(normal), g = encodeBitmap(luminous);
    if (n.length > (data.maxBitmapChars || 131072) || g.length > (data.maxBitmapChars || 131072) || n.length + g.length > (data.maxTattooBitmapChars || 262144)) {
      setStatus('Изображение превышает допустимый объём. Упростите рисунок.'); return;
    }
    const recipe = JSON.stringify(work.current.layers.map(({ useBase, visible, opacity, strokes }) => ({ useBase, visible, opacity, strokes })));
    if (recipe.length > 240000 || work.current.layers.reduce((sum, l) => sum + l.strokes.reduce((n, s) => n + s.points.length, 0), 0) > 16384) {
      setStatus('Слишком много штрихов. Отмените последние штрихи, завершите рисунок и продолжите редактирование.'); return;
    }
    work.current.savedProof = tattoo.artworkProof; setSaving(true); setStatus('Запекание…');
    const payload = JSON.stringify({ id: tattoo.id, recipe, ...transform });
    const count = Math.ceil(payload.length / 4000);
    for (let index = 0; index < count; index++) act('tattoo-bake-chunk', { id: tattoo.id, index, count, text: payload.slice(index * 4000, (index + 1) * 4000) });
  };
  const commit = value => { if (tattoo && !blocked && !editing) act('tattoo-transform', { id: tattoo.id, ...value }); };
  const screenPosition = e => { const b = e.currentTarget.getBoundingClientRect(); return [(e.clientX - b.left) / b.width, (e.clientY - b.top) / b.height]; };
  const position = e => canvasPoint(screenPosition(e), viewRef.current);
  const designPoint = p => {
    const angle = -transform.rotation * Math.PI / 180, x = p[0] - transform.x, y = p[1] - transform.y;
    return [clamp((x * Math.cos(angle) - y * Math.sin(angle)) / transform.scale * (tattoo.mirrored ? -1 : 1) + .5, 0, 1),
      clamp((x * Math.sin(angle) + y * Math.cos(angle)) / transform.scale + .5, 0, 1)];
  };
  const begin = e => {
    if (!mask.current) return;
    if (e.button === 1 || e.button === 2 || e.altKey) {
      e.preventDefault(); e.currentTarget.setPointerCapture(e.pointerId);
      gesture.current = { pointerId: e.pointerId, kind: 'pan', start: screenPosition(e), view: viewRef.current }; return;
    }
    if (!tattoo || blocked || e.button !== 0 || (mode !== 'move' && !editing)) return;
    const layer = work.current?.layers[work.current.active]; if (editing && !layer?.visible) return;
    e.preventDefault(); e.currentTarget.setPointerCapture(e.pointerId); const p = position(e);
    gesture.current = { pointerId: e.pointerId, kind: mode === 'move' ? 'move' : 'stroke', start: p, transform, points: [designPoint(p)] };
    if (editing && mode !== 'move') setDraft({ color: color + 'FF', width: width / 512, glow, erase: mode === 'erase', points: gesture.current.points });
  };
  const move = e => {
    const g = gesture.current; if (!g || g.pointerId !== e.pointerId) return;
    if (g.kind === 'pan') {
      const p = screenPosition(e);
      setView({ ...g.view, x: g.view.x + p[0] - g.start[0], y: g.view.y + p[1] - g.start[1] }); return;
    }
    const p = position(e);
    if (g.kind === 'move') setTransform({ ...g.transform, x: clamp(g.transform.x + p[0] - g.start[0], -1, 2), y: clamp(g.transform.y + p[1] - g.start[1], -1, 2) });
    else {
      const point = designPoint(p), last = g.points.at(-1); if (Math.hypot(point[0] - last[0], point[1] - last[1]) < .002) return;
      if (g.points.length >= 2048) g.points = simplifyPoints(g.points, 1024); g.points.push(point);
      setDraft({ color: color + 'FF', width: width / 512, glow, erase: mode === 'erase', points: [...g.points] });
    }
  };
  const end = (e, cancelled = false) => {
    const g = gesture.current; if (!g || g.pointerId !== e.pointerId) return; gesture.current = null;
    if (e.currentTarget.hasPointerCapture(e.pointerId)) e.currentTarget.releasePointerCapture(e.pointerId);
    if (!cancelled) {
      if (g.kind === 'move') commit(transform);
      else if (g.kind === 'stroke') { const layer = work.current.layers[work.current.active];
        const stroke = { color: color + 'FF', width: width / 512, glow, erase: mode === 'erase', points: g.points };
        const candidate = work.current.layers.map(l => ({ useBase: l.useBase, visible: l.visible, opacity: l.opacity,
          strokes: l === layer ? [...l.strokes, stroke] : l.strokes }));
        const strokes = candidate.flatMap(l => l.strokes);
        const paintWork = candidate.filter(l => l.visible).reduce((sum, l) => sum
          + l.strokes.reduce((n, s) => n + strokeWork(s), 0)
          + (l.useBase ? (tattoo.strokes || []).reduce((n, s) => n + strokeWork({ ...s, points: decode(s.points) }), 0) : 0), 0);
        if (strokes.length > 1024 || strokes.reduce((sum, s) => sum + s.points.length, 0) > 16384
            || JSON.stringify(candidate).length > 240000 || paintWork > 7900000) {
          setStatus('Завершите текущий рисунок, затем продолжите редактирование. Последний штрих не добавлен.'); setDraft(null); return;
        }
        checkpoint();
        layer.strokes.push(stroke);
        [layer.normal, layer.glow].forEach((c, i) => paint(c.getContext('2d'), stroke, !!i)); update(); }
    }
    setDraft(null);
  };
  const w = editing ? work.current : null, active = w?.layers[w.active];
  const changeLayer = callback => { checkpoint(); callback(); update(); };
  return <div className="TattooStudio">
    <div className="TattooStudio__locations">
      <label>Область <TattooSelect label="Область татуировки" disabled={!!data.disabled || !!data.pending || !!data.fileBusy || loading || saving} value={part}
        options={parts.map(p => ({ value: p.id, label: p.name + (data.machine && p.covered ? ' — закрыта' : '') }))}
        onChange={requestPart} /></label>
      <div className="TattooStudio__views">{views.map((v, i) => <button type="button" key={v} disabled={editing || blocked} aria-pressed={direction === i} onClick={() => { setDirection(i); setSelected(tattoos.find(t => t.part === part && t.direction === i)?.id || ''); act('tattoo-preview-direction', { direction: i }); }}>{v}</button>)}</div>
      <button type="button" disabled={editing || blocked || !body || tattoos.length >= (data.maxTattoos || 8)} onClick={() => { expectedNew.current = true; act('tattoo-add', { part, direction }); }}>Новая татуировка</button>
    </div>
    <div className="TattooStudio__list">{tattoos.map((t, i) => <button type="button" key={t.id} disabled={editing || blocked} aria-pressed={selected === t.id} onClick={() => choose(t)}>{i + 1}. {t.name} · {views[t.direction]}</button>)}</div>
    <div className="TattooStudio__tools">
      <button type="button" disabled={!tattoo || editing || loading || saving || data.pending || data.fileBusy} onClick={() => act('tattoo-export', { id: tattoo.id })}>Сохранить PNG</button>
      <button type="button" disabled={!tattoo || editing || blocked} onClick={() => act('tattoo-import', { id: tattoo.id })}>Загрузить PNG</button>
      {!editing ? <button type="button" disabled={!tattoo || blocked} onClick={startEditing}>Редактировать рисунок</button> : <>
        <button type="button" disabled={blocked} onClick={finish}>Завершить рисунок</button>
        <button type="button" disabled={blocked} onClick={() => { work.current = null; setEditing(false); setDraft(null); gesture.current = null;
          setTransform({ x: tattoo.x, y: tattoo.y, scale: tattoo.scale, rotation: tattoo.rotation, opacity: tattoo.opacity }); update(); }}>Отбросить изменения</button>
      </>}
      {[['brush', 'Кисть'], ['erase', 'Ластик'], ['move', 'Переместить']].map(([tool, title]) => <button type="button" key={tool} disabled={blocked || !tattoo || (tool !== 'move' && !editing)} aria-pressed={mode === tool} onClick={() => setMode(tool)}>{title}</button>)}
      <ColorSquare label="Цвет чернил" value={color} onChange={setColor} />
      <label>Кисть <input aria-label="Толщина кисти" type="range" min="2" max="64" value={width} onChange={e => setWidth(Number(e.target.value))} /> {width}</label>
      <label><input type="checkbox" checked={glow} onChange={e => setGlow(e.target.checked)} /> Светящиеся чернила</label>
      <button type="button" disabled={blocked || !w?.undo.length} onClick={() => undo(false)}>Отменить</button>
      <button type="button" disabled={blocked || !w?.redo.length} onClick={() => undo(true)}>Повторить</button>
    </div>
    <div className="TattooStudio__importWarning">
      <small>{data.externalPngAllowed ? 'Импорт сторонних PNG доступен: третий тир доната.' : 'Импорт сторонних PNG доступен с третьего тира доната. Собственные PNG, подписанные сервером для вашего аккаунта, можно загрузить без доната.'}</small>
      <p>Не смотря на неограниченные возможности импорта татуировок, они должны соблюдать атмосферу и логику игры. За нарушение атмосферы вы будете наказаны</p>
    </div>
    <div className="TattooStudio__workspace">
      <div className="TattooStudio__drawing">
        <div className="TattooStudio__viewportTools"><span>Приближение: {Math.round(view.zoom * 100)}%</span><button type="button" onClick={() => setView(initialView)}>Сбросить вид</button></div>
        <canvas ref={canvas} width="512" height="512" aria-label="Рисование татуировки на выбранной части тела" className={blocked ? 'TattooStudio__canvas TattooStudio__canvas--blocked' : 'TattooStudio__canvas'}
          onContextMenu={event => event.preventDefault()} onPointerDown={begin} onPointerMove={move} onPointerUp={end} onPointerCancel={e => end(e, true)} />
        <small>Колёсико — приближение. Средняя/правая кнопка или Alt + перетаскивание — перемещение вида. «Переместить» — перенос рисунка.</small>
      </div>
      <div className="TattooStudio__placement">
        <p>{editing ? 'Слои и история существуют только до завершения рисунка. Завершите рисунок перед сохранением персонажа или закрытием окна.' : tattoo ? 'Редактируйте рисунок или загрузите PNG вместо него. Экспорт сохраняет оба вида чернил; размещение на теле настраивается отдельно.' : 'Создайте татуировку на выбранной области и ракурсе, затем рисуйте или загрузите PNG.'}</p>
        {w && <fieldset disabled={blocked}>
          <label>Слои <TattooSelect label="Слой рисунка" disabled={blocked} value={w.active}
            options={w.layers.map((layer, index) => ({ value: index, label: `${index + 1}. ${layer.name}` }))}
            onChange={value => { w.active = Number(value); update(); }} /></label>
          <input aria-label="Название слоя" value={active.name} maxLength={40} onChange={e => { active.name = e.target.value; update(); }} />
          <label><input aria-label="Видимость слоя" type="checkbox" checked={active.visible} onChange={e => changeLayer(() => { active.visible = e.target.checked; })} /> Видимый слой</label>
          <label>Непрозрачность слоя <input aria-label="Непрозрачность слоя" type="range" min="0" max="1" step=".01" value={active.opacity} onChange={e => { active.opacity = Number(e.target.value); update(); }} /></label>
          <button type="button" disabled={w.layers.length >= (data.maxLayers || 4)} onClick={() => changeLayer(() => { w.layers.push(makeLayer('Слой ' + (w.layers.length + 1))); w.active = w.layers.length - 1; })}>Добавить слой</button>
          <button type="button" disabled={w.layers.length === 1} onClick={() => changeLayer(() => { w.layers.splice(w.active, 1); w.active = Math.min(w.active, w.layers.length - 1); })}>Удалить слой</button>
          {[[-1, 'Слой ниже'], [1, 'Слой выше']].map(([offset, title]) => <button type="button" key={title} disabled={w.active + offset < 0 || w.active + offset >= w.layers.length} onClick={() => changeLayer(() => { const target = w.active + offset; [w.layers[w.active], w.layers[target]] = [w.layers[target], w.layers[w.active]]; w.active = target; })}>{title}</button>)}
          <small>Без лимита мазков. Отмена: последние 16 действий текущей сессии.</small>
        </fieldset>}
        <fieldset disabled={blocked || !tattoo}>
          {[['scale', 'Масштаб', .05, 4, .01], ['rotation', 'Поворот', -180, 180, 1], ['opacity', 'Непрозрачность чернил', 0, 1, .01]].map(([field, title, min, max, step]) => <label key={field}>{title}: {Math.round(transform[field] * (field === 'rotation' ? 1 : 100))}{field === 'rotation' ? '°' : '%'}
            <input aria-label={title} type="range" min={min} max={max} step={step} value={transform[field]} onChange={e => setTransform(p => ({ ...p, [field]: Number(e.target.value) }))} onPointerUp={() => commit(transform)} onKeyUp={() => commit(transform)} onBlur={() => commit(transform)} /></label>)}
          <button type="button" onClick={() => { setTransform(defaults); commit(defaults); }}>Сбросить размещение</button>
          <button type="button" disabled={editing} onClick={() => act('tattoo-mirror', { id: tattoo.id })}>Отразить по горизонтали</button>
          <button type="button" disabled={editing || tattoos.length >= (data.maxTattoos || 8)} onClick={() => { expectedNew.current = true; act('tattoo-copy', { id: tattoo.id, direction: (direction + 1) % 4 }); }}>Копировать на следующий ракурс</button>
          <button type="button" disabled={editing} onClick={() => act('tattoo-remove', { id: tattoo.id })}>Удалить татуировку</button>
        </fieldset>
        <small>Свечение видно в темноте. Одежда перекрывает оба вида чернил.</small>
        {(data.status || status || blocked) && <p role="status">{data.disabled ? 'Нанесение татуировок выключено в настройках.' : data.status || status || (data.pending ? 'Нанесение…' : 'Область закрыта одеждой.')}</p>}
      </div>
    </div>
    {partSwitch && <div className="TattooStudio__switch" role="alertdialog" aria-label="Смена области татуировки"><div>
      <p>В рисунке есть несохранённые изменения. Сохранить их перед сменой области?</p>
      <button disabled={saving || blocked} onClick={() => { pendingPart.current = partSwitch; finish(); }}>Сохранить и перейти</button>
      <button disabled={saving} onClick={() => { const next = partSwitch; pendingPart.current = null; setPartSwitch(null); switchPart(next); }}>Отбросить изменения</button>
      <button disabled={saving || blocked} onClick={() => { pendingPart.current = null; setPartSwitch(null); }}>Остаться</button>
    </div></div>}
  </div>;
};
export const TattooStudio = () => { const { data, act } = useBackend(); return <div className="TattooStudioPage"><TattooEditor data={data} act={act} /></div>; };
