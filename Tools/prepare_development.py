#!/usr/bin/env python3
"""Prepare local development tools, then optionally run a command in that environment."""
from contextlib import contextmanager
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import time

from check_engine_compatibility import CompatibilityError, ROOT, find_git, read_contract
from prepare_ci_engine import OFFICIAL_ENGINE

PNPM_VERSION = "11.25.0"
MIN_NODE = (22, 13, 0)


class PreparationError(RuntimeError):
    pass


def run(command, *, root=ROOT, env=None, capture=False):
    if env is not None and not os.path.dirname(str(command[0])):
        executable = shutil.which(str(command[0]), path=env.get("PATH"))
        if not executable:
            raise PreparationError(f"{command[0]} was not found. Install it and restart VS Code.")
        command = [executable, *command[1:]]
    # .cmd tools require cmd.exe on Windows. list2cmdline preserves quoted paths.
    if os.name == "nt" and str(command[0]).lower().endswith((".cmd", ".bat")):
        parts = [subprocess.list2cmdline([str(part)]) for part in command]
        parts = ['"' + part + '"' if not part.startswith('"') and re.search(r"[&|<>^()]", part)
                 else part for part in parts]
        # Pass a command line string: passing this through list2cmdline a second
        # time would turn cmd.exe's nested quotes into literal backslash-quotes.
        command = (subprocess.list2cmdline([os.environ.get("COMSPEC", "cmd.exe")]) +
                   ' /d /s /c "' + " ".join(parts) + '"')
    return subprocess.run(command, cwd=root, env=env, check=True, text=True,
                          stdout=subprocess.PIPE if capture else None,
                          stderr=subprocess.STDOUT if capture else None)


def prepend_path(env, directory):
    env["PATH"] = str(directory) + os.pathsep + env.get("PATH", "")


def find_node(env):
    candidates = [shutil.which("node", path=env.get("PATH"))]
    if os.name == "nt":
        for variable, relative in (("ProgramFiles", "nodejs/node.exe"),
                                   ("LOCALAPPDATA", "Programs/nodejs/node.exe"),
                                   ("USERPROFILE", ".cache/codex-runtimes/codex-primary-runtime/dependencies/node/bin/node.exe")):
            if env.get(variable):
                candidates.append(str(Path(env[variable]) / relative))
    for candidate in candidates:
        if not candidate or not Path(candidate).is_file():
            continue
        try:
            version = run([candidate, "--version"], env=env, capture=True).stdout.strip()
        except (OSError, subprocess.CalledProcessError):
            continue
        match = re.fullmatch(r"v(\d+)\.(\d+)\.(\d+)", version)
        if match and tuple(map(int, match.groups())) >= MIN_NODE:
            prepend_path(env, Path(candidate).parent)
            fallback = Path(candidate).parent.parent.parent / "bin/fallback"
            if os.name == "nt" and (fallback / "pnpm.cmd").is_file():
                env["PATH"] += os.pathsep + str(fallback)
            print(f"DeepLagoon: Node.js {version}", flush=True)
            return
    raise PreparationError("Node.js 22.13+ is required (Node.js 24 is used in CI). "
                           "Install it from https://nodejs.org/ and restart VS Code.")


def prepare_pnpm(root, env):
    directory = root / ".local/codex/development-environment/pnpm"
    # Prefer a previously installed project-local version to an incompatible global one.
    prepend_path(env, directory / "node_modules/.bin")
    pnpm = shutil.which("pnpm", path=env["PATH"])
    if pnpm:
        try:
            if run([pnpm, "--version"], root=root, env=env, capture=True).stdout.strip() == PNPM_VERSION:
                print(f"DeepLagoon: pnpm {PNPM_VERSION}", flush=True)
                return
        except (OSError, subprocess.CalledProcessError):
            pass
    npm = shutil.which("npm", path=env["PATH"])
    if not npm:
        raise PreparationError("npm was not found. Install Node.js with npm from https://nodejs.org/ "
                               "and restart VS Code.")
    print(f"DeepLagoon: installing pnpm {PNPM_VERSION} into {directory}", flush=True)
    run([npm, "install", "--prefix", str(directory), "--no-audit", "--no-fund",
         "--ignore-scripts", f"pnpm@{PNPM_VERSION}"], root=root, env=env)
    pnpm = shutil.which("pnpm", path=env["PATH"])
    if not pnpm or run([pnpm, "--version"], root=root, env=env, capture=True).stdout.strip() != PNPM_VERSION:
        raise PreparationError(f"Could not prepare pnpm {PNPM_VERSION}. Check the npm installation output above.")


def prepare_engine(root, env):
    git = find_git()
    prepend_path(env, Path(git).parent)
    version = read_contract(root)["official_engine_version"]
    run([git, "submodule", "update", "--init", "--recursive"], root=root, env=env)
    release = f"refs/tags/v{version}"
    probe = subprocess.run([git, "-C", str(root / "RobustToolbox"), "rev-parse", "--verify",
                            "--quiet", release + "^{commit}"], env=env, capture_output=True)
    if probe.returncode:
        print(f"DeepLagoon: fetching official engine reference v{version}", flush=True)
        run([git, "-C", str(root / "RobustToolbox"), "fetch", "--no-tags", OFFICIAL_ENGINE,
             f"{release}:{release}"], root=root, env=env)
    print(f"DeepLagoon: engine reference v{version} ready", flush=True)


@contextmanager
def preparation_lock(root):
    # Compounds and runQuickAll may prepare client and server at the same time.
    path = root / ".local/codex/development-environment/prepare.lock"
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("a+b") as handle:
        if path.stat().st_size == 0:
            handle.write(b"\0")
            handle.flush()
        deadline = time.monotonic() + 300
        while True:
            try:
                handle.seek(0)
                if os.name == "nt":
                    import msvcrt
                    msvcrt.locking(handle.fileno(), msvcrt.LK_NBLCK, 1)
                else:
                    import fcntl
                    fcntl.flock(handle, fcntl.LOCK_EX | fcntl.LOCK_NB)
                break
            except OSError:
                if time.monotonic() >= deadline:
                    raise PreparationError("Another environment preparation is still running. Retry when it finishes.")
                time.sleep(0.2)
        try:
            yield
        finally:
            handle.seek(0)
            if os.name == "nt":
                msvcrt.locking(handle.fileno(), msvcrt.LK_UNLCK, 1)
            else:
                fcntl.flock(handle, fcntl.LOCK_UN)


def prepare(root=ROOT):
    root = root.resolve()
    env = os.environ.copy()
    env["DeepLagoonPython"] = sys.executable
    find_node(env)
    with preparation_lock(root):
        prepare_pnpm(root, env)
        prepare_engine(root, env)
    return env


def main(args=None):
    args = list(sys.argv[1:] if args is None else args)
    if args[:1] == ["--"]:
        args.pop(0)
    try:
        env = prepare()
        if args:
            return run(args, env=env).returncode
        print("DeepLagoon: development environment ready", flush=True)
        return 0
    except subprocess.CalledProcessError as exc:
        print(f"DeepLagoon: command failed (exit {exc.returncode}): {exc.cmd}", file=sys.stderr)
        if exc.stdout:
            print(exc.stdout, file=sys.stderr)
        return exc.returncode or 1
    except (PreparationError, CompatibilityError, OSError) as exc:
        print(f"DeepLagoon: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
