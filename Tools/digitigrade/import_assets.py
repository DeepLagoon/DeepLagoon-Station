"""Import the four limb states and build the matching clothing displacement variants.

Usage: python Tools/digitigrade/import_assets.py --source C:/path/to/arcane
Requires Pillow. Source RSI license/attribution is retained in each destination.
"""
import argparse
import json
import re
import shutil
from pathlib import Path
from PIL import Image

ROOT = Path(__file__).resolve().parents[2]
TEXTURES = ROOT / 'Resources/Textures'
# First row affected by the shared digitigrade trouser map, in a 32px frame.
HEM_TOP = 22
FOOT_TOP = 25
SPECIES = {
    '_DV/Mobs/Species/Vulpkanin': '_DV/Mobs/Species/Vulpkanin',
    '_DV/Mobs/Species/Feroxi': '_Goobstation/Mobs/Species/Feroxi',
    '_Goobstation/Mobs/Species/Tajaran': '_Goobstation/Mobs/Species/Tajaran',
}
HIP_BRIDGES = {
    '_DV/Mobs/Species/Vulpkanin': {
        'l_leg': {2: [(24, 25, (12, 13, 14, 15))], 3: [(24, 25, (14,))]},
        'r_leg': {2: [(24, 25, (17,))], 3: [(24, 25, (16, 17, 18, 19))]},
    },
    '_Goobstation/Mobs/Species/Tajaran': {
        'l_leg': {0: [(21, 22, (18, 19))], 1: [(21, 23, (11, 12)), (22, 23, (11, 12))]},
        'r_leg': {0: [(21, 22, (11, 12))], 1: [(21, 23, (18, 19)), (22, 23, (18,))]},
    },
}


def import_limbs(source):
    for destination, origin in SPECIES.items():
        src = source / 'Resources/Textures' / origin / 'parts.rsi'
        dst = TEXTURES / destination / 'parts.rsi'
        original = (dst / 'meta.json').read_text(encoding='utf-8')
        credit = json.loads(original)['copyright'].split(' | l_leg, r_leg, l_foot, r_foot from Arcane:')[0]
        credit += ' | l_leg, r_leg, l_foot, r_foot from Arcane: ' + json.loads((src / 'meta.json').read_text())['copyright']
        updated = re.sub(r'("copyright"\s*:\s*)"(?:[^"\\]|\\.)*"', lambda m: m[1] + json.dumps(credit, ensure_ascii=False), original)
        (dst / 'meta.json').write_text(updated, encoding='utf-8')
        for part in ('l_leg', 'r_leg', 'l_foot', 'r_foot'):
            shutil.copyfile(src / (part + '.png'), dst / (part + '.png'))


def join_native_hips():
    # Some donor thighs start below our native torso. Extend only their
    # proximal edge; moving the whole legs would misalign knees, paws and clothing.
    for species, parts in HIP_BRIDGES.items():
        root = TEXTURES / species / 'parts.rsi'
        for part, directions in parts.items():
            image = Image.open(root / (part + '.png')).convert('RGBA')
            for direction, bridges in directions.items():
                ox, oy = direction % 2 * 32, direction // 2 * 32
                for row, source_row, columns in bridges:
                    for x in columns:
                        target = (ox + x, oy + row)
                        if image.getpixel(target)[3] == 0:
                            image.putpixel(target, image.getpixel((ox + x, oy + source_row)))
            image.save(root / (part + '.png'))


