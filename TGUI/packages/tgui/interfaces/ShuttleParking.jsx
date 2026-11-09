import { useEffect, useMemo, useRef, useState } from 'react';
import { useBackend } from '../backend';
import { EmbeddedSelect } from '../components/EmbeddedSelect';
import './ShuttleParking.scss';

const portLabels = {
  fits: 'Шаттл помещается',
  blocked: 'Шаттл не помещается',
  occupied: 'Порт занят',
};
const hullPath = (hull) => hull.map(({ points }) =>
  points.length ? `M${points.map(({ x, y }) => `${x},${y}`).join(' L')} Z` : '',
).join(' ');

const overview = { x: 0, y: 0, zoom: 1 };
const dragThreshold = 5;
const fitScale = (svg, width, height) => Math.min(svg.clientWidth / width, svg.clientHeight / height);

const RecallMap = ({ map, dock, setDock, disabled }) => {
  const svg = useRef(null);
  const drag = useRef(null);
  const [view, setView] = useState(overview);
  const hasPorts = !!map?.ports.length;
  const mapWidth = map?.width ?? 1;
  const mapHeight = map?.height ?? 1;
  const stationPath = useMemo(() => hullPath(map?.station ?? []), [map?.station]);
  const shipPath = useMemo(() => hullPath(map?.ship ?? []), [map?.ship]);

  useEffect(() => {
    if (!hasPorts) {
      drag.current = null;
      return;
    }
    const element = svg.current;
    const wheel = (event) => {
      if (!event.deltaY) return;
      event.preventDefault();
      if (drag.current) return;

      const fit = fitScale(element, mapWidth, mapHeight);
      if (fit <= 0) return;
      const rect = element.getBoundingClientRect();
      const x = (event.clientX - rect.left - rect.width / 2) / fit;
      const y = (event.clientY - rect.top - rect.height / 2) / fit;
      setView((previous) => {
        const zoom = Math.min(16, Math.max(1, previous.zoom * (event.deltaY < 0 ? 1.2 : 1 / 1.2)));
        const shift = 1 / previous.zoom - 1 / zoom;
        return { x: previous.x + x * shift, y: previous.y + y * shift, zoom };
      });
    };
    element.addEventListener('wheel', wheel, { passive: false });
    return () => element.removeEventListener('wheel', wheel);
  }, [hasPorts, mapWidth, mapHeight]);

  if (!hasPorts) {
    return <p>На карте станции нет доступных для выбора портов.</p>;
  }

  const startDrag = (event) => {
    if (event.button !== 0 || drag.current) return;
    drag.current = {
      pointer: event.pointerId,
      startX: event.clientX, startY: event.clientY,
      lastX: event.clientX, lastY: event.clientY,
      port: event.target.closest('[data-port]')?.getAttribute('data-port'),
      moved: false,
    };
    event.currentTarget.setPointerCapture(event.pointerId);
  };
  const moveDrag = (event) => {
    const gesture = drag.current;
    if (!gesture || gesture.pointer !== event.pointerId) return;
    if (!gesture.moved && Math.hypot(event.clientX - gesture.startX, event.clientY - gesture.startY) < dragThreshold) return;

    const fit = fitScale(event.currentTarget, mapWidth, mapHeight);
    if (fit <= 0) return;
    const dx = (event.clientX - gesture.lastX) / fit;
    const dy = (event.clientY - gesture.lastY) / fit;
    gesture.lastX = event.clientX;
    gesture.lastY = event.clientY;
    gesture.moved = true;
    setView((previous) => ({ x: previous.x - dx / previous.zoom, y: previous.y - dy / previous.zoom, zoom: previous.zoom }));
  };
  const finishDrag = (event, cancelled = false) => {
    const gesture = drag.current;
    if (!gesture || gesture.pointer !== event.pointerId) return;
    drag.current = null;
    if (!cancelled && !disabled && !gesture.moved && gesture.port
      && Math.hypot(event.clientX - gesture.startX, event.clientY - gesture.startY) < dragThreshold) {
      setDock(gesture.port);
    }
  };

  const port = map.ports.find((candidate) => candidate.id === dock);
  const width = mapWidth / view.zoom;
  const height = mapHeight / view.zoom;
  const left = map.x + (mapWidth - width) / 2 + view.x;
  const top = -map.y - (mapHeight + height) / 2 + view.y;
  const markerRadius = Math.max(0.6, Math.max(mapWidth, mapHeight) / 95) / view.zoom;
  return <div className="ShuttleParking__recall">
    <p>Колёсико меняет масштаб, перетаскивание перемещает схему, клик выбирает порт.</p>
    <svg ref={svg} className="ShuttleParking__map" viewBox={`${left} ${top} ${width} ${height}`}
      aria-label="Схема станции и стыковочные порты"
      onPointerDown={startDrag} onPointerMove={moveDrag} onPointerUp={finishDrag}
      onPointerCancel={(event) => finishDrag(event, true)} onLostPointerCapture={(event) => finishDrag(event, true)}>
      <g transform="scale(1 -1)">
        <path className="ShuttleParking__station" d={stationPath} />
        {port?.hasPose && <g transform={`translate(${port.shipX} ${port.shipY}) rotate(${port.shipAngle * 180 / Math.PI})`}>
          <path className={`ShuttleParking__ship ShuttleParking__port--${port.status}`} d={shipPath} />
        </g>}
        {map.ports.map((candidate) => <circle key={candidate.id} data-port={candidate.id} cx={candidate.x} cy={candidate.y} r={markerRadius}
          className={`ShuttleParking__port ShuttleParking__port--${candidate.status}`}
          role="button" tabIndex={disabled ? -1 : 0} aria-disabled={disabled}
          aria-label={`${candidate.name} · ${portLabels[candidate.status]}`} aria-pressed={candidate.id === dock}
          onClick={(event) => !disabled && event.detail === 0 && setDock(candidate.id)}
          onKeyDown={(event) => {
            if (!disabled && (event.key === 'Enter' || event.key === ' ')) {
              event.preventDefault();
              setDock(candidate.id);
            }
          }}>
          <title>{candidate.name} · {portLabels[candidate.status]}</title>
        </circle>)}
      </g>
    </svg>
    <button type="button" onClick={() => setView(overview)}>Вся станция</button>
    <label className="ShuttleParking__field">Стыковочный порт
      <EmbeddedSelect value={port?.id || ''} disabled={disabled} onChange={(event) => setDock(event.target.value)}>
        <option value="">Выберите стыковочный порт</option>
        {map.ports.map((candidate) => <option key={candidate.id} value={candidate.id}>
          {candidate.name} · {portLabels[candidate.status]}
        </option>)}
      </EmbeddedSelect>
    </label>
    <p className="ShuttleParking__legend">
      <span className="ShuttleParking__port--fits">Подходит</span>
      <span className="ShuttleParking__port--blocked">Не подходит</span>
      <span className="ShuttleParking__port--occupied">Занят</span>
    </p>
    {port && <p role="status">{portLabels[port.status]}</p>}
  </div>;
};

