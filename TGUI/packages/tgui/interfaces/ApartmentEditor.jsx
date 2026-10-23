import { useInterfaceStyleGuard } from './useInterfaceStyleGuard';
import { useEffect, useRef, useState } from 'react';
import { useBackend } from '../backend';
import { copyLayout, validateLayout } from './apartmentLayout.mjs';
import './ApartmentEditor.scss';
import { useApartmentViewport } from './useApartmentViewport';
import { NavMapPlan } from './NavMapPlan.jsx';

const Sprite = ({ images = [] }) => <span className="ApartmentEditor__sprite">
  {images.map((image, i) => <img key={i} src={image.url} alt="" draggable={false} style={{ filter: image.color && image.color !== '#ffffff' ? `drop-shadow(0 0 0 ${image.color})` : undefined }} />)}
</span>;

const PlanSprite = ({ images = [] }) => <span className="ApartmentEditor__planSprite">
  {images.map((image, index) => <img key={index} src={image.url} alt="" draggable={false} style={image.native ? {
    width: `${image.width / 32 * 100}%`, height: `${image.height / 32 * 100}%`,
    left: `${50 + image.m31 * 100}%`, top: `${50 - image.m32 * 100}%`,
    transform: `translate(-50%, -50%) matrix(${image.m11}, ${-image.m12}, ${-image.m21}, ${image.m22}, 0, 0)`,
  } : undefined} />)}
</span>;

