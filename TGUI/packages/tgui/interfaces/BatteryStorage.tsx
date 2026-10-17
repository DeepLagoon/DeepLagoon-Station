import { useState } from 'react';
import { useBackend } from '../backend';
import './BatteryStorage.scss';

type Data = {
  title: string; smes: boolean; ready: boolean; labels: Record<string, string>;
  input: boolean; output: boolean; inputPowered: boolean; outputPowered: boolean;
  fraction: number; percentText: string; energyText: string; etaLabel: string; etaText: string;
  inputText: string; outputText: string; chargeText: string; dischargeText: string;
  chargeLimitText: string; dischargeLimitText: string; passthroughText: string; passthrough: number;
  chargeLimit: number; chargeMin: number; chargeMax: number;
  dischargeLimit: number; dischargeMin: number; dischargeMax: number;
};

const digitSegments = ['abcdef', 'bc', 'abged', 'abgcd', 'fgbc', 'afgcd', 'afgecd', 'abc', 'abcdefg', 'abfgcd'];
const segmentShapes = {
  a: '3,1 17,1 20,4 17,7 3,7 0,4', b: '18,8 21,5 21,21 18,24 15,21 15,11',
  c: '18,26 21,29 21,45 18,48 15,45 15,29', d: '3,46 17,46 20,49 17,52 3,52 0,49',
  e: '2,26 5,29 5,45 2,48 0,45 0,29', f: '2,8 5,11 5,21 2,24 0,21 0,5',
  g: '3,23 17,23 20,26 17,29 3,29 0,26',
};

function PowerCounter({ watts, label, labels }: { watts: number; label: string; labels: Data['labels'] }) {
  const value = Number.isFinite(watts) ? Math.max(0, watts) : 0;
  const scale = value >= 1e9 ? 3 : value >= 1e6 ? 2 : value >= 1000 ? 1 : 0;
  const text = (value / 1000 ** scale).toFixed(2).padStart(7, '0');
  const unit = labels[['watts', 'kilowatts', 'megawatts', 'gigawatts'][scale]];
  return <div className="BatteryStorage__segments" role="img" aria-label={label}>
    {text.split('').map((digit, index) => digit === '.'
      ? <i key={index} className="BatteryStorage__decimal" aria-hidden="true" />
      : <svg key={index} viewBox="0 0 22 53" aria-hidden="true">
        {Object.entries(segmentShapes).map(([segment, points]) =>
          <polygon key={segment} points={points} fill="currentColor" opacity={digitSegments[Number(digit)]?.includes(segment) ? 1 : 0.075} />)}
      </svg>)}
    <span className="BatteryStorage__unit" aria-hidden="true">{unit}</span>
  </div>;
}

// Keep the thumb under the pointer while fresh server snapshots arrive.
function Regulator({ label, text, value = 0, min = 0, max = 0, disabled, change }: {
  label: string; text: string; value: number; min: number; max: number; disabled: boolean; change: (value: number) => void;
}) {
  const [draft, setDraft] = useState<number | null>(null);
  return <label className="BatteryStorage__regulator">
    <span className="BatteryStorage__reg-label"><span>{label}</span><output>{text || '—'}</output></span>
    <input type="range" aria-label={label} aria-valuetext={text} min={min} max={max} step="any"
      value={Math.min(max, Math.max(min, draft ?? value))} disabled={disabled || max <= min}
      onPointerDown={event => setDraft(Number(event.currentTarget.value))}
      onChange={event => { const rate = Number(event.currentTarget.value); setDraft(rate); change(rate); }}
      onPointerUp={() => setDraft(null)} onPointerCancel={() => setDraft(null)} onBlur={() => setDraft(null)} onKeyUp={() => setDraft(null)} />
  </label>;
}

