#!/usr/bin/env bash
# Quick install script (no package manager required).
# Requires root.  Run from the project root directory.
set -euo pipefail

if [ "$(id -u)" -ne 0 ]; then
    echo "Run as root: sudo ./install.sh" >&2
    exit 1
fi

INSTALL_DIR=/usr/lib/valenius
CONF_DIR=/etc/valenius

echo "Installing Valenius to $INSTALL_DIR …"

# ── Copy sources ──────────────────────────────────────────────────────────────
mkdir -p "$INSTALL_DIR"
cp -r shared daemon tray appsettings.json.example "$INSTALL_DIR/"
cp packaging/valenius-tray.desktop "$INSTALL_DIR/"
mkdir -p "$INSTALL_DIR/icons"
cp packaging/icons/valenius-connected.png "$INSTALL_DIR/icons/"
cp packaging/icons/valenius-disconnected.png "$INSTALL_DIR/icons/"
cp packaging/icons/valenius.png "$INSTALL_DIR/icons/"

# ── Create venv with system-site-packages (picks up python3-gi etc.) ─────────
python3 -m venv --system-site-packages "$INSTALL_DIR/venv"

# ── Config ────────────────────────────────────────────────────────────────────
mkdir -p "$CONF_DIR"
if [ ! -f "$CONF_DIR/appsettings.json" ]; then
    cp "$INSTALL_DIR/appsettings.json.example" "$CONF_DIR/appsettings.json"
    chmod 640 "$CONF_DIR/appsettings.json"
    echo ""
    echo "  *** Edit $CONF_DIR/appsettings.json with your BackendUrl and ApiKey ***"
    echo ""
fi

# ── Data directory ────────────────────────────────────────────────────────────
mkdir -p /var/lib/valenius
chmod 700 /var/lib/valenius

# ── systemd ───────────────────────────────────────────────────────────────────
cp packaging/valenius.service /lib/systemd/system/
systemctl daemon-reload
systemctl enable valenius.service
systemctl start valenius.service

# ── XDG autostart (tray for all users) ───────────────────────────────────────
cp packaging/valenius-tray.desktop /etc/xdg/autostart/

# ── App launcher entry (so the tray can be found/relaunched from the menu) ──
cp packaging/valenius-tray.desktop /usr/share/applications/valenius.desktop
cp packaging/icons/valenius.png /usr/share/pixmaps/valenius.png

echo ""
echo "Installation complete."
echo "  Daemon status:  systemctl status valenius"
echo "  Logs:           journalctl -u valenius -f"
echo "  Start tray now: /usr/lib/valenius/venv/bin/python3 -m tray"
