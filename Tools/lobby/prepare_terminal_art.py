"""Pack the generated terminal artwork onto the original lobby sprite palette.

Requires Pillow. Originals are kept in art_sources; runtime assets use nearest
sampling and native sprite sizes, with straight edges suitable for nine-slicing.
"""
import json
from pathlib import Path

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[2]
SOURCE = Path(__file__).parent / "art_sources"
TARGET = ROOT / "Resources/Textures/_DeepLagoon/Interface/Lobby"


def pack(name, size, destination):
    image = Image.open(SOURCE / name).convert("RGBA")
    mask = image.getchannel("A").point(lambda value: 255 if value >= 128 else 0)
    image = image.crop(mask.getbbox()).resize(size, Image.Resampling.NEAREST)
    reference = Image.open(TARGET / "background.rsi/background.png").convert("RGBA")
    palette = {pixel[:3] for pixel in reference.getdata() if pixel[3] > 128}
    palette.update([(13, 25, 24), (20, 34, 32), (39, 54, 49), (76, 172, 147), (128, 244, 216)])
    pixels = []
    for red, green, blue, alpha in image.getdata():
        color = min(palette, key=lambda p: (p[0] - red) ** 2 + (p[1] - green) ** 2 + (p[2] - blue) ** 2)
        pixels.append((*color, 255 if alpha >= 128 else 0))
    image.putdata(pixels)
    # Reserve clean, flat screen space for the game's live text, keeping the generated casing.
    screen = (16, 16, size[0] - 17, size[1] - 17) if size[1] == 80 else (14, 10, size[0] - 15, size[1] - 11)
    ImageDraw.Draw(image).rectangle(screen, fill=(13, 25, 24, 255))
    destination.parent.mkdir(parents=True, exist_ok=True)
    image.save(destination)


if __name__ == "__main__":
    pack("console-frame.png", (192, 80), TARGET / "console-frame.png")
    pack("utility-button.png", (156, 32), TARGET / "utility.rsi/utility.png")
    meta = {
        "version": 1, "license": "CC-BY-SA-3.0",
        "copyright": "Generated with OpenAI ImageGen for DeepLagoon, based on the NovaSector lobby style. Palette and pixel grid adapted for native UI.",
        "size": {"x": 156, "y": 32}, "states": [{"name": "utility"}],
    }
    (TARGET / "utility.rsi/meta.json").write_text(json.dumps(meta, indent=2) + "\n", encoding="utf-8")