const Confirm = ({ label, question, disabled, onConfirm }) => {
  const [open, setOpen] = useState(false);
  const [sent, setSent] = useState(false);
  return <div className="ShuttleParking__confirm">
    <button disabled={disabled} onClick={() => { setSent(false); setOpen(true); }}>{label}</button>
    {open && <section><p>{question}</p><div className="ShuttleParking__actions">
      <button disabled={disabled || sent} onClick={() => { setSent(true); onConfirm(); setOpen(false); }}>Подтвердить</button>
      <button onClick={() => setOpen(false)}>Отмена</button>
    </div></section>}
  </div>;
};

export const ShuttleParking = () => {
  const { data, act } = useBackend();
  const [tab, setTab] = useState('parking');
  const [dock, setDock] = useState('');
  const [recallDock, setRecallDock] = useState('');
  const [checked, setChecked] = useState(false);
  const docks = data.docks || [];
  const places = data.places || [];
  const usable = data.id && !data.busy;
  const arrival = docks.some(d => d.id === dock) ? dock : (docks[0]?.id || '');
  const recallPort = data.recallMap?.ports.find((port) => port.id === recallDock);
  useEffect(() => setChecked(false), [data.reviewToken]);
  const dockControl = <label className="ShuttleParking__field">Шлюз для возврата
    <EmbeddedSelect value={arrival} disabled={!usable} onChange={event => setDock(event.target.value)} aria-label="Шлюз для возврата">
      {docks.length === 0 && <option value="">Нет свободного совместимого шлюза</option>}
      {docks.map(d => <option key={d.id} value={d.id}>{d.name}</option>)}
    </EmbeddedSelect></label>;
  return <div className="ShuttleParking">
    <div className="ShuttleParking__identity"><div><small>Пользователь · ID-карта</small><div>{data.user || 'Карта не вставлена'}</div></div>
      <button disabled={data.busy} onClick={() => act('id')}>{data.id ? 'Извлечь ID-карту' : 'Вставить ID-карту'}</button></div>
    <div className="ShuttleParking__balance">Кредиты: <b>{data.credits || 0} кр.</b><span>Lagoon Coin: <b>{data.coins || '…'} LC</b></span></div>
    <nav aria-label="Раздел парковки">{[['parking','Парковка'],['storage','Долгосрочное хранение'],['shop','Купить места']].map(([id,name]) =>
      <button key={id} aria-pressed={tab === id} onClick={() => setTab(id)}>{name}</button>)}</nav>
    {tab === 'parking' && <>
      <section><small>Собственный шаттл</small><h2>{data.shipName || 'Нет шаттла на ID-карте'}</h2>
        <div className="ShuttleParking__details"><div><small>Категория</small><b>{data.hasShip ? data.category : '—'}</b></div>
          <div><small>Владелец</small><span>{data.owner || '—'}</span></div><div><small>Состояние</small><span>{data.hasShip ? (data.parked ? 'На парковке · заморожен' : 'В секторе') : '—'}</span></div>
          <div><small>Стоимость парковки</small><b>Бесплатно</b></div></div>
        {data.parked && <RecallMap map={data.recallMap} dock={recallPort?.id || ''} setDock={setRecallDock} disabled={!usable} />}
        <div className="ShuttleParking__actions"><button className="ShuttleParking__primary" disabled={!usable || !data.hasShip || (data.parked && recallPort?.status !== 'fits')}
          onClick={() => data.parked ? act('recall', { dock: recallPort.id }) : act('park')}>{data.parked ? 'Вернуть бесплатно' : 'Поставить на парковку'}</button>
          <button disabled={!usable || !data.hasShip} onClick={() => setTab('storage')}>Сохранить надолго…</button></div>
      </section><p>Бесплатная парковка действует до конца смены. В конце смены шаттл продадут, а деньги поступят на ваш счёт. Для переноса в новую смену сохраните шаттл в долгосрочное место.</p>
    </>}
    {tab === 'storage' && <><p>{data.description}</p>{dockControl}
      {places.length === 0 && <p>У вас пока нет мест долгосрочного хранения.</p>}
      {places.map((place,i) => <section key={place.id}><div className="ShuttleParking__slot-title"><strong>{place.categoryName} · место {i+1}</strong><span>{place.name ? 'На диске' : 'Свободно'}</span></div>
        {place.name ? <><h3>{place.name}</h3><p>Категория шаттла: {place.shipCategory} · Владелец: {place.owner}</p><p>Выкуп: <b>{place.price} кр.</b></p>
          {data.hasShip && <p className="ShuttleParking__warning">На карте уже есть действующий шаттл. Сначала сохраните его.</p>}
          {place.paid && <p>Оплата уже учтена. Повторного списания не будет.</p>}
          <Confirm label={place.paid ? 'Получить оплаченный шаттл…' : place.recovering ? 'Завершить выкуп…' : 'Выкупить…'} question={place.paid ? 'Завершить оплаченный выкуп?' : `Выкупить за ${place.price} кр.? Управление будет привязано к вставленной ID-карте.`}
            disabled={!usable || data.hasShip || !arrival} onConfirm={() => act('redeem', { slot:place.id, dock:arrival })} />
        </> : <><p>Можно сохранить шаттл своей или меньшей категории.</p><button disabled={!usable || !data.hasShip || data.shipCategory > place.category}
          onClick={() => act('preview', { slot:place.id })}>{data.shipCategory > place.category ? 'Шаттл слишком большой' : 'Сохранить текущий шаттл…'}</button></>}
      </section>)}</>}
    {tab === 'shop' && <><p>Место приобретается навсегда. В каждом месте хранится один шаттл своей или меньшей категории. Дополнительные места той же категории стоят 50% цены с округлением вверх.</p>
      {(data.categories || []).map(category => <section key={category.id} className="ShuttleParking__shop-row"><div><strong>{category.name}</strong><small>Куплено мест: {category.count}</small>
        <div>Первое: {category.first} LC · следующее: {category.extra} LC</div></div>
        <Confirm label={`${category.count ? 'Ещё место' : 'Купить место'} · ${category.count ? category.extra : category.first} LC`}
          question={`Оплатить ${category.count ? category.extra : category.first} LC за место хранения?`} disabled={!usable}
          onConfirm={() => act('buy', { category:category.id, token:data.buyToken })} />
      </section>)}</>}
    {data.reviewToken && <section className="ShuttleParking__review"><h3>Проверка перед сохранением</h3><p>Эти предметы исключены из сохранения. Запрещённые будут проданы в вашу пользу, остальные останутся у консоли.</p>
      <ul>{(data.excluded || []).map((entry,i) => <li key={i}>{entry.name}</li>)}</ul>
      <p>Штатное оборудование сохранится. Шаттл будет убран из мира.</p>
      <label className="ShuttleParking__check"><input type="checkbox" checked={checked} onChange={e => setChecked(e.target.checked)} />Я проверил список</label>
      <div className="ShuttleParking__actions"><button disabled={!usable || !checked} onClick={() => { setChecked(false); act('save', { slot:data.reviewSlot, token:data.reviewToken }); }}>Сохранить шаттл</button><button onClick={() => act('cancel')}>Отмена</button></div>
    </section>}
    <footer><span role="status" aria-live="polite">{data.message}</span><button disabled={data.busy} onClick={() => act('refresh')}>Обновить</button></footer>
  </div>;
};
