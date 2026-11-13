"""Resolve WhiteMoon's authored map art and repack it as DeepLagoon resources."""

from collections import Counter, defaultdict
from functools import cache
from hashlib import sha256
from io import BytesIO
import json
from math import ceil, sqrt
from pathlib import Path
import re
import shutil

from PIL import Image
import yaml

from import_sprites import (
    URL, dimensions, dmi_states, export_state, property_values, write_frame, write_json,
)


PROTOTYPES = Path("Resources/Prototypes/_DeepLagoon/Bitrunning/WhiteMoonArt.yml")
TEXTURES = Path("Resources/Textures/_DeepLagoon/Bitrunning/WhiteMoon")
FIELDS = (
    "base_icon_state", "icon", "icon_state", "parent_type", "name", "color", "alpha",
    "dir", "pixel_x", "pixel_y", "invisibility", "smoothing_flags", "icon_living",
    "icon_dead", "icon_resting", "icon_door", "icon_base", "density", "desc",
    "icon_keyboard", "icon_screen", "keyboard_change_icon", "icon_deny", "icon_vend", "light_mask", "panel_type",
)
ART_FIELD = re.compile(r"^\t(?:var/(?:static/)?)?(" + "|".join(FIELDS) + r")\s*=\s*(.*?)\s*$")
TYPE = re.compile(r"^(/[A-Za-z_][\w]*(?:/[\w]+)*)\s*\{?\s*$")
LITERAL = re.compile(r"""^("(?:[^"\\]|\\.)*"|'[^']*'|/\w[\w/]*|[-+]?\d+|[\w]+)""")
PROTOTYPE_ENTRY = re.compile(r"(?m)^- type: (\w+)[^\n]*\n")
PROTOTYPE_ID = re.compile(r"(?m)^  id: ([\w.]+)\s*(?:#.*)?$")
DIRECTION = {"NORTH": 1, "SOUTH": 2, "EAST": 4, "WEST": 8,
             "NORTHEAST": 5, "SOUTHEAST": 6, "NORTHWEST": 9, "SOUTHWEST": 10}
FRAME_DIRECTION = (2, 1, 4, 8, 6, 10, 5, 9)
DAMAGE_ATLAS = "icons/turf/damaged.dmi"
MAP_SWITCH = re.compile(r"^MAP_SWITCH\(\s*(.*?),")
DIRECTIONAL_HELPER = re.compile(r"^MAPPING_DIRECTIONAL_HELPERS\((/[^,]+),\s*(\d+)\)")
BASE_PIXEL = re.compile(r"^\tSET_BASE_PIXEL(?:_NOMAP)?\(\s*([-+]?\d+),\s*([-+]?\d+)\)")
MOVEMENT = "\0movement"
PLANT_ATLAS = "modular_skyrat/modules/aesthetics/plants/plants.dmi"
POSTER_ATLAS = "icons/obj/poster.dmi"
POSTER_FAMILIES = {
    "random_anything": ("official", "contraband"), "random_official": ("official",),
    "random_contraband": ("contraband",), "random_abductor": ("abductor",),
}
MAPPING_STATES = {"random_plant", "random_bookcase", "random_book", "randompile", "lighting_marker", *POSTER_FAMILIES}
RUNTIME_APPEARANCE = {
    "BitrunVirtualWall", "BitrunMilsimFog", "BitrunFishingRod", "BitrunHealthStation",
    "BitrunHuntAnimal", "BitrunGondola", "BitrunRitualCrew", "BitrunMilsimMine",
    "BitrunSeashell", "SalvageHumanCorpse", "DrinkPinaColadaGlass", "BitrunAmbientLight",
}


class PrototypeLoader(yaml.SafeLoader):
    # SS14 uses YAML 1.2. Keep the sprite state names "on" and "off" as strings.
    yaml_implicit_resolvers = {
        initial: [(tag, pattern) for tag, pattern in rules if tag != "tag:yaml.org,2002:bool"]
        for initial, rules in yaml.SafeLoader.yaml_implicit_resolvers.items()
    }


def load_tag(loader, suffix, node):
    if isinstance(node, yaml.MappingNode):
        return loader.construct_mapping(node)
    if isinstance(node, yaml.SequenceNode):
        return loader.construct_sequence(node)
    return loader.construct_scalar(node)


PrototypeLoader.add_multi_constructor("!", load_tag)
PrototypeLoader.add_implicit_resolver(
    "tag:yaml.org,2002:bool", re.compile(r"^(?:true|false)$", re.IGNORECASE), list("tTfF"))


def digest(value):
    return sha256(value.encode()).hexdigest()[:12]


def dm_string(value):
    if value and value.startswith('"'):
        try:
            return json.loads(value)
        except ValueError:
            return value[1:-1]
    return None


def dm_int(value, default=0):
    if value in DIRECTION:
        return DIRECTION[value]
    try:
        return int(value)
    except (ValueError, TypeError):
        return default


def dm_definitions(repo, revision, git, read_blobs):
    """Read definitions in DME include order, including explicit parent_type overrides."""
    dme = read_blobs(repo, ["tgstation.dme"])["tgstation.dme"].decode("utf-8-sig")
    paths = [p.replace("\\", "/") for p in re.findall(r'^#include "([^"]+\.dm)"', dme, re.M)]
    available = set(git(repo, "ls-tree", "-r", "--name-only", revision).decode().splitlines())
    paths = list(dict.fromkeys(path for path in paths if path in available))
    definitions = defaultdict(dict)
    # Avoid holding the entire source tree in memory while unpacking Git batches.
    for start in range(0, len(paths), 600):
        for blob in read_blobs(repo, paths[start:start + 600]).values():
            current = None
            for line in blob.decode("utf-8-sig", errors="replace").splitlines():
                if helper := DIRECTIONAL_HELPER.match(line):
                    path, pixels = helper.groups()
                    for side, x, y in (("north", 0, 1), ("south", 0, -1), ("east", 1, 0), ("west", -1, 0)):
                        definitions[f"{path}/directional/{side}"].update(
                            dir=side.upper(), pixel_x=str(x * int(pixels)), pixel_y=str(y * int(pixels)))
                elif line.startswith("/"):
                    match = TYPE.fullmatch(line.split("//", 1)[0].rstrip())
                    path = match[1] if match else ""
                    current = definitions[path] if path.startswith(("/atom", "/obj", "/mob", "/turf")) else None
                elif current is not None and line.startswith("\t"):
                    if offset := BASE_PIXEL.match(line):
                        current.update(pixel_x=offset[1], pixel_y=offset[2])
                    match = ART_FIELD.match(line)
                    if match:
                        expression = match[2]
                        if switch := MAP_SWITCH.match(expression):
                            expression = switch[1]
                        if literal := LITERAL.match(expression):
                            current[match[1]] = literal[1]
    return definitions