export const BatteryStorage = () => {
  const { data, act } = useBackend<Data>();
  const t = (key: string) => data.labels?.[key] || key;
  const ready = !!data.ready;
  const fraction = Math.min(1, Math.max(0, data.fraction || 0));
  const switchRow = (side: 'input' | 'output') => <div className="BatteryStorage__switch-row">
    <div className="BatteryStorage__switch-copy">
      <span><i className={`BatteryStorage__lamp ${ready && data[side] ? 'is-on' : ''}`} aria-hidden="true" />{t(side)}</span>
      <span className="BatteryStorage__line-value">{t('network')} {ready ? data[`${side}Text`] : '—'}</span>
      <span className="BatteryStorage__breaker-status">{ready ? t(data[side] ? 'on' : 'off') : '—'}</span>
      <span className={`BatteryStorage__network ${ready && data[`${side}Powered`] ? 'is-powered' : ''}`}>
        {ready ? t(data[`${side}Powered`] ? 'network-powered' : 'network-unpowered') : '—'}
      </span>
    </div>
    <button type="button" className="BatteryStorage__toggle" aria-label={t(side)} aria-pressed={!!data[side]}
      disabled={!ready} onClick={() => act(side, { on: !data[side] })}>
      <span className="BatteryStorage__lever" aria-hidden="true" />
    </button>
  </div>;

  return <main className={`BatteryStorage ${data.smes ? 'BatteryStorage--smes' : ''}`} aria-label={data.title}>
    <div className="BatteryStorage__chassis">
      {[0, 1, 2, 3].map(i => <i className="BatteryStorage__screw" key={i} aria-hidden="true" />)}
      <header className="BatteryStorage__heading">
        <div><strong>{t(data.smes ? 'smes-model' : 'substation-model')}</strong>
          <small>{t(data.smes ? 'smes-description' : 'substation-description')}</small></div>
        <span className="BatteryStorage__plate">DL POWER<br />7.2 REV 6</span>
      </header>
      <div className="BatteryStorage__body">
        <section className="BatteryStorage__monitor" aria-label={t('stored')}>
          <div className="BatteryStorage__glass">
            <div className="BatteryStorage__screen-label"><span>{t('stored')}</span><span aria-hidden="true">{ready ? '●' : '○'}</span></div>
            <div className="BatteryStorage__charge">{ready ? data.percentText : '—'}</div>
            <div className="BatteryStorage__battery-strip" role="img" aria-label={ready ? data.percentText : t('waiting')}>
              {Array.from({ length: 10 }, (_, i) => <i key={i} className={ready && fraction > i / 10 ? 'is-lit' : ''} />)}
            </div>
            <div className="BatteryStorage__screen-rule" />
            {!ready && <div role="status" className="BatteryStorage__waiting">{t('waiting')}</div>}
            <dl className="BatteryStorage__readings">
              <div><dt>{t('energy')}</dt><dd>{ready ? data.energyText : '—'}</dd></div>
              <div><dt>{ready ? data.etaLabel : t('discharge')}</dt><dd>{ready ? data.etaText : '—'}</dd></div>
              <div><dt>{t('charge')}</dt><dd>{ready ? data.chargeText : '—'}</dd></div>
              <div><dt>{t('discharge')}</dt><dd>{ready ? data.dischargeText : '—'}</dd></div>
            </dl>
          </div>
          <div className="BatteryStorage__bezel-label" aria-hidden="true"><span>MONITOR / BATT.</span><span>DC</span></div>
        </section>
        <section className="BatteryStorage__controls" aria-label={t('switches')}>
          <div className="BatteryStorage__bank-head">{t('switches')}</div>
          {switchRow('input')}{switchRow('output')}
          <Regulator label={t('charge-limit')} text={data.chargeLimitText} value={data.chargeLimit}
            min={data.chargeMin} max={data.chargeMax} disabled={!ready} change={value => act('charge-rate', { value })} />
          <Regulator label={t('discharge-limit')} text={data.dischargeLimitText} value={data.dischargeLimit}
            min={data.dischargeMin} max={data.dischargeMax} disabled={!ready} change={value => act('discharge-rate', { value })} />
        </section>
      </div>
      <section className="BatteryStorage__meter" aria-label={t('load')}>
        <div className="BatteryStorage__meter-title">{t('load')}</div>
        {ready ? <PowerCounter watts={data.passthrough} label={data.passthroughText} labels={data.labels || {}} />
          : <div className="BatteryStorage__segments">—</div>}
        <div className="BatteryStorage__meter-foot"><span>{t('input')} → {t('output')}</span><span>{t('through')}</span></div>
      </section>
      <div className="BatteryStorage__service">
        <div className="BatteryStorage__vent" aria-hidden="true" />
        <div className="BatteryStorage__cable-bay" aria-hidden="true">
          <i /><i /><i /><b />
        </div>
        <span className="BatteryStorage__hazard">⚡ {t('high-voltage')}<br />{t('maintenance')}</span>
      </div>
      <footer className="BatteryStorage__footer"><span>{t('section')}</span><span>7.2 REV 6</span></footer>
    </div>
    <div className="BatteryStorage__hazard-edge" aria-hidden="true" />
  </main>;
};
