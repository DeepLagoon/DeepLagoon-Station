"""Run every integration test in bounded processes, preserving failures and reports."""
import argparse
import os
from pathlib import Path
import subprocess

BASE_FILTER = "FullyQualifiedName!~ShipyardTest"
GROUPS = {
    "prototype-client": ("ClientPrototypeSaveLoadSaveTest", "TestAllClientPrototypesAreSerializable"),
    "prototype-server": ("ServerPrototypeSaveLoadSaveTest", "TestAllServerPrototypesAreSerializable"),
    "prototype-spawn": ("PrototypeSaveTest",),
    "maps": ("PostMapInitTest",),
    "entities": ("Tests.EntityTest.",),
    "fork-content": ("Tests._",),
    "interactions": ("Tests.Interaction.", "Tests.Construction.", "Tests.Movement.", "Tests.Chemistry."),
}


def filters():
    result = {name: BASE_FILTER + "&(" + "|".join("FullyQualifiedName~" + term for term in terms) + ")"
              for name, terms in GROUPS.items()}
    result["remaining"] = BASE_FILTER + "".join("&FullyQualifiedName!~" + term
                                                for terms in GROUPS.values() for term in terms)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--target", default="Content.IntegrationTests/Content.IntegrationTests.csproj")
    parser.add_argument("--configuration", default="DebugOpt")
    parser.add_argument("--results", default="test_results")
    parser.add_argument("--group", choices=filters())
    args = parser.parse_args()
    env = dict(os.environ, DOTNET_gcServer="0")
    failed = []
    for name, test_filter in filters().items():
        if args.group and args.group != name:
            continue
        output = (Path(args.results) / name).resolve()
        output.mkdir(parents=True, exist_ok=True)
        print(f"::group::Integration tests: {name}", flush=True)
        command = ["dotnet", "test", args.target, "--no-build", "--configuration", args.configuration,
                   "--filter", test_filter, "--logger", "trx;LogFileName=results.trx",
                   "--results-directory", str(output), "--", "NUnit.NumberOfTestWorkers=1",
                   "NUnit.ConsoleOut=0", "NUnit.MapWarningTo=Failed", "NUnit.TestOutputXml=logs",
                   f"NUnit.WorkDirectory={output}"]
        if subprocess.run(command, env=env, check=False).returncode:
            failed.append(name)
        print("::endgroup::", flush=True)
    if failed:
        print("Failed integration groups: " + ", ".join(failed), flush=True)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