export const ApartmentEditor = () => {
  const { data, act } = useBackend();
  const [draft, setDraft] = useState(data.layout || []);
  const [selected, setSelected] = useState(null);
  const [brush, setBrush] = useState(null);
  const [hover, setHover] = useState(null);
  const [before, setBefore] = useState(false);
  const [undo, setUndo] = useState([]);
  const [redo, setRedo] = useState([]);
  const [pending, setPending] = useState(false);
  const [confirmClose, setConfirmClose] = useState(false);
  const plan = useRef(null);
  const viewport = useRef(null);
  const { view, zoom, cellSize, panning, fit, setScale, events: viewportEvents } = useApartmentViewport(viewport, data.width, data.height, data.token);
  const [gridLines, setGridLines] = useState(true);
  const drag = useRef(null);
  const current = useRef(draft);
  const counter = useRef(0);
  const room = { ...data, catalog: data.catalog || [] };
  const originX = data.scene?.x || 0, originY = data.scene?.y || 0;
  const floors = new Map((data.scene?.tiles || []).map(tile => [`${tile.x},${tile.y}`, tile]));
  const fixed = new Set((data.scene?.occupied || []).map(cell => `${cell.x},${cell.y}`));
  const addedCount = draft.filter(item => !room.catalog.find(def => def.id === item.furniture)?.existingOnly).length;
  const errors = validateLayout(draft, room);
  const valid = !Object.keys(errors).length;
  const dirty = JSON.stringify(draft) !== JSON.stringify(data.layout || []);
  useInterfaceStyleGuard(dirty || pending);
  const selectedItem = draft.find((item) => item.id === selected);
  const definition = room.catalog.find((item) => item.id === selectedItem?.furniture);

  useEffect(() => {
    const next = copyLayout(data.layout || []);
    current.current = next; setDraft(next); setUndo([]); setRedo([]);
    setSelected(null); setBrush(null); setHover(null); setBefore(false); setPending(false);
  }, [data.token]);
  useEffect(() => { setPending(false); }, [data.status, data.success]);
  useEffect(() => {
    act(before ? 'before' : 'after');
    if (!before) act('preview', { layout: hover && brush ? [...draft, hover] : draft });
  }, [draft, hover, before]);

  const update = (next) => { current.current = next; setDraft(next); };
  const remember = () => {
    // Capture now: React may run the state updater after update() changes the ref.
    const previous = copyLayout(current.current);
    setUndo((history) => [...history.slice(-49), previous]); setRedo([]);
  };
  const change = (next) => { remember(); update(next); };
  const cellAt = (event) => {
    const rect = plan.current.getBoundingClientRect();
    return { x: originX + Math.floor((event.clientX - rect.left) / rect.width * data.width), y: originY + data.height - 1 - Math.floor((event.clientY - rect.top) / rect.height * data.height) };
  };
  const pointerDown = (event) => {
    if (before || pending || event.button !== 0) return;
    event.preventDefault(); // Keep DOM focus on the plan after a click on a ghost.
    act('focus'); // The native host must also hand keyboard input to this CEF view.
    const cell = cellAt(event);
    const id = event.target.closest('[data-placement]')?.dataset.placement;
    plan.current.focus();
    if (brush) {
      const item = { id: `added_${Date.now()}_${counter.current++}`, furniture: brush, ...cell, rotation: hover?.rotation || 0 };
      change([...current.current, item]); setSelected(item.id); setBrush(null); setHover(null);
    } else if (id) {
      setSelected(id);
      const item = current.current.find((entry) => entry.id === id);
      drag.current = { id, x: cell.x - item.x, y: cell.y - item.y, original: copyLayout(current.current), moved: false };
      plan.current.setPointerCapture(event.pointerId);
    } else if (selected) {
      // A second click also moves the selected object, without requiring a held mouse button.
      change(current.current.map((item) => item.id === selected ? { ...item, ...cell } : item));
    }
  };
  const pointerMove = (event) => {
    if (before || pending) return;
    const cell = cellAt(event);
    if (brush) setHover({ id: '__hover', furniture: brush, ...cell, rotation: hover?.rotation || 0 });
    if (!drag.current) return;
    const gesture = drag.current;
    const x = cell.x - gesture.x, y = cell.y - gesture.y;
    const old = current.current.find((item) => item.id === gesture.id);
    if (old.x === x && old.y === y) return;
    if (!gesture.moved) { setUndo((history) => [...history.slice(-49), gesture.original]); setRedo([]); gesture.moved = true; }
    update(current.current.map((item) => item.id === gesture.id ? { ...item, x, y } : item));
  };
  const rotate = () => {
    if ((brush ? room.catalog.find(item => item.id === brush) : definition)?.canRotate === false) return;
    if (hover && brush) setHover({ ...hover, rotation: (hover.rotation + 1) % 4 });
    else if (selectedItem) change(draft.map((item) => item.id === selected ? { ...item, rotation: (item.rotation + 1) % 4 } : item));
  };
  const remove = () => { if (selectedItem && !definition?.existingOnly) { change(draft.filter((item) => item.id !== selected)); setSelected(null); } };
  const stepBack = () => {
    if (!undo.length) return;
    setRedo((history) => [...history, copyLayout(draft)]); update(copyLayout(undo[undo.length - 1])); setUndo(undo.slice(0, -1));
  };
  const stepForward = () => {
    if (!redo.length) return;
    setUndo((history) => [...history, copyLayout(draft)]); update(copyLayout(redo[redo.length - 1])); setRedo(redo.slice(0, -1));
  };
  const keyDown = (event) => {
    if (before || pending) return;
    if (event.key.toLowerCase() === 'r') { event.preventDefault(); rotate(); }
    if (event.key === 'Delete') { event.preventDefault(); remove(); }
    if (event.ctrlKey && event.key.toLowerCase() === 'z') { event.preventDefault(); event.shiftKey ? stepForward() : stepBack(); }
    if (event.key === 'Escape') { setBrush(null); setHover(null); setSelected(null); }
  };
  const displayed = before ? data.layout || [] : hover && brush ? [...draft, hover] : draft;
  // Clicking toolbar buttons changes DOM focus; shortcuts still belong to this editor.
  useEffect(() => {
    document.addEventListener('keydown', keyDown);
    return () => document.removeEventListener('keydown', keyDown);
  }, [draft, selected, hover, brush, before, pending, undo, redo]);
  const displayErrors = validateLayout(displayed, room);
  const close = () => dirty ? setConfirmClose(true) : act('close');

  return <div className="ApartmentEditor">
    <header><div><strong>{data.name || 'Обустройство квартиры'}{data.number ? ` · Квартира ${data.number}` : ''}</strong><small>{before ? 'Сохранённая обстановка' : 'Примерка — изменения ещё не применены'}</small></div><button onClick={close} aria-label="Закрыть редактор">×</button></header>
    <div className="ApartmentEditor__body">
      <aside><h3>Мебель</h3><p>Выберите предмет и нажмите на свободное место.</p>
        {room.catalog.filter(item => !item.existingOnly).map((item) => {
          const count = draft.filter((p) => p.furniture === item.id).length;
          return <button key={item.id} className={brush === item.id ? 'selected' : ''} disabled={before || pending || count >= item.maxCount || addedCount >= data.maxFurniture}
            onClick={() => { setBrush(item.id); setSelected(null); setHover(null); }}><Sprite images={item.images} /><span>{item.name}<small>{count} / {item.maxCount}</small></span></button>;
        })}
        <h3>Исходный интерьер</h3><p>Выберите предмет, затем укажите новое место на плане.</p>
        {room.catalog.filter(item => item.existingOnly).map(item => <button key={item.id} disabled={before || pending} className={selected === item.placementId ? 'selected' : ''}
          onClick={() => { setSelected(item.placementId); setBrush(null); setHover(null); }}>{item.name}</button>)}
      </aside>
      <main><div className="ApartmentEditor__toolbar">
        <button disabled={!undo.length || before || pending} onClick={stepBack}>Отменить</button>
        <button disabled={!redo.length || before || pending} onClick={stepForward}>Повторить</button>
        <button disabled={pending} className={before ? 'selected' : ''} onClick={() => { setBefore(!before); setHover(null); }}>{before ? 'Показать проект' : 'До / После'}</button>
        <button aria-label="Уменьшить масштаб" onClick={() => setScale(zoom / 1.25)}>−</button>
        <span aria-label="Масштаб">{Math.round(zoom * 100)}%</span>
        <button aria-label="Увеличить масштаб" onClick={() => setScale(zoom * 1.25)}>+</button>
        <button onClick={fit}>Уместить</button>
        <button aria-pressed={gridLines} onClick={() => setGridLines(!gridLines)}>Сетка</button>
      </div>
        <div ref={viewport} className={`ApartmentEditor__viewport ${panning ? 'panning' : ''}`} {...viewportEvents}>
        <div className="ApartmentEditor__surface" style={{ width: data.width * cellSize + view.width, height: data.height * cellSize + view.height }}>
        <div ref={plan} className={`ApartmentEditor__plan ${before ? 'before' : ''} ${gridLines ? 'grid' : ''}`} tabIndex={0} aria-label="План квартиры"
          style={{ left: view.width / 2, top: view.height / 2, width: data.width * cellSize, height: data.height * cellSize, '--cell': `${cellSize}px`, '--columns': data.width, '--rows': data.height }}
          onPointerDown={pointerDown} onPointerMove={pointerMove} onPointerUp={(event) => { if (drag.current) pointerMove(event); drag.current = null; }} onPointerCancel={() => { if (drag.current?.moved) update(drag.current.original); drag.current = null; }}
          onPointerLeave={() => { if (!drag.current) setHover(null); }}>
          <NavMapPlan tiles={data.scene?.tiles || Array.from({ length: data.width * data.height }, (_, i) => ({ x: i % data.width, y: Math.floor(i / data.width) }))}
            structures={data.scene?.objects.filter(object => object.wall || object.door) || []}
            area={{ x: originX, y: originY, width: data.width, height: data.height }} />
          {Array.from({ length: (data.width || 0) * (data.height || 0) }, (_, index) => {
            const x = originX + index % data.width, y = originY + data.height - 1 - Math.floor(index / data.width);
            const tile = floors.get(`${x},${y}`);
            const wall = !data.scene && (x === 0 || y === 0 || x === data.width - 1 || y === data.height - 1);
            const arrival = x === data.arrivalX && y === data.arrivalY;
            const terminal = !data.scene && x === data.arrivalX && y === data.arrivalY + 1;
            return <div key={index} data-cell={`${x},${y}`} className={`ApartmentEditor__tile ${wall || fixed.has(`${x},${y}`) ? 'wall' : ''} ${data.scene && !tile ? 'void' : ''} ${arrival || terminal ? 'reserved' : ''}`}>
              <span>{arrival ? 'Вход' : terminal ? 'Выход' : ''}</span>
            </div>;
          })}
          {(data.scene?.objects || []).filter(object => !object.wall && !object.door && fixed.has(`${Math.floor(object.x)},${Math.floor(object.y)}`)).map(object => <div key={object.id}
            className={`ApartmentEditor__fixture ${object.images?.length ? 'sprite' : ''}`} title={object.name}
            style={{ left: `${(object.x - originX - .5) / data.width * 100}%`, top: `${(originY + data.height - object.y - .5) / data.height * 100}%`, width: `${100 / data.width}%`, height: `${100 / data.height}%` }}>
            <span style={{ transform: `rotate(${object.noRotation ? 0 : -object.rotation}deg)` }}>{object.images?.length > 0 && <PlanSprite images={object.images} />}</span>
          </div>)}
          {[...displayed].sort((a, b) => (room.catalog.find(def => def.id === a.furniture)?.drawDepth || 0)
            - (room.catalog.find(def => def.id === b.furniture)?.drawDepth || 0) || b.y - a.y).map((item, index) => {
            const def = room.catalog.find((entry) => entry.id === item.furniture);
            if (!def) return null;
            const width = item.rotation % 2 ? def.height : def.width, height = item.rotation % 2 ? def.width : def.height;
            return <div key={item.id} data-placement={item.id} title={displayErrors[item.id] || def.name}
              className={`ApartmentEditor__ghost ${selected === item.id ? 'selected' : ''} ${displayErrors[item.id] ? 'invalid' : ''} ${item.id === '__hover' ? 'hover' : ''}`}
              style={{ zIndex: index + 1, left: `${(item.x - originX) / data.width * 100}%`, top: `${(originY + data.height - item.y - height) / data.height * 100}%`, width: `${width / data.width * 100}%`, height: `${height / data.height * 100}%` }}>
              <span style={{ width: cellSize, height: cellSize, transform: `rotate(${def.noRotation ? 0 : -item.rotation * 90}deg)` }}><PlanSprite images={def.images} /></span>
            </div>;
          })}
        </div>
        </div>
        </div>
        <div className="ApartmentEditor__selection"><span>{definition ? `${definition.name} · Нажмите новую клетку, чтобы переместить` : (brush ? 'Нажмите на план, чтобы разместить предмет' : 'ЛКМ — мебель · ПКМ / СКМ — перемещение вида')}</span>
          <button disabled={before || pending || (!selectedItem && !hover) || definition?.canRotate === false} onClick={rotate}>Повернуть · R</button>
          <button disabled={before || pending || !selectedItem || definition?.existingOnly} onClick={remove}>Убрать · Delete</button></div>
        <p className={valid ? 'ApartmentEditor__hint' : 'ApartmentEditor__error'} role="status">{Object.values(errors)[0] || `${addedCount} / ${data.maxFurniture} предметов · Выход свободен`}</p>
        {data.status && <p className={data.success ? 'ApartmentEditor__hint' : 'ApartmentEditor__error'}>{data.status}</p>}
      </main>
    </div>
    <footer><button disabled={pending || dirty} onClick={() => act('shop')}>Шаблоны квартир</button><button disabled={pending || !dirty} onClick={() => { change(copyLayout(data.layout)); setBrush(null); setHover(null); }}>Сбросить изменения</button>
      <button disabled={pending || !valid || !dirty || before || !!brush} className="primary" onClick={() => { setPending(true); act('apply', { layout: draft }); }}>{pending ? 'Сохранение…' : 'Применить'}</button>
      <button disabled={pending} onClick={() => dirty ? setConfirmClose('exit') : act('exit')}>{data.lobby ? 'Закрыть обустройство' : 'Вернуться из квартиры'}</button></footer>
    {confirmClose && <div className="ApartmentEditor__veil"><div role="dialog" aria-label="Несохранённые изменения"><strong>Изменения ещё не применены</strong><p>Закрыть редактор и отказаться от черновика?</p><button onClick={() => setConfirmClose(false)}>Продолжить обустройство</button><button onClick={() => act(confirmClose === 'exit' ? 'exit' : 'close')}>Отказаться от изменений</button></div></div>}
  </div>;
};
