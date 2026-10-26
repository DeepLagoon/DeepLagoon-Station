import { useEffect, useState } from 'react';
import { useBackend } from '../backend';
import './ApartmentShop.scss';

export const ApartmentShop = () => {
  const { data, act } = useBackend();
  const [pending, setPending] = useState(false), [confirm, setConfirm] = useState(null);
  useEffect(() => { setPending(false); setConfirm(null); }, [data]);
  return <div className="ApartmentShop"><header><strong>Шаблоны квартир</strong>{data.balance >= 0 && <span>{data.balance} LC</span>}<button aria-label="Закрыть" onClick={() => act('close')}>×</button></header>
    <main><p>Ваш номер квартиры: <strong>{data.number}</strong></p>
      {data.message && <p role="status">{data.message}</p>}
      <p>{data.lobby ? 'Выберите квартиру и настройте обстановку до входа в раунд.' : 'После смены шаблона спуститесь на лифте на остановку и введите новый номер квартиры.'}</p>
      {(data.templates || []).map(item => <article key={item.id}><div><strong>{item.name}</strong><small>{item.price ? `Доплата: ${item.price} Lagoon Coin` : item.owned ? 'Доступно' : 'Бесплатно'}{item.owned && item.price ? ' · Куплено' : ''}</small></div>
        <button disabled={pending || item.selected} onClick={() => setConfirm(item)}>{item.selected ? 'Выбрано' : item.owned ? 'Выбрать' : 'Купить и выбрать'}</button></article>)}
      {confirm && <section role="dialog" aria-label="Подтверждение выбора"><p>{confirm.owned ? 'Выбрать' : `Купить за ${confirm.price} LC`} «{confirm.name}»{!confirm.owned ? ' и выбрать' : ''}?</p>
        <button disabled={pending} onClick={() => { setPending(true); act('select', { template: confirm.id }); }}>Подтвердить</button><button disabled={pending} onClick={() => setConfirm(null)}>Отмена</button></section>}
    </main><footer><button disabled={pending} onClick={() => act('edit')}>Обустройство квартиры</button>
      {data.legacy && <button onClick={() => act('legacyExit')}>Покинуть старую квартиру</button>}</footer></div>;
};
