import { useEffect, useState } from 'react';
import { useBackend } from '../backend';
import { Icon } from '../components';
import { filter } from './fuzzySearch';
import { InteractionTargetDoll } from './InteractionTargetDoll';
import './InteractionPanel.scss';

type Option = { id: string; name: string };
type Action = { id: string; name: string; body: string; category: number; available: boolean; favorite: boolean; repeating: boolean };
type Custom = { id: string; title: string; template: string; body: string; category: number; flags: number; color: string; sound: string };
type Data = {
  name: string; self: boolean; humanoid: boolean; debug: boolean; interval: number; status: string;
  labels: Record<string, string>; regions: Option[]; categories: { id: number; name: string }[];
  actions: Action[]; custom: Custom[]; sounds: (Option & { category: string })[];
  libraryLoaded: boolean; waiting: boolean; libraryStatus: string;
  libraryRevision: number; librarySaved: boolean;
  targetDoll: { base: string; parts: Record<string, string> };
  bodyFeatures: { part: string; name: string; visibility: number }[];
};
const categoryIcons = ['heart', 'bolt', 'utensils', 'hand'];

export const InteractionPanel = () => {
  const { data, act } = useBackend<Data>();
  const t = (key: string) => data.labels?.[key] || key;
  const actions = data.actions || [];
  const [tab, setTab] = useState('actions');
  const [query, setQuery] = useState('');
  const [region, setRegion] = useState('all');
  const [favoritesOnly, setFavoritesOnly] = useState(false);
  const [availableOnly, setAvailableOnly] = useState(false);
  const [collapsed, setCollapsed] = useState<Record<string, boolean>>({});
  const [bodyMap, setBodyMap] = useState(false);
  const [interval, setInterval] = useState(data.interval || 3);
  useEffect(() => setInterval(data.interval), [data.interval]);
  const focus = () => act('focus');
  const filtered = filter(actions.filter(a => (!data.humanoid || region === 'all' || a.body === region)
    && (!favoritesOnly || a.favorite) && (!availableOnly || a.available)), query, a => a.name,
    a => (data.regions?.find(r => r.id === a.body)?.name || '') + ' ' + (data.categories?.find(c => c.id === a.category)?.name || ''));
  const groups = [
    { id: 'favorites', name: t('favorites'), items: filtered.filter(a => a.favorite) },
    ...(data.regions || []).map(r => ({ ...r, items: filtered.filter(a => !a.favorite && (!data.humanoid || a.body === r.id)) }))
      .filter(r => data.humanoid || r.id === 'all'),
  ].filter(g => g.items.length);
  const repeating = actions.some(a => a.repeating);
  const [help, setHelp] = useState(false);

  return (
    <div className="InteractionPanel">
      <header className="InteractionPanel__title">
        <Icon aria-hidden="true" name="hand" /><strong>{t('open')}</strong>
        <button className="InteractionPanel__icon" title={t('compact-help')} aria-label={t('compact-help')}
          onClick={() => setHelp(!help)}><Icon aria-hidden="true" name="circle-question" /></button>
        <button className="InteractionPanel__icon InteractionPanel__close" title={t('compact-close')} aria-label={t('compact-close')}
          onClick={() => act('close')}><Icon aria-hidden="true" name="times" /></button>
      </header>
      <section className="InteractionPanel__target">
        <div className="InteractionPanel__portrait" aria-label={data.name} />
        <div className="InteractionPanel__identity"><small>{t(data.self ? 'self-mode' : 'compact-target')}</small>
          <strong title={data.name}>{data.name}</strong>
          <span>{actions.filter(a => a.available).length} {t('compact-available')} · {actions.length} {t('compact-all')}</span>
        </div>
      </section>
      {data.debug && <div className="InteractionPanel__notice">{t('debug')}</div>}
      {help && <div className="InteractionPanel__notice">{t('action-hint')}</div>}
      <nav className="InteractionPanel__tabs" aria-label={t('open')}>
        <button aria-pressed={tab === 'actions'} onClick={() => setTab('actions')}><Icon aria-hidden="true" name="hand" /> {t('tab-actions')}</button>
        <button aria-pressed={tab === 'custom'} onClick={() => setTab('custom')}><Icon aria-hidden="true" name="pen" /> {t('tab-custom')}</button>
        <button aria-pressed={tab === 'body'} onClick={() => setTab('body')}><Icon aria-hidden="true" name="person" /> {t('tab-body')}</button>
      </nav>
      {tab === 'actions' ? <>
        <div className="InteractionPanel__search"><Icon aria-hidden="true" name="search" />
          <input aria-label={t('search')} placeholder={t('search')} value={query} onFocus={focus}
            onChange={e => { setQuery(e.target.value); setCollapsed({}); }} />
          {!!query && <button className="InteractionPanel__icon" title={t('clear')} aria-label={t('clear')}
            onClick={() => setQuery('')}><Icon aria-hidden="true" name="times" /></button>}
        </div>
        <div className="InteractionPanel__bodyPicker">
        <div className="InteractionPanel__filters">
          {data.humanoid && <><button className="InteractionPanel__icon" aria-expanded={bodyMap} title={t('editor-region')}
            aria-label={t('editor-region')} onClick={() => setBodyMap(!bodyMap)}><Icon aria-hidden="true" name="person" /></button>
            <span className="InteractionPanel__selectedRegion">{data.regions?.find(r => r.id === region)?.name}</span>
            {region !== 'all' && <button className="InteractionPanel__icon" aria-label={t('body-all')} title={t('body-all')}
              onClick={() => { setRegion('all'); setCollapsed({}); }}><Icon aria-hidden="true" name="times" /></button>}</>}
          <button aria-pressed={favoritesOnly} title={t('favorites')} onClick={() => { setFavoritesOnly(!favoritesOnly); setCollapsed({}); }}>
            <Icon aria-hidden="true" name="star" /> {t('favorites')}</button>
          <button className="InteractionPanel__icon" aria-pressed={availableOnly} title={t('compact-available-only')}
            aria-label={t('compact-available-only')} onClick={() => setAvailableOnly(!availableOnly)}><Icon aria-hidden="true" name="lock-open" /></button>
        </div>
        {bodyMap && data.humanoid && <div className="InteractionPanel__bodyMap">
          <InteractionTargetDoll images={data.targetDoll} region={region} label={t('editor-region')}
            regionName={id => data.regions?.find(r => r.id === id)?.name || id}
            onSelect={id => { setRegion(id); setCollapsed({}); }} />
          <div className="InteractionPanel__bodyHint"><strong>{data.regions?.find(r => r.id === region)?.name}</strong>
            <span>{t('compact-body-hint')}</span></div>
        </div>}
        </div>
        <div className="InteractionPanel__listTools"><span>{filtered.length} {t('compact-found')}</span>
          <button title={t('compact-expand')} aria-label={t('compact-expand')}
            onClick={() => setCollapsed({})}><Icon aria-hidden="true" name="angles-down" /></button>
          <button title={t('compact-collapse')} aria-label={t('compact-collapse')}
            onClick={() => { setQuery(''); setFavoritesOnly(false); setRegion('all');
              setCollapsed(Object.fromEntries(['favorites', ...(data.regions || []).map(r => r.id)].map(id => [id, true]))); }}><Icon aria-hidden="true" name="angles-up" /></button>
        </div>
        <main className="InteractionPanel__actions">
          {!groups.length && <p className="InteractionPanel__empty">{t('empty')}</p>}
          {groups.map(group => {
            const open = !collapsed[group.id];
            return <section className="InteractionPanel__group" key={group.id}>
              <button className="InteractionPanel__groupTitle" aria-expanded={open}
                onClick={() => setCollapsed({ ...collapsed, [group.id]: open })}>
                <Icon aria-hidden="true" name={open ? 'chevron-down' : 'chevron-right'} />
                {group.id === 'favorites' && <Icon aria-hidden="true" name="star" />}<strong>{group.name}</strong><span>{group.items.length}</span>
              </button>
              {open && group.items.map(a => <div key={a.id} className={'InteractionPanel__row InteractionPanel__row--' + a.category + (a.repeating ? ' is-repeating' : '')}>
                <button className="InteractionPanel__run" disabled={!a.available} title={t(a.available ? 'action-hint' : 'compact-locked')}
                  onClick={() => act('run', { id: a.id })}>
                  <span className="InteractionPanel__actionIcon" title={data.categories?.find(c => c.id === a.category)?.name}><Icon aria-hidden="true" name={a.available ? categoryIcons[a.category] : 'lock'} /></span>
                  <span>{a.name}</span>
                </button>
                <button className="InteractionPanel__icon" aria-pressed={a.repeating} disabled={!a.available}
                  title={t(a.repeating ? 'stop' : 'repeat')} aria-label={t(a.repeating ? 'stop' : 'repeat') + ': ' + a.name}
                  onClick={() => act('repeat', { id: a.id })}><Icon aria-hidden="true" name={a.repeating ? 'stop' : 'repeat'} /></button>
                <button className="InteractionPanel__icon InteractionPanel__favorite" aria-pressed={a.favorite}
                  title={t('favorite-toggle')} aria-label={t('favorite-toggle') + ': ' + a.name}
                  onClick={() => act('favorite', { id: a.id })}><Icon aria-hidden="true" name="star" /></button>
              </div>)}
            </section>;
          })}
        </main>
        <footer className="InteractionPanel__footer">
          <div className="InteractionPanel__repeat"><label htmlFor="interaction-interval">{t('speed')}</label>
            <input id="interaction-interval" type="range" min="1" max="10" step="0.1" value={interval}
              onChange={e => setInterval(Number(e.target.value))}
              onPointerUp={e => act('interval', { value: Number(e.currentTarget.value) })}
              onKeyUp={e => act('interval', { value: Number(e.currentTarget.value) })} />
            <output>{interval.toFixed(1)} {t('compact-seconds')}</output>
            <button disabled={!repeating} onClick={() => act('stop')}><Icon aria-hidden="true" name="stop" /> {t('stop')}</button>
          </div><div className="InteractionPanel__status" role="status" title={data.status}>{data.status}</div>
        </footer>
      </> : null}
      <div className="InteractionPanel__customPage" hidden={tab !== 'custom'}>
        <CustomEditor data={data} act={act} t={t} focus={focus} />
      </div>
      {tab === 'body' && <main className="InteractionPanel__bodySettings">
        <p>{t('body-own')}</p>
        {!(data.bodyFeatures || []).length && <p className="InteractionPanel__empty">{t('body-empty')}</p>}
        {(data.bodyFeatures || []).map(feature => <label key={feature.part}>
          <span>{feature.name}</span>
          <select aria-label={feature.name} value={feature.visibility}
            onChange={event => act('body-visibility', { id: feature.part, value: Number(event.target.value) })}>
            <option value={0}>{t('body-hidden')}</option>
            <option value={1}>{t('body-under')}</option>
            <option value={2}>{t('body-over')}</option>
          </select>
        </label>)}
      </main>}
    </div>
  );
};

