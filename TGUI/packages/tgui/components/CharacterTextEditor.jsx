import { useEffect, useRef, useState } from 'react';
import { Button } from './Button';
import { ColorSquare } from './PlayerColorPicker';

export const CharacterTextEditor = ({ label, value, maxLength, onChange }) => {
  const input = useRef(null), selection = useRef([0, 0]), pending = useRef([]), colorRange = useRef(null);
  const [draft, setDraft] = useState(value);
  const [textColor, setTextColor] = useState('#ffffff');
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
  const applyColor = () => {
    const value = text.current;
    const previous = colorRange.current;
    const [start, end] = previous ? [previous.start, previous.end] : selection.current;
    const selected = previous ? previous.text : value.slice(start, end) || 'текст';
    const before = '-=' + textColor + '(';
    const next = value.slice(0, start) + before + selected + ')=-' + value.slice(end);
    if (next.length > maxLength) return;
    colorRange.current = { start, end: start + before.length + selected.length + 3, text: selected };
    change(next);
    select(start + before.length, start + before.length + selected.length);
  };
  return <div className="CharacterTextEditor"><div className="CharacterTextEditor__tools" role="toolbar" aria-label={'Форматирование: ' + label}>
    {[['Жирный', '**'], ['Курсив', '*'], ['Подчёркнутый', '__'], ['Зачёркнутый', '~~'], ['Спойлер', '||'], ['Код', '`']].map(([name, token]) =>
      <Button key={name} onMouseDown={event => event.preventDefault()} onClick={() => wrap(token)}>{name}</Button>)}
    <Button role="button" onMouseDown={event => event.preventDefault()} onClick={applyColor}>Цвет текста</Button>
    <ColorSquare label="Изменить цвет текста" value={textColor} onChange={setTextColor} hideLabel />
  </div><small>Выделите текст и нажмите нужное оформление. Без выделения вставится заготовка.</small>
    <textarea ref={input} aria-label={label} value={draft} maxLength={maxLength} onSelect={remember} onKeyUp={remember} onMouseUp={remember}
      onPointerDown={() => { colorRange.current = null; }}
      onKeyDown={event => {
        if (['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown', 'Home', 'End'].includes(event.key)) colorRange.current = null;
        if (event.key === 'Enter' && (event.shiftKey || !event.nativeEvent.isTrusted) && !event.nativeEvent.isComposing) {
          event.preventDefault();
          const field = event.currentTarget;
          const length = field.value.length - (field.selectionEnd - field.selectionStart) + 1;
          if (length > maxLength) return;
          // Insert the newline on keydown so it does not depend on CEF's Return char event.
          field.setRangeText('\n', field.selectionStart, field.selectionEnd, 'end');
          colorRange.current = null;
          remember();
          change(field.value);
          return;
        }
        if ((event.ctrlKey || event.metaKey) && ['b', 'i', 'u'].includes(event.key.toLowerCase())) { event.preventDefault(); remember(); wrap({ b: '**', i: '*', u: '__' }[event.key.toLowerCase()]); }
      }}
      onChange={event => { colorRange.current = null; remember(); change(event.target.value); }} />
  </div>;
};
