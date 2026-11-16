import { useId, useMemo, useState } from 'react';
import { Button, Section } from '../components';

const signedPoints = points => points > 0 ? '+' + points : String(points);
const pointTone = points => points > 0 ? 'gain' : points < 0 ? 'cost' : 'neutral';

export const CharacterTraits = ({ data, act }) => {
  const id = useId();
  const [groupId, setGroupId] = useState('speech');
  const [inspected, setInspected] = useState({});
  const [query, setQuery] = useState('');
  const [showUnavailable, setShowUnavailable] = useState(false);
  const [confirmClear, setConfirmClear] = useState(false);
  const { mode, traits, traitGroups, traitState, traitBudget } = data;
  const search = query.trim().toLocaleLowerCase();
  const results = useMemo(() => {
    const groups = new Map();
    if (mode !== 'traits') return groups;

    for (const entry of traits || []) {
      const trait = { ...entry, ...traitState?.[entry.id] };
      if (!showUnavailable && !trait.selected && !trait.available) continue;
      if (search && !`${trait.name} ${trait.description}`.toLocaleLowerCase().includes(search)) continue;
      if (!groups.has(trait.group)) groups.set(trait.group, []);
      groups.get(trait.group).push(trait);
    }
    return groups;
  }, [mode, traits, traitState, search, showUnavailable]);

  if (mode !== 'traits') return null;

  const groups = traitGroups || [];
  const group = groups.find(entry => entry.id === groupId) || groups[0];
  const rows = results.get(group?.id) || [];
  const detail = rows.find(trait => trait.id === inspected[group?.id]) || rows[0];
  const tabId = category => `${id}-${category}`;
  const panelId = `${id}-panel`;
  const navigateTab = (event, index) => {
    const last = groups.length - 1;
    let next;
    switch (event.key) {
      case 'ArrowLeft': next = index === 0 ? last : index - 1; break;
      case 'ArrowRight': next = index === last ? 0 : index + 1; break;
      case 'Home': next = 0; break;
      case 'End': next = last; break;
      default: return;
    }
    event.preventDefault();
    setGroupId(groups[next].id);
    event.currentTarget.parentElement.children[next].focus();
  };

  return (
    <>
      <Section title="Черты персонажа">
        <div role="status" className="CharacterEditor__points">
          Осталось очков: {traitBudget?.points ?? 0} · Слоты: {traitBudget?.slots ?? 0} / {traitBudget?.maxSlots ?? 0}
        </div>
        {traitBudget?.valid === false && (
          <div role="alert">{traitBudget.reasons?.map(reason => <p key={reason}>{reason}</p>)}</div>
        )}
        <input
          type="search"
          aria-label="Поиск черт"
          placeholder="Поиск по всем категориям…"
          value={query}
          onChange={event => setQuery(event.target.value)}
        />
        <div className="CharacterEditor__traitTools">
          <Button
            role="button"
            aria-pressed={showUnavailable}
            selected={showUnavailable}
            onClick={() => setShowUnavailable(!showUnavailable)}>
            Показать недоступные
          </Button>
          <Button role="button" color="bad" onClick={() => {
            if (confirmClear) act('clear-traits');
            setConfirmClear(!confirmClear);
          }}>
            {confirmClear ? 'Подтвердить удаление всех черт' : 'Очистить все черты'}
          </Button>
          {confirmClear && <Button role="button" onClick={() => setConfirmClear(false)}>Отмена</Button>}
        </div>
      </Section>

      <nav className="CharacterEditor__traitTabs" role="tablist" aria-label="Категории черт">
        {groups.map((category, index) => (
          <button
            type="button"
            role="tab"
            key={category.id}
            id={tabId(category.id)}
            aria-controls={panelId}
            aria-selected={category === group}
            tabIndex={category === group ? 0 : -1}
            onClick={() => setGroupId(category.id)}
            onKeyDown={event => navigateTab(event, index)}>
            {category.name}{search && ` (${results.get(category.id)?.length ?? 0})`}
          </button>
        ))}
      </nav>

      {group && (
        <div
          id={panelId}
          role="tabpanel"
          aria-labelledby={tabId(group.id)}
          className="CharacterEditor__traitBody">
          {detail ? (
            <>
              <ul className="CharacterEditor__traitList" aria-label={group.name} key={group.id}>
                {rows.map(trait => (
                  <li key={trait.id}>
                    <button
                      type="button"
                      aria-pressed={trait === detail}
                      className={`CharacterEditor__traitOption CharacterEditor__traitTone--${pointTone(trait.points)}`}
                      onClick={() => setInspected(previous => ({ ...previous, [group.id]: trait.id }))}>
                      <span className="CharacterEditor__traitName">{trait.name}</span>
                      {trait.selected && <span aria-label="Применена" title="Применена">✓</span>}
                      <span className="CharacterEditor__traitValue">{signedPoints(trait.points)}</span>
                    </button>
                  </li>
                ))}
              </ul>
              <div className="CharacterEditor__traitDetail">
                <h3>{detail.name}</h3>
                <p className="CharacterEditor__traitDescription">{detail.description || 'Описание отсутствует.'}</p>
                <dl className="CharacterEditor__traitParameters">
                  <div>
                    <dt>Очки черт</dt>
                    <dd className={`CharacterEditor__traitTone--${pointTone(detail.points)}`}>{signedPoints(detail.points)}</dd>
                  </div>
                  <div><dt>Занимает слотов</dt><dd>{detail.slots}</dd></div>
                </dl>
                {detail.reason && <p className="CharacterEditor__traitReason">{detail.reason}</p>}
                <Button
                  role="button"
                  color={detail.selected ? 'bad' : 'good'}
                  disabled={!detail.selected && !detail.canSelect}
                  aria-disabled={!detail.selected && !detail.canSelect}
                  onClick={() => act('trait', { id: detail.id })}>
                  {detail.selected ? 'Очистить' : 'Применить'}
                </Button>
              </div>
            </>
          ) : (
            <p role="status">В этой категории нет подходящих черт.</p>
          )}
        </div>
      )}
    </>
  );
};
