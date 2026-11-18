#!/usr/bin/env sh
set -eu
development_scripts=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
mkdir -p "$development_scripts/../logs"
if sh "$development_scripts/prepareDevelopment.sh" dotnet test Content.Tests/Content.Tests.csproj -c DebugOpt -- NUnit.ConsoleOut=0 > "$development_scripts/../logs/Content.Tests.log" 2>&1; then
    echo "Tests complete."
else
    development_exit_code=$?
    cat "$development_scripts/../logs/Content.Tests.log"
    exit "$development_exit_code"
fi
