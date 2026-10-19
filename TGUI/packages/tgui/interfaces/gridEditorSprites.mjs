// Native sprite layer matrices use world units and row-vector Matrix3x2 layout.
// Keep one transform definition for chunk bounds, pixel picking and map drawing.
export function spriteLayers(item, visual) {
  const entity = item.kind === 'entity';
  const angle = (entity && visual?.noRotation ? 0 : item.kind === 'tile' ? visual?.allowRotationMirror ? (item.mirror & 3) * 90 : 0 : item.rotation || 0) * Math.PI / 180;
  const cos=Math.cos(angle),sin=Math.sin(angle),cx=item.x+(entity?0:.5),cy=item.y+(entity?0:.5);
  return (visual?.images?.length ? visual.images : [{}]).map(image=>{
    const a=image.native?image.m11:1,b=image.native?image.m12:0,c=image.native?image.m21:0,d=image.native?image.m22:1;
    const x=image.native?image.m31:0,y=image.native?image.m32:0;
    return {image,a:cos*a-sin*b,b:sin*a+cos*b,c:cos*c-sin*d,d:sin*c+cos*d,
      x:cx+cos*x-sin*y,y:cy+sin*x+cos*y,width:image.native?image.width/32:1,height:image.native?image.height/32:1};
  });
}
export function spriteBounds(item,visual) {
  let left=Infinity,right=-Infinity,bottom=Infinity,top=-Infinity;
  for(const layer of spriteLayers(item,visual))for(const u of [-.5,.5])for(const v of [-.5,.5]){
    const x=layer.x+layer.a*u*layer.width+layer.c*v*layer.height,y=layer.y+layer.b*u*layer.width+layer.d*v*layer.height;
    left=Math.min(left,x);right=Math.max(right,x);bottom=Math.min(bottom,y);top=Math.max(top,y);
  }
  return {left,right,bottom,top};
}
