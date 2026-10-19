import { memo, useEffect, useMemo, useRef, useState } from 'react';
import { buildRoute, deleteRoutePoint, insertRoutePoint, moveRoutePoint, pipePorts, readRoute, routeCells, routeProfile, routeProfiles, routePrototypes } from './gridEditorRouting.mjs';

const rotateAngle=rotation=>-rotation*Math.PI/180;
const empty = () => ({nodes:[],edges:[],source:[],active:null});
export function useGridEditorRoutes({token, act, items, area, nextId, commit, fail, locked, toCell, canvas}) {
  const [graph,setGraph]=useState(empty), [profileId,setProfileId]=useState('hv');
  const [visible,setVisible]=useState(true), [capped,setCapped]=useState(false), [vertical,setVertical]=useState(false);
  const [history,setHistory]=useState([]), [future,setFuture]=useState([]);
  const drag=useRef(null), serial=useRef(0);
  const profile=routeProfiles.find(p=>p.id===profileId);
  useEffect(()=>{setGraph(empty());setHistory([]);setFuture([]);},[token]);
  useEffect(()=>{act('routeVisuals',{prototypes:routePrototypes(profile)});},[profileId,token]);
  const save=()=>{setHistory(h=>[...h.slice(-49),graph]);setFuture([]);};
  const reset=()=>{setGraph(empty());setHistory([]);setFuture([]);drag.current=null;};
  const edit=cell=>{
    if(locked)return;
    if(!graph.nodes.length) {
      const candidate=items.find(item=>Math.floor(item.x)===cell.x&&Math.floor(item.y)===cell.y&&routeProfile(item)?.id===profile.id)||items.find(item=>Math.floor(item.x)===cell.x&&Math.floor(item.y)===cell.y&&routeProfile(item));
      const picked=candidate?routeProfile(candidate):profile;
      const existing=readRoute(items,cell,picked);
      if(existing)setProfileId(picked.id);
      if(existing){save();setGraph({...existing,active:existing.nodes[0]?.id});return;}
    }
    save();const id=`r${serial.current++}`;
    setGraph(g=>({...g,nodes:[...g.nodes,{...cell,id}],edges:g.active?[...g.edges,{id:`e${serial.current++}`,a:g.active,b:id,vertical}]:g.edges,active:id}));
  };
  const startDrag=(event,point)=>{
    if(locked||event.button!==0)return;
    event.stopPropagation();event.preventDefault();canvas.current.focus();
    const inserted=insertRoutePoint(graph,point,`r${serial.current++}`),id=inserted.id;
    if(event.shiftKey){removePoint(id,inserted.graph);return;}
    save();drag.current={id,before:graph,base:inserted.graph};setGraph({...inserted.graph,active:id});event.currentTarget.setPointerCapture(event.pointerId);
  };
  const moveDrag=event=>{
    if(!drag.current)return;
    event.stopPropagation();const p=toCell(event);
    p.x=Math.max(area.x,Math.min(area.x+area.width-1,p.x));p.y=Math.max(area.y,Math.min(area.y+area.height-1,p.y));
    const active=drag.current;setGraph({...moveRoutePoint(active.base,active.id,p),active:active.id});
  };
  const endDrag=(event,cancel=false)=>{
    if(!drag.current)return;
    event.stopPropagation();if(cancel)setGraph(drag.current.before);drag.current=null;
    if(event.currentTarget.hasPointerCapture(event.pointerId))event.currentTarget.releasePointerCapture(event.pointerId);
  };
  const removePoint=(id=graph.active,base=graph)=>{
    if(!id||locked)return;
    save();setGraph(deleteRoutePoint(base,id));
  };
  const split=(event,edge)=>{
    if(locked||event.button!==0)return;
    event.preventDefault();event.stopPropagation();save();const p=toCell(event),id=`r${serial.current++}`;
    const inserted=insertRoutePoint(graph,p,id);drag.current={id:inserted.id,before:graph,base:inserted.graph};
    setGraph({...inserted.graph,active:inserted.id});
    event.currentTarget.setPointerCapture(event.pointerId);
  };
  const undo=forward=>{const from=forward?future:history;if(!from.length)return;
    const previous=from[from.length-1];if(forward){setFuture(from.slice(0,-1));setHistory(h=>[...h,graph]);}
    else{setHistory(from.slice(0,-1));setFuture(f=>[...f,graph]);}setGraph(previous);
  };
  const finish=()=>{
    if(locked)return;
    try{const result=buildRoute(items,graph,profile,graph.source,nextId,capped);commit(result);reset();}
    catch(error){fail(error.message);}
  };
  const cells=useMemo(()=>routeCells(graph),[graph]);
  const controls=<>
    <label><input type="checkbox" checked={visible} onChange={e=>setVisible(e.target.checked)}/>Показать сети поверх пола</label>
    <select aria-label="Режим трассы" value={profile.kind} disabled={locked||!!graph.nodes.length} onChange={e=>setProfileId(routeProfiles.find(p=>p.kind===e.target.value).id)}>
      <option value="wire">Провода</option><option value="gas">Газовые трубы</option><option value="disposal">Мусоросброс</option>
    </select>
    <select title={profile.name} aria-label="Тип сети" value={profileId} disabled={locked||!!graph.nodes.length} onChange={e=>setProfileId(e.target.value)}>
      {routeProfiles.filter(p=>p.kind===profile.kind).map(p=><option key={p.id} value={p.id}>{p.name}</option>)}
    </select>
    <small>В инструменте «Трасса»: клик по существующей сети открывает её точки. Клик по пустой клетке продолжает трассу от выбранной точки.</small>
    <small>Тяните круги и участки линии. Shift+клик по точке удаляет её. Выберите узел, затем кликните в стороне, чтобы создать ответвление.</small>
    <label><input type="checkbox" checked={vertical} onChange={e=>setVertical(e.target.checked)}/>Новые участки сначала по вертикали</label>
    {profile.kind==='gas'&&<label><input type="checkbox" checked={capped} onChange={e=>setCapped(e.target.checked)}/>Закрытые концы труб</label>}
    {!!graph.nodes.length&&<>
      <span>Точек: {cells.length} · клеток: {cells.length}</span>
      <button disabled={locked} onClick={finish}>Трассу в черновик · Enter</button>
      <button disabled={locked} onClick={()=>removePoint()}>Удалить точку</button>
      <button onClick={reset}>Отменить трассу · Esc</button>
      <small>После завершения нажмите «Применить». Перенос существующей сети убирает её прежние секции; другие слои сохраняются.</small>
    </>}
  </>;
  return {graph,profile,cells,visible,controls,edit,startDrag,moveDrag,endDrag,split,finish,reset,removePoint,undo,
    capped,previewItems:makeId=>buildRoute(items,graph,profile,graph.source,makeId,capped),canUndo:!!history.length,canRedo:!!future.length,editing:!!graph.nodes.length,dragging:()=>!!drag.current};
}

