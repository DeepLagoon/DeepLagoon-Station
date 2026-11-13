"""Convert the WhiteMoon Bitrunning DMM pack to Robust grid maps."""

from __future__ import annotations

import argparse
import base64
import hashlib
import json
import math
import re
import struct
import subprocess
import tomllib
from collections import Counter, defaultdict
from dataclasses import dataclass, replace
from functools import cache
from pathlib import Path

from map_art import MapArt

SOURCE_COMMIT = "634692776c7b38f998c12907bdbabe3aae46df65"
DOMAIN_ROOT = "_maps/virtual_domains/"
SAFEHOUSE_ROOT = "_maps/safehouses/"
SAFEHOUSE_CONFIG = "strings/modular_maps/safehouse.toml"
MAP_ROOT = Path("Resources/Maps/_DeepLagoon/Bitrunning")
DICTIONARY = re.compile(r'^"([A-Za-z]+)"\s*=\s*\(', re.M)
BLOCK = re.compile(r'^\((\d+),\s*(\d+),\s*(\d+)\)\s*=\s*\{"\s*\n(.*?)^"\}', re.M | re.S)
ASSIGNMENT = re.compile(r'^\s*(\w+)\s*=\s*(.*?)\s*$', re.S)
DIRS = {1: "180", 2: "0", 4: "90", 8: "-90"}
TILE_BYTES = struct.Struct("<IBBB")
CHUNK_SIZE = 16


@dataclass(frozen=True)
class Atom:
    path: str
    fields: dict[str, str]


@dataclass(frozen=True)
class Entity:
    proto: str
    position: tuple[float, float]
    rotation: str = "0"
    name: str = ""
    description: str = ""
    link: str = ""
    sprite_offset: tuple[float, float] | None = None
    sprite_color: str = ""


def git(repo, *args, **kwargs):
    return subprocess.run(["git", "-C", str(repo), *args], capture_output=True, check=True, **kwargs).stdout


def read_git_blobs(repo, paths):
    queries = "".join(f"{SOURCE_COMMIT}:{path}\n" for path in paths).encode()
    output = git(repo, "cat-file", "--batch", input=queries)
    blobs = {}
    offset = 0
    for path in paths:
        end = output.index(b"\n", offset)
        _, kind, size = output[offset:end].split()
        if kind != b"blob":
            raise ValueError(f"{path}: expected a Git blob")
        offset = end + 1
        blobs[path] = output[offset:offset + int(size)]
        offset += int(size) + 1
    return blobs


def split_dm(text, separator, final=None):
    """Split only at top-level separators, honoring BYOND strings and lists."""
    parts = []
    start = 0
    quote = None
    escape = False
    stack = []
    for index, char in enumerate(text):
        if quote is not None:
            if escape:
                escape = False
            elif char == "\\":
                escape = True
            elif char == quote:
                quote = None
            continue
        if not stack and (char == separator or char == final):
            parts.append(text[start:index].strip())
            if char == final:
                return parts
            start = index + 1
            continue
        if char in "\"'":
            quote = char
        elif char in "({[":
            stack.append(char)
        elif char in ")}]":
            if not stack:
                raise ValueError(f"unexpected {char!r} near {text[index:index + 40]!r}")
            stack.pop()
    if final is not None:
        raise ValueError(f"unterminated DM tuple near {text[:80]!r}")
    parts.append(text[start:].strip())
    return parts


def parse_atom(text):
    path, delimiter, payload = text.partition("{")
    path = path.strip()
    if not re.fullmatch(r"/(?:[A-Za-z0-9_]+/)*[A-Za-z0-9_]+", path):
        raise ValueError(f"invalid atom path: {path!r}")
    fields = {}
    if delimiter:
        for assignment in split_dm(payload, ";", "}"):
            match = ASSIGNMENT.fullmatch(assignment)
            if match:
                fields[match[1]] = match[2]
    return Atom(path, fields)


def parse_dmm(data):
    text = data.decode("utf-8-sig").replace("\r\n", "\n")
    first_block = BLOCK.search(text)
    if not first_block:
        raise ValueError("missing map cells")
    dictionary_text = text[:first_block.start()]
    dictionary = {}
    rows = list(DICTIONARY.finditer(dictionary_text))
    for i, row in enumerate(rows):
        end = rows[i + 1].start() if i + 1 < len(rows) else len(dictionary_text)
        dictionary[row[1]] = tuple(parse_atom(atom) for atom in split_dm(dictionary_text[row.end():end], ",", ")") if atom)
    cells = {}
    key_size = len(next(iter(dictionary)))
    for block in BLOCK.finditer(text, first_block.start()):
        x, y, z = map(int, block.groups()[:3])
        if z != 1:
            raise ValueError(f"multiple Z-levels require explicit topology: {z}")
        rows = block[4].strip().splitlines()
        for dy, row in enumerate(reversed(rows)):
            if len(row) % key_size:
                raise ValueError(f"row width {len(row)} is not divisible by key size {key_size}")
            for dx, start in enumerate(range(0, len(row), key_size)):
                coord = x + dx - 1, y + dy - 1
                if coord in cells:
                    raise ValueError(f"duplicate cell: {coord}")
                cells[coord] = dictionary[row[start:start + key_size]]
    return cells


def as_int(value, default=0):
    try:
        return int(value)
    except (ValueError, TypeError):
        return default


def as_string(value):
    if value and len(value) >= 2 and value[0] == value[-1] == '"':
        try:
            return json.loads(value)
        except json.JSONDecodeError:
            return value[1:-1]
    return ""


def merge_safehouses(cells, safehouses):
    result = dict(cells)
    for (x, y), atoms in cells.items():
        for atom in atoms:
            if atom.path != "/obj/modular_map_root/safehouse":
                continue
            key = as_string(atom.fields.get("key"))
            room = safehouses[key]
            connector = [(rx, ry) for (rx, ry), things in room.items()
                         if any(thing.path == "/obj/modular_map_connector" for thing in things)]
            if len(connector) > 1:
                raise ValueError(f"{key}: expected at most one safehouse connector, found {connector}")
            cx, cy = connector[0] if connector else (0, 0)
            for (rx, ry), things in room.items():
                pos = x + rx - cx, y + ry - cy
                underlying = result.get(pos, ())
                floor_is_transparent = any(thing.path == "/turf/template_noop" for thing in things)
                objects = tuple(thing for thing in underlying if not thing.path.startswith(("/turf", "/area", "/obj/modular_map_root")))
                if floor_is_transparent:
                    objects += tuple(thing for thing in underlying if thing.path.startswith("/turf"))
                    things = tuple(thing for thing in things if thing.path != "/turf/template_noop")
                result[pos] = objects + things
    return result


def target_prototypes(station):
    """Read IDs without parsing unrelated YAML tags or deprecated prototypes."""
    kinds = {"tile": set(), "entity": set(), "noRotation": set()}
    parents = {}
    rotation_flags = {}
    header = re.compile(r"^- type:\s*(tile|entity)\s*(?:#.*)?$", re.M)
    for file in (station / "Resources" / "Prototypes").rglob("*.yml"):
        if file.name == "WhiteMoonArt.yml":
            continue
        text = file.read_text(encoding="utf-8-sig", errors="replace")
        for match in header.finditer(text):
            stop = re.search(r"^- type:", text[match.end():], re.M)
            end = match.end() + stop.start() if stop else len(text)
            body = text[match.end():end]
            proto = re.search(r"^\s{2}id:\s*([\w.]+)", body, re.M)
            abstract = re.search(r"^\s{2}abstract:\s*true", body, re.M)
            if not proto:
                continue
            proto_id = proto[1]
            if not abstract:
                kinds[match[1]].add(proto_id)
            if match[1] != "entity":
                continue
            parent = re.search(r"^  parent:[ \t]*(.*)$", body, re.M)
            if parent:
                value = parent[1].partition("#")[0].strip()
                if value:
                    parents[proto_id] = re.findall(r"[\w.]+", value)
                else:
                    block = body[parent.end():].splitlines()
                    parents[proto_id] = []
                    for line in block:
                        if not line.strip():
                            continue
                        entry = re.match(r"^\s+-\s+([\w.]+)", line)
                        if not entry:
                            break
                        parents[proto_id].append(entry[1])
            transform = re.search(r"^( +)- type: Transform\b[^\n]*\n", body, re.M)
            if transform:
                rest = body[transform.end():]
                next_component = re.search(rf"^{transform[1]}- type:", rest, re.M)
                fields = rest[:next_component.start()] if next_component else rest
                flag = re.search(r"^\s+noRot:\s*(true|false)\b", fields, re.I | re.M)
                if flag:
                    rotation_flags[proto_id] = flag[1].lower() == "true"

    @cache
    def forbids_rotation(proto):
        if proto in rotation_flags:
            return rotation_flags[proto]
        return any(forbids_rotation(parent) for parent in parents.get(proto, ()))

    kinds["noRotation"] = {proto for proto in kinds["entity"] if forbids_rotation(proto)}
    return kinds


