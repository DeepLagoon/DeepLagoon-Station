import { useEffect, useRef } from 'react';
import { tintSprite } from './gridEditorTint.mjs';

export const GridEditorDecalSprite = ({ image, color, cellSize, native }) => {
  const canvas = useRef(null);
  useEffect(() => {
    let cancelled = false;
    const source = new Image(); source.onload = () => {
      if (cancelled) return;
      const tinted = tintSprite(source, color), target = canvas.current;
      target.width = source.width; target.height = source.height;
      target.getContext('2d').drawImage(tinted, 0, 0);
    }; source.src = image.url;
    return () => { cancelled = true; };
  }, [image.url, color]);
  const width=(image.width||32)*cellSize/32,height=(image.height||32)*cellSize/32;
  return <canvas ref={canvas} aria-hidden="true" style={native ? { position:'absolute',left:'50%',top:'50%',width,height,maxWidth:'none',maxHeight:'none',transform:'translate(-50%,-50%)' } : {position:'absolute',left:0,top:0,width:'100%',height:'100%',objectFit:'contain',imageRendering:'pixelated'}} />;
};
