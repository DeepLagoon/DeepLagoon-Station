import { useEffect, useId, useRef, useState } from 'react';

export const GridEditorSelect = ({ value, options, onChange, disabled, ...props }) => {
  const id = useId(), root = useRef(null), control = useRef(null);
  const [open, setOpen] = useState(false), [active, setActive] = useState(0);
  const selected = Math.max(0, options.findIndex(option => option.value === value));
  const expanded = open && !disabled;
  const show = () => { setActive(selected); setOpen(true); };
  const choose = index => { onChange(options[index].value); setOpen(false); control.current.focus(); };
  const key = event => {
    if (disabled || !options.length || event.ctrlKey || event.altKey || event.metaKey) return;
    const code = event.code || event.key;
    if (['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(code)) {
      event.preventDefault(); event.stopPropagation?.();
      if (!expanded) show();
      else setActive(index => code === 'Home' ? 0 : code === 'End' ? options.length - 1
        : (index + (code === 'ArrowDown' ? 1 : -1) + options.length) % options.length);
    } else if (['Enter', 'Space', ' '].includes(code)) {
      event.preventDefault(); event.stopPropagation?.();
      if (expanded) choose(active); else show();
    } else if (code === 'Escape' && expanded) {
      event.preventDefault(); event.stopPropagation?.(); setOpen(false);
    } else if (code === 'Tab') setOpen(false);
  };
  useEffect(() => {
    const nativeKey = event => {
      if (event.detail.down && document.activeElement === control.current)
        key({ ...event.detail, preventDefault() {} });
    };
    window.addEventListener('grid-editor-key', nativeKey);
    return () => window.removeEventListener('grid-editor-key', nativeKey);
  });
  useEffect(() => { if (disabled) setOpen(false); }, [disabled]);
  useEffect(() => {
    if (!expanded) return;
    root.current.querySelector('[data-active=true]')?.scrollIntoView({ block: 'nearest' });
  }, [expanded, active]);
  useEffect(() => {
    if (!expanded) return;
    const outside = event => { if (!root.current.contains(event.target)) setOpen(false); };
    const blur = () => setOpen(false);
    document.addEventListener('pointerdown', outside, true); window.addEventListener('blur', blur);
    return () => { document.removeEventListener('pointerdown', outside, true); window.removeEventListener('blur', blur); };
  }, [expanded]);
  return <div ref={root} className="GridEditor__select" onBlur={event => {
    if (!event.currentTarget.contains(event.relatedTarget)) setOpen(false);
  }}>
    <button {...props} ref={control} type="button" role="combobox" disabled={disabled}
      aria-expanded={expanded} aria-haspopup="listbox" aria-controls={id}
      aria-activedescendant={expanded ? id + '-' + active : undefined}
      onClick={() => { if (expanded) setOpen(false); else show(); }} onKeyDown={key}>
      <span>{options[selected]?.label}</span><span aria-hidden="true">▾</span>
    </button>
    {expanded && <div id={id} role="listbox" aria-label={props['aria-label']}>
      {options.map((option, index) => <button key={option.value} id={id + '-' + index}
        type="button" role="option" tabIndex={-1} aria-selected={option.value === value}
        data-active={index === active} onPointerMove={() => setActive(index)}
        onPointerDown={event => event.preventDefault()} onClick={() => choose(index)}>{option.label}</button>)}
    </div>}
  </div>;
};
