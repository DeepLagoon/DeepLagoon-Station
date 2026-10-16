#!/usr/bin/env python3
"""Check fixture geometry in YAML resources before it reaches a running server.

Requires PyYAML. Uses the YAML node tree without constructing tagged objects.
"""
from __future__ import annotations

import argparse
import math
from pathlib import Path
import sys

import yaml

ROOT = Path(__file__).resolve().parents[1]
LOADER = getattr(yaml, "CSafeLoader", yaml.SafeLoader)


def numbers(node: yaml.Node, count: int) -> list[float]:
    if not isinstance(node, yaml.ScalarNode):
        raise ValueError("expected comma-separated coordinates")
    values = [float(part.strip()) for part in node.value.split(",")]
    if len(values) != count or not all(math.isfinite(value) for value in values):
        raise ValueError(f"expected {count} finite coordinates")
    return values


def check_text(text: str) -> list[tuple[int, str]]:
    errors: list[tuple[int, str]] = []
    visited: set[int] = set()

    def walk(node: yaml.Node | None) -> None:
        if node is None or id(node) in visited:
            return
        visited.add(id(node))
        if isinstance(node, yaml.MappingNode):
            fields = {key.value: value for key, value in node.value}
            try:
                if node.tag == "!type:PhysShapeAabb" and "bounds" in fields:
                    left, bottom, right, top = numbers(fields["bounds"], 4)
                    if left >= right or bottom >= top:
                        raise ValueError("PhysShapeAabb bounds require left < right and bottom < top")
                elif node.tag == "!type:PolygonShape":
                    vertices = fields.get("vertices")
                    if not isinstance(vertices, yaml.SequenceNode) or not 3 <= len(vertices.value) <= 8:
                        raise ValueError("PolygonShape requires 3-8 vertices")
                    points = [numbers(value, 2) for value in vertices.value]
                    # Ordering is normalized by PhysicsHullSerializer; reject a collapsed hull.
                    a = points[0]
                    if not any((b[0] - a[0]) * (c[1] - a[1]) != (b[1] - a[1]) * (c[0] - a[0])
                               for b in points[1:] for c in points[1:]):
                        raise ValueError("PolygonShape vertices are collinear or identical")
                if node.tag in {"!type:PolygonShape", "!type:PhysShapeAabb", "!type:PhysShapeCircle",
                                "!type:EdgeShape", "!type:ChainShape"} and "radius" in fields:
                    if numbers(fields["radius"], 1)[0] < 0:
                        raise ValueError("shape radius must be non-negative")
                if node.tag == "!type:PhysShapeCircle" and "position" in fields:
                    numbers(fields["position"], 2)
                fixtures = fields.get("fixtures")
                if isinstance(fixtures, yaml.MappingNode):
                    for key, fixture in fixtures.value:
                        if not key.value:
                            errors.append((key.start_mark.line + 1, "empty fixture ID"))
                        if isinstance(fixture, yaml.MappingNode):
                            for key, value in fixture.value:
                                if key.value in {"density", "friction", "restitution"} and numbers(value, 1)[0] < 0:
                                    errors.append((value.start_mark.line + 1, f"negative fixture {key.value}"))
            except (ValueError, TypeError) as exc:
                errors.append((node.start_mark.line + 1, str(exc)))
            for _, value in node.value:
                walk(value)
        elif isinstance(node, yaml.SequenceNode):
            for value in node.value:
                walk(value)
        elif node.tag == "!type:PolygonShape":
            errors.append((node.start_mark.line + 1, "PolygonShape is missing vertices"))

    try:
        for document in yaml.compose_all(text, Loader=LOADER):
            walk(document)
    except yaml.YAMLError as exc:
        mark = getattr(exc, "problem_mark", None)
        errors.append((mark.line + 1 if mark else 1, f"invalid YAML: {exc}"))
    return errors


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("paths", nargs="*", type=Path)
    args = parser.parse_args()
    files: set[Path] = set()
    for path in args.paths or [ROOT / "Resources/Prototypes", ROOT / "Resources/Maps"]:
        if path.is_dir():
            files.update(p for p in path.rglob("*") if p.is_file() and p.suffix in {".yml", ".yaml", ""})
        else:
            files.add(path)
    failures = 0
    for path in sorted(files):
        text = path.read_text(encoding="utf-8-sig")
        # Other resources need not be parsed by this focused physics check.
        if not any(tag in text for tag in ("!type:PhysShape", "!type:PolygonShape", "!type:EdgeShape", "!type:ChainShape")):
            continue
        for line, reason in check_text(text):
            print(f"{path}:{line}: {reason}")
            failures += 1
    print(f"Physics geometry: {len(files)} resource files scanned, {failures} errors")
    return int(failures != 0)


if __name__ == "__main__":
    sys.exit(main())
