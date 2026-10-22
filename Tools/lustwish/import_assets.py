"""Convert the explicitly listed WhiteMoon DMI states to RSI, preserving animation.

Run from the Station root: python Tools/lustwish/import_assets.py <WhiteMoon checkout>
Requires Pillow. The manifest records source paths, states, and output names.
"""
import json
import re
import subprocess
import sys
from pathlib import Path

from PIL import Image, ImageOps

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'Tools/body-customization'))
from dmi_states import states


def convert(source, entry, revision):
    target = ROOT / 'Resources/Textures/_DeepLagoon/LustWish' / (entry['id'] + '.rsi')
    target.mkdir(parents=True, exist_ok=True)
    metadata = []
    sources = []
    for output, spec in entry['states'].items():
        # GAGS consists of ordinary DMI layers. Composite neutral layers before runtime tinting.
        parts = spec.get('layers') if isinstance(spec, dict) else None
        if parts:
            sheets = []
            for path, state in parts:
                temporary = dict(entry, id=entry['id'] + '-layer', states={output: [path, state]})
                convert(source, temporary, revision)
                layer_dir = target.with_name(temporary['id'] + '.rsi')
                with Image.open(layer_dir / (output + '.png')) as layer:
                    sheets.append(layer.convert('RGBA'))
                layer_meta = json.loads((layer_dir / 'meta.json').read_text())
                sources.append(f'https://github.com/BlueMoon-Labs/WhiteMoon-Station/blob/{revision}/{path} (state {state})')
            canvas = sheets[0]
            for layer in sheets[1:]:
                assert layer.size == canvas.size
                canvas = Image.alpha_composite(canvas, layer)
            canvas.save(target / (output + '.png'))
            metadata.append(layer_meta['states'][0])
            # Remove only the exact generated temporary files, never an arbitrary source path.
            (layer_dir / (output + '.png')).unlink()
            (layer_dir / 'meta.json').unlink()
            layer_dir.rmdir()
            continue
        path, state = spec
        with Image.open(source / path) as original:
            description = original.info['Description']
            image = original.convert('RGBA')
        width = int(re.search(r'width = (\d+)', description)[1])
        height = int(re.search(r'height = (\d+)', description)[1])
        assert (width, height) == tuple(entry.get('size', [32, 32])), path
        block, directions, frames, offset = next((b, d, f, o) for n, b, d, f, o in states(description) if n == state)
        assert directions in (1, 4, 8)
        columns = image.width // width
        sheet = Image.new('RGBA', (width * frames, height * directions))
        for direction in range(directions):
            for frame in range(frames):
                index = offset + frame * directions + direction
                x, y = index % columns * width, index // columns * height
                sheet.paste(image.crop((x, y, x + width, y + height)), (frame * width, direction * height))
        if entry.get('neutralize'):
            alpha = sheet.getchannel('A')
            sheet = ImageOps.grayscale(sheet).convert('RGBA')
            sheet.putalpha(alpha)
        sheet.save(target / (output + '.png'))
        result = {'name': output}
        if directions != 1:
            result['directions'] = directions
        if frames > 1:
            delay = re.search(r'delay = ([\d.,]+)', block)
            values = [float(v) / 10 for v in delay[1].split(',')] if delay else [0.1] * frames
            if len(values) == 1:
                values *= frames
            # Some source DMIs have unused trailing delay entries.
            values = values[:frames]
            assert len(values) == frames and all(v > 0 for v in values), spec
            result['delays'] = [values] * directions
        metadata.append(result)
        sources.append(f'https://github.com/BlueMoon-Labs/WhiteMoon-Station/blob/{revision}/{path} (state {state})')
    meta = {'version': 1, 'size': dict(zip(('x', 'y'), entry.get('size', [32, 32]))),
            'license': 'CC-BY-SA-3.0',
            'copyright': 'WhiteMoon / SPLURT / Bubber / Skyrat / tgstation contributors. Converted DMI to RSI; states renamed. Sources: ' + '; '.join(dict.fromkeys(sources)),
            'states': metadata}
    (target / 'meta.json').write_text(json.dumps(meta, indent=2) + '\n', encoding='utf-8')


if __name__ == '__main__':
    source = Path(sys.argv[1]).resolve()
    manifest = json.loads(Path(__file__).with_name('assets.json').read_text(encoding='utf-8'))
    revision = subprocess.check_output(['git', '-c', f'safe.directory={source.as_posix()}', '-C', str(source), 'rev-parse', 'HEAD'], text=True).strip()
    if revision != manifest['revision']:
        raise SystemExit(f'Expected WhiteMoon revision {manifest["revision"]}; got {revision}')
    for entry in manifest['assets']:
        convert(source, entry, revision)
        print(entry['id'])
