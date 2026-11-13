"""Export the WhiteMoon bitrunning DMI states as native RSI. Requires Pillow.

Run from any directory with a WhiteMoon-Station Git checkout as the only
argument. Use --check to compare the committed resources against the source.
"""

import argparse
import hashlib
from io import BytesIO
import json
from math import ceil, sqrt
from pathlib import Path
import re
import subprocess
import sys

from PIL import Image


ROOT = Path(__file__).resolve().parents[2]
OUTPUT = ROOT / "Resources/Textures/_DeepLagoon/Bitrunning"
REVISION = "634692776c7b38f998c12907bdbabe3aae46df65"
URL = "https://github.com/BlueMoon-Labs/WhiteMoon-Station/blob"
sys.path.insert(0, str(ROOT / "Tools/body-customization"))
from dmi_states import states as dmi_states


# None selects every state from a dedicated bitrunning or boss atlas.
ATLAS_EXPORTS = (
    ("machines", "icons/obj/machines/bitrunning.dmi", None),
    ("effects", "icons/effects/bitrunning.dmi", None),
    ("effects_48", "icons/effects/bitrunning_48.dmi", None),
    ("effects_64", "icons/effects/bitrunning_64.dmi", None),
    ("netguardian", "icons/mob/nonhuman-player/netguardian.dmi", None),
    ("health_station", "modular_skyrat/modules/bitrunning/icons/health_station.dmi", None),
    ("console", "icons/obj/machines/computer.dmi",
     ("computer", "computer_broken", "mining", "mining_key", "mining_key_off", "bitrunning")),
    ("crate", "icons/obj/storage/crates.dmi", ("bitrunning", "bitrunningopen")),
    ("circuitboard", "icons/obj/devices/circuitry_n_data.dmi", ("circuit_board", "circuit_chip")),
    ("disks", "icons/obj/devices/circuitry_n_data.dmi", (*(f"datadisk{i}" for i in range(8)), "rndmajordisk")),
    ("hololadder", "icons/obj/structures.dmi", ("ladder11",)),
    ("floors", "icons/turf/floors.dmi", ("bitrunning",)),
    ("walls", "icons/turf/floors.dmi", ("binary",)),
    ("areas", "icons/area/areas_station.dmi", ("bit_den", "bit_ruin", "bit_safe", "bit_ice", "bit_space")),
    ("milsim_areas", "icons/area/areas_away_missions.dmi",
     ("unknown", *(f"awaycontent{i}" for i in (*range(1, 6), *range(7, 18))))),
    ("fog", "icons/effects/anomalies.dmi", ("pyroclastic",)),
    ("remains", "icons/effects/blood.dmi", ("remains",)),
    ("host_monitor", "icons/obj/devices/scanner.dmi", ("host_monitor",)),
    ("device_inhand_left", "icons/mob/inhands/items/devices_lefthand.dmi", ("electronic",)),
    ("device_inhand_right", "icons/mob/inhands/items/devices_righthand.dmi", ("electronic",)),
    ("device_belt", "icons/mob/clothing/belt.dmi", ("electronic",)),
    ("sleeper", "icons/obj/machines/sleeper.dmi", ("sleeper_s", "psykerpod")),
    ("human", "icons/mob/simple/simple_human.dmi", ("",)),
    ("mine", "icons/obj/weapons/grenade.dmi", ("landmine-inactive", "landmine")),
    ("rocketbundle", "icons/obj/weapons/guns/ammo.dmi", ("rocketbundle",)),
    ("milsim_id", "modular_skyrat/master_files/icons/obj/card.dmi", ("trim_terragov",)),
    ("milsim_console", "modular_skyrat/modules/advanced_shuttles/icons/computer.dmi", ("intercom",)),
    ("ash_drake", "icons/mob/simple/lavaland/96x96megafauna.dmi",
     ("dragon", "dragon_shadow", "dragon_dead", "dragon_swoop")),
    ("colossus", "icons/mob/simple/lavaland/96x96megafauna.dmi", ("eva", "eva_attack")),
    ("bubblegum", "icons/mob/simple/lavaland/96x96megafauna.dmi", ("bubblegum",)),
    ("hierophant", "icons/mob/simple/lavaland/hierophant_new.dmi", ("hierophant",)),
    ("wendigo", "icons/mob/simple/icemoon/64x64megafauna.dmi",
     ("wendigo", "wendigo_noblood", "wendigo_dead")),
    ("blood_drunk_miner", "icons/mob/simple/broadMobs.dmi", ("miner", "miner_transformed")),
    ("boss_projectile", "icons/obj/weapons/guns/projectiles.dmi", ("chronobolt",)),
    ("drake_flame", "icons/effects/fire.dmi", ("light",)),
    ("drake_safe_ground", "icons/obj/service/hand_of_god_structures.dmi", ("trap-earth",)),
    ("drake_fireball", "icons/effects/magic.dmi", ("fireball",)),
    ("boss_target", "icons/mob/actions/actions_items.dmi", ("sniper_zoom",)),
    ("bubblegum_effects", "icons/effects/bubblegum.dmi", None),
    ("colossus_shield", "icons/effects/effects.dmi", ("at_shield2",)),
    ("hierophant_effects", "icons/effects/effects.dmi",
     ("hierophant_squares", "hierophant_squares_indefinite",
      "hierophant_blast", "hierophant_blast_indefinite")),
    ("hierophant_wall", "icons/turf/walls/hierophant_wall_temp.dmi", None),
    ("hierophant_telegraph", "icons/effects/96x96.dmi",
     ("hierophant_telegraph", "hierophant_telegraph_diagonal", "hierophant_telegraph_cardinal",
      "hierophant_telegraph_teleport", "hierophant_telegraph_edge")),
    ("hierophant_beacon", "icons/obj/mining_zones/artefacts.dmi",
     ("hierophant_tele_off", "hierophant_club_ready_beacon")),
    ("oval_glasses", "icons/obj/clothing/glasses.dmi", ("jensenshades",)),
    ("echo_blindfold", "icons/obj/clothing/glasses.dmi", ("blindfold",)),
    ("echo_blindfold_equipped", "icons/mob/clothing/eyes.dmi", ("blindfold",)),
    ("echo_blindfold_left", "icons/mob/inhands/clothing/glasses_lefthand.dmi", ("blindfold",)),
    ("echo_blindfold_right", "icons/mob/inhands/clothing/glasses_righthand.dmi", ("blindfold",)),
    ("oval_glasses_equipped", "icons/mob/clothing/eyes.dmi", ("jensenshades",)),
    ("leather_trenchcoat", "icons/obj/clothing/suits/jacket.dmi", ("leather_trenchcoat",)),
    ("leather_trenchcoat_equipped", "icons/mob/clothing/suits/jacket.dmi", ("leather_trenchcoat",)),
    ("bitrunner_uniform", "icons/obj/clothing/under/cargo.dmi", ("bitrunner",)),
    ("bitrunner_uniform_equipped", "icons/mob/clothing/under/cargo.dmi", ("bitrunner", "bitrunner_d")),
    ("milsim_tape", "icons/obj/devices/circuitry_n_data.dmi", ("tape_blue", "tape_blue_reverse")),
    ("milsim_tape_inhand_left", "icons/mob/inhands/equipment/tools_lefthand.dmi", ("analyzer",)),
    ("milsim_tape_inhand_right", "icons/mob/inhands/equipment/tools_righthand.dmi", ("analyzer",)),
    ("actions", "icons/hud/actions.dmi", ("round_end",)),
    ("alert", "icons/hud/screen_alert.dmi", ("template", "netpod_stasis")),
    ("fishing_rod", "icons/obj/fishing.dmi",
     ("fishing_rod", "reel_overlay", "hook_overlay", "line_overlay")),
    ("fishing_rod_inhand_left", "icons/mob/inhands/equipment/fishing_rod_lefthand.dmi", None),
    ("fishing_rod_inhand_right", "icons/mob/inhands/equipment/fishing_rod_righthand.dmi", None),
    ("beach", "icons/obj/fluff/beach.dmi", ("shell1", "shell2", "shell3")),
    ("gondola", "icons/mob/simple/gondolas.dmi", None),
    ("deer", "icons/mob/simple/animal.dmi", ("deer-doe", "deer-doe-dead")),
)