# More specific entries take precedence over broader families.
TURFS = {
    "template_noop": ("Space", ""),
    "open/space": ("Space", ""),
    "open/floor/plating/abductor": ("FloorXeno", ""),
    "open/floor/plating/abductor2": ("FloorXenoSteel", ""),
    "open/floor/plating/heretic_rust": ("PlatingDamaged", ""),
    "open/floor/plating/snowed": ("PlatingSnow", ""),
    "open/floor/plating": ("Plating", ""),
    "open/floor/iron/dark/diagonal": ("FloorDarkDiagonal", ""),
    "open/floor/iron/dark/herringbone": ("FloorDarkHerringbone", ""),
    "open/floor/iron/dark/side": ("FloorDarkOffset", ""),
    "open/floor/iron/dark/smooth_edge": ("FloorDarkPavement", ""),
    "open/floor/iron/dark/smooth_large": ("FloorDarkMono", ""),
    "open/floor/iron/dark/textured_edge": ("FloorDarkPavement", ""),
    "open/floor/iron/dark/textured_large": ("FloorDarkMini", ""),
    "open/floor/iron/dark": ("FloorDark", ""),
    "open/floor/iron/white/textured_large": ("FloorWhiteMini", ""),
    "open/floor/iron/white": ("FloorWhite", ""),
    "open/floor/iron/grimy": ("FloorSteelDirty", ""),
    "open/floor/iron/smooth_corner": ("FloorSteelPavement", ""),
    "open/floor/iron/smooth_half": ("FloorSteelOffset", ""),
    "open/floor/iron/smooth_large": ("FloorSteelMono", ""),
    "open/floor/iron/smooth": ("FloorSteelMono", ""),
    "open/floor/iron/cafeteria": ("FloorBar", ""),
    "open/floor/iron/kitchen": ("FloorKitchen", ""),
    "open/floor/iron/freezer": ("FloorFreezer", ""),
    "open/floor/iron/stairs": ("FloorSteel", ""),
    "open/floor/iron/vaporwave": ("FloorArcadeBlue", ""),
    "open/floor/iron": ("FloorSteel", ""),
    "open/floor/catwalk_floor/iron_dark": ("FloorTileCatwalkHorizontal", ""),
    "open/floor/catwalk_floor/iron": ("FloorTileCatwalkHorizontal", ""),
    "open/floor/carpet/blue": ("Plating", "CarpetBlue"),
    "open/floor/carpet/cyan": ("Plating", "CarpetCyan"),
    "open/floor/carpet/green": ("Plating", "CarpetGreen"),
    "open/floor/carpet/orange": ("Plating", "CarpetOrange"),
    "open/floor/carpet/purple": ("Plating", "CarpetPurple"),
    "open/floor/carpet/royalblack": ("Plating", "CarpetBlack"),
    "open/floor/carpet/royalblue": ("Plating", "CarpetBlue"),
    "open/floor/carpet": ("Plating", "Carpet"),
    "open/floor/eighties": ("FloorEighties", ""),
    "open/floor/grass": ("FloorGrass", ""),
    "open/floor/holofloor/basalt": ("FloorBasalt", ""),
    "open/floor/holofloor/beach/water": ("FloorRiverbed", "FloorWaterDecorativeEntity"),
    "open/floor/holofloor/beach": ("FloorDesert", ""),
    "open/floor/holofloor/hyperspace": ("FloorShuttleBlue", ""),
    "open/floor/light/colour_cycle": ("FloorArcadeRed", ""),
    "open/floor/mineral/plastitanium/red": ("FloorShuttleRed", ""),
    "open/floor/mineral/plastitanium": ("FloorShuttleBlack", ""),
    "open/floor/noslip": ("FloorMetalDiamond", ""),
    "open/floor/plastic": ("FloorPlastic", ""),
    "open/floor/pod/dark": ("FloorShuttleBlack", ""),
    "open/floor/pod/light": ("FloorShuttleWhite", ""),
    "open/floor/pod": ("FloorShuttleGrey", ""),
    "open/floor/sepia": ("FloorLino", ""),
    "open/floor/wood/large": ("FloorWoodLarge", ""),
    "open/floor/wood/parquet": ("FloorWoodTile", ""),
    "open/floor/wood": ("FloorWood", ""),
    "open/floor": ("FloorSteel", ""),
    "open/indestructible/binary": ("FloorGreenCircuit", ""),
    "open/indestructible/bitrunning_transport": ("FloorGreenCircuit", ""),
    "open/indestructible/dark": ("FloorDark", ""),
    "open/indestructible/hierophant/two": ("FloorRedCircuit", ""),
    "open/indestructible/hierophant": ("FloorReinforced", ""),
    "open/indestructible/honk": ("FloorClown", ""),
    "open/indestructible/hotelwood": ("FloorWood", ""),
    "open/indestructible/light": ("FloorWhite", ""),
    "open/indestructible/meat": ("FloorFlesh", ""),
    "open/indestructible/necropolis": ("FloorRockVault", ""),
    "open/indestructible/permalube": ("FloorIce", ""),
    "open/indestructible/white": ("FloorWhite", ""),
    "open/lava/plasma": ("FloorRiverbed", "BitrunPlasmaLake"),
    "open/lava": ("FloorBasalt", "FloorLavaEntity"),
    "open/misc/asteroid/basalt": ("FloorBasalt", ""),
    "open/misc/asteroid/snow": ("FloorSnow", ""),
    "open/misc/asteroid": ("FloorAsteroidSand", ""),
    "open/misc/beach/coast": ("FloorDesert", ""),
    "open/misc/beach/sand": ("FloorDesert", ""),
    "open/misc/dirt": ("FloorDirt", ""),
    "open/misc/grass/jungle": ("FloorGrassJungle", ""),
    "open/misc/grass": ("FloorGrass", ""),
    "open/misc/ice": ("FloorIce", ""),
    "open/water": ("FloorRiverbed", "FloorWaterEntity"),
    "open/chasm": ("Plating", "FloorChasmEntity"),
    "open/cliff/snowrock": ("FloorSnow", "FloorSnowChasm"),
    "closed/indestructible/alien": ("FloorXeno", "BitrunAlienWall"),
    "closed/indestructible/binary": ("FloorGreenCircuit", "BitrunVirtualWall"),
    "closed/indestructible/fakedoor": ("Plating", "BitrunFakeDoor"),
    "closed/indestructible/meat": ("FloorFlesh", "BitrunFleshWall"),
    "closed/indestructible/necropolis": ("FloorRockVault", "BitrunStoneWall"),
    "closed/indestructible/opsglass": ("FloorHull", "BitrunObservationWindow"),
    "closed/indestructible/reinforced": ("FloorReinforced", "BitrunReinforcedWall"),
    "closed/indestructible/riveted/hierophant": ("FloorReinforced", "BitrunReinforcedWall"),
    "closed/indestructible/rock/snow": ("FloorSnow", "BitrunIceWall"),
    "closed/indestructible/rock": ("FloorBasalt", "BitrunStoneWall"),
    "closed/indestructible/syndicate": ("FloorShuttleBlack", "WallPlastitaniumIndestructible"),
    "closed/mineral/bananium": ("FloorAsteroidSand", "WallRockBananium"),
    "closed/mineral/random/jungle": ("FloorGrassJungle", "WallRockAndesite"),
    "closed/mineral/random/volcanic": ("FloorBasalt", "WallRockBasalt"),
    "closed/mineral/volcanic": ("FloorBasalt", "WallRockBasalt"),
    "closed/mineral": ("FloorAsteroidSand", "WallRock"),
    "closed/wall/heretic_rust": ("PlatingDamaged", "WallSolidRust"),
    "closed/wall/ice": ("FloorIce", "WallIce"),
    "closed/wall/mineral/abductor": ("FloorXeno", "WallSolidChitin"),
    "closed/wall/mineral/plastitanium": ("FloorShuttleBlack", "WallPlastitanium"),
    "closed/wall/mineral/sandstone": ("FloorDesert", "WallSandstone"),
    "closed/wall/mineral/silver": ("FloorSilver", "WallSilver"),
    "closed/wall/mineral/titanium": ("FloorHull", "WallShuttle"),
    "closed/wall/mineral/wood": ("FloorWood", "WallWood"),
    "closed/wall/r_wall/plastitanium": ("FloorShuttleBlack", "WallPlastitanium"),
    "closed/wall/r_wall": ("FloorReinforced", "WallReinforced"),
    "closed/wall/rock": ("FloorBasalt", "WallRockBasalt"),
    "closed/wall/rust": ("PlatingDamaged", "WallSolidRust"),
    "closed/wall": ("Plating", "WallSolid"),
}

