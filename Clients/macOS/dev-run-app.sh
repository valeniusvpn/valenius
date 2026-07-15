#!/usr/bin/env bash
# Dev helper: build + run the menu bar app in the foreground for local iteration.
# NOT the release path — the pkg (M7) bundles this into /Applications/Valenius.app.
#
# The daemon must be running (./dev-run-daemon.sh) for the popup to show live status.
# Logs stream to this terminal (stderr) and the unified log (subsystem com.stranto.valenius).
#
# Usage:
#   ./dev-run-app.sh          # build + run (Ctrl-C to stop)
#   ./dev-run-app.sh --stop   # stop a running dev instance
set -euo pipefail

cd "$(dirname "$0")/ValeniusApp"

pkill -f "ValeniusApp" 2>/dev/null || true
if [[ "${1:-}" == "--stop" ]]; then
    echo "Stopped."
    exit 0
fi

echo "Building…"
swift build

echo "Launching menu bar app (look for the shield icon in the menu bar; Ctrl-C to stop)…"
exec .build/debug/ValeniusApp
