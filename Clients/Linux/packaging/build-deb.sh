#!/usr/bin/env bash
# Build a .deb package for Valenius Linux client.
# Usage: ./build-deb.sh [version]
set -euo pipefail

VERSION=${1:-1.0.0}
PKG=valenius
INSTALL_DIR=/usr/lib/valenius
BUILD_DIR=$(mktemp -d)

echo "Building ${PKG}_${VERSION}.deb …"

# ── Directory structure ───────────────────────────────────────────────────────
DEST="$BUILD_DIR/${PKG}_${VERSION}"
mkdir -p "$DEST/DEBIAN"
mkdir -p "$DEST${INSTALL_DIR}"
mkdir -p "$DEST/lib/systemd/system"
mkdir -p "$DEST/etc/xdg/autostart"
mkdir -p "$DEST/etc/valenius"
mkdir -p "$DEST/usr/share/applications"
mkdir -p "$DEST/usr/share/pixmaps"
mkdir -p "$DEST/usr/share/icons/hicolor/256x256/apps"
mkdir -p "$DEST/usr/share/metainfo"
mkdir -p "$DEST/etc/modules-load.d"

# ── Python sources ────────────────────────────────────────────────────────────
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(dirname "$SCRIPT_DIR")"

cp -r "$ROOT/shared"  "$DEST${INSTALL_DIR}/"
cp -r "$ROOT/daemon"  "$DEST${INSTALL_DIR}/"
cp -r "$ROOT/tray"    "$DEST${INSTALL_DIR}/"
cp    "$ROOT/appsettings.json.example" "$DEST${INSTALL_DIR}/"
cp    "$SCRIPT_DIR/valenius-tray.desktop" "$DEST${INSTALL_DIR}/"
cp    "$SCRIPT_DIR/valenius-tray.desktop" "$DEST/usr/share/applications/valenius.desktop"
cp    "$SCRIPT_DIR/icons/valenius.png" "$DEST/usr/share/pixmaps/valenius.png"

# Themed icon + AppStream metainfo — the branding graphical software centers
# (GNOME Software, KDE Discover) actually read. /usr/share/pixmaps alone is
# enough for the .desktop launcher but is ignored by the software-center listing.
cp    "$SCRIPT_DIR/icons/valenius.png" "$DEST/usr/share/icons/hicolor/256x256/apps/valenius.png"
cp    "$SCRIPT_DIR/valenius.metainfo.xml" "$DEST/usr/share/metainfo/com.stranto.Valenius.metainfo.xml"

# Ensure the WireGuard kernel module is loaded at boot -- the hardened daemon
# cannot autoload it itself (see wireguard.conf).
cp    "$SCRIPT_DIR/wireguard.conf" "$DEST/etc/modules-load.d/valenius-wireguard.conf"

mkdir -p "$DEST${INSTALL_DIR}/icons"
cp "$SCRIPT_DIR/icons/valenius-connected.png"    "$DEST${INSTALL_DIR}/icons/"
cp "$SCRIPT_DIR/icons/valenius-disconnected.png" "$DEST${INSTALL_DIR}/icons/"
cp "$SCRIPT_DIR/icons/valenius.png"              "$DEST${INSTALL_DIR}/icons/"

# Stamp the real package version into BOTH VERSION constants (each is a
# source-tree default that is never otherwise updated):
#   - tray/app.py    -> the About dialog
#   - daemon/backend.py -> the version reported to the backend in every heartbeat
#     ('Version': VERSION) AND used for the auto-update comparison. Miss this and
#     every client reports the source default (e.g. 1.0.0) regardless of the real
#     package version, and self-update version checks compare against the wrong base.
sed -i "s/^VERSION = '.*'/VERSION = '$VERSION'/" "$DEST${INSTALL_DIR}/tray/app.py"
sed -i "s/^VERSION = '.*'/VERSION = '$VERSION'/" "$DEST${INSTALL_DIR}/daemon/backend.py"

# ── systemd unit ──────────────────────────────────────────────────────────────
cp "$SCRIPT_DIR/valenius.service" "$DEST/lib/systemd/system/"

# ── DEBIAN control files ──────────────────────────────────────────────────────
sed "s/Version: .*/Version: $VERSION/" "$SCRIPT_DIR/debian/control" > "$DEST/DEBIAN/control"
cp "$SCRIPT_DIR/debian/postinst" "$DEST/DEBIAN/postinst"
cp "$SCRIPT_DIR/debian/prerm"    "$DEST/DEBIAN/prerm"
cp "$SCRIPT_DIR/debian/postrm"   "$DEST/DEBIAN/postrm"
chmod 755 "$DEST/DEBIAN/postinst" "$DEST/DEBIAN/prerm" "$DEST/DEBIAN/postrm"

# ── Build ─────────────────────────────────────────────────────────────────────
dpkg-deb --root-owner-group --build "$DEST" "${PKG}_${VERSION}_all.deb"
rm -rf "$BUILD_DIR"

echo "Done: ${PKG}_${VERSION}_all.deb"
