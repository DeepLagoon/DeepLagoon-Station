#!/usr/bin/env python3
"""Detect changed SS14 maps, render Git snapshots, and produce a portable PR report."""
from __future__ import annotations

import argparse
import html
import json
import math
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[2]
SHA = re.compile(r"[0-9a-f]{40}")


def git(*args, repo=ROOT):
    return subprocess.check_output(["git", "-C", str(repo), *args])


def map_path(path):
    p = PurePosixPath(path)
    return p.parts[:2] == ("Resources", "Maps") and p.suffix.lower() in (".yml", ".yaml")


def changed_maps(base, head, repo=ROOT):
    if not SHA.fullmatch(base) or not SHA.fullmatch(head):
        raise ValueError("Expected full Git commit SHAs")
    ancestor = git("merge-base", base, head, repo=repo).decode().strip()
    fields = git("diff", "--name-status", "-z", "-M", ancestor, head, "--", "Resources/Maps/", repo=repo).decode().split("\0")
    files = []
    index = 0
    while index < len(fields)-1:
        status, path = fields[index:index+2]
        index += 2
        old, new = (None, path) if status == "A" else (path, None) if status == "D" else (path, path)
        if status[0] in "RC":
            new = fields[index]
            index += 1
            old = path if status[0] == "R" else None
        if (old and map_path(old)) or (new and map_path(new)):
            files.append({"before": old if old and map_path(old) else None,
                          "after": new if new and map_path(new) else None})
    return ancestor, files


