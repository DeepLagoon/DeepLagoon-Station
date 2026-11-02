const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const yaml = require('../node_modules/js-yaml');
const root = path.resolve(__dirname, '../..');
const directory = path.join(root, 'Resources/Prototypes/_DeepLagoon/Apartments');
const entries = yaml.load(fs.readFileSync(path.join(directory, 'utility-catalog.generated.yml'), 'utf8'));
const furniture = entries.filter(entry => entry.type === 'apartmentFurniture');
const entities = new Map(entries.filter(entry => entry.type === 'entity').map(entry => [entry.id, entry]));

test('household catalogue covers every agreed category with functional inherited objects', () => {
  assert.ok(furniture.length >= 150);
  assert.equal(new Set(furniture.map(item => item.id)).size, furniture.length);
  assert.deepEqual([...new Set(furniture.map(item => item.category))].sort(), [
    'Уборка и покраска', 'Инструменты', 'Мебель', 'Освещение', 'Декор и растения',
    'Кухня и посуда', 'Хранение', 'Сантехника', 'Электроника', 'Торговые автоматы',
  ].sort());
  for (const item of furniture) {
    assert.ok(entities.get(item.entity)?.parent);
    assert.ok(item.maxCount >= 1 && item.maxCount <= 400);
    assert.ok(entities.get(item.entity).components.some(component => component.type === 'NoDeconstruct'));
  }
});

test('mandatory cleaning supplies are portable and vending machines retain their behaviour', () => {
  for (const base of ['MopItem', 'Bucket', 'RagItem', 'SprayPainter', 'Screwdriver']) {
    const item = furniture.find(item => item.id === 'DLAptUtility' + base);
    assert.ok(item, base); assert.equal(item.blocksMovement, false); assert.equal(item.allowsOverlap, true);
    assert.equal(entities.get(item.entity).parent, base);
  }
  const vendors = furniture.filter(item => item.category === 'Торговые автоматы');
  assert.ok(vendors.length >= 10);
  for (const item of vendors) assert.equal(entities.get(item.entity).components.find(component => component.type === 'ApcPowerReceiver')?.needsPower, false);
});

test('every map template includes household catalogue and both locales name every entry', () => {
  const templates = yaml.load(fs.readFileSync(path.join(directory, 'templates.yml'), 'utf8'));
  for (const template of templates.filter(entry => entry.type === 'apartmentTemplate')) {
    for (const item of furniture) assert.ok(template.catalog.includes(item.id), template.id + ': ' + item.id);
  }
  for (const locale of ['ru-RU', 'en-US']) {
    const text = fs.readFileSync(path.join(root, `Resources/Locale/${locale}/_DeepLagoon/apartment-utilities.ftl`), 'utf8');
    for (const item of furniture) assert.ok(text.includes(item.name + ' = '), locale + ': ' + item.id);
  }
});
