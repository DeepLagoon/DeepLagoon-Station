#!/usr/bin/env sh
set -eu
exec sh "$(dirname -- "$0")/prepareDevelopment.sh" dotnet build -c Release "$@"
