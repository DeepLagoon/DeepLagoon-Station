import test from 'node:test';
import assert from 'node:assert/strict';
import { routeProfiles, routeCells, routeShape, pipePorts, readRoute, buildRoute, rotatePorts, moveRoutePoint, deleteRoutePoint } from '../packages/tgui/interfaces/gridEditorRouting.mjs';
import { spriteBounds } from '../packages/tgui/interfaces/gridEditorSprites.mjs';
import { itemChunks } from '../packages/tgui/interfaces/gridEditorSceneCache.mjs';
import { indexItems, nearbyItems, itemsAtPoint } from '../packages/tgui/interfaces/gridEditorModel.mjs';
const hv=routeProfiles[0],gas=routeProfiles[3];
const floor=(x,y)=>({id:`t:${x}:${y}`,kind:'tile',prototype:'Plating',x,y});
const cable=(x,y,id=`c:${x}:${y}`)=>({id,kind:'entity',prototype:'CableHV',x:x+.5,y:y+.5,rotation:0,anchored:true});
const graph=(nodes,edges)=>({nodes:nodes.map(([x,y],i)=>({id:String(i),x,y})),edges:edges.map(([a,b,vertical=false],i)=>({id:`e${i}`,a:String(a),b:String(b),vertical}))});
let serial=0;const nextId=()=>`new_${serial++}`;
const floors=[];for(let x=-5;x<8;x++)for(let y=-5;y<8;y++)floors.push(floor(x,y));

test('orthogonal route bends, branches and crossings have cardinal ports without duplicates',()=>{
  const cells=routeCells(graph([[0,0],[3,2],[1,0],[1,3]],[[0,1],[2,3]]));
  const byCell=new Map(cells.map(c=>[`${c.x}:${c.y}`,c]));
  assert.equal(byCell.get('3:0').mask,5); // north + west
  assert.equal(byCell.get('1:0').mask,13); // north + east + west
  assert.equal(byCell.get('1:2').mask,3);
  assert.equal(cells.length,9);
});
test('every gas port shape and rotation agrees with the native cardinal convention',()=>{
  for(let mask=1;mask<=15;mask++){
    const shape=routeShape(mask,gas,true);
    assert.equal(pipePorts({kind:'entity',...shape},gas),mask,`mask ${mask}`);
  }
  assert.equal(rotatePorts(2,1),8); // south rotated +90 faces east
  assert.equal(routeShape(12,gas).rotation,90);
});
test('existing wire networks compress into editable ends, corners and junctions',()=>{
  const source=[cable(0,0),cable(1,0),cable(2,0),cable(1,1),cable(1,2)];
  const route=readRoute([...floors,...source],{x:0,y:0},hv);
  assert.equal(route.source.length,5);assert.equal(route.nodes.length,5);assert.equal(route.edges.length,4);
  const restored=routeCells(route).map(c=>`${c.x}:${c.y}`).sort();
  assert.deepEqual(restored,['0:0','1:0','1:1','1:2','2:0']);
});
test('moving existing control points removes previous cable cells and preserves floors and other voltage',()=>{
  const source=[cable(0,0),cable(1,0),cable(2,0)],other={...cable(0,0,'mv'),prototype:'CableMV'};
  const route=readRoute([...floors,...source,other],{x:0,y:0},hv);
  route.nodes=route.nodes.map(n=>({...n,y:1}));
  const result=buildRoute([...floors,...source,other],route,hv,route.source,nextId);
  assert.equal(result.items.filter(i=>i.prototype==='CableHV'&&i.y===.5).length,0);
  assert.equal(result.items.filter(i=>i.prototype==='CableHV'&&i.y===1.5).length,3);
  assert.strictEqual(result.items.find(i=>i.id==='mv'),other);
  assert.equal(result.items.filter(i=>i.kind==='tile').length,floors.length);
});
test('rerouting unchanged cable cells retains identities and rejects missing support atomically',()=>{
  const source=[cable(0,0),cable(1,0),cable(2,0)],route=readRoute(source,{x:0,y:0},hv);
  const result=buildRoute([...floors,...source],route,hv,source,nextId);
  assert.deepEqual(result.ids.sort(),source.map(i=>i.id).sort());
  assert.throws(()=>buildRoute(source,route,hv,source,nextId),/нет пола/);
  assert.equal(source.length,3);
});
test('gas routes choose elbows, connect at crossings, and keep other layers separate',()=>{
  const route=graph([[0,0],[2,2]],[[0,1]]);
  const existing={id:'pipe',kind:'entity',...routeShape(3,gas),x:1.5,y:.5};
  const other={...existing,id:'alt',prototype:'GasPipeStraightAlt1'};
  const result=buildRoute([...floors,existing,other],route,gas,[],nextId,true);
  const at=(x,y)=>result.items.find(i=>i.kind==='entity'&&i.x===x+.5&&i.y===y+.5&&i.id!=='alt');
  assert.equal(at(2,0).prototype,'GasPipeBend');assert.equal(pipePorts(at(2,0),gas),5);
  assert.equal(at(1,0).prototype,'GasPipeFourway');
  assert.strictEqual(result.items.find(i=>i.id==='alt'),other);
  assert.equal(result.items.some(i=>i.id==='pipe'),false);
});
test('pipe extraction respects port directions, separate layers, loops, and isolated cables',()=>{
  const pipe={id:'p',kind:'entity',...routeShape(3,gas),x:.5,y:.5};
  const sideways={...pipe,id:'side',x:1.5};
  assert.equal(readRoute([pipe,sideways],{x:0,y:0},gas).source.length,1);
  const isolated=readRoute([cable(0,0)],{x:0,y:0},hv);
  assert.equal(buildRoute([...floors,cable(0,0)],isolated,hv,isolated.source,nextId).ids.length,1);
  const loop=[cable(0,0),cable(1,0),cable(1,1),cable(0,1)];
  assert.equal(readRoute(loop,{x:0,y:0},hv).edges.length,4);
});
const image={native:true,width:32,height:64,m11:1,m12:0,m21:0,m22:1,m31:0,m32:1,url:'lamp'};
test('wall lamp native origin and size are preserved in drawing bounds, chunks and pixel picking',()=>{
  const lamp={id:'lamp',kind:'entity',prototype:'Light',x:15.5,y:15.5,rotation:0};
  const visual={images:[image]};const bounds=spriteBounds(lamp,visual);
  assert.deepEqual(bounds,{left:15,right:16,bottom:15.5,top:17.5});
  assert.equal(itemChunks(lamp,visual).length,2);
  assert.deepEqual(itemsAtPoint([lamp],{x:15.5,y:17},{entity:true},()=>visual).map(i=>i.id),['lamp']);
  assert.deepEqual(itemsAtPoint([lamp],{x:15.5,y:15},{entity:true},()=>visual),[]);
  const rotated=spriteBounds({...lamp,rotation:90},visual);
  assert.ok(Math.abs(rotated.left-13.5)<1e-8);assert.ok(Math.abs(rotated.right-15.5)<1e-8);
});
test('native scale and layer matrices affect picking while noRotation keeps the screen orientation',()=>{
  const item={id:'big',kind:'entity',prototype:'Big',x:.5,y:.5,rotation:90};
  const visual={noRotation:true,images:[{...image,width:192,height:32,m31:3,m32:0,m11:2}]};
  const index=indexItems([item],()=>visual);
  assert.ok(nearbyItems(index,{x:8,y:.5}).includes(item));
  assert.equal(itemsAtPoint([item],{x:8,y:.5},{entity:true},()=>visual).length,1);
});


