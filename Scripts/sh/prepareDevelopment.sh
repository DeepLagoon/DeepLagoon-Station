#!/usr/bin/env sh
set -eu
development_root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
if ! command -v python3 >/dev/null 2>&1; then
    echo 'DeepLagoon: Python 3.10+ is required. Install Python and restart VS Code.' >&2
    exit 1
fi
if ! python3 -c 'import sys; sys.exit(0 if sys.version_info >= (3, 10) else 1)'; then
    echo 'DeepLagoon: Python 3.10+ is required. Update Python and restart VS Code.' >&2
    exit 1
fi
exec python3 "$development_root/Tools/prepare_development.py" "$@"
