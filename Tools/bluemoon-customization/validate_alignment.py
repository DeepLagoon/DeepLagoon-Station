"""Check large shipped RSI frames against BYOND world coordinates, without importing."""
import argparse
import json
import re
from collections import defaultdict
from pathlib import Path

from PIL import Image, ImageChops

from import_assets import PREFIX, ROOT, definitions, read_dmi, slug


def main():
    parser = argparse.ArgumentParser(__doc__)
    parser.add_argument('source', type=Path)
    args = parser.parse_args()
    types = definitions(args.source)
    # Tag constructors are only needed to load the fixed-color definitions.
    import yaml
    loader = getattr(yaml, 'CSafeLoader', yaml.SafeLoader)
    loader.add_multi_constructor('!', lambda loader, tag, node: loader.construct_mapping(node))
    catalogue = ROOT / 'Resources/Prototypes/_DeepLagoon/Entities/Mobs/Customization/Markings/bluemoon.yml'
    checked_frames, checked_sets, checked_states = 0, set(), set()
    for prototype in yaml.load(catalogue.read_text(encoding='utf-8'), Loader=loader):
        directory = ROOT / 'Resources/Textures' / prototype['sprites'][0]['sprite']
        meta = json.loads((directory / 'meta.json').read_text(encoding='utf-8'))
        source_file = re.search(r'/blob/[0-9a-f]+/(.*?)\. Definitions:', meta['copyright'])[1]
        source, width, height, states = read_dmi(str(args.source / source_file))
        if width <= 32 and height <= 32:
            continue
        source_type = next(data for path, data in types if
            'BlueMoon' + ''.join(word.capitalize() for word in path[len(PREFIX):].split('/') if word)
            + prototype['bodyPart'] + (prototype.get('sexRestriction') or '') == prototype['id'])
        # Use the original center_image formula, independently of frame_layout.
        pixel_x = pixel_y = 0
        if source_type.get('center') == 'TRUE':
            pixel_x = -(int(source_type.get('dimension_x', '32')) - 32) / 2
            pixel_y = -(int(source_type.get('dimension_y', '32')) - 32) / 2
        groups = defaultdict(list)
        for sprite in prototype['sprites']:
            original = next(name for name in sorted(states, key=len, reverse=True) if name and
                re.fullmatch(re.escape(slug(name)) + r'(?:_(?:base|primary|secondary|tertiary))?_\d+', sprite['state']))
            groups[original].append(sprite['state'])
        for name, layers in groups.items():
            _, dirs, frames, offset = states[name]
            checked_states.add((str(directory), name))
            with_sheets = {layer: Image.open(directory / (layer + '.png')).convert('RGBA') for layer in layers}
            target_w, target_h = meta['size']['x'], meta['size']['y']
            source_cols = source.width // width
            for direction in range(dirs):
                for frame in range(frames):
                    tile = offset + frame * dirs + direction
                    sx, sy = tile % source_cols * width, tile // source_cols * height
                    expected = Image.new('L', (256, 256))
                    source_mask = source.crop((sx, sy, sx + width, sy + height)).getchannel('A')
                    expected.paste(source_mask, (int(128 - 16 + pixel_x), int(128 - height + 16 - pixel_y)))
                    actual = Image.new('L', expected.size)
                    for sheet in with_sheets.values():
                        tile = direction * frames + frame
                        cols = sheet.width // target_w
                        x, y = tile % cols * target_w, tile // cols * target_h
                        mask = sheet.crop((x, y, x + target_w, y + target_h)).getchannel('A')
                        overlay = Image.new('L', expected.size)
                        overlay.paste(mask, (128 - target_w // 2, 128 - target_h // 2))
                        actual = ImageChops.lighter(actual, overlay)
                    assert ImageChops.difference(actual, expected).getbbox() is None, f'{prototype["id"]}/{name}/{direction}/{frame}'
                    checked_frames += 1
            checked_sets.add(directory)
    assert checked_frames > 0, 'No oversized source frames found'
    print(f'PASS: {len(checked_sets)} large RSI sets, {len(checked_states)} source states, {checked_frames} direction/animation frames match BYOND world coordinates.')


if __name__ == '__main__':
    main()
