#!/usr/bin/env bash
# Build (and optionally sign + notarize) the Valenius macOS installer pkg.
#
#   ./build-pkg.sh <version> [options]
#
# Options:
#   --sign-app "Developer ID Application: NAME (TEAMID)"     codesign the binaries + .app
#   --sign-installer "Developer ID Installer: NAME (TEAMID)" sign the product pkg
#   --notary-profile <name>   notarytool keychain profile (implies notarize + staple)
#   --skip-notarize           build signed but don't notarize (fast local RC iteration)
#
# With no signing options it produces an UNSIGNED pkg for local dev testing (installer CLI as
# root installs it fine; Gatekeeper would block a double-click). Real fleet releases MUST be
# signed + notarized or macOS blocks them.
#
# Output: packaging/output/Valenius-<version>.pkg  (upload on Admin → Releases → macOS).
#
# The auto-update path is load-bearing: after building a new pkg, verify a real
# unattended self-update (old installed → this published → the running daemon updates itself),
# not just that a double-click install works.
set -euo pipefail

VERSION="${1:-}"
if [[ -z "$VERSION" ]]; then echo "usage: $0 <version> [options]" >&2; exit 1; fi
shift

SIGN_APP=""; SIGN_INSTALLER=""; NOTARY_PROFILE=""; SKIP_NOTARIZE=0
while [[ $# -gt 0 ]]; do
    case "$1" in
        --sign-app) SIGN_APP="$2"; shift 2 ;;
        --sign-installer) SIGN_INSTALLER="$2"; shift 2 ;;
        --notary-profile) NOTARY_PROFILE="$2"; shift 2 ;;
        --skip-notarize) SKIP_NOTARIZE=1; shift ;;
        *) echo "unknown option: $1" >&2; exit 1 ;;
    esac
done

here="$(cd "$(dirname "$0")" && pwd)"
root="$(cd "$here/.." && pwd)"          # Clients/macOS
# Build in a fresh temp dir, NOT under the package tree: a mispackaged/relocated install can
# leave root-owned files in the build dir that then block `rm -rf` on the next run. mktemp
# sidesteps that entirely and is cleaned on exit.
build="$(mktemp -d "${TMPDIR:-/tmp}/valenius-pkg.XXXXXX")"
payload="$build/payload"
out="$here/output"
mkdir -p "$payload" "$out"

BUNDLE_ID_PKG="com.stranto.valenius.pkg"
SUPPORT="Library/Application Support/Valenius"

# ── 1. Stamp the version into the Swift source constants (restore after build) ──────────────
# Never hardcode a release number in source; the build stamps it. We edit in
# place and restore on exit so the working tree stays clean.
DAEMON_MAIN="$root/ValeniusDaemon/Sources/ValeniusDaemon/BackendClient.swift"
APP_DELEGATE="$root/ValeniusApp/Sources/ValeniusApp/AppDelegate.swift"
cp "$DAEMON_MAIN" "$build/BackendClient.swift.bak"
cp "$APP_DELEGATE" "$build/AppDelegate.swift.bak"
restore() { cp "$build/BackendClient.swift.bak" "$DAEMON_MAIN"; cp "$build/AppDelegate.swift.bak" "$APP_DELEGATE"; }
trap 'restore; rm -rf "$build"' EXIT
/usr/bin/sed -i '' -E "s/let daemonVersion = \"[^\"]*\"/let daemonVersion = \"$VERSION\"/" "$DAEMON_MAIN"
/usr/bin/sed -i '' -E "s/let appVersion = \"[^\"]*\"/let appVersion = \"$VERSION\"/" "$APP_DELEGATE"

# ── 2. Universal release builds ─────────────────────────────────────────────────────────────
echo "Building daemon (universal)…"
( cd "$root/ValeniusDaemon" && swift build -c release --arch arm64 --arch x86_64 )
echo "Building app (universal)…"
( cd "$root/ValeniusApp" && swift build -c release --arch arm64 --arch x86_64 )
echo "Building wireguard-go (universal)…"
bash "$here/build-wireguard-go.sh"

DAEMON_BIN="$root/ValeniusDaemon/.build/apple/Products/Release/ValeniusDaemon"
APP_BIN="$root/ValeniusApp/.build/apple/Products/Release/ValeniusApp"
WG_BIN="$here/bin/wireguard-go"

# ── 3. Assemble the payload ─────────────────────────────────────────────────────────────────
echo "Assembling payload…"
APP="$payload/Applications/Valenius.app"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
/usr/bin/sed "s/__VERSION__/$VERSION/g" "$here/Info.plist.template" > "$APP/Contents/Info.plist"
cp "$APP_BIN" "$APP/Contents/MacOS/ValeniusApp"
chmod 755 "$APP/Contents/MacOS/ValeniusApp"

# Menu bar icons + the full logo for the About window / popup header (loaded via Bundle.main).
cp "$here/assets/MenuBarConnected.png" "$here/assets/MenuBarDisconnected.png" "$APP/Contents/Resources/"
cp "$here/assets/AppIcon-source.png" "$APP/Contents/Resources/Logo.png"

