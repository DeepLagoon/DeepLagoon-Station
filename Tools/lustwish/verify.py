"""Validate imported LustWish metadata, native slots and RSI files without a BYOND checkout."""
from pathlib import Path
import json
import re
import yaml
from PIL import Image
from import_items import render_locale

ROOT = Path(__file__).resolve().parents[2]

class Loader(yaml.SafeLoader):
    pass

Loader.add_multi_constructor('!type:', lambda loader, tag, node:
    loader.construct_mapping(node) if isinstance(node, yaml.MappingNode) else loader.construct_scalar(node))

def verify():
    folder = ROOT / 'Tools/lustwish'
    snapshot = json.loads((folder / 'source_metadata.json').read_text(encoding='utf-8'))
    manifest = json.loads((folder / 'items.json').read_text(encoding='utf-8'))
    assert snapshot['revision'] == manifest['revision']
    assert {key: data['sourceType'] for key, data in snapshot['items'].items()} == manifest['items']
    prototypes = {}
    for path in (ROOT / 'Resources/Prototypes/_DeepLagoon/LustWish').glob('*.yml'):
        for prototype in yaml.load(path.read_text(encoding='utf-8-sig'), Loader=Loader):
            if prototype['type'] == 'entity': prototypes[prototype['id']] = prototype
    for key, data in snapshot['items'].items():
        actual = prototypes[key]
        assert actual.get('name') == data['name'], key + ' name'
        assert actual.get('description') == data['description'], key + ' description'
    descriptions = json.loads((folder / 'descriptions.ru.json').read_text(encoding='utf-8'))
    assert descriptions.keys() == snapshot['items'].keys()
    for key, data in snapshot['items'].items():
        if data['description'].strip(): assert re.search('[А-Яа-яЁё]', descriptions[key]), key + ' Russian description'
        else: assert descriptions[key] == data['description'], key + ' originally empty description'
    for language in ('en-US', 'ru-RU'):
        locale = ROOT / f'Resources/Locale/{language}/_DeepLagoon'
        assert (locale / 'lustwish-source.ftl').read_text(encoding='utf-8') == render_locale(snapshot['items'], language), language
        for path in locale.glob('lustwish*.ftl'):
            if path.name == 'lustwish-source.ftl': continue
            assert not any(key in snapshot['items'] for key in re.findall(r'^ent-([\w]+)\s*=', path.read_text(encoding='utf-8-sig'), re.M)), path
    slots = json.loads((folder / 'slot_mapping.json').read_text(encoding='utf-8'))
    for key, mapping in slots.items():
        assert snapshot['items'][key]['sourceSlots'] == mapping['source'], key
        components = {component['type']: component for component in prototypes[key].get('components', [])}
        flags = components['Clothing']['slots']
        assert set(flags if isinstance(flags,list) else flags.replace(' ','').split(',')) == set(mapping['nativeFlags']), key
        if 'LustWishAccessory' in components:
            assert components['LustWishAccessory']['inventorySlots'] == mapping['nativeSlots'], key
    assets = json.loads((folder / 'assets.json').read_text(encoding='utf-8'))
    count = 0
    rsi_states = {}
    for asset in assets['assets']:
        rsi = ROOT / f"Resources/Textures/_DeepLagoon/LustWish/{asset['id']}.rsi"
        meta = json.loads((rsi / 'meta.json').read_text(encoding='utf-8'))
        rsi_states[f"_DeepLagoon/LustWish/{asset['id']}.rsi"] = set(asset['states'])
        assert set(asset['states']) == {state['name'] for state in meta['states']}, rsi
        assert {path.stem for path in rsi.glob('*.png')} == set(asset['states']), rsi
        assert meta['license'] == 'CC-BY-SA-3.0' and assets['revision'] in meta['copyright'], rsi
        for state in meta['states']:
            with Image.open(rsi / (state['name']+'.png')) as image:
                w,h = meta['size']['x'],meta['size']['y']
                assert image.width % w == image.height % h == 0, rsi
                frames = len(state.get('delays',[[0]])[0])
                assert image.width//w * (image.height//h) == frames * state.get('directions',1), rsi
            count += 1
    for key, prototype in prototypes.items():
        components = {component['type']: component for component in prototype.get('components', [])}
        sprite = components.get('Sprite', {})
        for layer in [sprite, *sprite.get('layers', [])]:
            path = layer.get('sprite', sprite.get('sprite'))
            if path in rsi_states and 'state' in layer:
                assert layer['state'] in rsi_states[path], f"{key}: missing state {layer['state']}"
        vendor = components.get('VendingMachine', {})
        if sprite.get('sprite') in rsi_states:
            for field in ('offState','normalState','brokenState'):
                if field in vendor: assert vendor[field] in rsi_states[sprite['sprite']], key + ' ' + field
    print(f"PASS: {len(snapshot['items'])} original prototype metadata and English locale, Russian description translations, {len(slots)} native slot mappings, {len(assets['assets'])} RSIs/{count} states")

if __name__ == '__main__': verify()