OBJECTS = {
    "obj/item/bodybag": "BodyBagFolded",
    "obj/effect/decal/cleanable/dirt": "Dirt",
    "obj/effect/decal/cleanable/glass": "ShardGlass",
    "obj/effect/decal/cleanable/confetti": "Confetti",
    "obj/effect/light_emitter": "BitrunAmbientLight",
    "obj/machinery/light_switch": "SignalSwitchDirectional",
    "obj/machinery/light/small": "PoweredSmallLight",
    "obj/machinery/light": "Poweredlight",
    "obj/machinery/door/airlock/wood": "Airlock",
    "obj/machinery/door/airlock/glass": "AirlockGlass",
    "obj/machinery/door/airlock/external": "AirlockExternal",
    "obj/machinery/door/airlock/maintenance": "AirlockMaint",
    "obj/machinery/door/airlock/mining": "AirlockMining",
    "obj/machinery/door/airlock/engineering": "AirlockEngineering",
    "obj/machinery/door/airlock/medical": "AirlockMedical",
    "obj/machinery/door/airlock/research": "AirlockScience",
    "obj/machinery/door/airlock/security": "AirlockSecurity",
    "obj/machinery/door/airlock/freezer": "AirlockFreezer",
    "obj/machinery/door/airlock": "Airlock",
    "obj/machinery/door/puzzle/keycard/yellow_required": "BitrunYellowLockedDoor",
    "obj/machinery/door/puzzle/keycard/blue_required": "BitrunBlueLockedDoor",
    "obj/machinery/door/poddoor/preopen": "BlastDoorOpen",
    "obj/machinery/door/poddoor/ancient_milsim": "BitrunMilsimFog",
    "obj/machinery/door/poddoor/shutters/preopen": "ShuttersNormalOpen",
    "obj/machinery/door/poddoor/shutters/window": "ShuttersWindow",
    "obj/machinery/door/poddoor/shutters": "ShuttersNormal",
    "obj/machinery/door/poddoor": "BlastDoor",
    "obj/machinery/door/firedoor": "Firelock",
    "obj/machinery/door/password/voice/sfc": "Airlock",
    "obj/structure/mineral_door/wood": "WoodDoor",
    "obj/structure/mineral_door": "MetalDoor",
    "obj/structure/door_assembly/door_assembly_eng": "AirlockAssemblyEngineering",
    "obj/machinery/vending/cigarette": "VendingMachineCigs",
    "obj/machinery/vending/cola": "VendingMachineCola",
    "obj/machinery/vending/boozeomat": "VendingMachineBooze",
    "obj/machinery/vending/snack": "VendingMachineSnack",
    "obj/machinery/vending/coffee": "VendingMachineCoffee",
    "obj/machinery/vending/medical": "VendingMachineMedical",
    "obj/machinery/vending/engineering": "VendingMachineEngivend",
    "obj/machinery/atmospherics/pipe/smart/manifold4w": "GasPipeFourway",
    "obj/machinery/atmospherics/pipe/smart/manifold": "GasPipeTJunction",
    "obj/machinery/atmospherics/pipe": "GasPipeStraight",
    "obj/machinery/atmospherics/components/unary/vent_pump": "GasVentPump",
    "obj/machinery/atmospherics/components/unary/vent_scrubber": "GasVentScrubber",
    "obj/machinery/disposal": "DisposalUnit",
    "obj/machinery/sleeper": "CryoPod",
    "obj/machinery/cryopod": "CryoPod",
    "obj/machinery/computer/terminal": "ComputerTerminal",
    "obj/machinery/computer/arcade": "SpaceVillainArcade",
    "obj/machinery/computer/atmos_alert": "ComputerAlert",
    "obj/machinery/computer/security/telescreen": "WallmountTelescreen",
    "obj/machinery/computer/security/wooden_tv": "ComputerTelevision",
    "obj/machinery/computer/security": "ComputerSurveillanceCameraMonitor",
    "obj/machinery/computer": "ComputerTabletopComms",
    "obj/machinery/jukebox": "Jukebox",
    "obj/machinery/chem_dispenser/drinks/beer": "BoozeDispenser",
    "obj/machinery/chem_dispenser/drinks": "SodaDispenser",
    "obj/machinery/reagentgrinder": "KitchenReagentGrinder",
    "obj/item/reagent_containers/condiment/saltshaker": "FoodShakerSalt",
    "obj/item/reagent_containers/condiment/peppermill": "FoodShakerPepper",
    "obj/machinery/grill": "KitchenMicrowave",
    "obj/machinery/microwave": "KitchenMicrowave",
    "obj/machinery/chem_master": "ChemMaster",
    "obj/machinery/smartfridge": "SmartFridge",
    "obj/machinery/suit_storage_unit": "SuitStorageBase",
    "obj/machinery/power/apc": "APCBasic",
    "obj/structure/alien/weeds": "BitrunAlienGrowth",
    "obj/structure/alien/resin/wall": "BitrunResinWall",
    "obj/structure/alien/resin/membrane": "Window",
    "obj/structure/alien/resin": "WallSolidChitin",
    "obj/structure/disposalpipe/segment": "DisposalPipe",
    "obj/structure/disposalpipe/junction/flip": "DisposalJunctionFlipped",
    "obj/structure/disposalpipe/junction": "DisposalJunction",
    "obj/structure/disposalpipe/trunk": "DisposalTrunk",
    "obj/structure/disposalpipe": "DisposalPipe",
    "obj/structure/window/reinforced": "WindowReinforcedDirectional",
    "obj/structure/window": "WindowDirectional",
    "obj/structure/grille": "Grille",
    "obj/structure/flora/tree/palm": "FloraTree",
    "obj/structure/flora/tree/pine": "FloraTreeConifer",
    "obj/structure/flora/tree/stump": "FloraTreeStump",
    "obj/structure/flora/tree/jungle": "FloraTree",
    "obj/structure/flora/tree": "FloraTree",
    "obj/structure/flora/rock": "FloraRockSolid",
    "obj/structure/flora/ash": "FloraRockSolid",
    "obj/structure/flora/bush": "",
    "obj/structure/flora/grass": "",
    "obj/structure/table/reinforced": "TableReinforced",
    "obj/structure/table/bronze": "TableBrass",
    "obj/structure/table/wood": "TableWood",
    "obj/structure/table/glass": "TableGlass",
    "obj/structure/table": "Table",
    "obj/structure/chair/office": "ChairOfficeDark",
    "obj/structure/chair/wood": "ChairWood",
    "obj/structure/chair/comfy": "ComfyChair",
    "obj/structure/chair": "Chair",
    "obj/structure/barricade/sandbags": "BarricadeDirectional",
    "obj/structure/barricade": "Barricade",
    "obj/structure/closet/crate/secure": "CrateSecure",
    "obj/structure/closet/crate/wood": "CrateWood",
    "obj/structure/closet/crate/wooden": "CrateWood",
    "obj/structure/closet/crate/freezer": "CrateFreezer",
    "obj/structure/closet/crate": "CrateGenericSteel",
    "obj/structure/closet/secure_closet": "LockerSteel",
    "obj/structure/closet": "ClosetSteelBase",
    "obj/structure/lattice": "",
    "obj/structure/railing": "Railing",
    "obj/structure/cable": "CableApcExtension",
    "obj/structure/curtain/bounty/start_closed": "CurtainsRed",
    "obj/structure/curtain": "CurtainsWhiteOpen",
    "obj/structure/rack": "Rack",
    "obj/structure/bed": "Bed",
    "obj/structure/stool": "Stool",
    "obj/structure/sink": "Sink",
    "obj/structure/toilet": "ToiletEmpty",
    "obj/structure/shower": "Shower",
    "obj/structure/reagent_dispensers/watertank": "WaterTank",
    "obj/structure/reagent_dispensers/fueltank": "WeldingFuelTank",
    "obj/structure/filingcabinet": "FilingCabinet",
    "obj/structure/inflatable": "InflatableWallStack1",
    "obj/structure/fake_stairs": "",
    "obj/item/fishing_rod": "BitrunFishingRod",
    "obj/item/keycard/yellow": "BitrunYellowKeycard",
    "obj/item/keycard/blue": "BitrunBlueKeycard",
    "obj/item/flashlight": "FlashlightLantern",
    "obj/item/tank/internals/emergency_oxygen": "EmergencyOxygenTankFilled",
    "obj/item/tank/internals/oxygen": "OxygenTankFilled",
    "obj/item/clothing/mask/breath": "ClothingMaskBreath",
    "obj/item/clothing/mask/gas": "ClothingMaskGas",
    "obj/item/tank/jetpack": "JetpackBlueFilled",
    "obj/item/storage/box/survival": "BoxSurvival",
    "obj/item/storage/firstaid": "MedkitFilled",
    "obj/item/reagent_containers/cup/glass/drink": "DrinkGlass",
    "obj/item/reagent_containers/cup/glass": "DrinkGlass",
    "obj/item/reagent_containers/cup/soda_cans": "DrinkColaCan",
    "obj/item/reagent_containers/cup/bottle": "DrinkBeerBottleFull",
    "obj/item/gun/energy/laser": "WeaponLaserCarbine",
    "obj/item/gun/ballistic/automatic": "WeaponSubMachineGunDrozd",
    "obj/item/gun/ballistic/shotgun": "WeaponShotgunDoubleBarreled",
    "obj/item/gun/ballistic/revolver": "WeaponRevolverInspector",
    "obj/item/gun/ballistic": "WeaponPistolMk58",
    "obj/item/stack/sheet/iron": "SheetSteel",
    "obj/item/stack/sheet/glass": "SheetGlass",
    "obj/item/stack/sheet/mineral/coal": "Coal",
    "obj/item/stack/sheet/mineral/plasma": "SheetPlasma",
    "obj/item/stack/sheet/mineral/wood": "MaterialWoodPlank",
    "obj/item/pickaxe": "Pickaxe",
    "obj/item/paper": "Paper",
    "obj/item/food/pizza": "FoodPizzaMargherita",
    "obj/item/food/burger": "FoodBurgerPlain",
    "obj/item/food/grown/banana": "FoodBanana",
    "obj/item/food/grown/apple": "FoodApple",
    "obj/item/food/grown/coconut": "FoodCoconut",
    "obj/item/food/grown": "FoodApple",
    "obj/item/toy/seashell": "BitrunSeashell",
    "obj/item/coin": "SpaceCash",
    "obj/item/bitrunning_disk": "BitrunDiskKinetic",
    "obj/item/tape/ruins/ancient_milsim": "BitrunMilsimLog",
    "obj/item/reagent_containers/cup/glass/drinkingglass/filled/pina_colada": "DrinkPinaColadaGlass",
    "obj/machinery/iv_drip/health_station": "BitrunHealthStation",
    "obj/machinery/iv_drip": "MedicalBed",
    "obj/machinery/button/door/indestructible/ancient_milsim": "BitrunStageButton",
    "obj/machinery/button": "SignalButton",
    "obj/structure/landmine": "BitrunMilsimMine",
    "obj/item/minespawner/ancient_milsim": "BitrunMilsimMine",
    "obj/effect/fog_of_war": "BitrunMilsimFog",
    "obj/structure/hololadder": "",
    "obj/structure/bed/bunk": "Bed",
    "obj/structure/flora/tree/pine/snow": "FloraTreeSnow",
    "obj/structure/flora/tree/snow": "FloraTreeSnow",
    "obj/structure/flora/bush/reed": "",
    "obj/structure/potted_plant": "PottedPlantRandom",
    "obj/structure/fans/tiny": "",
    "obj/structure/table/plasmaglass": "TablePlasmaGlass",
    "obj/structure/tent": "Barricade",
    "obj/structure/catwalk": "Catwalk",
    "obj/machinery/door/airlock/abductor": "AirlockXeno",
    "obj/structure/showcase": "TableGlass",
    "obj/machinery/door/window": "Windoor",
    "obj/item/food/pizzaslice": "FoodPizzaMargheritaSlice",
    "obj/item/toy/beach_ball": "BeachBall",
    "obj/item/stack/medical/gauze": "Gauze",
    "obj/item/stack/medical/bruise_pack": "BruisePack",
    "obj/item/stack/medical/ointment": "Ointment",
    "obj/item/stack/medical/bone_gel": "",
    "obj/item/stack/medical": "Gauze",
    "obj/item/storage/medkit": "MedkitFilled",
    "obj/item/storage/toolbox/fishing": "ToolboxEmergency",
    "obj/item/storage/toolbox": "ToolboxMechanical",
    "obj/item/storage/box/utensils": "BoxPlasticCutlery",
    "obj/item/kinetic_crusher": "WeaponCrusher",
    "obj/item/gun/energy/recharge/kinetic_accelerator": "WeaponProtoKineticAccelerator",
    "obj/item/clothing/glasses/meson": "ClothingEyesGlassesMeson",
    "obj/item/clothing/shoes/sandal": "ClothingShoesSandals",
    "obj/item/clothing/head/cone": "ClothingHeadHatCone",
    "obj/item/stack/rods": "PartRodMetal",
    "obj/item/restraints/legcuffs/beartrap": "BearTrap",
    "obj/item/grown/log": "Log",
    "obj/item/cigbutt": "CigaretteSpent",
    "obj/item/shard": "ShardGlass",
    "obj/item/radio/intercom": "IntercomCommon",
    "obj/item/radio": "HandheldRadio",
    "obj/item/camera": "Camera",
    "obj/item/knife/combat": "CombatKnife",
    "obj/item/ammo_box/magazine/c35sol_pistol/stendo": "Magazine45_ACPPistolHighCapacityFMJ",
    "obj/item/ammo_box/magazine/c35sol_pistol": "Magazine45_ACPPistolFMJ",
    "obj/item/ammo_box/magazine/m9mm": "MagazinePistol",
    "obj/item/ammo_box/magazine/m7mm": "MagazineLightRifle",
    "obj/item/ammo_box/magazine/lanca": "MagazineLightRifle",
    "obj/item/ammo_box/c35sol": "MagazineBoxPistol",
    "obj/item/ammo_casing/rocket": "CartridgeRocket",
    "obj/item/ammo_casing/spent": "CasingPistol",
    "obj/item/kirbyplants": "PottedPlant0",
    "obj/item/reagent_containers/hypospray/medipen/survival": "EmergencyMedipen",
    "obj/item/trash": "FoodPacketChipsTrash",
    "obj/structure/extinguisher_cabinet": "ExtinguisherCabinet",
    "obj/structure/tank_dispenser/oxygen": "ClosetO2Filled",
    "obj/structure/flora/coconuts": "FoodCoconut",
    "obj/structure/lattice/catwalk": "Catwalk",
    "obj/structure/fence/door": "FenceMetalGate",
    "obj/structure/fence": "FenceMetalStraight",
    "obj/structure/disposaloutlet": "DisposalUnit",
    "obj/structure/mystery_box/guns": "BitrunWeaponSupplyBox",
    "obj/structure/frame/computer": "ComputerFrame",
    "obj/structure/frame/machine": "MachineFrame",
    "obj/structure/bonfire": "Bonfire",
    "obj/structure/spacevine": "Kudzu",
    "obj/structure/spider/stickyweb": "SpiderWeb",
    "obj/structure/holosign/barrier": "InflatableWall",
    "obj/structure/fluff/paper/stack": "PaperBin",
    "obj/structure/table_frame": "TableFrame",
    "obj/structure/closet/syndicate": "LockerSyndicate",
    "obj/structure/closet/secure_closet/personal/cabinet": "LockerSteel",
    "obj/structure/closet/secure_closet/personal": "LockerSteel",
    "obj/structure/sign/departments/cargo": "SignCargo",
    "obj/structure/sign/directions/command": "SignDirectionCommand",
    "obj/structure/sign/warning/secure_area": "SignSecureArea",
    "obj/machinery/firealarm": "FireAlarm",
    "obj/machinery/conveyor": "ConveyorBelt",
    "obj/machinery/status_display": "StatusDisplay",
    "obj/machinery/power/shuttle_engine/propulsion": "Thruster",
    "obj/machinery/power/shuttle_engine/heater": "Thruster",
    "obj/machinery/porta_turret/syndicate": "WeaponTurretSyndicate",
    "obj/machinery/recharger": "WeaponCapacitorRecharger",
    "obj/machinery/recharge_station": "BorgCharger",
    "obj/machinery/hydroponics": "hydroponicsTray",
    "obj/effect/mine/explosive": "BitrunMilsimMine",
    "obj/effect/spawner/structure/flipped_table": "Table",
    "obj/effect/spawner/random/trash": "FoodPacketChipsTrash",
}

