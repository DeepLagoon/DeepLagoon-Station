import './ApartmentShop.scss';
import { useEffect, useId, useRef, useState } from 'react';
import { useBackend } from '../backend';
import { EmbeddedSelect } from '../components/EmbeddedSelect';
import { StoreHeader, StoreTabs, StoreDialog, formatCoins } from './LagoonStore';

// Also works in stock CEF builds without crypto.randomUUID().
const newOrder = () => 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, c => {
  const r = Math.floor(Math.random() * 16);
  return (c === 'x' ? r : (r & 3) | 8).toString(16);
});

const capitalizeName = (name = '') => name.charAt(0).toLocaleUpperCase('ru') + name.slice(1);

const ItemAppearance = ({ item, large = false }) => {
  const id = useId();
  const images = (item.images || []).filter(image => image.url).map(image => {
    const color = /^#[0-9a-f]{6}$/i.test(image.color || '') ? image.color : '#ffffff';
    const [r, g, b] = [1, 3, 5].map(offset => parseInt(color.slice(offset, offset + 2), 16) / 255);
    return { ...image, tinted: color.toLowerCase() !== '#ffffff', matrix: `${r} 0 0 0 0 0 ${g} 0 0 0 0 0 ${b} 0 0 0 0 0 1 0` };
  });
  return <span className={`ApartmentShop__itemImage ${large ? 'ApartmentShop__itemImage--large' : ''}`} role="img" aria-label={`Внешний вид: ${item.name}`}>
    {images.length ? <span className="ApartmentShop__itemLayers">
      <svg aria-hidden="true" width="0" height="0"><defs>{images.map((image, index) => image.tinted && <filter key={index} id={`${id}-${index}`} colorInterpolationFilters="sRGB"><feColorMatrix type="matrix" values={image.matrix} /></filter>)}</defs></svg>
      {images.map((image, index) => <img key={index} src={image.url} alt="" draggable={false} style={image.tinted ? { filter: `url(#${id}-${index})` } : undefined} />)}
    </span>
      : <small>Изображение недоступно</small>}
  </span>;
};

const Plan = ({ scene }) => {
  const scale = Math.min(30, 550 / scene.width, 360 / scene.height);
  // Engine draw depths are relative to other sprites, and may be negative.
  // Paint floors first, then sprites in engine order with positive CSS layers.
  const objects = [...(scene.objects || [])].sort((a, b) => (a.depth || 0) - (b.depth || 0) || b.y - a.y);
  const layers = [...(scene.tiles || []), ...objects];
  return <div className="LagoonStore__plan" role="img" aria-label="Планировка квартиры с мебелью" style={{ width: scene.width * scale, height: scene.height * scale, '--cell': `${scale}px` }}>
    {layers.map((item, index) => <span key={index} className="LagoonStore__planItem" data-prototype={item.prototype} data-kind={item.kind} style={{
      left: (item.x - scene.x - (item.kind === 'entity' ? .5 : 0)) * scale,
      top: (scene.height - (item.y - scene.y) - (item.kind === 'entity' ? .5 : 1)) * scale,
      zIndex: item.kind === 'entity' ? index + 1 : 0,
    }}>{(item.images || []).map((image, layer) => <img key={layer} src={image.url} alt="" draggable={false} style={image.native ? {
      width: `${image.width / 32 * 100}%`, height: `${image.height / 32 * 100}%`,
      left: `${50 + image.m31 * 100}%`, top: `${50 - image.m32 * 100}%`,
      transform: `translate(-50%, -50%) rotate(${image.worldAligned || item.noRotation ? 0 : -item.rotation}deg) matrix(${image.m11}, ${-image.m12}, ${-image.m21}, ${image.m22}, 0, 0)`,
    } : undefined} />)}</span>)}
  </div>;
};

