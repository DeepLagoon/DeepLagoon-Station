import { useEffect, useState } from 'react';
import { useBackend } from '../backend';
import { EmbeddedSelect } from '../components/EmbeddedSelect';
import './ShuttleParking.scss';

const Confirm = ({ label, question, disabled, onConfirm }) => {
  const [open, setOpen] = useState(false);
  const [sent, setSent] = useState(false);
  return <div className="ShuttleParking__confirm">
    <button disabled={disabled} onClick={() => { setSent(false); setOpen(true); }}>{label}</button>
    {open && <section><p>{question}</p><div className="ShuttleParking__actions">
      <button disabled={disabled || sent} onClick={() => { setSent(true); onConfirm(); setOpen(false); }}>Подтвердить</button>
      <button onClick={() => setOpen(false)}>Отмена</button>
    </div></section>}
  </div>;
};

export const ShuttleParking = () => {
  const { data, act } = useBackend();
  const [tab, setTab] = useState('parking');
  const [dock, setDock] = useState('');
  const [checked, setChecked] = useState(false);
  const docks = data.docks || [];
  const places = data.places || [];
  const usable = data.id && !data.busy;
  const arrival = docks.some(d => d.id === dock) ? dock : (docks[0]?.id || '');
  useEffect(() => setChecked(false), [data.reviewToken]);
  const dockControl = <label className="ShuttleParking__field">Шлюз для возврата
    <EmbeddedSelect value={arrival} disabled={!usable} onChange={event => setDock(event.target.value)} aria-label="Шлюз для возврата">
      {docks.length === 0 && <option value="">Нет свободного совместимого шлюза</option>}
      {docks.map(d => <option key={d.id} value={d.id}>{d.name}</option>)}
    </EmbeddedSelect></label>;
  return <div className="ShuttleParking">
    <div className="ShuttleParking__identity"><div><small>Пользователь · ID-карта</small><div>{data.user || 'Карта не вставлена'}</div></div>
      <button disabled={data.busy} onClick={() => act('id')}>{data.id ? 'Извлечь ID-карту' : 'Вставить ID-карту'}</button></div>
    <div className="ShuttleParking__balance">Кредиты: <b>{data.credits || 0} кр.</b><span>Lagoon Coin: <b>{data.coins || '…'} LC</b></span></div>
    <nav aria-label="Раздел парковки">{[['parking','Парковка'],['storage','Долгосрочное хранение'],['shop','Купить места']].map(([id,name]) =>
      <button key={id} aria-pressed={tab === id} onClick={() => setTab(id)}>{name}</button>)}</nav>
    {tab === 'parking' && <>
      <section><small>Собственный шаттл</small><h2>{data.shipName || 'Нет шаттла на ID-карте'}</h2>
        <div className="ShuttleParking__details"><div><small>Категория</small><b>{data.hasShip ? data.category : '—'}</b></div>
          <div><small>Владелец</small><span>{data.owner || '—'}</span></div><div><small>Состояние</small><span>{data.hasShip ? (data.parked ? 'На парковке · заморожен' : 'В секторе') : '—'}</span></div>
          <div><small>Стоимость парковки</small><b>Бесплатно</b></div></div>
        <div className="ShuttleParking__actions"><button className="ShuttleParking__primary" disabled={!usable || !data.hasShip || (data.parked && !arrival)}
          onClick={() => act(data.parked ? 'recall' : 'park', { dock: arrival })}>{data.parked ? 'Вернуть бесплатно' : 'Поставить на парковку'}</button>
          <button disabled={!usable || !data.hasShip} onClick={() => setTab('storage')}>Сохранить надолго…</button></div>
      </section>{dockControl}<p>Бесплатная парковка действует до конца смены. Шаттл заморожен, доступ к нему закрыт. Для переноса в новую смену сохраните его в долгосрочное место.</p>
    </>}
    {tab === 'storage' && <><p>{data.description}</p>{dockControl}
      {places.length === 0 && <p>У вас пока нет мест долгосрочного хранения.</p>}
      {places.map((place,i) => <section key={place.id}><div className="ShuttleParking__slot-title"><strong>{place.categoryName} · место {i+1}</strong><span>{place.name ? 'На диске' : 'Свободно'}</span></div>
        {place.name ? <><h3>{place.name}</h3><p>Категория шаттла: {place.shipCategory} · Владелец: {place.owner}</p><p>Выкуп: <b>{place.price} кр.</b></p>
          {data.hasShip && <p className="ShuttleParking__warning">На карте уже есть действующий шаттл. Сначала сохраните его.</p>}
          {place.paid && <p>Оплата уже учтена. Повторного списания не будет.</p>}
          <Confirm label={place.paid ? 'Получить оплаченный шаттл…' : place.recovering ? 'Завершить выкуп…' : 'Выкупить…'} question={place.paid ? 'Завершить оплаченный выкуп?' : `Выкупить за ${place.price} кр.? Управление будет привязано к вставленной ID-карте.`}
            disabled={!usable || data.hasShip || !arrival} onConfirm={() => act('redeem', { slot:place.id, dock:arrival })} />
        </> : <><p>Можно сохранить шаттл своей или меньшей категории.</p><button disabled={!usable || !data.hasShip || data.shipCategory > place.category}
          onClick={() => act('preview', { slot:place.id })}>{data.shipCategory > place.category ? 'Шаттл слишком большой' : 'Сохранить текущий шаттл…'}</button></>}
      </section>)}</>}
    {tab === 'shop' && <><p>Место приобретается навсегда. В каждом месте хранится один шаттл своей или меньшей категории. Дополнительные места той же категории стоят 50% цены с округлением вверх.</p>
      {(data.categories || []).map(category => <section key={category.id} className="ShuttleParking__shop-row"><div><strong>{category.name}</strong><small>Куплено мест: {category.count}</small>
        <div>Первое: {category.first} LC · следующее: {category.extra} LC</div></div>
        <Confirm label={`${category.count ? 'Ещё место' : 'Купить место'} · ${category.count ? category.extra : category.first} LC`}
          question={`Оплатить ${category.count ? category.extra : category.first} LC за место хранения?`} disabled={!usable}
          onConfirm={() => act('buy', { category:category.id, token:data.buyToken })} />
      </section>)}</>}
    {data.reviewToken && <section className="ShuttleParking__review"><h3>Проверка перед сохранением</h3><p>Эти добавленные предметы останутся у консоли:</p>
      <ul>{(data.excluded || []).map((entry,i) => <li key={i}>{entry.name}</li>)}</ul>
      <p>Штатное оборудование сохранится. Шаттл будет убран из мира.</p>
      <label className="ShuttleParking__check"><input type="checkbox" checked={checked} onChange={e => setChecked(e.target.checked)} />Я проверил список</label>
      <div className="ShuttleParking__actions"><button disabled={!usable || !checked} onClick={() => { setChecked(false); act('save', { slot:data.reviewSlot, token:data.reviewToken }); }}>Сохранить шаттл</button><button onClick={() => act('cancel')}>Отмена</button></div>
    </section>}
    <footer><span role="status" aria-live="polite">{data.message}</span><button disabled={data.busy} onClick={() => act('refresh')}>Обновить</button></footer>
  </div>;
};
