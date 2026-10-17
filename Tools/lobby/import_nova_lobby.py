"""Convert NovaSector's single-direction lobby DMI sheets to native RSI assets.

Usage: python Tools/lobby/import_nova_lobby.py PATH/TO/icons/hud/lobby
Requires Pillow. DMI delays are deciseconds; RSI delays are seconds.
"""

import argparse
import json
import re
from pathlib import Path

from PIL import Image

ASSETS = ("background", "ready", "join", "observe", "character_setup")
DESTINATION = Path(__file__).resolve().parents[2] / "Resources/Textures/_DeepLagoon/Interface/Lobby"


def convert(source: Path, destination: Path) -> None:
    with Image.open(source) as image:
        metadata = image.info["Description"]
        width = int(re.search(r"\bwidth = (\d+)", metadata)[1])
        height = int(re.search(r"\bheight = (\d+)", metadata)[1])
        image = image.convert("RGBA")
        columns = image.width // width
        offset = 0
        states = []
        destination.mkdir(parents=True, exist_ok=True)
        for block in re.split(r"\nstate = ", metadata)[1:]:
            name = re.match(r'"([\w]+)"', block)[1]
            directions = int(re.search(r"dirs = (\d+)", block)[1])
            frames = int(re.search(r"frames = (\d+)", block)[1])
            if directions != 1:
                raise ValueError(f"{source}: {name} has {directions} directions")
            sheet = Image.new("RGBA", (width * frames, height))
            for frame in range(frames):
                index = offset + frame
                x, y = index % columns * width, index // columns * height
                if y + height > image.height:
                    raise ValueError(f"{source}: frame outside DMI sheet")
                sheet.paste(image.crop((x, y, x + width, y + height)), (frame * width, 0))
            sheet.save(destination / f"{name}.png")
            state = {"name": name}
            if frames > 1:
                delay = re.search(r"delay = ([\d.,]+)", block)
                delays = [float(value) / 10 for value in delay[1].split(",")] if delay else [0.1] * frames
                if len(delays) != frames or any(value <= 0 for value in delays):
                    raise ValueError(f"{source}: invalid frame delays for {name}")
                state["delays"] = [delays]
            states.append(state)
            offset += frames

        manifest = {
            "version": 1,
            "license": "CC-BY-SA-3.0",
            "copyright": f"NovaSector / tgstation contributors. Converted from icons/hud/lobby/{source.name} for DeepLagoon; original pixels and frame timings preserved.",
            "source": f"https://github.com/NovaSector/NovaSector/tree/master/icons/hud/lobby/{source.name}",
            "size": {"x": width, "y": height},
            "states": states,
        }
        (destination / "meta.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
        print(f"{source.name}: {len(states)} states, {offset} frames -> {destination.name}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    args = parser.parse_args()
    for asset in ASSETS:
        convert(args.source / f"{asset}.dmi", DESTINATION / f"{asset}.rsi")
