import { useLayoutEffect, useRef } from 'react';
import { useBackend } from '../backend';
import { Icon } from '../components';
import './Pda.scss';

type Program = { id: string; name: string; kind: string; installed: boolean; canInstall: boolean; canUninstall: boolean };
type Data = {
  labels: Record<string, string>; ready: boolean; view: number; time: string;
  owner: string; idOwner: string; job: string; company: string; companyColor: string;
  station: string; alert: string; alertColor: string; instructions: string; war: string;
  balance: string; ship: string; date: string; address: string; programTitle: string;
  hasId: boolean; hasPen: boolean; hasPai: boolean; flashlight: boolean; canPlayMusic: boolean; hasUplink: boolean;
  programs: Program[];
};

const programIcon = (kind: string) => /note/i.test(kind) ? 'file-alt'
  : /manifest|crew/i.test(kind) ? 'users' : /news/i.test(kind) ? 'newspaper'
  : /nav/i.test(kind) ? 'compass' : /med/i.test(kind) ? 'heartbeat'
  : /mail/i.test(kind) ? 'envelope' : /wanted|forensic/i.test(kind) ? 'search' : 'microchip';
const color = (value: string) => /^#?[\da-f]{6}([\da-f]{2})?$/i.test(value || '')
  ? (value.startsWith('#') ? value : '#' + value) : undefined;

