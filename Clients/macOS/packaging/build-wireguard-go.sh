#!/usr/bin/env bash
# Build wireguard-go as a universal (arm64 + x86_64) binary for bundling in the pkg.
# The daemon drives it via UAPI (see ValeniusDaemon/TunnelEngine.swift); it is NOT wg-quick.
#
# Output: packaging/bin/wireguard-go  (git-ignored — a build artifact, rebuilt from source).
# The pkg (M7) installs it to /Library/Application Support/Valenius/bin/wireguard-go (0755).
#
# wireguard-go is MIT-licensed (safe to bundle). We deliberately do NOT bundle wg-quick/bash
# (GPL + a root-shell attack surface) — see docs/macos-client-concept.md.
set -euo pipefail

# Pin to the wireguard-go commit this client was developed against; override with WG_GO_REF.
WG_GO_REF="${WG_GO_REF:-ecfc5a8d54462e18e13c72173e2623d16d8e25a0}"
WG_GO_REPO="https://git.zx2c4.com/wireguard-go"

here="$(cd "$(dirname "$0")" && pwd)"
outdir="$here/bin"
mkdir -p "$outdir"

if ! command -v go >/dev/null 2>&1; then
    echo "error: Go toolchain not found (brew install go)." >&2
    exit 1
fi

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

echo "Cloning wireguard-go @ $WG_GO_REF…"
git clone "$WG_GO_REPO" "$work/src" >/dev/null 2>&1
git -C "$work/src" checkout -q "$WG_GO_REF"

echo "Building arm64…"
( cd "$work/src" && GOOS=darwin GOARCH=arm64 go build -trimpath -o "$work/wg-arm64" . )
echo "Building x86_64…"
( cd "$work/src" && GOOS=darwin GOARCH=amd64 go build -trimpath -o "$work/wg-amd64" . )

echo "Fusing universal binary with lipo…"
lipo -create -output "$outdir/wireguard-go" "$work/wg-arm64" "$work/wg-amd64"
chmod 0755 "$outdir/wireguard-go"

echo "Done: $outdir/wireguard-go"
lipo -info "$outdir/wireguard-go"