# These DM markers resolve to real structures. Select the source object before importing its art.
SPAWN_RESULTS = {
    "obj/effect/spawner/structure/window/reinforced/tinted": ("/obj/structure/window/reinforced/tinted/fulltile", "TintedWindow"),
    "obj/effect/spawner/structure/window/reinforced/shuttle": ("/obj/structure/window/reinforced/shuttle", "ShuttleWindow"),
    "obj/effect/spawner/structure/window/reinforced/plasma/plastitanium": ("/obj/structure/window/reinforced/plasma/plastitanium", "PlastitaniumWindow"),
    "obj/effect/spawner/structure/window/reinforced/plasma": ("/obj/structure/window/reinforced/plasma/fulltile", "ReinforcedPlasmaWindow"),
    "obj/effect/spawner/structure/window/reinforced": ("/obj/structure/window/reinforced/fulltile", "ReinforcedWindow"),
    "obj/effect/spawner/structure/window/ice": ("/obj/structure/window/reinforced/fulltile/ice", "ReinforcedWindow"),
    "obj/effect/spawner/structure/window/survival_pod": ("/obj/structure/window/reinforced/shuttle/survival_pod", "ShuttleWindow"),
    "obj/effect/spawner/structure/window": ("/obj/structure/window/fulltile", "Window"),
    "obj/effect/spawner/random/vending/colavend": ("/obj/machinery/vending/cola", "VendingMachineCola"),
}

