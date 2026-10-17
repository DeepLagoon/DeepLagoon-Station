import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import { useBackend } from '../backend';
import { Icon } from '../components';
import './GhostRoles.scss';

type Role = { id: string; kind: number; allowed: boolean; reason: string; request: string; preview: 'entity' | 'prototype' | 'unknown' };
type Group = { name: string; description: string; roles: Role[] };
type Data = { groups?: Group[]; labels?: Record<string, string> };
const NO_GROUPS: Group[] = [];

/** Sprites are rendered by the engine over these slots, including clothing masks and live appearance. */
const Preview = ({ role, large = false, label }: { role: Role; large?: boolean; label: string }) => (
  <span className={`GhostRoles__preview${large ? ' GhostRoles__preview--large' : ''}`} data-role-preview={role.id} role="img" aria-label={label}>
    {role.preview === 'unknown' && <Icon name="user" aria-hidden="true" />}
  </span>
);

export const GhostRoles = () => {
  const { data, act } = useBackend<Data>();
  const groups = data.groups ?? NO_GROUPS;
  const t = (key: string) => data.labels?.[key] ?? '';
  // Store concrete role IDs. The selected group survives reordering and remains selected
  // when its first member is taken, while disappearing roles fall back immediately.
  const [selection, setSelection] = useState<string[]>([]);
  const group = groups.find(entry => entry.roles.some(role => selection.includes(role.id))) ?? groups[0];
  const [character, setCharacter] = useState<string | null>(null);
  const selected = group?.roles.find(role => role.id === character) ?? group?.roles[0];
  useEffect(() => {
    if (group && !group.roles.some(role => selection.includes(role.id))) setSelection(group.roles.map(role => role.id));
  }, [group]);
  const root = useRef<HTMLDivElement>(null);
  const actRef = useRef(act);
  actRef.current = act;
  const reportLayout = useRef<() => void>(() => {});

  useEffect(() => {
    const element = root.current;
    if (!element) return;
    let frame = 0, last = '';
    const report = () => {
      frame = 0;
      // The native host covers the whole browser viewport, not this component's
      // intrinsic content box. Use viewport coordinates on both sides of the bridge.
      const width = window.innerWidth, height = window.innerHeight;
      const content = element.getBoundingClientRect();
      if (!width || !height || !content.width || !content.height) return;
      const slots: string[] = [];
      const previews = [
        ...element.querySelectorAll<HTMLElement>('.GhostRoles__details [data-role-preview]'),
        ...element.querySelectorAll<HTMLElement>('.GhostRoles__list [data-role-preview]'),
      ];
      for (const node of previews) {
        if (slots.length === 32) break;
        const box = node.getBoundingClientRect();
        const clip = node.closest('.GhostRoles__list, .GhostRoles__details')?.getBoundingClientRect() ?? content;
        // Partly clipped thumbnails must not paint across scroll boundaries or buttons.
        if (box.width < 8 || box.height < 8 || box.left < Math.max(0, clip.left) || box.top < Math.max(0, clip.top) ||
            box.right > Math.min(width, clip.right) + 0.5 || box.bottom > Math.min(height, clip.bottom) + 0.5) continue;
        slots.push([node.dataset.rolePreview, box.left, box.top, box.width, box.height]
          .map(value => typeof value === 'number' ? Math.round(value * 10) / 10 : value).join(','));
      }
      const payload = { layout: slots.join(';'), width, height };
      const key = JSON.stringify(payload);
      if (key === last) return;
      last = key;
      actRef.current('preview-layout', payload);
    };
    const schedule = () => { if (!frame) frame = requestAnimationFrame(report); };
    reportLayout.current = schedule;
    const observer = new ResizeObserver(schedule);
    observer.observe(element);
    element.addEventListener('scroll', schedule, true);
    window.addEventListener('resize', schedule);
    schedule();
    return () => {
      observer.disconnect();
      element.removeEventListener('scroll', schedule, true);
      window.removeEventListener('resize', schedule);
      cancelAnimationFrame(frame);
      actRef.current('preview-layout', { layout: '', width: 1, height: 1 });
    };
  }, []);
  useLayoutEffect(() => { reportLayout.current(); }, [groups, group, selected]);

  return (
    <div className="GhostRoles" ref={root}>
      <div className="GhostRoles__summary">
        <span>{t('groups')}: {groups.length} · {t('roles')}: {groups.reduce((sum, entry) => sum + entry.roles.length, 0)}</span>
        <span>{t('select')}</span>
      </div>
      {!group || !selected ? <div className="GhostRoles__empty" role="status">{t('empty')}</div> : (
        <div className="GhostRoles__body">
          <nav className="GhostRoles__list" aria-label={t('select')}>
            {groups.map(entry => {
              const first = entry.roles[0];
              const blocked = entry.roles.every(role => !role.allowed);
              return (
                <button key={first.id} type="button" className={`GhostRoles__choice${entry === group ? ' is-selected' : ''}`}
                  aria-pressed={entry === group} onClick={() => { setSelection(entry.roles.map(role => role.id)); setCharacter(first.id); }}>
                  <Preview role={first} label={entry.name} />
                  <span className="GhostRoles__choice-copy"><strong>{entry.name}</strong>
                    <span className="GhostRoles__count">{t('available')}: {entry.roles.length}</span>
                    {blocked && <span className="GhostRoles__locked"><Icon name="lock" aria-hidden="true" /> {t('locked')}</span>}
                  </span>
                </button>
              );
            })}
          </nav>
          <section className="GhostRoles__details" aria-label={group.name}>
            <div className="GhostRoles__portrait"><Preview role={selected} large label={group.name} /><h3>{group.name}</h3></div>
            <div className="GhostRoles__description">{group.description}</div>
            {selected.preview !== 'entity' && <p className="GhostRoles__preview-note">{t(selected.preview === 'prototype' ? 'preview-spawner' : 'preview-unknown')}</p>}
            <div className="GhostRoles__available">{t('available')}: {group.roles.length}</div>
            <div className="GhostRoles__instances">
              {group.roles.map((role, index) => (
                <article key={role.id} className="GhostRoles__instance">
                  <button type="button" className="GhostRoles__character" aria-pressed={role.id === selected.id}
                    onClick={() => setCharacter(role.id)} aria-label={`${t('instance')} ${index + 1}: ${group.name}`}>
                    <Preview role={role} label={group.name} /><span>{t('instance')} {index + 1}</span>
                  </button>
                  {!role.allowed && <p className="GhostRoles__reason"><Icon name="lock" aria-hidden="true" /> {role.reason || t('locked')}</p>}
                  <div className="GhostRoles__actions">
                    <button type="button" className={`GhostRoles__request${role.kind === 3 ? ' GhostRoles__request--joined' : ''}`}
                      disabled={!role.allowed} onClick={() => act('request', { id: role.id })}>{!role.allowed && <Icon name="lock" aria-hidden="true" />} {role.request}</button>
                    <button type="button" className="GhostRoles__follow" onClick={() => act('follow', { id: role.id })}><Icon name="eye" aria-hidden="true" /> {t('follow')}</button>
                  </div>
                </article>
              ))}
            </div>
          </section>
        </div>
      )}
    </div>
  );
};
