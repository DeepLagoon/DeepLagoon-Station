"""Import MOLOT typing sprites into native RSI frames without modifying the source."""
import argparse
import hashlib
import json
import re
import sys
from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'Tools/body-customization'))
from dmi_states import states

STATES = {
    'normal': 'normal_typing',
    'emote': 'emotetyping',
    'medical': 'CMO_typing',
    'science': 'RD_typing',
    'engineering': 'CE_typing',
    'command': 'CAP_typing',
    'cargo': 'QM_typing',
    'security': 'lawyer_typing',
    'service': 'clown_typing',
}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('source', type=Path, help='MOLOT-BlueMoon-Station checkout')
    args = parser.parse_args()
    source = args.source / 'icons/mob/talk.dmi'
    digest = hashlib.sha256(source.read_bytes()).hexdigest()
    with Image.open(source) as image:
        description = image.info['Description']
        width = int(re.search(r'width = (\d+)', description)[1])
        height = int(re.search(r'height = (\d+)', description)[1])
        if (width, height) != (64, 64):
            raise ValueError('Expected the 64x64 MOLOT talk.dmi')
        image = image.convert('RGBA')
    source_states = {name: (block, dirs, count, offset)
                     for name, block, dirs, count, offset in states(description)}
    output = ROOT / 'Resources/Textures/_DeepLagoon/Effects/typing.rsi'
    output.mkdir(parents=True, exist_ok=True)
    metadata = {
        'version': 1,
        'license': 'CC-BY-SA-3.0',
        'copyright': 'Adapted from MOLOT-BlueMoon-Station icons/mob/talk.dmi at '
                     'e9ece923dd956fb78ae6cb83e1eaaf259b61a432. '
                     'BYOND padding cropped; idle frames extracted by DeepLagoon. '
                     f'Source SHA256: {digest}.',
        'size': {'x': 32, 'y': 32},
        'states': [],
    }
    columns = image.width // width
    for name, source_name in STATES.items():
        block, dirs, count, offset = source_states[source_name]
        if dirs != 1:
            raise ValueError(f'{source_name}: expected one direction')
        delays = re.search(r'delay = ([\d.,]+)', block)
        delays = [float(value) / 10 for value in delays[1].split(',')] if delays else [0.1] * count
        if len(delays) != count:
            raise ValueError(f'{source_name}: invalid frame delays')
        strip = Image.new('RGBA', (32 * count, 32))
        for frame in range(count):
            tile = offset + frame
            x, y = tile % columns * width, tile // columns * height
            original = image.crop((x, y, x + width, y + height))
            bounds = original.getbbox()
            if not bounds or bounds[0] < 0 or bounds[1] < 30 or bounds[2] > 32 or bounds[3] > 62:
                raise ValueError(f'{source_name}: art outside the expected BYOND origin')
            cropped = original.crop((0, 30, 32, 62))
            strip.paste(cropped, (32 * frame, 0))
        strip.save(output / f'{name}.png')
        strip.crop((32 * (count - 1), 0, 32 * count, 32)).save(output / f'{name}-idle.png')
        metadata['states'].extend([
            {'name': name, 'delays': [delays]},
            {'name': f'{name}-idle'},
        ])
    (output / 'meta.json').write_text(json.dumps(metadata, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(f'Imported {len(STATES)} animations and {len(STATES)} static idle states; source SHA256 {digest}')


if __name__ == '__main__':
    main()