test('moving a per-cell wire point removes the old cell instead of leaving a second cable',()=>{
  const source=[cable(0,0),cable(1,0),cable(2,0)];const route=readRoute(source,{x:0,y:0},hv);
  assert.equal(route.nodes.length,3);
  const moved=moveRoutePoint(route,'1:0',{x:1,y:1});
  const result=buildRoute([...floors,...source],moved,hv,source,nextId);
  assert.equal(result.items.some(i=>i.prototype==='CableHV'&&i.x===1.5&&i.y===.5),false);
  assert.ok(result.items.some(i=>i.prototype==='CableHV'&&i.x===1.5&&i.y===1.5));
});
test('disposal routing uses native bends and directional junctions and rejects an impossible cross',()=>{
  const disposal=routeProfiles.find(p=>p.disposal);
  for(const mask of [3,5,6,9,10,12,7,11,13,14]){
    const shape=routeShape(mask,disposal);assert.equal(pipePorts({kind:'entity',...shape},disposal),mask);
  }
  assert.throws(()=>routeShape(15,disposal),/нет крестовины/);
  const result=buildRoute(floors,graph([[0,0],[2,2]],[[0,1]]),disposal,[],nextId);
  assert.ok(result.items.some(i=>i.prototype==='DisposalBend'));
  assert.equal(readRoute(result.items,{x:0,y:0},disposal).source.length,5);
});

test('moving an uncuttable cable network preserves its variant in new cells',()=>{
  const source=[{...cable(0,0),prototype:'CableHVUncuttable'},{...cable(1,0),prototype:'CableHVUncuttable'}];
  const route=readRoute(source,{x:0,y:0},hv);route.nodes=route.nodes.map(n=>({...n,y:1}));
  const result=buildRoute([...floors,...source],route,hv,source,nextId);
  assert.equal(result.items.filter(i=>i.kind==='entity').every(i=>i.prototype==='CableHVUncuttable'),true);
});

test('deleting a per-cell route point removes that cell without erasing neighboring segments',()=>{
  const route=graph([[0,0],[4,0]],[[0,1]]);route.nodes.push({id:'middle',x:2,y:0});
  const result=deleteRoutePoint(route,'middle');
  assert.deepEqual(routeCells(result).map(c=>`${c.x}:${c.y}`).sort(),['0:0','1:0','3:0','4:0']);
});
