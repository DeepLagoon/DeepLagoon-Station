"""Map-format-aware placement diff; serialized entity/decal IDs are not identities."""
from __future__ import annotations

import base64
from collections import Counter
from dataclasses import dataclass, field
import json
import math
import struct

import yaml


class MapLoader(yaml.CSafeLoader):
    pass


def tagged(loader, tag, node):
    if isinstance(node, yaml.MappingNode):
        return loader.construct_mapping(node)
    if isinstance(node, yaml.SequenceNode):
        return loader.construct_sequence(node)
    return loader.construct_scalar(node)


MapLoader.add_multi_constructor("!type:", tagged)


def vector(value):
    return tuple(float(n) for n in str(value).split(","))


def angle(value=0):
    text = str(value or 0)
    result = float(text.split()[0])
    if "rad" in text:
        result = math.degrees(result)
    return round(result % 360, 3) % 360


@dataclass
class Grid:
    uid: int
    name: str
    tiles: dict = field(default_factory=dict)
    objects: Counter = field(default_factory=Counter)
    decals: Counter = field(default_factory=Counter)
    warnings: list = field(default_factory=list)


def read_map(raw: bytes) -> list[Grid]:
    data = yaml.load(raw, Loader=MapLoader)
    if not isinstance(data, dict) or "entities" not in data:
        raise ValueError("Not an SS14 map file")
    entities = {}
    for group in data["entities"]:
        for entity in group["entities"]:
            entities[entity["uid"]] = (group.get("proto", ""), {
                c["type"]: c for c in entity.get("components", [])})
    grids = []
    for uid, (proto, components) in entities.items():
        if "MapGrid" not in components:
            continue
        grid = Grid(uid, components.get("MetaData", {}).get("name", proto))
        for chunk in components["MapGrid"].get("chunks", {}).values():
            cx, cy = map(int, str(chunk["ind"]).split(","))
            size, version = chunk.get("size", 16), chunk.get("version", 1)
            if version < 1 or version > 7:
                raise ValueError(f"Unsupported tile chunk version {version}")
            fmt = "<iBBB" if version >= 7 else "<iBB" if version >= 6 else "<HBB"
            tiles = list(struct.iter_unpack(fmt, base64.b64decode(chunk["tiles"], validate=True)))
            if len(tiles) != size * size:
                raise ValueError("Tile chunk size does not match its payload")
            for index, value in enumerate(tiles):
                name = data["tilemap"][value[0]]
                if name != "Space":
                    grid.tiles[cx*size + index % size, cy*size + index // size] = (
                        name, *value[1:], *([0] if version < 7 else []))
        for entity_proto, comp in entities.values():
            tr = comp.get("Transform", {})
            if not entity_proto or tr.get("parent") != uid:
                continue
            x, y = vector(tr.get("pos", "0,0"))
            grid.objects[entity_proto, round(x, 4), round(y, 4), angle(tr.get("rot"))] += 1
        collection = components.get("DecalGrid", {}).get("chunkCollection", {})
        if collection and collection.get("version") != 2:
            grid.warnings.append("Decal placement statistics require DecalGrid version 2")
        for node in collection.get("nodes", []):
            properties = dict(node["node"])
            properties["angle"] = angle(properties.get("angle"))
            style = json.dumps(properties, sort_keys=True, default=str)
            for pos in node.get("decals", {}).values():
                grid.decals[style, *vector(pos)] += 1
        grids.append(grid)
    return grids


def match_grids(before: list[Grid], after: list[Grid]):
    """Prefer matching geometry and names over UIDs, which change when a grid becomes a map."""
    remaining = list(before)
    for new in after:
        ranked = []
        for old in remaining:
            a, b = set(old.tiles), set(new.tiles)
            overlap = len(a & b) / max(1, len(a | b))
            same_name = bool(new.name and old.name == new.name)
            if overlap >= .35 or same_name:
                ranked.append((overlap + same_name + .001*(old.uid == new.uid), old))
        old = max(ranked, key=lambda item: item[0])[1] if ranked else None
        if old:
            remaining.remove(old)
        yield old, new
    for old in remaining:
        yield old, None


def compare(before: Grid | None, after: Grid | None):
    a, b = before or Grid(-1, ""), after or Grid(-1, "")
    changes = []
    counts = Counter()
    for (x, y) in sorted(a.tiles.keys() | b.tiles.keys()):
        old, new = a.tiles.get((x, y)), b.tiles.get((x, y))
        if old == new:
            continue
        kind = "added" if old is None else "removed" if new is None else "changed"
        counts["tiles_" + kind] += 1
        changes.append({"x": x+.5, "y": y+.5, "kind": kind, "type": "tile",
                        "before": old, "after": new})
    added = list((b.objects-a.objects).elements())
    removed = list((a.objects-b.objects).elements())
    for obj in added[:]:
        old = next((v for v in removed if v[:3] == obj[:3]), None)
        if old:
            added.remove(obj)
            removed.remove(old)
            counts["objects_rotated"] += 1
            changes.append({"x": obj[1], "y": obj[2], "kind": "changed", "type": "rotation",
                            "prototype": obj[0], "before": old[3], "after": obj[3]})
    for kind, values in [("added", added), ("removed", removed)]:
        counts["objects_" + kind] = len(values)
        for proto, x, y, rot in values:
            changes.append({"x": x, "y": y, "kind": kind, "type": "object", "prototype": proto})
    for kind, values in [("added", b.decals-a.decals), ("removed", a.decals-b.decals)]:
        counts["decals_" + kind] = sum(values.values())
        for (_, x, y), n in values.items():
            changes.extend({"x": x+.5, "y": y+.5, "kind": kind, "type": "decal"} for _ in range(n))
    return dict(counts), changes
