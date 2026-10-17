import { memo, useDeferredValue, useEffect, useMemo, useRef, useState } from 'react';
import { useBackend } from '../../backend';
import { Tooltip } from '../../components/Tooltip';
import { TintedSprite } from '../../components/PlayerTheme';
import { rank } from '../fuzzySearch';
import { useIcon } from '../tguiIcons';
import { CabinetWear } from './CabinetWear';
import { BrandDisplay } from './BrandDisplay';
import './VendingMachine.scss';

type Item = {
  key: string;
  id: string;
  type: number;
  name: string;
  description: string;
  code: string;
  price: number;
  priceText: string;
};
type Stock = { key: string; available: boolean; unlimited: boolean; amount: string };
type Data = {
  title?: string;
  items?: Item[];
  stock?: Stock[];
  labels?: Record<string, string>;
  hasBankAccount?: boolean;
  balanceText?: string;
  acceptsCash?: boolean;
  cashText?: string;
  requiresCash?: boolean;
  status?: 'ready' | 'off' | 'broken' | 'busy' | 'denied';
};
const NO_ITEMS: Item[] = [];
const NO_LABELS: Record<string, string> = {};

/** First-frame layered sprites are shared with the other TGUI catalogues. Nothing animates at rest. */
const ProductSprite = memo(({ id, large = false, noImage }: { id: string; large?: boolean; noImage: string }) => {
  const element = useRef<HTMLSpanElement>(null);
  const [visible, setVisible] = useState(false);
  useEffect(() => {
    if (large) return;
    const observer = new IntersectionObserver(entries => {
      if (entries.some(entry => entry.isIntersecting)) {
        setVisible(true);
        observer.disconnect();
      }
    }, { rootMargin: '100px' });
    if (element.current) observer.observe(element.current);
    return () => observer.disconnect();
  }, [id, large]);
  const layers = useIcon(large || visible ? id : '');
  return (
    <span ref={element} className={`VendingMachine__sprite${large ? ' VendingMachine__sprite--large' : ''}`} aria-hidden="true">
      {layers.length ? <>
        {!large && <span className="VendingMachine__sprite-extrusion">{layers.map((layer, index) => layer.color.toLowerCase() === '#ffffff' ? (
          <img key={index} src={layer.url} alt="" draggable={false} />
        ) : <TintedSprite key={index} image={layer.url} color={layer.color} />)}</span>}
        <span className="VendingMachine__sprite-face">{layers.map((layer, index) => layer.color.toLowerCase() === '#ffffff' ? (
          <img key={index} src={layer.url} alt="" draggable={false} />
        ) : (
          <TintedSprite key={index} image={layer.url} color={layer.color} />
        ))}</span>
        {!large && layers.map((layer, index) => <span key={index} className="VendingMachine__sprite-shine" style={{ maskImage: `url("${layer.url}")` }} />)}
      </> : <span className="VendingMachine__no-image">{noImage}</span>}
    </span>
  );
});

