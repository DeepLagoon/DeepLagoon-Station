// Prepare a frozen GridEditor plan without loading a game server or changing its layout.
const fs = require('node:fs');
const path = require('node:path');
const yaml = require(require.resolve('js-yaml', { paths: [path.join(__dirname, '../TGUI')] }));
const repo = path.resolve(__dirname, '..');
const [input, output] = process.argv.slice(2).map(p => path.resolve(p));
if (!input || !output || input === output) throw new Error('Use distinct input and output paths.');
const doc = yaml.load(fs.readFileSync(input, 'utf8'));
if (doc.meta?.format !== 7 || doc.grids?.length !== 1) throw new Error('Expected one format-7 grid.');
const root = doc.entities.flatMap(g => g.entities).find(e => e.uid === doc.grids[0]);
const component = (entity, type) => {
  let value = entity.components.find(c => c.type === type);
  if (!value) entity.components.push(value = { type });
  return value;
};
const floors = new Set();
for (const [key, chunk] of Object.entries(component(root, 'MapGrid').chunks)) {
  const [cx, cy] = key.split(',').map(Number), bytes = Buffer.from(chunk.tiles, 'base64');
  if (chunk.version !== 7 || bytes.length !== 16 * 16 * 7) throw new Error('Unexpected tile chunk.');
  for (let y = 0; y < 16; y++) for (let x = 0; x < 16; x++)
    if (doc.tilemap[bytes.readInt32LE((y * 16 + x) * 7)] !== 'Space') floors.add(`${cx * 16 + x},${cy * 16 + y}`);
}
const cell = entity => component(entity, 'Transform').pos.split(',').map(Number).map(Math.floor).join(',');
const all = doc.entities.flatMap(group => group.entities.map(entity => ({ entity, proto: group.proto })));
const byProto = id => all.filter(entry => entry.proto === id).map(entry => entry.entity);
const unique = id => {
  const values = byProto(id);
  if (values.length !== 1) throw new Error(`Expected one ${id}.`);
  return values[0];
};
const generator = unique(byProto('DebugGenerator').length ? 'DebugGenerator' : 'DLApartmentStopGenerator'), substation = unique('SubstationBasic');
const apc = unique('APCSuperCapacity'), smes = unique('SMESAdvanced');
for (const entity of [generator, substation, apc, smes]) component(entity, 'Transform').anchored = true;
const debugGenerator = doc.entities.find(group => group.proto === 'DebugGenerator');
if (debugGenerator) debugGenerator.proto = 'DLApartmentStopGenerator';
Object.assign(component(root, 'MetaData'), { name: 'Жилой квартал Lagoon' });
Object.assign(component(root, 'Gravity'), { enabled: true, inherent: true });
Object.assign(component(substation, 'Battery'), { maxCharge: 2500000, startingCharge: 2500000 });
Object.assign(component(substation, 'PowerNetworkBattery'), { maxChargeRate: 100000, maxSupply: 150000 });
Object.assign(component(apc, 'PowerNetworkBattery'), { maxChargeRate: 50000, maxSupply: 100000 });
Object.assign(component(smes, 'Battery'), { maxCharge: 16000000, startingCharge: 16000000 });

// Resolve inherited receiver components to wire actual consumers, not every decoration.
const tags = ['mapping', 'sequence', 'scalar'].map(kind => new yaml.Type('!type:', { kind, multi: true, construct: data => data }));
const schema = yaml.DEFAULT_SCHEMA.extend(tags), prototypes = new Map();
function readPrototypes(dir) {
  for (const file of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, file.name);
    if (file.isDirectory()) readPrototypes(full);
    else if (/\.ya?ml$/.test(file.name)) yaml.loadAll(fs.readFileSync(full, 'utf8'), values => {
      if (Array.isArray(values)) for (const value of values)
        if (value.type === 'entity') prototypes.set(value.id, value);
    }, { schema });
  }
}
readPrototypes(path.join(repo, 'Resources/Prototypes'));
const resolved = new Map();
function resolve(id, stack = new Set()) {
  if (resolved.has(id)) return resolved.get(id);
  if (stack.has(id) || !prototypes.has(id)) throw new Error(`Missing or cyclic entity prototype ${id}.`);
  const definition = prototypes.get(id), parts = new Map();
  const parents = definition.parent ? [].concat(definition.parent) : [];
  for (const parent of parents) for (const [type, value] of resolve(parent, new Set([...stack, id]))) parts.set(type, value);
  for (const value of definition.components || []) parts.set(value.type, { ...parts.get(value.type), ...value });
  resolved.set(id, parts);
  return parts;
}
const receivers = all.filter(entry => entry.proto && resolve(entry.proto).has('ApcPowerReceiver')).map(entry => entry.entity);