export const GridEditorNetworks=memo(({items,area,view,cellSize})=>{
  const canvas=useRef(null);
  const networks=useMemo(()=>{
    const entries=items.map(item=>({item,profile:routeProfile(item)})).filter(e=>e.profile);
    const keys=new Set(entries.map(({item,profile})=>`${profile.id}:${Math.floor(item.x)}:${Math.floor(item.y)}`));
    return entries.map(({item,profile})=>{
      let mask=pipePorts(item,profile);
      if(profile.cable) {mask=0;for(const [dx,dy,bit] of [[0,1,1],[0,-1,2],[-1,0,4],[1,0,8]])
        if(keys.has(`${profile.id}:${Math.floor(item.x)+dx}:${Math.floor(item.y)+dy}`))mask|=bit;}
      return {item,profile,mask};
    });
  },[items]);
  useEffect(()=>{
    const ctx=canvas.current.getContext('2d');ctx.clearRect(0,0,view.width,view.height);
    for(const {item,profile,mask} of networks){
      const x=(item.x-area.x)*cellSize-view.left,y=(area.y+area.height-item.y)*cellSize-view.top;
      if(x< -cellSize||y< -cellSize||x>view.width+cellSize||y>view.height+cellSize)continue;
      const offset=(profile.id==='mv'?-1:profile.id==='lv'?1:0)*Math.min(4,cellSize/8);
      ctx.strokeStyle=profile.color;ctx.lineWidth=Math.max(1,Math.min(4,cellSize/7));ctx.beginPath();
      for(const [dx,dy,bit] of [[0,-1,1],[0,1,2],[-1,0,4],[1,0,8]])if(mask&bit){ctx.moveTo(x+offset,y+offset);ctx.lineTo(x+offset+dx*cellSize/2,y+offset+dy*cellSize/2);}
      ctx.stroke();ctx.fillStyle=profile.color;ctx.fillRect(x+offset-2,y+offset-2,4,4);
      if(profile.disposal&&item.prototype.includes('Junction')){
        const direction=rotateAngle(item.rotation||0);ctx.save();ctx.translate(x,y);ctx.rotate(direction);
        ctx.fillStyle='#fff4c6';ctx.beginPath();ctx.moveTo(0,5);ctx.lineTo(-3,-2);ctx.lineTo(3,-2);ctx.closePath();ctx.fill();ctx.restore();
      }
    }
  },[networks,area,view,cellSize]);
  return <canvas ref={canvas} className="GridEditor__scene GridEditor__networks" width={Math.max(1,view.width)} height={Math.max(1,view.height)} style={{left:view.left,top:view.top,width:view.width,height:view.height}} aria-hidden="true"/>;
});

