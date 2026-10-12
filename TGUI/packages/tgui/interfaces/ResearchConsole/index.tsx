import {
  type CSSProperties,
  type SyntheticEvent,
  useCallback,
  useDeferredValue,
  useEffect,
  useMemo,
  useRef,
  useState,
} from 'react';

import { sendMessage, useBackend } from '../../backend';
import { playerTheme } from '../../components/PlayerTheme';
import { DetailsPanel, PANEL_STRIP_WIDTH, PANEL_WIDTH } from './DetailsPanel';
import {
  buildDependents,
  buildStateMap,
  buildTechs,
  type TechState,
  type WireData,
} from './model';
import './ResearchConsole.scss';
import { buildSearchIndex, searchTechs } from './search';
import { type CameraCommand, type CameraTarget, TechTree } from './TechTree';
import { Tips } from './Tips';
import { TopBar } from './TopBar';
import { useResearchFx } from './useResearchFx';
import { buildTree } from './tree';

// The same research request is repeated only after the technology states change, or after this
// time if the server never answered.
const REQUEST_REPEAT_MS = 1000;

const NO_IDS: string[] = [];

type Request = { id: string; states: Map<string, TechState>; at: number };

// Left mouse button drags are reserved for panning, so the browser must not
// start text selection or image dragging. Text fields keep their behavior.
const suppressNativeDrag = (event: SyntheticEvent) => {
  if (!(event.target as Element).closest('input, textarea')) {
    event.preventDefault();
  }
};

const sameStates = (a: Map<string, TechState>, b: Map<string, TechState>) =>
  a.size === b.size && [...a].every(([id, state]) => b.get(id) === state);

