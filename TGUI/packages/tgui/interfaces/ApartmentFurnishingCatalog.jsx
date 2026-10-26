import { useEffect, useRef, useState } from 'react';

const variantsFor = (product, catalog) => (product.variants || [{ id: product.id }])
  .map(variant => catalog.find(item => item.id === variant.id)).filter(Boolean);
const stockUsed = (product, draft, catalog) => draft.filter(placement => {
  const item = catalog.find(entry => entry.id === placement.furniture);
  return item && !item.existingOnly && (item.product || item.id) === product.id;
}).length;
const priceText = price => `${price.toLocaleString('ru-RU')} LC`;

export const ApartmentBalance = ({ data }) => {
  let balance = '—';
  try {
    if (data.balanceText || data.balance >= 0) balance = BigInt(data.balanceText || String(data.balance)).toLocaleString('ru-RU');
  } catch { /* Unavailable balances are shown without inventing a zero. */ }
  return <div className="ApartmentEditor__balance" aria-label="Баланс Lagoon Coin"><small>Баланс</small><strong>{balance} <span>LC</span></strong></div>;
};

export const ApartmentFurnishingCatalog = ({ data, room, draft, brush, selected, before, pending, choose, selectExisting, act, Sprite, onPurchaseBusy }) => {
  const [query, setQuery] = useState(''), [category, setCategory] = useState('all'), [filter, setFilter] = useState('all');
  const [active, setActive] = useState(null), [purchase, setPurchase] = useState(null), [quantity, setQuantity] = useState('1');
  const [transaction, setTransaction] = useState(null), [waiting, setWaiting] = useState(false), [error, setError] = useState('');
  const request = useRef(null), sending = useRef(false);
  const catalog = room.catalog;
  const products = data.products || catalog.filter(item => !item.existingOnly).map(item => ({
    id: item.id, name: item.name, category: item.category || 'Мебель', price: item.price || 0,
    packSize: item.maxCount, stock: item.maxCount, variants: [{ id: item.id }],
  }));
  const ready = data.commerceReady === true;
  const used = product => stockUsed(product, draft, catalog);
  const product = products.find(item => item.id === purchase);
  const unitPrice = transaction?.unitPrice ?? product?.price ?? -1;
  const packs = Number(quantity), total = packs * unitPrice;
  const validQuantity = Number.isInteger(packs) && packs >= 1 && packs <= 2147483647 && product && product.stock + product.packSize * packs <= 2147483647;
  const validTotal = validQuantity && Number.isSafeInteger(total) && unitPrice >= 0;
  let affordable = false, remaining = '—';
  try {
    const balance = BigInt(transaction?.balanceBefore || data.balanceText || String(data.balance ?? -1));
    affordable = validTotal && balance >= BigInt(total);
    if (affordable) remaining = balance - BigInt(total);
  } catch { /* A fresh server quote is required before any purchase. */ }
  useEffect(() => {
    request.current = null; sending.current = false; setTransaction(null); setWaiting(false); setPurchase(null); setError('');
  }, [data.token]);
  useEffect(() => { onPurchaseBusy(!!transaction); return () => onPurchaseBusy(false); }, [transaction, onPurchaseBusy]);
  useEffect(() => {
    if (!transaction || data.commerceOrder !== transaction.order) return;
    sending.current = false; setWaiting(false);
    if (!data.commerceCompleted) { setError(data.commerceMessage || 'Не удалось проверить результат. Повторите тот же запрос.'); return; }
    request.current = null; setTransaction(null);
    if (data.commerceSuccess) { setPurchase(null); setError(''); }
    else setError(data.commerceMessage || 'Покупка не выполнена.');
  }, [data.commerceSequence, transaction?.order]);
  useEffect(() => {
    if (!waiting) return;
    const timer = setTimeout(() => { sending.current = false; setWaiting(false); setError('Сервер не ответил. Повторите запрос: его идентификатор сохранён, повторного списания не будет.'); }, 12000);
    return () => clearTimeout(timer);
  }, [waiting]);

  const openPurchase = item => { if (transaction) return; setPurchase(item.id); setQuantity('1'); setError(''); };
  const submit = () => {
    if (sending.current || !request.current && (!product || !ready || !validTotal || !affordable)) return;
    const intent = request.current || { order: crypto.randomUUID(), product: product.id, quantity: packs, unitPrice, stockBefore: product.stock, balanceBefore: data.balanceText || String(data.balance) };
    request.current = intent; sending.current = true; setTransaction(intent); setWaiting(true); setError('');
    act('buyFurniture', { order: intent.order, product: intent.product, quantity: intent.quantity, unitPrice: intent.unitPrice });
  };
  const visible = products.filter(item => (category === 'all' || item.category === category)
    && (item.name + ' ' + variantsFor(item, catalog).map(variant => variant.name).join(' ')).toLocaleLowerCase('ru').includes(query.trim().toLocaleLowerCase('ru'))
    && (filter === 'all' || (filter === 'owned') === (item.stock > 0)));
  const original = catalog.filter(item => item.existingOnly && item.name.toLocaleLowerCase('ru').includes(query.trim().toLocaleLowerCase('ru')));
  return <>
    <aside className="ApartmentEditor__catalog">
      <div className="ApartmentEditor__catalogHead"><h3>Каталог обустройства</h3><input aria-label="Поиск предметов" type="search" value={query} placeholder="Найти предмет…" onFocus={() => act('focus')} onChange={event => setQuery(event.target.value)} />
        <select aria-label="Категория предметов" value={category} onFocus={() => act('focus')} onChange={event => setCategory(event.target.value)}><option value="all">Все категории</option>{[...new Set(products.map(item => item.category))].map(value => <option key={value}>{value}</option>)}</select>
        <div className="ApartmentEditor__filters">{[['all', 'Все'], ['owned', 'Открытые'], ['locked', 'За LC']].map(([id, label]) => <button key={id} aria-pressed={filter === id} onClick={() => setFilter(id)}>{label}</button>)}<button aria-pressed={filter === 'original'} onClick={() => setFilter('original')}>Исходный интерьер</button></div>
        {!ready && <p role="status">{data.commerceMessage || 'Загрузка баланса и цен…'}{data.commerceMessage && <button onClick={() => act('refreshCommerce')}>Повторить загрузку</button>}</p>}
      </div>
      <div className="ApartmentEditor__products">
        {filter === 'original' ? original.map(item => <button key={item.id} data-existing={item.id} className={selected === item.placementId ? 'selected' : ''} disabled={before || pending || !!transaction} onClick={() => selectExisting(item.placementId)}><Sprite images={item.images} /><span>{item.name}</span></button>) : visible.map(item => {
          const variants = variantsFor(item, catalog), count = used(item), available = item.stock - count;
          const canPlace = !before && !pending && !transaction && available > 0;
          const canBuy = ready && !pending && !transaction && item.price >= 0 && data.balance >= item.price;
          const thumbnail = variants.length >= 3 ? [variants[1], variants[0], variants[2]] : variants;
          return <article key={item.id} data-product={item.id} className={variants.some(variant => variant.id === brush) || active === item.id ? 'selected' : ''}>
            <button className="ApartmentEditor__productArt" aria-label={`Выбрать ${item.name}`} onClick={() => setActive(active === item.id ? null : item.id)}>{thumbnail.map(variant => <Sprite key={variant.id} images={variant.images} />)}</button>
            <div className="ApartmentEditor__productBody"><strong>{item.name}</strong>{variants.length > 1 && <small>Все секции вместе · общий запас</small>}<small>{item.stock > 0 ? `На плане: ${count} / ${item.stock}` : `${item.packSize} шт. в наборе`}</small>
              <button data-place={item.id} disabled={!canPlace} className="place" onClick={() => { setActive(item.id); choose(variants[0].id); }}>{item.stock > 0 ? available > 0 ? 'Разместить' : 'Весь запас размещён' : 'Модель не открыта'}</button>
              <button data-buy={item.id} className={item.stock > 0 ? 'expand' : 'buy'} disabled={!canBuy} onClick={() => openPurchase(item)}>{item.price < 0 ? 'Загрузка цены…' : item.stock > 0 ? `Ещё +${item.packSize} · ${item.price ? priceText(item.price) : 'бесплатно'}` : `Открыть · ${priceText(item.price)}`}</button>
              {active === item.id && variants.length > 1 && <div className="ApartmentEditor__variants" aria-label="Секции дивана">{variants.map((variant, index) => <button key={variant.id} data-variant={variant.id} disabled={!canPlace} aria-pressed={brush === variant.id} onClick={() => choose(variant.id)}><Sprite images={variant.images} />{variants.length === 4 ? ['Средняя', 'Левая', 'Правая', 'Угловая'][index] : variant.name}</button>)}</div>}
            </div>
          </article>;
        })}
        {filter !== 'original' && !visible.length && <p>Предметы не найдены.</p>}
      </div>
    </aside>
    {product && <div className="ApartmentEditor__veil"><div className="ApartmentEditor__purchase" role="dialog" aria-label="Покупка набора мебели"><h3>{product.stock > 0 ? 'Увеличить запас' : 'Открыть модель'}</h3>
      <div className="ApartmentEditor__purchaseProduct"><div>{variantsFor(product, catalog).slice(0, 3).map(variant => <Sprite key={variant.id} images={variant.images} />)}</div><strong>{product.name}</strong></div>
      {variantsFor(product, catalog).length > 1 && <p>Все секции входят в покупку и расходуют общий запас.</p>}
      <label>Количество наборов<input aria-label="Количество наборов" type="number" min="1" max="2147483647" step="1" value={quantity} disabled={!!transaction} onFocus={() => act('focus')} onChange={event => setQuantity(event.target.value)} /></label>
      <p>{product.packSize} экземпляров за {unitPrice >= 0 ? priceText(unitPrice) : '—'} в каждом наборе.</p>
      <dl><dt>Запас модели</dt><dd>{transaction?.stockBefore ?? product.stock} → {validQuantity && Number.isSafeInteger((transaction?.stockBefore ?? product.stock) + product.packSize * packs) ? (transaction?.stockBefore ?? product.stock) + product.packSize * packs : '—'} шт.</dd><dt>К оплате</dt><dd className="amount">{validTotal ? priceText(total) : '—'}</dd><dt>Баланс после покупки</dt><dd>{remaining.toLocaleString('ru-RU')} LC</dd></dl>
      <p className="ApartmentEditor__error" role="status">{error || (!validQuantity ? 'Введите целое число наборов от 1.' : !affordable ? 'Недостаточно Lagoon Coin.' : '')}</p>
      <small>Наборы сохраняются сразу. Сброс расстановки не отменяет покупку.</small>
      <div className="ApartmentEditor__purchaseActions"><button disabled={!!transaction} onClick={() => setPurchase(null)}>Отмена</button><button className="buy" disabled={waiting || !transaction && (!ready || !validTotal || !affordable)} onClick={submit}>{waiting ? 'Проверка покупки…' : transaction ? 'Повторить запрос' : `Купить за ${validTotal ? priceText(total) : '—'}`}</button></div>
    </div></div>}
  </>;
};