// Greedy shortest paths join each endpoint to its voltage network through existing floor only.
function network(origin, targets) {
  const connected = new Set([origin]);
  if (!floors.has(origin)) throw new Error(`Power source outside the floor: ${origin}`);
  for (const target of targets) {
    if (!floors.has(target)) throw new Error(`Power endpoint outside the floor: ${target}`);
    const queue = [target], previous = new Map([[target, null]]);
    let joined;
    for (let index = 0; index < queue.length; index++) {
      const current = queue[index];
      if (connected.has(current)) { joined = current; break; }
      const [x, y] = current.split(',').map(Number);
      for (const next of [`${x + 1},${y}`, `${x - 1},${y}`, `${x},${y + 1}`, `${x},${y - 1}`])
        if (floors.has(next) && !previous.has(next)) { previous.set(next, current); queue.push(next); }
    }
    if (!joined) throw new Error(`Disconnected power endpoint: ${target}`);
    for (let current = joined; current != null; current = previous.get(current)) connected.add(current);
  }
  return connected;
}
let nextUid = Math.max(...all.map(entry => entry.entity.uid));
function wires(proto, cells) {
  const existing = new Set(byProto(proto).map(cell));
  for (const entity of byProto(proto)) component(entity, 'Transform').anchored = true;
  const additions = [...cells].filter(key => !existing.has(key)).sort().map(key => {
    const [x, y] = key.split(',').map(Number);
    return { uid: ++nextUid, components: [{ type: 'Transform', parent: root.uid, pos: `${x + .5},${y + .5}`, anchored: true }] };
  });
  if (!additions.length) return;
  const group = doc.entities.find(group => group.proto === proto);
  if (group) group.entities.push(...additions);
  else doc.entities.push({ proto, entities: additions });
}
const hv = network(cell(generator), [cell(substation), cell(smes), ...byProto('CableHV').map(cell)]);
const mv = network(cell(substation), [cell(apc), ...byProto('CableMV').map(cell)]);
const lv = network(cell(apc), [...receivers.map(cell), ...byProto('CableApcExtension').map(cell)]);
wires('CableHV', hv); wires('CableMV', mv); wires('CableApcExtension', lv);

// Safe air follows the grid, including exposed floors beside the shuttle dock.
Object.assign(component(root, 'GridEnvironment'), { safeAtmosphere: true });
root.components = root.components.filter(part => part.type !== 'GridAtmosphere');
Object.assign(component(root, 'ProtectedGrid'), { preventFloorRemoval: true, preventFloorPlacement: true,
  preventRCDUse: true, preventEmpEvents: true, preventExplosions: true, preventArtifactTriggers: true, killHostileMobs: true });
Object.assign(component(root, 'PacifiedZoneGenerator'), { wholeGrid: true, disallowAllCombat: true });
component(root, 'ApartmentProtectedGrid');
doc.meta.entityCount = nextUid;
fs.writeFileSync(output, '# Frozen residential stop. Prepared from the original GridEditor export.\n' + yaml.dump(doc, { noRefs: true, lineWidth: -1 }));
process.stdout.write(JSON.stringify({ originalEntities: all.length, entities: nextUid, floorTiles: floors.size,
  receivers: receivers.length, hv: hv.size, mv: mv.size, lv: lv.size, atmosphereTiles: floors.size, output }) + '\n');
