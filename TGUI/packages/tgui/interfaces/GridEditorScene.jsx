import { spriteLayers, spriteBounds } from './gridEditorSprites.mjs';
import { memo, useEffect, useRef } from 'react';
import { GridSceneCache } from './gridEditorSceneCache.mjs';

// Only the viewport is uploaded to CEF. Pan/zoom composite prepared chunks;
// source sprites are rasterized only on load or when their chunk changes.
export const GridEditorScene = memo(({ index, area, layers, visuals, view, cellSize, onProgress }) => {
  const canvas = useRef(null), cache = useRef(null), frame = useRef(null);
  const ready = useRef(false);
  const camera = useRef(null); camera.current = { area, view, cellSize };
  const schedule = () => {
    if (frame.current !== null) return;
    frame.current = requestAnimationFrame(() => {
      frame.current = null;
      const { area, view, cellSize } = camera.current;
      if (canvas.current) cache.current?.paint(canvas.current, area, view, cellSize);
    });
  };
  useEffect(() => {
    cache.current = new GridSceneCache(schedule);
    return () => {
      cache.current.dispose(); cache.current = null;
      if (frame.current !== null) cancelAnimationFrame(frame.current);
      frame.current = null;
    };
  }, []);
  useEffect(() => {
    let cancelled = false;
    cache.current.prepare(index, layers, visuals, value => {
      if (!value) ready.current = true;
      onProgress(value && { ...value, blocking: !ready.current });
    }, () => cancelled)
      .catch(error => { if (!cancelled) onProgress({ error: error.message, blocking: true }); });
    return () => { cancelled = true; };
  }, [index, layers, visuals, onProgress]);
  useEffect(schedule, [area, view, cellSize]);
  return <canvas ref={canvas} className="GridEditor__scene" width={Math.max(1, view.width)} height={Math.max(1, view.height)}
    style={{ left: view.left, top: view.top, width: view.width, height: view.height }} aria-hidden="true" />;
});

// Tint sprite alpha, including native offsets/scales; never substitute a cell rectangle.
const highlightImages=new Map();
const highlightImage=url=>{
  if(!highlightImages.has(url))highlightImages.set(url,new Promise(resolve=>{
    const image=new Image();image.onload=()=>{
      const tinted=document.createElement('canvas');tinted.width=image.width;tinted.height=image.height;
      const ctx=tinted.getContext('2d');ctx.drawImage(image,0,0);ctx.globalCompositeOperation='source-in';
      ctx.fillStyle='#53ff82';ctx.fillRect(0,0,tinted.width,tinted.height);resolve(tinted);
    };image.onerror=()=>resolve(null);image.src=url;
  }));
  return highlightImages.get(url);
};
export const GridEditorSelection = memo(({ items, area, layers, view, cellSize, offset, visuals }) => {
  const canvas = useRef(null);
  useEffect(() => {
    let cancelled=false;
    const draw=async()=>{
      const visible=items.filter(item=>{
        if(!layers[item.kind])return false;
        const b=spriteBounds(item,visuals?.get(`${item.kind}:${item.prototype}`));
        return (b.right+(offset?.dx||0)-area.x)*cellSize>=view.left&&
          (b.left+(offset?.dx||0)-area.x)*cellSize<=view.left+view.width&&
          (area.y+area.height-b.bottom-(offset?.dy||0))*cellSize>=view.top&&
          (area.y+area.height-b.top-(offset?.dy||0))*cellSize<=view.top+view.height;
      });
      const urls=new Set(visible.flatMap(item=>(visuals?.get(`${item.kind}:${item.prototype}`)?.images||[]).map(image=>image.url)).filter(Boolean));
      const images=new Map(await Promise.all([...urls].map(async url=>[url,await highlightImage(url)])));
      if(cancelled)return;
      const context=canvas.current.getContext('2d');context.clearRect(0,0,view.width,view.height);
      context.imageSmoothingEnabled=false;context.globalAlpha=.6;context.shadowColor='#53ff82';context.shadowBlur=2;
      for(const item of visible){
        const visual=visuals?.get(`${item.kind}:${item.prototype}`);
        for(const layer of spriteLayers(item,visual)){
          const image=images.get(layer.image.url);if(!image)continue;
          const x=(layer.x+(offset?.dx||0)-area.x)*cellSize-view.left;
          const y=(area.y+area.height-layer.y-(offset?.dy||0))*cellSize-view.top;
          const scale=cellSize/Math.max(image.width,image.height);
          const width=layer.image.native?layer.width*cellSize:image.width*scale;
          const height=layer.image.native?layer.height*cellSize:image.height*scale;
          context.save();context.transform(layer.a,-layer.b,-layer.c,layer.d,x,y);
          if(item.kind==='tile'&&visual?.allowRotationMirror&&(item.mirror&4))context.scale(-1,1);
          context.drawImage(image,-width/2,-height/2,width,height);context.restore();
        }
      }
    };
    draw();return()=>{cancelled=true;};
  }, [items, area, layers, view, cellSize, offset, visuals]);
  return <canvas ref={canvas} className="GridEditor__scene GridEditor__selection" width={Math.max(1, view.width)} height={Math.max(1, view.height)}
    style={{ left: view.left, top: view.top, width: view.width, height: view.height }} aria-hidden="true" />;
});
