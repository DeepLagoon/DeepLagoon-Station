import { useEffect, useMemo, useState } from 'react';
import { createPortal } from 'react-dom';
import { useBackend } from '../backend';
import './ShuttleParkingRepair.scss';

export const ShuttleParkingRepair = ({ repair, usable }) => {
  const { act } = useBackend();
  const [selected, setSelected] = useState(new Set());
  const [confirmation, setConfirmation] = useState('');
  const [sent, setSent] = useState(false);
  const [zoom, setZoom] = useState(1);
  const [pan, setPan] = useState({ x: 0, y: 0 });
  const [drag, setDrag] = useState(null);
  const [pendingQuote, setPendingQuote] = useState(null);
  const parts = repair?.parts || [];
  useEffect(() => { setSelected(new Set((repair?.selected || '').split('|').filter(Boolean))); setConfirmation(''); setPendingQuote(null); setZoom(1); setPan({ x: 0, y: 0 }); }, [repair?.ship]);
  useEffect(() => { if (repair?.working) { setSelected(new Set((repair.selected || '').split('|').filter(Boolean))); setConfirmation(''); } }, [repair?.working, repair?.selected]);
  useEffect(() => {
    setSent(false);
    const quoted = new Set((repair?.selected || '').split('|').filter(Boolean));
    if (pendingQuote && repair?.token && repair.token !== pendingQuote.token && pendingQuote.ids.length === quoted.size && pendingQuote.ids.every(id => quoted.has(id))) {
      setConfirmation('overview'); setPendingQuote(null);
    } else {
      setConfirmation('');
      if (pendingQuote && repair?.selectionVersion !== pendingQuote.version) setPendingQuote(null);
    }
  }, [repair?.token, repair?.selectionVersion]);
  const visuals = useMemo(() => new Map((repair?.visuals || []).map(v => [v.id, v])), [repair?.visuals]);
  if (!repair?.ship) return <p>Вставьте ID-карту с вашим шаттлом.</p>;
  const enabled = usable && repair.parked && !repair.working;
  const valid = new Set(parts.map(p => p.id));
  const chosen = new Set([...selected].filter(id => valid.has(id)));
  const quote = new Set((repair.selected || '').split('|').filter(Boolean));
  const quoteMatches = chosen.size === quote.size && [...chosen].every(id => quote.has(id));
  const toggle = (ids, on) => {
    setSelected(previous => { const next = new Set(previous); ids.forEach(id => on ? next.add(id) : next.delete(id)); return next; });
    setConfirmation('');
  };
  const ranges = (selection) => {
    const result = []; let first = -1, last = -1;
    parts.forEach((part, index) => {
      if (!selection.has(part.id)) return;
      if (index === last + 1 && first >= 0) last = index;
      else { if (first >= 0) result.push(first === last ? `${first}` : `${first}-${last}`); first = last = index; }
    });
    if (first >= 0) result.push(first === last ? `${first}` : `${first}-${last}`);
    return result.join(',');
  };
  const selectionPayload = (selection = chosen) => {
    const compact = ranges(selection);
    if (compact.length <= 6000) return { ranges: compact, selectionVersion: repair.selectionVersion };
    const bitmap = new Uint8Array(Math.ceil(parts.length / 8));
    parts.forEach((part, index) => { if (selection.has(part.id)) bitmap[index >> 3] |= 1 << (index & 7); });
    return { bits: btoa(String.fromCharCode(...bitmap)), selectionVersion: repair.selectionVersion };
  };
  const requestQuote = (selection) => {
    setConfirmation('');
    setPendingQuote({ token: repair.token, version: repair.selectionVersion, ids: [...selection] });
    act('repairQuote', selectionPayload(selection));
  };
  const groups = [['tiles', 'Палуба'], ['structures', 'Стены и конструкции'], ['equipment', 'Оборудование']];
  const all = [...(repair.current || []), ...parts];
  const bounds = all.reduce((b, p) => ({ minX: Math.min(b.minX, p.x), minY: Math.min(b.minY, p.y), maxX: Math.max(b.maxX, p.x), maxY: Math.max(b.maxY, p.y) }), all.length ? { minX: Infinity, minY: Infinity, maxX: -Infinity, maxY: -Infinity } : { minX: 0, minY: 0, maxX: 1, maxY: 1 });
  const { minX, minY, maxX, maxY } = { minX: bounds.minX - 1, minY: bounds.minY - 1, maxX: bounds.maxX + 1, maxY: bounds.maxY + 1 };
  const width = maxX - minX, height = maxY - minY;
  const render = (part, index, ghost) => {
    const geometry = part.geometry || (part.tile ? 'floor' : 'equipment');
    return <g key={`${ghost}:${part.id || index}`} className={`ParkingRepair__part ${geometry} ${ghost ? 'ghost' : ''} ${chosen.has(part.id) ? 'selected' : ''}`}
      transform={`translate(${part.x},${-part.y}) rotate(${-(part.rotation || 0) * 180 / Math.PI})`}
      onPointerDown={ghost && enabled ? e => e.stopPropagation() : undefined}
      onClick={ghost && enabled ? () => toggle([part.id], !chosen.has(part.id)) : undefined}>
      <title>{part.name || part.prototype}</title>
      {geometry === 'floor' && <rect x={-.5} y={-.5} width={1} height={1} />}
      {(geometry === 'wall' || geometry === 'door') && <rect x={-.335} y={-.335} width={.67} height={.67} />}
      {geometry === 'door' && <path d="M0,-.335 V.335" />}
      {geometry === 'equipment' && (visuals.get(part.visual)?.images || []).map((image, i) => {
        const w = image.native ? image.width / 32 : 1, h = image.native ? image.height / 32 : 1;
        return <image key={i} href={image.url} x={-w / 2} y={-h / 2} width={w} height={h}
          transform={image.native ? `matrix(${image.m11},${-image.m12},${-image.m21},${image.m22},${image.m31},${-image.m32})` : undefined} />;
      })}
      {ghost && <rect className="ParkingRepair__ghostOutline" x={-.5} y={-.5} width={1} height={1} />}
    </g>;
  };
  return <section className="ParkingRepair">
    {!repair.parked && <p>Для ремонта поставьте шаттл на бесплатную парковку. <button disabled={!usable} onClick={() => act('park')}>Поставить на парковку</button></p>}
    {repair.working && <p role="status">Шаттл ремонтируется · осталось {repair.remaining} с.</p>}
    <div className="ParkingRepair__layout">
      <div><div className="ShuttleParking__actions"><button onClick={() => setZoom(Math.max(.2, zoom / 1.5))}>−</button><button onClick={() => setZoom(Math.min(8, zoom * 1.5))}>+</button><button onClick={() => { setZoom(1); setPan({ x: 0, y: 0 }); }}>Центр</button></div>
        <div className="ParkingRepair__viewport" aria-label="Текущий повреждённый шаттл и призраки ремонта"><svg role="img" aria-label="Схема шаттла"
          viewBox={`${(minX + maxX) / 2 - width / zoom / 2 + pan.x} ${-(minY + maxY) / 2 - height / zoom / 2 + pan.y} ${width / zoom} ${height / zoom}`}
          onPointerDown={e => { if (e.button !== 0) return; e.currentTarget.setPointerCapture(e.pointerId); setDrag({ x: e.clientX, y: e.clientY }); }}
          onPointerMove={e => { if (!drag) return; const box = e.currentTarget.getBoundingClientRect(); setPan(p => ({ x: p.x - (e.clientX - drag.x) * width / zoom / box.width, y: p.y - (e.clientY - drag.y) * height / zoom / box.height })); setDrag({ x: e.clientX, y: e.clientY }); }}
          onPointerUp={() => setDrag(null)} onPointerCancel={() => setDrag(null)}>
          {[...(repair.current || [])].sort((a, b) => Number(b.tile) - Number(a.tile)).map((p, i) => render(p, i, false))}
          {[...parts].sort((a, b) => Number(b.tile) - Number(a.tile)).map((p, i) => render(p, i, true))}
        </svg></div></div>
      <aside>{groups.map(([kind, title]) => {
        const group = parts.filter(p => p.kind === kind);
        return group.length > 0 && <details key={kind} open><summary><label><input type="checkbox" disabled={!enabled}
          checked={group.every(p => chosen.has(p.id))} onChange={e => toggle(group.map(p => p.id), e.target.checked)} />{title} · {group.length}</label></summary>
          {group.map(part => <label key={part.id}><input type="checkbox" disabled={!enabled} checked={chosen.has(part.id)}
            onChange={e => toggle([part.id], e.target.checked)} />{part.name} · {part.x}, {part.y}</label>)}
        </details>;
      })}</aside>
    </div>
    <p>Выбрано: {chosen.size} / {parts.length}</p>
    <div className="ShuttleParking__actions"><button disabled={!enabled || parts.length === 0} onClick={() => { const all = new Set(parts.map(p => p.id)); setSelected(all); requestQuote(all); }}>Починить всё</button>
      <button disabled={!enabled || chosen.size === 0} onClick={() => requestQuote(chosen)}>Рассчитать выбранный ремонт</button></div>
    {repair.token && <section><p>Материалы по рынку: {repair.materials} кр. · Вместе с комиссией верфи: <b>{repair.credits} кр.</b></p>
      <p>Ремонт займёт {Math.floor(repair.seconds / 60)} мин. {repair.seconds % 60} с. · Мгновенно: <b>{repair.coins} LC</b></p>
      {!quoteMatches && <p>Выбор изменён. Рассчитайте новую котировку.</p>}
      <button disabled={!enabled || !quoteMatches || sent} onClick={() => setConfirmation('credits')}>Оплатить кредитами…</button>
      <button disabled={!enabled || !quoteMatches || sent} onClick={() => setConfirmation('coins')}>Мгновенно за LC…</button>
    </section>}
    {confirmation && repair.token && createPortal(<div className="ParkingRepair__modalBackdrop" onClick={() => setConfirmation('')}>
      <section role="dialog" aria-modal="true" aria-label="Подтверждение ремонта" className="ParkingRepair__modal" onClick={e => e.stopPropagation()}>
        <header><h2>Подтверждение ремонта</h2><button aria-label="Закрыть подтверждение" onClick={() => setConfirmation('')}>×</button></header>
        <p>Элементов к восстановлению: <b>{quote.size}</b></p>
        <p>Материалы: {repair.materials} кр.</p>
        <label><input type="radio" name="repair-payment" checked={confirmation === 'credits'} onChange={() => setConfirmation('credits')} />
          <span><b>{repair.credits} кр.</b> с комиссией верфи<br />{Math.floor(repair.seconds / 60)} мин. {repair.seconds % 60} с.</span></label>
        <label><input type="radio" name="repair-payment" checked={confirmation === 'coins'} onChange={() => setConfirmation('coins')} />
          <span><b>{repair.coins} LC</b><br />Мгновенный ремонт</span></label>
        {!quoteMatches && <p>Выбор изменён. Рассчитайте новую котировку.</p>}
        <footer><button disabled={!enabled || !quoteMatches || sent || confirmation === 'overview'} onClick={() => { setSent(true); setConfirmation(''); act('repairConfirm', { token: repair.token, coins: confirmation === 'coins' }); }}>Подтвердить ремонт</button>
          <button onClick={() => setConfirmation('')}>Отмена</button></footer>
      </section></div>, document.body)}
  </section>;
};
