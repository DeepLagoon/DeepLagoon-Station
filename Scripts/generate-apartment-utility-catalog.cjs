// Functional household objects, kept separate from the decorative catalogue generator.
const fs = require('node:fs'), path = require('node:path'), assert = require('node:assert/strict');
const root = path.resolve(__dirname, '..');
const yaml = require(path.join(root, 'TGUI/node_modules/js-yaml'));
const groups = {
  'Уборка и покраска': ['MopItem', 'AdvMopItem', 'Bucket', 'RagItem', 'SprayPainter', 'SprayBottle', 'SprayBottleWater', 'SprayBottleSpaceCleaner', 'MegaSprayBottle', 'Soap', 'SoapNT', 'SoapDeluxe', 'SoapHomemade', 'TrashBag', 'TrashBagBlue', 'WetFloorSign', 'Plunger'],
  'Инструменты': ['Screwdriver', 'Wrench', 'Crowbar', 'CrowbarRed', 'CrowbarGreen', 'CrowbarOrange', 'CrowbarYellow', 'Wirecutter', 'Multitool', 'Welder', 'WelderMini', 'PowerDrill', 'Shovel', 'ToolboxMechanical', 'ToolboxElectrical', 'ToolboxEmergency', 'ToolboxArtistic', 'HandLabeler', 'LightReplacer', 'FireExtinguisher'],
  'Мебель': ['Chair', 'ChairBlack', 'ChairWood', 'ChairOfficeLight', 'ChairOfficeDark', 'ChairFolding', 'Stool', 'StoolBar', 'ComfyChair', 'SteelBench', 'WoodenBench', 'BenchComfy', 'BenchRedComfy', 'BenchBlueComfy', 'Bed', 'DogBed', 'Mattress', 'PsychBed', 'Table', 'TableWood', 'TableGlass', 'TableReinforced', 'TableCounterMetal', 'TableCounterWood', 'TableCarpet'],
  'Освещение': ['Lamp', 'LampGold', 'Lantern', 'FlashlightLantern', 'LightBulb', 'LightBulbLed', 'LightTube', 'LightTubeLed', 'GlowstickRed', 'GlowstickPurple', 'GlowstickYellow', 'GlowstickBlue'],
  'Декор и растения': ['PottedPlant0', 'PottedPlant1', 'PottedPlant2', 'PottedPlant3', 'PottedPlant4', 'PottedPlant5', 'PottedPlant6', 'PottedPlant7', 'PottedPlant8', 'PottedPlant9', 'PottedPlant10', 'PottedPlant11', 'PottedPlant12', 'PottedPlant13', 'PottedPlant14', 'PottedPlant15', 'Carpet', 'CarpetBlack', 'CarpetPink', 'CarpetBlue', 'CarpetGreen', 'CarpetOrange', 'CarpetPurple', 'CarpetCyan', 'CarpetWhite', 'CurtainsBlack', 'CurtainsBlue', 'CurtainsCyan', 'CurtainsGreen', 'CurtainsOrange', 'CurtainsPink', 'CurtainsPurple', 'CurtainsRed', 'CurtainsWhite'],
  'Кухня и посуда': ['KitchenMicrowave', 'KitchenReagentGrinder', 'KitchenElectricGrill', 'KitchenElectricGrillLarge', 'KitchenDeepFryer', 'KitchenAssembler', 'DrinkMug', 'DrinkMugBlack', 'DrinkMugRed', 'DrinkMugBlue', 'DrinkMugGreen', 'DrinkGlass', 'FoodPlate', 'FoodPlateSmall', 'FoodPlatePlastic', 'FoodPlateSmallPlastic', 'FoodBowlBig', 'FoodBowlFancy', 'Fork', 'Spoon', 'KitchenKnife', 'RollingPin', 'Tray'],
  'Хранение': ['Bookshelf', 'Rack', 'RackBlack', 'CrateGenericSteel', 'CratePlastic', 'CrateWood', 'CrateFreezer', 'CrateTrashCart', 'WardrobeBlue', 'WardrobePink', 'WardrobeBlack', 'WardrobeGreen', 'WardrobeYellow', 'WardrobeWhite', 'WardrobeGrey', 'WardrobeMixed', 'ClosetTool', 'ClosetJanitor', 'ClosetChef', 'filingCabinet', 'filingCabinetTall', 'filingCabinetDrawer', 'PaperBin', 'BigBox'],
  'Сантехника': ['Sink', 'SinkWide', 'SinkStemless', 'ToiletEmpty', 'ToiletGoldenEmpty', 'WaterCooler', 'WaterTank', 'WaterTankHighCapacity'],
  'Электроника': ['ComputerTelevision', 'Jukebox', 'BlockGameArcade', 'SpaceVillainArcade', 'IntercomCommon', 'RadioHandheld', 'HandheldGPSBasic', 'DoorRemoteDefault', 'RemoteSignaller'],
  'Торговые автоматы': ['VendingMachineCoffee', 'VendingMachineCola', 'VendingMachineColaBlack', 'VendingMachineColaRed', 'VendingMachineSpaceUp', 'VendingMachineSoda', 'VendingMachineStarkist', 'VendingMachineShamblersJuice', 'VendingMachinePwrGame', 'VendingMachineDrGibb', 'VendingMachineSmite', 'VendingMachineSnack', 'VendingMachineSnackBlue', 'VendingMachineSnackOrange', 'VendingMachineSnackGreen', 'VendingMachineSnackTeal', 'VendingMachineCondiments', 'VendingMachineDinnerware', 'VendingMachineChefvend', 'VendingMachineDonut', 'VendingMachineGames', 'VendingMachineYouTool', 'VendingMachineClothing', 'VendingMachineJaniDrobe', 'VendingMachinePottedPlantVend', 'VendingMachineAutoTuneVend'],
};
function files(dir) { return fs.readdirSync(dir, { withFileTypes: true }).flatMap(e => e.isDirectory() ? files(path.join(dir, e.name)) : [path.join(dir, e.name)]); }
// Only entity headers are needed; component values may contain custom YAML tags.
function parentIds(block) {
  const lines = block.split(/\r?\n/), index = lines.findIndex(line => /^  parent:/.test(line));
  if (index < 0) return [];
  const inline = lines[index].slice(9).split('#')[0].trim();
  if (inline) return inline.replace(/[\[\]]/g, '').split(',').map(value => value.trim()).filter(Boolean);
  const result = [];
  for (const line of lines.slice(index + 1)) {
    const match = line.match(/^\s+-\s+([A-Za-z0-9_]+)\s*(?:#.*)?$/);
    if (!match) break;
    result.push(match[1]);
  }
  return result;
}
const entities = new Map();
for (const file of files(path.join(root, 'Resources/Prototypes')).filter(file => file.endsWith('.yml') && !file.endsWith('utility-catalog.generated.yml'))) {
  for (const block of fs.readFileSync(file, 'utf8').replace(/^\uFEFF/, '').split(/(?=^- type: )/m)) {
    if (!/^- type: entity\s*$/m.test(block)) continue;
    const id = block.match(/^  id: ([\w]+)\b/m)?.[1];
    if (id) entities.set(id, { block, name: block.match(/^  name: (.+)$/m)?.[1], parents: parentIds(block), abstract: /^  abstract: true/m.test(block) });
  }
}
function has(id, component, seen = new Set()) {
  if (seen.has(id)) return false; seen.add(id);
  const entity = entities.get(id); if (!entity) return false;
  return new RegExp('^  - type: ' + component + '\\s*$', 'm').test(entity.block) || entity.parents.some(parent => has(parent, component, seen));
}
function englishName(id) { const entity = entities.get(id); return entity?.name?.replace(/^['\"]|['\"]$/g, '') || entity?.parents.map(englishName).find(Boolean) || id; }
const generated = [], ids = [], unavailable = [];
for (const [category, candidates] of Object.entries(groups)) for (const base of candidates) {
  if (!entities.has(base) || entities.get(base).abstract) { unavailable.push(base); continue; }
  const id = 'DLAptUtility' + base, entity = id + 'Entity', portable = has(base, 'Item');
  const wall = has(base, 'WallMount'), key = 'dl-apt-utility-' + base.toLowerCase();
  const components = [{ type: 'NoDeconstruct' }];
  if (has(base, 'ApcPowerReceiver')) components.push({ type: 'ApcPowerReceiver', needsPower: false });
  generated.push({ type: 'entity', id: entity, parent: base, categories: ['HideSpawnMenu'], components });
  generated.push({ type: 'apartmentFurniture', id, name: key, entity, category, price: 0, maxCount: category === 'Торговые автоматы' ? 2 : 8,
    blocksMovement: !portable && !has(base, 'Strap') && !wall, allowsOverlap: portable || wall, ...(wall ? { layer: 'Wall' } : {}) });
  ids.push({ id, base, category, key });
}
assert(ids.length >= 150, 'Household catalogue unexpectedly small');
for (const required of ['MopItem','Bucket','RagItem','SprayPainter','Screwdriver','VendingMachineCoffee']) assert(ids.some(item => item.base === required), required);
const dump = value => yaml.dump(value, { noRefs: true, lineWidth: 120 });
fs.writeFileSync(path.join(root, 'Resources/Prototypes/_DeepLagoon/Apartments/utility-catalog.generated.yml'), '# Generated by Scripts/generate-apartment-utility-catalog.cjs. Functional household objects.\n' + dump(generated));
for (const language of ['ru-RU', 'en-US']) fs.writeFileSync(path.join(root, `Resources/Locale/${language}/_DeepLagoon/apartment-utilities.ftl`), ids.map(item => language === 'ru-RU' ? `${item.key} = { ent-${item.base} }` : `${item.key} = ${englishName(item.base)}`).join('\n') + '\n');
for (const name of ['templates.yml', 'apartments.yml']) {
 const file = path.join(root, 'Resources/Prototypes/_DeepLagoon/Apartments', name);
 const text = fs.readFileSync(file, 'utf8').replace(/^  catalog: \[([^\]]*)\]/gm, (_, old) => '  catalog: [' + [...new Set([...old.split(',').map(s => s.trim()).filter(id => id && !id.startsWith('DLAptUtility')), ...ids.map(item => item.id)])].join(', ') + ']');
 fs.writeFileSync(file, text);
}
console.log(JSON.stringify({ added: ids.length, categories: Object.fromEntries(Object.keys(groups).map(category => [category, ids.filter(item => item.category === category).length])), unavailable }));