export function GridEditorRouteHandles({route,area,view,cellSize,locked}) {
  const canvas=useRef(null);
  const position=p=>({x:(p.x+.5-area.x)*cellSize-view.left,y:(area.y+area.height-p.y-.5)*cellSize-view.top});
  const nodes=useMemo(()=>new Map(route.graph.nodes.map(node=>[`${node.x}:${node.y}`,node])),[route.graph.nodes]);
  const cells=useMemo(()=>new Map(route.cells.map(cell=>[`${cell.x}:${cell.y}`,cell])),[route.cells]);
  useEffect(()=>{
    const ctx=canvas.current.getContext('2d');ctx.clearRect(0,0,view.width,view.height);
    ctx.strokeStyle=route.profile.color;ctx.fillStyle=route.profile.color;ctx.lineWidth=Math.max(1,Math.min(5,cellSize/6));
    for(const cell of route.cells){
      const p=position(cell);if(p.x< -cellSize||p.y< -cellSize||p.x>view.width+cellSize||p.y>view.height+cellSize)continue;
      ctx.beginPath();for(const [dx,dy,bit]of [[0,-1,1],[0,1,2],[-1,0,4],[1,0,8]])if(cell.mask&bit){ctx.moveTo(p.x,p.y);ctx.lineTo(p.x+dx*cellSize/2,p.y+dy*cellSize/2);}ctx.stroke();
      ctx.beginPath();ctx.arc(p.x,p.y,Math.max(1,Math.min(4,cellSize/6)),0,Math.PI*2);ctx.fill();
    }
  },[route.cells,route.profile.color,area,view,cellSize]);
  const hit=event=>{
    const rect=event.currentTarget.getBoundingClientRect();
    const cell={x:Math.floor(area.x+(view.left+event.clientX-rect.left)/cellSize),y:Math.floor(area.y+area.height-(view.top+event.clientY-rect.top)/cellSize)};
    if(cells.has(`${cell.x}:${cell.y}`))route.startDrag(event,cell);
  };
  return <div className="GridEditor__routeHandles" style={{left:view.left,top:view.top,width:view.width,height:view.height}}>
    <canvas ref={canvas} width={view.width} height={view.height} className="GridEditor__routeCanvas" onPointerDown={hit}
      onPointerMove={route.moveDrag} onPointerUp={e=>route.endDrag(e)} onPointerCancel={e=>route.endDrag(e,true)}/>
    {cellSize>=10&&route.cells.map((cell,i)=>{
      const node=nodes.get(`${cell.x}:${cell.y}`)||{...cell,id:`cell:${cell.x}:${cell.y}`},p=position(node);
      if(p.x< -10||p.y< -10||p.x>view.width+10||p.y>view.height+10)return null;
      const size=Math.min(16,cellSize*.6);
      return <button key={node.id} aria-label={`Точка трассы ${i+1}`} disabled={locked} className={route.graph.active===node.id?'active':''}
        style={{left:p.x,top:p.y,width:size,height:size}} onPointerDown={e=>route.startDrag(e,node)}
        onPointerMove={route.moveDrag} onPointerUp={e=>route.endDrag(e)} onPointerCancel={e=>route.endDrag(e,true)} title={`${node.x}, ${node.y}`}/>;
    })}
  </div>;
}
