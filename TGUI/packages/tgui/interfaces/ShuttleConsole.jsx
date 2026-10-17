import { useEffect, useRef, useState } from 'react';
import { useBackend } from '../backend';
import './ShuttleConsole.scss';

const Key = ({ children, selected, ...props }) => <button className={selected ? 'is-selected' : ''} aria-pressed={selected} {...props}>{children}</button>;
const Sticker = ({ children }) => <h3 className="ShuttleConsole__sticker">{children}</h3>;

// Text inputs own their draft until submission; the 5 Hz telemetry stream must
// never overwrite a search or speed value while the pilot is typing.
const Field = ({ label, value, onCommit, type = 'text' }) => {
  const [draft, setDraft] = useState(String(value ?? ''));
  const focused = useRef(false);
  useEffect(() => { if (!focused.current) setDraft(String(value ?? '')); }, [value]);
  return <label className="ShuttleConsole__field"><span>{label}</span><input type={type} value={draft}
    onFocus={() => { focused.current = true; }}
    onChange={event => setDraft(event.target.value)}
    onBlur={() => { focused.current = false; onCommit(draft); }}
    onKeyDown={event => { if (event.key === 'Enter') event.currentTarget.blur(); }} /></label>;
};

export const ShuttleConsole = () => {
  const { data, act } = useBackend();
  const viewport = useRef(null);
  const viewportArea = useRef(null);
  const nav = data.nav || {}, weapons = data.weapons || {}, map = data.map || {}, dock = data.dock || {};
  const shield = data.shield || {};
  const mode = data.mode || 0;
  const send = (channel, id, value) => act(channel, { id, ...(value === undefined ? {} : { value: String(value) }) });
  useEffect(() => {
    const element = viewport.current;
    const area = viewportArea.current;
    let frame = 0, last = '';
    const report = () => {
      cancelAnimationFrame(frame);
      frame = requestAnimationFrame(() => {
        const available = area.getBoundingClientRect();
        const diameter = Math.max(0, Math.floor(Math.min(available.width, available.height) - 16));
        element.style.width = `${diameter}px`;
        element.style.height = `${diameter}px`;
        const rect = element.getBoundingClientRect();
        const bounds = { x: rect.left / innerWidth, y: rect.top / innerHeight, w: rect.width / innerWidth, h: rect.height / innerHeight };
        const key = JSON.stringify(bounds);
        if (key !== last && rect.width > 0 && rect.height > 0) { last = key; act('viewport', bounds); }
      });
    };
    const observer = new ResizeObserver(report);
    observer.observe(area); window.addEventListener('resize', report); report();
    return () => { observer.disconnect(); window.removeEventListener('resize', report); cancelAnimationFrame(frame); };
  }, []);
  return <div className="ShuttleConsole" data-mode={mode} data-skin="quasimorph">
    <header><span className="ShuttleConsole__sticker">{nav.name || 'Консоль шаттла'}</span>
      <nav>{['Пилот / орудия', 'Карта / FTL', 'Стыковка'].map((name, i) =>
        <Key key={name} selected={mode === i} onClick={() => act('mode', { value: i })}>{name}</Key>)}</nav>
      <Key aria-label="Закрыть" onClick={() => act('close')}>×</Key></header>
    <main><section className="ShuttleConsole__scope">
      <div className="ShuttleConsole__scope-title"><Sticker>{['Навигационный радар', 'Карта сектора', 'Стыковочный радар'][mode]}</Sticker>
        <span className="ShuttleConsole__indicator" role="status"
          aria-label={weapons.hasWeapons ? 'Корабельные орудия обнаружены' : 'Корабельные орудия не обнаружены'}>
          <span className={`ShuttleConsole__lamp${weapons.hasWeapons ? ' is-on' : ''}`} aria-hidden="true" />
          FIRE CONTROL</span></div>
      <div className="ShuttleConsole__viewport-area" ref={viewportArea}>
        <div className="ShuttleConsole__viewport" ref={viewport} aria-label="Нативный радар" />
      </div>
      <footer>{mode === 0 ? 'Клавиши пилотирования · ЛКМ: огонь · Колесо: масштаб' : 'Выберите цель на радаре или в списке'}</footer>
    </section>
    <aside>
      <section className="ShuttleConsole__terminal"><div className="ShuttleConsole__terminal-title">БОРТОВОЙ ТЕРМИНАЛ / TELEMETRY</div>
        <dl>{[['Кодовый номер', nav.designation], ['Координаты', nav.position], ['Азимут', nav.azimuth], ['Линейная скорость', nav.velocity], ['Угловая скорость', nav.angular],
          ...(nav.zLevel ? [['Высота', nav.altitude], ['Вертикальная скорость', nav.vertical], ['Режим полёта', nav.travel]] : [])].map(([label, value]) =>
          <div key={label}><dt>{label}</dt><dd>{value || '—'}</dd></div>)}</dl></section>
      {mode === 0 && <>
        <details className="ShuttleConsole__settings"><summary><Sticker>Настройки пилотирования</Sticker></summary><section><div className="ShuttleConsole__keys">
          <Key selected={nav.iff} onClick={() => send('nav', 'iff')}>IFF</Key>
          <Key selected={nav.details} onClick={() => send('nav', 'details')}>Подробно</Key>
          <Key selected={nav.docks} onClick={() => send('nav', 'docks')}>Доки</Key>
        </div>{!nav.station && <div className="ShuttleConsole__keys">{['Свободный ход', 'Торможение', 'Парковка'].map((label, i) =>
          <Key key={label} disabled={i === 2 && nav.ftl} selected={nav.dampener === i} onClick={() => send('nav', 'dampener', i)}>{label}</Key>)}</div>}
        <Sticker>Поиск по системе опознавания</Sticker>
        <Field label="Название / компания" value={nav.search} onCommit={value => send('nav', 'search', value)} />
        <div className="ShuttleConsole__fields"><Field label="Дальность IFF" type="number" value={nav.range} onCommit={value => send('nav', 'range', value)} />
          <Field label="Предел скорости" type="number" value={nav.speed} onCommit={value => send('nav', 'speed', value)} /></div>
        </section></details>
        <section><Sticker>Управление орудиями</Sticker><div className="ShuttleConsole__status">{weapons.status || 'Нет связи'}</div>
          <div className="ShuttleConsole__keys">{[['refresh', 'Связь'], ['all', 'Все'], ['none', 'Снять']].map(([id, label]) =>
            <Key key={id} onClick={() => send('weapon', id)}>{label}</Key>)}</div>
          <div className="ShuttleConsole__keys">{[['ballistic', 'Баллистика'], ['energy', 'Энергия'], ['missile', 'Ракеты']].map(([id, label]) =>
            <Key key={id} onClick={() => send('weapon', id)}>{label}</Key>)}</div>
          <div className="ShuttleConsole__weapons">{(weapons.entries || []).map(weapon => <Key key={weapon.id} disabled={weapon.disabled}
            selected={weapon.selected} onClick={() => send('weapon', weapon.id)}>
            <span>{weapon.name}</span>
            {weapon.selected && <progress className="ShuttleConsole__reload" max="1" value={weapon.reload ?? 1}
              aria-label={`Перезарядка: ${weapon.name}`}
              title={weapon.reloadSeconds > 0 ? `Готовность через ${weapon.reloadSeconds.toFixed(1)} с` : 'Готово к стрельбе'} />}
          </Key>)}
            {!(weapons.entries || []).length && <p>Подключите сервер орудий.</p>}</div>
        </section>
        <section className="ShuttleConsole__ports"><div className="ShuttleConsole__terminal-title">СЕТЕВЫЕ ПОРТЫ / I/O</div>
          <div className="ShuttleConsole__port-list">{(nav.ports || []).map(port => <Key key={port.id} disabled={port.disabled}
            onClick={() => send('nav', 'port', port.id)}>{port.name}</Key>)}</div></section>
      </>}
      {mode === 1 && <><section><Sticker>Настройки карты</Sticker><div className="ShuttleConsole__status">{map.status}</div>
        <progress className="ShuttleConsole__progress" aria-label="Прогресс FTL" max="1" value={map.progress || 0} />
        <div className="ShuttleConsole__keys"><Key selected={map.beacons} onClick={() => send('map', 'beacons')}>Маяки</Key>
          <Key selected={map.ftl} disabled={map.disabled} onClick={() => send('map', 'ftl')}>FTL</Key>
          <Key selected={map.autopilot} disabled={map.autopilotDisabled} onClick={() => send('map', 'autopilot')}>Автопилот</Key>
          <Key disabled={map.refreshDisabled} onClick={() => send('map', 'refresh')}>Обновить</Key></div></section>
        <section><Sticker>Объекты сектора</Sticker><div className="ShuttleConsole__weapons">{(map.destinations || []).map(target =>
          <Key key={target.id} disabled={target.disabled} onClick={() => send('map', target.id)}>{target.name}</Key>)}</div></section></>}
      {mode === 2 && <><section><Sticker>Настройки стыковки</Sticker><div className="ShuttleConsole__keys">
        <Key selected={dock.locked} onClick={() => send('dock', 'lock')}>Блок FTL</Key>
        <Key selected={!dock.locked} onClick={() => send('dock', 'unlock')}>Разблокировать</Key>
        <Key disabled={dock.undockDisabled} onClick={() => send('dock', 'undockAll')}>Отстыковать все</Key></div></section>
        <section><Sticker>Стыковочные порты</Sticker><div className="ShuttleConsole__weapons">{(dock.ports || []).map(port =>
          <Key key={port.id} selected={port.selected} onClick={() => send('dock', port.id)}>{port.name} {port.connected ? '●' : '○'}</Key>)}</div></section></>}
      {shield.present && <section className="ShuttleConsole__shields"><Sticker>Состояние щита</Sticker>
        <div className="ShuttleConsole__status">{!shield.powered ? 'Нет питания'
          : shield.overload > 0 ? `Перегрузка: ${Math.ceil(shield.overload)} с`
            : shield.recharging && shield.health < 1 ? 'Восстановление'
              : shield.active ? 'Щит активен' : 'Щит отключён'}</div>
        <progress className="ShuttleConsole__progress" max="1" value={shield.health || 0}
          aria-label="Прочность щита" />
        <div>{Math.round((shield.health || 0) * 100)}% · {Math.round((shield.draw || 0) / 1000)} / {Math.round((shield.maxDraw || 0) / 1000)} кВт</div>
      </section>}
    </aside></main>
  </div>;
};
