import {
  type PointerEvent,
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
import { Tips } from '../ResearchConsole/Tips';
import { buildStock, statusOf } from './availability';
import { buildCostLayout } from './costs';
import { CostHeader } from './CostHeader';
import { buildItems, filterRecipes, type ListItem, ROW_HEIGHT } from './list';
import { Materials } from './Materials';
import { makePrinting } from './printing';
import {
  buildRecipes,
  compareNames,
  type Current,
  NO_CATEGORY,
  type Recipe,
  type WireData,
  type WireMaterial,
} from './model';
import { QueuePanel } from './QueuePanel';
import { detailsHeight, RecipeRow } from './RecipeRow';
import { makeT } from './text';
import { Categories, TopBar } from './TopBar';
import { VirtualList } from './VirtualList';
import './LatheConsole.scss';

const NO_AMOUNTS: Record<string, number> = {};
const NO_CURRENT: Current = {};

// Left mouse button drags belong to the window, so the browser must not start text selection or image
// dragging. Text fields keep their behavior.
const suppressNativeDrag = (event: SyntheticEvent) => {
  if (!(event.target as Element).closest('input, textarea')) {
    event.preventDefault();
  }
};

export const LatheConsole = () => {
  const { data, act } = useBackend<WireData>();
  const { labels } = data;
  const t = useMemo(() => makeT(labels ?? {}), [labels]);
  const separator = labels?.['dl-lathe-decimal-separator'] ?? '.';
  const defaultAmount = data.defaultAmount ?? 1;

  const recipes = useMemo(() => buildRecipes(data.recipes ?? []), [data.recipes]);
  const recipeById = useMemo(
    () => new Map(recipes.map((recipe) => [recipe.id, recipe])),
    [recipes],
  );
  const materials = useMemo(
    () => new Map((data.materials ?? []).map((material) => [material.id, material])),
    [data.materials],
  );
  const layout = useMemo(() => buildCostLayout(recipes, materials), [recipes, materials]);
  const categoryNames = useMemo(
    () => new Map((data.categories ?? []).map((category) => [category.id, category.name])),
    [data.categories],
  );
  const stock = useMemo(
    () => buildStock(data.stock, data.reserved, data.entityStock, data.reagentStock),
    [data.stock, data.reserved, data.entityStock, data.reagentStock],
  );

  const current = data.current ?? NO_CURRENT;
  // The page's clock for the print is taken when the print first appears; later states of the same print
  // leave it alone.
  const printing = useMemo(
    () => makePrinting(current),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [current.active, current.id, current.key],
  );
  const batch = current.id ? (data.queue ?? []).find((entry) => entry.id === current.id) : undefined;

  const [query, setQuery] = useState('');
  const [category, setCategory] = useState<string | null>(null);
  const [onlyAvailable, setOnlyAvailable] = useState(false);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [hoverId, setHoverId] = useState<string | null>(null);
  const [amounts, setAmounts] = useState<Record<string, number>>(NO_AMOUNTS);
  const [pulse, setPulse] = useState({ id: '', count: 0 });
  // A click on the printing block asks the list to show that row, which then blinks once.
  const [locate, setLocate] = useState({ id: '', n: 0, blinking: false });
  const searchInput = useRef<HTMLInputElement>(null);

  const amountOf = useCallback(
    (id: string) => amounts[id] ?? defaultAmount,
    [amounts, defaultAmount],
  );

  // The chips: the categories that have recipes here, and "other" for recipes without a category.
  const chips = useMemo(() => {
    const counts = new Map<string, number>();
    for (const recipe of recipes) {
      if (recipe.cats.length === 0) {
        counts.set(NO_CATEGORY, (counts.get(NO_CATEGORY) ?? 0) + 1);
      }
      for (const id of recipe.cats) {
        counts.set(id, (counts.get(id) ?? 0) + 1);
      }
    }
    return [...counts]
      .map(([id, count]) => ({
        id,
        count,
        name: id === NO_CATEGORY ? t('category-none') : (categoryNames.get(id) ?? id),
      }))
      .sort((a, b) =>
        a.id === NO_CATEGORY ? 1 : b.id === NO_CATEGORY ? -1 : compareNames(a.name, b.name),
      );
  }, [recipes, categoryNames, t]);

  // The results follow the typing a little behind it, so that typing itself stays quick.
  const searchQuery = useDeferredValue(query);
  const filterAmounts = onlyAvailable ? amounts : NO_AMOUNTS;
  const filtered = useMemo(
    () =>
      filterRecipes(
        recipes,
        category,
        searchQuery,
        onlyAvailable
          ? (recipe) => statusOf(recipe, filterAmounts[recipe.id] ?? defaultAmount, stock) === 'ok'
          : null,
      ),
    [recipes, category, searchQuery, onlyAvailable, filterAmounts, defaultAmount, stock],
  );

  const grouped = category === null && searchQuery.trim() === '';
  const noCategoryName = t('category-none');
  const items = useMemo(
    () =>
      buildItems(filtered, grouped, categoryNames, noCategoryName, (recipe) =>
        recipe.id === selectedId ? ROW_HEIGHT + detailsHeight(recipe) : ROW_HEIGHT,
      ),
    [filtered, grouped, categoryNames, noCategoryName, selectedId],
  );

  // What the recipe under the pointer, or else the selected one, would use of each material.
  const previewRecipe = recipeById.get(hoverId ?? selectedId ?? '');
  const preview = useMemo(() => {
    const result = new Map<string, number>();
    if (previewRecipe) {
      const amount = amountOf(previewRecipe.id);
      for (const material of previewRecipe.mats) {
        result.set(material.id, material.n * amount);
      }
    }
    return result;
  }, [previewRecipe, amountOf]);

  const reserved = useMemo(
    () => new Map((data.reserved ?? []).map((entry) => [entry.id, entry.n])),
    [data.reserved],
  );
  const materialStock = useMemo(
    () =>
      [...(data.stock ?? [])].sort((a, b) =>
        compareNames(materials.get(a.id)?.name ?? a.name, materials.get(b.id)?.name ?? b.name),
      ),
    [data.stock, materials],
  );
  const extras = useMemo(() => extrasOf(recipes, data, separator, t('unit-reagent')), [
    recipes,
    data,
    separator,
    t,
  ]);

  const filteredIds = useMemo(() => new Set(filtered.map((recipe) => recipe.id)), [filtered]);
  const onLocate = useCallback(() => {
    if (!current.id) {
      return;
    }
    // A search or a category that hides the row is dropped: the request is to see the row.
    if (!filteredIds.has(current.id)) {
      setQuery('');
      setCategory(null);
      setOnlyAvailable(false);
    }
    setLocate((previous) => ({ id: current.id ?? '', n: previous.n + 1, blinking: true }));
  }, [current.id, filteredIds]);
  const onFlashEnd = useCallback(
    () => setLocate((previous) => ({ ...previous, blinking: false })),
    [],
  );

  const latest = useRef({ query, selectedId });
  useEffect(() => {
    latest.current = { query, selectedId };
  });

  const select = useCallback((id: string) => {
    setSelectedId((current) => (current === id ? null : id));
  }, []);
  const setAmount = useCallback((id: string, amount: number) => {
    setAmounts((current) => (current[id] === amount ? current : { ...current, [id]: amount }));
  }, []);
  const queue = useCallback(
    (id: string) => {
      const recipe = recipeById.get(id);
      const amount = amountOf(id);
      if (!recipe || statusOf(recipe, amount, stock) !== 'ok') {
        return;
      }
      act('queue', { id, qty: amount });
      setPulse((current) => ({ id, count: current.count + 1 }));
    },
    [act, amountOf, recipeById, stock],
  );

  useEffect(() => {
    // Escape steps back: the search text, then the open recipe, then the window. The embedded browser
    // takes the key from the game while it has the focus, so closing is asked for here.
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key !== 'Escape' || event.isComposing) {
        return;
      }
      if (latest.current.query !== '') {
        setQuery('');
      } else if (latest.current.selectedId) {
        setSelectedId(null);
      } else {
        sendMessage({ type: 'close' });
      }
    };
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, []);

  const onPointerOver = useCallback((event: PointerEvent<HTMLDivElement>) => {
    setHoverId((event.target as Element).closest<HTMLElement>('[data-recipe]')?.dataset.recipe ?? null);
  }, []);
  const onPointerLeave = useCallback(() => setHoverId(null), []);

  const renderItem = useCallback(
    (item: ListItem) =>
      item.kind === 'header' ? (
        <div className="ListHeader">
          <span>{item.name}</span>
          <span className="ListHeader__count">{item.count}</span>
        </div>
      ) : (
        <RecipeRow
          recipe={item.recipe}
          amount={amountOf(item.recipe.id)}
          selected={item.recipe.id === selectedId}
          stock={stock}
          materials={materials}
          layout={layout}
          printing={printing?.id === item.recipe.id ? printing : null}
          batchPrinted={printing?.id === item.recipe.id ? (batch?.printed ?? 0) : 0}
          batchRequested={printing?.id === item.recipe.id ? (batch?.requested ?? 0) : 0}
          flash={locate.blinking && locate.id === item.recipe.id ? locate.n : 0}
          pulse={pulse.id === item.recipe.id ? pulse.count : 0}
          separator={separator}
          t={t}
          onSelect={select}
          onAmount={setAmount}
          onQueue={queue}
          onFlashEnd={onFlashEnd}
        />
      ),
    [
      amountOf,
      selectedId,
      stock,
      materials,
      layout,
      printing,
      batch?.printed,
      batch?.requested,
      locate,
      pulse,
      separator,
      t,
      select,
      setAmount,
      queue,
      onFlashEnd,
    ],
  );

  const theme = playerTheme(data.chatAppearance);
  const rootClass = `LatheConsole ${theme.className}`;

  if (!labels || !data.recipes) {
    return <div className={rootClass} style={theme.style} />;
  }

  return (
    <div className={rootClass} style={theme.style} onDragStart={suppressNativeDrag}>
      <TopBar
        query={query}
        input={searchInput}
        shown={filtered.length}
        total={recipes.length}
        servers={data.servers === true}
        t={t}
        onQuery={setQuery}
        onServers={() => act('servers')}
      />
      <Categories
        active={category}
        categories={chips}
        total={recipes.length}
        onlyAvailable={onlyAvailable}
        t={t}
        onSelect={setCategory}
        onToggleAvailable={() => setOnlyAvailable((value) => !value)}
      />
      <div className="LatheConsole__body">
        <div
          className={`LatheConsole__list${layout.compact ? ' LatheConsole__list--compact' : ''}`}
          onPointerOver={onPointerOver}
          onPointerLeave={onPointerLeave}
        >
          <CostHeader layout={layout} />
          {items.length === 0 && <div className="LatheConsole__empty">{t('search-empty')}</div>}
          <VirtualList
            items={items}
            renderItem={renderItem}
            resetKey={`${category}|${searchQuery}|${onlyAvailable}`}
            revealKey={selectedId}
            scrollTo={locate.n > 0 ? { key: locate.id, n: locate.n } : null}
          />
        </div>
        <aside className="LatheConsole__side">
          <Materials
            stock={materialStock}
            materials={materials as Map<string, WireMaterial>}
            reserved={reserved}
            preview={preview}
            canEject={data.canEject === true}
            silo={data.silo === true}
            extras={extras}
            materialMultiplier={data.materialMultiplier ?? 1}
            timeMultiplier={data.timeMultiplier ?? 1}
            separator={separator}
            t={t}
            onEject={(id, sheets) => act('eject', { id, sheets })}
          />
          <QueuePanel
            current={current}
            printing={printing}
            progressMode={data.progressMode === 'indeterminate' ? 'indeterminate' : 'estimate'}
            queue={data.queue ?? []}
            looping={data.looping === true}
            skipping={data.skipping === true}
            separator={separator}
            t={t}
            onCancel={(index) => act('cancel', { index })}
            onLoop={(value) => act('loop', { value: String(value) })}
            onSkip={(value) => act('skip', { value: String(value) })}
            onLocate={onLocate}
          />
        </aside>
      </div>
      <Tips />
    </div>
  );
};

/** What the machine's storage and beaker hold, for the things some recipe needs. */
const extrasOf = (
  recipes: Recipe[],
  data: WireData,
  separator: string,
  reagentUnit: string,
): { id: string; name: string; text: string }[] => {
  const names = new Map<string, string>();
  const reagentNames = new Map<string, string>();
  for (const recipe of recipes) {
    for (const entity of recipe.ents) {
      names.set(entity.id, entity.name);
    }
    for (const reagent of recipe.reagents) {
      reagentNames.set(reagent.id, reagent.name);
    }
  }
  const result: { id: string; name: string; text: string }[] = [];
  for (const entry of data.entityStock ?? []) {
    const name = names.get(entry.id);
    if (name) {
      result.push({ id: `entity:${entry.id}`, name, text: `×${entry.n}` });
    }
  }
  for (const entry of data.reagentStock ?? []) {
    const name = reagentNames.get(entry.id);
    if (name) {
      const value = String(Math.round(entry.n * 100) / 100);
      result.push({
        id: `reagent:${entry.id}`,
        name,
        text: `${separator === '.' ? value : value.replace('.', separator)} ${reagentUnit}`,
      });
    }
  }
  return result;
};
