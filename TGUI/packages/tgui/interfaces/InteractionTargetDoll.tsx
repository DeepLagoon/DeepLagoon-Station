type DollImages = { base: string; parts: Record<string, string> };
type Piece = { part: string; region: string; x: number; y: number; width: number; height: number; offsetY?: number; imageHeight?: number };

// HUD target_doll.png is 32x32. Reuse its targeting overlays at the same pixel scale.
// The interaction catalog groups both sides, and splits the upper torso into shoulders.
const pieces: Piece[] = [
  { part: 'head', region: 'head', x: 11, y: 4, width: 9, height: 7 },
  { part: 'torso', region: 'shoulders', x: 11, y: 11, width: 9, height: 2, imageHeight: 10 },
  { part: 'torso', region: 'torso', x: 11, y: 13, width: 9, height: 7, offsetY: -2, imageHeight: 10 },
  { part: 'groin', region: 'groin', x: 11, y: 20, width: 9, height: 4 },
  { part: 'rightarm', region: 'hands', x: 7, y: 11, width: 4, height: 8 },
  { part: 'leftarm', region: 'hands', x: 20, y: 11, width: 4, height: 8 },
  { part: 'righthand', region: 'hands', x: 7, y: 18, width: 4, height: 4 },
  { part: 'lefthand', region: 'hands', x: 20, y: 18, width: 4, height: 4 },
  { part: 'rightleg', region: 'legs', x: 11, y: 23, width: 4, height: 9 },
  { part: 'leftleg', region: 'legs', x: 17, y: 23, width: 4, height: 9 },
  { part: 'rightfoot', region: 'legs', x: 9, y: 29, width: 6, height: 3 },
  { part: 'leftfoot', region: 'legs', x: 17, y: 29, width: 6, height: 3 },
];

export const InteractionTargetDoll = ({ images, region, regionName, onSelect, label }: {
  images?: DollImages; region: string; regionName: (id: string) => string; onSelect: (id: string) => void; label: string;
}) => (
  <div className="InteractionTargetDoll" role="group" aria-label={label}>
    {images?.base && <img className="InteractionTargetDoll__base" src={images.base} alt="" draggable={false} />}
    {pieces.map((piece, i) => <button key={piece.part + '-' + piece.region} type="button"
      className="InteractionTargetDoll__part" data-region={piece.region} data-part={piece.part}
      aria-label={regionName(piece.region)} aria-pressed={region === piece.region}
      // One keyboard stop for each catalog region; mouse users can click either side.
      tabIndex={pieces.findIndex(p => p.region === piece.region) === i ? 0 : -1}
      style={{ left: piece.x * 3, top: piece.y * 3, width: piece.width * 3, height: piece.height * 3 }}
      onClick={() => onSelect(region === piece.region ? 'all' : piece.region)}>
      {images?.parts[piece.part] && <img src={images.parts[piece.part]} alt="" draggable={false}
        style={{ top: (piece.offsetY || 0) * 3, width: piece.width * 3, height: (piece.imageHeight || piece.height) * 3 }} />}
    </button>)}
  </div>
);
