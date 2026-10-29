import { useEffect, useRef, useState } from 'react';
import { Button } from './Button';
import { ColorSquare } from './PlayerColorPicker';

export const CharacterTextEditor = ({ label, value, maxLength, onChange }) => {
  const input = useRef(null), selection = useRef([0, 0]), pending = useRef([]), colorRange = useRef(null);
  const [draft, setDraft] = useState(value);
  const text = useRef(draft);
  useEffect(() => {
    const acknowledged = pending.current.indexOf(value);
    if (acknowledged >= 0) pending.current.splice(0, acknowledged + 1);
    else if (!pending.current.length) { text.current = value; setDraft(value); colorRange.current = null; }
  }, [value]);
  const change = next => {
    text.current = next;
    setDraft(next);
    pending.current.push(next);
    if (pending.current.length > 128) pending.current.shift();
    onChange(next);
  };
  const remember = () => { if (input.current) selection.current = [input.current.selectionStart, input.current.selectionEnd]; };
  const select = (start, end) => requestAnimationFrame(() => {
    input.current?.focus(); input.current?.setSelectionRange(start, end); remember();
  });
  const wrap = (before, after = before) => {
    const [start, end] = selection.current;
    const value = text.current;
    const selected = value.slice(start, end) || 'текст';
    const next = value.slice(0, start) + before + selected + after + value.slice(end);
    if (next.length > maxLength) return;
    colorRange.current = null;
    change(next);
    select(start + before.length, start + before.length + selected.length);
  };
  const color = color => {
    const value = text.current;
    const previous = colorRange.current;
    const [start, end] = previous ? [previous.start, previous.end] : selection.current;
    const selected = previous ? previous.text : value.slice(start, end) || 'текст';
    const before = '-=' + (color.startsWith('#') ? color : '#' + color) + '(';
    const next = value.slice(0, start) + before + selected + ')=-' + value.slice(end);
    if (next.length > maxLength) return;
    colorRange.current = { start, end: start + before.length + selected.length + 3, text: selected };
    change(next);
    // Keep the picker focused while its sliders are being dragged.
    selection.current = [start + before.length, start + before.length + selected.length];
  };
  return <div className="CharacterTextEditor"><div className="CharacterTextEditor__tools" role="toolbar" aria-label={'Форматирование: ' + label}>
    {[['Жирный', '**'], ['Курсив', '*'], ['Подчёркнутый', '__'], ['Зачёркнутый', '~~'], ['Спойлер', '||'], ['Код', '`']].map(([name, token]) =>
      <Button key={name} onMouseDown={event => event.preventDefault()} onClick={() => wrap(token)}>{name}</Button>)}
    <ColorSquare label="Цвет текста" value="#ffffff" onChange={color} />
  </div><small>Выделите текст и нажмите нужное оформление. Без выделения вставится заготовка.</small>
    <textarea ref={input} aria-label={label} value={draft} maxLength={maxLength} onSelect={remember} onKeyUp={remember} onMouseUp={remember}
      onPointerDown={() => { colorRange.current = null; }}
      onKeyDown={event => {
        if (['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown', 'Home', 'End'].includes(event.key)) colorRange.current = null;
        if (event.key === 'Enter' && !event.nativeEvent.isTrusted) {
          event.preventDefault();
          const { selectionStart: start, selectionEnd: end } = event.currentTarget;
          const next = text.current.slice(0, start) + '\n' + text.current.slice(end);
          if (next.length <= maxLength) { colorRange.current = null; change(next); select(start + 1, start + 1); }
        }
        if ((event.ctrlKey || event.metaKey) && ['b', 'i', 'u'].includes(event.key.toLowerCase())) { event.preventDefault(); remember(); wrap({ b: '**', i: '*', u: '__' }[event.key.toLowerCase()]); }
      }}
      onChange={event => { colorRange.current = null; remember(); change(event.target.value); }} />
  </div>;
};