export const ApartmentShop = () => {
  const { data, act } = useBackend();
  const main = useRef(null);
  const [tab, setTab] = useState('owned'), [search, setSearch] = useState(''), [category, setCategory] = useState('all');
  const [sort, setSort] = useState('cheap'), [minimum, setMinimum] = useState(''), [maximum, setMaximum] = useState('');
  const [pending, setPending] = useState(false), [confirm, setConfirm] = useState(null), [preview, setPreview] = useState(null);
  const [previewAttempt, setPreviewAttempt] = useState(0);
  const [itemPreview, setItemPreview] = useState(null);
  useEffect(() => { if (main.current) main.current.scrollTop = 0; }, [tab, data.token]);
  const [itemSearch, setItemSearch] = useState('');
  const [galleryPage, setGalleryPage] = useState(0), [galleryAttempt, setGalleryAttempt] = useState(0), [painting, setPainting] = useState(null);
  const sending = useRef(false);
  useEffect(() => { sending.current = false; setPending(false); setConfirm(null); setItemPreview(null); }, [data.token]);
  const send = (action, fields) => {
    if (sending.current) return;
    sending.current = true; setPending(true); act(action, fields);
  };
  useEffect(() => { if (preview) { act('preview', { template: preview.id }); } }, [preview?.id, data.token, previewAttempt]);
  useEffect(() => {
    if (tab !== 'gallery') return;
    const timer = setTimeout(() => act('gallery', { page: galleryPage }), 550);
    return () => clearTimeout(timer);
  }, [tab, data.token, galleryPage, galleryAttempt]);
  const gallery = data.gallery || {};
  const templates = data.templates || [];
  const categories = [...new Set(templates.map(item => item.category || 'Классика'))].sort();
  const query = search.trim().toLocaleLowerCase('ru');
  const visible = templates.filter(item => (tab === 'owned' ? item.owned : !item.owned)
    && (category === 'all' || (item.category || 'Классика') === category)
    && `${item.name} ${item.description || ''} ${item.category || ''}`.toLocaleLowerCase('ru').includes(query)
    && (minimum === '' || item.price >= Number(minimum)) && (maximum === '' || item.price <= Number(maximum)))
    .sort((a, b) => (sort === 'popular' ? (b.popularity || 0) - (a.popularity || 0) : sort === 'expensive' ? b.price - a.price : a.price - b.price) || a.name.localeCompare(b.name, 'ru'));
  const affordable = item => item.owned || data.balance >= item.price;
  const previewData = data.preview?.id === preview?.id ? data.preview : null;
  return <div className="LagoonStore ApartmentShop">
    <StoreHeader title="Магазин квартир" balance={data.balance} />
    <StoreTabs tabs={[["owned", `Мои квартиры · ${templates.filter(t => t.owned).length}`], ["catalog", `Каталог · ${templates.filter(t => !t.owned).length}`], ...(data.items?.length ? [["items", 'Предметы']] : []), ["gallery", 'Галерея']]} selected={tab} onChange={setTab} />
    <main ref={main} inert={confirm || preview || painting || itemPreview ? true : undefined}>
      {data.message && <p className="LagoonStore__notice" role="status">{data.message}</p>}
      {data.pendingItemOrder && <div className="LagoonStore__notice"><p>Есть незавершённый заказ. Завершите доставку; повторного списания не будет.</p><button disabled={pending} onClick={() => send('retryItem', { order: data.pendingItemOrder })}>Завершить доставку</button></div>}
      <div className="LagoonStore__intro"><p>Ваш номер квартиры: <strong>{data.number}</strong></p>{!data.lobby && <small>После смены шаблона спуститесь на остановку и введите новый номер квартиры.</small>}</div>
      {(tab === 'owned' || tab === 'catalog') ? <>
        <div className="LagoonStore__filters">
          <label>Поиск<input aria-label="Поиск квартир" placeholder="Название, описание, тема…" value={search} onFocus={() => act('focus')} onChange={e => setSearch(e.target.value)} /></label>
          <label>Тематика<EmbeddedSelect aria-label="Тематика" value={category} onChange={e => setCategory(e.target.value)}><option value="all">Все темы</option>{categories.map(c => <option key={c} value={c}>{c}</option>)}</EmbeddedSelect></label>
          <label>От, LC<input aria-label="Минимальная цена" type="number" min="0" value={minimum} onFocus={() => act('focus')} onChange={e => setMinimum(e.target.value)} /></label>
          <label>До, LC<input aria-label="Максимальная цена" type="number" min="0" value={maximum} onFocus={() => act('focus')} onChange={e => setMaximum(e.target.value)} /></label>
          <label>Сортировка<EmbeddedSelect aria-label="Сортировка" value={sort} onChange={e => setSort(e.target.value)}><option value="cheap">Сначала дешёвые</option><option value="expensive">Сначала дорогие</option><option value="popular">Сначала популярные</option></EmbeddedSelect></label>
        </div>
        {tab === 'catalog' && <p className="muted" style={{ marginBottom: 12 }}>Откройте предпросмотр, прежде чем покупать. Цена учитывает ваш квартирный кредит.</p>}
        <div className="LagoonStore__grid">{visible.map(item => <article className={`LagoonStore__card ${item.selected ? 'selected' : ''}`} key={item.id}>
          <div className="LagoonStore__cardTop"><span className="badge">{item.category || 'Классика'}</span>{item.selected && <span className="badge active">Текущая квартира</span>}</div>
          <h3>{item.name}</h3><small>{item.width} × {item.height} клеток · Владельцев: {item.popularity || 0}</small>
          <details><summary>Описание квартиры</summary><p>{item.description || 'Описание пока не добавлено.'}</p></details>
          <button onClick={() => setPreview(item)}>Предпросмотр</button>
          <div className="LagoonStore__cardActions"><div className="price">{item.owned ? <span>В вашей коллекции</span> : <>{formatCoins(item.price)} <span>LC</span></>}</div>
            <button className="primary" disabled={pending || item.selected || !affordable(item)} onClick={() => setConfirm(item)}>{item.selected ? 'Выбрано' : item.owned ? 'Выбрать' : 'Купить и выбрать'}</button>
          </div>{!affordable(item) && <small>Не хватает {formatCoins(item.price - (data.balance || 0))} LC</small>}
        </article>)}</div>
        {!visible.length && <div className="empty">{tab === 'catalog' && !templates.some(t => !t.owned) ? 'Все доступные квартиры уже в вашей коллекции.' : 'Квартиры не найдены. Измените условия поиска.'}<br /><button style={{ marginTop: 12 }} onClick={() => { setSearch(''); setCategory('all'); setMinimum(''); setMaximum(''); }}>Сбросить фильтры</button></div>}
      </> : tab === 'items' ? <>
        <p className="LagoonStore__notice ApartmentShop__deliveryNotice">Купленные предметы появляются у входа в вашу квартиру.</p>
        <div className="LagoonStore__filters ApartmentShop__itemFilters"><label>Поиск предметов<input aria-label="Поиск предметов" value={itemSearch} onFocus={() => act('focus')} onChange={e => setItemSearch(e.target.value)} /></label></div>
        <div className="LagoonStore__grid">{(data.items || []).filter(item => `${item.name} ${item.category}`.toLocaleLowerCase('ru').includes(itemSearch.toLocaleLowerCase('ru'))).map(item => <article key={item.id} className="LagoonStore__card"><span className="badge ApartmentShop__itemCategory">{item.category}</span><h3 className="ApartmentShop__itemName">{capitalizeName(item.name)}</h3>
          <button className="ApartmentShop__itemPreview" aria-label={`Предпросмотр предмета: ${item.name}`} onClick={() => setItemPreview(item)}><ItemAppearance item={item} /><small>Рассмотреть</small></button>
          <div className="LagoonStore__cardActions"><div className="price">{formatCoins(item.price)} <span>LC</span></div><button className={data.balance >= item.price ? 'primary' : undefined} disabled={pending || !!data.pendingItemOrder || !(data.balance >= item.price)} onClick={() => setConfirm({ ...item, item: true, order: newOrder() })}>Купить предмет</button></div></article>)}</div>
      </> : <>
        <div className="LagoonStore__notice"><strong>Личная картина</strong><p>Купите рамку и долговечный холст в разделе «Предметы». Возьмите рамку в руку и нажмите ею на стену своей квартиры. Нарисуйте картину кистью на долговечном холсте, закройте редактор и вставьте холст в рамку. Картина и её слои сохранятся между раундами без публикации в библиотеке. Обычный холст в эту рамку вставить нельзя.</p></div>
        <div className="LagoonStore__cardActions"><h2>Картины в квартире{gallery.loaded && !gallery.error ? ` · ${gallery.total}` : ''}</h2><button onClick={() => setGalleryAttempt(n => n + 1)}>Обновить галерею</button></div>
        {!gallery.loaded ? <p role="status">Загрузка картин квартиры…</p> : gallery.error ? <p role="alert">{gallery.error}</p> : !gallery.total ? <div className="empty">В квартире пока нет нарисованных картин.</div> : <>
          <div className="LagoonStore__grid">{(gallery.paintings || []).map(item => <article className="LagoonStore__card" key={item.id}>
            <span className="badge">{item.framed ? 'В рамке' : 'Холст вне рамки'}</span><h3>{item.title}</h3>
            <button className="ApartmentGallery__thumbnail" aria-label={`Предпросмотр: ${item.title}`} onClick={() => setPainting(item)}><img src={item.image} alt={item.title} /></button>
          </article>)}</div>
          <nav className="LagoonStore__dialogActions" aria-label="Страницы галереи"><button disabled={!gallery.page} onClick={() => setGalleryPage(gallery.page - 1)}>Предыдущие картины</button><span>Страница {gallery.page + 1} из {Math.ceil(gallery.total / (gallery.pageSize || 12))}</span><button disabled={(gallery.page + 1) * (gallery.pageSize || 12) >= gallery.total} onClick={() => setGalleryPage(gallery.page + 1)}>Следующие картины</button></nav>
        </>}
      </>}
    </main>
    <footer inert={confirm || preview || painting || itemPreview ? true : undefined}><button disabled={pending} onClick={() => act('edit')}>Обустройство квартиры</button>{data.legacy && <button disabled={pending} onClick={() => act('legacyExit')}>Покинуть старую квартиру</button>}</footer>
    {itemPreview && <StoreDialog title={itemPreview.name} onCancel={() => setItemPreview(null)}>
      <ItemAppearance item={itemPreview} large />
      <p className="muted">{itemPreview.category}</p><div className="price">{formatCoins(itemPreview.price)} <span>LC</span></div>
      <div className="LagoonStore__dialogActions"><button onClick={() => setItemPreview(null)}>Назад к предметам</button><button className={data.balance >= itemPreview.price ? 'primary' : undefined} disabled={pending || !!data.pendingItemOrder || !(data.balance >= itemPreview.price)} onClick={() => { setConfirm({ ...itemPreview, item: true, order: newOrder() }); setItemPreview(null); }}>Купить предмет</button></div>
    </StoreDialog>}
    {painting && <StoreDialog title={painting.title} onCancel={() => setPainting(null)} preview>
      <img className="ApartmentGallery__preview" src={painting.image} alt={painting.title} />
      <p>{painting.framed ? 'Картина находится в рамке в вашей квартире.' : 'Холст находится в вашей квартире вне рамки.'}</p>
      <button onClick={() => setPainting(null)}>Назад в галерею</button>
    </StoreDialog>}
    {preview && <StoreDialog title={preview.name} onCancel={() => setPreview(null)} preview>
      <p className="muted">Исходный шаблон с мебелью. Ваши изменения обстановки здесь не показаны.</p>
      {!previewData ? <p role="status">Загрузка предпросмотра…</p> : previewData.error ? <><p role="alert">{previewData.error}</p><button onClick={() => setPreviewAttempt(n => n + 1)}>Повторить предпросмотр</button></> : <div style={{ maxWidth: '100%', overflow: 'auto' }}><Plan scene={previewData} /></div>}
      <details><summary>Описание квартиры</summary><p>{preview.description || 'Описание пока не добавлено.'}</p></details>
      <div className="LagoonStore__dialogActions"><button onClick={() => setPreview(null)}>Назад к квартирам</button><button className="primary" disabled={pending || preview.selected || !affordable(preview)} onClick={() => { setConfirm(preview); setPreview(null); }}>{preview.selected ? 'Выбрано' : preview.owned ? 'Выбрать' : `Купить за ${formatCoins(preview.price)} LC`}</button></div>
    </StoreDialog>}
    {confirm && <StoreDialog title="Подтверждение выбора" busy={pending} onCancel={() => setConfirm(null)}>
      {confirm.item && <ItemAppearance item={confirm} />}
      <p>{confirm.item ? 'Купить предмет' : confirm.owned ? 'Выбрать квартиру' : 'Купить и выбрать квартиру'} «{confirm.name}»?</p>
      <div className="price">{confirm.owned ? 'Бесплатно' : <>{formatCoins(confirm.price)} <span>LC</span></>}</div>
      <div className="LagoonStore__dialogActions"><button className="primary" disabled={pending} onClick={() => send(confirm.item ? 'buyItem' : 'select', { template: confirm.id, ...(confirm.item ? { order: confirm.order, unitPrice: confirm.price } : {}) })}>{pending ? 'Обработка…' : 'Подтвердить'}</button><button disabled={pending} onClick={() => setConfirm(null)}>Отмена</button></div>
    </StoreDialog>}
  </div>;
};
