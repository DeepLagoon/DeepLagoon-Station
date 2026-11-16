"""Generate human skin-matching RSI variants from the imported anatomy sprites."""

import json
from pathlib import Path

from PIL import Image


PARTS = ('butt', 'belly', 'breasts')
TEXTURES = Path(__file__).resolve().parents[2] / 'Resources/Textures'
DIRECTORY = TEXTURES / '_DeepLagoon/BodyCustomization'

# Gray shading levels in the source art mapped to the fill, inner shadows and
# outlines of Mobs/Species/Human/parts.rsi. Preserve alpha and colored details.
SHADES = (
    (243, (255, 255, 255)),
    (224, (249, 238, 242)),
    (200, (234, 217, 220)),
    (183, (218, 180, 178)),
    (0, (212, 166, 162)),
)
PALETTE = [next(rgb for minimum, rgb in SHADES if shade >= minimum) for shade in range(256)]


def generate(part):
    source = DIRECTORY / f'{part}.rsi'
    target = DIRECTORY / 'human' / source.name
    target.mkdir(parents=True, exist_ok=True)

    meta = json.loads((source / 'meta.json').read_text(encoding='utf-8'))
    meta['copyright'] += ' Recolored to match the Station human body palette.'
    meta['states'] = [state for state in meta['states'] if state['name'].startswith(f'{part}_')]
    names = {f"{state['name']}.png" for state in meta['states']}
    for stale in target.glob('*.png'):
        if stale.name not in names:
            stale.unlink()

    for name in sorted(names):
        with Image.open(source / name) as original:
            image = original.convert('RGBA')
        pixels = image.load()
        for y in range(image.height):
            for x in range(image.width):
                r, g, b, a = pixels[x, y]
                if a and r == g == b:
                    pixels[x, y] = (*PALETTE[r], a)
        image.save(target / name)
    (target / 'meta.json').write_text(json.dumps(meta, indent=2) + '\n', encoding='utf-8')


if __name__ == '__main__':
    for part in PARTS:
        generate(part)
