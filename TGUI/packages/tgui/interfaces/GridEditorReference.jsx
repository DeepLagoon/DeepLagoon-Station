import { useEffect, useRef, useState } from 'react';

export function useGridEditorReference({ data, act, area, viewport, canvas, cellSize, blocked, onFailure }) {
  const [image, setImage] = useState(null), drag = useRef(null), sequence = useRef(0), alive = useRef(true);
  const imagePasteOnly = useRef(false);
  const patch = fields => setImage(previous => previous && { ...previous, ...fields });
  const load = (url, name) => {
    if (!alive.current) return;
    const ticket = ++sequence.current, source = new Image();
    source.onload = () => {
      if (ticket !== sequence.current) return;
      if (source.width > 8192 || source.height > 8192 || source.width * source.height > 16777216) { onFailure('Максимум 8192 по стороне и 16 миллионов пикселей.'); return; }
      const width = source.width / 32, aspect = source.height / source.width, view = viewport.current;
      const bounds = view.getBoundingClientRect(), plan = canvas.current.getBoundingClientRect();
      setImage({ url, name, width, aspect, opacity: .55, rotation: 0, locked: false, visible: true,
        x: area.x + (bounds.left + view.clientWidth / 2 - plan.left) / cellSize - width / 2,
        y: area.y + area.height - (bounds.top + view.clientHeight / 2 - plan.top) / cellSize - width * aspect / 2 });
    };
    source.onerror = () => { if (ticket === sequence.current) onFailure('Не удалось прочитать изображение.'); }; source.src = url;
  };
  const importFile = file => {
    if (!file || !['image/png', 'image/jpeg', 'image/webp'].includes(file.type)) { onFailure('Выберите PNG, JPEG или WebP.'); return; }
    if (file.size > 8 * 1024 * 1024) { onFailure('Максимальный размер скриншота — 8 МБ.'); return; }
    const reader = new FileReader(); reader.onload = () => load(reader.result, file.name || 'Буфер обмена');
    reader.onerror = () => onFailure('Не удалось прочитать файл.'); reader.readAsDataURL(file);
  };
  const handlePaste = event => {
    const file = Array.from(event.clipboardData?.items || []).find(item => item.kind === 'file' && item.type.startsWith('image/'))?.getAsFile();
    if (!file) return false;
    event.preventDefault(); importFile(file); return true;
  };
  const pasteImage = async () => {
    canvas.current.focus();
    try {
      if (navigator.clipboard?.read) {
        for (const entry of await navigator.clipboard.read()) {
          const type = entry.types.find(type => ['image/png', 'image/jpeg', 'image/webp'].includes(type));
          if (type) { importFile(new File([await entry.getType(type)], 'Буфер обмена', { type })); return; }
        }
        onFailure('В буфере обмена нет изображения.'); return;
      }
    } catch { /* Native CEF paste is available even when the async clipboard API is unavailable. */ }
    imagePasteOnly.current = true;
    if (!document.execCommand('paste')) { imagePasteOnly.current = false; onFailure('Нажмите Ctrl+V над планом для вставки скриншота.'); }
  };
  useEffect(() => { if (data.referenceImage) load(data.referenceImage, 'Скриншот'); }, [data.referenceVersion]);
  useEffect(() => { alive.current = true; return () => { alive.current = false; sequence.current++; }; }, []);
  const controls = <><strong>Скриншот для примерки</strong>
    <button disabled={blocked} onClick={() => act('referenceImport')}>Открыть PNG / JPEG / WebP</button><button disabled={blocked} onClick={pasteImage}>Вставить скриншот · Ctrl+V</button>
    {image && <>
      <label><input type="checkbox" checked={image.visible} onChange={event => patch({ visible: event.target.checked })} />Показывать скриншот</label>
      <label><input type="checkbox" checked={image.locked} onChange={event => patch({ locked: event.target.checked })} />Закрепить подложку</label>
      <label>Ширина в клетках <input aria-label="Ширина скриншота" type="number" min={.125} max={1024} step={.125} value={image.width} onChange={event => patch({ width: Math.max(.125, Math.min(1024, Number(event.target.value) || .125)) })} /></label>
      <label>Прозрачность <input aria-label="Прозрачность скриншота" type="range" min={.05} max={1} step={.05} value={image.opacity} onChange={event => patch({ opacity: Number(event.target.value) })} /></label>
      <button onClick={() => patch({ rotation: (image.rotation + 90) % 360 })}>Повернуть скриншот · 90°</button>
      <button onClick={() => { sequence.current++; setImage(null); }}>Убрать скриншот</button>
    </>}</>;
  const overlay = image?.visible && <div className="GridEditor__reference" aria-label="Скриншот на плане"
    style={{ left: (image.x - area.x) * cellSize, top: (area.y + area.height - image.y - image.width * image.aspect) * cellSize,
      width: image.width * cellSize, height: image.width * image.aspect * cellSize, opacity: image.opacity,
      transform: `rotate(${-image.rotation}deg)`, pointerEvents: image.locked || blocked ? 'none' : 'auto' }}
    onPointerDown={event => { if (event.button !== 0) return; event.preventDefault(); event.stopPropagation(); canvas.current.focus();
      drag.current = { px: event.clientX, py: event.clientY, x: image.x, y: image.y, cellSize }; event.currentTarget.setPointerCapture(event.pointerId); }}
    onPointerMove={event => { if (!drag.current) return; event.stopPropagation(); const start = drag.current;
      patch({ x: start.x + (event.clientX - start.px) / start.cellSize, y: start.y - (event.clientY - start.py) / start.cellSize }); }}
    onPointerUp={event => { event.stopPropagation(); drag.current = null; if (event.currentTarget.hasPointerCapture(event.pointerId)) event.currentTarget.releasePointerCapture(event.pointerId); }}
    onPointerCancel={() => { if (drag.current) patch({ x: drag.current.x, y: drag.current.y }); drag.current = null; }}>
    <img src={image.url} alt={image.name} draggable={false} />
  </div>;
  return { controls, overlay, handlePaste, importFile, imagePasteOnly };
}
