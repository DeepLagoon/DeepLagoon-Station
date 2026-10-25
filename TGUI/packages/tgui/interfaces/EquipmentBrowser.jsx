import { LazySprite } from '../components/LazySprite';
import { memo, useEffect, useMemo, useRef, useState } from 'react';
import { Button, Dropdown, Section } from '../components';
import { PlayerColorPicker } from '../components/PlayerColorPicker';
import { TintedSprite } from '../components/PlayerTheme';
import './EquipmentBrowser.scss';
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
export const EquipmentBrowser = memo(({data={},act}) => {
  const [visibleCount,setVisibleCount]=useState(24);
  const sentinel=useRef(null);

  const [category,setCategory]=useState(''),[unavailable,setUnavailable]=useState(false),[modal,setModal]=useState(null);
  const categories=data.categories||[];
  const descendants=id=>{const seen=new Set();const visit=key=>{if(seen.has(key))return;seen.add(key);(categories.find(c=>c.id===key)?.children||[]).forEach(c=>visit(c.id));};visit(id);return seen;};
  const accepted=category?descendants(category):null;
  const catalog=useMemo(()=>[...(data.jobItems||[]).map(item=>({...item,jobItem:true})),...(data.items||[])].filter(item=>unavailable||item.allowed||item.selected||item.donor),[data,unavailable]);
  const hasItems=id=>{const ids=descendants(id);return catalog.some(item=>ids.has(item.category));};
  const parent=categories.find(c=>c.root&&descendants(c.id).has(category));
  useEffect(()=>setVisibleCount(24),[category,unavailable,data.slot,data.job]);
  useEffect(()=>{if(category&&!hasItems(category))setCategory('');},[data,unavailable,category]);
  const filtered=useMemo(()=>catalog.filter(item=>!accepted||accepted.has(item.category)),[catalog,category,data]);
  useEffect(()=>{const observer=new IntersectionObserver(entries=>{if(entries.some(e=>e.isIntersecting))setVisibleCount(n=>n+24);});if(sentinel.current)observer.observe(sentinel.current);return()=>observer.disconnect();},[visibleCount,filtered.length]);
  const item=catalog.find(item=>item.id===modal?.id && !!item.jobItem===!!modal?.jobItem && item.group===modal?.group);
  return <div className="EquipmentBrowser">
    <Section title="Снаряжение"><Dropdown selected={data.job} options={(data.jobs||[]).map(job=>({value:job.id,displayText:job.name}))} onSelected={value=>act('job',{value})} />
      <div className="EquipmentBrowser__summary" aria-label="Бюджет снаряжения">
        <div><span>Счёт персонажа</span><strong>{data.balance}</strong></div>
        <div><span>Стоимость снаряжения</span><strong>{data.cost}</strong></div>
        <div><span>Очки снаряжения</span><strong>{data.points} / {data.maxPoints}</strong><progress max={Math.max(1,data.maxPoints||0)} value={data.points||0} /></div>
      </div>
      <Button selected={unavailable} onClick={()=>setUnavailable(!unavailable)}>Показать недоступные</Button><Button onClick={()=>act('remove-unavailable')}>Убрать недоступные</Button>
      {data.slot&&<Button onClick={()=>act('all-slots')}>{data.slot} · Все предметы</Button>}
    </Section>
    <nav aria-label="Категории снаряжения"><Button selected={!category} onClick={()=>setCategory('')}>Все категории</Button>{categories.filter(c=>c.root&&hasItems(c.id)).map(c=><Button key={c.id} selected={parent?.id===c.id} onClick={()=>setCategory(c.id)}>{c.name}</Button>)}</nav>
    {parent&&<nav aria-label="Подкатегории снаряжения">{(parent.children||[]).filter(child=>hasItems(child.id)).map(child=><Button key={child.id} selected={category===child.id} onClick={()=>setCategory(child.id)}>{categories.find(c=>c.id===child.id)?.name||child.id}</Button>)}</nav>}
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
},(before,after)=>before.data===after.data);
