import { type RefObject, useEffect, useState } from 'react';

type Rect = { x: number; y: number; width: number; height: number };
const intersects = (a: Rect, b: Rect) => a.x < b.x + b.width && a.x + a.width > b.x && a.y < b.y + b.height && a.y + a.height > b.y;

/** Fixed for this window's lifetime. Hover, stock and balance updates must not regenerate the wear. */
function drawWear(seed: number, width: number, height: number, protectedAreas: Rect[]) {
  let state = seed || 1;
  const random = () => {
    state ^= state << 13; state ^= state >>> 17; state ^= state << 5;
    return (state >>> 0) / 4294967296;
  };
  const marks: string[] = [];
  // Dense hairline wear, visible close up rather than large gouges across the paint.
  const count = Math.min(1200, Math.max(220, Math.round(width * height / 600)));
  const n = (number: number) => number.toFixed(1);
  for (let attempt = 0; attempt < count * 35 && marks.length < count; attempt++) {
    const kind = random();
    const w = kind < .94 ? 1 + random() * 8 : 1.2 + random() * 3;
    const h = kind < .94 ? .4 + random() * 1.6 : .6 + random() * 2;
    const x = 12 + random() * Math.max(0, width - w - 24);
    const y = 12 + random() * Math.max(0, height - h - 24);
    const safeBounds = { x: x - 3, y: y - 3, width: w + 6, height: h + 6 };
    if (protectedAreas.some(area => intersects(safeBounds, area))) continue;
    const opacity = (.08 + random() * .16).toFixed(2);
    if (kind < .94) {
      const end = y + h * (random() < .5 ? .2 : .8);
      marks.push(`<g opacity="${opacity}"><path d="M${n(x)} ${n(y + .5)}l${n(w * .4)} ${n(h * .2)}L${n(x + w)} ${n(end + .5)}" fill="none" stroke="#081006" stroke-width=".8"/><path d="M${n(x)} ${n(y)}l${n(w * .4)} ${n(h * .2)}L${n(x + w)} ${n(end)}" fill="none" stroke="#b9bd9f" stroke-opacity=".4" stroke-width=".45"/></g>`);
    } else if (kind < .99) {
      marks.push(`<g opacity="${opacity}"><path d="M${n(x)} ${n(y + h * .4)}l${n(w * .25)} ${n(-h * .4)} ${n(w * .43)} ${n(h * .13)} ${n(w * .32)} ${n(h * .42)} ${n(-w * .29)} ${n(h * .4)} ${n(-w * .48)} ${n(h * .05)}Z" fill="#111a0d" stroke="#75805c" stroke-width=".35"/><path d="M${n(x + w * .2)} ${n(y + h * .25)}l${n(w * .42)} ${n(h * .03)} ${n(w * .21)} ${n(h * .3)} ${n(-w * .49)} ${n(h * .21)}Z" fill="#877e61" fill-opacity=".35"/></g>`);
    } else {
      marks.push(`<g opacity="${opacity}"><ellipse cx="${n(x + w / 2)}" cy="${n(y + h / 2)}" rx="${n(w / 2)}" ry="${n(h / 2)}" fill="url(#dent)"/><path d="M${n(x + w * .18)} ${n(y + h * .8)}q${n(w * .35)} ${n(h * .2)} ${n(w * .65)} ${n(-h * .06)}" fill="none" stroke="#a1aa81" stroke-opacity=".2" stroke-width=".4"/></g>`);
    }
  }
  // The mask is a second guard against scratches crossing text when a line wraps.
  const mask = protectedAreas.map(area => `<rect x="${n(area.x - 3)}" y="${n(area.y - 3)}" width="${n(area.width + 6)}" height="${n(area.height + 6)}" fill="black"/>`).join('');
  const svg = `<svg xmlns="http://www.w3.org/2000/svg" width="${width}" height="${height}" viewBox="0 0 ${width} ${height}"><defs><radialGradient id="dent" cx=".38" cy=".3"><stop stop-color="#080e06" stop-opacity=".75"/><stop offset=".7" stop-color="#182211" stop-opacity=".5"/><stop offset="1" stop-color="#a4ae82" stop-opacity=".05"/></radialGradient><mask id="free"><rect width="100%" height="100%" fill="white"/>${mask}</mask></defs><g mask="url(#free)">${marks.join('')}</g></svg>`;
  return `url("data:image/svg+xml,${encodeURIComponent(svg)}")`;
}

export function CabinetWear({ surface }: { surface: RefObject<HTMLDivElement | null> }) {
  const [seed] = useState(() => Math.floor(Math.random() * 4294967296));
  const [image, setImage] = useState<string>();
  useEffect(() => {
    const root = surface.current;
    if (!root) return;
    let frame = 0;
    const redraw = () => {
      cancelAnimationFrame(frame);
      frame = requestAnimationFrame(() => {
        const bounds = root.getBoundingClientRect();
        if (!root.offsetWidth || !root.offsetHeight || bounds.width <= 0 || bounds.height <= 0) return;
        const sx = bounds.width / root.offsetWidth, sy = bounds.height / root.offsetHeight;
        const protectedAreas = [...root.querySelectorAll<HTMLElement>('[data-wear-protect]')].map(element => {
          let rect = element.getBoundingClientRect();
          if (element.dataset.wearProtect === 'text') {
            const range = document.createRange(); range.selectNodeContents(element); rect = range.getBoundingClientRect();
          }
          return { x: (rect.left - bounds.left) / sx - root.clientLeft, y: (rect.top - bounds.top) / sy - root.clientTop, width: rect.width / sx, height: rect.height / sy };
        });
        setImage(drawWear(seed, root.clientWidth, root.clientHeight, protectedAreas));
      });
    };
    const observer = new ResizeObserver(redraw);
    observer.observe(root);
    root.querySelectorAll('[data-wear-protect]').forEach(element => observer.observe(element));
    redraw();
    return () => { cancelAnimationFrame(frame); observer.disconnect(); };
  }, [seed, surface]);
  return <div className="VendingMachine__wear" style={{ backgroundImage: image }} aria-hidden="true" />;
}
