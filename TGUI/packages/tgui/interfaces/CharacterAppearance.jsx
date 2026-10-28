import { useLayoutEffect, useRef } from 'react';
import { Button, Section } from '../components';
import { ColorSquare } from '../components/PlayerColorPicker';
import { LazySprite } from '../components/LazySprite';
import { TattooEditor } from './TattooStudio';

const toggle = (option, act) => act('marking-toggle', { value: option.id });
const MarkingRow = ({ marking, identity, act, capture }) => <div
  className="CharacterEditor__markingRow CharacterEditor__markingRow--selected"
  data-marking-key={identity} data-marking-id={marking.id} role="group" aria-label={'Выбрано: ' + marking.name}
  onContextMenu={event => { event.preventDefault(); act('marking-remove', { index: marking.index }); }}>
  <strong>{marking.name}</strong>
  <div className="CharacterEditor__markingColors">
    {(marking.colors || []).map((color, layer) => <ColorSquare key={layer}
      label={'Цвет ' + (layer + 1) + ': ' + marking.name} value={color.name} disabled={marking.locked}
      onChange={value => act('marking-color', { index: marking.index, layer, value })} />)}
  </div>
  <div className="CharacterEditor__markingActions">
    <Button role="button" aria-label={'Удалить: ' + marking.name} icon="times" onClick={() => act('marking-remove', { index: marking.index })} />
    <Button role="button" aria-label={'Выше: ' + marking.name} onClick={() => { capture(); act('marking-move', { index: marking.index, value: -1 }); }}>↑</Button>
    <Button role="button" aria-label={'Ниже: ' + marking.name} onClick={() => { capture(); act('marking-move', { index: marking.index, value: 1 }); }}>↓</Button>
  </div>
</div>;
const SelectedMarkings = ({ selected, act }) => {
  const list = useRef(null), before = useRef(null), animations = useRef([]);
  const nodes = () => [...list.current.querySelectorAll('[data-marking-key]')];
  const capture = () => { before.current = new Map(nodes().map(node => [node.dataset.markingKey, node.getBoundingClientRect()])); };
  useLayoutEffect(() => {
    if (!before.current) return;
    const old = before.current; before.current = null;
    animations.current.forEach(animation => animation.cancel()); animations.current = [];
    if (window.matchMedia('(prefers-reduced-motion: reduce)').matches) return;
    for (const node of nodes()) {
      const previous = old.get(node.dataset.markingKey), next = node.getBoundingClientRect();
      if (!previous) continue;
      const x = previous.left - next.left, y = previous.top - next.top;
      if (Math.abs(x) < 1 && Math.abs(y) < 1) continue;
      animations.current.push(node.animate([{ transform: `translate(${x}px, ${y}px)` }, { transform: 'translate(0, 0)' }],
        { duration: 280, easing: 'cubic-bezier(.2,.7,.2,1)' }));
    }
  });
  useLayoutEffect(() => () => animations.current.forEach(animation => animation.cancel()), []);
  const occurrences = new Map();
  return <div ref={list} className="CharacterEditor__selectedMarkings">{selected.map(marking => {
    const count = occurrences.get(marking.id) || 0; occurrences.set(marking.id, count + 1);
    const identity = marking.id + ':' + count;
    return <MarkingRow key={identity} identity={identity} marking={marking} act={act} capture={capture} />;
  })}</div>;
};
export const MarkingChoices = ({ options, selected, act }) => <div className="CharacterEditor__tiles">
  {options.map(option => <button type="button" className="cursor-interaction" key={option.id}
    data-marking-option={option.id} aria-pressed={selected.some(marking => marking.id === option.id)}
    onClick={() => toggle(option, act)} onContextMenu={event => {
      const existing = selected.find(marking => marking.id === option.id);
      if (existing) { event.preventDefault(); toggle(option, act); }
    }}>
    <div className="CharacterEditor__sprite">{option.iconId && <LazySprite id={option.iconId} />}</div>{option.name}
  </button>)}
</div>;
export const PreviewBackgroundButton = ({ light, onToggle }) => <Button role="button"
  aria-label="Сменить фон превью" aria-pressed={light} onClick={onToggle}>
  Фон: {light ? 'светлый' : 'тёмный'}
</Button>;
export const CharacterAppearance = ({ data, act, previewRef, tattooPanelOpen, setTattooPanelOpen, lightPreview, togglePreviewBackground }) => <div className="CharacterEditor__anatomy">
  <div className="CharacterEditor__anatomySides">
    <section className="CharacterEditor__anatomySelection" aria-label="Выбор элементов">
      <Section title="Выбор элементов">
        <div className="CharacterEditor__appearanceActions">
          <Button role="button" aria-label="Каталог особенностей" onClick={() => act('catalog-open', { kind: 'markings' })}>Каталог ↗</Button>
          <Button role="button" aria-disabled={!data.canUndoAppearance} disabled={!data.canUndoAppearance} onClick={() => act('appearance-undo')}>Отменить</Button>
          <Button role="button" aria-disabled={!data.canRedoAppearance} disabled={!data.canRedoAppearance} onClick={() => act('appearance-redo')}>Вернуть</Button>
          <label className="CharacterEditor__combineMarkings"><input type="checkbox" checked={!!data.combineMarkings} onChange={() => act('marking-combine')} /><span>Комбинировать выборы</span></label>
        </div>
        <SelectedMarkings selected={data.markings || []} act={act} />
      </Section>
    </section>
    <div className="CharacterEditor__anatomyPreview">
      <div ref={previewRef} className="CharacterEditor__anatomySpace" style={{ backgroundColor: lightPreview ? '#dce4ec' : '#262e3b' }} onWheel={event => { if (event.target.closest('input[type=range]')) return; event.preventDefault(); act('rotate', { value: event.deltaY > 0 ? 1 : -1 }); }}>
        <div className="CharacterEditor__anatomyFloor" style={{ backgroundImage: data.previewFloor ? `url("${data.previewFloor}")` : undefined }} />
      </div>
      <div className="CharacterEditor__anatomyRotation"><Button aria-label="Повернуть влево" onClick={() => act('rotate', { value: -1 })}>◀</Button><Button aria-label="Повернуть вправо" onClick={() => act('rotate', { value: 1 })}>▶</Button></div>
      <PreviewBackgroundButton light={lightPreview} onToggle={togglePreviewBackground} />
    </div>
  </div>
  <div className="CharacterEditor__anatomyLower">
    <details className="CharacterEditor__tattoos" open={tattooPanelOpen} onToggle={event => setTattooPanelOpen(event.currentTarget.open)}>
      <summary>Татуировки ({data.tattooEditor?.tattoos?.length || 0})</summary><Section>
        <Button color="bad" disabled={!data.tattooEditor?.tattoos?.length || data.tattooEditor?.fileBusy || data.tattooEditor?.banned} onClick={() => act('tattoo-clear')}>Очистить все татуировки</Button>
        <TattooEditor data={data.tattooEditor} act={act} active={tattooPanelOpen} />
      </Section>
    </details>
  </div>
</div>;
