import { useId } from 'react';
// Takes the chat appearance settings object (theme, chatBgColor, chatTextColor), not a JSON string:
// the page should not parse anything on render.
export const playerTheme = settings => {
  settings=settings||{};
  const light=settings.theme==='default'||settings.theme==='light';
  // Themes supply colors only. Interfaces that need animation explicitly
  // configure the native layer so movement does not repaint their CEF texture.
  return {className:light?'theme-light':'theme-dark',
    style:{backgroundColor:settings.chatBgColor||(light?'#eeeeee':'#171c24'),color:settings.chatTextColor||(light?'#171c24':'#ddd'),
      '--player-surface':light?'#dedee3':'#252b35'}};
};
export const TintedSprite = ({image,color='#ffffff',baseColor='#ffffff'}) => {
  color='#'+[1,3,5].map(i=>Math.round((parseInt(color.slice(i,i+2),16)||0)*(parseInt(baseColor.slice(i,i+2),16)||0)/255).toString(16).padStart(2,'0')).join('');
  // Chromium resolves fragment filters by ID; every image needs its own stable filter.
  const id='sprite-tint-'+useId().replace(/[^a-z0-9_-]/gi,'');
  const rgb=[1,3,5].map(i=>(parseInt(color.slice(i,i+2),16)||0)/255);
  return <><svg width="0" height="0" aria-hidden="true"><filter id={id} colorInterpolationFilters="sRGB"><feColorMatrix type="matrix" values={`${rgb[0]} 0 0 0 0 0 ${rgb[1]} 0 0 0 0 0 ${rgb[2]} 0 0 0 0 0 1 0`} /></filter></svg><img src={image} alt="" style={{filter:`url(#${id})`}} /></>;
};
