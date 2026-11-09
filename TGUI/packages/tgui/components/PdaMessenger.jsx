import { useEffect, useRef, useState } from 'react';
import './PdaMessenger.scss';

const maxBytes = 256 * 1024;
const emoji = ['🙂', '👍', '👋', '❤️', '✅', '⚠️'];

export const PdaMessenger = ({ data, act }) => {
  const t = key => data.labels?.[key] || '';
  const [draft, setDraft] = useState('');
  const [number, setNumber] = useState('');
  const [error, setError] = useState('');
  const [pasting, setPasting] = useState(false);
  const log = useRef(null);
  const upload = useRef(0);
  const contacts = data.contacts || [];
  const saved = contacts.filter(contact => contact.saved);
  const incoming = contacts.filter(contact => !contact.saved);
  const peer = contacts.find(contact => contact.id === data.peer);
  const canSend = peer?.saved && peer.available;

  useEffect(() => { setDraft(''); setError(''); upload.current++; setPasting(false); }, [data.peer, data.sentRevision]);
  useEffect(() => { setNumber(''); }, [data.contactRevision]);
  useEffect(() => () => { upload.current++; }, []);
  useEffect(() => { if (log.current) log.current.scrollTop = log.current.scrollHeight; }, [data.messages?.at(-1)?.id, data.peer]);

  const paste = async event => {
    const file = Array.from(event.clipboardData?.files || []).find(item => item.type.startsWith('image/'));
    if (!file) return;
    event.preventDefault();
    if (!data.attachments || !canSend || pasting) return;
    if (file.size > 8 * 1024 * 1024) { setError(t('file-error')); return; }

    const id = ++upload.current;
    const url = URL.createObjectURL(file);
    setPasting(true);
    setError('');
    try {
      const source = new Image();
      source.src = url;
      await source.decode();
      const ratio = Math.min(1, 768 / Math.max(source.width, source.height));
      const canvas = document.createElement('canvas');
      canvas.width = Math.max(1, Math.round(source.width * ratio));
      canvas.height = Math.max(1, Math.round(source.height * ratio));
      const ctx = canvas.getContext('2d');
      ctx.fillStyle = '#ffffff';
      ctx.fillRect(0, 0, canvas.width, canvas.height);
      ctx.drawImage(source, 0, 0, canvas.width, canvas.height);
      let encoded = canvas.toDataURL('image/jpeg', 0.84).split(',')[1];
      if (Math.ceil(encoded.length * 3 / 4) > maxBytes)
        encoded = canvas.toDataURL('image/jpeg', 0.65).split(',')[1];
      if (Math.ceil(encoded.length * 3 / 4) > maxBytes)
        throw new Error('Image exceeds attachment budget');
      if (id !== upload.current) return;
      act('clipboard-start', { peer: peer.id });
      for (let offset = 0; offset < encoded.length; offset += 6000)
        act('clipboard-chunk', { data: encoded.slice(offset, offset + 6000) });
      act('clipboard-end');
    } catch {
      if (id === upload.current) setError(t('file-error'));
    } finally {
      URL.revokeObjectURL(url);
      if (id === upload.current) setPasting(false);
    }
  };

  const send = event => {
    event.preventDefault();
    if (canSend && (draft.trim() || data.attachment) && !pasting)
      act('send', { text: draft });
  };

  const add = event => {
    event.preventDefault();
    if (number.trim())
      act('add', { number });
  };

  const contactButton = contact => (
    <button type="button" key={contact.id} aria-pressed={contact.id === data.peer}
      onClick={() => act('select', { id: contact.id })}>
      {contact.unread && <span className="PdaMessenger__unread" aria-hidden="true">● </span>}{contact.id}
      {!contact.available && <small>{t('offline-label')}</small>}
    </button>
  );

  return (
    <main className="PdaMessenger Pda__content" onPaste={paste}>
      {!data.ready ? <p role="status">{t('waiting')}</p> : <>
        <form className="PdaMessenger__number" onSubmit={add}>
          <div className="PdaMessenger__send">
            <input value={number} inputMode="numeric" maxLength={16} onChange={event => setNumber(event.target.value)}
              placeholder={t('number-input')} aria-label={t('number-input')}
              aria-describedby={saved.length ? undefined : 'pda-messenger-number-help'} />
            <button type="submit" disabled={!number.trim()}>{t('add-contact')}</button>
          </div>
          {!saved.length && <small id="pda-messenger-number-help">{t('number-help')}</small>}
        </form>
        {(error || data.status) && <small role="status">{error || data.status}</small>}
        <div className={'PdaMessenger__columns' + (peer ? ' PdaMessenger__columns--chat' : '')}>
        <aside aria-label={t('contacts')}>
          <div className="PdaMessenger__contacts">
            <div className="Pda__eyebrow">{t('contacts')}</div>
            {!saved.length && <p>{t('empty-contacts')}</p>}
            {saved.map(contactButton)}
            {!!incoming.length && <div className="Pda__eyebrow">{t('incoming')}</div>}
            {incoming.map(contactButton)}
          </div>
        </aside>
        <section>
          {peer ? <>
            <div className="PdaMessenger__bar">
              <strong>{peer.id}</strong>
              {peer.saved && <button type="button" onClick={() => act('remove', { id: peer.id })}>{t('remove-contact')}</button>}
            </div>
            <div ref={log} className="PdaMessenger__history" role="log" aria-live="polite">
              {!data.messages?.length && <p>{t('empty')}</p>}
              {(data.messages || []).map(message => (
                <article key={message.id} className={message.outgoing ? 'PdaMessenger__outgoing' : ''}>
                  {!!message.text && <span>{message.text}</span>}
                  {message.hasImage && (data.imageId === message.id && data.image
                    ? <img src={data.image} alt={t('image')} />
                    : <button type="button" onClick={() => act('image', { id: message.id })}>{t('image')}</button>)}
                </article>
              ))}
            </div>
            {!peer.saved ? <div className="PdaMessenger__bar">
              <p>{t('save-to-reply')}</p>
              <button type="button" onClick={() => act('add', { number: String(peer.id) })}>{t('add-contact')}</button>
            </div> : <form onSubmit={send}>
              <div className="PdaMessenger__toolbar">
                {emoji.map(symbol => <button key={symbol} type="button" disabled={!canSend} onClick={() => setDraft(value => (value + symbol).slice(0, 500))}>{symbol}</button>)}
                {data.attachments && <button type="button" title={t('paste')} disabled={!canSend || pasting}
                  onClick={() => act('file')}>{t('file')}</button>}
              </div>
              {!!data.attachment && <div className="PdaMessenger__preview">
                <img src={data.attachment} alt={t('image')} />
                <button type="button" onClick={() => act('detach')}>{t('remove')}</button>
              </div>}
              <div className="PdaMessenger__send">
                <input value={draft} maxLength={500} placeholder={t('write')} aria-label={t('write')} disabled={!canSend}
                  onChange={event => setDraft(event.target.value)} />
                <button type="submit" disabled={!canSend || pasting || (!draft.trim() && !data.attachment)}>{t('send')}</button>
              </div>
            </form>}
          </> : <p>{t('select')}</p>}
        </section>
        </div>
      </>}
    </main>
  );
};
