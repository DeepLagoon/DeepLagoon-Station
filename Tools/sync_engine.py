#!/usr/bin/env python3
"""Prepare a compatible fast-forward from DeepLagoon/RobustToolbox main.

Does not commit, push, deploy, or change the official launcher baseline.
"""
import argparse
import json
import os
from pathlib import Path
import re
import subprocess
import xml.etree.ElementTree as ET

import check_engine_compatibility as compatibility

ENGINE_REMOTE = "https://github.com/DeepLagoon/RobustToolbox.git"
OFFICIAL_REMOTE = "https://github.com/space-wizards/RobustToolbox.git"
CANDIDATE_REF = "refs/deeplagoon/engine-sync"


def git(root, *args):
    env = {key: value for key, value in os.environ.items() if not key.upper().startswith("GIT_")}
    env.update(GIT_TERMINAL_PROMPT="0", GIT_CONFIG_NOSYSTEM="1", GIT_CONFIG_GLOBAL=os.devnull)
    return subprocess.run(
        [compatibility.find_git(), "-c", "safe.directory=" + root.as_posix(),
         "-c", "core.hooksPath=" + os.devnull, "-c", "core.fsmonitor=false",
         "-C", str(root), *args], env=env, check=True, capture_output=True,
        text=True, encoding="utf-8", timeout=300,
    ).stdout.strip()


def candidate_version(engine, commit):
    try:
        document = ET.fromstring(git(engine, "show", commit + ":MSBuild/Robust.Engine.Version.props"))
        version = document.findtext("./PropertyGroup/Version")
    except ET.ParseError as exc:
        raise compatibility.CompatibilityError("Invalid candidate engine version") from exc
    if not isinstance(version, str) or not re.fullmatch(r"\d+\.\d+\.\d+", version):
        raise compatibility.CompatibilityError("Invalid candidate engine version")
    return version


def prepare(root=compatibility.ROOT):
    root = root.resolve()
    engine = root / "RobustToolbox"
    if git(root, "status", "--porcelain", "--untracked-files=all", "--ignore-submodules=none"):
        raise compatibility.CompatibilityError("Engine sync requires a clean Station checkout")
    contract = compatibility.read_contract(root)
    entry = git(root, "ls-tree", "HEAD", "--", "RobustToolbox").split()
    if len(entry) != 4 or entry[:2] != ["160000", "commit"]:
        raise compatibility.CompatibilityError("Station must track the engine as a submodule")
    previous = entry[2]
    if git(engine, "rev-parse", "HEAD") != previous:
        raise compatibility.CompatibilityError("Engine checkout differs from the Station gitlink")
    # Never trust a mutable origin URL or an event-supplied SHA/ref.
    git(engine, "fetch", "--no-tags", "--no-recurse-submodules", ENGINE_REMOTE,
        "+refs/heads/main:" + CANDIDATE_REF)
    candidate = git(engine, "rev-parse", CANDIDATE_REF)
    version = candidate_version(engine, candidate)
    if version != contract["official_engine_version"]:
        raise compatibility.CompatibilityError(
            "Official engine version changed from " + contract["official_engine_version"] + " to " + version +
            "; review the signed WebView and offline builder before upgrading the launcher baseline")
    result = {"changed": candidate != previous, "engine_commit": candidate, "engine_version": version}
    if candidate == previous:
        return result
    # No silent downgrade, branch rewrite, or unrelated engine history.
    git(engine, "merge-base", "--is-ancestor", previous, candidate)
    release = "refs/tags/v" + version
    git(engine, "fetch", "--no-tags", "--no-recurse-submodules", OFFICIAL_REMOTE,
        "+" + release + ":" + release)
    changed = set(filter(None, git(engine, "diff", "--no-ext-diff", "--no-textconv",
                                 "--name-only", release, candidate, "--").splitlines()))
    unexpected = changed - compatibility.REVIEWED_ENGINE_PATHS
    if unexpected:
        raise compatibility.CompatibilityError("Unreviewed engine changes: " + ", ".join(sorted(unexpected)))
    # Check ownership before updating nested dependencies or running candidate build code.
    try:
        git(engine, "checkout", "--detach", candidate)
        git(engine, "submodule", "update", "--init", "--recursive")
        compatibility.check(root)
    except Exception:
        git(engine, "checkout", "--detach", previous)
        git(engine, "submodule", "update", "--init", "--recursive")
        raise
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=compatibility.ROOT)
    args = parser.parse_args()
    result = prepare(args.root)
    if output := os.environ.get("GITHUB_OUTPUT"):
        with open(output, "a", encoding="utf-8") as stream:
            for key, value in result.items():
                stream.write(key + "=" + (str(value).lower() if isinstance(value, bool) else value) + "\n")
    print(json.dumps(result))


if __name__ == "__main__":
    main()
