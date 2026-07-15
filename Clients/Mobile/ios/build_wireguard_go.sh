#!/bin/bash
# Build WireGuard's wireguard-go bridge (libwg-go.a) for the Packet Tunnel extension.
#
# SwiftPM does NOT build WireGuardKitGo's Go library: the SPM target only compiles a
# stub dummy.c and carries the `-lwg-go` linker flag, expecting libwg-go.a to be built
# separately -- exactly as the official WireGuard iOS app does via a build phase. This
# script runs the upstream Makefile, which emits libwg-go.a into $CONFIGURATION_BUILD_DIR
# (== BUILT_PRODUCTS_DIR, on the linker search path). Requires the Go toolchain on PATH.
#
# Invoked as a Run Script build phase on the ValeniusTunnel target (before its link phase).
set -eo pipefail

if [ "${ACTION:-}" = "indexbuild" ]; then exit 0; fi

# Go (and the tools the Makefile needs) may not be on Xcode's minimal PATH.
export PATH="/opt/homebrew/bin:/usr/local/bin:${PATH}"

# wireguard-apple is vendored as a local Swift package (ios/wireguard-apple), so
# SwiftPM builds it in place -- the sources live at a fixed in-repo path. Fall back
# to the old SPM-checkout locations just in case.
GODIR=""
for cand in \
  "${SRCROOT}/wireguard-apple/Sources/WireGuardKitGo" \
  "${BUILD_DIR%/Build/*}/SourcePackages/checkouts/wireguard-apple/Sources/WireGuardKitGo" \
  "${SRCROOT}/../build/ios/SourcePackages/checkouts/wireguard-apple/Sources/WireGuardKitGo"; do
  if [ -d "${cand}" ]; then
    GODIR="${cand}"
    break
  fi
done
if [ -z "${GODIR}" ]; then
  GODIR="$(find "${SRCROOT}/wireguard-apple" "${BUILD_DIR%/Build/*}" -type d -name WireGuardKitGo -path '*wireguard-apple*' 2>/dev/null | head -1)"
fi
if [ -z "${GODIR}" ] || [ ! -d "${GODIR}" ]; then
  echo "error: could not locate wireguard-apple/Sources/WireGuardKitGo checkout" >&2
  exit 1
fi

echo "note: building wireguard-go bridge in ${GODIR}"
cd "${GODIR}"

# The pinned wireguard-go (and its golang.org/x/* deps) predate Go's strict
# //go:linkname enforcement, so a modern Go toolchain fails to link with
# "invalid reference to syscall.recvmsg". Go 1.23+ ships the compatibility escape
# hatch -checklinkname=0; inject it into the Makefile's go-build ldflags. Idempotent.
if ! grep -q 'checklinkname' Makefile; then
  chmod u+w Makefile 2>/dev/null || true
  sed -i '' 's/go build -ldflags=-w /go build -ldflags="-w -checklinkname=0" /' Makefile
fi

# The Makefile reads ARCHS / SDKROOT / PLATFORM_NAME / CONFIGURATION_BUILD_DIR /
# CONFIGURATION_TEMP_DIR from Xcode's build environment and writes libwg-go.a to
# $CONFIGURATION_BUILD_DIR.
make