export const Pda = () => {
  const { data, act } = useBackend<Data>();
  const aperture = useRef<HTMLDivElement>(null);
  const t = (key: string) => data.labels?.[key] || key;
  const view = data.view ?? 0;
  const ready = !!data.ready;
  const programs = Array.isArray(data.programs) ? data.programs : [];
  const title = view === 1 ? t('programs') : view === 2 ? t('settings') : view === 3 ? data.programTitle : t('shift');

  // Native cartridge controls are positioned above this exact, measured rectangle.
  useLayoutEffect(() => {
    const node = aperture.current;
    if (!node || view !== 3 || !data.programTitle) return;
    let previous = '';
    const measure = () => {
      const r = node.getBoundingClientRect();
      if (r.width <= 0 || r.height <= 0) return;
      const bounds = { x: r.x / innerWidth, y: r.y / innerHeight, w: r.width / innerWidth, h: r.height / innerHeight };
      const signature = JSON.stringify(bounds);
      if (signature !== previous) { previous = signature; act('viewport', bounds); }
    };
    measure();
    const observer = new ResizeObserver(measure);
    observer.observe(node);
    window.addEventListener('resize', measure);
    return () => { observer.disconnect(); window.removeEventListener('resize', measure); };
  }, [view, data.programTitle]);

  const changeView = (value: number) => act('view', { view: value });
  const app = (program: Program, manage = false) => (
    <div className="Pda__app" key={program.id}>
      <button type="button" className="Pda__appLaunch" disabled={!ready} onClick={() => act('program', { id: program.id })}>
        <Icon aria-hidden="true" name={programIcon(program.kind)} />
        <span>{program.name}</span>
        <small>{program.installed ? t('installed') : t('cartridge')}</small>
      </button>
      {manage && (program.canInstall || program.canUninstall) && (
        <button type="button" className="Pda__install" disabled={!ready}
          onClick={() => act(program.canInstall ? 'install' : 'uninstall', { id: program.id })}>
          <Icon aria-hidden="true" name={program.canInstall ? 'download' : 'eject'} /> {t(program.canInstall ? 'install' : 'uninstall')}
        </button>
      )}
    </div>
  );
  const detail = (key: string, value: string, field = key) => value && (
    <button type="button" className="Pda__detail" key={key} onClick={() => act('copy', { field })}>
      <span>{t(key)}</span><strong>{value}</strong>
    </button>
  );
  const setting = (key: string, glyph: string, action: string, visible = true) => visible && (
    <button type="button" className="Pda__setting" disabled={!ready} onClick={() => act(action)}>
      <Icon aria-hidden="true" name={glyph} /><span>{t(key)}</span><Icon aria-hidden="true" name="chevron-right" />
    </button>
  );

  return (
    <div className="Pda">
      <div className="Pda__device">
        <header className="Pda__bezel">
          <div className="Pda__brand"><strong>ORBITAL / НТ</strong><small>PERSONAL DECK · SERIES 09</small></div>
          <div className="Pda__vents" aria-hidden="true" />
          <span className={'Pda__pilot' + (data.flashlight ? ' Pda__pilot--lit' : '')} aria-label={t('flashlight') + ': ' + t(data.flashlight ? 'on' : 'off')}>●</span>
          <button type="button" className="Pda__close" aria-label={t('close')} onClick={() => act('close')}><Icon aria-hidden="true" name="times" /></button>
        </header>
        <div className="Pda__display">
          <header className="Pda__screenHead">
            <div><div className="Pda__eyebrow">{view === 0 ? 'PERSONAL DECK' : 'ROBUST / OS'}</div><h1>{title}</h1></div>
            <button type="button" className="Pda__clock" onClick={() => act('copy', { field: 'time' })}>
              <strong>{data.time || '00:00:00'}</strong><span>{t('time')}</span>
            </button>
          </header>
          {view === 3 && !!data.programTitle ? (
            <div className="Pda__programArea" ref={aperture} aria-label={data.programTitle} />
          ) : (
            <main className="Pda__content">
              {!ready && <p role="status">{t('waiting')}</p>}
              {view === 0 && (
                <div className="Pda__home">
                  <aside className="Pda__identity">
                    <div className="Pda__portrait"><Icon aria-hidden="true" name={data.hasId ? 'user' : 'address-card'} /></div>
                    <div className="Pda__eyebrow">{t('identity')}</div>
                    <button type="button" className="Pda__idName" onClick={() => act('copy', { field: 'id' })}>
                      {data.hasId ? data.idOwner || t('unassigned') : t('no-id')}
                    </button>
                    <p>{data.hasId ? data.job || t('unassigned') : ''}</p>
                    {!!data.company && <p style={{ color: color(data.companyColor) }}>{data.company}</p>}
                    {!!data.owner && <div className="Pda__owner"><span>{t('owner')}</span><button type="button" onClick={() => act('copy', { field: 'owner' })}>{data.owner}</button></div>}
                  </aside>
                  <section className="Pda__desktop">
                    <div className="Pda__appGrid">{programs.slice(0, 4).map(program => app(program))}
                      {!programs.length && <div className="Pda__empty"><Icon aria-hidden="true" name="microchip" /><p>{t('empty')}</p></div>}
                    </div>
                    {!!programs.length && <button type="button" className="Pda__allPrograms" onClick={() => changeView(1)}>{t('programs')} <span>{programs.length.toString().padStart(2, '0')}</span><Icon aria-hidden="true" name="arrow-right" /></button>}
                    <div className="Pda__details">
                      {detail('station', data.station)}
                      {detail('alert', data.alert)}
                      {detail('balance', data.balance)}
                      {detail('ship', data.ship)}
                      {detail('date', data.date)}
                      {detail('war', data.war)}
                    </div>
                    {!!data.instructions && <button type="button" className="Pda__instructions" onClick={() => act('copy', { field: 'instructions' })}>
                      <span className="Pda__alertDot" style={{ backgroundColor: color(data.alertColor) }} aria-hidden="true" />{data.instructions}
                    </button>}
                  </section>
                </div>
              )}
              {view === 1 && <div className="Pda__appGrid Pda__appGrid--catalog">{programs.map(program => app(program, true))}{!programs.length && <p>{t('empty')}</p>}</div>}
              {view === 2 && <div className="Pda__settings">
                <button type="button" className="Pda__setting" disabled={!ready} aria-pressed={!!data.flashlight} onClick={() => act('flashlight')}>
                  <Icon aria-hidden="true" name="lightbulb" /><span>{t('flashlight')}</span><small>{t(data.flashlight ? 'on' : 'off')}</small>
                </button>
                {setting('ringtone', 'bell', 'ringtone')}
                {setting('music', 'music', 'music', data.canPlayMusic)}
                {setting('uplink', 'unlock', 'uplink', data.hasUplink)}
                {setting('lock-uplink', 'lock', 'lockUplink', data.hasUplink)}
                {detail('address', data.address)}
              </div>}
            </main>
          )}
          <nav className="Pda__dock" aria-label={t('programs')}>
            {[[0, 'overview', 'th-large'], [1, 'programs', 'microchip'], [2, 'settings', 'cog']].map(([value, label, glyph]) => (
              <button type="button" key={value} aria-pressed={view === value} onClick={() => changeView(Number(value))}><Icon aria-hidden="true" name={String(glyph)} /><span>{t(String(label))}</span></button>
            ))}
            {!!data.programTitle && <button type="button" aria-pressed={view === 3} aria-label={t('active-program')} onClick={() => changeView(3)}><Icon aria-hidden="true" name="window-maximize" /><span>{data.programTitle}</span></button>}
            {!!data.programTitle && <button type="button" aria-label={t('close-program')} onClick={() => act('closeProgram')}><Icon aria-hidden="true" name="times" /></button>}
          </nav>
          <footer className="Pda__screenFoot"><span>NT-PDA / 009</span><button type="button" onClick={() => act('copy', { field: 'address' })}>{data.address || '—'}</button></footer>
        </div>
        <footer className="Pda__hardware">
          <button type="button" aria-label={t('overview')} onClick={() => changeView(0)}>HOME</button>
          <button type="button" disabled={!ready} aria-label={t('flashlight')} aria-pressed={!!data.flashlight} onClick={() => act('flashlight')}>LIGHT</button>
          <button type="button" aria-label={t('close')} onClick={() => act('close')}><Icon aria-hidden="true" name="power-off" /></button>
          <span className="Pda__hardwareLabel">PERSONAL DECK</span>
          <div className="Pda__slots">
            <button type="button" disabled={!ready} aria-pressed={!!data.hasPen} aria-label={t('slot-pen')} onClick={() => act('pen')}><Icon aria-hidden="true" name="pen" /></button>
            <button type="button" disabled={!ready} aria-pressed={!!data.hasPai} aria-label={t('slot-pai')} onClick={() => act('pai')}><Icon aria-hidden="true" name="robot" /></button>
            <button type="button" disabled={!ready} aria-pressed={!!data.hasId} aria-label={t('slot-id')} onClick={() => act('id')}>ID <Icon aria-hidden="true" name="eject" /></button>
          </div>
        </footer>
        <span className="Pda__resize" aria-label={t('resize')} />
      </div>
    </div>
  );
};
