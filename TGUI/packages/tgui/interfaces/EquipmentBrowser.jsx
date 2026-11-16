import { LazySprite } from '../components/LazySprite';
import { memo, useEffect, useId, useMemo, useRef, useState } from 'react';
import { Button, Dropdown, Section } from '../components';
import { PlayerColorPicker } from '../components/PlayerColorPicker';
import { TintedSprite } from '../components/PlayerTheme';
import './EquipmentBrowser.scss';
import { filter as fuzzyFilter } from './fuzzySearch';
import { BackpackEditor } from './BackpackEditor';

const equipmentKey = item => item.handKey || (item.group || 'personal') + '/' + item.id;

const Sprite = ({item,color='#ffffff'}) => <span className="EquipmentBrowser__sprite">{item.iconId ? <LazySprite id={item.iconId} color={color} /> : (item.images||[]).map((image,i)=><TintedSprite key={i} image={image.url} color={color} baseColor={image.color} />)}</span>;
const Customization = ({item,mode,act,close}) => {
  const [color,setColor]=useState(item.color?.slice(0,7)||'#ffffff');
  const [name,setName]=useState(item.customName||'');
  const [description,setDescription]=useState(item.description||'');
  useEffect(()=>{const handler=e=>{if(e.key==='Escape'){e.stopPropagation();close();}};document.addEventListener('keydown',handler,true);return ()=>document.removeEventListener('keydown',handler,true);},[]);
  return <div className="EquipmentBrowser__backdrop" onPointerDown={e=>{if(e.target===e.currentTarget)close();}}><section role="dialog" aria-modal="true" aria-label={mode==='paint'?'Покраска предмета':'Название и описание'} className="EquipmentBrowser__modal">
    <header><strong>{item.name}</strong><Button role="button" icon="times" aria-label="Закрыть" onClick={close} /></header>
    {mode==='paint'?<><Sprite item={item} color={color} /><PlayerColorPicker value={color} onChange={setColor} channels /><Button onClick={()=>{act('paint',{id:item.id,group:item.group,jobItem:!!item.jobItem,value:''});close();}}>Сбросить покраску</Button></>:<>
      {item.canRename&&<label>Название<input autoFocus value={name} maxLength={32} onChange={e=>setName(e.target.value)} /></label>}
      {item.canDescribe&&<label>Описание<textarea value={description} maxLength={512} onChange={e=>setDescription(e.target.value)} /></label>}
    </>}
    <footer><Button role="button" onClick={close}>Отмена</Button><Button role="button" color="good" onClick={()=>{act(mode==='paint'?'paint':'rename',mode==='paint'?{id:item.id,group:item.group,jobItem:!!item.jobItem,value:color}:{id:item.id,group:item.group,jobItem:!!item.jobItem,name,description});close();}}>Применить</Button></footer>
  </section></div>;
};

const EquipmentCard = ({ item, compact, act, onCustomize }) => (
  <article title={[item.customName || item.name, item.reason, item.groupName, item.donor].filter(Boolean).join(' · ')}>
    <button type="button" disabled={!item.canSelect} aria-pressed={!!item.selected}
      onClick={() => item.handKey ? act('hand-select', { key: item.handKey })
        : act(item.jobItem ? 'job-select' : 'select', { id: item.id, group: item.group })}>
      <Sprite item={item} color={item.color?.slice(0, 7) || '#ffffff'} />
      <span className="EquipmentBrowser__details">
        <strong>{item.customName || item.name}</strong>
        <small>{item.cost} {item.jobItem && !item.costInPoints ? '$' : item.cost === 1 ? 'очко' : 'очков'}</small>
        {!compact && <>
          {item.donor && <small>{item.donor}</small>}
          {!item.allowed && <small>🔒 {item.reason}</small>}
          {item.jobItem && item.groupName && <small>{item.groupName} · {item.min}–{item.max}</small>}
        </>}
      </span>
    </button>
    <div className="EquipmentBrowser__tools">
      {item.canPaint && <Button role="button" icon="palette" aria-label="Покрасить" tooltip="Покрасить"
        onClick={() => onCustomize({ item, mode: 'paint' })}>{!compact && 'Покрасить'}</Button>}
      {(item.canRename || item.canDescribe) && <Button role="button" icon="pen" aria-label="Название и описание"
        tooltip="Название и описание" onClick={() => onCustomize({ item, mode: 'rename' })}>
        {!compact && 'Название / описание'}
      </Button>}
      {item.canHeirloom && item.selected && <Button role="button" icon="star" selected={item.heirloom}
        aria-label="Семейная реликвия" tooltip="Семейная реликвия" onClick={() => act('heirloom', { id: item.id })} />}
    </div>
  </article>
);

