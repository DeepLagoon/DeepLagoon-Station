// Inventory active Computer prototypes and their primary BUI, following inheritance.
// This is a rollout aid; it never rewrites prototypes or assigns a new interface.
const fs = require('node:fs');
const path = require('node:path');
const yaml = require('js-yaml');
const repo = path.resolve(__dirname, '../..');
const target = path.join(repo, 'Docs/DeepLagoon/ConsoleConcepts/civilian-rollout.json');
const walk = (dir, suffix) => fs.readdirSync(dir, {withFileTypes:true}).flatMap(entry =>
  entry.isDirectory() ? walk(path.join(dir, entry.name), suffix) : entry.name.endsWith(suffix) ? [path.join(dir, entry.name)] : []);
const relative = file => path.relative(repo, file).replaceAll('\\', '/');
const schema = yaml.DEFAULT_SCHEMA.extend(['mapping', 'sequence', 'scalar'].map(kind =>
  new yaml.Type('!type:', {kind, multi:true, construct:data=>data})));
const entities = new Map(), warnings = [];
for (const file of walk(path.join(repo, 'Resources/Prototypes'), '.yml')) {
  try {
    yaml.loadAll(fs.readFileSync(file, 'utf8'), docs => {
      for (const entity of Array.isArray(docs) ? docs : []) {
        if (entity?.type !== 'entity' || !entity.id) continue;
        if (entities.has(entity.id)) warnings.push(`Duplicate prototype: ${entity.id}`);
        entities.set(entity.id, {...entity, file:relative(file)});
      }
    }, {schema});
  } catch (error) { warnings.push(`${relative(file)}: ${error.reason || error.message}`); }
}
const resolved = new Map();
function components(id, stack = new Set()) {
  if (resolved.has(id)) return resolved.get(id);
  if (stack.has(id)) { warnings.push(`Inheritance cycle: ${id}`); return new Map(); }
  const entity = entities.get(id);
  if (!entity) { warnings.push(`Unknown parent: ${id}`); return new Map(); }
  const next = new Set([...stack, id]), result = new Map();
  for (const parent of [].concat(entity.parent || []))
    for (const [type, value] of components(parent, next)) result.set(type, {...result.get(type), ...value});
  for (const component of entity.components || []) {
    const previous = result.get(component.type) || {};
    result.set(component.type, {...previous, ...component,
      ...(component.type === 'UserInterface' ? {interfaces:{...previous.interfaces, ...component.interfaces}} : {})});
  }
  resolved.set(id, result);
  return result;
}
const sources = new Map();
for (const file of walk(path.join(repo, 'Content.Client'), '.cs')) {
  const type = path.basename(file).split('.')[0];
  if (!sources.has(type)) sources.set(type, []);
  sources.get(type).push(file);
}
function hasTgui(type, seen = new Set()) {
  if (seen.has(type)) return false;
  seen.add(type);
  for (const file of sources.get(type) || []) {
    const code = fs.readFileSync(file, 'utf8');
    if (/TguiPanel|OpenTgui\(|CivilianConsoleHost|ShipRadarConsoleHost/.test(code)) return true;
    for (const match of code.matchAll(/(?:CreateWindow<|new\s+)(\w+)(?:>|\()/g))
      if (hasTgui(match[1], seen)) return true;
  }
  return /TguiBoundUserInterface/.test(type);
}
const completed = new Set(['ResearchConsoleTguiBoundUserInterface', 'DiskConsoleBoundUserInterface', 'PointDiskConsoleBoundUserInterface']);
const ship = new Set(['ShuttleConsoleBoundUserInterface','FireControlConsoleBoundUserInterface','RadarConsoleBoundUserInterface']);
const rows = [];
for (const [id, entity] of entities) {
  if (entity.abstract) continue;
  const merged = components(id);
  if (!merged.has('Computer')) continue;
  const interfaces = merged.get('UserInterface')?.interfaces || {};
  const key = merged.get('ActivatableUI')?.key;
  const primary = key && interfaces[key]?.type;
  if (!primary) continue;
  rows.push({prototype:id, file:entity.file, controller:primary,
    hasTgui:hasTgui(primary),
    rollout:completed.has(primary) ? 'civilian-first-wave' : ship.has(primary) ? 'ship-steel' : 'pending-style-review'});
}
const groups = [...new Set(rows.map(row=>row.controller))].sort().map(controller => ({
  controller, hasTgui:rows.find(row=>row.controller===controller).hasTgui,
  rollout:rows.find(row=>row.controller===controller).rollout,
  prototypes:rows.filter(row=>row.controller===controller).map(row=>row.prototype),
  sources:(sources.get(controller)||[]).map(relative),
}));
const report = {scope:'Non-abstract entity prototypes inheriting Computer, with a primary ActivatableUI binding',
  notes:['hasTgui is inferred from the active BUI and referenced window sources; verify each controller before migration.',
    'Auxiliary UI bindings and devices without Computer are outside this inventory.',
    'The user selected the ledger layout for disk terminals. Research keeps its technology tree inside the approved civilian shell.',
    'The legacy DiskConsoleMenu is also ported but has no active primary Computer prototype in this checkout.'],
  counts:{prototypes:rows.length,controllers:groups.length,controllersWithTgui:groups.filter(row=>row.hasTgui).length},
  controllers:groups, prototypes:rows.sort((a,b)=>a.prototype.localeCompare(b.prototype)), warnings:[...new Set(warnings)]};
fs.mkdirSync(path.dirname(target), {recursive:true});
fs.writeFileSync(target, JSON.stringify(report,null,2)+'\n');
console.log(JSON.stringify({file:relative(target),...report.counts,warnings:report.warnings.length,
  firstWave:groups.filter(group=>group.rollout==='civilian-first-wave').map(group=>group.controller)}));
