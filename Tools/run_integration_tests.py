"""Run all integration tests in bounded processes with disjoint CI shards."""
import argparse
import hashlib
import os
from pathlib import Path
import subprocess

# StationPowerTests is an existing [Explicit] manual fixture. Never create an
# all-explicit batch: adapter 5.2 can rebuild its empty selection as run-all.
MANUAL_FIXTURE = "Tests.Power.StationPowerTests."
BASE_FILTER = "FullyQualifiedName!~ShipyardTest"
BASE_FILTER += "&FullyQualifiedName!~" + MANUAL_FIXTURE
GROUPS = {
    "prototype-client": ("ClientPrototypeSaveLoadSaveTest", "TestAllClientPrototypesAreSerializable"),
    "prototype-server": ("ServerPrototypeSaveLoadSaveTest", "TestAllServerPrototypesAreSerializable"),
    "prototype-spawn": ("PrototypeSaveTest",),
    "maps": ("PostMapInitTest",),
    "entities": ("Tests.EntityTest.",),
    "fork-content": ("Tests._",),
    "interactions": ("Tests.Interaction.", "Tests.Construction.", "Tests.Movement.", "Tests.Chemistry."),
}
FIXTURE_GROUPS = {"fork-content", "interactions", "remaining"}


def filters():
    result = {name: BASE_FILTER + "&(" + "|".join("FullyQualifiedName~" + term for term in terms) + ")"
              for name, terms in GROUPS.items()}
    result["remaining"] = BASE_FILTER + "".join("&FullyQualifiedName!~" + term
                                                for terms in GROUPS.values() for term in terms)
    return result


def fixture_prefix(full_name):
    """Find the fixture separator without treating dots inside arguments as separators."""
    depth = 0
    separator = -1
    quote = None
    escaped = False
    for index, char in enumerate(full_name):
        if quote:
            if escaped:
                escaped = False
            elif char == "\\":
                escaped = True
            elif char == quote:
                quote = None
            continue
        if char in "\"'":
            quote = char
        elif char in "([<":
            depth += 1
        elif char in ")]>":
            depth -= 1
        elif char == "." and depth == 0:
            separator = index
    fixture = full_name[:separator]
    arguments = [fixture.index(delimiter) for delimiter in "([<" if delimiter in fixture]
    if arguments:
        return fixture[:min(arguments)]
    return fixture + "."


def group_for(full_name):
    for group, terms in GROUPS.items():
        if any(term in full_name for term in terms):
            return group
    return "remaining"


def discover_fixtures(output):
    names = [line.strip() for line in output.splitlines()
             if line.strip().startswith("Content.IntegrationTests.") and "ShipyardTest" not in line
             and MANUAL_FIXTURE not in line]
    if not names:
        raise RuntimeError("Integration test discovery returned no fully qualified test names")
    fixtures = {group: {} for group in FIXTURE_GROUPS}
    for full_name in names:
        group = group_for(full_name)
        if group not in FIXTURE_GROUPS:
            continue
        # Fork features intentionally create fresh client/server worlds. Bound their
        # lifetime to one method while keeping its parameterized cases together.
        prefix = full_name.split("(", 1)[0] if group == "fork-content" else fixture_prefix(full_name)
        fixtures[group].setdefault(prefix, {prefix})
    # Generic or parameterized fixtures can share a prefix. Merge overlapping selections
    # so each discovered case still runs in exactly one process.
    for full_name in names:
        group = group_for(full_name)
        if group not in FIXTURE_GROUPS:
            continue
        matches = [key for key, prefixes in fixtures[group].items()
                   if any(prefix in full_name for prefix in prefixes)]
        if len(matches) > 1:
            for key in matches[1:]:
                fixtures[group][matches[0]].update(fixtures[group].pop(key))
    return fixtures


def test_batches(args, env):
    selected = {name: expression for name, expression in filters().items()
                if not args.group or args.group == name}
    fixtures = {}
    if FIXTURE_GROUPS.intersection(selected):
        discovery = subprocess.run(
            ["dotnet", "test", args.target, "--no-build", "--configuration", args.configuration,
             "--list-tests", "--", "NUnit.DisplayName=FullName"],
            env=env, capture_output=True, text=True, encoding="utf-8-sig", errors="replace", check=True)
        fixtures = discover_fixtures(discovery.stdout)
    for group, expression in selected.items():
        if group not in FIXTURE_GROUPS:
            yield group, expression, group
            continue
        for fixture, prefixes in sorted(fixtures[group].items()):
            identifier = hashlib.sha256(fixture.encode()).hexdigest()[:12]
            selection = "|".join("FullyQualifiedName~" + prefix for prefix in sorted(prefixes))
            yield f"{group}-{identifier}", expression + "&(" + selection + ")", fixture


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--target", default="Content.IntegrationTests/Content.IntegrationTests.csproj")
    parser.add_argument("--configuration", default="DebugOpt")
    parser.add_argument("--results", default="test_results")
    parser.add_argument("--group", choices=filters())
    parser.add_argument("--shard-index", type=int, default=0)
    parser.add_argument("--shard-count", type=int, default=1)
    args = parser.parse_args()
    if args.shard_count < 1 or not 0 <= args.shard_index < args.shard_count:
        parser.error("Shard index must be in [0, shard count)")
    # Leave room for native textures, JIT code and the test runner on small workers.
    env = dict(os.environ, DOTNET_gcServer="0", DOTNET_GCHeapHardLimit="0x200000000")
    failed = []
    for index, (name, test_filter, label) in enumerate(test_batches(args, env)):
        if index % args.shard_count != args.shard_index:
            continue
        output = (Path(args.results) / name).resolve()
        output.mkdir(parents=True, exist_ok=True)
        print(f"::group::Integration tests: {label}", flush=True)
        command = ["dotnet", "test", args.target, "--no-build", "--configuration", args.configuration,
                   "--filter", test_filter, "--logger", "trx;LogFileName=results.trx",
                   "--results-directory", str(output), "--", "NUnit.NumberOfTestWorkers=1",
                   "NUnit.ConsoleOut=0", "NUnit.MapWarningTo=Failed", "NUnit.ExplicitMode=None", "NUnit.TestOutputXml=logs",
                   f"NUnit.WorkDirectory={output}"]
        if subprocess.run(command, env=env, check=False).returncode:
            failed.append(label)
        print("::endgroup::", flush=True)
    if failed:
        print("Failed integration groups: " + ", ".join(failed), flush=True)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
