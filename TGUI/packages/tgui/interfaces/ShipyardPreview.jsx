import { useMemo, useRef, useState } from 'react';
import { useBackend } from '../backend';
import { NavMapPlan } from './NavMapPlan.jsx';
import { GridEditorScene } from './GridEditorScene';
import { indexItems } from './gridEditorModel.mjs';
import { useApartmentViewport } from './useApartmentViewport';
import './ShipyardPreview.scss';

const layers = { entity: true, decal: true };
export const ShipyardPreview = () => {
  const { data, act } = useBackend();
  return <div className="ShipyardPreview">
    <header><div><strong>{data.name}</strong><small>{data.description}</small></div><button onClick={() => act('close')}>Закрыть</button></header>
    {data.error ? <div className="ShipyardPreview__error" role="alert">{data.error}</div> : <PreviewPlan data={data} />}
  </div>;
};

const PreviewPlan = ({ data }) => {
  const viewport = useRef(null), [progress, setProgress] = useState(null), [scroll, setScroll] = useState({ left: 0, top: 0 });
  const { view, zoom, cellSize, panning, fit, setScale, events } = useApartmentViewport(viewport, data.width, data.height, data.token);
  const area = useMemo(() => ({ x: data.x, y: data.y, width: data.width, height: data.height }), [data.x, data.y, data.width, data.height]);
  const visuals = useMemo(() => new Map((data.visuals || []).map(item => [`${item.kind}:${item.id}`, item])), [data.visuals]);
  const index = useMemo(() => indexItems(data.items || [], item => visuals.get(`${item.kind}:${item.prototype}`)), [data.items, visuals]);
  const camera = { ...view, left: scroll.left - view.width / 2, top: scroll.top - view.height / 2 };
  return <>
    <div className="ShipyardPreview__toolbar"><button onClick={() => setScale(zoom / 1.25)}>−</button><span>{Math.round(zoom * 100)}%</span>
      <button onClick={() => setScale(zoom * 1.25)}>+</button><button onClick={fit}>Уместить</button>
      <small>Колесо — масштаб · ПКМ / СКМ — перемещение вида</small></div>
    <div ref={viewport} className={`ShipyardPreview__viewport ${panning ? 'panning' : ''}`} {...events}
      onScroll={event => { events.onScroll(); setScroll({ left: event.currentTarget.scrollLeft, top: event.currentTarget.scrollTop }); }}>
      <div className="ShipyardPreview__surface" style={{ width: data.width * cellSize + view.width, height: data.height * cellSize + view.height }}>
        <div className="ShipyardPreview__plan" aria-label="Предпросмотр корабля" style={{ left: view.width / 2, top: view.height / 2, width: data.width * cellSize, height: data.height * cellSize }}>
          <NavMapPlan tiles={data.tiles} structures={data.structures} area={area} />
          <GridEditorScene index={index} area={area} layers={layers} visuals={visuals} view={camera} cellSize={cellSize} onProgress={setProgress} />
        </div>
      </div>
    </div>
    {progress && <div className="ShipyardPreview__progress" role="status">{progress.error || `${progress.label} · ${progress.done} / ${progress.total}`}</div>}
  </>;
};
