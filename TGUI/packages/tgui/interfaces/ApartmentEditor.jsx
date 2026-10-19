import { useInterfaceStyleGuard } from './useInterfaceStyleGuard';
import { useEffect, useRef, useState } from 'react';
import { useBackend } from '../backend';
import { copyLayout, validateLayout } from './apartmentLayout.mjs';
import './ApartmentEditor.scss';

const Sprite = ({ images = [] }) => <span className="ApartmentEditor__sprite">
  {images.map((image, i) => <img key={i} src={image.url} alt="" draggable={false} style={{ filter: image.color && image.color !== '#ffffff' ? `drop-shadow(0 0 0 ${image.color})` : undefined }} />)}
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
  const drag = useRef(null);
  const current = useRef(draft);
  const counter = useRef(0);
  const room = { ...data, catalog: data.catalog || [] };
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
    return { x: Math.floor((event.clientX - rect.left) / rect.width * data.width), y: data.height - 1 - Math.floor((event.clientY - rect.top) / rect.height * data.height) };
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
    if (hover && brush) setHover({ ...hover, rotation: (hover.rotation + 1) % 4 });
    else if (selectedItem) change(draft.map((item) => item.id === selected ? { ...item, rotation: (item.rotation + 1) % 4 } : item));
  };
  const remove = () => { if (selectedItem) { change(draft.filter((item) => item.id !== selected)); setSelected(null); } };
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
    <header><div><strong>{data.name || 'Обустройство квартиры'}</strong><small>{before ? 'Сохранённая обстановка' : 'Примерка — изменения ещё не применены'}</small></div><button onClick={close} aria-label="Закрыть редактор">×</button></header>
    <div className="ApartmentEditor__body">
      <aside><h3>Мебель</h3><p>Выберите предмет и нажмите на свободное место.</p>
        {room.catalog.map((item) => {
          const count = draft.filter((p) => p.furniture === item.id).length;
          return <button key={item.id} className={brush === item.id ? 'selected' : ''} disabled={before || pending || count >= item.maxCount || draft.length >= data.maxFurniture}
            onClick={() => { setBrush(item.id); setSelected(null); setHover(null); }}><Sprite images={item.images} /><span>{item.name}<small>{count} / {item.maxCount}</small></span></button>;
        })}
        <p>В прототипе все предметы доступны бесплатно.</p>
      </aside>
      <main><div className="ApartmentEditor__toolbar">
        <button disabled={!undo.length || before || pending} onClick={stepBack}>Отменить</button>
        <button disabled={!redo.length || before || pending} onClick={stepForward}>Повторить</button>
        <button disabled={pending} className={before ? 'selected' : ''} onClick={() => { setBefore(!before); setHover(null); }}>{before ? 'Показать проект' : 'До / После'}</button>
      </div>
        <div ref={plan} className={`ApartmentEditor__plan ${before ? 'before' : ''}`} tabIndex={0} aria-label="План квартиры"
          style={{ aspectRatio: `${data.width}/${data.height}`, '--columns': data.width, '--rows': data.height }}
          onPointerDown={pointerDown} onPointerMove={pointerMove} onPointerUp={(event) => { if (drag.current) pointerMove(event); drag.current = null; }} onPointerCancel={() => { if (drag.current?.moved) update(drag.current.original); drag.current = null; }}
          onPointerLeave={() => { if (!drag.current) setHover(null); }}>
          {Array.from({ length: (data.width || 0) * (data.height || 0) }, (_, index) => {
            const x = index % data.width, y = data.height - 1 - Math.floor(index / data.width);
            const wall = x === 0 || y === 0 || x === data.width - 1 || y === data.height - 1;
            const arrival = x === data.arrivalX && y === data.arrivalY;
            const terminal = x === data.arrivalX && y === data.arrivalY + 1;
            return <div key={index} className={`ApartmentEditor__tile ${wall ? 'wall' : ''} ${arrival || terminal ? 'reserved' : ''}`}>{arrival ? 'Вход' : terminal ? 'Выход' : ''}</div>;
          })}
          {displayed.map((item) => {
            const def = room.catalog.find((entry) => entry.id === item.furniture);
            if (!def) return null;
            const width = item.rotation % 2 ? def.height : def.width, height = item.rotation % 2 ? def.width : def.height;
            return <div key={item.id} data-placement={item.id} title={displayErrors[item.id] || def.name}
              className={`ApartmentEditor__ghost ${selected === item.id ? 'selected' : ''} ${displayErrors[item.id] ? 'invalid' : ''} ${item.id === '__hover' ? 'hover' : ''}`}
              style={{ left: `${item.x / data.width * 100}%`, top: `${(data.height - item.y - height) / data.height * 100}%`, width: `${width / data.width * 100}%`, height: `${height / data.height * 100}%` }}>
              <span style={{ transform: `rotate(${-item.rotation * 90}deg)` }}><Sprite images={def.images} /></span>
            </div>;
          })}
        </div>
        <div className="ApartmentEditor__selection"><span>{definition ? `${definition.name} · Нажмите новую клетку, чтобы переместить` : (brush ? 'Нажмите на план, чтобы разместить предмет' : 'Перетаскивайте мебель или выберите её и нажмите новую клетку')}</span>
          <button disabled={before || pending || (!selectedItem && !hover)} onClick={rotate}>Повернуть · R</button>
          <button disabled={before || pending || !selectedItem} onClick={remove}>Убрать · Delete</button></div>
        <p className={valid ? 'ApartmentEditor__hint' : 'ApartmentEditor__error'} role="status">{Object.values(errors)[0] || `${draft.length} / ${data.maxFurniture} предметов · Выход свободен`}</p>
        {data.status && <p className={data.success ? 'ApartmentEditor__hint' : 'ApartmentEditor__error'}>{data.status}</p>}
      </main>
    </div>
    <footer><button disabled={pending || !dirty} onClick={() => { change(copyLayout(data.layout)); setBrush(null); setHover(null); }}>Сбросить изменения</button>
      <button disabled={pending || !valid || !dirty || before || !!brush} className="primary" onClick={() => { setPending(true); act('apply', { layout: draft }); }}>{pending ? 'Сохранение…' : 'Применить'}</button>
      <button disabled={pending} onClick={() => dirty ? setConfirmClose('exit') : act('exit')}>Вернуться из квартиры</button></footer>
    {confirmClose && <div className="ApartmentEditor__veil"><div role="dialog" aria-label="Несохранённые изменения"><strong>Изменения ещё не применены</strong><p>Закрыть редактор и отказаться от черновика?</p><button onClick={() => setConfirmClose(false)}>Продолжить обустройство</button><button onClick={() => act(confirmClose === 'exit' ? 'exit' : 'close')}>Отказаться от изменений</button></div></div>}
  </div>;
};
