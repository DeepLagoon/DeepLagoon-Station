import { LazySprite } from '../components/LazySprite';
import { memo, useEffect, useId, useMemo, useRef, useState } from 'react';
import { Button, Dropdown, Section } from '../components';
import { PlayerColorPicker } from '../components/PlayerColorPicker';
import { TintedSprite } from '../components/PlayerTheme';
import './EquipmentBrowser.scss';
import { filter as fuzzyFilter } from './fuzzySearch';
import { BackpackEditor } from './BackpackEditor';

const buildCategoryIndex = (categories) => {
  const index = new Map(categories.map(category => [category.id, category]));
  const branches = new Map();
  const visit = (id, parents) => {
    if (!index.has(id) || branches.has(id)) return;
    const branch = [...parents, id];
    branches.set(id, branch);
    for (const child of index.get(id).children || []) visit(child.id, branch);
  };
  const roots = categories.filter(category => category.root);
  for (const root of roots) visit(root.id, []);
  return { index, branches, roots };
};

const Sprite = ({item,color='#ffffff'}) => <div className="EquipmentBrowser__sprite">{item.iconId ? <LazySprite id={item.iconId} color={color} /> : (item.images||[]).map((image,i)=><TintedSprite key={i} image={image.url} color={color} baseColor={image.color} />)}</div>;
const Customization = ({item,mode,act,close}) => {
  const [color,setColor]=useState(item.color?.slice(0,7)||'#ffffff');
  const [name,setName]=useState(item.customName||'');
  const [description,setDescription]=useState(item.description||'');
  useEffect(()=>{const handler=e=>{if(e.key==='Escape'){e.stopPropagation();close();}};document.addEventListener('keydown',handler,true);return ()=>document.removeEventListener('keydown',handler,true);},[]);
  return <div className="EquipmentBrowser__backdrop" onPointerDown={e=>{if(e.target===e.currentTarget)close();}}><section role="dialog" aria-modal="true" aria-label={mode==='paint'?'Покраска предмета':'Название и описание'} className="EquipmentBrowser__modal">
    <header><strong>{item.name}</strong><Button icon="times" onClick={close} /></header>
    {mode==='paint'?<><Sprite item={item} color={color} /><PlayerColorPicker value={color} onChange={setColor} channels /><Button onClick={()=>{act('paint',{id:item.id,group:item.group,jobItem:!!item.jobItem,value:''});close();}}>Сбросить покраску</Button></>:<>
      {item.canRename&&<label>Название<input autoFocus value={name} maxLength={32} onChange={e=>setName(e.target.value)} /></label>}
      {item.canDescribe&&<label>Описание<textarea value={description} maxLength={512} onChange={e=>setDescription(e.target.value)} /></label>}
    </>}
    <footer><Button onClick={close}>Отмена</Button><Button color="good" onClick={()=>{act(mode==='paint'?'paint':'rename',mode==='paint'?{id:item.id,group:item.group,jobItem:!!item.jobItem,value:color}:{id:item.id,group:item.group,jobItem:!!item.jobItem,name,description});close();}}>Применить</Button></footer>
  </section></div>;
};
export const EquipmentBrowser = memo(({data={},act,backpack,characterSlot,active}) => {
  const [visibleCount,setVisibleCount]=useState(24);
  const [query,setQuery]=useState('');
  const sentinel=useRef(null);
  const searchId = useId();

  const [category,setCategory]=useState(''),[unavailable,setUnavailable]=useState(false),[modal,setModal]=useState(null);
  const { index, branches, roots } = useMemo(() => buildCategoryIndex(data.categories || []), [data.categories]);
  const catalog = useMemo(() => [
    ...(data.jobItems || []).map(item => ({ ...item, jobItem: true })),
    ...(data.items || []),
  ].filter(item => unavailable || (item.allowed && item.canSelect)), [data.items, data.jobItems, unavailable]);
  const visibleCategories = useMemo(() => new Set(catalog.flatMap(item => branches.get(item.category) || [])), [catalog, branches]);
  const activeCategory = visibleCategories.has(category) ? category : '';
  const branch = branches.get(activeCategory) || [];
  const visibleRoots = roots.filter(root => visibleCategories.has(root.id));
  useEffect(() => setVisibleCount(24), [activeCategory, unavailable, data.slot, data.job, query]);
  useEffect(() => {
    if (category !== activeCategory) setCategory(activeCategory);
  }, [category, activeCategory]);
  const filtered = useMemo(() => {
    const matches = fuzzyFilter(catalog.filter(item => !activeCategory || branches.get(item.category)?.includes(activeCategory)), query,
      item => [item.name, item.customName, item.id].filter(Boolean).join(' '));
    return matches.sort((a, b) => Number(!!b.selected) - Number(!!a.selected));
  }, [catalog, activeCategory, branches, query]);
  useEffect(()=>{const observer=new IntersectionObserver(entries=>{if(entries.some(e=>e.isIntersecting))setVisibleCount(n=>n+24);});if(sentinel.current)observer.observe(sentinel.current);return()=>observer.disconnect();},[visibleCount,filtered.length]);
  const item=catalog.find(item=>item.id===modal?.id && !!item.jobItem===!!modal?.jobItem && item.group===modal?.group);
  return <div className="EquipmentBrowser">
    <Section title="Снаряжение"><Dropdown selected={data.job} options={(data.jobs||[]).map(job=>({value:job.id,displayText:job.name}))} onSelected={value=>act('job',{value})} />
      <div className="EquipmentBrowser__summary" aria-label="Бюджет снаряжения">
        <div><span>Счёт персонажа</span><strong>{data.balance}</strong></div>
        <div><span>Стоимость снаряжения</span><strong>{data.cost}</strong></div>
        <div><span>Очки снаряжения</span><strong>{data.points} / {data.maxPoints}</strong><progress max={Math.max(1,data.maxPoints||0)} value={data.points||0} /></div>
      </div>
      <BackpackEditor data={backpack} context={`${data.job}/${characterSlot ?? ''}`} active={active} act={act} showUnavailable={unavailable} />
      <Button.Checkbox role="checkbox" aria-checked={unavailable} checked={unavailable} onClick={() => setUnavailable(!unavailable)}>Показать недоступные</Button.Checkbox><Button onClick={()=>act('remove-unavailable')}>Убрать недоступные</Button>
      {data.slot&&<Button onClick={()=>act('all-slots')}>{data.slot} · Все предметы</Button>}
    </Section>
    {!!visibleRoots.length && <nav aria-label="Категории снаряжения">
      <Button selected={!activeCategory} onClick={() => setCategory('')}>Все категории</Button>
      {visibleRoots.map(root => <Button key={root.id} selected={branch.includes(root.id)} onClick={() => setCategory(root.id)}>{root.name}</Button>)}
    </nav>}
    {branch.map(parent => {
      const children = (index.get(parent).children || []).filter(child => visibleCategories.has(child.id));
      return !!children.length && <nav key={parent} aria-label={index.get(parent).name}>
        {children.map(child => <Button key={child.id} selected={branch.includes(child.id)} onClick={() => setCategory(child.id)}>{index.get(child.id).name}</Button>)}
      </nav>;
    })}
    <div className="EquipmentBrowser__search">
      <label htmlFor={searchId}>Поиск снаряжения</label>
      <div className="EquipmentBrowser__searchRow">
        <input id={searchId} type="search" placeholder="Название или ID предмета…" value={query} onChange={event=>setQuery(event.target.value)} />
        <Button role="button" disabled={!query} aria-disabled={!query} icon="times" onClick={()=>setQuery('')}>Очистить поиск</Button>
      </div>
    </div>
    {!filtered.length&&<p role="status">Ничего не найдено</p>}
    <div className="EquipmentBrowser__grid">{filtered.slice(0,visibleCount).map(item=><article key={(item.group||'personal')+item.id} title={[item.reason,item.groupName,item.donor].filter(Boolean).join(' · ')}>
      <button type="button" disabled={!item.canSelect} aria-pressed={item.selected} onClick={()=>act(item.jobItem?'job-select':'select',{id:item.id,group:item.group})}><Sprite item={item} color={item.color?.slice(0,7)||'#ffffff'} /><strong>{item.customName||item.name}</strong><small>{item.cost} {item.jobItem?'$':'очков'}</small>{item.donor&&<small>{item.donor}</small>}{!item.allowed&&<small>🔒 {item.reason}</small>}{item.jobItem&&<small>{item.groupName} · {item.min}–{item.max}</small>}</button>
      {<div className="EquipmentBrowser__tools">
        {item.canPaint&&<Button icon="palette" tooltip="Покрасить" onClick={()=>{setModal({id:item.id,group:item.group,jobItem:item.jobItem,mode:'paint'});}}>Покрасить</Button>}
        {(item.canRename||item.canDescribe)&&<Button icon="pen" tooltip="Название и описание" onClick={()=>{setModal({id:item.id,group:item.group,jobItem:item.jobItem,mode:'rename'});}}>Название / описание</Button>}
        {item.canHeirloom&&item.selected&&<Button icon="star" selected={item.heirloom} tooltip="Семейная реликвия" onClick={()=>act('heirloom',{id:item.id})} />}
      </div>}
    </article>)}</div>
    {visibleCount<filtered.length&&<div ref={sentinel}><Button fluid onClick={()=>setVisibleCount(n=>n+24)}>Показать ещё</Button></div>}
    {modal&&item&&<Customization key={modal.id+modal.mode} item={item} mode={modal.mode} act={act} close={()=>setModal(null)} />}
  </div>;
},(before,after)=>before.data===after.data && before.backpack===after.backpack
  && before.characterSlot===after.characterSlot && before.active===after.active);
