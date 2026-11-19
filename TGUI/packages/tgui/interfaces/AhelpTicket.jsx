import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import { useBackend } from '../backend';
import './AhelpTicket.scss';

const Thumbnail = ({ thumbnail, name }) => {
  const atlas = useRef(null);
  const { image, width, height, loops, frameEnds } = thumbnail;
  const frameTimes = frameEnds.map(({ time }) => time).join(',');
  useEffect(() => {
    if (!atlas.current) return;
    const times = frameTimes.split(',').map(Number);
    const duration = times.at(-1);
    const columns = Math.min(5, times.length);
    const positions = times.map((_, index) => ({
      backgroundPosition: `${-(width + 1) * (index % columns)}px ${-(height + 1) * Math.floor(index / columns)}px`,
      offset: (times[index - 1] || 0) / duration,
      easing: 'steps(1, end)',
    }));
    const animation = atlas.current.animate([...positions, { ...positions.at(-1), offset: 1 }], {
      duration, iterations: loops || Infinity, fill: 'forwards',
    });
    return () => animation.cancel();
  }, [image, width, height, loops, frameTimes]);

  if (!frameEnds.length) return <img src={image} alt={name} />;
  return <div ref={atlas} role="img" aria-label={name}
    style={{ width, height, backgroundImage: `url(${image})`, backgroundRepeat: 'no-repeat', marginBottom: '0.3rem' }} />;
};

