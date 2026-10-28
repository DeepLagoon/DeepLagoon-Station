import { Children, useEffect, useId, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import './EmbeddedSelect.scss';

// CEF's native select popup has a separate paint buffer. Keep menus in the
// document so signed WebViews without popup compositing can render them safely.
export const EmbeddedSelect = ({ children, value, onChange, disabled, className = '', ...props }) => {
  const options = Children.toArray(children).filter(Boolean);
  const selected = options.findIndex(option => String(option.props.value ?? option.props.children) === String(value));
  const [menu, setMenu] = useState(null);
  const [active, setActive] = useState(0);
  const button = useRef(null);
  const list = useRef(null);
  const id = useId();
  const close = () => { setMenu(null); button.current?.focus(); };
  const choose = index => {
    const option = options[index];
    if (!option || option.props.disabled) return;
    onChange?.({ target: { value: String(option.props.value ?? option.props.children) } });
    close();
  };
  const open = () => {
    const rect = button.current.getBoundingClientRect();
    const below = innerHeight - rect.bottom - 4;
    const above = rect.top - 4;
    const upwards = below < 160 && above > below;
    setActive(Math.max(0, selected));
    setMenu({ left: Math.max(4, Math.min(rect.left, innerWidth - rect.width - 4)),
      width: Math.min(rect.width, innerWidth - 8), maxHeight: Math.max(24, Math.min(240, upwards ? above : below)),
      ...(upwards ? { bottom: innerHeight - rect.top + 2 } : { top: rect.bottom + 2 }) });
  };
  useEffect(() => {
    if (!menu) return;
    list.current?.focus();
    const outside = event => {
      if (!button.current?.contains(event.target) && !list.current?.contains(event.target)) setMenu(null);
    };
    const blur = () => setMenu(null);
    const reposition = event => { if (event.type === 'resize' || !list.current?.contains(event.target)) setMenu(null); };
    document.addEventListener('pointerdown', outside, true);
    window.addEventListener('resize', reposition);
    window.addEventListener('blur', blur);
    window.addEventListener('scroll', reposition, true);
    return () => {
      document.removeEventListener('pointerdown', outside, true);
      window.removeEventListener('resize', reposition);
      window.removeEventListener('blur', blur);
      window.removeEventListener('scroll', reposition, true);
    };
  }, [menu]);
  useEffect(() => { list.current?.children[active]?.scrollIntoView({ block: 'nearest' }); }, [active, menu]);
  useEffect(() => { if (disabled) setMenu(null); }, [disabled]);
  const keyDown = event => {
    if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); close(); return; }
    if (event.key === 'Tab') { setMenu(null); button.current?.focus(); return; }
    if (['ArrowDown', 'ArrowUp', 'Home', 'End', 'Enter', ' '].includes(event.key)) {
      event.preventDefault();
      if (!menu) { open(); return; }
      if (event.key === 'Enter' || event.key === ' ') { choose(active); return; }
      const step = event.key === 'ArrowUp' || event.key === 'End' ? -1 : 1;
      let next = event.key === 'Home' ? 0 : event.key === 'End' ? options.length - 1 : active + step;
      while (next >= 0 && next < options.length && options[next].props.disabled) next += step;
      if (next >= 0 && next < options.length) setActive(next);
    }
  };
  return <>
    <button {...props} ref={button} type="button" role="combobox" disabled={disabled}
      className={`EmbeddedSelect ${className}`} aria-expanded={!!menu} aria-controls={menu ? id : undefined}
      aria-haspopup="listbox" onKeyDown={keyDown} onClick={() => menu ? close() : open()}>
      <span>{options[selected]?.props.children ?? ''}</span><span aria-hidden="true">▾</span>
    </button>
    {menu && !disabled && createPortal(<div ref={list} id={id} className="EmbeddedSelect__menu" role="listbox"
      aria-label={props['aria-label']} aria-activedescendant={`${id}-${active}`} tabIndex={-1} style={menu} onKeyDown={keyDown}>
      {options.map((option, index) => <div key={option.key ?? index} id={`${id}-${index}`} role="option"
        aria-selected={index === selected} aria-disabled={!!option.props.disabled} data-active={index === active}
        onPointerMove={() => { if (!option.props.disabled) setActive(index); }} onClick={() => choose(index)}>
        {option.props.children}
      </div>)}
    </div>, document.body)}
  </>;
};