# Finder/app icon: build an .icns from the branded shield (square it up first — iconutil
# requires square inputs; the source is ~1:1 so the pad is negligible).
iconset="$build/AppIcon.iconset"; mkdir -p "$iconset"
sq="$build/appicon-square.png"
sips -s format png -z 1024 1024 "$here/assets/AppIcon-source.png" --out "$sq" >/dev/null 2>&1 || cp "$here/assets/AppIcon-source.png" "$sq"
for sz in 16 32 128 256 512; do
    sips -z $sz $sz "$sq" --out "$iconset/icon_${sz}x${sz}.png" >/dev/null 2>&1
    sips -z $((sz*2)) $((sz*2)) "$sq" --out "$iconset/icon_${sz}x${sz}@2x.png" >/dev/null 2>&1
done
if iconutil -c icns "$iconset" -o "$APP/Contents/Resources/AppIcon.icns" 2>/dev/null; then
    /usr/libexec/PlistBuddy -c "Add :CFBundleIconFile string AppIcon" "$APP/Contents/Info.plist" 2>/dev/null || true
fi

mkdir -p "$payload/$SUPPORT/bin"
cp "$DAEMON_BIN" "$payload/$SUPPORT/bin/valeniusd"; chmod 755 "$payload/$SUPPORT/bin/valeniusd"
cp "$WG_BIN" "$payload/$SUPPORT/bin/wireguard-go"; chmod 755 "$payload/$SUPPORT/bin/wireguard-go"
cp "$root/ValeniusDaemon/appsettings.json.example" "$payload/$SUPPORT/appsettings.json.example"

mkdir -p "$payload/Library/LaunchDaemons" "$payload/Library/LaunchAgents"
cp "$here/com.stranto.valenius.daemon.plist" "$payload/Library/LaunchDaemons/"
cp "$here/com.stranto.valenius.app.plist" "$payload/Library/LaunchAgents/"

# ── 4. Sign (Developer ID Application, hardened runtime) ────────────────────────────────────
if [[ -n "$SIGN_APP" ]]; then
    echo "Signing binaries…"
    for bin in "$payload/$SUPPORT/bin/valeniusd" "$payload/$SUPPORT/bin/wireguard-go"; do
        codesign --force --options runtime --timestamp --sign "$SIGN_APP" "$bin"
    done
    codesign --force --options runtime --timestamp --deep --sign "$SIGN_APP" "$APP"
    codesign --verify --strict "$APP"
else
    echo "WARNING: no --sign-app — building an UNSIGNED pkg (local dev only)."
fi

# ── 5. Build the component + product pkg ────────────────────────────────────────────────────
echo "Building pkg…"
# Strip extended attributes so pkgbuild doesn't emit `._name` AppleDouble companion files into
# the payload. Safe after signing: Mach-O + .app signatures live in the binary / _CodeSignature,
# not in xattrs. (com.apple.provenance is system-protected and survives — harmless.)
/usr/bin/xattr -cr "$payload"
chmod 755 "$here/scripts/preinstall" "$here/scripts/postinstall"

# Disable bundle relocation. pkgbuild defaults app bundles to RELOCATABLE, so PackageKit
# "finds" an existing bundle by CFBundleIdentifier (e.g. this very build dir, or a stale copy)
# and installs THERE instead of the payload's /Applications — the app then silently doesn't
# land in /Applications. Force every component to install at its payload path.
pkgbuild --analyze --root "$payload" "$build/component.plist"
/usr/bin/python3 - "$build/component.plist" <<'PY'
import plistlib, sys
path = sys.argv[1]
with open(path, "rb") as f: comps = plistlib.load(f)
for c in comps:
    if "BundleIsRelocatable" in c: c["BundleIsRelocatable"] = False
with open(path, "wb") as f: plistlib.dump(comps, f)
PY
pkgbuild --root "$payload" --component-plist "$build/component.plist" --scripts "$here/scripts" \
    --identifier "$BUNDLE_ID_PKG" --version "$VERSION" \
    --install-location / "$build/component.pkg"

PKG_OUT="$out/Valenius-$VERSION.pkg"
if [[ -n "$SIGN_INSTALLER" ]]; then
    productbuild --package "$build/component.pkg" --sign "$SIGN_INSTALLER" "$PKG_OUT"
else
    productbuild --package "$build/component.pkg" "$PKG_OUT"
fi

# ── 6. Notarize + staple ────────────────────────────────────────────────────────────────────
if [[ -n "$NOTARY_PROFILE" && "$SKIP_NOTARIZE" -eq 0 ]]; then
    echo "Notarizing (this can take a few minutes)…"
    xcrun notarytool submit "$PKG_OUT" --keychain-profile "$NOTARY_PROFILE" --wait
    xcrun stapler staple "$PKG_OUT"
    echo "Notarized + stapled."
elif [[ -n "$SIGN_INSTALLER" ]]; then
    echo "NOTE: signed but not notarized (--skip-notarize or no --notary-profile)."
fi

SHA=$(/usr/bin/shasum -a 256 "$PKG_OUT" | awk '{print $1}')
echo ""
echo "Built: $PKG_OUT"
echo "SHA-256: $SHA"
echo "Upload on Admin → Releases → macOS → Stable/Beta (the upload computes its own SHA)."