def build_maps():
    base = TEXTURES / '_White/Mobs/Species/displacement.rsi'
    dst = TEXTURES / '_DeepLagoon/Mobs/Digitigrade/displacement.rsi'
    dst.mkdir(parents=True, exist_ok=True)
    for state in ('jumpsuit', 'jumpsuit-female'):
        image = Image.open(base / (state + '.png')).convert('RGBA')
        for direction in range(4):
            for y in range(HEM_TOP, 32):
                for x in range(32):
                    image.putpixel((direction % 2 * 32 + x, direction // 2 * 32 + y), (128, 128, 0, 255))
        image.save(dst / (state + '-hem.png'))
        # Feroxi thighs extend one pixel farther at the upper side-view bend.
        feroxi = Image.open(base / (state + '.png')).convert('RGBA')
        for direction, x, shift in ((2, 19, -1), (3, 12, 1)):
            feroxi.putpixel((direction % 2 * 32 + x, direction // 2 * 32 + HEM_TOP), (128 + shift, 128, 0, 255))
        feroxi.save(dst / (state + '-feroxi.png'))
    # Preserve boot shafts above the paw instead of clipping them at the ankle.
    shoes = Image.open(base / 'shoes.png').convert('RGBA')
    legs = Image.open(base / 'jumpsuit.png').convert('RGBA')
    for direction in range(4):
        for y in range(FOOT_TOP):
            for x in range(32):
                at = (direction % 2 * 32 + x, direction // 2 * 32 + y)
                shoes.putpixel(at, legs.getpixel(at))
    shoes.save(dst / 'shoes.png')
    for suffix in ('', '-feroxi'):
        origin = dst / ('jumpsuit-feroxi.png') if suffix else base / 'jumpsuit.png'
        outer = Image.open(origin).convert('RGBA')
        for direction in range(4):
            for y in range(HEM_TOP):
                for x in range(32):
                    outer.putpixel((direction % 2 * 32 + x, direction // 2 * 32 + y), (128, 128, 0, 255))
        outer.save(dst / ('outerwear' + suffix + '.png'))
    Image.new('RGBA', (64, 64), (128, 128, 0, 255)).save(dst / 'outerwear-hem.png')
    meta = {'version': 1, 'license': 'CC-BY-SA-3.0',
            'copyright': 'Derived from _White/Mobs/Species/displacement.rsi by Litogin; hem preserves original coordinates; boot shafts use the trouser map.',
            'size': {'x': 32, 'y': 32}, 'load': {'srgb': False},
            'states': [{'name': s, 'directions': 4} for s in ('jumpsuit-hem', 'jumpsuit-female-hem', 'jumpsuit-feroxi', 'jumpsuit-female-feroxi', 'shoes', 'outerwear', 'outerwear-feroxi', 'outerwear-hem')]}
    (dst / 'meta.json').write_text(json.dumps(meta, indent=2) + '\n')


def fit_markings():
    sources = Path(__file__).parent / 'sources'
    credits = json.loads((sources / 'credits.json').read_text(encoding='utf-8'))
    for species, credit in credits.items():
        folder = '_Goobstation' if species == 'Tajaran' else '_DV'
        skin = Image.new('RGBA', (64, 64))
        for part in ('l_leg', 'r_leg', 'l_foot', 'r_foot'):
            skin.alpha_composite(Image.open(TEXTURES / folder / 'Mobs/Species' / species / 'parts.rsi' / (part + '.png')).convert('RGBA'))
        suffix = '-feroxi' if species == 'Feroxi' else ''
        maps = TEXTURES / ('_DeepLagoon/Mobs/Digitigrade' if suffix else '_White/Mobs/Species') / 'displacement.rsi'
        legs = Image.open(maps / ('jumpsuit' + suffix + '.png')).convert('RGBA')
        shoes = Image.open(TEXTURES / '_DeepLagoon/Mobs/Digitigrade/displacement.rsi/shoes.png').convert('RGBA')
        for state in credit['states']:
            original = Image.open(sources / species / (state + '.png')).convert('RGBA')
            result = original.copy()
            for direction in range(4):
                ox, oy = direction % 2 * 32, direction // 2 * 32
                for y in range(HEM_TOP, 32):
                    for x in range(32):
                        at = (ox + x, oy + y)
                        r, g, _, a = (shoes if y >= 29 else legs).getpixel(at)
                        sx, sy = x + r - 128, y + g - 128
                        pixel = (0, 0, 0, 0)
                        if a and skin.getpixel(at)[3] and 0 <= sx < 32 and 0 <= sy < 32:
                            pixel = original.getpixel((ox + sx, oy + sy))
                        result.putpixel(at, pixel)
            result.save(TEXTURES / credit['path'] / (state + '.png'))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path)
    args = parser.parse_args()
    if args.source:
        import_limbs(args.source)
    join_native_hips()
    build_maps()
    fit_markings()
