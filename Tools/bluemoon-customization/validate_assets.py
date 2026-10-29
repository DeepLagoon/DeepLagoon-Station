"""Validate shipped BlueMoon customization without a BlueMoon checkout or game server."""
import json
from collections import Counter

from PIL import Image
import yaml

from import_assets import DEST, ROOT, existing_markings, rsi_frames


def main():
    loader = getattr(yaml, 'CSafeLoader', yaml.SafeLoader)
    loader.add_multi_constructor('!', lambda loader, tag, node: loader.construct_mapping(node))
    file = ROOT / 'Resources/Prototypes/_DeepLagoon/Entities/Mobs/Customization/Markings/bluemoon.yml'
    prototypes = yaml.load(file.read_text(encoding='utf-8'), Loader=loader)
    existing = {p['id'] for p in existing_markings()}
    ids, references = set(), set()
    textures = ROOT / 'Resources/Textures'
    states = 0
    for prototype in prototypes:
        identifier = prototype['id']
        assert identifier not in ids and identifier not in existing, identifier
        ids.add(identifier)
        assert prototype.get('sexRestriction') in (None, 'Male', 'Female'), identifier
        assert prototype['sprites'], identifier
        if identifier.startswith('BlueMoonEars'):
            assert prototype.get('editorType') == 'Ears', f'{identifier}: missing ear classification'
        if prototype['markingCategory'] == 'Tail':
            front = prototype.get('spriteLayers', {})
            rear = prototype.get('spriteLayersBefore', {})
            north_visible = False
            for sprite in prototype['sprites']:
                state = sprite['state']
                if state in rear:
                    assert rear[state] == 'Chest', identifier
                    continue
                assert front.get(state, 'Tail') == 'Tail', f'{identifier}/{state}: tail below limbs'
                frames, _ = rsi_frames(sprite)
                north = frames[1] if len(frames) > 1 else frames[0]
                north_visible |= any(frame.getbbox() for frame in north)
            assert north_visible, f'{identifier}: no north-facing tail above the body'
        for sprite in prototype['sprites']:
            directory = textures / sprite['sprite']
            assert directory.resolve().is_relative_to((textures / DEST).resolve()), directory
            references.add(directory.resolve())
            meta = json.loads((directory / 'meta.json').read_text(encoding='utf-8'))
            assert meta['license'] == 'CC-BY-SA-3.0' and 'MOLOT-BlueMoon-Station/blob/' in meta['copyright']
            names = [s['name'] for s in meta['states']]
            assert len(names) == len(set(names)) and sprite['state'] in names, identifier
            state = next(s for s in meta['states'] if s['name'] == sprite['state'])
            dirs = state.get('directions', 1)
            assert dirs in (1, 4)
            delays = state.get('delays', [[0.1]] * dirs)
            assert len(delays) == dirs and len({len(d) for d in delays}) == 1
            assert all(value > 0 for direction in delays for value in direction)
            width, height = meta['size']['x'], meta['size']['y']
            with Image.open(directory / (sprite['state'] + '.png')) as image:
                assert image.width % width == image.height % height == 0
                assert image.width // width * (image.height // height) == sum(map(len, delays)), identifier
                assert image.convert('RGBA').getbbox(), identifier
            states += 1
    directories = {p.resolve() for p in (textures / DEST).glob('*.rsi')}
    assert directories == references, 'Unused generated assets'
    for locale in ('ru-RU', 'en-US'):
        text = (ROOT / f'Resources/Locale/{locale}/_DeepLagoon/bluemoon-customization.ftl').read_text(encoding='utf-8')
        keys = [line.split(' = ', 1)[0] for line in text.splitlines() if ' = ' in line]
        assert len(keys) == len(set(keys)), 'Duplicate localization keys'
        assert all('marking-' + identifier in keys for identifier in ids)
        assert all('{' not in line and '}' not in line for line in text.splitlines() if ' = ' in line), 'Unescaped Fluent expression'
    print(f'PASS: {len(ids)} unique prototypes, {states} sprite states, {len(directories)} RSI sets, both locales, no missing or orphan assets.')
    print(json.dumps(dict(Counter(p['markingCategory'] for p in prototypes)), ensure_ascii=False))


if __name__ == '__main__':
    main()