export const AhelpTicket = () => {
  const { data, act } = useBackend();
  const [draft, setDraft] = useState('');
  const [pasting, setPasting] = useState(false);
  const [error, setError] = useState('');
  const [showLatest, setShowLatest] = useState(false);
  const generation = useRef(0);
  const scroll = useRef(null);
  const history = useRef(null);
  const followLatest = useRef(true);
  const composer = useRef(null);
  const lines = data.lines || [];
  const thumbnails = new Map((data.thumbnails || []).map((thumbnail) => [thumbnail.id, thumbnail]));
  const newest = lines.at(-1)?.id;

  useEffect(() => {
    generation.current++;
    setDraft('');
    setError('');
    setPasting(false);
  }, [data.ticket]);
  useEffect(() => () => { generation.current++; }, []);
  useEffect(() => {
    if (!data.focusComposer || !data.writable) return;
    const focus = () => composer.current?.focus({ preventScroll: true });
    focus();
    window.addEventListener('deeplagoon/focus-input', focus);
    return () => window.removeEventListener('deeplagoon/focus-input', focus);
  }, [data.focusComposer, data.writable, data.ticket]);
  const jumpToLatest = () => {
    followLatest.current = true;
    if (scroll.current) scroll.current.scrollTop = scroll.current.scrollHeight;
    setShowLatest(false);
  };
  const trackScroll = () => {
    const pane = scroll.current;
    if (!pane) return;
    followLatest.current = pane.scrollHeight - pane.scrollTop - pane.clientHeight <= 16;
    setShowLatest(!followLatest.current);
  };
  useLayoutEffect(jumpToLatest, [data.ticket]);
  useLayoutEffect(() => {
    if (followLatest.current) jumpToLatest();
  }, [newest]);
  useEffect(() => {
    if (!history.current || !scroll.current) return;
    const observer = new ResizeObserver(() => {
      if (followLatest.current) jumpToLatest();
      else trackScroll();
    });
    observer.observe(history.current);
    observer.observe(scroll.current);
    return () => observer.disconnect();
  }, []);

  const paste = async (event) => {
    const file = [...(event.clipboardData?.files || [])].find((item) => item.type.startsWith('image/'))
      || [...(event.clipboardData?.items || [])]
        .find((item) => item.kind === 'file' && item.type.startsWith('image/'))?.getAsFile();
    if (!file) return;
    event.preventDefault();
    if (!data.writable || pasting) return;
    if (!file.size || file.size > 20 * 1024 * 1024) {
      setError('Максимальный размер изображения — 20 МиБ.');
      return;
    }
    const batch = ++generation.current;
    setError('');
    setPasting(true);
    try {
      const encoded = await new Promise((resolve, reject) => {
        const reader = new FileReader();
        reader.onload = () => resolve(String(reader.result).split(',')[1]);
        reader.onerror = reject;
        reader.readAsDataURL(file);
      });
      if (batch !== generation.current) return;
      act('paste-start', { name: file.name || 'clipboard-image', round: data.round });
      for (let offset = 0; offset < encoded.length; offset += 6000) {
        if (batch !== generation.current) return;
        act('paste-chunk', { data: encoded.slice(offset, offset + 6000) });
        if (offset % 48000 === 42000) {
          await new Promise((resolve) => setTimeout(resolve, 16));
        }
      }
      if (batch === generation.current) act('paste-end');
    } catch {
      if (batch === generation.current) setError('Не удалось прочитать изображение.');
    } finally {
      if (batch === generation.current) setPasting(false);
    }
  };

  const send = () => {
    if (data.writable && draft.trim()) {
      act('send', { text: draft, round: data.round });
      setDraft('');
    }
  };
  const disabled = !data.writable;

  return (
    <section className="AhelpTicket" onClick={() => {
      if (data.focusComposer && data.writable) composer.current?.focus({ preventScroll: true });
    }}>
      <div className="AhelpTicket__timeline">
        <div className="AhelpTicket__history" ref={scroll} onScroll={trackScroll} role="log">
          <div ref={history}>
            {data.older && <button type="button" onClick={() => act('older')}>Ранние сообщения</button>}
            {lines.map((line) => (
              <article key={line.id} className={line.staff ? 'AhelpTicket__staff' : undefined}>
                <header><strong style={{ color: line.color }}>{line.author}</strong> <time>{line.at}</time>{line.staff && ' · для администраторов'}</header>
                {line.text && <div className="AhelpTicket__text">{line.text}</div>}
                {line.file && (
                  <div className="AhelpTicket__attachment">
                    {line.failed ? <div role="status">Не удалось загрузить медиа.</div>
                      : line.loading || (!thumbnails.has(line.id) && !line.previewUnavailable && data.previewId !== line.id)
                        ? <div className="AhelpTicket__loading" role="status"><span className="AhelpTicket__spinner" />Загрузка медиа…</div>
                        : data.previewId === line.id && data.preview
                      ? line.mime?.startsWith('video/') ? <video controls preload="metadata" src={data.preview} />
                        : line.mime?.startsWith('audio/') ? <audio controls preload="metadata" src={data.preview} />
                          : <img src={data.preview} alt={line.file} />
                      : thumbnails.has(line.id) && <Thumbnail thumbnail={thumbnails.get(line.id)} name={line.file} />}
                    <button type="button" disabled={line.loading || line.failed} onClick={() => act('view', { id: line.id })}>{line.file}</button>
                    <button type="button" disabled={line.loading || line.failed} onClick={() => act('save', { id: line.id })}>Сохранить</button>
                  </div>
                )}
              </article>
            ))}
            {(data.pending || []).map((pending) => <article key={pending.id}>
              {pending.text && <div className="AhelpTicket__text">{pending.text}</div>}
              {pending.file && <div>{pending.file}</div>}
              {pending.error ? <div role="status">{pending.error} <button type="button" onClick={() => act('retry-send', { id: pending.id })}>Повторить</button></div>
                : <div className="AhelpTicket__loading" role="status"><span className="AhelpTicket__spinner" />{pending.file ? 'Загрузка медиа…' : 'Отправка…'}</div>}
            </article>)}
          </div>
        </div>
        {showLatest && <button type="button" className="AhelpTicket__latest" onClick={jumpToLatest}>
          К последнему сообщению ↓
        </button>}
      </div>
      {error && <div role="status">{error}</div>}
      <form className="AhelpTicket__composer" onSubmit={(event) => { event.preventDefault(); send(); }}>
        <input ref={composer} aria-label="Сообщение" placeholder="Сообщение" value={draft} maxLength={1800}
          disabled={!data.writable} onChange={(event) => setDraft(event.target.value)} onPaste={paste}
          onKeyDown={(event) => {
            if ((event.key !== 'Enter' && event.code !== 'NumpadEnter') || event.nativeEvent.isComposing) return;
            event.preventDefault();
            if (!event.repeat) send();
          }} />
        <button type="button" aria-label="Прикрепить файл" disabled={disabled} onClick={() => act('file', { round: data.round })}>Файл</button>
        <button type="submit" disabled={disabled || !draft.trim()}>Отправить</button>
        {data.retry && <button type="button" disabled={data.busy || pasting} onClick={() => act('retry')}>Повторить</button>}
      </form>
    </section>
  );
};
