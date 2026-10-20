import { useBackend } from '../backend';
import { ConsolePlate, ConsoleKey, RadarScope } from '../components/ShipConsoleFrame';

const categories = [
  ['thrusters', 'Двигатели и гироскопы'],
  ['weapons', 'Орудия'],
  ['structures', 'Стены и конструкции'],
  ['tiles', 'Палуба'],
];

// Uses the same native NavMapControl as crew monitoring, positioned by RadarScope.
export const ShuttleHullSchematic = ({ hull }) => <RadarScope
  title="Схема шаттла" indicator="КОРПУС" active={hull.present}
  statusLabel="Мониторинг состояния шаттла"
  footer="Красный: утрачено · Голубой: двигатели · Жёлтый: орудия · Колесо: масштаб · ПКМ: перемещение" />;

export const ShuttleHullSummary = ({ hull }) => {
  const { act } = useBackend();
  const missing = categories.filter(([key]) => (hull[key]?.missing || 0) > 0);
  const integrity = Math.max(0, Math.min(1, hull.integrity ?? 1));
  const energy = Math.max(0, hull.storedEnergy || 0);
  const capacity = Math.max(0, hull.energyCapacity || 0);
  const formatEnergy = value => value >= 1e9 ? `${(value / 1e9).toFixed(1)} ГДж`
    : value >= 1e6 ? `${(value / 1e6).toFixed(1)} МДж` : `${(value / 1000).toFixed(1)} кДж`;
  return <>
    <section className="ShuttleHull__summary"><ConsolePlate>Бортовые системы</ConsolePlate>
      {hull.present ? <>
        <dl>
          <div><dt>Целостность шаттла</dt><dd className={integrity < 1 ? 'ShuttleHull__missing' : ''}>{Math.floor(integrity * 100 + 0.00001)}%</dd></div>
          <div><dt>Рабочие двигатели</dt><dd>{hull.workingThrusters > 0 ? `Есть · ${hull.workingThrusters}` : 'Нет'}</dd></div>
          <div><dt>Рабочие гироскопы</dt><dd>{hull.workingGyroscopes > 0 ? `Есть · ${hull.workingGyroscopes}` : 'Нет'}</dd></div>
          <div><dt>Запас электроэнергии</dt><dd>{formatEnergy(energy)} / {formatEnergy(capacity)}</dd></div>
        </dl>
        <p className="ShuttleHull__note">Суммарный заряд СМЭС, подстанций и ЛКП шаттла.</p>
      </> : <p>Ожидание телеметрии шаттла.</p>}
    </section>
    <section className="ShuttleHull__summary"><ConsolePlate>Целостность систем</ConsolePlate>
      {hull.present ? <dl>{categories.map(([key, name]) => {
        const entry = hull[key] || {};
        const lost = entry.missing || 0, total = entry.expected || 0;
        return <div key={key}><dt>{name}</dt><dd className={lost ? 'ShuttleHull__missing' : ''}>
          {Math.max(0, total - lost)} / {total}</dd></div>;
      })}</dl> : <p>Ожидание телеметрии шаттла.</p>}
      <p className="ShuttleHull__note">На месте / учтено. Новые постройки автоматически входят в схему.</p>
    </section>
    <section className="ShuttleHull__summary"><ConsolePlate>Утраченные элементы</ConsolePlate>
      {hull.present && (missing.length ? <ul>{missing.map(([key, name]) =>
        <li key={key}><span>{name}</span><strong className="ShuttleHull__missing">{hull[key].missing}</strong></li>)}
      </ul> : <p>Утрат не обнаружено.</p>)}
      {hull.singleMissingName && <p className="ShuttleHull__missing">Утрачен объект: {hull.singleMissingName}</p>}
      <ConsoleKey disabled={!hull.present} onClick={() => act('resetHullHistory')}>
        Очистить историю повреждений
      </ConsoleKey>
      <p className="ShuttleHull__note">Сброс принимает текущее состояние за исходное для всех консолей шаттла.</p>
      <p className="ShuttleHull__note">Учитываются удалённые элементы относительно сохранённой схемы.
        Боезапас и текущая прочность отдельных элементов здесь не отображаются.</p>
    </section>
  </>;
};
