export type TechState = 'researched' | 'available' | 'unaffordable' | 'locked';

export type IconLayer = { url: string; color: string };

export type Prerequisite = { id: string; name: string };

export type Recipe = { id: string; name: string };

export type WireTech = {
  id: string;
  name: string;
  discipline: string;
  tier: number;
  cost: number;
  prerequisites: Prerequisite[];
  recipes: Recipe[];
  /** Other things researching it does, in words. */
  effects: { text: string }[];
};

export type WireDiscipline = {
  id: string;
  name: string;
  shortName: string;
  color: string;
  icon: string;
};

export type WireData = {
  labels?: Record<string, string>;
  disciplines?: WireDiscipline[];
  techs?: WireTech[];
  points?: number;
  hasAccess?: boolean;
  states?: { id: string; state: TechState }[];
  iconBatch?: { icons: { id: string; layers: IconLayer[] }[] };
  /** The chat panel appearance (TguiPanel), for playerTheme. */
  chatAppearance?: { theme?: string; chatBgColor?: string; chatTextColor?: string };
};

export type Tech = Omit<WireTech, 'prerequisites'> & {
  prerequisites: string[];
  /** The same prerequisites with their names, also those the server does not offer. */
  requires: Prerequisite[];
};

export const buildTechs = (techs: WireTech[]): Tech[] =>
  techs.map((tech) => ({
    ...tech,
    prerequisites: tech.prerequisites.map((prerequisite) => prerequisite.id),
    requires: tech.prerequisites,
  }));

/** For each technology the technologies that list it as a prerequisite. */
export const buildDependents = (techs: Tech[]): Map<string, string[]> => {
  const dependents = new Map<string, string[]>();
  for (const tech of techs) {
    for (const prerequisite of tech.prerequisites) {
      dependents.set(prerequisite, [...(dependents.get(prerequisite) ?? []), tech.id]);
    }
  }
  return dependents;
};

export const buildStateMap = (
  states: NonNullable<WireData['states']>,
): Map<string, TechState> =>
  new Map(states.map((entry) => [entry.id, entry.state]));
