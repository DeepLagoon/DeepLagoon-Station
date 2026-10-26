import { useEffect, useState } from 'react';
import { useBackend } from '../backend';
import './ApartmentShop.scss';

export const LagoonEconomy = () => {
  const { data, act } = useBackend();
  const [pending, setPending] = useState(false), [confirm, setConfirm] = useState(null);
  const [color, setColor] = useState(data.color || '#7ABFA5');
  const [referral, setReferral] = useState('');
  useEffect(() => { setPending(false); setConfirm(null); setColor(data.color || '#7ABFA5'); }, [data]);
  const submit = (action, product) => { setPending(true); act(action, { product: String(product), order: crypto.randomUUID() }); };
  if (data.loading || data.error) return <div className="ApartmentShop"><header><strong>Lagoon Coin и подписки</strong><button aria-label="Закрыть" onClick={() => act('close')}>×</button></header>
    <main aria-busy={!!data.loading}><p role={data.error ? 'alert' : 'status'}>{data.loading ? 'Загрузка Lagoon Coin…' : data.error}</p>
      {data.error && <button onClick={() => act('retry')}>Повторить загрузку</button>}
    </main></div>;
  return <div className="ApartmentShop"><header><strong>Lagoon Coin и подписки</strong><button onClick={() => act('close')}>×</button></header>
    <main><h2>{data.balance} LC</h2><p>2 LC за час активной игры без подписки, 5 LC за старт по Ready после 20 минут участия и 3 LC за 30 минут активности в день.</p>
      {data.message && <p role="status">{data.message}</p>}
      {data.passTier > 0 && <p>Доступ за LC: {(data.subscriptions || []).find(t => t.id === data.passTier)?.name}, осталось {Number(data.passDays).toFixed(1)} дн.{data.paused ? ' · время приостановлено на период равной или старшей платной подписки' : ''}</p>}
      <h3>Доступ на 30 дней за LC</h3><p>Покупка открывает игровые бонусы. Подарки LC за рублёвую оплату и закрытые публикации Boosty в неё не входят.</p>
      {(data.subscriptions || []).map(t => <article key={t.id}><div><strong>{t.name}</strong><small>{t.price} LC · {t.rate} LC/час</small></div>
        <button disabled={pending || data.passTier > t.id} onClick={() => setConfirm(t)}>{data.passTier === t.id ? 'Продлить' : data.passTier > 0 && data.passTier < t.id ? 'Повысить уровень' : 'Купить доступ'}</button></article>)}
      {confirm && <section role="dialog" aria-label="Подтверждение покупки"><p>{data.passTier > 0 && data.passTier < confirm.id
        ? `Повысить уровень до «${confirm.name}» до конца текущего LC-периода? Доплата: ${Math.ceil(((confirm.price - data.subscriptions.find(t => t.id === data.passTier).price) * data.passDays) / 30)} LC.`
        : `Купить 30 дней «${confirm.name}» за ${confirm.price} LC?`}</p>
        <button disabled={pending} onClick={() => submit('pass', confirm.id)}>Подтвердить</button><button disabled={pending} onClick={() => setConfirm(null)}>Отмена</button></section>}
      <h3>Цвет никнейма OOC</h3><label>Цвет <input aria-label="Цвет OOC" type="color" value={color} onChange={e => setColor(e.target.value)} /></label>
      <p><span style={{ background: '#15212b', color, padding: '4px 8px' }}>Никнейм на тёмном фоне</span> <span style={{ background: '#f0f3f5', color, padding: '4px 8px' }}>На светлом фоне</span></p>
      <button disabled={pending || !data.tier} onClick={() => submit('color', color)}>Сохранить цвет</button>
      {!data.tier && <p>Доступно с «Искры».</p>}
      <h3>Приглашение друга</h3><label>Код пригласившего <input value={referral} maxLength={20} onFocus={() => act('focus')} onChange={e => setReferral(e.target.value)} /></label><button disabled={pending || !referral} onClick={() => submit('referral', referral)}>Закрепить код</button><p>Ваш код: <strong>{data.referralCode}</strong></p><p>За освоившегося новичка: до 100 LC вам и 50 LC ему. Награды зависят от активной игры и возвращения в разные дни.</p>
    </main></div>;
};
