#!/usr/bin/env python3
"""Validate server revisions against an official engine release, without commit pins.

Authentication, transport, loader and version sources stay official.
UI scale validation and grid collision stepping have reviewed implementation-only fixes.
"""
import argparse
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
REVIEWED_ENGINE_PATHS = frozenset({
    "Robust.Shared/Prototypes/PrototypeManager.cs",
    "Robust.Shared.IntegrationTests/Prototypes/PrototypeReloadTests.cs",
    "Robust.Shared/Physics/Systems/FixtureSystem.cs",
    "Robust.Shared/Physics/Systems/FixtureSystem.Validation.cs",
    "Robust.Shared/Physics/Systems/SharedPhysicsSystem.Components.cs",
    "Robust.Shared/Physics/Systems/SharedPhysicsSystem.cs",
    "Robust.Shared/Physics/Systems/SharedPhysicsSystem.GridSubsteps.cs",
    "Robust.Shared.IntegrationTests/Physics/GridMotionSubstepTests.cs",
    "Robust.Client/UserInterface/UserInterfaceManager.Scaling.cs",
    "Robust.Client.Tests/UserInterface/UIScaleTest.cs",
    "Robust.Shared/Physics/Events/InvalidPhysicsFixtureEvent.cs",
    "Robust.Shared.IntegrationTests/Physics/InvalidFixtureTests.cs",
    "Robust.Shared/GameObjects/Systems/SharedPointLightSystem.cs",
    "Robust.Shared/GameStates/ChunkEntitySystem.cs",
    "Robust.Shared/Serialization/Manager/SerializationManager.Reading.cs",
    "Robust.Shared/Serialization/Manager/SerializationManager.SerializerProvider.cs",
    "Robust.Shared/Serialization/Manager/SerializationManager.cs",
    "Robust.Shared/Serialization/TypeSerializers/Implementations/EntProtoIdSerializer.cs",
    "Robust.Shared/Toolshed/ReflectionExtensions.cs",
    "Robust.Shared.IntegrationTests/Serialization/SerializationTests/GeneratedDefinitionReadingTests.cs",
})


class CompatibilityError(RuntimeError):
    pass


def find_git() -> str:
    executable = shutil.which("git")
    if executable:
        return executable
    # Editor tasks may inherit a PATH without Git even when Git for Windows is installed.
    if sys.platform == "win32":
        locations = (
            ("ProgramW6432", "Git/cmd/git.exe"),
            ("ProgramFiles", "Git/cmd/git.exe"),
            ("ProgramFiles(x86)", "Git/cmd/git.exe"),
            ("LocalAppData", "Programs/Git/cmd/git.exe"),
        )
        for variable, relative in locations:
            directory = os.environ.get(variable)
            if directory:
                candidate = Path(directory) / relative
                if candidate.is_file():
                    return str(candidate)
    raise CompatibilityError("Git was not found. Install Git or add its cmd directory to PATH, then restart the editor")


def _git(root: Path, *args: str) -> str:
    command = [find_git(), "-c", f"safe.directory={root.as_posix()}", "-C", str(root), *args]
    try:
        result = subprocess.run(command, capture_output=True, text=True, encoding="utf-8", errors="replace", check=False)
    except OSError as exc:
        raise CompatibilityError(f"Cannot start Git ({command[0]}): {exc}") from exc
    if result.returncode:
        raise CompatibilityError(f"Cannot verify engine source: git {' '.join(args[:2])} failed: " +
                                 (result.stderr.strip() or f"exit code {result.returncode}"))
    return result.stdout.strip()


def read_contract(root: Path) -> dict:
    try:
        contract = json.loads((root / "ENGINE-COMPATIBILITY.json").read_text(encoding="utf-8"))
        if contract["schema_version"] != 2:
            raise ValueError("unsupported schema")
        if set(contract) != {"schema_version", "official_engine_version"}:
            raise ValueError("contract must declare only the official engine version")
        if not re.fullmatch(r"\d+\.\d+\.\d+", contract["official_engine_version"]):
            raise ValueError("official engine version must be exact")
        return contract
    except (OSError, ValueError, KeyError, TypeError) as exc:
        raise CompatibilityError(f"Invalid ENGINE-COMPATIBILITY.json: {exc}") from exc


def check(root: Path = ROOT, *, allow_stock: bool = False) -> dict:
    root = root.resolve()
    engine = root / "RobustToolbox"
    contract = read_contract(root)
    if _git(engine, "status", "--porcelain", "--untracked-files=all", "--ignore-submodules=none"):
        raise CompatibilityError("Engine checkout or its dependencies have uncommitted changes")
    release = "refs/tags/v" + contract["official_engine_version"]
    changed = set(filter(None, _git(engine, "diff", "--no-ext-diff", "--no-textconv", "--name-only", release, "HEAD", "--").splitlines()))
    unexpected = changed - REVIEWED_ENGINE_PATHS
    if unexpected:
        raise CompatibilityError("Unreviewed engine changes relative to the official client baseline: " +
                                 ", ".join(sorted(unexpected)))
    if allow_stock and changed:
        raise CompatibilityError("Stock validation requires unmodified official release source")
    try:
        version = ET.parse(engine / "MSBuild/Robust.Engine.Version.props").findtext("./PropertyGroup/Version")
    except (OSError, ET.ParseError) as exc:
        raise CompatibilityError("Cannot read the engine version") from exc
    if version != contract["official_engine_version"]:
        raise CompatibilityError(f"Engine version {version!r} is not official {contract['official_engine_version']}")
    for name in ("DeepLagoon.Network.targets", "DeepLagoon.WebView.targets", "DeepLagoon.FuzzySearch.targets"):
        if (root / "MSBuild" / name).exists():
            raise CompatibilityError(f"Station must not patch engine implementations: {name}")
    forbidden = re.compile(r"\b(WebViewJson|ReadPostBody|SetSubImageBottomUp)\b")
    for path in (root / "Content.Client").rglob("*.cs"):
        if any(part in ("bin", "obj") for part in path.relative_to(root).parts):
            continue
        if forbidden.search(path.read_text(encoding="utf-8-sig")):
            raise CompatibilityError(f"Fork-only client API reference: {path.relative_to(root)}")
    return {
        "schema_version": 2,
        "engine_version": contract["official_engine_version"],
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=ROOT)
    parser.add_argument("--allow-stock", action="store_true", help="CI-only validation against unmodified official release source")
    parser.add_argument("--metadata-output", type=Path)
    args = parser.parse_args()
    try:
        if args.allow_stock and args.metadata_output:
            raise CompatibilityError("Stock validation cannot produce release metadata")
        metadata = check(args.root, allow_stock=args.allow_stock)
        if args.metadata_output:
            args.metadata_output.parent.mkdir(parents=True, exist_ok=True)
            args.metadata_output.write_text(json.dumps(metadata, indent=2) + "\n", encoding="utf-8")
        print(f"Official engine compatibility: {metadata['engine_version']}")
        return 0
    except CompatibilityError as exc:
        print(f"Engine compatibility check failed: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
