// ShuttleHullNavMapControl palette and NavMapControl's inset airlock geometry.
export const navMapPalette = { background: '#10171b', floor: '#435257', wall: '#869395' };
export function navMapGeometry(tiles, structures, area) {
  let floors = '';
  for (const tile of tiles) floors += `M${tile.x - area.x},${area.y + area.height - tile.y - 1}h1v1h-1z`;
  return { floors, doors: structures.filter(item => item.door).map(item => ({
    x: Math.floor(item.x) - area.x + .5, y: area.y + area.height - Math.floor(item.y) - .5, rotation: -(item.rotation || 0),
  })) };
}
