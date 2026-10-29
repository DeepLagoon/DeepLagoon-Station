import { LazySprite } from '../components/LazySprite';
import { useEffect, useMemo, useRef, useState } from 'react';
import { useBackend } from '../backend';
import { Button, Dropdown, Section } from '../components';
import './CharacterEditor.scss';
import { ColorSquare, ColorTransferContext, EditorColorDialogProvider } from '../components/PlayerColorPicker';
import { useThrottledChange } from '../components/useThrottledChange';
import { playerTheme, TintedSprite } from '../components/PlayerTheme';
import { CharacterText } from '../components/CharacterText';
import { CharacterTextEditor } from '../components/CharacterTextEditor';
import { CharacterHeadshots } from './CharacterHeadshots';
import { EquipmentBrowser } from './EquipmentBrowser';
import { filter as fuzzyFilter } from './fuzzySearch'; // DeepLagoon: fuzzy search
import { CharacterEditorSettings, editorAppearance } from './CharacterEditorSettings';
import { CharacterAppearance, PreviewBackgroundButton, PreviewArousalButton } from './CharacterAppearance';
import { BlueMoonPreferences } from './BlueMoonPreferences';
import { configureNativeChatBackground, clearNativeChatBackground } from '../../tgui-panel/chat/nativeBackground';

const Field = ({ label, children }) => <label className="CharacterEditor__field"><span>{label}</span>{children}</label>;
const Select = ({ label, value, options = [], onChange, disabled }) => <Field label={label}>
  <Dropdown aria-label={label} selected={String(value ?? '')} disabled={disabled} width="100%"
    options={options.map(option => ({ value: String(option.id), displayText: option.name }))}
    onSelected={onChange} /></Field>;
const DraftInput = ({ label, value, onCommit, type = 'text', min, max }) => {
  const [draft, setDraft] = useState(String(value ?? ''));
  useEffect(() => setDraft(String(value ?? '')), [value]);
  const commit = () => { if (draft !== String(value)) onCommit(type === 'number' ? Number(draft) : draft); };
  return <Field label={label}><input aria-label={label} type={type} min={min} max={max} value={draft}
    onChange={event => setDraft(event.target.value)} onBlur={commit}
    onKeyDown={event => { if (event.key === 'Enter') event.currentTarget.blur(); }} /></Field>;
};
const ColorEditor = ColorSquare;
const SizeSlider = ({ label, value, onChange, vertical = false }) => {
  const [local, setLocal] = useState(value);
  const dragging = useRef(false);
  const [send, flush] = useThrottledChange(onChange);
  useEffect(() => { if (!dragging.current) setLocal(value); }, [value]);
  useEffect(() => {
    const end = () => { dragging.current = false; flush(); };
    window.addEventListener('pointerup', end);
    window.addEventListener('pointercancel', end);
    window.addEventListener('blur', end);
    return () => {
      window.removeEventListener('pointerup', end);
      window.removeEventListener('pointercancel', end);
      window.removeEventListener('blur', end);
    };
  }, [flush]);
  return <input className="CharacterEditor__sizeSlider" aria-label={label} aria-orientation={vertical ? 'vertical' : 'horizontal'}
      type="range" min="0.8" max="1.2" step="0.01" value={local}
      onPointerDown={() => { dragging.current = true; }} onBlur={flush}
      onChange={event => { const next = Number(event.target.value); setLocal(next); send(next); }} />;
};
const consentOptions = [{ id: '0', name: 'Запрещено' }, { id: '1', name: 'Спросить' }, { id: '2', name: 'Разрешено' }];
const priorities = [{ id: '0', name: 'Никогда' }, { id: '1', name: 'Низкий' }, { id: '2', name: 'Средний' }, { id: '3', name: 'Высокий' }];

