"""Apply reviewed BlueMoon physiology metadata after regenerating the interaction catalog."""
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
FIELDS = "userArousalGain|targetArousalGain|userBodyPart|targetBodyPart|userFluidDestination|targetFluidDestination|rawArousalLimit"
mapping = json.loads(Path(__file__).with_name("interactions.json").read_text(encoding="utf-8"))
path = ROOT / "Resources/Prototypes/_DeepLagoon/interaction_panel_molot_import.yml"
blocks = re.split(r"(?=^- type: interactionPanel\s*$)", path.read_text(encoding="utf-8"), flags=re.M)
seen = set()
for index, block in enumerate(blocks):
    match = re.search(r"^  id: (\S+)", block, re.M)
    if not match:
        continue
    key = match[1]
    values = mapping[key]
    seen.add(key)
    block = re.sub(r"^  (?:arousalGain|" + FIELDS + r"):.*\n", "", block, flags=re.M)
    block = re.sub(r"(^  chatColor:.*$)", lambda m: m[0] + "\n" + "\n".join("  " + k + ": " + str(v) for k, v in values.items()), block, flags=re.M)
    blocks[index] = block
assert seen == mapping.keys(), "The reviewed mapping and imported catalog differ"
path.write_text("".join(blocks), encoding="utf-8")
print(f"Updated {len(seen)} interactions")
