import { useBackend } from '../backend';
import './ApartmentTravel.scss';
export const ApartmentTravelPanel = () => {
  const { data, act } = useBackend();
  return <div className="ApartmentTravel"><header><strong>Терминал квартиры</strong><button onClick={() => act('close')}>×</button></header>
    <main><p>Для поездки в квартиру или на остановку воспользуйтесь пультом лифта. Все пассажиры едут вместе; приглашения не требуются.</p>
      {data.canManage && data.atHome && <><button onClick={() => act('edit')}>Обустройство квартиры</button>
      <button onClick={() => act('shop')}>Шаблоны квартир</button></>}
      {data.message && <p role="status">{data.message}</p>}
    </main></div>;
};
export const ApartmentTravel = ApartmentTravelPanel;