export const VendingMachine = () => {
  const { data, act } = useBackend<Data>();
  const labels = data.labels ?? NO_LABELS;
  const t = (key: string) => labels[key] ?? '';
  const items = data.items ?? NO_ITEMS;
  const stock = useMemo(() => new Map((data.stock ?? []).map(entry => [entry.key, entry])), [data.stock]);
  const [query, setQuery] = useState('');
  const [previewKey, setPreviewKey] = useState<string | null>(null);
  const deferredQuery = useDeferredValue(query);
  // Match the native vending search, including typos, word order and ё/е normalization.
  const filtered = useMemo(() => rank(items, deferredQuery, item => `${item.name} ${item.code} ${item.priceText}`), [items, deferredQuery]);
  const preview = items.find(item => item.key === previewKey);
  const currentStock = preview ? stock.get(preview.key) : undefined;
  const isUnavailable = !currentStock?.available;
  const blocked = data.status === 'off' || data.status === 'broken' || data.status === 'busy';
  const status = data.status && data.status !== 'ready' ? t(data.status) : '';
  const quantity = (entry?: Stock) => !entry?.available ? t('unavailable') : entry.unlimited ? t('unlimited') : `${t('remaining')}: ${entry.amount}`;
  const shelf = useRef<HTMLDivElement>(null);
  const cabinet = useRef<HTMLDivElement>(null);
  useEffect(() => { if (shelf.current) shelf.current.scrollTop = 0; }, [deferredQuery]);

  if (!data.items || !data.labels) return <div className="VendingMachine" aria-busy="true" />;

  return (
    <div className="VendingMachine" ref={cabinet} onDragStart={event => event.preventDefault()}>
      <CabinetWear surface={cabinet} />
      <div className="VendingMachine__frame" aria-hidden="true">
        <span className="VendingMachine__screw is-top-left" /><span className="VendingMachine__screw is-top-right" />
        <span className="VendingMachine__screw is-bottom-left" /><span className="VendingMachine__screw is-bottom-right" />
        <span className="VendingMachine__hinge is-upper" /><span className="VendingMachine__hinge is-lower" />
      </div>
      <header className="VendingMachine__brand" data-wear-protect="box">
        <BrandDisplay title={data.title ?? ''} powered={data.status !== 'off' && data.status !== 'broken'} />
      </header>
      <div className="VendingMachine__toolbar">
        <input type="search" value={query} placeholder={t('search')} aria-label={t('search')} data-wear-protect="box" onChange={event => setQuery(event.target.value)} />
        <span className="VendingMachine__count" data-wear-protect="box">{filtered.length} / {items.length}</span>
      </div>
      <main className="VendingMachine__body">
        <section className="VendingMachine__glass" data-wear-protect="box" aria-label={t('catalogue')}>
          <div className="VendingMachine__shelves" ref={shelf}>
            <div className="VendingMachine__grid">
              {filtered.map(item => {
                const entry = stock.get(item.key);
                const available = entry?.available === true;
                return (
                  <Tooltip key={item.key} content={<div className="VendingMachine__tip"><strong>{item.name}</strong><p>{item.description}</p><span>{item.priceText} · {quantity(entry)}</span></div>}>
                    <button type="button" className={`VendingMachine__product${preview?.key === item.key ? ' is-selected' : ''}${available ? '' : ' is-empty'}`}
                      aria-label={`${item.code}. ${item.name}. ${item.priceText}. ${quantity(entry)}`}
                      aria-pressed={preview?.key === item.key}
                      onClick={() => setPreviewKey(item.key)}>
                      <span className="VendingMachine__quantity">{!available ? '—' : entry.unlimited ? '∞' : `×${entry.amount}`}</span>
                      <span className="VendingMachine__well" aria-hidden="true">
                        <span className="VendingMachine__niche-back" />
                        <span className="VendingMachine__niche-wall is-left" /><span className="VendingMachine__niche-wall is-right" />
                        <span className="VendingMachine__niche-floor"><span className="VendingMachine__floor-light" /></span>
                        <span className="VendingMachine__niche-lamp" /><span className="VendingMachine__light-beam" />
                        <span className="VendingMachine__contact-shadow" />
                        <ProductSprite id={item.id} noImage={t('no-image')} />
                      </span>
                      <span className="VendingMachine__ticket"><span>{item.code}</span><span>{item.priceText}</span></span>
                    </button>
                  </Tooltip>
                );
              })}
              {!filtered.length && <p className="VendingMachine__empty" role="status">{t(items.length ? 'no-results' : 'empty')}</p>}
            </div>
          </div>
        </section>
        <aside className="VendingMachine__side" data-wear-protect="box" aria-label={t('preview')}>
          <div className="VendingMachine__display">
            <span className="VendingMachine__caption">{t('balance')}</span>
            <strong className="VendingMachine__balance">{data.hasBankAccount ? data.balanceText : t('no-account')}</strong>
            {data.acceptsCash && <div className="VendingMachine__cash"><span>{t('cash')}</span><span>{data.cashText}</span></div>}
            {!data.requiresCash && <div className="VendingMachine__free">{t('free')}</div>}
          </div>
          <div className="VendingMachine__details">
            {preview ? <>
              <span className="VendingMachine__caption">{t('preview')} · {preview.code}</span>
              <h2>{preview.name}</h2>
              <div className="VendingMachine__details-body">
                <div className="VendingMachine__hero"><ProductSprite id={preview.id} large noImage={t('no-image')} /></div>
                <p className="VendingMachine__description">{preview.description}</p>
              </div>
              <div className="VendingMachine__price"><strong>{preview.priceText}</strong><span>{quantity(currentStock)}</span></div>
              <button type="button" className="VendingMachine__buy" disabled={isUnavailable || blocked}
                onClick={() => act('vend', { id: preview.id, type: preview.type })}>
                {isUnavailable ? t('unavailable') : `${t('buy')} · ${preview.priceText}`}
              </button>
            </> : <p className="VendingMachine__empty">{t('select')}</p>}
          </div>
          <div className={`VendingMachine__status${data.status === 'denied' ? ' is-denied' : ''}`} role="status" aria-live="polite">{status}</div>
        </aside>
      </main>
      <footer className="VendingMachine__footer"><div className="VendingMachine__chute" data-wear-protect="box">{t('chute')}</div><span data-wear-protect="text">{data.requiresCash ? t('payment') : t('free')}</span></footer>
    </div>
  );
};
