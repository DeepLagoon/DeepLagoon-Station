import { useEffect, useRef, useState } from 'react';
import { useBackend } from '../backend';
import './PdaMessenger.scss';

const maxBytes = 256 * 1024;
const emoji = ['🙂', '👍', '👋', '❤️', '✅', '⚠️'];

export const PdaMessenger = () => {
  const { data, act } = useBackend();
  const t = key => data.labels?.[key] || '';
  const [draft, setDraft] = useState('');
  const [search, setSearch] = useState('');
  const [error, setError] = useState('');
  const [pasting, setPasting] = useState(false);
  const log = useRef(null);
  const upload = useRef(0);
  const peer = data.contacts?.find(contact => contact.id === data.peer);

  useEffect(() => { setDraft(''); setError(''); upload.current++; setPasting(false); }, [data.peer, data.sentRevision]);
  useEffect(() => { if (log.current) log.current.scrollTop = log.current.scrollHeight; }, [data.messages?.length, data.peer]);

  const paste = async event => {
    const file = Array.from(event.clipboardData?.files || []).find(item => item.type.startsWith('image/'));
    if (!file) return;
    event.preventDefault();
    if (!data.attachments || !peer?.available || pasting) return;
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
    if (peer?.available && (draft.trim() || data.attachment) && !pasting)
      act('send', { text: draft });
  };

  return (
    <div className="PdaMessenger" onPaste={paste}>
      <header><strong>{t('title')}</strong><button type="button" onClick={() => act('refresh')}>{t('refresh')}</button></header>
      {!data.ready ? <p>{t('waiting')}</p> : <main>
        <aside aria-label={t('contacts')}>
          <input value={search} onChange={event => setSearch(event.target.value)} placeholder={t('contacts')} aria-label={t('contacts')} />
          <div className="PdaMessenger__contacts">
            {(data.contacts || []).filter(contact => contact.name.toLocaleLowerCase().includes(search.trim().toLocaleLowerCase())).map(contact =>
              <button type="button" key={contact.id} aria-pressed={contact.id === data.peer}
                onClick={() => act('select', { id: contact.id })}>
                {contact.unread && <span className="PdaMessenger__unread" aria-hidden="true">● </span>}{contact.name}
                {!contact.available && <small>{t('offline-label')}</small>}
              </button>
            )}
          </div>
        </aside>
        <section>
          {peer ? <>
            <strong className="PdaMessenger__peer">{peer.name}</strong>
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
            <form onSubmit={send}>
              <div className="PdaMessenger__toolbar">
                {emoji.map(symbol => <button key={symbol} type="button" onClick={() => setDraft(value => (value + symbol).slice(0, 500))}>{symbol}</button>)}
                {data.attachments && <button type="button" title={t('paste')} disabled={!peer.available || pasting}
                  onClick={() => act('file')}>{t('file')}</button>}
              </div>
              {!!data.attachment && <div className="PdaMessenger__preview">
                <img src={data.attachment} alt={t('image')} />
                <button type="button" onClick={() => act('detach')}>{t('remove')}</button>
              </div>}
              <div className="PdaMessenger__send">
                <input value={draft} maxLength={500} placeholder={t('write')} aria-label={t('write')}
                  onChange={event => setDraft(event.target.value)} />
                <button type="submit" disabled={!peer.available || pasting || (!draft.trim() && !data.attachment)}>{t('send')}</button>
              </div>
            </form>
          </> : <p>{t('select')}</p>}
          {(error || data.status) && <small role="status">{error || data.status}</small>}
        </section>
      </main>}
    </div>
  );
};