MOBS = {
    "mob/living/basic/alien/queen": "NFMobXenoQueen",
    "mob/living/basic/alien/drone": "NFMobXenoDrone",
    "mob/living/basic/alien": "NFMobXeno",
    "mob/living/basic/bear": "NFMobBearSpace",
    "mob/living/basic/bot/cleanbot": "MobCleanBot",
    "mob/living/basic/butterfly": "MobButterfly",
    "mob/living/basic/chicken": "MobChicken",
    "mob/living/basic/clown": "MobClownSpider",
    "mob/living/basic/crab": "MobCrab",
    "mob/living/basic/fake_crewman": "BitrunRitualCrew",
    "mob/living/basic/heretic_summon": "MobArgocytePouncerExpeditions",
    "mob/living/basic/hivebot": "MobRogueSiliconDroneLethals",
    "mob/living/basic/mimic": "MobMimic",
    "mob/living/basic/parrot": "MobParrot",
    "mob/living/basic/pet/cat/space": "MobCatSpace",
    "mob/living/basic/pet/cat": "MobCat",
    "mob/living/basic/pet/dog/corgi": "MobCorgi",
    "mob/living/basic/pet/fox": "MobFox",
    "mob/living/basic/pet/gondola": "BitrunGondola",
    "mob/living/basic/trooper/pirate/melee": "MobSyndicateFootsoldier",
    "mob/living/basic/trooper/pirate/ranged": "MobSyndicateFootsoldierPilot",
    "mob/living/basic/trooper/syndicate/ranged": "MobSyndicateFootsoldierPilot",
    "mob/living/basic/trooper/syndicate": "MobSyndicateFootsoldier",
    "mob/living/basic/zombie": "MobHorrorExpeditions",
    "mob/living/simple_animal/hostile/megafauna/blood_drunk_miner": "BitrunMobBloodDrunkMiner",
    "mob/living/simple_animal/hostile/megafauna/bubblegum": "BitrunMobBubblegum",
    "mob/living/simple_animal/hostile/megafauna/colossus": "BitrunMobColossus",
    "mob/living/simple_animal/hostile/megafauna/dragon": "BitrunMobAshDrake",
    "mob/living/simple_animal/hostile/megafauna/hierophant": "BitrunMobHierophant",
    "mob/living/simple_animal/hostile/megafauna/wendigo": "BitrunMobWendigo",
    "obj/effect/mob_spawn/corpse/human/cyber_police": "MobRandomSecurityCorpse",
    "obj/effect/mob_spawn/corpse/human/doctor": "MobRandomMedicCorpse",
    "obj/effect/mob_spawn/corpse/human/scientist": "MobRandomScienceCorpse",
    "obj/effect/mob_spawn/corpse/human/miner": "MobRandomCargoCorpse",
    "obj/effect/mob_spawn/corpse/human/cargo_tech": "MobRandomCargoCorpse",
    "obj/effect/mob_spawn/corpse/human": "SalvageHumanCorpse",
    "obj/effect/mob_spawn/ghost_role/human/ancient_milsim": "BitrunGhostSoldier",
    "obj/effect/mob_spawn/ghost_role/human/virtual_domain/beach": "BitrunGhostSkeleton",
    "obj/effect/mob_spawn/ghost_role/human/virtual_domain/islander": "BitrunGhostIslander",
    "obj/effect/mob_spawn/ghost_role/human/virtual_domain/pirate": "BitrunGhostPirate",
    "obj/effect/mob_spawn/ghost_role/human/virtual_domain/syndie": "BitrunGhostSoldier",
}

MODULAR_MOBS = {
    "mob/living/basic/alien/queen": "NFMobXenoQueen",
    "mob/living/basic/alien/drone": "NFMobXenoDrone",
    "mob/living/basic/alien": "NFMobXeno",
    "mob/living/basic/bear": "NFMobBearSpace",
    "mob/living/basic/bot/cleanbot": "MobCleanBot",
    "mob/living/basic/butterfly": "MobButterfly",
    "mob/living/basic/chicken": "MobChicken",
    "mob/living/basic/clown": "MobClownSpider",
    "mob/living/basic/crab": "MobCrab",
    "mob/living/basic/fake_crewman": "BitrunRitualCrew",
    "mob/living/basic/heretic_summon": "MobArgocytePouncerExpeditions",
    "mob/living/basic/hivebot": "MobRogueSiliconDroneLethals",
    "mob/living/basic/mimic": "MobMimic",
    "mob/living/basic/parrot": "MobParrot",
    "mob/living/basic/pet/cat/space": "MobCatSpace",
    "mob/living/basic/pet/cat": "MobCat",
    "mob/living/basic/pet/dog/corgi": "MobCorgi",
    "mob/living/basic/pet/fox": "MobFox",
    "mob/living/basic/pet/gondola": "BitrunGondola",
    "mob/living/basic/trooper/pirate/melee": "MobSyndicateFootsoldier",
    "mob/living/basic/trooper/pirate/ranged": "MobSyndicateFootsoldierPilot",
    "mob/living/basic/trooper/syndicate/ranged": "MobSyndicateFootsoldierPilot",
    "mob/living/basic/trooper/syndicate": "MobSyndicateFootsoldier",
    "mob/living/basic/zombie": "MobHorrorExpeditions",
    "mob/living/simple_animal/hostile/megafauna/blood_drunk_miner": "BitrunMobBloodDrunkMiner",
    "mob/living/simple_animal/hostile/megafauna/bubblegum": "BitrunMobBubblegum",
    "mob/living/simple_animal/hostile/megafauna/colossus": "BitrunMobColossus",
    "mob/living/simple_animal/hostile/megafauna/dragon": "BitrunMobAshDrake",
    "mob/living/simple_animal/hostile/megafauna/hierophant": "BitrunMobHierophant",
    "mob/living/simple_animal/hostile/megafauna/wendigo": "BitrunMobWendigo",
    "obj/effect/mob_spawn/corpse/human/cyber_police": "MobRandomSecurityCorpse",
    "obj/effect/mob_spawn/corpse/human/doctor": "MobRandomMedicCorpse",
    "obj/effect/mob_spawn/corpse/human/scientist": "MobRandomScienceCorpse",
    "obj/effect/mob_spawn/corpse/human/miner": "MobRandomCargoCorpse",
    "obj/effect/mob_spawn/corpse/human/cargo_tech": "MobRandomCargoCorpse",
    "obj/effect/mob_spawn/corpse/human": "SalvageHumanCorpse",
    "obj/effect/mob_spawn/ghost_role/human/ancient_milsim": "BitrunGhostSoldier",
    "obj/effect/mob_spawn/ghost_role/human/virtual_domain/beach": "BitrunGhostSkeleton",
    "obj/effect/mob_spawn/ghost_role/human/virtual_domain/islander": "BitrunGhostIslander",
    "obj/effect/mob_spawn/ghost_role/human/virtual_domain/pirate": "BitrunGhostPirate",
    "obj/effect/mob_spawn/ghost_role/human/virtual_domain/syndie": "BitrunGhostSoldier",
}


