import type { IconLayer } from '../tguiIcons';

export type { IconLayer };

export type WireMaterial = {
  id: string;
  name: string;
  /** Volume of one sheet; costs and stock are volumes. */
  sheet: number;
  color: string;
  /** A data URI. */
  icon: string;
};

export type Requirement = { id: string; name: string; n: number };

export type WireRecipe = {
  id: string;
  name: string;
  desc: string;
  /** Category ids, comma separated. */
  cats: string;
  /** Seconds to print one, with the machine's multipliers (the server decides the real value). */
  time: number;
  /** How many things one print gives. */
  count: number;
  /** Costs, already adjusted by the machine's multiplier. */
  mats: { id: string; n: number }[];
  ents?: Requirement[];
  reagents?: Requirement[];
  yields?: { id: string; n: number }[];
};

export type StockEntry = { id: string; name: string; text: string; n: number };

export type QueueEntry = {
  index: number;
  id: string;
  name: string;
  printed: number;
  requested: number;
};

export type Current = {
  id?: string;
  name?: string;
  /** False while the recipe waits (no materials, no power, too hot). */
  active?: boolean;
  /** Changes with every print; the progress bar restarts on it. */
  key?: string;
  total?: number;
  elapsed?: number;
};

export type WireData = {
  labels?: Record<string, string>;
  materialMultiplier?: number;
  timeMultiplier?: number;
  materials?: WireMaterial[];
  categories?: { id: string; name: string }[];
  recipes?: WireRecipe[];

  name?: string;
  looping?: boolean;
  skipping?: boolean;
  canEject?: boolean;
  silo?: boolean;
  servers?: boolean;
  defaultAmount?: number;
  stock?: StockEntry[];
  reserved?: { id: string; n: number }[];
  entityStock?: { id: string; n: number }[];
  reagentStock?: { id: string; n: number }[];
  queue?: QueueEntry[];
  current?: Current;
  /** How the print is shown; not sent by the host (the page picks `estimate`), the test stand sets it. */
  progressMode?: 'estimate' | 'indeterminate';

  /** The chat panel appearance (TguiPanel), for playerTheme. */
  chatAppearance?: { theme?: string; chatBgColor?: string; chatTextColor?: string };
};

export type Recipe = Omit<WireRecipe, 'cats' | 'ents' | 'reagents' | 'yields'> & {
  cats: string[];
  ents: Requirement[];
  reagents: Requirement[];
  yields: { id: string; n: number }[];
};

const collator = new Intl.Collator(undefined, { sensitivity: 'base', numeric: true });

export const compareNames = (a: string, b: string) => collator.compare(a, b);

/** The recipes in name order, with the optional lists filled in. */
export const buildRecipes = (recipes: WireRecipe[]): Recipe[] =>
  recipes
    .map((recipe) => ({
      ...recipe,
      cats: recipe.cats === '' ? [] : recipe.cats.split(','),
      ents: recipe.ents ?? [],
      reagents: recipe.reagents ?? [],
      yields: recipe.yields ?? [],
    }))
    .sort((a, b) => compareNames(a.name, b.name));

export const NO_CATEGORY = '';

/** The amount shown for a volume: sheets, with at most two decimals. */
export const formatSheets = (volume: number, sheet: number, separator: string): string =>
  formatNumber(volume / Math.max(1, sheet), separator);

export const formatNumber = (value: number, separator: string): string => {
  const text = String(Math.round(value * 100) / 100);
  return separator === '.' ? text : text.replace('.', separator);
};
