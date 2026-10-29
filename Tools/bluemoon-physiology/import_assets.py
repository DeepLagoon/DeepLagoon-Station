"""Convert the reviewed BlueMoon protective-item DMI states to native RSI assets."""
import json
import runpy
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
source = Path(sys.argv[1]).resolve()
manifest = json.loads(Path(__file__).with_name('source.json').read_text(encoding='utf-8'))
revision = subprocess.check_output(['git', '-c', f'safe.directory={source.as_posix()}', '-C', str(source), 'rev-parse', 'HEAD'], text=True).strip()
if revision != manifest['revision']:
    raise SystemExit(f'Expected source revision {manifest["revision"]}; got {revision}')
convert = runpy.run_path(str(ROOT / 'Tools/lustwish/import_assets.py'))['convert']
entries = [
    {'id': 'BlueMoonCondom', 'states': {name: ['modular_splurt/icons/obj/condom.dmi', state] for name, state in {
        'wrapped': 'b_condom_wrapped', 'empty': 'b_condom', 'filled': 'b_condom_inflated',
        'medium': 'b_condom_inflated_med', 'large': 'b_condom_inflated_large', 'huge': 'b_condom_inflated_huge'}.items()}},
    {'id': 'BlueMoonChastity', 'states': {'icon': ['modular_splurt/icons/obj/lewd_items/chastity.dmi', 'standard_cage']}},
]
for entry in entries:
    convert(source, entry, revision)
    metadata = ROOT / 'Resources/Textures/_DeepLagoon/LustWish' / (entry['id'] + '.rsi') / 'meta.json'
    text = metadata.read_text(encoding='utf-8').replace('WhiteMoon / SPLURT', 'MOLOT BlueMoon / SPLURT')
    text = text.replace('https://github.com/BlueMoon-Labs/WhiteMoon-Station/', 'https://github.com/iamasay/MOLOT-BlueMoon-Station/')
    metadata.write_text(text, encoding='utf-8')
    print(entry['id'])