MARKERS = {"BitrunAvatarSpawn", "BitrunCacheSpawn", "BitrunCacheGoal", "BitrunExit", "BitrunScenarioMarker"}
DECALS = {
    "obj/structure/flora/bush/ferny": "Busha1",
    "obj/structure/flora/bush": "Bushb1",
    "obj/structure/flora/grass": "Grassa1",
    "obj/effect/turf_decal/weather/dirt": "Dirt",
    "obj/effect/turf_decal/weather/snow": "DirtLight",
    "obj/effect/turf_decal/weather/sand": "DirtLight",
    "obj/effect/turf_decal/loading_area": "DirtLight",
    "obj/effect/turf_decal/delivery": "DirtLight",
    "obj/effect/turf_decal/plaque": "DirtLight",
    "obj/effect/turf_decal/bot": "DirtLight",
    "obj/effect/turf_decal/sand": "DirtLight",
    "obj/effect/turf_decal/stripes": "DirtLight",
    "obj/effect/decal/cleanable/blood": "splatter",
    "obj/effect/turf_decal/tile": "DirtLight",
    "obj/effect/turf_decal/trimline": "DirtLight",
    "obj/effect/turf_decal/siding": "DirtLight",
    "obj/effect/decal/cleanable/dirt": "Dirt",
}



def match_path(table, path):
    """Use the closest specified ancestor in the source type tree."""
    while path:
        if path in table:
            return table[path]
        path = path.rpartition("/")[0]
    return None


def direction_for(atom):
    direction = as_int(atom.fields.get("dir"))
    if not direction:
        direction = {"north": 1, "south": 2, "east": 4, "west": 8}.get(atom.path.rsplit("/", 1)[-1], 2)
    return {**DIRS, 5: "135", 6: "45", 9: "-135", 10: "-45"}.get(direction, "0")


def atom_entity(atom, proto, pos, name=""):
    rotation = direction_for(atom)
    if proto == "DisposalBend":
        direction = as_int(atom.fields.get("dir"))
        rotation = {5: "180", 6: "90", 9: "-90", 10: "0"}[direction]
    color = as_string(atom.fields.get("color"))
    offset = None
    if "pixel_x" in atom.fields or "pixel_y" in atom.fields:
        offset = (as_int(atom.fields.get("pixel_x")) / 32, as_int(atom.fields.get("pixel_y")) / 32)
    return Entity(proto, (pos[0] + 0.5, pos[1] + 0.5), rotation,
                  name or as_string(atom.fields.get("name")), as_string(atom.fields.get("desc")),
                  sprite_offset=offset,
                  sprite_color=color if re.fullmatch(r"#[\da-fA-F]{6}(?:[\da-fA-F]{2})?", color) else "")


def imported_placement(atom, entity, art):
    source = art.source_fields(atom.path)
    components = art.placed_components(entity.proto)
    sprite = components.get("Sprite", {})
    if atom.path.startswith("/obj/machinery/light") and not atom.path.startswith("/obj/machinery/light/floor") and "PoweredLight" in components:
        # Native fixtures and the light source face north; DM light fixtures face their wall.
        entity = replace(entity, rotation=str((int(direction_for(atom)) + 180) % 360))
    has_offset = any(axis in atom.fields or axis in source for axis in ("pixel_x", "pixel_y"))
    x, y = (as_int(atom.fields.get(axis, source.get(axis))) / 32 for axis in ("pixel_x", "pixel_y"))
    if atom.path.startswith("/obj/structure/sign/") and (has_offset or atom.path.startswith("/obj/structure/sign/poster/")):
        # WhiteMoon mounts the sign on the tile reached by its authored offset.
        dx, dy = (round(value) if abs(value) > 0.5 else 0 for value in (x, y))
        px, py = entity.position
        x, y = x - dx, y - dy
        entity = replace(entity, position=(px + dx, py + dy), rotation="0")
    if not sprite:
        return entity
    if not sprite.get("noRot", False) and not sprite.get("snapCardinals", False):
        # BYOND pixel offsets are in grid axes. Robust stores a sprite's offset in its local axes.
        angle = math.radians(int(entity.rotation))
        x, y = x * math.cos(angle) + y * math.sin(angle), -x * math.sin(angle) + y * math.cos(angle)
    fields = {}
    if has_offset:
        fields["offset"] = float_pos((round(x, 6) or 0, round(y, 6) or 0))
    if entity.sprite_color:
        fields["color"] = entity.sprite_color
    proto = art.placement_variant(atom, entity.proto, fields)
    return replace(entity, proto=proto, sprite_offset=None, sprite_color="")


def normalize_id(value):
    return re.sub("[^a-z0-9]", "", value.casefold())


def lookup_equivalent(path, proto_by_name):
    """Use exact type suffixes for straightforward item aliases."""
    family = path.split("/")
    if family[0] != "obj" or len(family) < 3:
        return ""
    domain = family[1]
    prefixes = {
        "item": ("", "Tool", "Food", "Drink", "Weapon", "Clothing", "Material"),
        "machinery": ("", "Computer", "Machine", "VendingMachine"),
        "structure": ("", "Structure"),
    }.get(domain, ())
    for depth in range(len(family) - 2, 0, -1):
        name = normalize_id("".join(family[-depth:]))
        if len(name) < 3:
            continue
        for prefix in prefixes:
            proto = proto_by_name.get(prefix.casefold() + name)
            if proto:
                return proto
    return ""


def corpse_entity(kind, target, pos):
    entity = atom_entity(kind, target, pos)
    return replace(entity, description=entity.description or "A body left behind in the virtual domain.")


def stairs_decal(atom, atom_path):
    if "fake_stairs" in atom_path:
        family = "StairsWood"
    else:
        family = {"left": "StairsL", "medium": "StairsM", "right": "StairsR"}.get(atom_path.rsplit("/", 1)[-1], "Stairs")
    direction = {"0": "S", "90": "E", "180": "N", "-90": "W"}.get(direction_for(atom), "S")
    return family + direction


