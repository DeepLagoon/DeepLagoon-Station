"""Preview/export the same pixel captions and packed frames used by native lobby controls."""
import re
import json
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[2]
ART = ROOT / "Resources/Textures/_DeepLagoon/Interface/Lobby"
OUT = ROOT / "artifacts/lobby-validation"
FONT_SOURCE = (ROOT / "Content.Client/Lobby/UI/LobbyPixelText.cs").read_text(encoding="utf-8")
GLYPHS = {key: [int(i) for i in rows.split(",")] for key, rows in re.findall(r"\['(.)'\] = \[([\d,]+)\]", FONT_SOURCE)}
LABELS = [("ahelp", "AHelp"), ("vote", "Голосование"), ("settings", "Настройки"), ("leave", "Выйти"),
          ("rules", "Правила"), ("guidebook", "Руководство"), ("changelog", "Обновления"), ("credits", "Авторы")]


def caption(image, text, color, pressed=False):
    text = text.upper()
    scale = max(1, min(2, (image.width - 20) // (len(text) * 6 - 1)))
    x = (image.width - (len(text) * 6 - 1) * scale) // 2
    y = (image.height - 7 * scale) // 2 + int(pressed)
    draw = ImageDraw.Draw(image)
    for dx, dy, ink in [(1, 1, "#263d38"), (0, 0, color)]:
        for n, letter in enumerate(text):
            for row, mask in enumerate(GLYPHS[letter]):
                for col in range(5):
                    if mask & (1 << (4 - col)):
                        left, top = x + (n * 6 + col) * scale + dx, y + row * scale + dy
                        draw.rectangle((left, top, left + scale - 1, top + scale - 1), fill=ink)


def nine_slice(frame, size, patch=16):
    output = Image.new("RGBA", size)
    xs = [(0, patch, 0, patch), (patch, frame.width - 2 * patch, patch, size[0] - 2 * patch),
          (frame.width - patch, patch, size[0] - patch, patch)]
    ys = [(0, patch, 0, patch), (patch, frame.height - 2 * patch, patch, size[1] - 2 * patch),
          (frame.height - patch, patch, size[1] - patch, patch)]
    for sy, sh, dy, dh in ys:
        for sx, sw, dx, dw in xs:
            tile = frame.crop((sx, sy, sx + sw, sy + sh)).resize((dw, dh), Image.Resampling.NEAREST)
            output.alpha_composite(tile, (dx, dy))
    return output


if __name__ == "__main__":
    OUT.mkdir(parents=True, exist_ok=True)
    sprites = OUT / "utility-sprites"
    sprites.mkdir(exist_ok=True)
    button = Image.open(ART / "utility.rsi/utility.png").convert("RGBA")
    preview = Image.new("RGBA", (340, 536), "#151b23")
    tray = Image.new("RGBA", (340, 164), "#9aa4a1")
    tray.alpha_composite(nine_slice(Image.open(ART / "background.rsi/background.png"), (340, 164), patch=8))
    for index, (name, text) in enumerate(LABELS):
        for state, color in [("normal", "#80f4d8"), ("hover", "#f4d58a"), ("pressed", "#e1fff0"), ("disabled", "#596661")]:
            sprite = button.copy()
            caption(sprite, text, color, state == "pressed")
            sprite.save(sprites / f"{name}-{state}.png")
            if state == "normal":
                tray.alpha_composite(sprite, (12 + index % 2 * 160, 12 + index // 2 * 36))
    preview.alpha_composite(tray)
    attribution = [{
        "files": sorted(path.name for path in sprites.glob("*.png")),
        "license": "CC-BY-SA-3.0",
        "copyright": "Generated with OpenAI ImageGen for DeepLagoon, based on the NovaSector lobby hardware style; palette, Cyrillic LED alphabet and button states adapted for native UI.",
        "source": "https://github.com/NovaSector/NovaSector/tree/master/icons/hud/lobby",
    }]
    (sprites / "attributions.yml").write_text(json.dumps(attribution, indent=2) + "\n", encoding="utf-8")
    console = nine_slice(Image.open(ART / "console-frame.png"), (340, 164))
    draw = ImageDraw.Draw(console)
    font = ImageFont.truetype(str(ROOT / "Resources/Fonts/RobotoMono/RobotoMono-Regular.ttf"), 12)
    lines = ["Добро пожаловать на DeepLagoon Station!", "Текущий раунд: #19", "Игроков на сервере: 1",
             "Текущий режим: Стандарт (ТСФ | ДФ)", ">Местные силы ДФ мешают колониальной", "экспансии ТСФ в сектор."]
    for row, line in enumerate(lines):
        draw.text((20, 18 + row * 20), line, font=font, fill="#e8da75" if row >= 4 else "#a5d6be")
    preview.alpha_composite(console, (0, 174))
    vote = nine_slice(Image.open(ART / "console-frame.png"), (340, 188))
    draw = ImageDraw.Draw(vote)
    draw.text((12, 14), "Сервер начал голосование:", font=font, fill="#a5d6be")
    draw.text((12, 34), "Следующий режим игры", font=font, fill="#80f4d8")
    for row, text in enumerate(["Стандарт (ТСФ | ДФ) (0)", "Динамический (0)"]):
        choice = nine_slice(button, (316, 32), patch=8)
        ink = "#e1fff0" if row == 0 else "#80f4d8"
        ImageDraw.Draw(choice).text(((316 - font.getlength(text)) / 2, 7), text, font=font, fill=ink)
        vote.alpha_composite(choice, (12, 58 + row * 38))
    bar = nine_slice(button, (282, 24), patch=8)
    draw = ImageDraw.Draw(bar)
    draw.rectangle((12, 9, 269, 14), fill="#0d1918")
    fill = (282 - 24) * 0.35
    for x in range(0, 258, 7):
        width = min(5, 258 - x)
        draw.rectangle((12 + x, 9, 12 + x + width - 1, 14), fill="#263d38")
        lit = min(width, max(0, int(fill - x)))
        if lit:
            draw.rectangle((12 + x, 9, 12 + x + lit - 1, 14), fill="#4cac93")
            draw.line((12 + x, 9, 12 + x + lit - 1, 9), fill="#80f4d8")
    vote.alpha_composite(bar, (12, 142))
    ImageDraw.Draw(vote).text((298, 147), "2:08", font=font, fill="#f4d58a")
    preview.alpha_composite(vote, (0, 348))
    destination = OUT / "terminal-preview.png"
    preview.resize((680, 1072), Image.Resampling.NEAREST).save(destination)
    print(destination)