# Git history of the dedicated sprite sources provides the original port attribution.
DEDICATED_CREDITS = {
    "machines": "Jeremiah, SyncIt21 and tgstation contributors",
    "effects": "Jeremiah and tgstation contributors",
    "effects_48": "Jeremiah and tgstation contributors",
    "effects_64": "Jeremiah and tgstation contributors",
    "netguardian": "Jeremiah, Jacquerel and tgstation contributors",
    "health_station": "Stalkeros2 and Skyrat contributors",
}


def source_blobs(repo):
    paths = sorted({path for _, path, _ in ATLAS_EXPORTS})
    request = "".join(f"{REVISION}:{path}\n" for path in paths).encode()
    result = subprocess.run(
        ["git", "-C", str(repo), "cat-file", "--batch"],
        input=request, stdout=subprocess.PIPE, check=True,
    )
    data = BytesIO(result.stdout)
    blobs = {}
    for path in paths:
        header = data.readline().split()
        if len(header) != 3 or header[1] != b"blob":
            raise ValueError(f"Missing source atlas {path} at {REVISION}")
        blobs[path] = data.read(int(header[2]))
        if data.read(1) != b"\n":
            raise ValueError(f"Truncated source atlas {path}")
    return blobs


def property_values(block, name):
    match = re.search(rf"(?m)^[ \t]*{name} = ([^\r\n]+)", block)
    return match[1].strip() if match else None


def dimensions(description):
    return tuple(int(property_values(description, axis) or 32) for axis in ("width", "height"))