def convert(cells, key, is_domain, known, art):
    tiles = {}
    entities = []
    decals = []
    associations = defaultdict(set)
    counts = Counter()
    room_anchors = []
    closed_turfs = set()
    goals = set()
    exits = set()
    spawns = set()
    caches = set()
    tile_floor_candidates = set()
    crate_candidates = set()
    shoreline = set()
    water = set()
    rods = set()
    proto_by_name = {normalize_id(p): p for p in sorted(known["entity"])}
    imported_objects = known["entity"]

    def marker(proto, pos, name=""):
        entities.append(Entity(proto, (pos[0] + 0.5, pos[1] + 0.5), name=name))

    def appearance(atom, entity):
        proto = art.entity(atom, entity.proto, entity.position)
        return imported_placement(atom, replace(entity, proto=proto), art)

    for pos, atoms in sorted(cells.items(), key=lambda item: (item[0][1], item[0][0])):
        tile = "Space"
        at_space = False
        floor_changed = any(atom.path.startswith(("/obj/effect/mapping_helpers/broken_floor",
                                                  "/obj/effect/mapping_helpers/burnt_floor")) for atom in atoms)
        for atom in atoms:
            if not atom.path.startswith("/turf/"):
                continue
            path = atom.path.removeprefix("/turf/")
            target = match_path(TURFS, path)
            if target is None:
                raise ValueError(f"{key}: no tile mapping for {atom.path}")
            tile, overlay = target
            catwalk = key == "blood_drunk_miner" and pos in {(17, 27), (17, 28)} and overlay == "FloorLavaEntity"
            if catwalk:
                tile, overlay = "FloorTileCatwalkHorizontal", ""
            associations[atom.path].add(tile)
            if overlay:
                entities.append(appearance(atom, atom_entity(atom, overlay, pos)))
                associations[atom.path].add(overlay)
            if tile != "Space" and not overlay and not catwalk and not floor_changed:
                tile = art.tile(atom, tile)
            if path.startswith("closed/") or overlay in {"FloorSnowChasm", "FloorChasmEntity", "FloorLavaEntity", "BitrunPlasmaLake"}:
                closed_turfs.add(pos)
            if path.startswith(("open/water", "open/floor/holofloor/beach/water")):
                water.add(pos)
            if path.startswith("open/misc/beach/"):
                shoreline.add(pos)
            if path.startswith("open/floor/iron/stairs"):
                if tile == target[0]:
                    decals.append((stairs_decal(atom, path), pos))
            break
        if tile == "Space":
            if any(atom.path == "/obj/structure/lattice" for atom in atoms):
                tile = "Lattice"
            else:
                at_space = True
        if tile != "Space":
            tiles[pos] = tile
            if (animation := art.animations.get(tile)) and not (
                art.tiles[tile][4] == "bitrunning" and any(
                    atom.path == "/obj/effect/landmark/bitrunning/cache_goal_turf" for atom in atoms)
            ):
                proto, direction = animation
                entities.append(atom_entity(Atom("/obj", {"dir": str(direction)}), proto, pos))
            if pos not in closed_turfs:
                tile_floor_candidates.add(pos)
        for atom in atoms:
            path = atom.path.lstrip("/")
            counts[atom.path] += 1
            if path.startswith(("turf/", "area/")):
                continue
            if path == "obj/modular_map_connector":
                room_anchors.append(pos)
                continue
            if path.startswith("obj/modular_map_root"):
                continue
            if path == "obj/structure/lattice":
                associations[atom.path].add("Lattice tile where exposed")
                continue
            if path.startswith("obj/effect/landmark/bitrunning/"):
                name = path.rsplit("/", 1)[-1]
                if name in {"hololadder_spawn", "permanent_exit"}:
                    exits.add(pos)
                    if name == "hololadder_spawn":
                        spawns.add(pos)
                elif name == "cache_goal_turf":
                    goals.add(pos)
                elif name == "cache_spawn":
                    caches.add(pos)
                else:
                    scenario_proto = "BitrunRitualAltar" if key == "heretic_hunt" and name == "loot_signal" else "BitrunScenarioMarker"
                    marker(scenario_proto, pos, name)
                    associations[atom.path].add(f"{scenario_proto} ({name})")
                    if name == "loot_signal":
                        caches.add(pos)
                    if name == "mob_segment" and key in MODULAR_MOBS:
                        mob_pool = MODULAR_MOBS[key]
                        # Each room uses a stable distribution of its scenario's enemy types.
                        proto = mob_pool[(pos[0] + 3 * pos[1]) % len(mob_pool)]
                        entities.append(atom_entity(atom, proto, pos))
                        associations[atom.path].add(proto)
                continue
            if path.startswith("obj/structure/closet/crate/secure/bitrunning/encrypted"):
                caches.add(pos)
                associations[atom.path].add("BitrunCacheSpawn")
                continue
            if path.startswith("obj/item/storage/lockbox/bitrunning/encrypted"):
                marker("BitrunScenarioMarker", pos, "curiosity_spawn")
                continue
            if path.startswith("obj/structure/closet/crate"):
                crate_candidates.add(pos)
            if path.startswith("mob/living/simple_animal/hostile/megafauna"):
                caches.add(pos)
                marker("BitrunScenarioMarker", pos, "boss")
            if path.startswith("obj/effect/mob_spawn/ghost_role/"):
                marker("BitrunScenarioMarker", pos, "ghost_role")
                if key == "island_brawl" and path.endswith("/islander"):
                    spawns.add(pos)
            if path.startswith("obj/item/fishing_rod"):
                rods.add(pos)
            if path.startswith("obj/structure/fake_stairs"):
                decals.append((art.decal(atom, stairs_decal(atom, path)), pos))
                associations[atom.path].add("stairs decal")
                continue
            if path.startswith(("obj/effect/mapping_helpers/broken_floor",
                                "obj/effect/mapping_helpers/burnt_floor")) and not at_space:
                tiles[pos] = art.damaged_tile(atom, "PlatingDamaged")
                associations[atom.path].add("PlatingDamaged")
                continue

            decal = match_path(DECALS, path)
            if decal and not at_space:
                decals.append((art.decal(atom, decal), pos))
                associations[atom.path].add(f"decal {decal}")
                continue
            if result := match_path(SPAWN_RESULTS, path):
                source_type, proto = result
                result_atom = Atom(source_type, {field: atom.fields[field] for field in ("dir",) if field in atom.fields})
                entities.append(appearance(result_atom, atom_entity(result_atom, proto, pos)))
                associations[atom.path].add(proto)
                if path.startswith("obj/effect/spawner/structure/window") and not any(
                    item.path.startswith("/obj/structure/grille") for item in atoms
                ):
                    grille = Atom("/obj/structure/grille", {})
                    entities.append(appearance(grille, atom_entity(grille, "Grille", pos)))
                    associations[atom.path].add("Grille")
                continue
            proto = match_path(MOBS, path) or match_path(OBJECTS, path)
            if proto is None or proto and proto not in imported_objects:
                proto = lookup_equivalent(path, proto_by_name) or proto
            if proto == "DisposalPipe" and path == "obj/structure/disposalpipe/segment" and as_int(atom.fields.get("dir")) in {5, 6, 9, 10}:
                proto = "DisposalBend"
            if proto and proto in imported_objects:
                entity = (corpse_entity if path.startswith("obj/effect/mob_spawn/corpse/") else atom_entity)(atom, proto, pos)
                if proto == "BitrunStageButton":
                    entity = replace(entity, name="ancient_stage")
                if entity.proto in known["noRotation"]:
                    entity = replace(entity, rotation="0")
                entities.append(appearance(atom, entity))
                associations[atom.path].add(proto)
            elif proto:
                associations[atom.path].add(f"unmapped entity ({proto} unavailable)")
            elif not at_space and (decoration := art.decorative(atom, pos)):
                entities.append(imported_placement(atom, atom_entity(atom, decoration, pos), art))
                associations[atom.path].add(decoration)

    if key == "breeze_bay":
        fishing_spots = [(x, y) for x, y in shoreline if any((x+dx, y+dy) in water for dx, dy in [(1,0), (-1,0), (0,1), (0,-1)])]
        if not fishing_spots or not rods:
            raise ValueError("Breeze Bay requires accessible shoreline and fishing rods")
        spot = min(fishing_spots, key=lambda pos: (min(abs(pos[0]-r[0]) + abs(pos[1]-r[1]) for r in rods), pos))
        marker("BitrunFishingSpot", spot, "fishing")

    if is_domain:
        if not exits:
            exits.update(room_anchors)
        if not spawns:
            spawns.update(exits)
        if not spawns:
            raise ValueError(f"{key}: no avatar spawn found in domain or embedded safehouse")
        if not goals:
            goals.update(spawns)
        if key in {"syndicate_assault", "vaporwave"}:
            for x, y in sorted(spawns):
                entities.extend(Entity(proto, (x + 0.5, y + 0.5)) for proto in (
                    "ClothingOuterHardsuitBasic", "ClothingHeadHelmetHardsuitBasic",
                    "JetpackBlueFilled", "ClothingMaskGas", "EmergencyOxygenTankFilled",
                ))
        if key == "xeno_nest":
            entities.extend(Entity("WeaponProtoKineticAccelerator", (x + 0.5, y + 0.5)) for x, y in sorted(spawns))
        if key == "clown_planet":
            # The WhiteMoon peel can teleport through the boundary between the staging room and the arena.
            endpoints = ((13, 10), (13, 15))
            if not all(pos in tile_floor_candidates for pos in endpoints):
                raise ValueError("Clown Planet portal endpoints must be on traversable tiles")
            entities.extend(Entity("BitrunClownPortal", (x + 0.5, y + 0.5), link="peel") for x, y in endpoints)
        if key == "psyker_shuffle":
            caches.update(crate_candidates)
        if not caches:
            if not tile_floor_candidates:
                raise ValueError(f"{key}: no traversable cells for fallback cache")
            caches.add(max(tile_floor_candidates, key=lambda pos: (min(abs(pos[0]-s[0]) + abs(pos[1]-s[1]) for s in spawns), pos)))
        for marker_proto, positions in (
            ("BitrunAvatarSpawn", spawns), ("BitrunCacheSpawn", caches), ("BitrunCacheGoal", goals), ("BitrunExit", exits),
        ):
            for position in sorted(positions):
                marker(marker_proto, position)
    else:
        for positions, proto in ((spawns, "BitrunAvatarSpawn"), (goals, "BitrunCacheGoal"), (exits, "BitrunExit"), (caches, "BitrunCacheSpawn")):
            for position in sorted(positions):
                marker(proto, position)

    for path, proto in (
        ("/obj/effect/landmark/bitrunning/hololadder_spawn", "BitrunAvatarSpawn / BitrunExit"),
        ("/obj/effect/landmark/bitrunning/permanent_exit", "BitrunExit"),
        ("/obj/effect/landmark/bitrunning/cache_goal_turf", "BitrunCacheGoal"),
        ("/obj/effect/landmark/bitrunning/cache_spawn", "BitrunCacheSpawn"),
    ):
        if path in counts:
            associations[path].add(proto)
    # Native noRot transforms reject authored angles even when the source counterpart rotates.
    entities = [replace(entity, rotation="0") if entity.proto in known["noRotation"] else entity
                for entity in entities]
    return tiles, entities, decals, associations, counts


