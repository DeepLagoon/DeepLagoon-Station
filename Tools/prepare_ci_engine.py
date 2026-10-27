#!/usr/bin/env python3
"""Prepare the pinned engine and the official release reference used by the build gate."""
from pathlib import Path
import subprocess

from check_engine_compatibility import ROOT, find_git, read_contract

OFFICIAL_ENGINE = "https://github.com/space-wizards/RobustToolbox.git"


def prepare(root: Path = ROOT) -> None:
    root = root.resolve()
    version = read_contract(root)["official_engine_version"]
    git = find_git()
    subprocess.run([git, "-C", str(root), "submodule", "update", "--init", "--recursive"], check=True)
    release = f"refs/tags/v{version}"
    subprocess.run([git, "-C", str(root / "RobustToolbox"), "fetch", "--no-tags",
                    OFFICIAL_ENGINE, f"+{release}:{release}"], check=True)


if __name__ == "__main__":
    prepare()
