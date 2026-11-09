export function* spriteUrls(visual) {
  for (const image of visual?.images || []) {
    for (const frames of [image, ...(image.pipeFrames || [])]) {
      yield frames.url;
      for (const frame of frames.frames || []) yield frame.url;
    }
  }
}

// Native sprite layer matrices use world units and row-vector Matrix3x2 layout.
// Keep one transform definition for chunk bounds, pixel picking and map drawing.
export function spriteLayers(item, visual) {
  const entity = item.kind === 'entity';
  const degrees = item.kind === 'tile' ? visual?.allowRotationMirror ? (item.mirror & 3) * 90 : 0 : item.rotation || 0;
  const facing = (degrees % 360 + 360) % 360;
  const spriteMode = entity && visual?.noRotation ? 'NoRotation' : entity && visual?.snapCardinals ? 'SnapToCardinals' : 'Default';
  const cx=item.x+(entity?0:.5),cy=item.y+(entity?0:.5);
  return (visual?.images?.length ? visual.images : [{}]).map(base=>{
    const variant = base.pipeFrames?.find(frame => frame.layer === item.pipeLayer);
    const frames = variant || base;
    const count = (frames.frames?.length || 0) + 1, step = 360 / count;
    const bias = count === 4 ? ((Math.floor(facing / 90) % 2) - .5) * (visual.directionBias || 0) : 0;
    const direction = count === 1 ? 0 : Math.round((facing + bias) / step) % count;
    const url = direction === 0 ? frames.url : frames.frames[direction - 1].url;
    const image = variant || direction ? { ...base, width: frames.width, height: frames.height, url: url || frames.url } : base;
    const mode = image.rotationMode || spriteMode;
    const rotation = mode === 'NoRotation' ? 0 : mode === 'SnapToCardinals' ? degrees - Math.round(degrees / 90) * 90 : degrees;
    const angle = rotation * Math.PI / 180;
    const cos = Math.cos(angle), sin = Math.sin(angle);
    // The game compensates the selected RSI direction before applying the local layer matrix.
    const correction = visual?.noRotation ? 0 : -direction * step * Math.PI / 180;
    const dc = Math.cos(correction), ds = Math.sin(correction);
    const m11=image.native?image.m11:1,m12=image.native?image.m12:0,m21=image.native?image.m21:0,m22=image.native?image.m22:1;
    const a=dc*m11+ds*m21,b=dc*m12+ds*m22,c=dc*m21-ds*m11,d=dc*m22-ds*m12;
    const x=image.native?image.m31:0,y=image.native?image.m32:0;
    const width=image.native || item.kind==='decal' ? (image.width || 32)/32 : 1;
    const height=image.native || item.kind==='decal' ? (image.height || 32)/32 : 1;
    const centerX=item.kind==='decal'?item.x+width/2:cx,centerY=item.kind==='decal'?item.y+height/2:cy;
    return {image,a:cos*a-sin*b,b:sin*a+cos*b,c:cos*c-sin*d,d:sin*c+cos*d,
      x:centerX+cos*x-sin*y,y:centerY+sin*x+cos*y,width,height};
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