const HairCarousel = ({ options, selected, color, onSelect, face, beard, act }) => {
  const track = useRef(null);
  const [query, setQuery] = useState('');
  const filtered = useMemo(() => fuzzyFilter(options, query, option => option.name + ' ' + option.id), [options, query]);
  useEffect(() => { if (track.current) track.current.scrollLeft = 0; }, [query]);
  const turn = direction => track.current?.scrollBy({ left: direction * track.current.clientWidth, behavior: 'smooth' });
  const label = beard ? 'Поиск бороды и усов' : 'Поиск причёски';
  const title = beard ? 'Все бороды и усы' : 'Все причёски';
  const choice = option => <button type="button" className="cursor-interaction" key={option.id} aria-pressed={selected === option.id} onClick={() => onSelect(option.id)}>
    <div className="CharacterEditor__sprite">
      {face && <img className="HairCarousel__face" src={face} alt="" />}
      {option.iconId ? <LazySprite id={option.iconId} color={color} />
        : (option.images || []).map((image, i) => <TintedSprite key={i} image={image.url} color={color} />)}
    </div><span>{option.name}</span>
  </button>;
  return <div className="HairCarousel__browser">
    <div className="HairCarousel__searchRow"><input type="search" aria-label={label} placeholder={label + '…'} value={query} onChange={event => setQuery(event.target.value)} />
      <Button role="button" aria-label={title} onClick={() => act('catalog-open', { kind: beard ? 'beard' : 'hair' })}>Все</Button></div>
    {filtered.length ? <div className="HairCarousel">
      <button type="button" aria-label="Предыдущие четыре" onClick={() => turn(-1)}>◀</button>
      <div className="HairCarousel__track" ref={track}>
        {filtered.map(choice)}
      </div><button type="button" aria-label="Следующие четыре" onClick={() => turn(1)}>▶</button>
    </div> : <p role="status">Ничего не найдено</p>}

  </div>;
};

const BodyFeatureEditor = ({ feature, features, skinColor, act }) => {
  const set = field => value => act('body-feature', { part: feature.part, field, value });
  const testicles = features.find(part => part.part === 'testicles');
  const anus = features.find(part => part.part === 'anus');
  return <details>
    <summary>{feature.name}{feature.enabled ? ' ✓' : ''}</summary>
    <Button selected={feature.enabled} onClick={() => set('enabled')(String(!feature.enabled))}>
      {feature.enabled ? 'Добавлено' : 'Добавить'}
    </Button>
    {feature.enabled && <div className="CharacterEditor__grid">
      <Select label="Форма" value={feature.shape} options={feature.shapes} onChange={set('shape')} />
      <Select label="Размер" value={feature.size} options={feature.sizes} onChange={set('size')} disabled={feature.sizes.length < 2} />
      <Select label="Отображение" value={feature.visibility} options={[{id: '0', name: 'Скрыто'}, {id: '1', name: 'Под одеждой'}, {id: '2', name: 'Поверх одежды'}]} onChange={value => set('visibility')(Number(value))} />
      <label className="CharacterEditor__clothingFit">
        <input type="checkbox" checked={feature.showThroughClothing !== false}
          onChange={event => set('showThroughClothing')(String(event.target.checked))} /> Деформировать одежду
      </label>
      <div className="CharacterEditor__bodyColor" role="group" aria-label={'Цвет: ' + feature.name}>
        <div className="CharacterEditor__bodyColorControls">
          <Button role="button" aria-pressed={feature.matchSkin} selected={feature.matchSkin}
            onClick={() => set('matchSkin')(String(!feature.matchSkin))}>Цвет кожи</Button>
          <ColorEditor label="Цвет части" value={feature.matchSkin ? skinColor : feature.color} disabled={feature.matchSkin}
            lockReason="Сначала отключите «Цвет кожи», чтобы выбрать свой цвет." onChange={set('color')} />
        </div>
        {feature.matchSkin && <small>Сначала отключите «Цвет кожи», чтобы выбрать свой цвет.</small>}
      </div>
      {feature.part === 'breasts' && <>
        <Button selected={feature.secondaryEnabled} disabled={!feature.secondaryAvailable} onClick={() => set('secondaryEnabled')(String(!feature.secondaryEnabled))}>Цветные соски</Button>
        {feature.secondaryEnabled && <ColorEditor label="Цвет сосков" value={feature.secondaryColor} disabled={!feature.secondaryAvailable} onChange={set('secondaryColor')} />}
        {!feature.secondaryAvailable && <small>Второй цвет доступен для обычной формы груди.</small>}
      </>}
    </div>}
    {feature.part === 'vagina' && <Button selected={feature.womb} onClick={() => set('womb')(String(!feature.womb))}>
      Матка: {feature.womb ? 'есть' : 'нет'}
    </Button>}
    {feature.part === 'butt' && <Button selected={feature.anus} onClick={() => set('anus')(String(!feature.anus))}>
      Анус: {feature.anus ? 'есть' : 'нет'}
    </Button>}
    {feature.part === 'butt' && feature.anus && anus && <label className="CharacterEditor__clothingFit">
      <input type="checkbox" checked={anus.showThroughClothing !== false}
        onChange={event => act('body-feature', { part: 'anus', field: 'showThroughClothing', value: String(event.target.checked) })} /> Анус: Деформировать одежду
    </label>}
    {feature.part === 'penis' && testicles && <BodyFeatureEditor feature={testicles} features={features} skinColor={skinColor} act={act} />}
  </details>;
};

