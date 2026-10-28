"""Verify every imported penis state, animation frame and direction against the MOLOT DMI.

python Tools/body-customization/verify_penis.py C:/old_disk/repos/MOLOT-BlueMoon-Station
"""
import json
import re
import sys
from pathlib import Path

from PIL import Image
from dmi_states import states


def verify(source):
    root = Path(__file__).resolve().parents[2]
    directory = root / 'Resources/Textures/_DeepLagoon/BodyCustomization/penis.rsi'
    with Image.open(source / 'icons/obj/genitals/penis_onmob.dmi') as dmi:
        description = dmi.info['Description']
        width = int(re.search(r'width = (\d+)', description)[1])
        height = int(re.search(r'height = (\d+)', description)[1])
        columns = dmi.width // width
        expected = {entry[0]: entry for entry in states(description)
                    if re.fullmatch(r'penis_[^_]+_\d+_[01]_(FRONT|BEHIND)', entry[0])}
        metadata = json.loads((directory / 'meta.json').read_text(encoding='utf-8'))
        assert metadata['size'] == {'x': width, 'y': height}
        actual = {entry['name']: entry for entry in metadata['states']}
        assert actual.keys() == expected.keys(), (actual.keys() - expected.keys(), expected.keys() - actual.keys())
        assert {path.stem for path in directory.glob('*.png')} == expected.keys()
        checked = 0
        for name, (_, block, directions, frames, offset) in expected.items():
            assert actual[name]['directions'] == directions, name
            assert all(len(delays) == frames for delays in actual[name]['delays']), name
            with Image.open(directory / f'{name}.png') as imported:
                assert imported.size == (width * directions, height * frames), name
                for direction in range(directions):
                    for frame in range(frames):
                        tile = offset + frame * directions + direction
                        destination = direction * frames + frame
                        original = dmi.crop((tile % columns * width, tile // columns * height,
                                             tile % columns * width + width, tile // columns * height + height)).convert('RGBA')
                        converted = imported.crop((destination % directions * width, destination // directions * height,
                                                   destination % directions * width + width, destination // directions * height + height)).convert('RGBA')
                        assert original.tobytes() == converted.tobytes(), (name, direction, frame)
                        checked += 1
        print(f'Verified {len(expected)} states and {checked} direction frames: both states match MOLOT pixel-for-pixel.')


if __name__ == '__main__':
    verify(Path(sys.argv[1]))
