"""Convert the supplied shower DMI to native RSI, preserving directions and animation.

Run `py -3 Tools/shower/import_shower.py` to export, or append `--check` to
compare every output pixel and timing against the committed source DMI.
Requires Pillow, as does the existing bitrunning DMI exporter.
"""

import argparse
import json
from pathlib import Path
import sys

from PIL import Image

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "Tools/bitrunning"))
from import_sprites import dimensions, dmi_states, export_state

SOURCE = Path(__file__).parent / "sources/shower.dmi"
OUTPUT = ROOT / "Resources/Textures/_DeepLagoon/Structures/shower.rsi"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    with Image.open(SOURCE) as atlas:
        description = atlas.info["Description"]
        size = dimensions(description)
        metadata = {
            "version": 1,
            "size": {"x": size[0], "y": size[1]},
            "license": "Custom",
            "copyright": "Supplied by the project owner as shower.dmi on 2026-11-26. Original author and license were not specified. Source: Tools/shower/sources/shower.dmi.",
            "states": [],
        }
        OUTPUT.mkdir(parents=True, exist_ok=True)
        for state, block, directions, frames, offset in dmi_states(description):
            attributes, sheet, _ = export_state(atlas, size, state, block, directions, frames, offset)
            metadata["states"].append(attributes)
            target = OUTPUT / f"{state}.png"
            if args.check:
                with Image.open(target) as existing:
                    assert existing.size == sheet.size and existing.convert("RGBA").tobytes() == sheet.tobytes(), target
            else:
                sheet.save(target)
        assert [entry["name"] for entry in metadata["states"]] == ["shower", "water", "mist"]
        target = OUTPUT / "meta.json"
        if args.check:
            assert json.loads(target.read_text(encoding="utf-8")) == metadata, target
        else:
            target.write_text(json.dumps(metadata, indent=2) + "\n", encoding="utf-8", newline="\n")
    print("Shower RSI matches source DMI." if args.check else "Exported shower, water and mist RSI states.")


if __name__ == "__main__":
    main()
