import { useEffect, useMemo, useState } from 'react';
import { useBackend } from '../backend';
import { Button } from '../components';
import { EmbeddedSelect } from '../components/EmbeddedSelect';
import { LazySprite } from '../components/LazySprite';
import { playerTheme } from '../components/PlayerTheme';
import { editorAppearance } from './CharacterEditorSettings';
import { MarkingChoices } from './CharacterAppearance';
import { filter as fuzzyFilter } from './fuzzySearch';
import './CharacterEditor.scss';

export const CharacterCatalog = () => {
  const { data, act } = useBackend();
  const [query, setQuery] = useState('');
  const [category, setCategory] = useState('');
  const [race, setRace] = useState('');
  const [filterOpen, setFilterOpen] = useState(false);
  const markings = data.kind === 'markings', beard = data.kind === 'beard';
  const title = markings ? 'Каталог особенностей' : beard ? 'Все бороды и усы' : 'Все причёски';
  const options = markings ? (data.markingOptions || []) : data[beard ? 'beardOptions' : 'hairOptions'] || [];
  const categories = [...new Set(options.map(option => option.category))].filter(Boolean);
  const activeCategory = categories.includes(category) ? category : '';
  const combinationCategory = options.find(option => option.category === activeCategory)?.combinationCategory;
  const races = useMemo(() => [...new Map(options.flatMap(option => option.races || []).map(item => [item.id, item])).values()]
    .sort((a, b) => a.name.localeCompare(b.name, 'ru')), [options]);
  const raceRestricted = markings && !!data.markingSpeciesRestricted;
  const activeRace = !raceRestricted && races.some(item => item.id === race) ? race : '';
  const filtered = useMemo(() => fuzzyFilter(options.filter(option => !markings ||
    ((!activeCategory || option.category === activeCategory) && (!activeRace || option.races?.some(item => item.id === activeRace)))), query,
    option => option.name + ' ' + option.id), [options, query, markings, activeCategory, activeRace]);
  const key = beard ? 'beard' : 'hair';
  const color = data[key + 'Locked'] ? data.skinColor : data[key + 'Color'];
  useEffect(() => { setQuery(''); setCategory(''); setRace(''); setFilterOpen(false); }, [data.kind]);
  useEffect(() => { if (raceRestricted) { setRace(''); setFilterOpen(false); } }, [raceRestricted]);
  useEffect(() => {
    const key = event => { if (event.key === 'Escape') act('catalog-close'); };
    window.addEventListener('keydown', key);
    return () => window.removeEventListener('keydown', key);
  }, [act]);
  const appearance = data.appearance || {}, theme = playerTheme(appearance);
  return <div className={'CharacterEditor CharacterEditor--catalog Chat ' + theme.className} style={{ ...theme.style, ...editorAppearance(appearance) }}>
    <header className="CharacterEditor__header"><div className="CharacterEditor__windowTitle">{title}</div>
      <div className="CharacterEditor__headerActions"><Button role="button" icon="times" aria-label={'Закрыть: ' + title} onClick={() => act('catalog-close')}>Закрыть</Button></div>
    </header>
    <main className="CharacterEditor__catalogBody">
      {raceRestricted && <p className="CharacterEditor__raceRestriction" role="note">{`Сейчас раса вашего персонажа это "${data.speciesName || data.species}", чтобы выбрать элементы других рас, вам нужно взять черту "Генная модификация"`}</p>}
      <div className="CharacterEditor__catalogToolbar"><input type="search" aria-label={'Поиск: ' + title} placeholder="Поиск…" value={query} onChange={event => setQuery(event.target.value)} />
      {markings && <Button role="button" icon="filter" aria-label="Фильтр" disabled={raceRestricted} aria-disabled={raceRestricted} aria-expanded={!raceRestricted && filterOpen}
        aria-controls="marking-race-filter" selected={!!activeRace} onClick={() => setFilterOpen(!filterOpen)}>Фильтр</Button>}
      {markings && <label className="CharacterEditor__combineMarkings"><input type="checkbox" disabled={!combinationCategory}
        checked={!!combinationCategory && (data.combinedMarkingCategories || []).some(item => item.id === combinationCategory)}
        onChange={() => act('marking-combine', { value: combinationCategory })} /><span>Комбинировать выборы этой категории</span></label>}
    </div>
      {markings && !raceRestricted && filterOpen && <div className="CharacterEditor__catalogFilter" id="marking-race-filter">
        <label><span>Расовая категория</span><EmbeddedSelect aria-label="Расовая категория" value={activeRace} onChange={event => setRace(event.target.value)}>
          <option value="">Все расы</option>{races.map(item => <option key={item.id} value={item.id}>{item.name}</option>)}
        </EmbeddedSelect></label>
      </div>}
      {markings && <nav className="CharacterEditor__catalogCategories" aria-label="Категории особенностей">
        <button type="button" aria-pressed={!activeCategory} onClick={() => setCategory('')}>Все</button>
        {categories.map(item => <button type="button" key={item} aria-pressed={activeCategory === item} onClick={() => setCategory(item)}>{item}</button>)}
      </nav>}
      <div className="CharacterEditor__catalogContent">
        {markings ? [...new Set(filtered.map(option => option.category))].map(category => <section className="CharacterEditor__catalogGroup" key={category}>
          <h3>{category}</h3><MarkingChoices options={filtered.filter(option => option.category === category)} selected={data.markings || []} act={act} />
        </section>) : <div className="CharacterEditor__catalogGrid HairCarousel__catalog">{filtered.map(option => <button type="button" key={option.id}
          aria-pressed={data[key + 'Style'] === option.id} onClick={() => act(key + '-style', { value: option.id })}>
          <div className="CharacterEditor__sprite">{data.hairPreviewImage && <img className="HairCarousel__face" src={data.hairPreviewImage} alt="" />}
            {option.iconId && <LazySprite id={option.iconId} color={color} />}</div><span>{option.name}</span>
        </button>)}</div>}
        {!filtered.length && <p role="status">Ничего не найдено</p>}
      </div>
    </main>
  </div>;
};
