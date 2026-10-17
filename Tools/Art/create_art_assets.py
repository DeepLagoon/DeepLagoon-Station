"""Convert the selected BlueMoon DMI states to RSI, and generate an original brush sprite.

Usage: python Tools/Art/create_art_assets.py C:/old_disk/repos/MOLOT-BlueMoon-Station
Only writes Resources/Textures/_DeepLagoon/Art. Requires Pillow.
"""
from pathlib import Path
import json
import re
import sys
from PIL import Image, ImageDraw

root = Path(__file__).resolve().parents[2]
source = Path(sys.argv[1]) / 'icons/obj/artstuff.dmi'
target = root / 'Resources/Textures/_DeepLagoon/Art/supplies.rsi'
target.mkdir(parents=True, exist_ok=True)
image = Image.open(source).convert('RGBA')
description = Image.open(source).info['Description']
position = 0
for block in re.split(r'\nstate = ', description)[1:]:
    name = block.split('"')[1]
    dirs = re.search(r'\bdirs = (\d+)', block)
    frames = re.search(r'\bframes = (\d+)', block)
    count = int(dirs[1] if dirs else 1) * int(frames[1] if frames else 1)
    if name in ('24x24', '24x24frame'):
        x, y = position % (image.width // 32) * 32, position // (image.width // 32) * 32
        tile = image.crop((x, y, x + 32, y + 32))
        content = tile.crop(tile.getbbox())
        centered = Image.new('RGBA', (32, 32))
        centered.alpha_composite(content, ((32 - content.width) // 2, (32 - content.height) // 2))
        centered.save(target / ('canvas.png' if name == '24x24' else 'frame.png'))
    position += count

# Original pixel brush: handle, ferrule and bristles, not copied from the reference repository.
brush = Image.new('RGBA', (32, 32))
d = ImageDraw.Draw(brush)
d.line((7, 26, 20, 13), fill='#38281d', width=5)
d.line((7, 25, 19, 13), fill='#aa7746', width=3)
d.line((8, 25, 18, 15), fill='#d4a16a', width=1)
d.polygon([(17, 12), (20, 9), (24, 13), (21, 16)], fill='#474b50')
d.line((18, 12, 21, 15), fill='#c5c9cd', width=2)
d.polygon([(20, 9), (23, 3), (28, 4), (28, 8), (24, 13)], fill='#473728')
d.polygon([(21, 9), (24, 4), (27, 5), (26, 8), (23, 12)], fill='#dfc895')
d.line((24, 5, 27, 5), fill='#386b83', width=2)
brush.save(target / 'brush.png')
meta = {'version': 1, 'size': {'x': 32, 'y': 32}, 'license': 'CC-BY-SA-3.0',
        'copyright': 'Canvas/frame converted from MOLOT-BlueMoon-Station icons/obj/artstuff.dmi at 9481abffb4328bb4485a0910a5bc45c9a42751d9 (tgstation-derived). Brush created for DeepLagoon; see Tools/Art/create_art_assets.py.',
        'states': [{'name': n} for n in ('canvas', 'frame', 'brush')]}
(target / 'meta.json').write_text(json.dumps(meta, indent=2) + '\n', encoding='utf-8')
