// Cardinal ports match Content.Shared.Atmos.PipeDirection (N, S, W, E).
export const routeProfiles = [
  { kind:'wire', id: 'hv', name: 'Кабель высокого напряжения', cable: 'CableHV', color: '#ff963f' },
  { kind:'wire', id: 'mv', name: 'Кабель среднего напряжения', cable: 'CableMV', color: '#ffe35b' },
  { kind:'wire', id: 'lv', name: 'Кабель низкого напряжения', cable: 'CableApcExtension', color: '#73ea83' },
  ...['', 'Alt1', 'Alt2'].map((suffix, i) => ({ kind:'gas', id: `gas${i}`, name: `Газовые трубы · слой ${i + 1}`, suffix, color: ['#67d4ff','#c59aff','#fa9bd0'][i] })),
  {kind:'disposal',id:'disposal',name:'Трубы мусоросброса',disposal:true,color:'#e1b58b'},
];
const disposalShapes=[['DisposalPipe',3],['DisposalBend',6],['DisposalJunction',7],['DisposalJunctionFlipped',11],['DisposalYJunction',14]];
const shapes = [['Half', 2], ['Straight', 3], ['Bend', 6], ['TJunction', 14], ['Fourway', 15]];
export const routePrototypes = profile => profile.cable ? [profile.cable,profile.cable+'Uncuttable'] : profile.disposal ? disposalShapes.map(([id])=>id) : shapes.map(([shape]) => `GasPipe${shape}${profile.suffix}`);
const key = p => `${p.x}:${p.y}`;
const directions = [[0,1,1,2],[0,-1,2,1],[-1,0,4,8],[1,0,8,4]];
export const rotatePorts = (mask, turns) => {
  for (let i = 0; i < ((turns % 4) + 4) % 4; i++) mask = (mask & 1 ? 4 : 0) | (mask & 4 ? 2 : 0) | (mask & 2 ? 8 : 0) | (mask & 8 ? 1 : 0);
  return mask;
};
export function routeProfile(item) {
  return item.kind === 'entity' ? routeProfiles.find(profile => routePrototypes(profile).includes(item.prototype)) : undefined;
}
export function pipePorts(item, profile = routeProfile(item)) {
  if (!profile) return 0;
  if (profile.cable) return 15;
  if(profile.disposal){const shape=disposalShapes.find(([id])=>item.prototype===id);return shape?rotatePorts(shape[1],Math.round((item.rotation||0)/90)):0;}
  const shape = shapes.find(([shape]) => item.prototype === `GasPipe${shape}${profile.suffix}`);
  return shape ? rotatePorts(shape[1], Math.round((item.rotation || 0) / 90)) : 0;
}
export function routeShape(mask, profile, capped = false, exit = 0) {
  if (profile.cable) return { prototype: profile.cable, rotation: 0 };
  if(profile.disposal){
    if(mask===15)throw Error('В мусоросбросе нет крестовины. Разнесите пересечение по разным клеткам.');
    if([1,2,4,8].includes(mask))mask|=({1:2,2:1,4:8,8:4})[mask];
    const matches=[];for(const [prototype,ports]of disposalShapes)for(let turn=0;turn<4;turn++)if(rotatePorts(ports,turn)===mask)matches.push({prototype,rotation:turn*90,exit:rotatePorts(2,turn)});
    const chosen=matches.find(shape=>shape.exit===exit)||matches[0]||{prototype:'DisposalPipe',rotation:0};
    return {prototype:chosen.prototype,rotation:chosen.rotation};
  }
  if (!capped && [1,2,4,8].includes(mask)) mask |= mask === 1 ? 2 : mask === 2 ? 1 : mask === 4 ? 8 : 4;
  for (const [shape, ports] of shapes) for (let turn = 0; turn < 4; turn++)
    if (rotatePorts(ports, turn) === mask) return { prototype: `GasPipe${shape}${profile.suffix}`, rotation: turn * 90 };
  return { prototype: `GasPipeHalf${profile.suffix}`, rotation: 0 };
}
export function routeCells(graph) {
  const nodes = new Map(graph.nodes.map(node => [node.id,node])), cells = new Map();
  const add = p => { const id = key(p); if (!cells.has(id)) cells.set(id,{ x:p.x,y:p.y,mask:0 }); return cells.get(id); };
  const connect = (a,b) => {
    const bit = b.x > a.x ? 8 : b.x < a.x ? 4 : b.y > a.y ? 1 : 2;
    add(a).exit=bit;add(a).mask |= bit; add(b).mask |= ({1:2,2:1,4:8,8:4})[bit];
  };
  for(const node of graph.nodes)add(node);
  for (const edge of graph.edges) {
    const a = nodes.get(edge.a), b = nodes.get(edge.b); if (!a || !b) continue;
    const corner = edge.vertical ? {x:a.x,y:b.y} : {x:b.x,y:a.y};
    for (const [start,end] of [[a,corner],[corner,b]]) {
      let p = {x:start.x,y:start.y}; add(p);
      while (p.x !== end.x || p.y !== end.y) {
        const next = {x:p.x + Math.sign(end.x-p.x),y:p.y+Math.sign(end.y-p.y)}; connect(p,next);p=next;
      }
    }
  }
  return [...cells.values()];
}
// Existing networks expose a node on every occupied cell, retaining real port connectivity.
export function readRoute(items, seed, profile) {
  const byCell=new Map(items.filter(item=>routeProfile(item)?.id===profile.id).map(item=>[key({x:Math.floor(item.x),y:Math.floor(item.y)}),item]));
  if(!byCell.has(key(seed)))return null;
  const neighbors=p=>directions.filter(([dx,dy,bit,opposite])=>{
    const next=byCell.get(`${p.x+dx}:${p.y+dy}`);return next&&(pipePorts(byCell.get(key(p)),profile)&bit)&&(pipePorts(next,profile)&opposite);
  }).map(([dx,dy])=>({x:p.x+dx,y:p.y+dy}));
  const visited=new Map(),queue=[seed];
  for(let i=0;i<queue.length;i++){const p=queue[i];if(visited.has(key(p)))continue;visited.set(key(p),p);queue.push(...neighbors(p));}
  const nodes=[...visited.values()].map(p=>({...p,id:key(p)})),edges=[],walked=new Set();
  for(const node of nodes)for(const p of neighbors(node)){
    const id=[key(node),key(p)].sort().join('|');if(walked.has(id))continue;walked.add(id);
    edges.push({id,a:node.id,b:key(p),vertical:node.x===p.x});
  }
  return {nodes,edges,source:[...visited.keys()].map(k=>byCell.get(k))};
}
export function insertRoutePoint(graph,cell,id) {
  const existing=graph.nodes.find(n=>n.x===cell.x&&n.y===cell.y);if(existing)return {graph,id:existing.id};
  const edges=[];
  for(const edge of graph.edges){
    const cells=routeCells({nodes:graph.nodes,edges:[edge]});
    if(!cells.some(p=>p.x===cell.x&&p.y===cell.y)){edges.push(edge);continue;}
    edges.push({...edge,b:id},{...edge,id:edge.id+'_'+id,a:id});
  }
  return {id,graph:{...graph,nodes:[...graph.nodes,{...cell,id}],edges}};
}
export function moveRoutePoint(graph,id,cell) {
  const old=graph.nodes.find(n=>n.id===id);if(!old)return graph;
  const nodes=new Map(graph.nodes.map(n=>[n.id,n]));
  return {...graph,nodes:graph.nodes.map(n=>n.id===id?{...n,...cell}:n),edges:graph.edges.map(edge=>{
    if(edge.a!==id&&edge.b!==id)return edge;
    const other=nodes.get(edge.a===id?edge.b:edge.a);if(!other)return edge;
    // Turn away from the old point first: rerouting must not leave a redundant wire there.
    return {...edge,vertical:edge.a===id?old.x===other.x:old.x!==other.x};
  })};
}
export function deleteRoutePoint(graph,id) {
  const target=graph.nodes.find(node=>node.id===id);if(!target)return graph;
  const cells=routeCells(graph),oldNodes=new Map(graph.nodes.map(node=>[key(node),node]));
  const nodes=cells.filter(cell=>key(cell)!==key(target)).map(cell=>({...cell,id:oldNodes.get(key(cell))?.id||`cell:${key(cell)}`}));
  const byCell=new Map(nodes.map(node=>[key(node),node])),edges=[],seen=new Set();
  for(const node of nodes)for(const [dx,dy,bit,opposite]of directions){
    const next=byCell.get(`${node.x+dx}:${node.y+dy}`);if(!next||!(node.mask&bit)||!(next.mask&opposite))continue;
    const edge=[node.id,next.id].sort().join('|');if(seen.has(edge))continue;seen.add(edge);
    edges.push({id:edge,a:node.id,b:next.id,vertical:node.x===next.x});
  }
  return {...graph,nodes,edges,active:null};
}
export function buildRoute(items, graph, profile, source, nextId, capped = false) {
  const cells=routeCells(graph), originals=new Set(source.map(item=>item.id));
  if(!cells.length)throw Error('Добавьте хотя бы две точки трассы.');
  const floors=new Set(items.filter(item=>item.kind==='tile').map(item=>key(item)));
  const existing=new Map();
  for(const item of items) if(routeProfile(item)?.id===profile.id) {
    const k=key({x:Math.floor(item.x),y:Math.floor(item.y)});if(!existing.has(k))existing.set(k,[]);existing.get(k).push(item);
  }
  const sourceByCell=new Map(source.map(item=>[key({x:Math.floor(item.x),y:Math.floor(item.y)}),item]));
  const result=items.filter(item=>!originals.has(item.id)), generated=[];
  for(const cell of cells) {
    if(!floors.has(key(cell)))throw Error(`Под трассой нет пола: ${cell.x}, ${cell.y}.`);
    const others=(existing.get(key(cell))||[]).filter(item=>!originals.has(item.id));
    let mask=cell.mask;
    const original=sourceByCell.get(key(cell));
    if(original&&!profile.cable)for(const [dx,dy,bit] of directions)
      if(!sourceByCell.has(`${cell.x+dx}:${cell.y+dy}`))mask|=pipePorts(original,profile)&bit;
    for(const other of others) mask |= pipePorts(other,profile);
    // Attach only to ports that actually face the route; do not join a parallel pipe layer.
    if(!profile.cable) for(const [dx,dy,bit,opposite] of directions) {
      const near=existing.get(`${cell.x+dx}:${cell.y+dy}`)||[];
      if(near.some(item=>!originals.has(item.id)&&(pipePorts(item,profile)&opposite)))mask|=bit;
    }
    let shape=mask===0&&original?{prototype:original.prototype,rotation:original.rotation}:routeShape(mask,profile,capped,cell.exit);
    if(profile.cable){const variant=source.length&&source.every(item=>item.prototype===source[0].prototype)?source[0].prototype:profile.cable;shape={prototype:original?.prototype||variant,rotation:0};}
    if(original&&profile.disposal&&pipePorts(original,profile)===mask)shape={prototype:original.prototype,rotation:original.rotation};
    const same=[...(original?[original]:[]),...others].find(item=>Math.floor(item.x)===cell.x&&Math.floor(item.y)===cell.y&&item.prototype===shape.prototype);
    for(const other of others) { const index=result.findIndex(item=>item.id===other.id);if(index>=0)result.splice(index,1); }
    const item={...(same||{}),id:same?.id||nextId(),kind:'entity',...shape,x:cell.x+.5,y:cell.y+.5,anchored:true,color:same?.color||'#FFFFFFFF',z:0,cleanable:false,variant:0,flags:0,mirror:0};
    result.push(item);generated.push(item.id);
  }
  return {items:result,ids:generated};
}
