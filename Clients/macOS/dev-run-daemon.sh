#!/usr/bin/env bash
# Dev helper: build + (re)launch the daemon as root for local iteration.
# NOT the release path — that's packaging/build-pkg.sh (notarized .pkg), added in M7.
#
# Requires appsettings.json at /Library/Application Support/Valenius/appsettings.json
# (BackendUrl + ApiKey). See appsettings.json.example.
#
# Usage:
#   ./dev-run-daemon.sh            # build, then run in the foreground (Ctrl-C to stop)
#   ./dev-run-daemon.sh --stop     # just stop a running daemon
set -euo pipefail

cd "$(dirname "$0")/ValeniusDaemon"
BIN=".build/debug/ValeniusDaemon"

sudo /usr/bin/killall ValeniusDaemon 2>/dev/null || true
if [[ "${1:-}" == "--stop" ]]; then
    echo "Stopped."
    exit 0
fi

echo "Building…"
swift build

echo "Launching daemon as root (Ctrl-C to stop)…"
echo "Logs: log stream --predicate 'subsystem == \"com.stranto.valenius\"' --level debug"
exec sudo "$BIN"