export const CharacterEditor = () => {
  const { data, act } = useBackend();
  const [traitQuery, setTraitQuery] = useState('');
  const [showUnavailableTraits, setShowUnavailableTraits] = useState(false);
  const departments = useMemo(() => (data.departments || []).map(department => ({ ...department,
    jobs: department.jobs.map(job => ({ ...job, ...data.jobState?.[job.id] })) })), [data.departments, data.jobState]);
  const traits = useMemo(() => (data.traits || []).map(trait => ({ ...trait, ...data.traitState?.[trait.id] }))
    .filter(trait => (showUnavailableTraits || trait.selected || trait.available)
      && (trait.name + ' ' + trait.description).toLocaleLowerCase('ru').includes(traitQuery.toLocaleLowerCase('ru'))),
    [data.traits, data.traitState, showUnavailableTraits, traitQuery]);
  const backgroundRoot = useRef(null);
  const previewTarget = useRef(null);
  useEffect(() => {
    let previous = false, frame;
    const check = () => {
      cancelAnimationFrame(frame);
      frame = requestAnimationFrame(() => {
        const tooltip = document.querySelector('html > .Tooltip');
        const preview = previewTarget.current || document.querySelector('.CharacterEditor__floor');
        const a = tooltip?.getBoundingClientRect(), b = preview?.getBoundingClientRect();
        const hidden = !!(a && b && Number(getComputedStyle(tooltip).opacity) > 0 && a.width && a.height
          && a.left < b.right && a.right > b.left && a.top < b.bottom && a.bottom > b.top);
        if (hidden !== previous) { previous = hidden; act('preview-occluded', { value: String(hidden) }); }
      });
    };
    const observer = new MutationObserver(check);
    observer.observe(document.documentElement, { childList: true, subtree: true, attributes: true, attributeFilter: ['style', 'data-popper-placement'] });
    window.addEventListener('resize', check);
    document.addEventListener('scroll', check, true);
    document.addEventListener('transitionend', check, true);
    check();
    return () => { observer.disconnect(); cancelAnimationFrame(frame); window.removeEventListener('resize', check); document.removeEventListener('scroll', check, true); document.removeEventListener('transitionend', check, true); if (previous) act('preview-occluded', { value: 'false' }); };
  }, [act, data.mode]);
  useEffect(() => {
    if (data.mode !== 'markings' || !previewTarget.current) return;
    let frame;
    let previous = '';
    const publish = () => {
      cancelAnimationFrame(frame);
      frame = requestAnimationFrame(() => {
        const rect = previewTarget.current?.getBoundingClientRect();
        if (!rect) return;
        const bounds = { x: Math.round(rect.x), y: Math.round(rect.y), width: Math.round(rect.width), height: Math.round(rect.height) };
        const key = JSON.stringify(bounds);
        if (key !== previous) { previous = key; act('preview-bounds', bounds); }
      });
    };
    const observer = new ResizeObserver(publish);
    observer.observe(backgroundRoot.current);
    observer.observe(previewTarget.current);
    document.addEventListener('scroll', publish, true);
    window.addEventListener('resize', publish);
    publish();
    return () => { observer.disconnect(); cancelAnimationFrame(frame); document.removeEventListener('scroll', publish, true); window.removeEventListener('resize', publish); };
  }, [data.mode, data.available]);
  const activeBackground = useRef(null);
  const appearance = data.appearance || {};
  useEffect(() => {
    if (data.available && backgroundRoot.current) {
      activeBackground.current = backgroundRoot.current;
      configureNativeChatBackground(backgroundRoot.current, backgroundRoot.current,
        appearance.chatBgAnimation, appearance.chatBgAnimOpacity);
    } else {
      clearNativeChatBackground(activeBackground.current);
      activeBackground.current = null;
    }
  }, [data.available, appearance.chatBgAnimation, appearance.chatBgAnimOpacity,
    appearance.chatBgColor, appearance.theme]);
  useEffect(() => {
    return () => clearNativeChatBackground(activeBackground.current);
  }, []);
  const equipmentCache=useRef(null);
  if(data.equipment)equipmentCache.current=data.equipment;
  const [search, setSearch] = useState('');
  const [charactersOpen,setCharactersOpen]=useState(false),[statsOpen,setStatsOpen]=useState(false),[deleteSlot,setDeleteSlot]=useState(null);
  const [confirmClear, setConfirmClear] = useState(false);
  const [tattooPanelOpen, setTattooPanelOpen] = useState(false);
  const [lightPreview, setLightPreview] = useState(false);
  const togglePreviewBackground = () => setLightPreview(light => !light);
  const [colorTransfer, setColorTransfer] = useState({ phase: 'off', color: null });
  useEffect(() => {
    const cancel = event => {
      if (event.key === 'Escape') setColorTransfer(current => ({ ...current, phase: 'off' }));
    };
    document.addEventListener('keydown', cancel);
    return () => document.removeEventListener('keydown', cancel);
  }, []);
  useEffect(() => setColorTransfer({ phase: 'off', color: null }), [data.available, data.setup?.selected]);
  const transfer = {
    ...colorTransfer,
    useSquare: (value, onChange) => {
      if (colorTransfer.phase === 'pick') setColorTransfer({ phase: 'apply', color: value });
      else if (colorTransfer.phase === 'apply') {
        onChange(colorTransfer.color);
        setColorTransfer(current => ({ ...current, phase: 'off' }));
      }
    },
  };
  const pipetteHint = colorTransfer.phase === 'pick' ? 'Нажмите квадрат, цвет которого нужно скопировать. Esc — отмена.'
    : colorTransfer.phase === 'apply' ? 'Перейдите на нужную вкладку и нажмите квадрат, в который нужно вставить цвет. Esc — отмена.'
    : 'Пипетка: скопировать цвет одного квадрата в другой, в том числе на другой вкладке.';
  const change = action => value => act(action, { value });
  if (!data.available) return <div className="CharacterEditor"><header className="CharacterEditor__header">
    <div className="CharacterEditor__windowTitle">Редактор персонажа</div>
    {data.setup && <div className="CharacterEditor__headerActions"><Button icon="times" onClick={() => act('setup/close')}>Закрыть</Button></div>}
    </header>
    Выберите персонажа.</div>;
  // DeepLagoon: fuzzy search; one list per search, so the typo rule counts all jobs (or all markings) together
  const matchedJobs = new Set(fuzzyFilter(departments.flatMap(department => department.jobs), search, job => job.name));
  const theme = playerTheme(appearance);
  return <EditorColorDialogProvider data={data} act={act}><ColorTransferContext.Provider value={transfer}><div ref={backgroundRoot} className={'CharacterEditor Chat ' + theme.className + ' CharacterEditor--' + data.mode +
    (appearance.smoothScroll ? ' CharacterEditor--smooth' : '') + (appearance.hoverEffect ? ' CharacterEditor--hover' : '')}
    style={{ ...theme.style, ...editorAppearance(appearance) }}>
    <header className="CharacterEditor__header">
      <div className="CharacterEditor__windowTitle">Редактор персонажа</div>
      <div className="CharacterEditor__headerActions">
        <Button icon="eye-dropper" role="button" aria-pressed={colorTransfer.phase !== 'off'} selected={colorTransfer.phase !== 'off'} tooltip={pipetteHint} aria-label={pipetteHint}
          onClick={() => setColorTransfer(current => ({ ...current, phase: current.phase === 'off' ? 'pick' : 'off' }))}>
          {colorTransfer.color && <span className="CharacterEditor__pipetteColor" style={{ backgroundColor: colorTransfer.color }} />}
          <span className="CharacterEditor__pipetteLabel" aria-live="polite">{colorTransfer.phase === 'pick' ? 'Взять цвет' : colorTransfer.phase === 'apply' ? 'Вставить цвет' : 'Пипетка'}</span>
        </Button>
        <Button role="button" aria-disabled={!data.dirty || data.traitsValid === false} disabled={!data.dirty || data.traitsValid === false} color="good" onClick={()=>act('save')}>Сохранить</Button>
        <Button disabled={!data.dirty} onClick={()=>act('reset')}>Сбросить</Button>
        <Button icon={data.maximized ? "compress" : "expand"} onClick={() => act("window-maximize", { value: !data.maximized })}>{data.maximized ? "Свернуть" : "Развернуть"}</Button>
        {data.setup && <Button icon="times" onClick={()=>act('setup/close')}>Закрыть</Button>}
      </div>
    </header>
    {data.mode !== 'markings' && <aside className="CharacterEditor__preview" onWheel={event=>{if(event.target.closest('input[type=range]'))return;const bounds=event.currentTarget.getBoundingClientRect();if(event.clientX<bounds.left+64||event.clientX>bounds.right-64)return;event.preventDefault();act('rotate',{value:event.deltaY>0?1:-1});}}><div className="CharacterEditor__previewSpace" />
      <div className="CharacterEditor__floor" style={{ backgroundColor: lightPreview ? '#dce4ec' : '#262e3b', backgroundBlendMode: 'soft-light', backgroundImage: data.previewFloor ? `url("${data.previewFloor}")` : undefined }} />
      <div className="CharacterEditor__sizeRail CharacterEditor__sizeRail--height"><SizeSlider label="Рост" value={data.height} onChange={change('height')} vertical /></div>
      <div className="CharacterEditor__sizeRail CharacterEditor__sizeRail--width"><SizeSlider label="Ширина" value={data.width} onChange={change('width')} /></div>
      <div className="CharacterEditor__previewControls">
        <div className="CharacterEditor__rotation"><Button onClick={()=>act('rotate',{value:-1})}>◀</Button><Button onClick={()=>act('rotate',{value:1})}>▶</Button><Button selected={data.showClothes} onClick={()=>act('clothes')}>Show</Button><PreviewBackgroundButton light={lightPreview} onToggle={togglePreviewBackground} /></div>
        <PreviewArousalButton data={data} act={act} />
      </div>
      <div className="CharacterEditor__slots">{(data.previewSlots||[]).map((slot,index)=><button type="button" key={slot.id} title={slot.name} style={{gridColumn:index%2?3:1,gridRow:Math.floor(index/2)+1}} aria-label={slot.name} aria-pressed={slot.selected} onClick={()=>act('preview-slot',{value:slot.id})} onContextMenu={event=>{event.preventDefault();act('preview-slot-remove',{value:slot.id});}}><div className="CharacterEditor__sprite">{(slot.images.length?slot.images:[{url:slot.background,color:"#ffffff"}]).map((image,i)=><TintedSprite key={i} image={image.url} color={image.color} />)}</div></button>)}</div>
    </aside>}
    <main className="CharacterEditor__content">
    {data.setup && <>
      <div className="CharacterEditor__tabs"><Button onClick={()=>setCharactersOpen(!charactersOpen)} selected={charactersOpen}>Персонажи</Button><Button onClick={()=>setStatsOpen(!statsOpen)} selected={statsOpen}>Статистика</Button>{data.setup.notes&&<Button onClick={()=>act('setup/notes')}>Заметки администрации</Button>}</div>
      {charactersOpen&&<Section title="Персонажи"><div className="CharacterEditor__traits">{data.setup.characters.map(character=><div key={character.slot}><Button fluid selected={character.slot===data.setup.selected} onClick={()=>act('setup/select',{slot:character.slot})}>{character.name} · {character.balance}</Button>{character.slot!==data.setup.selected&&<Button color="bad" onClick={()=>{if(deleteSlot===character.slot){act('setup/delete',{slot:character.slot});setDeleteSlot(null);}else setDeleteSlot(character.slot);}}>{deleteSlot===character.slot?'Подтвердить удаление':'Удалить'}</Button>}</div>)}</div><Button disabled={!data.setup.canCreate} onClick={()=>act('setup/create')}>Создать персонажа</Button>{deleteSlot!==null&&<Button onClick={()=>setDeleteSlot(null)}>Отмена</Button>}</Section>}
      {statsOpen&&<Section title={'Общее время: '+data.setup.overallTime}><table><thead><tr><th>Профессия</th><th>Время</th></tr></thead><tbody>{data.setup.playtimes.map(time=><tr key={time.name}><td>{time.name}</td><td>{time.time}</td></tr>)}</tbody></table></Section>}
    </>}
    <nav className="CharacterEditor__tabs CharacterEditor__editorTabs" role="tablist" aria-label="Редактор персонажа">{(data.tabs || []).map(tab=><button type="button" role="tab" aria-selected={tab.mode===data.mode} key={tab.id} onClick={()=>act('select-tab',{value:tab.id})}>{tab.name}</button>)}</nav>
    {data.mode === 'appearance' && <div className="CharacterEditor__identity">
      <Button selected={data.showClothes} onClick={()=>act('clothes')}>Одежда</Button>
      <Button onClick={()=>act('random-all')}>Случайный персонаж</Button>
      <Button onClick={()=>act('import')}>Импорт</Button><Button onClick={()=>act('export')}>Экспорт</Button>
    </div>}
    {data.mode === 'settings' && <CharacterEditorSettings settings={appearance} act={act} />}
    {data.mode === 'identity' && <>
      <Button fluid selected={data.showClothes} onClick={() => act('clothes')}>Показывать одежду</Button>
      <Section fitted>
        <Button fluid onClick={() => act('random-all')}>Случайный персонаж</Button>
        <small className="CharacterEditor__warning">Имя должно соответствовать правилам именования выбранной расы.</small>
      </Section>
      <div className="CharacterEditor__buttons">
        <Button fluid onClick={() => act('import')}>Импорт</Button>
        <Button fluid onClick={() => act('export')}>Экспорт</Button>
      </div>
    </>}
    {data.mode === 'appearance' && <>
      <div className="CharacterEditor__nameRow">
        <DraftInput label="Имя персонажа" value={data.name} onCommit={change('name')} />
        <Button icon="dice" tooltip="Случайное имя" aria-label="Случайное имя" onClick={()=>act('random-name')} />
      </div>
      <div className="CharacterEditor__grid">
        <div className="CharacterEditor__speciesRow"><Select label="Раса" value={data.species} options={data.speciesOptions} onChange={change('species')} />
          <Button role="button" className="CharacterEditor__speciesGuide" icon="book" aria-label="Справка о расе" onClick={() => act('species-guide')}>Справка о расе</Button></div>
        <DraftInput label="Возраст" type="number" min={data.minAge} max={data.maxAge} value={data.age} onCommit={change('age')} />
        <Select label="Пол" value={data.sex} options={data.sexOptions} onChange={change('sex')} />
        <Select label="Местоимения" value={data.gender} options={data.genderOptions} onChange={change('gender')} />
        <Select label="Эротические взаимодействия" value={data.erp} options={consentOptions} onChange={change('erp')} />
        <Select label="Взаимодействия без согласия" value={data.noncon} options={consentOptions} onChange={change('noncon')} />
        <Select label="Vore" value={data.vore} options={consentOptions} onChange={change('vore')} />
        <Select label="Приоритет появления" value={data.spawn} options={data.spawnOptions} onChange={change('spawn')} />
      </div>
        <details className="CharacterEditor__physiology"><summary>Возбуждение, жидкости и физиология</summary>
          <p>Рост от жидкостей и визуальная беременность отключены по умолчанию. Изменение тела также требует разрешения в настройках химических изменений. Беременность изменяет только живот и описание при осмотре.</p>
          <BlueMoonPreferences options={data.physiology} act={act} />
        </details>
      {data.canUsePlantigradeLegs && <label className="CharacterEditor__legShape">
        <input type="checkbox" checked={!!data.plantigradeLegs}
          onChange={event => change('plantigrade-legs')(event.target.checked)} /> Прямые ноги
      </label>}
      <ColorEditor label="Цвет кожи" value={data.skinColor} onChange={change('skin-color')} />
      <details className="CharacterEditor__bodyFeatures">
        <summary>Части тела</summary>
        <small>Настройки независимы от пола. Размер выбирает спрайт. Изменения сохраняются кнопкой «Сохранить».</small>
        {(data.bodyFeatures || []).filter(feature => !['anus', 'testicles'].includes(feature.part)).map(feature =>
          <BodyFeatureEditor key={feature.part} feature={feature} features={data.bodyFeatures} skinColor={data.skinColor} act={act} />)}
      </details>
      <div className="CharacterEditor__hair">
        {data.hairOptions?.length <= 1 && data.beardOptions?.length <= 1 && <small>Для этой расы нет обычных причёсок и бороды. Доступные украшения головы настраиваются во вкладке «Особенности».</small>}
        {['hair', 'beard'].map((key, index) => {
          if ((data[key + 'Options'] || []).length <= 1) return null;
          const locked = data[key + 'Locked'];
          const missing = data[key + 'Style'] === (index ? 'FacialHairShaved' : 'HairBald');
          return <Section key={key} title={index ? 'Борода и усы' : 'Причёска'}>
            <details><summary>{index ? 'Выбор бороды и усов' : 'Выбор причёски'}</summary><HairCarousel act={act} options={data[key+'Options']||[]} selected={data[key+'Style']} color={locked ? data.skinColor : data[key+'Color']} face={data.hairPreviewImage} beard={!!index} onSelect={change(key+'-style')} /></details>
            <ColorEditor label={index ? 'Цвет бороды' : 'Цвет волос'} value={locked ? data.skinColor : data[key + 'Color']}
              disabled={locked || missing} onChange={change(key + '-color')} />
          </Section>;
        })}
      </div>
      <ColorEditor label="Цвет глаз" value={data.eyeColor} onChange={change('eye-color')} />
    </>}
    {data.mode === 'jobs' && <DraftInput label="Поиск" value={search} onCommit={setSearch} />}
    {data.mode === 'jobs' && <>
      <Select label="Если предпочтения недоступны" value={data.unavailable}
        options={[{ id: '0', name: 'Остаться в лобби' }, { id: '1', name: 'Выбрать запасную профессию' }]} onChange={change('unavailable')} />
      {departments.map(department => <Section key={department.id} title={department.name}>
        {department.jobs.filter(job => matchedJobs.has(job)).map(job => <div key={job.id} className="CharacterEditor__job">
          <div title={job.description}><strong>{job.name}</strong>{!job.allowed && <small>{job.reason}</small>}</div>
          <Select label={'Приоритет: ' + job.name} value={job.priority} options={priorities} disabled={!job.allowed}
            onChange={value => act('job', { id: job.id, value: Number(value) })} />
          <Button icon="book" tooltip="Справка" onClick={() => act('job-guide', { id: job.id })} />
        </div>)}
      </Section>)}
    </>}
    {data.mode === 'traits' && <>
      <Section title="Черты персонажа">
        <div role="status" className="CharacterEditor__points">
          Осталось очков: {data.traitBudget?.points ?? 0} · Слоты: {data.traitBudget?.slots ?? 0} / {data.traitBudget?.maxSlots ?? 0}
        </div>
        {!data.traitBudget?.valid && <div role="alert">{data.traitBudget?.reasons?.map(reason => <p key={reason}>{reason}</p>)}</div>}
        <input type="search" aria-label="Поиск черт" placeholder="Поиск черт…" value={traitQuery} onChange={event => setTraitQuery(event.target.value)} />
        <Button role="button" aria-pressed={showUnavailableTraits} selected={showUnavailableTraits} onClick={() => setShowUnavailableTraits(!showUnavailableTraits)}>Показать недоступные</Button>
        <Button role="button" color="bad" onClick={() => { if (confirmClear) { act('clear-traits'); setConfirmClear(false); } else setConfirmClear(true); }}>
          {confirmClear ? 'Подтвердить удаление всех черт' : 'Очистить все черты'}</Button>
        {confirmClear && <Button role="button" onClick={() => setConfirmClear(false)}>Отмена</Button>}
      </Section>
      {[...new Set(traits.map(trait => trait.category))].map(category => <Section key={category} title={category}>
        <div className="CharacterEditor__traits">{traits.filter(trait => trait.category === category).map(trait =>
          <Button role="button" fluid key={trait.id} selected={trait.selected} color={trait.selected && !trait.available ? 'bad' : undefined}
            tooltip={[trait.description, trait.reason].filter(Boolean).join('\n')} aria-disabled={!trait.selected && !trait.canSelect} disabled={!trait.selected && !trait.canSelect}
            onClick={() => act('trait', { id: trait.id })}>
            {trait.name} ({trait.points > 0 ? '+' : ''}{trait.points}; слоты: {trait.slots})
          </Button>)}</div>
      </Section>)}
      {!traits.length && <p role="status">Черты не найдены</p>}
    </>}
    {data.mode === 'saved' && <Section title="Сохранённые предметы"><div className="CharacterEditor__tiles">{(data.savedItems||[]).map((item,i)=><div key={i}><div className="CharacterEditor__sprite">{item.images.map((image,j)=><TintedSprite key={j} image={image.url} color={image.color} />)}</div>{item.name}</div>)}</div></Section>}
    {equipmentCache.current && <div style={{display:data.mode==='equipment'?'block':'none'}}><EquipmentBrowser data={equipmentCache.current} act={(action,payload)=>act('equipment/'+action,payload)} /></div>}
    {data.mode === 'flavor' && <>
      <CharacterHeadshots key={data.setup?.selected} data={data} act={act} />
      {[['flavor','Описание персонажа',data.flavorText],['ooc','OOC заметки',data.oocNotes]].map(([key,label,value])=><Section key={key} title={label}><CharacterTextEditor key={data.setup?.selected} label={label} value={value||''} maxLength={data.maxFlavorLength} onChange={change(key)} /><details><summary>Предпросмотр форматирования</summary><div className="CharacterText"><CharacterText text={value}/></div></details></Section>)}
    </>}
    {data.mode === 'markings' && <>
      <CharacterAppearance data={data} act={act} previewRef={previewTarget} lightPreview={lightPreview} togglePreviewBackground={togglePreviewBackground}
        tattooPanelOpen={tattooPanelOpen} setTattooPanelOpen={setTattooPanelOpen} />
    </>}
    {data.mode === 'company' && <Section title="Компания"><div className="CharacterEditor__companies">{(data.companyOptions||[]).map(company=><article key={company.id}>
      <header><strong>{company.name}</strong><Button selected={company.id===data.company} onClick={()=>act('company',{value:company.id})}>{company.id===data.company?'Выбрана':'Выбрать'}</Button></header>
      <details><summary>Описание компании</summary>{company.image&&<img src={company.image} alt={company.name}/>}<div dangerouslySetInnerHTML={{__html:company.descriptionHtml||''}}/>
      <Button disabled={!company.wikiUrl} onClick={()=>act('company-wiki',{value:company.id})}>Страница компании на вики</Button>{!company.wikiUrl&&<small>Страница пока не добавлена.</small>}</details>
    </article>)}</div></Section>}
    </main>
  </div></ColorTransferContext.Provider></EditorColorDialogProvider>;
};
