import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
let sequence = 0;

// Keep options inside the rendered page: CEF's native select popup uses a separate surface.
export const TattooSelect = ({ label, value, options, disabled = false, onChange }) => {
  const [open, setOpen] = useState(false), [active, setActive] = useState(0), [position, setPosition] = useState({});
  const button = useRef(null), popup = useRef(null), id = useRef(`tattoo-options-${++sequence}`);
  const selected = Math.max(0, options.findIndex(option => String(option.value) === String(value)));
  const close = () => { setOpen(false); button.current?.focus(); };
  const choose = index => { if (options[index]) onChange(options[index].value); close(); };
  useEffect(() => { if (disabled) setOpen(false); }, [disabled]);
  useLayoutEffect(() => {
    if (!open) return;
    const place = () => {
      const box = button.current.getBoundingClientRect(), below = window.innerHeight - box.bottom - 8;
      const above = below < 150 && box.top > below;
      setPosition({ left: Math.max(4, Math.min(box.left, window.innerWidth - Math.max(180, box.width) - 4)),
        width: Math.min(Math.max(180, box.width), window.innerWidth - 8), maxHeight: Math.max(40, Math.min(240, above ? box.top - 8 : below)),
        ...(above ? { bottom: window.innerHeight - box.top + 3 } : { top: box.bottom + 3 }) });
    };
    place(); window.addEventListener('resize', place); window.addEventListener('scroll', place, true);
    const outside = event => { if (!button.current.contains(event.target) && !popup.current?.contains(event.target)) setOpen(false); };
    document.addEventListener('pointerdown', outside);
    return () => { window.removeEventListener('resize', place); window.removeEventListener('scroll', place, true); document.removeEventListener('pointerdown', outside); };
  }, [open]);
  useEffect(() => { if (open) popup.current?.querySelector('[data-active="true"]')?.scrollIntoView({ block: 'nearest' }); }, [open, active]);
  const key = event => {
    if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); close(); }
    else if (event.key === 'Tab') setOpen(false);
    else if (['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(event.key)) {
      event.preventDefault();
      if (!open) { setActive(selected); setOpen(true); return; }
      setActive(index => event.key === 'Home' ? 0 : event.key === 'End' ? options.length - 1 : Math.max(0, Math.min(options.length - 1, index + (event.key === 'ArrowDown' ? 1 : -1))));
    } else if (event.key === 'Enter' || event.key === ' ') {
      event.preventDefault(); if (open) choose(active); else { setActive(selected); setOpen(true); }
    }
  };
  return <><button type="button" ref={button} role="combobox" aria-label={label} aria-controls={id.current} aria-expanded={open}
    aria-haspopup="listbox" aria-activedescendant={open ? `${id.current}-${active}` : undefined} disabled={disabled}
    onKeyDown={key} onClick={() => { setActive(selected); setOpen(!open); }}>{options[selected]?.label || 'Нет доступных областей'} ▾</button>
    {open && createPortal(<div ref={popup} id={id.current} role="listbox" aria-label={label} className="TattooSelect__options" style={position}>
      {options.map((option, index) => <button type="button" role="option" key={option.value} id={`${id.current}-${index}`} tabIndex={-1}
        aria-selected={String(option.value) === String(value)} data-active={index === active} onPointerDown={event => event.preventDefault()}
        onMouseEnter={() => setActive(index)} onClick={() => choose(index)}>{option.label}</button>)}
    </div>, document.body)}</>;
};