class MapArt:
    def __init__(self, repo, station, revision, git, read_blobs, verify):
        self.repo = repo
        self.station = station
        self.revision = revision
        self.verify = verify
        self.definitions = dm_definitions(repo, revision, git, read_blobs)
        self.native = self.index_native(station)
        self.available = set(git(repo, "ls-tree", "-r", "--name-only", revision).decode().splitlines())
        self.blobs = {}
        self.read_blobs = read_blobs
        self.atlases = {}
        self.exports = defaultdict(set)
        self.reversed_atlases = set()
        self.state_masks = {}
        self.tiles = {}
        self.animations = {}
        self.entities = {}
        self.decals = {}
        self.manifest = defaultdict(Counter)
        self.missing = defaultdict(Counter)
        self.placements = defaultdict(set)
        self.artifacts = set()

    @staticmethod
    def index_native(station):
        indexed = {}
        for path in sorted((station / "Resources/Prototypes").rglob("*.yml")):
            if path == station / PROTOTYPES:
                continue
            source = path.read_text(encoding="utf-8-sig", errors="replace")
            entries = list(PROTOTYPE_ENTRY.finditer(source))
            for start, end in zip(entries, [*entries[1:], None]):
                if start[1] != "entity":
                    continue
                body = source[start.end():end.start() if end else len(source)]
                if proto := PROTOTYPE_ID.search(body):
                    indexed[proto[1]] = (path, "- type: entity\n" + body)
        return indexed

    @cache
    def components(self, proto):
        entry = self.native_entry(proto)
        if entry is None:
            return {}
        components = {}
        parents = entry.get("parent", ())
        if isinstance(parents, str):
            parents = (parents,)
        for parent in parents or ():
            for kind, fields in self.components(parent).items():
                components[kind] = {**fields, **components.get(kind, {})}
        for component in entry.get("components", ()) or ():
            if component and (kind := component.get("type")):
                components[kind] = {**components.get(kind, {}), **component}
        return components

    @cache
    def native_entry(self, proto):
        if proto not in self.native:
            return None
        path, snippet = self.native[proto]
        try:
            return yaml.load(snippet, Loader=PrototypeLoader)[0]
        except yaml.composer.ComposerError:
            return self.native_file(path)[proto]

    def placed_components(self, proto):
        if not (entry := self.entities.get(proto)):
            return self.components(proto)
        parent = entry.get("parent")
        components = dict(self.placed_components(parent)) if parent else {}
        for component in entry["components"]:
            kind = component["type"]
            components[kind] = {**components.get(kind, {}), **component}
        return components

    def placement_variant(self, atom, parent, fields):
        if not fields:
            return parent
        ident = f"{parent}Placement{digest(repr(fields))}"
        if ident not in self.entities:
            entry = self.entities.get(parent) or self.native_entry(parent)
            localization = entry.get("localizationId") or (None if parent in self.entities else f"ent-{parent}")
            self.entities[ident] = {
                "parent": parent,
                **({"localizationId": localization} if localization else {}),
                "components": [{"type": "Sprite", **fields}],
            }
        self.placements[atom.path].add(ident)
        return ident

    @cache
    def native_file(self, path):
        entries = yaml.load(path.read_text(encoding="utf-8-sig"), Loader=PrototypeLoader)
        return {entry["id"]: entry for entry in entries if entry.get("type") == "entity"}

    @cache
    def source_fields(self, path):
        own = self.definitions.get(path, {})
        implicit_parent = {"/turf": "/atom", "/obj": "/atom/movable", "/mob": "/atom/movable"}
        parent = own.get("parent_type") or implicit_parent.get(path, path.rpartition("/")[0])
        fields = self.source_fields(parent) if parent.startswith("/") else {}
        return {**fields, **own}

    def prepare(self, parsed, spawn_results):
        """Batch Git reads for exactly the atlases reachable from the authored maps."""
        source_paths = {path for cells in parsed.values() for atoms in cells.values()
                        for atom in atoms if (path := self.atlas_path(atom))}
        source_paths.update(icon for source_type in spawn_results
                            if (icon := self.source_fields(source_type).get("icon", "")[1:-1]) in self.available)
        self.blobs = self.read_blobs(self.repo, sorted(source_paths | {DAMAGE_ATLAS, PLANT_ATLAS, POSTER_ATLAS}))

    def atlas_path(self, atom):
        icon = atom.fields.get("icon") or self.source_fields(atom.path).get("icon", "")
        if icon.startswith("'") and (path := icon[1:-1]) in self.available:
            return path
        return None

    def atlas(self, path):
        if path not in self.atlases:
            with Image.open(BytesIO(self.blobs[path])) as source:
                description = source.info["Description"]
                image = source.convert("RGBA")
            records = {}
            for name, block, dirs, frames, offset in dmi_states(description):
                if property_values(block, "movement") == "1":
                    name += MOVEMENT
                records[name] = (block, dirs, frames, offset)
            for name in tuple(records):
                if name.endswith(MOVEMENT):
                    records.setdefault(name.removesuffix(MOVEMENT), records[name])
            self.atlases[path] = (image, dimensions(description), records)
        return self.atlases[path]

    @cache
    def original(self, source_type, path, overrides):
        source = self.source_fields(source_type)
        authored = dict(overrides)
        fields = {**source, **authored}
        _, _, records = self.atlas(path)
        authored_state = dm_string(authored.get("icon_state"))
        source_state = dm_string(fields.get("icon_state"))
        base_state = dm_string(fields.get("base_icon_state"))
        candidates = [authored_state]
        if source_type == "/obj/item/gun/ballistic/automatic/pistol":
            candidates.append("pistol")
        elif source_type == "/mob/living/basic/alien/drone":
            candidates.append("aliendrone")
        elif source_type == "/obj/effect/decal/cleanable/blood" and authored_state and authored_state.endswith("-old"):
            candidates.append(authored_state.removesuffix("-old"))
        candidates.extend((source_state, base_state, dm_string(fields.get("icon_living"))))
        if re.search(r'/[\w/]+', source_state or "") and source_state in records:
            return path, source_state
        for state in tuple(candidates):
            if state is not None:
                candidates.extend((f"{state}-0", f"{state}0", f"{state}_0"))
        if authored_state is None and source_state is None:
            candidates.append("")
        for state in candidates:
            if state is None or state not in records or not self.nonempty(path, state):
                continue
            if re.search(r'[<>:"/\\|?*]', state) or state.rstrip(" .") != state:
                continue
            return path, state
        return None

    @cache
    def nonempty(self, path, state):
        image, (width, height), records = self.atlas(path)
        _, dirs, frames, offset = records[state]
        columns = image.width // width
        for index in range(offset, offset + dirs * frames):
            x, y = index % columns * width, index // columns * height
            if image.crop((x, y, x + width, y + height)).getbbox():
                return True
        return False

    def resolve(self, atom):
        if path := self.atlas_path(atom):
            return self.original(atom.path, path, tuple((name, atom.fields[name]) for name in
                                                       ("icon_state", "base_icon_state") if name in atom.fields))
        return None

    def placed_art(self, atom, position):
        resolved = self.resolve(atom)
        if not resolved:
            return None
        path, state = resolved
        if atom.path.startswith("/obj/item/kirbyplants/random"):
            path = PLANT_ATLAS
            _, _, records = self.atlas(path)
            if state == "random_plant":
                options = [name for name in records if re.fullmatch(r"plant-\d\d|applebush|monkeyplant", name)
                           and self.nonempty(path, name)]
                state = self.select_placed_state(atom, position, options)
        elif state in POSTER_FAMILIES and atom.path.startswith("/obj/structure/sign/poster/"):
            families = tuple(f"/obj/structure/sign/poster/{family}/" for family in POSTER_FAMILIES[state])
            _, _, records = self.atlas(path)
            options = {name for kind, fields in self.definitions.items() if kind.startswith(families)
                       and (name := dm_string(fields.get("icon_state"))) in records and name not in MAPPING_STATES
                       and self.nonempty(path, name)}
            state = self.select_placed_state(atom, position, options)
        elif state == "random_bookcase":
            state = "book-2"
        elif state == "random_book":
            state = self.select_placed_state(atom, position, [f"book{index}" for index in range(1, 9)])
        elif state == "randompile":
            state = self.select_placed_state(atom, position, [f"pile{index}" for index in (1, 2)])
        if state in MAPPING_STATES or state not in self.atlas(path)[2]:
            return None
        return path, state

    @staticmethod
    def select_placed_state(atom, position, options):
        options = sorted(options)
        return options[int(digest(repr((atom.path, position))), 16) % len(options)] if options else None

    @staticmethod
    def rsi(path, variant=""):
        key = digest(path + variant)
        return f"_DeepLagoon/Bitrunning/WhiteMoon/{Path(path).stem}_{key}.rsi"

    def add(self, path, state, alias=None, variant=""):
        alias = (state.removesuffix(MOVEMENT) or "base") if alias is None else alias
        if re.search(r'[<>:"/\\|?*]', alias) or alias.rstrip(" .") != alias:
            alias = "state_" + digest(alias)
        self.exports[path, variant].add((state, alias))
        return {"sprite": self.rsi(path, variant), "state": alias}

    def tile(self, atom, target):
        resolved = self.resolve(atom)
        if not resolved:
            self.missing[atom.path]["tile art has no static DMI state"] += 1
            return target
        fields = {**self.source_fields(atom.path), **atom.fields}
        return self.create_tile(atom.path, target, *resolved, dm_int(fields.get("dir"), 2), dm_string(fields.get("color")))

    def damaged_tile(self, atom, target):
        records = self.atlas(DAMAGE_ATLAS)[2]
        variants = ("floorscorched1", "scorched1", "burnt") if "burnt_floor" in atom.path else ("damaged1", "damaged")
        if not (state := self.state_candidate(records, *variants)):
            self.missing[atom.path]["floor damage atlas state unavailable"] += 1
            return target
        return self.create_tile(atom.path, target, DAMAGE_ATLAS, state, 2, None)

    def create_tile(self, source_type, target, path, state, direction, tint):
        image, size, records = self.atlas(path)
        _, dirs, frames, offset = records[state]
        if size != (32, 32):
            self.missing[source_type]["tile art is larger than a grid cell"] += 1
            return target
        frame = FRAME_DIRECTION.index(direction) if direction in FRAME_DIRECTION[:dirs] else 0
        style = repr((path, state, frame, tint))
        tile_id = f"BitrunTile{digest(target + style)}"
        if tile_id not in self.tiles:
            columns = image.width // 32
            index = offset + frame
            x, y = index % columns * 32, index // columns * 32
            png = image.crop((x, y, x + 32, y + 32))
            if frames > 1:
                animation = f"BitrunAnimatedTile{digest(style)}"
                layer = self.add(path, state)
                layer["renderingStrategy"] = "NoRotation"
                if tint and re.fullmatch(r"#[0-9a-fA-F]{6}(?:[0-9a-fA-F]{2})?", tint):
                    layer["color"] = tint
                self.entities[animation] = {
                    "name": "virtual floor animation",
                    "components": [
                        {"type": "Transform", "anchored": True},
                        {"type": "RequiresTile"},
                        {"type": "SyncSprite"},
                        {"type": "Sprite", "drawdepth": "LowFloors", "layers": [layer]},
                    ],
                }
                # The inherited tile still has its collision and physical properties.
                png = Image.new("RGBA", (32, 32))
                self.animations[tile_id] = (animation, direction if dirs > 1 else 2)
            else:
                png = self.tinted(png, tint)
            self.tiles[tile_id] = (target, style, png, path, state)
        self.manifest[source_type]["tile"] += 1
        self.placements[source_type].add(tile_id)
        if tile_id in self.animations:
            self.manifest[source_type]["tile animation"] += 1
        return tile_id

    @staticmethod
    def tinted(image, tint):
        if not tint or not re.fullmatch(r"#[0-9a-fA-F]{6}(?:[0-9a-fA-F]{2})?", tint):
            return image
        red, green, blue = (int(tint[i:i + 2], 16) for i in (1, 3, 5))
        alpha = int(tint[7:9], 16) if len(tint) == 9 else 255
        return Image.merge("RGBA", tuple(channel.point([index * factor // 255 for index in range(256)])
                                         for channel, factor in zip(image.split(), (red, green, blue, alpha))))

    def decal(self, atom, fallback):
        if not (resolved := self.resolve(atom)):
            if fallback:
                self.missing[atom.path]["decal art has no static DMI state"] += 1
            return fallback
        path, state = resolved
        _, size, records = self.atlas(path)
        if size != (32, 32) or records[state][1] != 1 or records[state][2] != 1:
            return fallback
        decal_id = f"BitrunDecal{digest(repr(resolved))}"
        self.decals.setdefault(decal_id, self.add(path, state))
        self.manifest[atom.path]["decal"] += 1
        self.placements[atom.path].add(decal_id)
        return decal_id

    def decorative(self, atom, position):
        """Restore visible objects whose gameplay is not present in the existing port."""
        allowed = ("/obj/item/", "/obj/structure/", "/obj/machinery/",
                   "/obj/effect/decal/", "/obj/effect/turf_decal/")
        if not atom.path.startswith(allowed):
            if atom.path.startswith("/mob/"):
                self.missing[atom.path]["no native gameplay mapping"] += 1
            return None
        source = self.source_fields(atom.path)
        if (invisibility := atom.fields.get("invisibility", source.get("invisibility"))) not in (None, "0"):
            self.missing[atom.path]["source object is hidden"] += 1
            return None
        if not (resolved := self.placed_art(atom, position)):
            self.missing[atom.path]["unmapped object has no static DMI state"] += 1
            return None
        path, state = resolved
        if self.is_spawner(atom.path, path):
            self.missing[atom.path]["no mapped spawn result"] += 1
            return None
        if atom.path.startswith(("/obj/structure/mineral_door", "/obj/machinery/door/")):
            raise ValueError(f"A functional door needs a native mapping: {atom.path}")
        style = self.add(path, state)
        proto = f"BitrunArt{digest(repr((atom.path, resolved)))}"
        portable = atom.path.startswith("/obj/item/")
        sign = atom.path.startswith("/obj/structure/sign/")
        parent = "BaseItem" if portable else "PosterBase" if atom.path.startswith("/obj/structure/sign/poster/") else "BaseSign" if sign else None
        drawdepth = "SmallObjects" if portable else "WallMountedItems" if sign else "FloorObjects" if atom.path.startswith(
            ("/obj/effect/decal/", "/obj/effect/turf_decal/")) else "Objects"
        self.entities[proto] = {
            "name": dm_string(source.get("name")) or atom.path.rsplit("/", 1)[-1].replace("_", " "),
            **({"parent": parent} if parent else {}),
            "components": ([{"type": "Transform", "anchored": True}] if not portable else []) + [
                {"type": "Sprite", "drawdepth": drawdepth, **style}],
        }
        if description := dm_string(source.get("desc")):
            self.entities[proto]["description"] = description
        self.manifest[atom.path]["decorative entity"] += 1
        if not atom.path.startswith(("/obj/effect/decal/", "/obj/effect/turf_decal/")):
            self.missing[atom.path]["restored original art; no native gameplay mapping"] += 1
        self.placements[atom.path].add(proto)
        return proto

    @staticmethod
    def is_spawner(source_type, path):
        return source_type.startswith(("/obj/effect/spawner/", "/obj/effect/mob_spawn/",
                                       "/obj/effect/landmark/", "/obj/effect/mapping_helpers/")) or \
            "spawner" in Path(path).stem

    def entity(self, atom, target, position):
        if target.startswith(("BitrunMob", "BitrunGhost", "MobRandom")) or target in RUNTIME_APPEARANCE:
            self.manifest[atom.path]["retained runtime prototype"] += 1
            return target
        if self.is_spawner(atom.path, self.atlas_path(atom) or ""):
            self.manifest[atom.path]["native spawn result"] += 1
            return target
        if not (resolved := self.placed_art(atom, position)):
            self.missing[atom.path]["entity art has no static DMI state"] += 1
            return target
        components = self.components(target)
        if "Sprite" not in components:
            self.missing[atom.path]["native prototype has no sprite"] += 1
            return target
        path, state = resolved
        source = {**self.source_fields(atom.path), **atom.fields}
        tint = dm_string(source.get("color"))
        opacity = dm_int(source.get("alpha"), 255)
        ident = (target, path, state, source.get("base_icon_state"), source.get("icon_living"),
                 source.get("icon_dead"), source.get("icon_door"), tint, opacity,
                 source.get("icon_keyboard"), source.get("icon_screen"), source.get("keyboard_change_icon"),
                 source.get("icon_deny"), source.get("icon_vend"), source.get("light_mask"), source.get("panel_type"))
        while ident[-1] is None:
            ident = ident[:-1]
        proto = f"{target}BitrunArt{digest(repr(ident))}"
        if proto not in self.entities:
            mapped = self.entity_components(source, components, path, state, digest(repr(ident)))
            if mapped is None:
                self.missing[atom.path]["native visual states need a dedicated adapter"] += 1
                return target
            localization = self.native_entry(target).get("localizationId")
            self.entities[proto] = {
                "parent": target,
                "localizationId": localization or f"ent-{target}",
                "components": mapped,
            }
        self.manifest[atom.path]["entity"] += 1
        self.placements[atom.path].add(proto)
        return proto

    def entity_components(self, source, native, path, state, variant):
        """Keep visualizer layer keys and any overlays the original state does not include."""
        sprite = native["Sprite"]
        layers = [dict(layer) for layer in sprite.get("layers", ()) if isinstance(layer, dict)]
        _, _, records = self.atlas(path)
        original = self.add(path, state)
        overrides = []
        art_layer = 0
        light = path == "icons/obj/lighting.dmi" and "PoweredLight" in native
        wall_light = light and not state.startswith("floor")
        if "LockVisuals" in native:
            self.lock_layers(path, state, variant, records, layers, overrides)
        if light:
            layers = [layer for layer in layers if "enum.PoweredLightLayers.Glow" not in layer.get("map", ())]
            overrides.append({"type": "PoweredLightVisuals", "spriteStateMap": {
                "Empty": "empty", "Off": "off", "On": "on", "Broken": "broken", "Burned": "burned"}})
            if wall_light:
                self.reversed_atlases.add((path, variant))
            else:
                overrides.append({"type": "PointLight", "offset": "0,0"})
            if state.startswith("floor") or state.endswith(("-empty", "-broken")):
                bulb = "Tube" if state.startswith("tube") else "Bulb"
                lamp = None if state.endswith("-empty") else "Light" + bulb
                if state.endswith("-broken"):
                    lamp += "Broken"
                overrides.append({"type": "PoweredLight", "bulb": bulb, "hasLampOnSpawn": lamp})
        if "VendingMachine" in native:
            layers, vending = self.vending_layers(source, path, state, variant, records, layers)
            overrides.append(vending)
            original = layers[0]
        elif "ApcVisuals" in native and "apco3-0" in records:
            original = self.apc_layers(path, variant, records, layers)
        elif any("computerLayerBody" in layer.get("map", ()) for layer in layers):
            layers, visual = self.computer_layers(source, native, path, state, variant, records, layers)
            overrides.append(visual)
            original = layers[0]
        elif path == "icons/obj/machines/suit_storage.dmi" and "EntityStorageVisuals" in native:
            casing = dm_string(source.get("base_icon_state")) or state
            for layer in layers:
                keys = layer.get("map", ())
                if "enum.StorageVisualLayers.Door" in keys:
                    layer.update(self.add(path, casing + "_open"), visible=False)
            overrides.append({"type": "EntityStorageVisuals", "stateDoorClosed": None,
                              "stateDoorOpen": casing + "_open"})
        elif path == "icons/obj/medical/bodybag.dmi" and state.endswith("_folded") and "Foldable" in native:
            art_layer = next((i for i, layer in enumerate(layers) if "foldedLayer" in layer.get("map", ())), None)
            if art_layer is None:
                return None
            bag = state.removesuffix("_folded")
            for layer in layers:
                keys = layer.get("map", ())
                if "unfoldedLayer" in keys and bag in records:
                    layer.update(self.add(path, bag))
                elif "enum.StorageVisualLayers.Door" in keys and bag + "_open" in records:
                    overlay = native["EntityStorageVisuals"].get("stateDoorOpen")
                    layer.update(self.add(path, bag + "_open", overlay, variant))
        elif "Door" in native:
            states = [native["Door"].get(f"{key}SpriteState", key) for key in
                      ("closed", "open", "opening", "closing")]
            prefix = re.sub(r"(?:closed|open|opening|closing)$", "", state)
            closed = self.state_candidate(records, state if state + "open" in records else None,
                                          prefix + "closed", "closed", "door_closed")
            opened = self.state_candidate(records, prefix + "open", "open", "door_open", "door_opened")
            if not layers or not closed or not opened:
                return None
            art_layer = next((i for i, layer in enumerate(layers)
                              if "enum.DoorVisualLayers.Base" in layer.get("map", ())), None)
            if art_layer is None:
                return None
            opening = self.state_candidate(records, prefix + "opening", "opening", "door_opening") or opened
            closing = self.state_candidate(records, prefix + "closing", "closing", "door_closing") or closed
            original = self.add(path, closed, states[0], variant)
            for from_state, to_state in zip((opened, opening, closing), states[1:]):
                self.add(path, from_state, to_state, variant)
        elif "EntityStorageVisuals" in native:
            art_layer = next((i for i, layer in enumerate(layers)
                              if "enum.StorageVisualLayers.Base" in layer.get("map", ())), None)
            visual = native["EntityStorageVisuals"]
            closed = visual.get("stateBaseClosed")
            original = self.add(path, state, closed, variant)
            if opened := visual.get("stateBaseOpen"):
                base = dm_string(source.get("base_icon_state"))
                found = self.state_candidate(records, state + "open", state + "_open", "open",
                                             base + "0" if base else None)
                if not found:
                    return None
                self.add(path, found, opened, variant)
            door_index = next((i for i, layer in enumerate(layers)
                               if "enum.StorageVisualLayers.Door" in layer.get("map", ())), None)
            if art_layer is None and door_index is None:
                return None
            if door_index is not None:
                door = dm_string(source.get("icon_door")) or dm_string(source.get("base_icon_state")) or state
                closed_door = self.state_candidate(records, door + "_door", door + "door", door)
                opened_door = self.state_candidate(records, door + "_open", door + "open", "cardboardopen")
                if path == "icons/obj/machines/crematorium.dmi" and visual.get("stateDoorOpen"):
                    layers[door_index].update(self.add(path, "cremat", visual["stateDoorOpen"], variant))
                    for layer in layers:
                        if "enum.CrematoriumVisualLayers.LightBurning" in layer.get("map", ()):
                            layer.update(self.add(path, "crema_active"))
                        elif "enum.CrematoriumVisualLayers.LightContent" in layer.get("map", ()):
                            layer.update(self.add(path, "crema2"))
                if opened_door and visual.get("stateDoorClosed") and visual.get("stateDoorOpen"):
                    if closed is None or art_layer is None:
                        # A WhiteMoon crate's closed/open DMI states already contain its base.
                        base = art_layer if art_layer is not None else 0
                        layers[base] = {**layers[base], "visible": False}
                        art_layer = door_index
                        original = self.add(path, closed_door or state, visual["stateDoorClosed"], variant)
                    elif closed_door:
                        door_style = self.add(path, closed_door, visual["stateDoorClosed"], variant)
                        layers[door_index] = {**layers[door_index], **door_style}
                    self.add(path, opened_door, visual["stateDoorOpen"], variant)
                elif closed is None or art_layer is None:
                    return None
        elif "DamageStateVisuals" in native:
            damage = native["DamageStateVisuals"]
            normal = self.state_candidate(records, dm_string(source.get("icon_living")), state)
            dead = self.state_candidate(records, state + "_dead", state + "-dead", state + "dead",
                                        dm_string(source.get("icon_dead")), state + "2", "clowns")
            for life, visual_states in damage.get("states", {}).items():
                for key, alias in visual_states.items():
                    if key == "Base" or key == "enum.DamageStateVisualLayers.Base":
                        self.add(path, dead if life in {"Critical", "Dead"} and dead is not None else normal, alias, variant)
            if layers and (old_state := layers[0].get("state")):
                original = self.add(path, normal, old_state, variant)
        smooth = native.get("IconSmooth")
        if smooth and smooth.get("mode") != "NoSprite":
            base = dm_string(source.get("base_icon_state")) or state
            base = re.sub(r"[-_]0$", "", base)
            prefix = next((f"{base}{separator}" for separator in ("-", "_", "")
                           if all(f"{base}{separator}{index}" in records for index in range(16))), None)
            if prefix:
                original = self.add(path, prefix + "0")
                for index in range(1, 16):
                    self.add(path, prefix + str(index))
                overrides.append({"type": "IconSmooth", "mode": "CardinalFlags", "base": prefix})
            else:
                overrides.append({"type": "IconSmooth", "mode": "NoSprite"})
        if layers:
            current_layer = layers[art_layer]
            keys = current_layer.get("map", ())
            alternates = {} if "computerLayerBody" in keys or "VendingMachine" in native else self.dynamic_states(
                native, keys, art_layer, records, state)
            if alternates:
                if original["sprite"] == self.rsi(path):
                    original = self.add(path, alternates.pop(original["state"], state), original["state"], variant)
                else:
                    alternates.pop(original["state"], None)
                for alias, source_state in alternates.items():
                    if alias:
                        self.add(path, source_state, alias, variant)
            layers[art_layer] = {**current_layer, **original}
        else:
            layers = [original]
        sprite_override = {"type": "Sprite", "layers": layers}
        if light:
            sprite_override.update(offset="0,0", noRot=False, snapCardinals=False)
            if wall_light and layers[0]["sprite"] == self.rsi(path):
                layers[0].update(self.add(path, state, variant=variant))
            if not wall_light:
                sprite_override["drawdepth"] = "FloorObjects"
        if "LockVisuals" in native and any("enum.LockVisualLayers.Lock" in layer.get("map", ())
                                           and layer.get("sprite") == self.rsi(path, variant) for layer in layers):
            sprite_override["sprite"] = self.rsi(path, variant)
            for index, layer in enumerate(layers):
                if "sprite" not in layer and sprite.get("sprite"):
                    layers[index] = {**layer, "sprite": sprite["sprite"]}
        source_color = dm_string(source.get("color"))
        opacity = dm_int(source.get("alpha"), 255)
        if source_color and re.fullmatch(r"#[\da-fA-F]{6}", source_color):
            sprite_override["color"] = f"{source_color}{opacity:02x}" if opacity != 255 else source_color
        elif opacity != 255:
            sprite_override["color"] = f"#ffffff{opacity:02x}"
        if path.startswith("icons/obj/doors/puzzledoor"):
            sprite_override["color"] = "#ffffff"
        overrides.append(sprite_override)
        if "Icon" in native:
            overrides.append({"type": "Icon", **self.add(path, state)})
        return overrides

    def lock_layers(self, path, state, variant, records, layers, overrides):
        locked = self.state_candidate(records, state + "_locked", "locked")
        if not locked:
            return
        unlocked = self.state_candidate(records, state + "_unlocked", "unlocked")
        for layer in layers:
            if "enum.LockVisualLayers.Lock" not in layer.get("map", ()):
                continue
            layer.update(self.add(path, locked, variant=variant))
            if unlocked:
                self.add(path, unlocked, variant=variant)
            else:
                unlocked = "lock_indicator_off"
                self.add(path, locked, unlocked, variant)
                self.state_masks[path, variant, unlocked] = None
            overrides.append({"type": "LockVisuals", "stateLocked": locked, "stateUnlocked": unlocked})

    def apc_layers(self, path, variant, records, layers):
        names = {"ChargeState": {"display-lack": "apco3-0", "display-charging": "apco3-1", "display-full": "apco3-2",
                                 "display-remote": "apco3-1", "emag-unlit": "apcemag"},
                 "InterfaceLock": {f"lock0-{alias}": f"apcox-{index}" for index, alias in enumerate(("unlocked", "locked"))},
                 "PanelLock": {f"lock1-{alias}": f"apcox-{index}" for index, alias in enumerate(("unlocked", "locked"))}}
        for index, channel in enumerate(("Equipment", "Lighting", "Environment")):
            names[channel] = {f"channel{index}-{alias}": f"apco{index}-{state}" for state, alias in
                              enumerate(("auto_off", "manual_off", "auto_on", "manual_on"))}
        for layer in layers:
            for key in layer.get("map", ()):
                if key == "enum.WiresVisualLayers.MaintenancePanel":
                    layer.update(self.add(path, "apcewires", variant=variant))
                if aliases := names.get(key.removeprefix("enum.ApcVisualLayers.")):
                    for alias, state in aliases.items():
                        if state in records:
                            self.add(path, state, alias, variant)
                    initial = layer.get("state")
                    layer.update(sprite=self.rsi(path, variant), state=initial if initial in aliases else next(iter(aliases)))
        return self.add(path, "apc0", variant=variant)

    def vending_layers(self, source, path, state, variant, records, layers):
        base_key = "enum.VendingMachineVisualLayers.Base"
        glow_key = "enum.VendingMachineVisualLayers.BaseUnshaded"
        screen_key = "enum.VendingMachineVisualLayers.Screen"
        panel_key = "enum.WiresVisualLayers.MaintenancePanel"
        off = self.state_candidate(records, state + "-off", state)
        broken = self.state_candidate(records, state + "-broken", off)
        normal = self.state_candidate(records, dm_string(source.get("light_mask")), state + "-light-mask", state)
        eject = self.state_candidate(records, dm_string(source.get("icon_vend")), normal)
        deny = self.state_candidate(records, dm_string(source.get("icon_deny")), normal)
        panel = self.state_candidate(records, dm_string(source.get("panel_type")))
        self.add(path, broken, variant=variant)
        self.add(path, eject, variant=variant)
        self.add(path, deny, variant=variant)
        if normal != state:
            # DM uses a monochrome emissive mask. Its alpha selects the original powered cabinet pixels.
            self.add(path, normal, "powered", variant)
            self.state_masks[path, variant, "powered"] = state
            if eject == normal:
                eject = "powered"
            if deny == normal:
                deny = "powered"
            normal = "powered"
        else:
            self.add(path, normal, variant=variant)
        imported = [{"map": [base_key], **self.add(path, off, variant=variant)},
                    {"map": [glow_key], "sprite": self.rsi(path, variant), "state": normal,
                     "shader": "unshaded", "visible": False}]
        for layer in layers:
            keys = set(layer.get("map", ()))
            if keys & {base_key, glow_key, screen_key}:
                continue
            if panel and panel_key in keys:
                layer.update(self.add(path, panel, variant=variant))
            imported.append(layer)
        return imported, {"type": "VendingMachine", "offState": off, "brokenState": broken, "normalState": normal,
                          "denyState": deny, "ejectState": eject, "screenState": None}

    @staticmethod
    def apply_emissive_mask(atlas, size, mask, directions, frames, source):
        _, cabinet_directions, cabinet_frames, offset = source
        width, height = size
        atlas_columns = atlas.width // width
        columns = ceil(sqrt(directions * frames))
        result = Image.new("RGBA", mask.size)
        for direction in range(directions):
            for frame in range(frames):
                source_index = offset + frame % cabinet_frames * cabinet_directions + direction % cabinet_directions
                sx, sy = source_index % atlas_columns * width, source_index // atlas_columns * height
                index = direction * frames + frame
                x, y = index % columns * width, index // columns * height
                tile = atlas.crop((sx, sy, sx + width, sy + height))
                alpha = mask.crop((x, y, x + width, y + height)).getchannel("A")
                tile.putalpha(alpha)
                result.paste(tile, (x, y))
        return result

    def computer_layers(self, source, native, path, state, variant, records, layers):
        keyboard = dm_string(source.get("icon_keyboard"))
        screen = dm_string(source.get("icon_screen"))
        body_key = "computerLayerBody"
        keyboard_key = "computerLayerKeyboard"
        screen_key = "computerLayerScreen"
        keys_key = "computerLayerKeys"
        body = next(layer for layer in layers if body_key in layer.get("map", ()))
        body.update(self.add(path, state, variant=variant))
        power = {}
        imported = [body]
        if keyboard in records:
            off = self.state_candidate(records, keyboard + "_off", keyboard)
            if source.get("keyboard_change_icon") in ("FALSE", "0"):
                off = keyboard
            imported.append({"map": [keyboard_key], **self.add(path, off, variant=variant)})
            imported.append({"map": [keys_key], **self.add(path, keyboard, variant=variant), "visible": False})
            power[keyboard_key] = {"True": {"visible": False}, "False": {"visible": True}}
            power[keys_key] = {"True": {"visible": True, "shader": "unshaded"}, "False": {"visible": False}}
        if screen in records:
            imported.append({"map": [screen_key], **self.add(path, screen, variant=variant), "visible": False})
            power[screen_key] = {"True": {"visible": True, "shader": "unshaded"}, "False": {"visible": False}}
        replaced = {body_key, keyboard_key, screen_key, keys_key}
        imported.extend(layer for layer in layers if not replaced.intersection(layer.get("map", ())))
        visuals = dict(native.get("GenericVisualizer", {}).get("visuals", {}))
        visuals["enum.ComputerVisuals.Powered"] = power
        return imported, {"type": "GenericVisualizer", "visuals": visuals}

    @staticmethod
    def state_candidate(records, *states):
        return next((state for state in states if state in records), None)

    def dynamic_states(self, native, keys, layer, records, state):
        aliases = {}

        def register(alias, *options):
            if isinstance(alias, str) and alias:
                found = self.state_candidate(records, *options, alias, state + "-" + alias,
                                             state + "_" + alias, state + alias)
                aliases[alias] = state if found is None else found

        visuals = native.get("GenericVisualizer", {}).get("visuals", {})
        for mapping in visuals.values():
            for key in keys:
                for value in mapping.get(key, {}).values():
                    if isinstance(value, dict) and "state" in value:
                        register(value["state"])
        if "enum.PoweredLightLayers.Base" in keys:
            root = re.sub(r"-(?:empty|burned|broken)$", "", state)
            for alias, suffix in (("empty", "-empty"), ("off", ""), ("on", "2"), ("broken", "-broken"),
                                  ("burned", "-burned")):
                register(alias, root + suffix, root)
        if "enum.JukeboxVisualLayers.Base" in keys:
            jukebox = native.get("Jukebox", {})
            register(jukebox.get("offState", "off"), state)
            register(jukebox.get("onState", "on"), state + "-active", state)
            register(jukebox.get("selectState", "select"), state + "-select", state)
        movement = native.get("SpriteMovement", {})
        for key in keys:
            moving = movement.get("movementLayers", {}).get(key, {})
            still = movement.get("noMovementLayers", {}).get(key, {})
            register(moving.get("state"), state + MOVEMENT, state)
            register(still.get("state"), state)
        if "enum.PowerChargerVisualLayers.Base" in keys:
            charger = native.get("PowerChargerVisuals", {})
            register(charger.get("emptyState", "empty"), state + "-empty")
            register(charger.get("occupiedState", "full"), state + "-full")
        if "enum.GunVisualLayers.Base" in keys and "ChamberMagazineAmmoProvider" in native:
            register("base", state)
            register("bolt-open", state + "_bolt_locked", state + "_bolt_open")
        stack = native.get("Stack", {})
        if stack.get("baseLayer") in keys:
            for index, alias in enumerate(stack.get("layerStates", ())):
                register(alias, f"{state}_{index + 1}", f"{state}-{index}" if index else state)
        random_sprites = native.get("RandomSprite", {}).get("available", ())
        for group in random_sprites:
            for key, states in group.items():
                if key not in keys and str(key) != str(layer):
                    continue
                for alias in states:
                    normalized = re.sub(r"(?<=\D)0+(?=\d)", "", alias)
                    register(alias, normalized)
        return aliases

    def finish(self):
        """Write the generated prototypes and exact source pixels, then return the coverage report."""
        root = self.station / TEXTURES
        if not self.verify:
            root.mkdir(parents=True, exist_ok=True)
        rows = []
        tiles = {}
        for tile_id, (parent, style, image, path, state) in sorted(self.tiles.items()):
            file = f"tile_{digest(style)}.png"
            self.artifacts.add(file)
            write_frame(root / file, image, self.verify)
            tiles[tile_id] = {"source": path, "state": state, "parent": parent}
            rows.append({"type": "tile", "id": tile_id, "parent": parent, "sprite": f"/Textures/{self.rsi(path).rsplit('/', 1)[0]}/{file}",
                         "variants": 1, "placementVariants": [1]})
        for decal_id, sprite in sorted(self.decals.items()):
            rows.append({"type": "decal", "id": decal_id, "showMenu": False, "sprite": sprite})
        for proto_id, fields in sorted(self.entities.items()):
            rows.append({"type": "entity", "id": proto_id, "categories": ["HideSpawnMenu"], **fields})
        encoded = "# Generated by Tools/bitrunning/import_maps.py.\n" + yaml.safe_dump(
            rows, sort_keys=False, allow_unicode=True, width=120)
        output = self.station / PROTOTYPES
        if self.verify:
            if output.read_text(encoding="utf-8") != encoded:
                raise ValueError(f"Metadata does not match source: {output}")
        else:
            output.write_text(encoded, encoding="utf-8", newline="\n")
        atlases = {}
        for (path, variant), exports in sorted(self.exports.items()):
            image, size, source_states = self.atlas(path)
            output = self.station / "Resources/Textures" / self.rsi(path, variant)
            self.artifacts.add(output.name)
            if not self.verify:
                output.mkdir(parents=True, exist_ok=True)
            states, mappings = [], []
            claims = {}
            for state, alias in sorted(exports):
                if alias.casefold() in {claimed.casefold() for claimed in claims if claimed != alias}:
                    raise ValueError(f"{path}: case-insensitive RSI state clash at {alias}")
                if alias not in claims or state == alias:
                    claims[alias] = state
            for alias, state in sorted(claims.items()):
                block, dirs, frames, offset = source_states[state]
                reverse = (path, variant) in self.reversed_atlases
                metadata, frame, mapping = export_state(image, size, state.removesuffix(MOVEMENT), block, dirs,
                                                        frames, offset, reverse_direction=reverse)
                if reverse and dirs > 1:
                    mapping["reversedDirections"] = True
                mask = (path, variant, alias)
                if mask in self.state_masks:
                    base = self.state_masks[mask]
                    if base is None:
                        frame = Image.new("RGBA", frame.size)
                    else:
                        frame = self.apply_emissive_mask(image, size, frame, dirs, frames, source_states[base])
                    mapping["maskSource"] = base
                metadata["name"] = alias
                mapping["rsi"] = alias
                write_frame(output / f"{alias}.png", frame, self.verify)
                states.append(metadata)
                mappings.append(mapping)
            atlases[self.rsi(path, variant)] = {"source": path, "states": mappings}
            write_json(output / "meta.json", {
                "version": 1, "size": {"x": size[0], "y": size[1]}, "license": "CC-BY-SA-3.0",
                "copyright": f"tgstation, Skyrat and WhiteMoon contributors. Repacked from {URL}/{self.revision}/{path}.",
                "states": states,
            }, self.verify)
            expected = {f"{alias}.png" for alias in claims}
            for stale in sorted(output.glob("*.png")):
                if stale.name not in expected:
                    if self.verify:
                        raise ValueError(f"Stale generated RSI state: {stale}")
                    stale.unlink()
        report = {
            "sourceCommit": self.revision, "tiles": tiles, "atlases": atlases,
            "sourceAtoms": {path: {"appearances": sorted(self.placements[path]),
                                   "applied": dict(sorted(self.manifest[path].items())),
                                   "remaining": dict(sorted(self.missing[path].items()))}
                            for path in sorted(self.manifest.keys() | self.missing.keys())},
        }
        self.artifacts.add("art_manifest.json")
        write_json(root / "art_manifest.json", report, self.verify)
        for stale in sorted(root.iterdir()):
            if stale.name in self.artifacts or not re.fullmatch(
                r"[\w.-]+_[a-f0-9]{12}\.(?:rsi|png)", stale.name
            ):
                continue
            if self.verify:
                raise ValueError(f"Stale generated map art: {stale}")
            if stale.is_dir() and stale.suffix == ".rsi" and stale.resolve().parent == root.resolve():
                shutil.rmtree(stale)
            elif stale.is_file() and stale.suffix == ".png":
                stale.unlink()
        return report