def float_pos(pos):
    return ",".join(f"{value:g}" for value in pos)


def map_yaml(name, tiles, entities, decals):
    tile_names = ["Space"] + sorted(set(tiles.values()) - {"Space"})
    ids = {name: index for index, name in enumerate(tile_names)}
    chunks = defaultdict(lambda: bytearray(TILE_BYTES.size * CHUNK_SIZE * CHUNK_SIZE))
    for (x, y), tile_name in tiles.items():
        if tile_name == "Space":
            continue
        cx, rx = divmod(x, CHUNK_SIZE)
        cy, ry = divmod(y, CHUNK_SIZE)
        index = (ry * CHUNK_SIZE + rx) * TILE_BYTES.size
        TILE_BYTES.pack_into(chunks[cx, cy], index, ids[tile_name], 0, 0, 0)
    rows = [
        "meta:", "  format: 7", "  category: Grid", f"  entityCount: {len(entities) + 1}",
        "maps: []", "grids: [1]", "orphans: [1]", "nullspace: []", "tilemap:",
    ]
    rows.extend(f'  "{i}": {tile}' for i, tile in enumerate(tile_names))
    rows.extend([
        "entities:", '  - proto: ""', "    entities:", "      - uid: 1", "        components:",
        "          - type: MetaData", f"            name: {json.dumps(name)}",
        "          - type: Transform", "            parent: invalid", "            pos: 0,0",
        "          - type: MapGrid", "            canSplit: false",
        "            chunks:" if chunks else "            chunks: {}",
    ])
    for (cx, cy), chunk in sorted(chunks.items()):
        rows.extend([f"              {cx},{cy}:", f"                ind: {cx},{cy}",
                     f"                tiles: {base64.b64encode(chunk).decode()}", "                version: 7"])
    rows.extend([
        "          - type: Broadphase", "          - type: Physics", "            bodyType: Static",
        "          - type: Fixtures", "            fixtures: {}",
        "          - type: ImplicitRoof", "            color: '#FFFFFFFF'",
        "          - type: GridEnvironment", "            forceGravity: true", "            alwaysPowered: true",
        "            safeAtmosphere: true",
    ])
    if decals:
        rows.extend(["          - type: DecalGrid", "            chunkCollection:", "              version: 2",
                     "              nodes:"])
        decal_groups = defaultdict(list)
        for decal_id, (decal, position) in enumerate(decals, start=1):
            decal_groups[decal].append((decal_id, position))
        for decal, positions in sorted(decal_groups.items()):
            rows.extend(["              - node:", f"                  id: {decal}", "                decals:"])
            rows.extend(f'                  "{uid}": {float_pos(position)}' for uid, position in positions)

    by_prototype = defaultdict(list)
    links = defaultdict(list)
    for uid, entity in enumerate(entities, start=2):
        by_prototype[entity.proto].append((uid, entity))
        if entity.link:
            links[entity.link].append(uid)
    if any(len(pair) != 2 for pair in links.values()):
        raise ValueError(f"{name}: each local portal must have exactly one counterpart")
    for proto, group in sorted(by_prototype.items()):
        rows.extend([f"  - proto: {proto}", "    entities:"])
        for uid, entity in group:
            rows.extend([f"      - uid: {uid}", "        components:", "          - type: Transform",
                         "            parent: 1", f"            pos: {float_pos(entity.position)}"])
            if entity.rotation != "0":
                rows.append(f"            rot: {entity.rotation}")
            if entity.sprite_offset is not None or entity.sprite_color:
                rows.append("          - type: Sprite")
                if entity.sprite_offset is not None:
                    rows.append(f"            offset: {float_pos(entity.sprite_offset)}")
                if entity.sprite_color:
                    rows.append(f"            color: {json.dumps(entity.sprite_color)}")
            if entity.link:
                linked = next(other for other in links[entity.link] if other != uid)
                rows.extend(["          - type: LinkedEntity", f"            linkedEntities: [{linked}]"])
            if entity.name or entity.description:
                rows.append("          - type: MetaData")
                if entity.name:
                    rows.append(f"            name: {json.dumps(entity.name, ensure_ascii=False)}")
                if entity.description:
                    rows.append(f"            desc: {json.dumps(entity.description, ensure_ascii=False)}")
    return "\n".join(rows) + "\n"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("whitemoon", type=Path, help="WhiteMoon Git repository containing the source commit")
    parser.add_argument("--station", type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument("--verify", action="store_true", help="check existing generated output without writing files")
    args = parser.parse_args()
    white_moon = args.whitemoon.resolve()
    station = args.station.resolve()
    paths = git(white_moon, "ls-tree", "-r", "--name-only", SOURCE_COMMIT, DOMAIN_ROOT, SAFEHOUSE_ROOT).decode().splitlines()
    map_paths = sorted(path for path in paths if path.endswith(".dmm"))
    domains = [p for p in map_paths if p.startswith(DOMAIN_ROOT)]
    rooms = [p for p in map_paths if p.startswith(SAFEHOUSE_ROOT)]
    if len(domains) != 26 or len(rooms) != 14:
        raise ValueError(f"expected 26 domain and 14 safehouse sources, got {len(domains)} and {len(rooms)}")
    blobs = read_git_blobs(white_moon, [*map_paths, SAFEHOUSE_CONFIG])
    catalog = tomllib.loads(blobs[SAFEHOUSE_CONFIG].decode())["rooms"]
    parsed = {path: parse_dmm(blobs[path]) for path in map_paths}
    safehouses = {key: parsed[SAFEHOUSE_ROOT + conf["modules"][0]] for key, conf in catalog.items()}
    known = target_prototypes(station)
    art = MapArt(white_moon, station, SOURCE_COMMIT, git, read_git_blobs, args.verify)
    art.prepare(parsed, ["/obj/structure/grille", *(source for source, _ in SPAWN_RESULTS.values())])
    manifest = {"sourceCommit": SOURCE_COMMIT, "maps": {}, "sourceAtoms": {}}
    source_counts = Counter(atom.path for cells in parsed.values() for atoms in cells.values() for atom in atoms)
    source_associations = defaultdict(set)
    produced = {}
    for path in [*domains, *rooms]:
        is_domain = path.startswith(DOMAIN_ROOT)
        relative = path.removeprefix(DOMAIN_ROOT if is_domain else SAFEHOUSE_ROOT)
        output = (MAP_ROOT / ("Domains" if is_domain else "Safehouses") / relative).with_suffix(".yml")
        name = Path(relative).stem
        original = parsed[path]
        cells = merge_safehouses(original, safehouses) if is_domain else original
        tiles, entities, decals, associations, _ = convert(cells, name, is_domain, known, art)
        unknown_tiles = set(tiles.values()) - known["tile"] - art.tiles.keys()
        unknown_entities = {e.proto for e in entities} - known["entity"] - art.entities.keys()
        if unknown_tiles or unknown_entities:
            raise ValueError(f"{name}: unknown tile {sorted(unknown_tiles)} or entity prototypes {sorted(unknown_entities)}")
        for atom_path, values in associations.items():
            source_associations[atom_path].update(values)
        manifest["maps"][path] = {
            "target": output.as_posix(),
            "sha256": hashlib.sha256(blobs[path]).hexdigest(),
            "dimensions": [max(x for x, y in original) + 1, max(y for x, y in original) + 1],
            "entities": len(entities),
            "decals": len(decals),
            "markers": dict(sorted(Counter(e.proto for e in entities if e.proto in MARKERS or e.proto in {"BitrunStageButton", "BitrunFishingSpot", "BitrunRitualAltar"}).items())),
        }
        produced[output] = map_yaml(name, tiles, entities, decals)
    for path, count in sorted(source_counts.items()):
        targets = sorted(source_associations.get(path, ()))
        if not targets and path.startswith(("/area", "/obj/effect/baseturf_helper", "/obj/modular_map_")):
            targets.append("area and map topology in target grid")
        manifest["sourceAtoms"][path] = {"occurrences": count, "targets": targets}
    produced[Path("Tools/bitrunning/map_manifest.json")] = json.dumps(manifest, indent=2, ensure_ascii=False) + "\n"
    art.finish()
    for relative, content in produced.items():
        target = station / relative
        if args.verify:
            if not target.exists() or target.read_text(encoding="utf-8") != content:
                raise SystemExit(f"out of date: {relative}")
        else:
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_text(content, encoding="utf-8", newline="\n")
    print(f"{'Verified' if args.verify else 'Generated'} {len(domains)} domains and {len(rooms)} safehouses.")


if __name__ == "__main__":
    main()