const CustomEditor = ({ data, act, t, focus }: { data: Data; act: (action: string, payload?: object) => void; t: (key: string) => string; focus: () => void }) => {
  const [draft, setDraft] = useState<Custom | null>(null);
  const [deleting, setDeleting] = useState('');
  const [sentRevision, setSentRevision] = useState<number | null>(null);
  const [soundCategory, setSoundCategory] = useState('');
  useEffect(() => {
    if (sentRevision !== null && data.libraryRevision > sentRevision) {
      setSentRevision(null);
      if (data.librarySaved) setDraft(null);
    }
  }, [data.libraryRevision, sentRevision]);
  const busy = !data.libraryLoaded || data.waiting;
  const patch = (change: Partial<Custom>) => setDraft(draft && { ...draft, ...change });
  const error = draft && validationError(draft);
  const preview = draft?.template.replace(/\$(you|target)\b/g, (_, key) => t(key === 'you' ? 'editor-you' : 'editor-target'));
  const soundGroups = [...new Set(data.sounds?.map(s => s.category) || [])];
  return <div className="InteractionPanel__custom">
    <div className="InteractionPanel__customTools">
      {draft ? <button disabled={busy} onClick={() => setDraft(null)}><Icon aria-hidden="true" name="arrow-left" /> {t('back')}</button>
        : <button disabled={busy || data.custom?.length >= 32} title={data.custom?.length >= 32 ? t('library-full') : t('editor-new')}
          onClick={() => { setSoundCategory(''); setDraft({ id: '', title: '', template: t('editor-example'), body: 'all', category: 3, flags: 6, color: '#C6E8DA', sound: '' }); }}>
          <Icon aria-hidden="true" name="plus" /> {t('editor-new')}</button>}
      <span>{data.custom?.length || 0} / 32</span>
    </div>
    <div className="InteractionPanel__customScroll">
      {draft ? <form onSubmit={e => { e.preventDefault(); if (!busy && !error) { setSentRevision(data.libraryRevision); act('save', { ...draft }); } }}>
        <fieldset disabled={busy}>
          <label>{t('editor-title')}<input value={draft.title} maxLength={48} onFocus={focus} onChange={e => patch({ title: e.target.value })} /></label>
          <label>{t('editor-template')}<input value={draft.template} maxLength={280} onFocus={focus} onChange={e => patch({ template: e.target.value })} /></label>
          <div className="InteractionPanel__tokens">{['$you', '$target'].map(token => <button type="button" key={token}
            onClick={() => patch({ template: draft.template + ' ' + token })}>{token}</button>)}</div>
          <div className="InteractionPanel__preview" style={{ color: /^#[0-9a-f]{6}$/i.test(draft.color) ? draft.color : undefined }}>
            <small>{t('compact-preview')}</small>{preview}
          </div>
          <label>{t('editor-preference')}<select value={draft.category} onChange={e => patch({ category: Number(e.target.value) })}>
            {data.categories?.map(c => <option key={c.id} value={c.id}>{c.name}</option>)}</select></label>
          <label>{t('editor-region')}<select value={draft.body} onChange={e => patch({ body: e.target.value })}>
            {data.regions?.map(r => <option key={r.id} value={r.id}>{r.name}</option>)}</select></label>
          <div className="InteractionPanel__flags">{['editor-self', 'editor-other', 'editor-humanoid', 'editor-animal'].map((key, i) => <label key={key}>
            <input type="checkbox" checked={!!(draft.flags & (1 << i))}
              onChange={e => patch({ flags: e.target.checked ? draft.flags | (1 << i) : draft.flags & ~(1 << i) })} />{t(key)}</label>)}</div>
          <label>{t('editor-color')}<input value={draft.color} maxLength={7} onFocus={focus} onChange={e => patch({ color: e.target.value })} /></label>
          <label>{t('editor-sound')}<select aria-label={t('editor-sound')} value={soundCategory} onChange={e => setSoundCategory(e.target.value)}>
            <option value="">{t('sounds-all')}</option>{soundGroups.map(group => <option key={group}>{group}</option>)}</select>
            <select value={draft.sound} onChange={e => patch({ sound: e.target.value })}>
              <option value="">{t('sound-none')}</option>
              {data.sounds?.filter(s => !soundCategory || s.category === soundCategory || s.id === draft.sound)
                .map(s => <option key={s.id} value={s.id}>{s.category} · {s.name}</option>)}
            </select></label>
        </fieldset>
        {error && <p className="InteractionPanel__error" role="alert">{t(error)}</p>}
        <button className="InteractionPanel__save" type="submit" disabled={busy || !!error}><Icon aria-hidden="true" name="check" /> {t('editor-save')}</button>
      </form> : <>
        {!data.custom?.length && <p className="InteractionPanel__empty">{t(data.libraryLoaded ? 'my-list-empty' : 'library-busy')}</p>}
        {data.custom?.map(a => <article className="InteractionPanel__libraryItem" key={a.id}>
          <div><strong>{a.title}</strong><small>{a.template}</small></div>
          {deleting === a.id ? <div className="InteractionPanel__deleteConfirm"><span>{t('compact-delete-confirm')}</span>
            <button disabled={busy} onClick={() => { act('delete', { id: a.id }); setDeleting(''); }}>{t('editor-delete')}</button>
            <button onClick={() => setDeleting('')}>{t('compact-cancel')}</button></div> : <>
            <button className="InteractionPanel__icon" disabled={busy} title={t('edit')} aria-label={t('edit') + ': ' + a.title}
              onClick={() => { setSoundCategory(''); setDraft({ ...a }); }}><Icon aria-hidden="true" name="pen" /></button>
            <button className="InteractionPanel__icon" disabled={busy} title={t('editor-delete')} aria-label={t('editor-delete') + ': ' + a.title}
              onClick={() => setDeleting(a.id)}><Icon aria-hidden="true" name="trash" /></button></>}
        </article>)}
      </>}
      <details className="InteractionPanel__help"><summary>{t('compact-help')}</summary>{t('editor-help')}</details>
    </div>
    <div className="InteractionPanel__status" role="status">{data.waiting ? t('library-busy') : data.libraryStatus}</div>
  </div>;
};

function validationError(draft: Custom): string | null {
  const title = draft.title.trim(), template = draft.template.trim();
  if (!title) return 'validation-title-required';
  if (title.length > 48) return 'validation-title-long';
  if (!template.startsWith('$you ')) return 'validation-template-start';
  if (template.length > 280) return 'validation-template-long';
  if (template.replace(/\$(you|target)\b/g, '').includes('$')) return 'validation-tokens';
  if (/[\p{Cc}\p{Cf}]/u.test(title + template)) return 'validation-hidden';
  if (!(draft.flags & 12)) return 'validation-targets';
  if (!(draft.flags & 3)) return 'validation-actors';
  if (!/^#[0-9a-f]{6}$/i.test(draft.color.trim())) return 'validation-color';
  return null;
}
