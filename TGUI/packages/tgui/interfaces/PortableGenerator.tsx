import { useState } from 'react';
import { useBackend } from '../backend';
import './PortableGenerator.scss';

type Data = {
  title: string; style: string; fuelLabel: string; labels: Record<string, string>; ready: boolean;
  anchored: boolean; starting: boolean; startProgress: number; on: boolean; clogged: boolean;
  target: number; min: number; max: number; efficiencyText: string; etaText: string;
  fuelFraction: number; fuelText: string; networkConnected: boolean; networkGood: boolean; networkText: string;
  canSwitch: boolean; voltage: string; nextVoltage: string; switchText: string;
};

export const PortableGenerator = () => {
  const { data, act } = useBackend<Data>();
  const [draft, setDraft] = useState<number | null>(null);
  const [numberDraft, setNumberDraft] = useState<string | null>(null);
  const t = (key: string) => data.labels?.[key] || key;
  const ready = !!data.ready;
  const target = draft ?? data.target ?? 0;
  const max = Math.max(0, data.max || 0);
  const fuel = Math.min(1, Math.max(0, data.fuelFraction || 0));
  const status = !ready ? t('waiting') : !data.anchored ? t('unanchored')
    : data.starting ? t('status-starting') : data.on ? t('status-running') : t('status-stopped');
  const style = ['plasma', 'uranium', 'welding', 'bananium', 'bananium-jr', 'pinch', 'sterling'].includes(data.style) ? data.style : 'steel';
  const powerChange = (value: number) => {
    if (!Number.isFinite(value)) return;
    const bounded = Math.max(0, Math.min(Math.floor(max), Math.round(value)));
    setDraft(bounded);
    act('power', { value: bounded });
  };
  return <main className={`PortableGenerator PortableGenerator--${style}`} aria-label={data.title}>
    <div className="PortableGenerator__chassis">
      {[0, 1, 2, 3].map(i => <i className="PortableGenerator__screw" key={i} aria-hidden="true" />)}
      <header className="PortableGenerator__heading">
        <div className="PortableGenerator__nameplate"><strong>{data.title}</strong><small>DL POWER / GEN</small></div>
        <div className="PortableGenerator__fuel-type">{data.fuelLabel}</div>
      </header>
      <section className="PortableGenerator__monitor" aria-label={t('target')}>
        <div className="PortableGenerator__glass">
          <div className="PortableGenerator__screen-label">{t('target')}</div>
          <div className="PortableGenerator__screen-body">
            <div className="PortableGenerator__power">{ready ? (data.target || 0).toFixed(1) : '—'}<small>{t('kilowatts')}</small></div>
            <dl className="PortableGenerator__readings">
              <div><dt>{t('efficiency')}</dt><dd>{ready ? data.efficiencyText : '—'}</dd></div>
              <div><dt>{t('eta')}</dt><dd>{ready ? data.etaText : '—'}</dd></div>
              <div><dt>{t('state')}</dt><dd>{status}</dd></div>
            </dl>
          </div>
        </div>
        <div className="PortableGenerator__bezel" aria-hidden="true"><span>MONITOR / GEN.</span><span>DC</span></div>
      </section>
      <section className="PortableGenerator__controls" aria-label={t('controls')}>
        <div className="PortableGenerator__starter">
          <span className="PortableGenerator__status"><i className={`PortableGenerator__lamp ${ready && data.on ? 'is-on' : ''}`} aria-hidden="true" />{status}</span>
          {data.starting && ready && data.anchored
            ? <progress max="1" value={data.startProgress} aria-label={t('status-starting')} />
            : <button type="button" className={data.on ? 'is-stop' : ''} disabled={!ready || !data.anchored}
                onClick={() => act(data.on ? 'stop' : 'start')}>{t(data.on ? 'stop' : 'start')}</button>}
        </div>
        <div className="PortableGenerator__regulator">
          <label className="PortableGenerator__reg-label"><span>{t('setpoint')}</span>
            <span className="PortableGenerator__number"><input type="number" min="0" max={Math.floor(max)} step="1"
              aria-label={t('target')} value={numberDraft ?? target} disabled={!ready}
              onFocus={event => setNumberDraft(event.currentTarget.value)}
              onChange={event => setNumberDraft(event.currentTarget.value)}
              onBlur={event => { if (event.currentTarget.value !== '') powerChange(Number(event.currentTarget.value)); setDraft(null); setNumberDraft(null); }}
              onKeyDown={event => { if (event.key === 'Enter') event.currentTarget.blur(); }} />{t('kilowatts')}</span>
          </label>
          <input type="range" min="0" max={Math.floor(max)} step="1" value={target} disabled={!ready || max <= 0}
            aria-label={t('target')} aria-valuetext={`${target} ${t('kilowatts')}`}
            onChange={event => powerChange(Number(event.currentTarget.value))}
            onPointerUp={() => setDraft(null)} onPointerCancel={() => setDraft(null)} onKeyUp={() => setDraft(null)} onBlur={() => setDraft(null)} />
          <div className="PortableGenerator__scale"><span>0</span><span>{ready ? `${data.min}–${max} ${t('kilowatts')}` : '—'}</span></div>
        </div>
        {data.canSwitch && <div className="PortableGenerator__output">
          <span>{t('output')}</span><span className="PortableGenerator__voltage">{data.voltage}</span>
          <button type="button" disabled={!ready || data.on} onClick={() => act('switch')}>{data.switchText}</button>
          {data.on && <small>{t('line-lock')}</small>}
        </div>}
      </section>
      <section className="PortableGenerator__fuel" aria-label={t('fuel')}>
        <div className="PortableGenerator__fuel-head"><span>{t('fuel')}</span><span>{t('remaining')}: {ready ? data.fuelText : '—'}</span></div>
        <div className="PortableGenerator__fuel-body"><div>
          <div className="PortableGenerator__fuel-track" role="progressbar" aria-label={t('fuel-use')} aria-valuenow={fuel * 100} aria-valuemin={0} aria-valuemax={100}>
            <div style={{ width: `${fuel * 100}%` }} /></div>
          <div className="PortableGenerator__fuel-foot"><span>{t('fuel-use')}</span><span>{Math.round(fuel * 100)} %</span></div>
        </div><button type="button" disabled={!ready} onClick={() => act('eject')}>{t('eject')}</button></div>
        {ready && data.clogged && <div className="PortableGenerator__warning" role="alert">⚠ {t('clogged')}</div>}
      </section>
      <div className={`PortableGenerator__network ${data.networkConnected && !data.networkGood ? 'is-warning' : ''}`}>
        <span>{t('network')}{data.canSwitch ? ` / ${data.voltage}` : ''}</span><span>{ready ? data.networkText : '—'}</span>
      </div>
      <div className="PortableGenerator__service" aria-hidden="true"><div className="PortableGenerator__vent" /><div className="PortableGenerator__cables"><i /><i /><i /></div><span>⚡ {t('maintenance')}</span></div>
      <footer className="PortableGenerator__footer"><span>{t('section')}</span><span>DL / GEN</span></footer>
    </div><div className="PortableGenerator__hazard" aria-hidden="true" />
  </main>;
};
