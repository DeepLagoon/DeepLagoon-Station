import { useBackend } from '../backend';
import { ShipConsoleFrame, ConsoleKey as Key, ConsolePlate as Plate, RadarScope } from '../components/ShipConsoleFrame';
import './ShuttleConsole.scss';

export const ShipRadarConsole = () => {
  const { data, act } = useBackend();
  const gunner = data.kind === 'gunnery';
  const nav = data.nav || {}, weapons = data.weapons || {}, shield = data.shield || {};
  const send = (channel, id) => act(channel, { id });
  if (data.kind === 'handheld') return <div className="ShuttleConsole HandheldMassScanner" data-kind="handheld">
    <RadarScope compact />
  </div>;
  return <ShipConsoleFrame className="ShuttleConsole ShipRadarConsole" data-kind={data.kind}>
    <header><span className="ShuttleConsole__sticker">{gunner ? 'Контроль орудий' : 'Сканер масс'}</span>
      <nav><Key selected={nav.iff} onClick={() => send('radar', 'iff')}>IFF</Key>
        <Key selected={nav.details} onClick={() => send('radar', 'details')}>Подробно</Key>
        {gunner ? <Key selected={nav.docks} onClick={() => send('radar', 'docks')}>Доки</Key> :
          <span className="ShuttleConsole__status">{Math.round(nav.range || 0)} м</span>}</nav>
      <Key aria-label="Закрыть" onClick={() => act('close')}>×</Key></header>
    <main><RadarScope title={gunner ? 'Тактический радар' : 'Сканер масс'}
      indicator={gunner ? 'FIRE CONTROL' : 'SCANNER'} active={gunner ? weapons.hasWeapons : data.online}
      statusLabel={gunner ? weapons.status : data.online ? 'Сканер подключён' : 'Ожидание данных сканера'}
      footer={gunner ? 'ЛКМ: огонь · Колесо: масштаб' : 'Колесо: масштаб'} />
      <aside>
        {gunner && <section><Plate>Сервер орудий</Plate>
          <div className="ShuttleConsole__status">{weapons.status || 'Нет связи'}</div>
          <div className="ShuttleConsole__keys">{[['refresh', 'Связь'], ['all', 'Все'], ['none', 'Снять']].map(([id, name]) =>
            <Key key={id} onClick={() => send('weapon', id)}>{name}</Key>)}</div>
        </section>}
        <section className="ShuttleConsole__terminal"><div className="ShuttleConsole__terminal-title">Параметры радара</div>
          <dl><div><dt>Масштаб</dt><dd>{Math.round(nav.range || 0)} м</dd></div></dl>
        </section>
        {gunner ? <section><Plate>Управление орудиями</Plate>
          <div className="ShuttleConsole__keys">{[['ballistic', 'Баллистика'], ['energy', 'Энергия'], ['missile', 'Ракеты']].map(([id, name]) =>
            <Key key={id} onClick={() => send('weapon', id)}>{name}</Key>)}</div>
          <div className="ShuttleConsole__weapons">{(weapons.entries || []).map(weapon =>
            <Key key={weapon.id} disabled={weapon.disabled} selected={weapon.selected} onClick={() => send('weapon', weapon.id)}>
              {weapon.name}{weapon.selected && <progress className="ShuttleConsole__reload" max="1" value={weapon.reload ?? 1}
                aria-label={`Перезарядка: ${weapon.name}`} />}
            </Key>)}{!weapons.entries?.length && <p>Подключите сервер орудий.</p>}</div>
        </section> : <section><Plate>Обозначения</Plate><div className="ShipRadarConsole__legend">
          <span>Контуры — объекты сектора</span><span>IFF — подписи кораблей</span><span>Подробно — сведения IFF</span>
        </div></section>}
        {shield.present && <section><Plate>Состояние щита</Plate><div className="ShuttleConsole__status">{shield.status}</div>
          <progress className="ShuttleConsole__progress" max="1" value={shield.health || 0} aria-label="Прочность щита" />
          <div>{Math.round((shield.health || 0) * 100)}% · {shield.load}</div>
        </section>}
      </aside>
    </main>
  </ShipConsoleFrame>;
};