def plan(args):
    event = json.loads(Path(args.event).read_text(encoding="utf-8")) if args.event else None
    pr = event["pull_request"] if event else None
    base, head = (pr["base"]["sha"], pr["head"]["sha"]) if pr else (args.base, args.head)
    ancestor, files = changed_maps(base, head)
    result = {"version": 1, "pr": event["number"] if event else 0,
              "base_sha": base, "head_sha": head, "merge_base": ancestor, "files": files}
    args.output.mkdir(parents=True, exist_ok=True)
    (args.output / "plan.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    if os.environ.get("GITHUB_OUTPUT"):
        with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as out:
            out.write(f"changed={'true' if files else 'false'}\n")
    print(f"Changed maps: {len(files)}")


def render_snapshot(raw, destination, renderer, timeout):
    destination.mkdir(parents=True, exist_ok=True)
    snapshot = destination / "input.yml"
    snapshot.write_bytes(raw)
    with (destination / "render.log").open("w", encoding="utf-8") as log:
        result = subprocess.run(["dotnet", str(renderer), "--snapshot", str(snapshot.resolve()),
                                 "--output", str(destination.resolve())], cwd=ROOT, stdout=log,
                                stderr=subprocess.STDOUT, timeout=timeout,
                                env={**os.environ, "DOTNET_gcServer": "0"})
    marker = destination / "snapshot.json"
    if result.returncode != 0 or not marker.is_file():
        raise RuntimeError(f"MapRenderer failed ({result.returncode}); see {destination.name}/render.log")
    return {g["yaml_uid"]: g for g in json.loads(marker.read_text(encoding="utf-8"))}


def font(size):
    from PIL import ImageFont
    for name in ["DejaVuSans.ttf", "C:/Windows/Fonts/arial.ttf"]:
        try:
            return ImageFont.truetype(name, size)
        except OSError:
            pass
    return ImageFont.load_default(size=size)


def preview(old, new, renders, folders, changes, output, index):
    from PIL import Image, ImageDraw
    coords = set((old.tiles if old else {})) | set((new.tiles if new else {}))
    coords.update((math.floor(c["x"]), math.floor(c["y"])) for c in changes)
    if not coords:
        coords = {(0, 0)}
    left, right = min(x for x, _ in coords), max(x for x, _ in coords)+1
    bottom, top = min(y for _, y in coords), max(y for _, y in coords)+1
    # Cap the preview, not the source render. Coordinate transforms retain the same scale on both sides.
    scale = min(16, 900/max(top-bottom, right-left))
    w, h = max(1, math.ceil((right-left)*scale)), max(1, math.ceil((top-bottom)*scale))
    canvases = []
    sources = []
    for stage, grid, rendered, folder in zip(["before", "after"], [old, new], renders, folders):
        image = Image.new("RGB", (w, h), "#111820")
        if grid:
            meta = rendered.get(grid.uid)
            if not meta:
                raise ValueError(f"Renderer did not return YAML grid {grid.uid}")
            full_name = f"map-{index:03}-{stage}-full.png"
            shutil.copyfile(folder/meta["file"], output/full_name)
            sources.append(full_name)
            with Image.open(folder/meta["file"]) as source:
                size = (max(1, round(source.width*scale/32)), max(1, round(source.height*scale/32)))
                tile = source.convert("RGBA").resize(size)
                image.paste(tile, (round((meta["x"]-left)*scale),
                                   round((top-meta["y"]-meta["height"]/32)*scale)), tile)
        canvases.append(image)
    overlay = canvases[1].copy()
    draw = ImageDraw.Draw(overlay, "RGBA")
    colors = {"added": (78, 222, 143, 210), "removed": (255, 100, 120, 220), "changed": (255, 202, 80, 230)}
    for change in changes:
        x, y = (change["x"]-left)*scale, (top-change["y"])*scale
        r = max(2.5, scale*.45)
        color = colors[change["kind"]]
        if change["kind"] == "removed":
            draw.line((x-r,y-r,x+r,y+r), fill=color, width=2)
            draw.line((x+r,y-r,x-r,y+r), fill=color, width=2)
        else:
            draw.ellipse((x-r,y-r,x+r,y+r), outline=color, width=2)
    files = []
    for stage, image in zip(["before", "after", "diff"], [*canvases, overlay]):
        name = f"map-{index:03}-{stage}.png"
        image.save(output/name, optimize=True)
        files.append(name)
    gap, header = 12, 40
    result = Image.new("RGB", (w*3+gap*4, h+header+42), "#111820")
    draw = ImageDraw.Draw(result)
    for i, (name, image) in enumerate(zip(["BEFORE", "AFTER", "PLACEMENT DIFF"], [*canvases, overlay])):
        x = gap+(w+gap)*i
        draw.text((x, 10), name, fill="white", font=font(16))
        result.paste(image, (x, header))
    draw.text((gap, h+header+12), "+ added    x removed    o changed / rotated", fill="#c9d5e5", font=font(14))
    # Reduce wide multi-panel previews to GitHub-friendly sizes.
    result.thumbnail((2400, 1400))
    name = f"preview-{index:03}.png"
    result.save(output/name, optimize=True)
    return name, files, sources


def build(args):
    from model import read_map, match_grids, compare
    data = json.loads((args.output/"plan.json").read_text(encoding="utf-8"))
    report = {k: data[k] for k in ["version", "pr", "base_sha", "head_sha", "merge_base"]}
    report.update(maps=[], errors=[])
    work = args.output.parent/(args.output.name+"-work")
    work.mkdir(parents=True, exist_ok=True)
    index = 0
    for number, paths in enumerate(data["files"]):
        path = paths["after"] or paths["before"]
        entry = {"path": path, "previous_path": paths["before"], "grids": []}
        report["maps"].append(entry)
        try:
            grid_data, folders, renders = [], [], []
            for stage, revision in [("before", data["merge_base"]), ("after", data["head_sha"])]:
                raw = git("show", f"{revision}:{paths[stage]}") if paths[stage] else None
                grid_data.append(read_map(raw) if raw else [])
                folder = work/f"{number:03}-{stage}"
                folders.append(folder)
                renders.append(render_snapshot(raw, folder, args.renderer, args.timeout) if raw else {})
            for old, new in match_grids(*grid_data):
                stats, changes = compare(old, new)
                name, images, sources = preview(old, new, renders, folders, changes, args.output, index)
                entry["grids"].append({"name": (new or old).name or f"Grid {index+1}",
                                       "preview": name, "images": images, "source_images": sources, "stats": stats,
                                       "changes": changes, "warnings": (old.warnings if old else []) + (new.warnings if new else [])})
                index += 1
            if not entry["grids"]:
                entry["warning"] = "No renderable grids in either version"
        except Exception as error:
            entry["error"] = str(error)
            report["errors"].append(path)
            print(f"::warning::Map diff failed for {path!r}: {type(error).__name__}")
    write_report(args.output, report)
    # Other maps still produce previews when one file cannot be loaded.
    return int(bool(report["errors"]))


def write_report(output, report):
    (output/"report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    sections = []
    for entry in report["maps"]:
        content = f'<h2>{html.escape(entry["path"])}</h2>'
        if entry.get("error"):
            content += '<p class="error">'+html.escape(entry["error"])+"</p>"
        for grid in entry["grids"]:
            content += '<h3>'+html.escape(grid["name"])+"</h3>"
            content += '<p>'+html.escape(json.dumps(grid["stats"]))+"</p>"
            content += f'<a href="{grid["preview"]}"><img src="{grid["preview"]}" alt="Before, after and placement diff"></a>'
            content += '<p>'+" · ".join(f'<a href="{f}">{s}</a>' for f, s in zip(grid["images"], ["Before", "After", "Diff"]))+"</p>"
            content += '<p>'+" · ".join(f'<a href="{f}">Full resolution: {f}</a>' for f in grid.get("source_images", []))+"</p>"
            for warning in grid["warnings"]:
                content += '<p>'+html.escape(warning)+"</p>"
        sections.append(content)
    document = '''<!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width">
<title>Station map diff</title><style>body{font:16px system-ui;background:#111820;color:#e4ecf5;margin:24px}img{max-width:100%;height:auto}a{color:#7dbdff}h2{overflow-wrap:anywhere}.error{color:#ff9a9a}</style>
<h1>Station map diff</h1><p>Before / after use current PR prototypes. Markers compare saved tiles, direct grid objects, rotations and decals, ignoring serialized IDs. Component settings, container contents and map-level entities are outside placement statistics.</p>'''
    (output/"index.html").write_text(document+"\n".join(sections)+"</html>", encoding="utf-8")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mode", choices=["plan", "build"])
    parser.add_argument("--event", type=Path)
    parser.add_argument("--base")
    parser.add_argument("--head")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--renderer", type=Path, default=ROOT/"bin/Content.MapRenderer/Content.MapRenderer.dll")
    parser.add_argument("--timeout", type=int, default=300)
    args = parser.parse_args()
    args.output = args.output.resolve()
    return plan(args) if args.mode == "plan" else build(args)


if __name__ == "__main__":
    sys.exit(main())
