import { useEffect, useRef } from 'react';
import { ConsolePlate } from '../components/ShipConsoleFrame';

const categories = [
  ['thrusters', 'Двигатели и гироскопы'],
  ['weapons', 'Орудия'],
  ['structures', 'Стены и конструкции'],
  ['tiles', 'Палуба'],
];

// One small server-aggregated bitmap. No sprites, entity controls, per-tile DOM,
// animation loop or work proportional to the real size of a very large shuttle.
export const ShuttleHullSchematic = ({ hull }) => {
  const canvas = useRef(null);
  const width = Math.min(64, Math.max(1, hull.width || 1));
  const height = Math.min(64, Math.max(1, hull.height || 1));
  useEffect(() => {
    const ctx = canvas.current.getContext('2d');
    ctx.clearRect(0, 0, width, height);
    if (!hull.present || !hull.cells) return;
    let cells;
    try { cells = atob(hull.cells); } catch { return; }
    const image = ctx.createImageData(width, height);
    for (let i = 0; i < Math.min(cells.length, width * height); i++) {
      const flags = cells.charCodeAt(i);
      if (!flags) continue;
      const color = flags & 2 ? [204, 68, 66]
        : flags & 8 ? [189, 185, 144]
          : flags & 4 ? [112, 157, 167]
            : flags & 16 ? [134, 147, 149] : [67, 82, 87];
      // Grid coordinates grow up; image coordinates grow down.
      const offset = ((height - 1 - Math.floor(i / width)) * width + i % width) * 4;
      image.data.set([...color, 255], offset);
    }
    ctx.putImageData(image, 0, 0);
  }, [hull.cells, hull.present, width, height]);
  return <section className="ShuttleHull__schematic">
    <ConsolePlate>Схема шаттла</ConsolePlate>
    <div className="ShuttleHull__display">
      <canvas ref={canvas} width={width} height={height}
        style={{ aspectRatio: `${width} / ${height}` }}
        role="img" aria-label="Схема шаттла: красным отмечены утраченные элементы" />
      {!hull.present && <span className="ShuttleHull__empty">Нет данных о корпусе</span>}
    </div>
    <div className="ShuttleHull__legend">
      <span><i className="is-hull" />Корпус</span><span><i className="is-thruster" />Двигатели</span>
      <span><i className="is-weapon" />Орудия</span><span><i className="is-missing" />Утрачено</span>
    </div>
    <footer>Схематический силуэт. Красным отмечены уничтоженные или демонтированные элементы.</footer>
  </section>;
};

export const ShuttleHullSummary = ({ hull }) => {
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
      <p className="ShuttleHull__note">Учитываются удалённые элементы относительно сохранённой схемы.
        Боезапас и текущая прочность отдельных элементов здесь не отображаются.</p>
    </section>
  </>;
};
