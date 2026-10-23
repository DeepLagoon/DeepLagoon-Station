import { memo, useId, useMemo } from 'react';
import { navMapGeometry, navMapPalette } from './navMapPlan.mjs';

export const NavMapPlan = memo(({ tiles = [], structures = [], area }) => {
  const filterPrefix = useId().replaceAll(':', '');
  const colors = [...new Set(structures.flatMap(item => item.images || []).map(image => image.color).filter(color => /^#[\da-f]{6}$/i.test(color) && color.toLowerCase() !== '#ffffff'))];
  const tintId = color => `${filterPrefix}-wall-${color.slice(1)}`;
  const geometry = useMemo(() => navMapGeometry(tiles, structures, area), [tiles, structures, area]);
  return <><svg className="NavMapPlan" viewBox={`0 0 ${area.width} ${area.height}`} preserveAspectRatio="none"
    aria-hidden="true" style={{ position: 'absolute', inset: 0, width: '100%', height: '100%', pointerEvents: 'none', background: navMapPalette.background }}>
    <defs>{colors.map(color => <filter key={color} id={tintId(color)} colorInterpolationFilters="sRGB">
      <feColorMatrix type="matrix" values={`${parseInt(color.slice(1, 3), 16) / 255} 0 0 0 0 0 ${parseInt(color.slice(3, 5), 16) / 255} 0 0 0 0 0 ${parseInt(color.slice(5, 7), 16) / 255} 0 0 0 0 0 1 0`} />
    </filter>)}</defs>
    <path d={geometry.floors} fill={navMapPalette.floor} />
    {geometry.doors.map((door, index) => <g key={index} transform={`translate(${door.x} ${door.y}) rotate(${door.rotation})`}>
      <rect x={-.335} y={-.335} width={.67} height={.67} fill={navMapPalette.wall} />
      <path d="M0,-.335V.335" stroke={navMapPalette.background} strokeWidth={.045} />
    </g>)}
  </svg>
    <div className="PlanWallSprites" aria-hidden="true" style={{ position: 'absolute', inset: 0, pointerEvents: 'none' }}>
      {structures.filter(item => !item.door).map((item, index) => <div key={index} data-wall-sprite="true" style={{
        position: 'absolute', left: `${(item.x - area.x - .5) / area.width * 100}%`, top: `${(area.y + area.height - item.y - .5) / area.height * 100}%`,
        width: `${100 / area.width}%`, height: `${100 / area.height}%`,
      }}><span style={{ position: 'absolute', inset: 0, transform: `rotate(${item.noRotation || item.images?.some(image => image.worldAligned) ? 0 : -(item.rotation || 0)}deg)` }}>
        {(item.images || []).map((image, layer) => <img key={layer} src={image.url} alt="" draggable={false} style={{
          position: 'absolute', imageRendering: 'pixelated', maxWidth: 'none', maxHeight: 'none',
          filter: colors.includes(image.color) ? `url(#${tintId(image.color)})` : undefined,
          width: `${image.native ? image.width / 32 * 100 : 100}%`, height: `${image.native ? image.height / 32 * 100 : 100}%`,
          left: `${50 + (image.m31 || 0) * 100}%`, top: `${50 - (image.m32 || 0) * 100}%`,
          transform: image.native ? `translate(-50%, -50%) matrix(${image.m11}, ${-image.m12}, ${-image.m21}, ${image.m22}, 0, 0)` : 'translate(-50%, -50%)',
        }} />)}
      </span></div>)}
    </div>
  </>;
});