export const ResearchConsole = () => {
  const { data, act } = useBackend<WireData>();
  const { disciplines, techs: wireTechs, labels, states } = data;
  const [selectedId, setSelectedId] = useState<string | null>(null);

  const techs = useMemo(() => buildTechs(wireTechs ?? []), [wireTechs]);
  const techById = useMemo(
    () => new Map(techs.map((tech) => [tech.id, tech])),
    [techs],
  );
  const tree = useMemo(() => buildTree(techs), [techs]);
  const dependents = useMemo(() => buildDependents(techs), [techs]);
  const disciplineById = useMemo(
    () => new Map((disciplines ?? []).map((entry) => [entry.id, entry])),
    [disciplines],
  );
  const techIdsByDiscipline = useMemo(() => {
    const groups = new Map<string, string[]>();
    for (const tech of techs) {
      groups.set(tech.discipline, [...(groups.get(tech.discipline) ?? []), tech.id]);
    }
    return groups;
  }, [techs]);
  const disciplineColors = useMemo(
    () => new Map((disciplines ?? []).map((entry) => [entry.id, entry.color])),
    [disciplines],
  );

  // Points change often; the state map keeps its identity while the states themselves do not.
  const previousStates = useRef<Map<string, TechState>>(undefined);
  const stateById = useMemo(() => {
    const next = buildStateMap(states ?? []);
    if (previousStates.current && sameStates(previousStates.current, next)) {
      return previousStates.current;
    }
    previousStates.current = next;
    return next;
  }, [states]);

  const fx = useResearchFx(tree, stateById);

  const [panelCollapsed, setPanelCollapsed] = useState(false);
  const [cameraCommand, setCameraCommand] = useState<CameraCommand | null>(null);
  const [activeDiscipline, setActiveDiscipline] = useState<string | null>(null);
  const [query, setQuery] = useState('');

  const selectedTech = selectedId ? techById.get(selectedId) : undefined;
  const selectedHubs = useMemo(
    () => new Set(selectedId ? tree.hubParents.get(selectedId) : undefined),
    [tree, selectedId],
  );
  // The results follow the typing a little behind it, so that typing itself stays quick.
  const searchIndex = useMemo(() => buildSearchIndex(techs), [techs]);
  const searchQuery = useDeferredValue(query);
  const results = useMemo(() => searchTechs(searchIndex, searchQuery), [searchIndex, searchQuery]);
  // While a search has results, the technologies it found stay bright; otherwise the tab's discipline.
  const highlight = useMemo(() => {
    if (results.length > 0) {
      return new Set(results.map((result) => result.id));
    }
    return activeDiscipline ? new Set(techIdsByDiscipline.get(activeDiscipline)) : null;
  }, [results, activeDiscipline, techIdsByDiscipline]);
  // The panel lies over the right edge of the tree: the toolbar moves aside for it, and the camera
  // works with what is left free.
  const panelWidth = selectedTech ? (panelCollapsed ? PANEL_STRIP_WIDTH : PANEL_WIDTH) : 0;

  // The first click on a technology selects it, the second one on the same technology researches
  // it. The callbacks read the latest values from a ref so that the nodes keep their props.
  const [attention, setAttention] = useState(0);
  const lastRequest = useRef<Request | null>(null);
  const latest = useRef({ selectedId, query, stateById, hasAccess: data.hasAccess === true });
  useEffect(() => {
    latest.current = { selectedId, query, stateById, hasAccess: data.hasAccess === true };
  });

  // The selection is also written to the ref at once: a second click that arrives before React has
  // rendered the first one must see the selected technology, or it would select it again.
  const select = useCallback((id: string | null) => {
    latest.current.selectedId = id;
    setSelectedId(id);
    setAttention(0);
  }, []);

  // False when the technology cannot be researched now.
  const research = useCallback(
    (id: string) => {
      const { stateById: states, hasAccess } = latest.current;
      if (states.get(id) !== 'available' || !hasAccess) {
        return false;
      }
      const now = performance.now();
      const last = lastRequest.current;
      if (!last || last.id !== id || last.states !== states || now - last.at >= REQUEST_REPEAT_MS) {
        lastRequest.current = { id, states, at: now };
        act('research', { id });
      }
      return true;
    },
    [act],
  );

  const togglePanel = useCallback(() => setPanelCollapsed((collapsed) => !collapsed), []);

  const moveCamera = useCallback((target: CameraTarget) => {
    setCameraCommand((previous) => ({ serial: (previous?.serial ?? 0) + 1, target }));
  }, []);

  // A click on a technology that is hidden under the panel or off the free view moves the camera.
  const onActivate = useCallback(
    (id: string) => {
      if (id !== latest.current.selectedId) {
        select(id);
        moveCamera({ kind: 'reveal', id });
      } else if (!research(id)) {
        setAttention((count) => count + 1);
      }
    },
    [moveCamera, research, select],
  );

  // Selecting a technology from the panel or from a search also brings it into view.
  const navigate = useCallback(
    (id: string) => {
      select(id);
      moveCamera({ kind: 'node', id });
    },
    [moveCamera, select],
  );

  const pickResult = useCallback(
    (id: string) => {
      setQuery('');
      navigate(id);
    },
    [navigate],
  );

  // A tab narrows the tree to a discipline and brings it into view; the active one clears it.
  const onTab = useCallback(
    (id: string | null) => {
      setActiveDiscipline(id);
      if (id) {
        moveCamera({ kind: 'group', ids: techIdsByDiscipline.get(id) ?? [] });
      }
    },
    [moveCamera, techIdsByDiscipline],
  );

  useEffect(() => {
    // Escape steps back: the search text, then the selection, then the window. The embedded browser
    // takes the key from the game while it has the focus, so closing is asked for here.
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key !== 'Escape' || event.isComposing) {
        return;
      }
      if (latest.current.query !== '') {
        setQuery('');
      } else if (latest.current.selectedId) {
        select(null);
      } else {
        sendMessage({ type: 'close' });
      }
    };
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [select]);

  const theme = playerTheme(data.chatAppearance);
  const rootClass = `ResearchConsole ${theme.className}`;

  if (!labels || !wireTechs) {
    return <div className={rootClass} style={theme.style} />;
  }

  return (
    <div
      className={rootClass}
      style={theme.style}
      onMouseDown={suppressNativeDrag}
      onDragStart={suppressNativeDrag}
    >
      <TopBar
        points={data.points ?? 0}
        disciplines={disciplines ?? []}
        activeDiscipline={activeDiscipline}
        query={query}
        results={results}
        techs={techById}
        disciplineColors={disciplineColors}
        labels={labels}
        onDiscipline={onTab}
        onQuery={setQuery}
        onPick={pickResult}
        onServers={() => act('servers')}
      />
      {stateById.size === 0 && (
        <div className="ResearchConsole__notice">{labels['dl-research-no-server']}</div>
      )}
      <div
        className="ResearchConsole__body"
        style={{ '--rc-inset': `${panelWidth}px` } as CSSProperties}
      >
        <TechTree
          tree={tree}
          techs={techById}
          disciplineColors={disciplineColors}
          states={stateById}
          selectedId={selectedId}
          attention={attention}
          points={data.points ?? 0}
          fx={fx}
          canResearch={data.hasAccess === true}
          highlight={highlight}
          labels={labels}
          insetRight={panelWidth}
          command={cameraCommand}
          onSelect={select}
          onActivate={onActivate}
        />
        {selectedTech && (
          <DetailsPanel
            tech={selectedTech}
            state={stateById.get(selectedTech.id) ?? 'locked'}
            discipline={disciplineById.get(selectedTech.discipline)}
            points={data.points ?? 0}
            canResearch={data.hasAccess === true}
            techs={techById}
            states={stateById}
            disciplineColors={disciplineColors}
            dependents={dependents.get(selectedTech.id) ?? NO_IDS}
            hubs={selectedHubs}
            collapsed={panelCollapsed}
            labels={labels}
            onToggle={togglePanel}
            onNavigate={navigate}
            onResearch={research}
          />
        )}
      </div>
      <Tips />
    </div>
  );
};