def export_state(atlas, size, name, block, directions, frames, offset, *, reverse_direction=False):
    if directions not in (1, 4, 8) or frames < 1:
        raise ValueError(f"Invalid DMI state {name}: {directions} directions and {frames} frames")
    source_delay = property_values(block, "delay")
    # DMI delays are in deciseconds. netguardian_emissive has two surplus
    # values in WhiteMoon. Preserve those values in the manifest.
    declared_delays = [float(v) for v in source_delay.split(",")] if source_delay else []
    delays = [value / 10 for value in declared_delays[:frames]]
    delays.extend([0.1] * (frames - len(delays)))
    if any(delay < 0 for delay in delays):
        raise ValueError(f"Negative frame duration in {name}")

    output_name = name or "base"
    attributes = {"name": output_name}
    if directions != 1:
        attributes["directions"] = directions
    if frames != 1 or source_delay:
        attributes["delays"] = [delays] * directions

    # BYOND and RSI share the direction order S, N, E, W, SE, SW, NE, NW.
    # DMI interleaves directions within each frame. RSI groups frames by direction.
    width, height = size
    source_columns = atlas.width // width
    state_columns = ceil(sqrt(directions * frames))
    state_rows = ceil(directions * frames / state_columns)
    sheet = Image.new("RGBA", (state_columns * width, state_rows * height))
    opposite = (1, 0, 3, 2, 7, 6, 5, 4)
    for direction in range(directions):
        for frame in range(frames):
            source_direction = opposite[direction] if reverse_direction and directions != 1 else direction
            index = offset + frame * directions + source_direction
            x, y = index % source_columns * width, index // source_columns * height
            if y + height > atlas.height:
                raise ValueError(f"{name} frames exceed the DMI atlas")
            index = direction * frames + frame
            sheet.paste(atlas.crop((x, y, x + width, y + height)),
                        (index % state_columns * width, index // state_columns * height))

    mapping = {"dmi": name, "rsi": output_name, "directions": directions, "frames": frames}
    for flag in ("loop", "rewind", "movement"):
        if value := property_values(block, flag):
            mapping[flag] = int(value)
    if len(declared_delays) > frames:
        mapping["surplus_delay_deciseconds"] = declared_delays[frames:]
    return attributes, sheet, mapping


def write_json(path, value, check):
    if check:
        if json.loads(path.read_text(encoding="utf-8")) != value:
            raise ValueError(f"Metadata does not match source: {path}")
        return
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n",
                    encoding="utf-8", newline="\n")


def write_frame(path, expected, check):
    if not check:
        expected.save(path)
        return
    with Image.open(path) as image:
        if image.size != expected.size or image.convert("RGBA").tobytes() != expected.tobytes():
            raise ValueError(f"Pixels do not match source: {path}")


def export_atlas(blobs, name, path, selected, check):
    blob = blobs[path]
    with Image.open(BytesIO(blob)) as source:
        description = source.info["Description"]
        image = source.convert("RGBA")
    size = dimensions(description)
    states = []
    mapping = []
    output = OUTPUT / f"{name}.rsi"
    if not check:
        output.mkdir(parents=True, exist_ok=True)
    for state, block, dirs, frames, offset in dmi_states(description):
        if selected is not None and state not in selected:
            continue
        meta, sheet, entry = export_state(image, size, state, block, dirs, frames, offset)
        png = output / f"{meta['name']}.png"
        if any(value["name"] == meta["name"] for value in states):
            raise ValueError(f"Duplicate state {state!r} in {path}")
        write_frame(png, sheet, check)
        states.append(meta)
        mapping.append(entry)
    missing = set(selected or ()) - {entry["dmi"] for entry in mapping}
    if missing:
        raise ValueError(f"{path} is missing states: {', '.join(sorted(missing))}")
    if not states:
        raise ValueError(f"{path} contains no exported states")
    if check and {p.stem for p in output.glob("*.png")} != {s["name"] for s in states}:
        raise ValueError(f"Extra state PNG in {output}")

    authors = DEDICATED_CREDITS.get(name, "tgstation, Skyrat and WhiteMoon contributors")
    copyright = f"{authors}. Repacked from {URL}/{REVISION}/{path} as RSI for DeepLagoon."
    write_json(output / "meta.json", {
        "version": 1,
        "size": {"x": size[0], "y": size[1]},
        "license": "CC-BY-SA-3.0",
        "copyright": copyright,
        "states": states,
    }, check)
    return {
        "source": path,
        "sha256": hashlib.sha256(blob).hexdigest(),
        "rsi": f"{name}.rsi",
        "scope": "all" if selected is None else "selected",
        "states": mapping,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path, help="WhiteMoon-Station Git checkout")
    parser.add_argument("--check", action="store_true", help="validate without writing")
    args = parser.parse_args()
    blobs = source_blobs(args.source)
    atlases = [export_atlas(blobs, *spec, args.check) for spec in ATLAS_EXPORTS]
    write_json(OUTPUT / "source_states.json", {"revision": REVISION, "atlases": atlases}, args.check)
    count = sum(len(atlas["states"]) for atlas in atlases)
    print(f"{'Verified' if args.check else 'Exported'} {count} states in {len(atlases)} RSI folders.")


if __name__ == "__main__":
    main()
