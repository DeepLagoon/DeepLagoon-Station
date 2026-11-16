import { useEffect, useId, useMemo, useRef, useState } from 'react';
import { Button } from '../components';
import { EmbeddedSelect } from '../components/EmbeddedSelect';
import { LazySprite } from '../components/LazySprite';
import { filter as fuzzyFilter } from './fuzzySearch';
import './BackpackEditor.scss';

const title = 'Редактирование содержимого рюкзака';
const catalogTitle = 'Каталог предметов';
const empty = [];
const cellKey = (x, y) => x + '/' + y;

const boundsOf = boxes => ({
  left: Math.min(...boxes.map(box => box.left)),
  bottom: Math.min(...boxes.map(box => box.bottom)),
  right: Math.max(...boxes.map(box => box.right)),
  top: Math.max(...boxes.map(box => box.top)),
});

// Match SharedItemSystem's rotation, which keeps the shape's bottom-left bound.
const itemBoxes = (shape, x, y, rotation) => {
  const bounds = boundsOf(shape);
  return shape.map(box => {
    const left = box.left - bounds.left;
    const right = box.right - bounds.left;
    const bottom = box.bottom - bounds.bottom;
    const top = box.top - bounds.bottom;
    const width = bounds.right - bounds.left;
    const height = bounds.top - bounds.bottom;
    const [l, b, r, t] = rotation === 90 ? [height - top, left, height - bottom, right]
      : rotation === 180 ? [width - right, height - top, width - left, height - bottom]
        : rotation === 270 ? [bottom, width - right, top, width - left]
          : [left, bottom, right, top];
    return { left: bounds.left + x + l, bottom: bounds.bottom + y + b,
      right: bounds.left + x + r, top: bounds.bottom + y + t };
  });
};

function* cellsOf(boxes) {
  for (const box of boxes) {
    for (let y = box.top; y >= box.bottom; y--) {
      for (let x = box.left; x <= box.right; x++) {
        yield { x, y, key: cellKey(x, y) };
      }
    }
  }
}

const gridArea = (box, bounds) => ({
  gridColumn: (box.left - bounds.left + 1) + ' / span ' + (box.right - box.left + 1),
  gridRow: (bounds.top - box.top + 1) + ' / span ' + (box.top - box.bottom + 1),
});

const Sprite = ({ item, rotation = 0 }) => (
  <span className="BackpackEditor__sprite" aria-hidden="true" style={{ rotate: -rotation + 'deg' }}>
    <LazySprite id={item.iconId} color={item.color?.slice(0, 7) || '#ffffff'} />
  </span>
);

const rotationOf = value => ((value || 0) % 360 + 360) % 360;

