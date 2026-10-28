"""Import tintable on-mob DMI states. Requires Pillow; run from the Station root.

python Tools/body-customization/import_bluemoon.py C:/old_disk/repos/MOLOT-BlueMoon-Station
"""
import json
import re
import subprocess
import argparse
from pathlib import Path
from PIL import Image
from dmi_states import states as dmi_states

root = Path(__file__).resolve().parents[2]
files = {
    'penis': 'icons/obj/genitals/penis_onmob.dmi',
    'testicles': 'icons/obj/genitals/testicles_onmob.dmi',
    'vagina': 'icons/obj/genitals/vagina_onmob.dmi',
    'breasts': 'icons/obj/genitals/breasts_onmob.dmi',
    'butt': 'modular_splurt/icons/obj/genitals/butt_onmob.dmi',
    'belly': 'modular_splurt/icons/obj/genitals/belly_onmob.dmi',
    'anus': 'modular_splurt/icons/obj/genitals/anus_onmob.dmi',
}
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('source', type=Path)
parser.add_argument('--parts', nargs='+', choices=files, help='Reimport only these parts; preserve the other RSI assets.')
args = parser.parse_args()
source = args.source
selected = set(args.parts or files)
revision = subprocess.check_output(['git', '-c', f'safe.directory={source.as_posix()}', '-C', str(source), 'rev-parse', 'HEAD'], text=True).strip()
catalog = {}
secondary_sizes = []
breasts_revision = 'e0681c819a195c390ded3109b2109ea09b2486c1'
for part, filename in files.items():
    directory = root / f'Resources/Textures/_DeepLagoon/BodyCustomization/{part}.rsi'
    if part not in selected:
        options = {}
        for entry in json.loads((directory / 'meta.json').read_text(encoding='utf-8'))['states']:
            name = entry['name']
            parsed = re.fullmatch(part + r'_(.+)_([^_]+)_0_(FRONT|BEHIND)', name)
            if parsed:
                shape, size, layer = parsed.groups()
                options.setdefault(shape, {}).setdefault(size, []).append(layer)
            if part == 'breasts' and (secondary := re.fullmatch(r'nipples_pair_([^_]+)_0_FRONT', name)):
                secondary_sizes.append(secondary[1])
        catalog[part] = options
        continue
    input_path = root / 'Tools/body-customization/sources/pr4108-breasts.dmi' if part == 'breasts' else source / filename
    image = Image.open(input_path).convert('RGBA')
    description = Image.open(input_path).info['Description']
    width = int(re.search(r'width = (\d+)', description)[1])
    height = int(re.search(r'height = (\d+)', description)[1])
    columns = image.width // width
    directory.mkdir(parents=True, exist_ok=True)
    for old in directory.glob('*.png'):
        old.unlink()
    states, options = [], {}
    # Unnamed states still occupy tiles. Skipping them shifts every subsequent direction.
    for name, block, dirs, frames, offset in dmi_states(description):
        # Retain both penis states; all other parts use their neutral, tintable states.
        secondary = part == 'breasts' and name.startswith('nipples_')
        prefix = 'nipples' if secondary else part
        modes = '[01]' if part == 'penis' else '0'
        parsed = re.fullmatch(prefix + r'_(.+)_([^_]+)_(' + modes + r')_(FRONT|BEHIND)', name) if '_s_' not in name else None
        if parsed and not parsed[1].endswith('_s') and dirs in (1, 4):
            shape, size, aroused, layer = parsed.groups()
            target = Image.new('RGBA', (width * dirs, height * frames))
            for frame in range(frames):
                for direction in range(dirs):
                    tile = offset + frame * dirs + direction
                    x, y = tile % columns * width, tile // columns * height
                    # DMI is frame-major; RSI stores each direction's frames consecutively.
                    destination = direction * frames + frame
                    target.paste(image.crop((x, y, x + width, y + height)),
                                 (destination % dirs * width, destination // dirs * height))
            target.save(directory / f'{name}.png')
            delays_match = re.search(r'delay = ([\d.,]+)', block)
            delays = [float(v) / 10 for v in delays_match[1].split(',')] if delays_match else [0.1] * frames
            if len(delays) != frames:
                delays = [delays[0]] * frames
            states.append({'name': name, 'directions': dirs, 'delays': [delays] * dirs})
            if secondary:
                if shape == 'pair': secondary_sizes.append(size)
            elif aroused == '0':
                options.setdefault(shape, {}).setdefault(size, []).append(layer)
    if part == 'penis':
        names = {state['name'] for state in states}
        for shape, sizes in options.items():
            for size, sides in sizes.items():
                for side in sides:
                    assert f'penis_{shape}_{size}_1_{side}' in names, (shape, size, side)
    assert states, filename
    source_revision = breasts_revision if part == 'breasts' else revision
    source_repo = 'BlueMoon-Station' if part == 'breasts' else 'MOLOT-BlueMoon-Station'
    meta = {'version': 1, 'size': {'x': width, 'y': height}, 'license': 'CC-BY-SA-3.0',
            'copyright': f'tgstation / Citadel / SPLURT / BlueMoon contributors. Converted from https://github.com/BlueMoon-Labs/{source_repo}/blob/{source_revision}/{filename}.',
            'states': states}
    (directory / 'meta.json').write_text(json.dumps(meta, indent=2) + '\n', encoding='utf-8')
    catalog[part] = options
    print(part, len(states), 'states', list(options))
# Catalog is compiled into Shared so server validation never trusts client-selected paths.
lines = ['// Generated by Tools/body-customization/import_bluemoon.py.',
         'namespace Content.Shared._DeepLagoon.BodyCustomization;',
         'public static partial class BodyCustomizationCatalog', '{',
         '    public static readonly Dictionary<string, Dictionary<string, Dictionary<string, string[]>>> Parts = new()', '    {']
for part, shapes in catalog.items():
    lines.append(f'        ["{part}"] = new() {{')
    for shape, sizes in shapes.items():
        lines.append(f'            ["{shape}"] = new() {{')
        for size, layers in sizes.items():
            lines.append(f'                ["{size}"] = new[] {{ ' + ', '.join(json.dumps(v) for v in layers) + ' },')
        lines.append('            },')
    lines.append('        },')
lines += ['    };', '    public static readonly HashSet<string> BreastSecondarySizes = [' + ', '.join(json.dumps(s) for s in secondary_sizes) + '];', '}']
dest = root / 'Content.Shared/_DeepLagoon/BodyCustomization/BodyCustomizationCatalog.Generated.cs'
dest.parent.mkdir(parents=True, exist_ok=True)
dest.write_text('\n'.join(lines) + '\n', encoding='utf-8')
