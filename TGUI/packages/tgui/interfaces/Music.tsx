import { useEffect, useRef, useState } from 'react';
import { useBackend } from '../backend';
import { Icon } from '../components';
import './Music.scss';

type Track = { id: string; title: string; artist: string; album?: string; duration: number };
type NowPlaying = { id: string; title: string; artist: string };
type Data = {
  labels: Record<string, string>;
  query: string;
  results: Track[];
  searching: boolean;
  status: string;
  current: NowPlaying | null;
  state: string;
  position: number;
  length: number;
  gain: number;
  global: boolean;
  listeners: number;
  progress: number;
};

const clamp01 = (value: number) => Math.min(1, Math.max(0, Number.isFinite(value) ? value : 0));
const time = (seconds: number) => {
  const total = Math.max(0, Math.floor(Number(seconds) || 0));
  return `${String(Math.floor(total / 60)).padStart(2, '0')}:${String(total % 60).padStart(2, '0')}`;
};
const duration = (seconds: number) => (Number(seconds) > 0 ? time(seconds) : '--:--');

export const Music = () => {
  const backend = useBackend<Data>();
  const data = backend.data || ({} as Data);
  const act = backend.act;
  const t = (key: string) => data.labels?.[key] || key;
  const glyph = (name: string) => <Icon aria-hidden="true" name={name} />;
  const results = data.results || [];
  const grouped = (() => {
    const order: string[] = [];
    const byArtist = new Map<string, Track[]>();
    for (const row of results) {
      const artist = (row?.artist || '').trim() || t('artist-unknown');
      let bucket = byArtist.get(artist);
      if (!bucket) {
        bucket = [];
        byArtist.set(artist, bucket);
        order.push(artist);
      }
      bucket.push(row);
    }
    return order.map(artist => ({ artist, rows: byArtist.get(artist) || [] }));
  })();
  const current = data.current;
  const length = Number(data.length) || 0;
  const state = typeof data.state === 'string' && data.state ? data.state : 'stopped';
  const playing = state === 'playing';
  const remote = Number(data.listeners) || 0;
  const progress = clamp01(Number(data.progress) || 0);
  const [query, setQuery] = useState(typeof data.query === 'string' ? data.query : '');
  const [seek, setSeek] = useState<number | null>(null);
  const [gain, setGain] = useState<number | null>(null);
  const [clock, setClock] = useState(Number(data.position) || 0);
  const sent = useRef(typeof data.query === 'string' ? data.query : '');
  const focused = useRef(false);
  const helpId = 'Music__help';

  const submit = (value: string) => {
    sent.current = value;
    act('search', { query: value });
  };

  useEffect(() => {
    const value = query.trim();
    const timer = setTimeout(() => {
      if (sent.current === value) return;
      sent.current = value;
      act('search', { query: value });
    }, 250);
    return () => clearTimeout(timer);
  }, [query]);

  useEffect(() => {
    const incoming = typeof data.query === 'string' ? data.query : '';
    if (focused.current || incoming === sent.current) return;
    sent.current = incoming;
    setQuery(incoming);
  }, [data.query]);

  useEffect(() => {
    const position = Number(data.position) || 0;
    setClock(position);
    if (!playing) return;
    const start = Date.now();
    const timer = setInterval(() => {
      const elapsed = position + (Date.now() - start) / 1000;
      setClock(length ? Math.min(length, elapsed) : elapsed);
    }, 500);
    return () => clearInterval(timer);
  }, [data.position, playing, length]);

  const shown = gain ?? clamp01(Number(data.gain ?? 0.5));
  const value = seek ?? Math.min(length || 0, clock);
  const transport = (label: string, icon: string, onClick: () => void, disabled: boolean, pressed?: boolean) =>
    <button type="button" className="Music__transport-button" aria-label={label} title={label} aria-pressed={pressed} disabled={disabled} onClick={onClick}>{glyph(icon)}<span>{label}</span></button>;

  return <main className="Music" aria-label={t('title')}>
    <header className="Music__crown">
      <div className={`Music__disc ${playing ? 'is-playing' : ''}`} aria-hidden="true" />
      <div className="Music__brand"><strong>{t('title')}</strong><span>{t('now-playing')}</span></div>
      <div className="Music__crown-actions">
        <span className={`Music__state is-${state}`}>{t(state)}</span>
        <button type="button" className="Music__close" aria-label={t('close')} title={t('close')} onClick={() => act('close')}>{glyph('times')}</button>
      </div>
    </header>
    {!!data.global && <div className="Music__banner" role="status">{glyph('globe')}<span>{t('global')}</span></div>}
    <div className="Music__body">
      <section className="Music__now" aria-label={t('now-playing')}>
        <div className="Music__now-title">
          <strong title={current?.title || undefined}>{current ? current.title || t('unavailable') : t('nothing-playing')}</strong>
          <small title={current?.artist || undefined}>{current ? current.artist || t('artist-unknown') : t('now-playing')}</small>
        </div>
        <input className="Music__seek" type="range" min={0} max={length || 0} step="0.1" value={value} aria-label={t('position')}
          disabled={!current || !length || state === 'stopped'}
          onChange={e => setSeek(Number(e.target.value))}
          onPointerUp={e => { act('seek', { value: Number(e.currentTarget.value) }); setSeek(null); }}
          onKeyUp={e => { if (['ArrowLeft', 'ArrowRight', 'Home', 'End', 'PageUp', 'PageDown'].includes(e.key)) { act('seek', { value: Number(e.currentTarget.value) }); setSeek(null); } }} />
        <div className="Music__timeline">
          <span>{t('position')} {time(value)}</span>
          <span>{t('duration')} {duration(length)}</span>
        </div>
        <div className="Music__progress" role="progressbar" aria-label={t('progress')} aria-valuemin={0} aria-valuemax={100} aria-valuenow={Math.round(progress * 100)}>
          <span style={{ width: `${Math.round(progress * 100)}%` }} />
        </div>
        <div className="Music__meta">
          <span>{t('progress')}: {Math.round(progress * 100)}%</span>
          <span>{t('listeners')}: {remote}</span>
        </div>
      </section>
      <div className="Music__transport">
        {transport(t(playing ? 'pause' : 'resume'), playing ? 'pause' : 'play', () => act(playing ? 'pause' : 'resume'), !current, playing)}
        {transport(t('stop'), 'stop', () => act('stop'), state === 'stopped')}
        {transport(t('next'), 'step-forward', () => act('next'), false)}
      </div>
      <label className="Music__volume">
        {glyph('volume-up')}<span className="Music__volume-label">{t('volume')}</span>
        <input className="Music__gain" type="range" min={0} max={1} step={0.01} value={shown} aria-label={t('volume')}
          onChange={e => setGain(Number(e.target.value))}
          onPointerUp={e => { act('gain', { value: Number(e.currentTarget.value) }); setGain(null); }}
          onKeyUp={e => { if (['ArrowLeft', 'ArrowRight', 'Home', 'End', 'PageUp', 'PageDown'].includes(e.key)) { act('gain', { value: Number(e.currentTarget.value) }); setGain(null); } }} />
        <output>{Math.round(shown * 100)}%</output>
      </label>
      <section className={`Music__results ${data.searching ? 'is-searching' : ''}`} aria-label={t('search')}>
        <input className="Music__search" type="search" aria-label={t('search')} aria-describedby={helpId} placeholder={t('search')} value={query}
          onFocus={() => { focused.current = true; act('focus'); }}
          onBlur={() => { focused.current = false; }}
          onChange={e => setQuery(e.target.value)}
          onKeyDown={e => { if (e.key === 'Enter') submit(query.trim()); }} />
        {!!data.searching && <div className="Music__searching" role="status">{t('searching')}</div>}
        {!!data.status && <div className="Music__status" role="alert">{t(data.status)}</div>}
        <div className="Music__list" role="list">
          {grouped.map(group => (
            <div className="Music__group" key={group.artist || '__unknown'} role="listitem">
              <div className="Music__group-head">
                <span title={group.artist}>{group.artist}</span>
                <small>{group.rows.length}</small>
              </div>
              {group.rows.map(row => {
                const id = row?.id || '';
                const available = !!id;
                const title = row?.title || t('unavailable');
                const album = row?.album || '';
                const label = `${t('play')}: ${title}`;
                return <div className={`Music__row ${available ? '' : 'is-unavailable'}`} key={id || title} role="listitem">
                  <div className="Music__row-text">
                    <span title={title}>{title}</span>
                    <small>{album ? `${album} - ` : ''}{duration(Number(row?.duration) || 0)}</small>
                  </div>
                  <button type="button" className="Music__row-play" aria-label={label} title={label} disabled={!available || !!data.searching} onClick={() => act('play', { id })}>
                    {glyph('play')}<span>{t('play')}</span>
                  </button>
                </div>;
              })}
            </div>
          ))}
          {!results.length && <div className="Music__empty">
            <span>{data.searching ? t('searching') : t('no-results')}</span>
            {!data.searching && !query.trim() && <small>{t('empty-query')}</small>}
          </div>}
        </div>
      </section>
      <footer className="Music__help" id={helpId}><strong>{t('command-help')}</strong><span>{t('command-desc')}</span></footer>
    </div>
  </main>;
};
