import { useEffect, useRef } from 'react';
import { useBackend } from '../backend';
import { EmbeddedSelect } from '../components/EmbeddedSelect';
import { TintedSprite } from '../components/PlayerTheme';
import { StoreDialog } from './LagoonStore';
import './SectorMarket.scss';

const categories = ['all', 'materials', 'equipment', 'weapons', 'food', 'clothing', 'other'];
export const MarketSprite = ({ prototype, icons, label, large = false }) => {
  const layers = (icons?.[prototype] || []).filter(layer => layer.url);
  return <div className={`SectorMarket__image ${large ? 'SectorMarket__image--large' : ''}`} role="img" aria-label={label}>
    {layers.length ? <span className="SectorMarket__sprite">{layers.map((layer, index) => (layer.color || '#ffffff').toLowerCase() === '#ffffff'
      ? <img key={index} src={layer.url} alt="" draggable={false} />
      : <TintedSprite key={index} image={layer.url} color={layer.color} />)}</span> : <span className="SectorMarket__muted">—</span>}
  </div>;
};

export const SectorMarket = () => {
  const { data, act } = useBackend();
  const t = data.labels || {}, icons = data.icons || {}, offers = data.offers || [], items = data.items || [], deliveries = data.deliveries || [];
  const draft = data.draft || {}, section = data.section || 'catalog', selected = offers.find(o => o.id === data.selected);
  const local = data.local || {};
  const groups = data.groups || [], group = groups.find(g => g.key === data.group), basket = data.basket || [], quote = data.quote || {};
  const groupOpen = !!data.groupOpen || !groups.length && offers.length > 0;
  const confirmation = offers.find(o => o.id === data.confirm), own = offers.filter(o => o.mine);
  const item = items.find(i => i.id === data.item), desired = draft.prototypes?.find(p => p.id === draft.prototype);
  const busy = !!data.busy, available = !!data.ready && !busy;
  const field = name => event => act(name, { value: event.target.value });
  const scroll = useRef(null), previousPage = useRef(data.page);
  useEffect(() => { if (previousPage.current !== data.page) { scroll.current?.scrollTo(0, 0); previousPage.current = data.page; } }, [data.page]);
  const image = (o, large = false) => <MarketSprite prototype={o?.prototype} icons={icons} label={o?.name || t['no-image']} large={large} />;
  const prepare = o => act('prepare', { id: o.id });
  const actionText = o => o.mine ? t.cancel : o.buy ? t.supply : o.splittable ? t['buy-whole'] : t.buy;
  const matches = o => item && item.prototype === o.prototype && item.units >= o.units && item.condition >= o.condition && item.charge >= o.charge;
  const itemSelect = <label className="SectorMarket__field">{t.pallet}<EmbeddedSelect value={data.item || ''} onChange={event => act('item', { id: event.target.value })} disabled={busy || !items.length}>
    {!items.length && <option value="">{t['select-prototype']}</option>}
    {items.map(i => <option key={i.id} value={i.id}>{i.name} ×{i.units} · {i.condition}%{i.hasBattery ? ` · ⚡${i.charge}%` : ''}</option>)}
  </EmbeddedSelect></label>;
  const detail = o => o ? <>
    {image(o, true)}<h2>{o.name}</h2><div className="SectorMarket__price">{o.money}</div>
    <small>{t[o.buy ? 'per-delivery' : 'per-lot']}</small>
    <dl><dt>{t.count}</dt><dd>{o.buy ? '≥ ' : ''}{o.units}</dd><dt>{t.state}</dt><dd>{o.buy ? '≥ ' : ''}{o.condition}%</dd>
      {o.hasBattery && <><dt>{t.battery}</dt><dd>{o.buy ? '≥ ' : ''}{o.charge}%</dd></>}<dt>{t[o.buy ? 'buyer' : 'seller']}</dt><dd>{o.character}</dd>
      {o.buy && <><dt>{t.remaining}</dt><dd>{o.remaining}</dd></>}</dl>
    {o.description && <p className="SectorMarket__description">{o.description}</p>}
    <section className="SectorMarket__comments"><h3>{t.comments}</h3>
      {data.commentOffer === o.id && (data.comments || []).map((c, i) => <article key={i}><strong>{c.author}</strong> <small>{c.time}</small><p className="SectorMarket__description">{c.text}</p></article>)}
      {data.readOnly && (data.canComment ? <><small>{data.commentAuthor}</small><textarea maxLength={500} value={data.commentText || ''} onChange={field('commentText')} /><button disabled={!available || !data.commentText?.trim()} onClick={() => act('comment')}>{t['comment-send']}</button></> : <p>{t['comment-id']}</p>)}
    </section>
    {!data.readOnly && <>
    {o.buy && !o.mine && itemSelect}
    <button className="SectorMarket__primary" disabled={!available} onClick={() => prepare(o)}>{actionText(o)}</button>
    {!o.mine && !o.buy && <>
      {o.splittable && <label className="SectorMarket__field">{t.count}<input inputMode="numeric" maxLength={10} value={data.lotAmount || ''} onChange={field('lotAmount')} /></label>}
      <button disabled={!available} onClick={() => act('basketAdd')}>{t['basket-add']}</button>
    </>}
    {group?.material && !group.buy && <><label className="SectorMarket__field">{t['basket-auto-units']}<input inputMode="numeric" maxLength={10} value={data.autoAmount || ''} onChange={field('autoAmount')} /></label>
      <button disabled={!available} onClick={() => act('basketAuto')}>{t['basket-auto']}</button></>}
    </>}
  </> : <p className="SectorMarket__muted">{t['select-offer']}</p>;

  return <div className="SectorMarket">
    <header className="SectorMarket__header"><h1>{data.readOnly ? t.marketplace : t.title}</h1><div className="SectorMarket__wallet"><small>{t['balance-label']}</small><strong>{data.bank || '—'}</strong></div></header>
    <nav className="SectorMarket__nav" role="tablist" aria-label={t.title}>
      {[['catalog', t.board], ['create', t.create], ['mine', t.mine], ['delivery', `${t.deliveries || ''} (${deliveries.length})`], ...(local.available ? [['local', t.local]] : []), ['basket', t.basket]].filter(([key]) => !data.readOnly || key === 'catalog').map(([key, name]) =>
        <button key={key} role="tab" aria-selected={section === key} onClick={() => act('section', { value: key })}>{name}</button>)}
    </nav>
    {section !== 'local' && (data.statusKey !== 'ready' || data.hasError) && data.status && <div className="SectorMarket__status" role="status">{data.status}</div>}
    {section === 'local' && <>
      <div className="SectorMarket__search"><input aria-label={t.search} placeholder={t.search} value={local.search || ''} maxLength={100} onChange={event => act('localSearch', { value: event.target.value })} />
        <button aria-pressed={!!data.cards} onClick={() => act('layout', { value: 'cards' })}>{t.cards}</button><button aria-pressed={!data.cards} onClick={() => act('layout', { value: 'list' })}>{t.list}</button></div>
      <div className="SectorMarket__local"><div className="SectorMarket__catalog">
        <div className={`SectorMarket__offers ${data.cards ? '' : 'SectorMarket__offers--list'}`}>
          {(local.stock || []).map(o => <div className="SectorMarket__offer SectorMarket__local-product" key={o.prototype}>
            {image(o)}<span className="SectorMarket__offer-name"><strong>{o.name}</strong><small>{t['local-stock']}: {o.quantity}</small><strong>{o.unitPrice} · {t['local-unit']}</strong></span>
            <div className="SectorMarket__local-actions">{[1, 5, 10, 30, 2147483647].filter(n => n === 2147483647 || n <= o.quantity).map(n => <button key={n} disabled={!local.enabled} onClick={() => act('localAdd', { prototype: o.prototype, amount: n })}>{n === 2147483647 ? t['local-all'] : `+${n}`}</button>)}</div>
          </div>)}
          {!local.stock?.length && <p className="SectorMarket__empty">{t['local-empty']}</p>}
        </div>
        <div className="SectorMarket__pagination"><button disabled={!local.page} onClick={() => act('localPage', { value: local.page - 1 })}>{t.prev}</button><span>{(local.page || 0) + 1} / {local.pages || 1}</span><button disabled={(local.page || 0) + 1 >= (local.pages || 1)} onClick={() => act('localPage', { value: local.page + 1 })}>{t.next}</button></div>
      </div><aside className="SectorMarket__details SectorMarket__local-cart"><h2>{t['local-cart']} · {local.entities || 0}/30</h2>
        {(local.cart || []).map(o => <div className="SectorMarket__local-cart-item" key={o.prototype}>{image(o)}<strong>{o.name}</strong><span>×{o.quantity} · {o.money}</span><button disabled={!local.enabled} onClick={() => act('localRemove', { prototype: o.prototype })}>{t['local-remove']}</button></div>)}
        <dl><dt>{t['local-fee']}</dt><dd>{local.fee}</dd><dt>{t['total-label']}</dt><dd>{local.total}</dd></dl>
        <button className="SectorMarket__primary" disabled={!local.canPay} onClick={() => act('localCheckout')}>{t['local-checkout']}</button>
      </aside></div>
    </>}
    {section === 'catalog' && <>
      <div className="SectorMarket__toolbar"><input aria-label={t.search} placeholder={t.search} value={data.search || ''} onChange={field('search')} maxLength={100} />
        <button onClick={() => act('refresh')} disabled={busy}>{t.refresh}</button><div className="SectorMarket__switch" aria-label={`${t.cards} / ${t.list}`}>
          <button aria-pressed={!!data.cards} onClick={() => act('layout', { value: 'cards' })}>{t.cards}</button><button aria-pressed={!data.cards} onClick={() => act('layout', { value: 'list' })}>{t.list}</button></div>
      </div>
      <div className="SectorMarket__filters"><EmbeddedSelect aria-label={t.all} value={data.filter || 0} onChange={field('filter')}>
        {[t.all, t['selling-short'], t['wanted-short']].map((name, id) => <option key={id} value={id}>{name}</option>)}</EmbeddedSelect>
        <EmbeddedSelect aria-label={t['category-all']} value={data.category || 0} onChange={field('category')}>{categories.map((c, id) => <option key={c} value={id}>{t[`category-${c}`]}</option>)}</EmbeddedSelect>
        <EmbeddedSelect aria-label={t['sort-new']} value={data.sort || 0} onChange={field('sort')}>{['sort-new', 'sort-cheap', 'sort-expensive'].map((key, id) => <option key={key} value={id}>{t[key]}</option>)}</EmbeddedSelect>
      </div>
      {(groups.length > 0 || !groupOpen) && <div className="SectorMarket__catalogue SectorMarket__catalogue--browse" style={{ display: groupOpen ? 'none' : undefined }}><div className="SectorMarket__catalogue-left">
        <div className={`SectorMarket__offers SectorMarket__group-grid ${data.cards ? '' : 'SectorMarket__offers--list'}`}>
          {groups.map(g => <button key={g.key} className="SectorMarket__offer" aria-pressed={g.key === data.group} onClick={() => act('group', { key: g.key })}>
            {image(g)}<span className="SectorMarket__offer-name"><small>{t[g.buy ? 'wanted-short' : 'selling-short']}</small><strong>{g.name}</strong></span>
            <span className="SectorMarket__offer-price"><strong>{t.from} {g.money}</strong><small>{t[g.material ? 'local-unit' : 'per-lot']}</small></span>
          </button>)}
          {!groups.length && <p className="SectorMarket__empty">{t.empty}</p>}
        </div>
        <div className="SectorMarket__pagination"><button disabled={busy || !data.page} onClick={() => act('page', { value: data.page - 1 })}>{t.prev}</button><span>{(data.page || 0) + 1} / {data.pages || 1}</span><button disabled={busy || (data.page || 0) + 1 >= (data.pages || 1)} onClick={() => act('page', { value: data.page + 1 })}>{t.next}</button></div>
      </div></div>}
      {groupOpen && <div className="SectorMarket__catalogue SectorMarket__catalogue--expanded"><div className="SectorMarket__catalogue-left">
        <div className="SectorMarket__offers-heading"><strong>{group?.name || t.offers}</strong><button aria-label={t['close-offers']} title={t['close-offers']} onClick={() => act('closeGroup')}>×</button></div>
        <div className={`SectorMarket__offers ${data.cards && !groups.length ? '' : 'SectorMarket__offers--list'}`} ref={scroll}>
          {offers.map(o => <button key={o.id} className="SectorMarket__offer" aria-pressed={o.id === data.selected} onClick={() => act('select', { id: o.id })}>
            {image(o)}<span className="SectorMarket__offer-name"><small>{t[o.buy ? 'wanted-short' : 'selling-short']}{o.mine ? ` · ${t.mine}` : ''}</small><strong>{o.name}</strong>
              <small>{o.character}</small>
              {o.preview && <small className="SectorMarket__offer-excerpt">{o.preview}</small>}
              <small>{o.units} · {o.condition}%{o.buy ? ` · ×${o.remaining}` : ''}</small></span><span className="SectorMarket__offer-price"><strong>{o.money}</strong><small>{t[o.buy ? 'per-delivery' : 'per-lot']}</small>{group?.material && <small>{o.unitMoney} / {t['local-unit']}</small>}</span>
          </button>)}
          {!offers.length && <p className="SectorMarket__empty">{t.empty}</p>}
        </div>
        <div className="SectorMarket__pagination"><button disabled={busy || !(data.sellerPage || 0)} onClick={() => act('sellerPage', { value: (data.sellerPage || 0) - 1 })}>{t.prev}</button><span>{(data.sellerPage || 0) + 1} / {data.sellerPages || 1}</span><button disabled={busy || (data.sellerPage || 0) + 1 >= (data.sellerPages || 1)} onClick={() => act('sellerPage', { value: (data.sellerPage || 0) + 1 })}>{t.next}</button></div>
      </div><aside className="SectorMarket__details">{detail(selected)}</aside></div>}
    </>}
    {section === 'basket' && <div className="SectorMarket__rows">
      {basket.map(o => <div className="SectorMarket__row" key={o.id}>{image(o)}<div><strong>{o.name}</strong><small>{o.character} · ×{o.units}</small><strong>{o.money}</strong></div>
        <button disabled={busy} onClick={() => act('basketRemove', { id: o.id })}>{t['local-remove']}</button></div>)}
      {!basket.length && <p className="SectorMarket__empty">{t['basket-empty']}</p>}
      <button className="SectorMarket__primary" disabled={!available || !basket.length} onClick={() => act('basketQuote')}>{t['basket-quote']}</button>
    </div>}
    {section === 'create' && <div className="SectorMarket__create">
      <div className="SectorMarket__form"><div className="SectorMarket__switch"><button aria-pressed={!draft.buy} onClick={() => act('mode', { value: 'sale' })}>{t['create-sale']}</button><button aria-pressed={!!draft.buy} onClick={() => act('mode', { value: 'buy' })}>{t['create-buy']}</button></div>
        {draft.buy ? <>
          <label className="SectorMarket__field">{t.wanted}<input value={draft.search || ''} onChange={field('prototypeSearch')} maxLength={100} /></label>
          <EmbeddedSelect aria-label={t['select-prototype']} value={draft.prototype || ''} onChange={event => act('prototype', { id: event.target.value })}>
            {!draft.prototypes?.length && <option value="">{t['select-prototype']}</option>}{draft.prototypes?.map(p => <option key={p.id} value={p.id}>{p.name}</option>)}</EmbeddedSelect>
          <div className="SectorMarket__form-grid"><label className="SectorMarket__field">{t.quantity}<input value={draft.quantity || ''} onChange={field('quantity')} inputMode="numeric" maxLength={10} /></label>
            <label className="SectorMarket__field">{t.stack}<input value={draft.units || ''} onChange={field('units')} inputMode="numeric" maxLength={10} /></label>
            <label className="SectorMarket__field">{t.condition}<EmbeddedSelect value={draft.condition ?? 100} onChange={field('condition')}>{[0, 50, 80, 100].map(n => <option key={n} value={n}>{n}%</option>)}</EmbeddedSelect></label>
            {draft.hasBattery && <label className="SectorMarket__field">{t.charge}<EmbeddedSelect value={draft.charge || 0} onChange={field('charge')}>{[0, 25, 50, 100].map(n => <option key={n} value={n}>{n}%</option>)}</EmbeddedSelect></label>}</div>
        </> : itemSelect}
        <label className="SectorMarket__field">{t.price}<input value={draft.price || ''} onChange={field('price')} inputMode="numeric" maxLength={20} /></label>
        <label className="SectorMarket__field">{t.description}<textarea value={draft.description || ''} onChange={field('description')} maxLength={500} rows={3} /></label>
        <p className="SectorMarket__error" role="alert">{draft.error}</p><button className="SectorMarket__primary" disabled={!available || !draft.valid} onClick={() => act('publish')}>{t[draft.buy ? 'publish-order' : 'sell']}</button>
      </div><aside className="SectorMarket__details">{image(draft.buy ? desired : item, true)}<h2>{(draft.buy ? desired : item)?.name || t['select-prototype']}</h2><small>{t[draft.buy ? 'reserved' : 'total-label']}</small><strong className="SectorMarket__price">{draft.total || '—'}</strong></aside>
    </div>}
    {(section === 'mine' || section === 'delivery') && <div className="SectorMarket__rows">
      {(section === 'mine' ? own : deliveries).map(o => <div className="SectorMarket__row" key={o.id}>{image(o)}<div><strong>{o.name}</strong>{section === 'mine' && <><small>{t[o.buy ? 'wanted-short' : 'selling-short']} · {o.units} · ×{o.remaining}</small><strong>{o.money}</strong></>}</div>
        <button disabled={!available} className={section === 'delivery' ? 'SectorMarket__primary' : ''} onClick={() => section === 'mine' ? prepare(o) : act('claim', { id: o.id })}>{t[section === 'mine' ? 'cancel' : 'claim']}</button></div>)}
      {!(section === 'mine' ? own : deliveries).length && <p className="SectorMarket__empty">{t[section === 'mine' ? 'empty-mine' : 'empty-deliveries']}</p>}
      {section === 'mine' && <div className="SectorMarket__pagination"><button disabled={busy || !data.page} onClick={() => act('page', { value: data.page - 1 })}>{t.prev}</button><span>{(data.page || 0) + 1}/{data.pages || 1}</span><button disabled={busy || (data.page || 0) + 1 >= (data.pages || 1)} onClick={() => act('page', { value: data.page + 1 })}>{t.next}</button></div>}
    </div>}
    {local.confirm && section === 'local' && <StoreDialog title={t['local-checkout']} onCancel={() => act('localBack')} busy={!local.enabled}>
      <p>{t['local-confirm']}</p><strong className="SectorMarket__price">{local.total}</strong>
      <div className="SectorMarket__dialog-actions"><button onClick={() => act('localBack')}>{t.back}</button><button className="SectorMarket__primary" disabled={!local.canPay} onClick={() => act('localConfirm')}>{t.confirm}</button></div>
    </StoreDialog>}
    {confirmation && <StoreDialog title={t[confirmation.mine ? 'cancel' : confirmation.buy ? 'supply' : 'buy']} onCancel={() => act('back')} busy={busy}>
      <div className="SectorMarket__confirm-preview">{image(confirmation)}<div><strong>{confirmation.name}</strong><p>{confirmation.units} · {confirmation.condition}%</p></div></div>
      <p>{t[confirmation.mine ? 'confirm-cancel' : confirmation.buy ? 'confirm-supply' : 'confirm-buy']}</p><strong className="SectorMarket__price">{confirmation.money}</strong>
      {confirmation.buy && !confirmation.mine && itemSelect}
      <div className="SectorMarket__dialog-actions"><button disabled={busy} onClick={() => act('back')}>{t.back}</button><button className="SectorMarket__primary" disabled={!available || confirmation.buy && !confirmation.mine && !matches(confirmation)} onClick={() => act('confirm')}>{t.confirm}</button></div>
    </StoreDialog>}
    {quote.open && <StoreDialog title={t['basket-confirm']} onCancel={() => act('basketBack')} busy={busy}>
      <div className="SectorMarket__quote-lines">{(quote.lines || []).map(o => <div className="SectorMarket__row" key={o.id}>{image(o)}<div><strong>{o.name}</strong><small>{o.character} · ×{o.units}</small></div><strong>{o.money}</strong></div>)}</div>
      <p>{t['total-label']}: <strong className="SectorMarket__price">{quote.money}</strong></p>
      <div className="SectorMarket__dialog-actions"><button disabled={busy} onClick={() => act('basketBack')}>{t.back}</button><button className="SectorMarket__primary" disabled={!available || !quote.canPay} onClick={() => act('basketConfirm')}>{t.confirm}</button></div>
    </StoreDialog>}
  </div>;
};
