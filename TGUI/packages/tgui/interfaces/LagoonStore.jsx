import { useEffect, useRef } from 'react';
import './LagoonStore.scss';

// Order IDs are idempotency keys, not secrets. Custom CEF origins may lack randomUUID.
export const storeOrderId = () => globalThis.crypto?.randomUUID?.() ?? 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, c => {
  const value = Math.floor(Math.random() * 16);
  return (c === 'x' ? value : (value & 3) | 8).toString(16);
});
export const formatCoins = value => Number(value || 0).toLocaleString('ru-RU');
export const StoreHeader = ({ title, balance }) => <header className="LagoonStore__header">
  <div><h1>{title}</h1></div>
  <div className="LagoonStore__wallet"><small>Ваш баланс</small><strong>{balance >= 0 ? formatCoins(balance) : '—'} <span>LC</span></strong></div>
</header>;
export const StoreTabs = ({ tabs, selected, onChange }) => <nav className="LagoonStore__tabs" role="tablist" aria-label="Разделы магазина">
  {tabs.map(([id, text]) => <button key={id} role="tab" aria-selected={selected === id} onClick={() => onChange(id)}>{text}</button>)}
</nav>;
export const StoreDialog = ({ title, onCancel, busy, children, preview = false }) => {
  const dialog = useRef(null), cancel = useRef(onCancel);
  cancel.current = onCancel;
  useEffect(() => {
    const previous = document.activeElement;
    dialog.current.focus();
    return () => { if (previous?.isConnected) previous.focus(); };
  }, []);
  return <div className="LagoonStore__overlay" onKeyDown={event => {
    if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); if (!busy) cancel.current(); }
    if (event.key === 'Tab') {
      const nodes = [...dialog.current.querySelectorAll('button:not(:disabled), input:not(:disabled), [tabindex="0"]')];
      const first = nodes[0], last = nodes.at(-1);
      if (!nodes.length) { event.preventDefault(); return; }
      if (event.shiftKey && (document.activeElement === first || document.activeElement === dialog.current)) { event.preventDefault(); last.focus(); }
      if (!event.shiftKey && (document.activeElement === last || document.activeElement === dialog.current)) { event.preventDefault(); first.focus(); }
    }
  }}>
    <section ref={dialog} tabIndex={-1} role="dialog" aria-modal="true" aria-label={title} className={`LagoonStore__dialog ${preview ? 'LagoonStore__preview' : ''}`}>
      <h2>{title}</h2>{children}
    </section>
  </div>;
};