function BackpackWindows({ data, act, onClose, showUnavailable, dialogId }) {
  const [picked, setPicked] = useState(null);
  const [hover, setHover] = useState(null);
  const [message, setMessage] = useState('');
  const [query, setQuery] = useState('');
  const [category, setCategory] = useState('');
  const [catalogOpen, setCatalogOpen] = useState(true);
  const [catalogOffset, setCatalogOffset] = useState({ x: 0, y: 0 });
  const gridNode = useRef(null);
  const catalogNode = useRef(null);
  const dialog = useRef(null);
  const gesture = useRef(null);
  const movingWindow = useRef(null);
  const skipClick = useRef(false);
  const grid = data?.grid || empty;
  const items = data?.items || empty;
  const catalog = data?.catalog || empty;
  const bounds = useMemo(() => boundsOf(grid), [grid]);
  const cells = useMemo(() => [...new Map([...cellsOf(grid)].map(cell => [cell.key, cell])).values()]
    .sort((a, b) => b.y - a.y || a.x - b.x), [grid]);
  const cellSet = useMemo(() => new Set(cells.map(cell => cell.key)), [cells]);
  const placed = useMemo(() => items.filter(item => !item.overflow && item.shape?.length).map(item => ({
    item,
    boxes: itemBoxes(item.shape, item.x, item.y, rotationOf(item.rotation)),
  })), [items]);
  const occupants = useMemo(() => new Map(placed.flatMap(({ item, boxes }) =>
    [...cellsOf(boxes)].map(cell => [cell.key, item]))), [placed]);
  const options = useMemo(() => {
    const used = new Set(items.map(item => item.key));
    return catalog.filter(item => !used.has(item.key) && (item.canSelect || showUnavailable));
  }, [items, catalog, showUnavailable]);
  const categories = [...new Set(options.map(item => item.categoryName).filter(Boolean))];
  const activeCategory = categories.includes(category) ? category : '';
  const filtered = useMemo(() => fuzzyFilter(options.filter(item =>
    !activeCategory || item.categoryName === activeCategory), query,
  item => [item.name, item.id, item.categoryName].filter(Boolean).join(' ')), [options, activeCategory, query]);
  const selected = picked && (picked.fromCatalog ? options : items).find(item => item.key === picked.key);

  useEffect(() => { dialog.current?.focus(); }, []);
  useEffect(() => {
    const onKey = event => {
      if (event.key === 'Escape') {
        if (event.target.closest?.('.EmbeddedSelect__menu, [role=combobox][aria-expanded=true]')) return;
        event.stopPropagation();
        if (picked) {
          gesture.current = null;
          setPicked(null);
          setHover(null);
          setMessage('');
        } else {
          onClose();
        }
      }
      if (selected && event.code === 'KeyR' && !event.target.closest?.('input, select, textarea')) {
        event.preventDefault();
        rotateSelected();
      }
    };
    window.addEventListener('keydown', onKey, true);
    return () => window.removeEventListener('keydown', onKey, true);
  });

  function rotateSelected() {
    setPicked(current => current && ({ ...current, rotation: rotationOf(current.rotation + 90) }));
  }

  const placement = (item, selection, cell) => {
    const x = cell.x - selection.offsetX;
    const y = cell.y - selection.offsetY;
    const keys = [...cellsOf(itemBoxes(item.shape || empty, x, y, selection.rotation))].map(cell => cell.key);
    return {
      x, y, keys,
      valid: !!keys.length && keys.every(key => cellSet.has(key)
        && (!occupants.has(key) || (!selection.fromCatalog && occupants.get(key).key === item.key))),
    };
  };

  const place = (item, selection, cell) => {
    const { x, y, valid } = placement(item, selection, cell);
    if (!valid) {
      setMessage('Предмет не помещается здесь.');
      return;
    }
    act(selection.fromCatalog ? 'backpack-add' : 'backpack-move', { key: item.key, x, y, rotation: selection.rotation });
    setPicked(null);
    setHover(null);
    setMessage('');
  };

  const cellAtPointer = event => {
    const rect = gridNode.current?.getBoundingClientRect();
    if (!rect || event.clientX < rect.left || event.clientX >= rect.right
      || event.clientY < rect.top || event.clientY >= rect.bottom) return null;
    return {
      x: bounds.left + Math.floor((event.clientX - rect.left) / rect.width * (bounds.right - bounds.left + 1)),
      y: bounds.top - Math.floor((event.clientY - rect.top) / rect.height * (bounds.top - bounds.bottom + 1)),
    };
  };

  const selectionFor = (item, fromCatalog, cell) => ({
    key: item.key, fromCatalog, rotation: rotationOf(item.rotation),
    offsetX: cell ? cell.x - item.x : 0, offsetY: cell ? cell.y - item.y : 0,
  });

  const begin = (event, item, fromCatalog, cell) => {
    if (event.button !== 0 || gesture.current) return;
    skipClick.current = false;
    if (!item || item.fixed || (fromCatalog && !item.canSelect)) return;
    gesture.current = {
      pointer: event.pointerId, x: event.clientX, y: event.clientY, item,
      selection: selectionFor(item, fromCatalog, cell), dragged: false,
    };
    event.currentTarget.setPointerCapture(event.pointerId);
  };

  const pointerMove = event => {
    const drag = gesture.current;
    if (!drag || drag.pointer !== event.pointerId) return;
    if (!drag.dragged && Math.hypot(event.clientX - drag.x, event.clientY - drag.y) < 4) return;
    if (!drag.dragged) {
      drag.dragged = true;
      setPicked(drag.selection);
      setMessage('');
    }
    const cell = cellAtPointer(event);
    setHover(previous => previous?.x === cell?.x && previous?.y === cell?.y ? previous : cell);
  };

  const pointerUp = event => {
    const drag = gesture.current;
    if (!drag || drag.pointer !== event.pointerId) return;
    gesture.current = null;
    if (!drag.dragged) return;
    skipClick.current = true;
    const cell = cellAtPointer(event);
    if (cell) place(drag.item, picked?.key === drag.item.key ? picked : drag.selection, cell);
    else setHover(null);
  };

  const pointerCancel = () => {
    gesture.current = null;
    setHover(null);
  };

  const handleClick = callback => event => {
    if (!event.detail || !skipClick.current) callback();
    skipClick.current = false;
  };

  const pickCard = (item, fromCatalog) => {
    setPicked(current => current?.key === item.key && current.fromCatalog === fromCatalog
      ? null : selectionFor(item, fromCatalog));
    setMessage('');
  };

  const pickCell = (cell, item) => {
    if (selected) {
      if (!picked.fromCatalog && selected.key === item?.key && picked.rotation === rotationOf(selected.rotation)) {
        setPicked(null);
        return;
      }
      place(selected, picked, cell);
    } else if (item && !item.fixed) {
      setPicked(selectionFor(item, false, cell));
      setMessage('');
    }
  };

  const moveCatalog = event => {
    const start = movingWindow.current;
    if (!start || start.pointer !== event.pointerId) return;
    const x = Math.max(8 - start.left, Math.min(event.clientX - start.x, window.innerWidth - start.right - 8));
    const y = Math.max(8 - start.top, Math.min(event.clientY - start.y, window.innerHeight - start.bottom - 8));
    setCatalogOffset({ x: start.offset.x + x, y: start.offset.y + y });
  };

  const beginMoveCatalog = event => {
    if (event.button !== 0 || event.target.closest('[role=button], button')) return;
    const { left, top, right, bottom } = catalogNode.current.getBoundingClientRect();
    movingWindow.current = { pointer: event.pointerId, x: event.clientX, y: event.clientY,
      left, top, right, bottom, offset: catalogOffset };
    event.currentTarget.setPointerCapture(event.pointerId);
  };

  const preview = selected && hover ? placement(selected, picked, hover) : null;
  const previewCells = new Set(preview?.keys);
  const overflow = items.filter(item => item.overflow);
  const width = bounds.right - bounds.left + 1;
  const height = bounds.top - bounds.bottom + 1;
  const dragHandlers = { onPointerMove: pointerMove, onPointerUp: pointerUp, onPointerCancel: pointerCancel };
  const selectHandlers = (item, fromCatalog) => ({
    onPointerDown: event => begin(event, item, fromCatalog),
    onClick: handleClick(() => pickCard(item, fromCatalog)),
  });

  return <div className="BackpackEditor__backdrop" onPointerDown={event => {
    if (event.target === event.currentTarget) onClose();
  }}>
    <div className="BackpackEditor__windows" data-catalog-open={catalogOpen && !!cells.length}>
      <section className="BackpackEditor__window" ref={dialog} tabIndex={-1} role="dialog"
        aria-labelledby={dialogId + '-title'} id={dialogId}>
        <header>
          <h2 id={dialogId + '-title'}>{data?.name || 'Содержимое рюкзака'}</h2>
          <Button role="button" icon="times" aria-label="Закрыть редактор рюкзака" onClick={onClose} />
        </header>
        <div className="BackpackEditor__body">
          {!!cells.length ? <>
            <div className="BackpackEditor__toolbar">
              <Button role="button" icon="list" selected={catalogOpen} aria-expanded={catalogOpen}
                aria-controls={dialogId + '-catalog'} onClick={() => setCatalogOpen(!catalogOpen)}>{catalogTitle}</Button>
              <Button role="button" icon="redo" disabled={!selected} onClick={rotateSelected}>Повернуть</Button>
              <Button role="button" icon="trash" disabled={!selected || picked.fromCatalog || selected.fixed}
                onClick={() => {
                  act('backpack-remove', { key: selected.key });
                  setPicked(null);
                  setHover(null);
                  setMessage('');
                }}>Убрать предмет</Button>
            </div>
            <div className="BackpackEditor__gridScroll">
              <div className="BackpackEditor__grid" ref={gridNode} role="group" aria-label="Сетка рюкзака"
                style={{ '--columns': width, '--rows': height }} onPointerLeave={() => {
                  if (!gesture.current?.dragged) setHover(null);
                }} {...dragHandlers}>
                {cells.map(cell => {
                  const item = occupants.get(cell.key);
                  const mark = previewCells.has(cell.key) && (preview.valid ? 'valid' : 'invalid');
                  return <button type="button" className="BackpackEditor__cell" key={cell.key}
                    data-occupied={!!item} data-fixed={!!item?.fixed} data-selected={!!item && item.key === selected?.key}
                    data-preview={mark || undefined} aria-label={item?.name || 'Ячейка ' + (cell.x - bounds.left + 1) + ', ' + (bounds.top - cell.y + 1)}
                    title={item?.name} style={{ gridColumn: cell.x - bounds.left + 1, gridRow: bounds.top - cell.y + 1 }}
                    onPointerEnter={() => { if (!gesture.current?.dragged) setHover(cell); }}
                    onFocus={() => setHover(cell)} onPointerDown={event => begin(event, item, false, cell)}
                    onClick={handleClick(() => pickCell(cell, item))} />;
                })}
                {placed.map(({ item, boxes }) => <div key={item.key} className="BackpackEditor__placed"
                  style={gridArea(boundsOf(boxes), bounds)}><Sprite item={item} rotation={item.rotation} /></div>)}
              </div>
            </div>
            <p className="BackpackEditor__hint">{selected ? 'Выберите ячейку для предмета «' + selected.name + '».'
              : 'Перетащите предмет в ячейку или выберите предмет, затем ячейку.'}</p>
            {!!overflow.length && <div className="BackpackEditor__overflow" {...dragHandlers}>
              <h3>Не поместилось</h3>
              {overflow.map(item => <Button role="button" key={item.key} selected={selected?.key === item.key}
                {...selectHandlers(item, false)}>{item.name}</Button>)}
            </div>}
          </> : !data?.status && <p>{data ? 'Выберите рюкзак или MODsuit с модулем хранения.' : 'Загрузка содержимого…'}</p>}
          {(message || data?.status) && <p className="BackpackEditor__status" role="status">{message || data.status}</p>}
        </div>
      </section>
      {catalogOpen && !!cells.length && <section id={dialogId + '-catalog'} role="dialog"
        aria-label={catalogTitle} ref={catalogNode} className="BackpackEditor__window BackpackEditor__catalog"
        style={{ translate: catalogOffset.x + 'px ' + catalogOffset.y + 'px' }}>
        <header onPointerDown={beginMoveCatalog} onPointerMove={moveCatalog}
          onPointerUp={() => { movingWindow.current = null; }}
          onPointerCancel={() => { movingWindow.current = null; }} title="Переместить окно каталога">
          <h2>{catalogTitle}</h2>
          <Button role="button" icon="times" aria-label="Закрыть каталог предметов" onClick={() => setCatalogOpen(false)} />
        </header>
        <div className="BackpackEditor__filters">
          <input type="search" aria-label="Поиск предметов" placeholder="Название или ID предмета…"
            value={query} onChange={event => setQuery(event.target.value)} />
          {categories.length > 1 && <EmbeddedSelect aria-label="Категория предметов" value={activeCategory}
            onChange={event => setCategory(event.target.value)}>
            <option value="">Все категории</option>
            {categories.map(name => <option key={name}>{name}</option>)}
          </EmbeddedSelect>}
        </div>
        <div className="BackpackEditor__catalogScroll" {...dragHandlers}>
          {!!filtered.length ? <div className="BackpackEditor__cards">
            {filtered.map(item => <button type="button" key={item.key} disabled={!item.canSelect}
              aria-pressed={picked?.fromCatalog && selected?.key === item.key || false} title={item.reason}
              {...selectHandlers(item, true)}>
              <Sprite item={item} />
              <span>{item.name}</span>
              {!!item.cost && <small>{item.cost} {item.jobItem ? '$' : 'очков'}</small>}
            </button>)}
          </div> : <p role="status">Ничего не найдено.</p>}
        </div>
      </section>}
    </div>
  </div>;
}

export function BackpackEditor({ data, context, active = true, act, showUnavailable }) {
  const dialogId = useId();
  const [open, setOpen] = useState(false);
  const trigger = useRef(null);
  useEffect(() => { setOpen(false); }, [context, active]);
  const close = () => {
    act('backpack-close');
    setOpen(false);
    trigger.current?.focus();
  };
  return <>
    <div className="Dropdown BackpackEditor__trigger">
      <button ref={trigger} type="button" className="Dropdown__control Button Button--color--default"
        aria-haspopup="dialog" aria-controls={open ? dialogId : undefined} aria-expanded={open}
        onClick={() => {
          setOpen(true);
          act('backpack-open');
        }}>
        <span className="Dropdown__selected-text">{title}</span>
      </button>
    </div>
    {active && open && <BackpackWindows data={data} act={act} onClose={close}
      showUnavailable={showUnavailable} dialogId={dialogId} />}
  </>;
}
