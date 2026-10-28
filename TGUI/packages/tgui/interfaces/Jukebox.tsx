import { EmbeddedSelect } from '../components/EmbeddedSelect';
import { useEffect, useMemo, useState } from 'react';
import { useBackend } from '../backend';
import { Icon } from '../components';
import './Jukebox.scss';

type Track = { id: string; name: string; length: number; available?: boolean };
type Entry = { id: string; track: string };
type Playlist = { name: string; tracks: { id: string }[] };
type Data = {
  rows: (Entry & { sourceIndex: number })[]; total: number; sourceCount: number; page: number; catalogBusy: boolean; catalogTab: string; catalogCount: number;
  tracks: Track[]; labels: Record<string, string>; selected: string; position: number; gain: number;
  playing: boolean; paused: boolean; powered: boolean; repeat: number; revision: number; queue: Entry[];
  favorites: { id: string }[]; playlists: Playlist[]; libraryLoaded: boolean; libraryBusy: boolean; status: string;
};
const time = (seconds: number) => `${Math.floor(Math.max(0, seconds || 0) / 60)}:${String(Math.floor(Math.max(0, seconds || 0) % 60)).padStart(2, '0')}`;

export const Jukebox = () => {
  const { data, act } = useBackend<Data>();
  const t = (key: string) => data.labels?.[key] || key;
  const tracks = data.tracks || [], queue = data.queue || [], playlists = data.playlists || [];
  const byId = useMemo(() => new Map(tracks.map(track => [track.id, track])), [tracks]);
  const favorites = useMemo(() => new Set((data.favorites || []).map(track => track.id)), [data.favorites]);
  const [tab, setTab] = useState('catalog'), [query, setQuery] = useState(''), [page, setPage] = useState(0);
  const [playlist, setPlaylist] = useState(''), [dialog, setDialog] = useState(''), [draft, setDraft] = useState('');
  const [submitted, setSubmitted] = useState(false), [visualizer, setVisualizer] = useState(false);
  const [gain, setGain] = useState<number | null>(null), [seek, setSeek] = useState<number | null>(null);
  const [clock, setClock] = useState(0);
  const selected = byId.get(data.selected), length = selected?.length || 0;
  const busy = data.libraryBusy || !data.libraryLoaded;
  useEffect(() => {
    if (!playlists.some(p => p.name === playlist)) setPlaylist(playlists[0]?.name || '');
  }, [data.playlists]);
  useEffect(() => {
    if (submitted && !data.libraryBusy) {
      if (data.status === 'saved') {
        if (dialog === 'create' || dialog === 'rename') { setPlaylist(draft.trim()); changeTab('playlists'); }
        setDialog(''); setDraft('');
      }
      setSubmitted(false);
    }
  }, [data.libraryBusy, data.status]);
  // Interpolate the clock locally. No network/UI bridge messages on animation frames.
  useEffect(() => {
    setClock(data.position || 0);
    if (!data.playing) return;
    const start = Date.now(), position = data.position || 0;
    const timer = setInterval(() => setClock(Math.min(length, position + (Date.now() - start) / 1000)), 500);
    return () => clearInterval(timer);
  }, [data.position, data.playing, length]);
  const activePlaylist = playlists.find(p => p.name === playlist);
  // Catalog searches run in the native metadata index. Only a visible page and
  // the selected song cross the bridge, independent of the full library size.
  useEffect(() => {
    const timer = setTimeout(() => act('browse', { tab, query, page, playlist }), 150);
    return () => clearTimeout(timer);
  }, [tab, query, page, playlist]);
  const rows = data.catalogTab === tab ? data.rows || [] : [];
  const pages = Math.max(1, Math.ceil((data.total || 0) / 25)), currentPage = data.page || 0;
  const changeTab = (value: string) => { setTab(value); setPage(0); };
  const command = (action: string, payload = {}) => act(action, { revision: data.revision, ...payload });
  const library = (action: string, payload = {}) => act(action, { playlist, ...payload });
  const submit = () => {
    setSubmitted(true);
    if (dialog === 'create') library('playlist-create', { value: draft.trim() });
    if (dialog === 'rename') library('playlist-rename', { value: draft.trim() });
    if (dialog === 'delete') library('playlist-delete');
    if (dialog === 'clear') command('clear');
    if (dialog === 'import') library('library-import', { value: draft });
    if (dialog === 'clear') { setDialog(''); setSubmitted(false); }
  };
  const openDialog = (mode: string) => {
    setDialog(mode); setSubmitted(false);
    setDraft(mode === 'rename' ? playlist : mode === 'export'
      ? JSON.stringify({ Favorites: [...favorites], Playlists: Object.fromEntries(playlists.map(p => [p.name, p.tracks.map(t => t.id)])) }, null, 2) : '');
  };
  const glyph = (name: string) => <Icon aria-hidden="true" name={name} />;
  const iconButton = (label: string, icon: string, onClick: () => void, disabled = false, pressed?: boolean) =>
    <button type="button" className="Jukebox__icon" aria-label={label} title={label} onClick={onClick} disabled={disabled} aria-pressed={pressed}>{glyph(icon)}</button>;

  return <main className="Jukebox" aria-label={t('title')}>
    <header className="Jukebox__crown">
      <div className="Jukebox__brand"><strong>NanoTrasen</strong><span>JUKEBOX · STEREOPHONIC</span></div>
      <div className="Jukebox__record" aria-hidden="true" />
      <button type="button" className="Jukebox__close" aria-label={t('close')} title={t('close')} onClick={() => act('close')}>{glyph('times')}</button>
    </header>
    <div className="Jukebox__chrome" aria-hidden="true" />
    <div className="Jukebox__body">
      <section className="Jukebox__screen" aria-label={t('playing')}>
        <div className="Jukebox__kicker"><span>{t(data.playing ? 'playing' : data.paused ? 'paused' : 'stopped')}</span><span>STEREO</span></div>
        <div className="Jukebox__song"><strong title={selected?.name}>{selected?.name || t('no-track')}</strong>
          {iconButton(t('favorite'), 'star', () => library('favorite', { id: data.selected }), busy || !selected, favorites.has(data.selected))}</div>
        {visualizer && <div className={`Jukebox__spectrum ${data.playing ? 'is-playing' : ''}`} role="img" aria-label={t('visualizer-hint')}>
          {Array.from({ length: 24 }, (_, i) => <span key={i} style={{ height: `${18 + (i * 29 % 80)}%`, animationDelay: `${-(i % 7) / 5}s` }} />)}
        </div>}
        <input aria-label={t('seek')} className="Jukebox__seek" type="range" min="0" max={length} step="0.1" value={seek ?? Math.min(length, clock)} disabled={!length || !data.powered}
          onChange={e => setSeek(Number(e.target.value))} onPointerUp={e => { act('seek', { value: Number(e.currentTarget.value) }); setSeek(null); }}
          onKeyUp={e => { if (['ArrowLeft', 'ArrowRight', 'Home', 'End', 'PageUp', 'PageDown'].includes(e.key)) { act('seek', { value: Number(e.currentTarget.value) }); setSeek(null); } }} />
        <div className="Jukebox__timeline"><span>{time(seek ?? clock)}</span><span>{time(length)}</span></div>
      </section>
      <div className="Jukebox__transport">
        {iconButton(t('restart'), 'step-backward', () => act('seek', { value: 0 }), !selected || !data.powered)}
        <button type="button" className="Jukebox__play" disabled={!data.powered || !selected && !queue.length} onClick={() => act(data.playing ? 'pause' : 'play')}>{glyph(data.playing ? 'pause' : 'play')}{t(data.playing ? 'pause' : 'play')}</button>
        {iconButton(t('stop'), 'stop', () => act('stop'), !selected)}
        {iconButton(t('next'), 'step-forward', () => command('next'), !queue.length && data.repeat !== 2)}
      </div>
      <div className="Jukebox__modes">
        <button type="button" aria-pressed={data.repeat !== 0} onClick={() => command('repeat', { index: ((data.repeat || 0) + 1) % 3 })}>{glyph(data.repeat === 1 ? 'repeat' : 'retweet')}{t(['repeat-off', 'repeat-track', 'repeat-queue'][data.repeat || 0])}</button>
        {iconButton(t('random'), 'shuffle', () => act('random'), !data.catalogCount || queue.length >= 256)}
        {iconButton(t('visualizer'), 'wave-square', () => setVisualizer(!visualizer), false, visualizer)}
      </div>
      <label className="Jukebox__volume">{glyph('volume-up')}<span>{t('volume')}</span>
        <input type="range" min="0" max="100" value={gain ?? Math.round((data.gain ?? .75) * 100)} aria-label={t('volume')}
          onChange={e => setGain(Number(e.target.value))}
          onPointerUp={e => { act('gain', { value: Number(e.currentTarget.value) / 100 }); setGain(null); }}
          onKeyUp={e => { if (['ArrowLeft', 'ArrowRight', 'Home', 'End', 'PageUp', 'PageDown'].includes(e.key)) { act('gain', { value: Number(e.currentTarget.value) / 100 }); setGain(null); } }} />
        <output>{gain ?? Math.round((data.gain ?? .75) * 100)}%</output>
      </label>
      {!data.powered && <div className="Jukebox__notice">{t('no-power')}</div>}
      <section className="Jukebox__panel" aria-label={t(tab)}>
        <nav className="Jukebox__tabs" aria-label={t('title')}>{['catalog', 'favorites', 'queue', 'playlists'].map(value =>
          <button key={value} type="button" aria-pressed={tab === value} onClick={() => changeTab(value)}>{t(value)}{value === 'queue' ? ` · ${queue.length}` : ''}</button>)}</nav>
        {tab !== 'queue' && <input className="Jukebox__search" type="search" aria-label={t('search')} placeholder={t('search')} value={query} onFocus={() => act('focus')} onChange={e => { setQuery(e.target.value); setPage(0); }} />}
        {(tab === 'catalog' || tab === 'playlists') && <div className="Jukebox__playlist-tools">
          <EmbeddedSelect value={playlist} aria-label={t('playlists')} onChange={e => { setPlaylist(e.target.value); setPage(0); }} disabled={busy || !playlists.length}>
            {!playlists.length && <option value="">{t('playlists')}</option>}{playlists.map(p => <option key={p.name}>{p.name}</option>)}
          </EmbeddedSelect>
          {iconButton(t('new'), 'plus', () => openDialog('create'), busy || playlists.length >= 40)}
          {tab === 'playlists' && <>{iconButton(t('rename'), 'pen', () => openDialog('rename'), busy || !playlist)}
            {iconButton(t('delete'), 'trash', () => openDialog('delete'), busy || !playlist)}
            {iconButton(t('queue-playlist'), 'list', () => library('playlist-queue'), busy || !activePlaylist?.tracks.length)}</>}
        </div>}
        <div className="Jukebox__list" role="list">
          {rows.map(entry => {
            const track = byId.get(entry.track), available = !!track && track.available !== false;
            const movable = tab !== 'catalog', index = entry.sourceIndex;
            const move = (to: number) => tab === 'queue' ? command('move', { id: entry.id, index: to })
              : library(tab === 'favorites' ? 'favorite-move' : 'playlist-move', { id: entry.track, index: to });
            const rowButton = (label: string, icon: string, onClick: () => void, disabled = false, pressed?: boolean) =>
              iconButton(label, icon, onClick, disabled || data.catalogBusy, pressed);
            return <div className={`Jukebox__row ${available ? '' : 'is-unavailable'}`} key={entry.id} role="listitem">
              {tab === 'queue' ? <input type="number" className="Jukebox__index" disabled={data.catalogBusy} aria-label={`${t('position')}: ${track?.name || entry.track}`} min={1} max={data.sourceCount} defaultValue={index + 1} key={`${entry.id}-${index}`} onFocus={() => act('focus')}
                onBlur={e => { const n = Number(e.currentTarget.value); if (Number.isInteger(n) && n >= 1 && n <= data.sourceCount) move(n - 1); e.currentTarget.value = String(index + 1); }} onKeyDown={e => { if (e.key === 'Enter') e.currentTarget.blur(); }} /> : <span className="Jukebox__number">{index + 1}</span>}
              <div className="Jukebox__track"><span title={track?.name || entry.track}>{track?.name || entry.track}</span><small>{available ? track.length ? time(track.length) : '…' : t('unavailable')}</small></div>
              <div className="Jukebox__row-actions">
                {movable && <>{rowButton(t('up'), 'arrow-up', () => move(index - 1), index === 0 || tab !== 'queue' && busy)}
                  {rowButton(t('down'), 'arrow-down', () => move(index + 1), index === data.sourceCount - 1 || tab !== 'queue' && busy)}</>}
                {tab === 'queue' ? rowButton(t('remove'), 'times', () => command('remove', { id: entry.id })) : <>
                  {tab === 'playlists' ? rowButton(t('remove-playlist'), 'times', () => library('playlist-remove', { id: entry.track }), busy)
                    : playlist && rowButton(t('add-playlist'), 'list', () => library('playlist-add', { id: entry.track }), busy || !available || !!activePlaylist?.tracks.some(t => t.id === entry.track))}
                  {rowButton(t('add-first'), 'angle-double-up', () => command('add-first', { id: entry.track }), !available || queue.length >= 256)}
                  {rowButton(t('add'), 'plus', () => command('add', { id: entry.track }), !available || queue.length >= 256)}
                  {rowButton(t('favorite'), 'star', () => library('favorite', { id: entry.track }), busy, favorites.has(entry.track))}
                </>}
                {tab === 'catalog' && rowButton(t('play-now'), 'play', () => command('play-track', { id: entry.track }), !available || !data.powered)}
              </div>
            </div>;
          })}
          {!rows.length && <div className="Jukebox__empty">{t(data.catalogBusy ? 'loading' : 'empty')}</div>}
        </div>
        <footer className="Jukebox__tools"><span>{data.total || 0} {t('tracks')}</span>
          {pages > 1 && <div>{iconButton('‹', 'chevron-left', () => setPage(currentPage - 1), currentPage === 0)}<span>{currentPage + 1}/{pages}</span>{iconButton('›', 'chevron-right', () => setPage(currentPage + 1), currentPage === pages - 1)}</div>}
          {tab === 'queue' ? iconButton(t('clear'), 'trash', () => openDialog('clear'), !queue.length)
            : iconButton(t('manage'), 'sliders', () => openDialog('manage'), busy)}
        </footer>
        <div className="Jukebox__feedback" aria-live="polite">{data.libraryBusy ? t('busy') : data.status ? t(data.status) : ''}
          {!data.libraryLoaded && !data.libraryBusy && <button type="button" onClick={() => library('library-load')}>{t('reload')}</button>}</div>
      </section>
    </div>
    <div className="Jukebox__speaker" aria-hidden="true"><span>NT</span></div>
    <div className="Jukebox__base" aria-hidden="true" />
    <div className="Jukebox__resize" aria-hidden="true" title={t('resize')}>◢</div>
    {dialog && <div className="Jukebox__shade"><section className="Jukebox__dialog" role="dialog" aria-modal="true" aria-label={t(dialog === 'manage' ? 'manage' : dialog === 'clear' ? 'confirm-clear' : dialog === 'delete' ? 'confirm-delete' : dialog === 'create' ? 'new' : dialog)}>
      <strong>{t(dialog === 'clear' ? 'confirm-clear' : dialog === 'delete' ? 'confirm-delete' : dialog === 'create' ? 'new' : dialog)}</strong>
      {dialog === 'manage' ? <div className="Jukebox__dialog-buttons"><button type="button" onClick={() => openDialog('import')}>{t('import')}</button><button type="button" onClick={() => openDialog('export')}>{t('export')}</button></div>
        : dialog === 'import' || dialog === 'export' ? <><p>{t('import-hint')}</p><textarea aria-label={t(dialog)} value={draft} readOnly={dialog === 'export'} maxLength={262144} onFocus={() => act('focus')} onChange={e => setDraft(e.target.value)} /></>
          : dialog !== 'delete' && dialog !== 'clear' && <input aria-label={t('playlist-name')} value={draft} maxLength={35} onFocus={() => act('focus')} onChange={e => setDraft(e.target.value)} onKeyDown={e => { if (e.key === 'Enter' && draft.trim() && !busy) submit(); }} />}
      {(data.status === 'invalid' || data.status === 'save-error') && <p role="alert">{t(data.status)}</p>}
      <div className="Jukebox__dialog-buttons"><button type="button" onClick={() => setDialog('')} disabled={submitted && busy}>{t('cancel')}</button>
        {!['manage', 'export'].includes(dialog) && <button type="button" disabled={dialog !== 'clear' && busy || !['delete', 'clear'].includes(dialog) && !draft.trim()} onClick={submit}>{t(dialog === 'create' ? 'create' : dialog === 'import' ? 'import' : dialog === 'delete' ? 'delete' : dialog === 'clear' ? 'clear' : 'save')}</button>}</div>
    </section></div>}
  </main>;
};