export const EquipmentBrowser = memo(({data={},act,backpack,characterSlot,active}) => {
  const [visibleCount,setVisibleCount]=useState(24);
  const [query,setQuery]=useState('');
  const sentinel=useRef(null);
  const searchId = useId();

  const [unavailable, setUnavailable] = useState(false);
  const [modal, setModal] = useState(null);
  const [equippedExpanded, setEquippedExpanded] = useState(true);
  const equippedId = useId();
  const catalog = useMemo(() => data.handItems || [
    ...(data.jobItems || []).map(item => ({ ...item, jobItem: true })),
    ...(data.items || []),
  ], [data.handItems, data.items, data.jobItems]);
  const equipped = useMemo(() => catalog.filter(item => item.selected), [catalog]);
  const filtered = useMemo(() => fuzzyFilter(catalog.filter(item => !item.selected && (unavailable || (item.allowed && item.canSelect))),
    query, item => [item.name, item.customName, item.id].filter(Boolean).join(' ')), [catalog, unavailable, query]);
  useEffect(() => setVisibleCount(24), [unavailable, data.hand, data.slot, data.job, query]);
  useEffect(()=>{const observer=new IntersectionObserver(entries=>{if(entries.some(e=>e.isIntersecting))setVisibleCount(n=>n+24);});if(sentinel.current)observer.observe(sentinel.current);return()=>observer.disconnect();},[visibleCount,filtered.length]);
  const item = modal && catalog.find(item => equipmentKey(item) === equipmentKey(modal.item));
  return <div className="EquipmentBrowser">
    <Section title={data.hand || data.slot ? 'Снаряжение · ' + (data.hand || data.slot) : 'Снаряжение'}>
      <Dropdown selected={data.job} options={(data.jobs||[]).map(job=>({value:job.id,displayText:job.name}))} onSelected={value=>act('job',{value})} />
      <div className="EquipmentBrowser__summary" aria-label="Бюджет снаряжения">
        <div><span>Счёт персонажа</span><strong>{data.balance}</strong></div>
        <div><span>Стоимость снаряжения</span><strong>{data.cost}</strong></div>
        <div><span>Очки снаряжения</span><strong>{data.points} / {data.maxPoints}</strong><progress max={Math.max(1,data.maxPoints||0)} value={data.points||0} /></div>
        {data.maxJobPoints > 0 && <div><span>Очки формы</span><strong>{data.jobPoints} / {data.maxJobPoints}</strong><progress max={data.maxJobPoints} value={data.jobPoints} /></div>}
      </div>
      <BackpackEditor data={backpack} context={`${data.job}/${characterSlot ?? ''}`} active={active} act={act} showUnavailable={unavailable} />
      <Button.Checkbox role="checkbox" aria-checked={unavailable} checked={unavailable} onClick={() => setUnavailable(!unavailable)}>Показать недоступные</Button.Checkbox><Button onClick={()=>act('remove-unavailable')}>Убрать недоступные</Button>
    </Section>
    <div className="EquipmentBrowser__search">
      <label htmlFor={searchId}>Поиск снаряжения</label>
      <div className="EquipmentBrowser__searchRow">
        <input id={searchId} type="search" placeholder="Название или ID предмета…" value={query} onChange={event=>setQuery(event.target.value)} />
        <Button role="button" disabled={!query} aria-disabled={!query} icon="times" onClick={()=>setQuery('')}>Очистить поиск</Button>
      </div>
    </div>
    {!!equipped.length && <section className="EquipmentBrowser__equipped" aria-label="Экипировано">
      <h3><Button role="button" aria-label="Экипировано" aria-expanded={equippedExpanded} aria-controls={equippedId}
        icon={equippedExpanded ? 'chevron-down' : 'chevron-right'} onClick={() => setEquippedExpanded(!equippedExpanded)}>
        Экипировано
      </Button></h3>
      <div id={equippedId} className="EquipmentBrowser__grid EquipmentBrowser__grid--equipped" hidden={!equippedExpanded}>
        {equipped.map(item => <EquipmentCard key={equipmentKey(item)} item={item} compact act={act} onCustomize={setModal} />)}
      </div>
    </section>}
    {!filtered.length && (query.trim() || !equipped.length) && <p role="status">Ничего не найдено</p>}
    <div className="EquipmentBrowser__grid" aria-label="Каталог снаряжения">
      {filtered.slice(0, visibleCount).map(item => <EquipmentCard key={equipmentKey(item)} item={item} act={act} onCustomize={setModal} />)}
    </div>
    {visibleCount<filtered.length&&<div ref={sentinel}><Button fluid onClick={()=>setVisibleCount(n=>n+24)}>Показать ещё</Button></div>}
    {modal&&item&&<Customization key={equipmentKey(item) + '/' + modal.mode} item={item} mode={modal.mode} act={act} close={()=>setModal(null)} />}
  </div>;
},(before,after)=>before.data===after.data && before.backpack===after.backpack
  && before.characterSlot===after.characterSlot && before.active===after.active);
