import { Button, Section } from '../components';
import { ColorSquare } from '../components/PlayerColorPicker';
import { LazySprite } from '../components/LazySprite';
import { TattooEditor } from './TattooStudio';

const side = part => /^L(Arm|Hand|Leg|Foot)/.test(part) ? 'left'
  : /^R(Arm|Hand|Leg|Foot)/.test(part) ? 'right' : 'center';

const MarkingRow = ({ marking, act }) => <div className="CharacterEditor__markingRow">
  <strong>{marking.name}</strong>
  <div className="CharacterEditor__markingColors">
    {(marking.colors || []).map((color, layer) => <ColorSquare key={layer}
      label={'Цвет ' + (layer + 1) + ': ' + marking.name} value={color.name} disabled={marking.locked}
      onChange={value => act('marking-color', { index: marking.index, layer, value })} />)}
  </div>
  <div className="CharacterEditor__markingActions">
    <Button role="button" aria-label={'Удалить: ' + marking.name} icon="times" onClick={() => act('marking-remove', { index: marking.index })} />
    <Button role="button" aria-label={'Выше: ' + marking.name} onClick={() => act('marking-move', { index: marking.index, value: -1 })}>↑</Button>
    <Button role="button" aria-label={'Ниже: ' + marking.name} onClick={() => act('marking-move', { index: marking.index, value: 1 })}>↓</Button>
  </div>
</div>;

const MarkingColumn = ({ position, data, act, matched }) => {
  const options = (data.markingOptions || []).filter(option => side(option.part || '') === position);
  const selected = (data.markings || []).filter(marking => side(marking.part || '') === position);
  return <>
    {selected.map(marking => <MarkingRow key={marking.index} marking={marking} act={act} />)}
    {[...new Set(options.map(option => option.category))].map(category => <details key={category}>
      <summary>{category}</summary>
      <div className="CharacterEditor__tiles">{options.filter(option => option.category === category && matched.has(option)).map(option =>
        <button type="button" key={option.id} aria-pressed={selected.some(marking => marking.id === option.id)}
          onClick={() => act('marking-add', { value: option.id })}>
          <div className="CharacterEditor__sprite">{option.iconId && <LazySprite id={option.iconId} />}</div>
          {option.name}
        </button>)}</div>
    </details>)}
  </>;
};

export const CharacterAppearance = ({ data, act, matched, previewRef, tattooPanelOpen, setTattooPanelOpen }) => <div className="CharacterEditor__anatomy">
  <label className="CharacterEditor__combineMarkings"><input type="checkbox" checked={!!data.combineMarkings}
    onChange={() => act('marking-combine')} /> Комбинировать выборы</label>
  <div className="CharacterEditor__anatomySides">
    <section className="CharacterEditor__anatomyLeft" aria-label="Левые части тела">
      <h3>Левые части тела</h3><MarkingColumn position="left" data={data} act={act} matched={matched} />
    </section>
    <div className="CharacterEditor__anatomyPreview">
      <div ref={previewRef} className="CharacterEditor__anatomySpace"
        onWheel={event => { event.preventDefault(); act('rotate', { value: event.deltaY > 0 ? 1 : -1 }); }}>
        <div className="CharacterEditor__anatomyFloor" style={{ backgroundImage: data.previewFloor ? `url("${data.previewFloor}")` : undefined }} />
      </div>
      <div className="CharacterEditor__anatomyRotation"><Button aria-label="Повернуть влево" onClick={() => act('rotate', { value: -1 })}>◀</Button>
        <Button aria-label="Повернуть вправо" onClick={() => act('rotate', { value: 1 })}>▶</Button></div>
    </div>
    <section className="CharacterEditor__anatomyRight" aria-label="Правые части тела">
      <h3>Правые части тела</h3><MarkingColumn position="right" data={data} act={act} matched={matched} />
    </section>
  </div>
  <div className="CharacterEditor__anatomyLower">
    <Section title="Торс, голова и особые детали"><MarkingColumn position="center" data={data} act={act} matched={matched} /></Section>
    <details className="CharacterEditor__tattoos" open={tattooPanelOpen} onToggle={event => setTattooPanelOpen(event.currentTarget.open)}>
      <summary>Татуировки ({data.tattooEditor?.tattoos?.length || 0})</summary>
      <Section>
        <Button color="bad" disabled={!data.tattooEditor?.tattoos?.length || data.tattooEditor?.fileBusy || data.tattooEditor?.banned} onClick={() => act('tattoo-clear')}>Очистить все татуировки</Button>
        <TattooEditor data={data.tattooEditor} act={act} active={tattooPanelOpen} />
      </Section>
    </details>
  </div>
</div>;
