#!/usr/bin/env sh
set -eu
development_scripts=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
sh "$development_scripts/prepareDevelopment.sh"
sh "$development_scripts/runQuickServer.sh" "$@" &
sh "$development_scripts/runQuickClient.sh" "$@"
