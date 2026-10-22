import { useState } from 'react';
import { useBackend } from '../backend';
import './ApartmentElevator.scss';

export const ApartmentElevator = () => {
  const { data, act } = useBackend();
  const [number, setNumber] = useState('');
  return <div className="ApartmentElevator">
    <header><strong>ЖИЛОЙ КОМПЛЕКС · ЛИФТ</strong><button onClick={() => act('close')} aria-label="Закрыть">×</button></header>
    <main><h2>{data.atHome ? 'На остановку' : 'Куда едем?'}</h2>
      <p>Все находящиеся в кабине поедут вместе. Дождитесь открытия дверей.</p>
      <aside className="ApartmentElevator__note">Ваш номер<br />квартиры <b>{data.number}</b><small>Действует до конца раунда</small>
        {!data.atHome && <button type="button" disabled={data.busy} onClick={() => setNumber(String(data.number))}>Ввести свой номер</button>}
      </aside>
      {data.atHome ? <button disabled={data.busy} onClick={() => act('return')}>Вернуться на жилую остановку</button>
        : <section className="ApartmentElevator__keypad" aria-label="Пульт лифта">
          <label id="apartment-number-label">Номер квартиры</label>
          <output aria-labelledby="apartment-number-label" aria-live="polite">{number || '— — — — — — — —'}</output>
          <div className="ApartmentElevator__keys">
            {['7', '8', '9', '4', '5', '6', '1', '2', '3', 'clear', '0', 'backspace'].map(key =>
              <button type="button" key={key} disabled={data.busy || (/^[0-9]$/.test(key) && number.length >= 8)}
                aria-label={key === 'clear' ? 'Сбросить номер' : key === 'backspace' ? 'Удалить цифру' : key}
                onClick={() => setNumber(value => key === 'clear' ? '' : key === 'backspace' ? value.slice(0, -1) : (value + key).slice(0, 8))}>
                {key === 'clear' ? 'C' : key === 'backspace' ? '⌫' : key}
              </button>)}
          </div>
          <button className="ApartmentElevator__depart" disabled={data.busy || number.length !== 8} onClick={() => act('depart', { number })}>Отправка</button>
        </section>}
      <p className="ApartmentElevator__status" role="status">{data.message || 'Для поездки в гости достаточно знать номер квартиры.'}</p>
      <small>Перед прибытием рабочая одежда и вещи сдаются на хранение. На обратном пути они возвращаются.</small>
    </main>
  </div>;
};
