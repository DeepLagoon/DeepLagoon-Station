import { useEffect, useRef, useState } from 'react';
import { useBackend } from '../backend';
import { StoreHeader, StoreTabs, StoreDialog, formatCoins, storeOrderId } from './LagoonStore';

const palette = ['#7ABFA5', '#83C7ED', '#A9A0EF', '#E6A0C4', '#EEAD82', '#E6CE83', '#CFDCE3', '#83D9CB'];
const validColor = color => /^#[0-9a-f]{6}$/i.test(color);
const rgbOf = color => [1, 3, 5].map(offset => parseInt(color.slice(offset, offset + 2), 16));
const permittedColor = color => {
  if (!validColor(color)) return false;
  const rgb = rgbOf(color);
  return !(Math.max(...rgb) - Math.min(...rgb) < 16 && (rgb[0] < 55 || rgb[0] > 225));
};
export const LagoonEconomy = () => {
  const { data, act } = useBackend();
  const main = useRef(null);
  const [tab, setTab] = useState('store');
  useEffect(() => { if (main.current) main.current.scrollTop = 0; }, [tab, data]);
  const [pending, setPending] = useState(false), [confirm, setConfirm] = useState(null);
  const [color, setColor] = useState(data.color || '#7ABFA5'), [hex, setHex] = useState(data.color || '#7ABFA5');
  const [referral, setReferral] = useState('');
  useEffect(() => { setPending(false); setConfirm(null); }, [data]);
  useEffect(() => { setColor(data.color || '#7ABFA5'); setHex(data.color || '#7ABFA5'); }, [data.color]);
  const chooseColor = value => { setColor(value); setHex(value); };
  const submit = (action, product) => { setPending(true); act(action, { product: String(product), order: storeOrderId() }); };
  const price = tier => data.passTier > 0 && data.passTier < tier.id
    ? Math.ceil(((tier.price - (data.subscriptions || []).find(t => t.id === data.passTier).price) * data.passDays) / 30) : tier.price;
  const rgb = rgbOf(color);
  const dirty = color.toUpperCase() !== (data.color || '#7ABFA5').toUpperCase();
  const colorAllowed = permittedColor(hex);
  const referrals = data.referrals || [];
  return <div className="LagoonStore LagoonEconomy">
    <StoreHeader title="Lagoon Coin и подписки" balance={data.balance ?? -1} />
    {data.loading || data.error ? <main aria-busy={!!data.loading}><p className="LagoonStore__notice" role={data.error ? 'alert' : 'status'}>{data.loading ? 'Загрузка Lagoon Coin…' : data.error}</p>{data.error && <button onClick={() => act('retry')}>Повторить загрузку</button>}</main> : <>
      <StoreTabs tabs={[["store", 'Подписки'], ["settings", 'Настройки пользователя'], ["referrals", 'Приглашение друзей']]} selected={tab} onChange={setTab} />
      <main ref={main} inert={confirm ? true : undefined}>
        {data.message && <p className="LagoonStore__notice" role="status">{data.message}</p>}
        {tab === 'store' && <>
          <div className="LagoonStore__intro"><h2>Больше возможностей с Lagoon</h2><p className="muted">Доступ на 30 дней за LC. Выберите уровень и посмотрите стоимость перед покупкой.</p>
            {data.passTier > 0 && <p className="LagoonStore__notice">Ваш LC-доступ: <strong>{data.subscriptions?.find(t => t.id === data.passTier)?.name}</strong> · осталось {Number(data.passDays).toFixed(1)} дн.{data.paused ? ' · время приостановлено на период равной или старшей платной подписки' : ''}</p>}
          </div>
          <div className="LagoonStore__grid">{(data.subscriptions || []).map(t => <article className={`LagoonStore__card ${data.passTier === t.id ? 'selected' : ''}`} key={t.id}>
            <div className="LagoonStore__cardTop"><span className="badge">{data.passTier > 0 && data.passTier < t.id ? 'До конца LC-периода' : '30 дней доступа'}</span>{data.passTier === t.id && <span className="badge active">Ваш уровень</span>}</div>
            <h3>{t.name}</h3><div className="price">{formatCoins(price(t))} <span>LC{data.passTier > 0 && data.passTier < t.id ? ' · доплата' : ''}</span></div>
            <p><strong>{t.rate} LC</strong> <span className="muted">за час активной игры</span></p><p className="muted">Свой цвет никнейма OOC</p>
            <div className="LagoonStore__cardActions"><button className="primary" disabled={pending || data.passTier > t.id || data.balance < price(t)} onClick={() => setConfirm(t)}>{data.passTier > t.id ? 'Доступен старший уровень' : data.passTier === t.id ? 'Продлить на 30 дней' : data.passTier > 0 && data.passTier < t.id ? 'Повысить уровень' : 'Купить доступ'}</button></div>
            {data.passTier <= t.id && data.balance < price(t) && <small>Не хватает {formatCoins(price(t) - data.balance)} LC</small>}
          </article>)}</div>
          <div className="LagoonStore__intro" style={{ marginTop: 20 }}><small>Покупка открывает игровые бонусы. Подарки LC за рублёвую оплату и закрытые публикации Boosty в неё не входят.</small><details><summary>Как получать Lagoon Coin</summary><p>2 LC за час активной игры без подписки. 5 LC за старт по Ready после 20 минут участия. Ещё 3 LC за 30 минут активности в день.</p></details></div>
        </>}
        {tab === 'settings' && <div className="LagoonStore__settings">
          <div className="LagoonStore__intro"><h2>Цвет никнейма OOC</h2><p className="muted">Выберите оттенок из палитры или настройте его через RGB и HEX.</p></div>
          {!data.tier && <p className="LagoonStore__notice">Изменение цвета доступно с подпиской «Искра».</p>}
          <fieldset disabled={pending || !data.tier} style={{ border: 0, padding: 0, margin: 0, minWidth: 0, display: 'grid', gap: 18 }}>
            <div className="LagoonStore__swatches" aria-label="Палитра цветов">{palette.map(value => <button key={value} aria-label={`Цвет ${value}`} aria-pressed={color.toUpperCase() === value.toUpperCase()} onClick={() => chooseColor(value)}><span style={{ background: value }} /></button>)}</div>
            <div className="LagoonStore__rgb">{['R', 'G', 'B'].map((channel, index) => <label key={channel}>{channel} · {rgb[index]}<input type="range" min="0" max="255" aria-label={`Канал ${channel}`} value={rgb[index]} onChange={event => { const next = [...rgb]; next[index] = Number(event.target.value); chooseColor('#' + next.map(v => v.toString(16).padStart(2, '0')).join('').toUpperCase()); }} /></label>)}</div>
            <label>HEX · #RRGGBB<input aria-label="Цвет OOC HEX" value={hex} maxLength={7} spellCheck={false} onFocus={() => act('focus')} onChange={event => { const value = event.target.value.toUpperCase(); setHex(value); if (validColor(value)) setColor(value); }} aria-invalid={!colorAllowed} /></label>
          </fieldset>
          <div className="LagoonStore__sample" style={{ background: '#15212b' }}><small>Предпросмотр на тёмном фоне</small><p><strong style={{ color }}>OOC · {data.playerName || 'Ваш никнейм'}</strong>: Всем привет!</p></div>
          <div className="LagoonStore__sample" style={{ background: '#f0f3f5', color: '#26333b' }}><small style={{ color: '#51616a' }}>Предпросмотр на светлом фоне</small><p><strong style={{ color }}>{data.playerName || 'Ваш никнейм'}</strong>: Всем привет!</p></div>
          {!colorAllowed && <p role="alert">{validColor(hex) ? 'Выберите более различимый цвет: почти чёрные и почти белые нейтральные оттенки недоступны.' : 'Введите цвет в формате #RRGGBB.'}</p>}
          <div className="LagoonStore__dialogActions"><button className="primary" disabled={pending || !data.tier || !colorAllowed || !dirty} onClick={() => submit('color', color)}>{pending ? 'Сохранение…' : 'Сохранить цвет'}</button><button disabled={pending || !dirty} onClick={() => chooseColor(data.color || '#7ABFA5')}>Сбросить изменения</button></div>
        </div>}
        {tab === 'referrals' && <div className="LagoonStore__settings">
          <div><h2>Приглашайте друзей</h2><p className="muted">До 100 LC вам и 50 LC другу за активную игру и возвращение в разные дни.</p></div>
          <label>Ваш код приглашения<input className="LagoonStore__refCode" aria-label="Ваш код приглашения" readOnly value={data.referralCode || ''} onFocus={event => { act('focus'); event.target.select(); }} /></label><small>Выделите и скопируйте код, чтобы отправить другу.</small>
          <article className="LagoonStore__card"><h3>Вас пригласил друг?</h3>{data.referrerBound ? <p className="badge active">Код пригласившего уже закреплён за аккаунтом</p> : <><p className="muted">Введите код до 2 часов игры и в первые 7 дней. Пригласившего можно закрепить только один раз.</p><label>Код пригласившего<input aria-label="Код пригласившего" value={referral} maxLength={20} spellCheck={false} onFocus={() => act('focus')} onChange={e => setReferral(e.target.value.toUpperCase())} /></label><button disabled={pending || !/^[A-F0-9]{20}$/.test(referral.trim())} onClick={() => submit('referral', referral.trim())}>Закрепить код</button></>}</article>
          <h3>Приглашённые игроки · {referrals.length}</h3>
          {referrals.length ? <ul className="LagoonStore__refList">{referrals.map((friend, index) => <li key={index}><div><strong>{friend.name}</strong><small>{friend.stage >= 2 ? 'Все этапы пройдены' : friend.stage === 1 ? 'Освоился · ожидаем возвращения' : 'Начинает знакомство со станцией'}</small></div><span className="badge">{friend.reward} LC</span></li>)}</ul> : <p className="empty">Пока никто не закрепил ваш код.</p>}
          <details><summary>Этапы и награды</summary><p>3 часа игры и 2 активных дня: 25 LC вам, 15 LC другу.
12 часов, 5 активных дней и неделя с первого входа: ещё 75 LC вам, 35 LC другу.
Награды пригласившему доступны за первых 5 освоившихся новичков в месяц. В списке показана сумма начисленных вам наград.</p></details>
        </div>}
      </main>
      {confirm && <StoreDialog title="Подтверждение покупки" busy={pending} onCancel={() => setConfirm(null)}>
        <p>{data.passTier > 0 && data.passTier < confirm.id ? `Повысить уровень до «${confirm.name}» до конца текущего LC-периода?` : `Купить 30 дней «${confirm.name}»?`}</p>
        <div className="price">{formatCoins(price(confirm))} <span>LC</span></div><p className="muted">После покупки останется {formatCoins(data.balance - price(confirm))} LC.</p>
        <div className="LagoonStore__dialogActions"><button className="primary" disabled={pending} onClick={() => submit('pass', confirm.id)}>{pending ? 'Обработка…' : 'Подтвердить покупку'}</button><button disabled={pending} onClick={() => setConfirm(null)}>Отмена</button></div>
      </StoreDialog>}
    </>}
  </div>;
};
