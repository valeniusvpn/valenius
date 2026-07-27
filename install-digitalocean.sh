#!/usr/bin/env bash
# Unattended first-boot installer for the Valenius Community edition on a
# DigitalOcean droplet (cloud-init `user_data` / a Marketplace 1-Click image's
# first-boot provisioner). Not meant to be run interactively from a checkout --
# for that, use ./install.sh instead.
#
# This is a thin wrapper: it gets a checkout of the public repo onto the
# droplet and root's SSH login banner, then delegates the actual build/compose/
# secrets work to install.sh so there's exactly one place that logic lives.
# Idempotent -- safe to re-run (e.g. on reboot, or by hand over SSH) after a
# partial or completed install.
#
# Usage (as root, e.g. DO droplet "User data" field):
#   curl -fsSL https://raw.githubusercontent.com/valeniusvpn/valenius/main/install-digitalocean.sh | bash
#
# Override defaults via env vars set before piping in, e.g.:
#   VALENIUS_REF=v1.11.18 BACKEND_PORT=9001 ADMIN_EMAIL=me@example.com bash install-digitalocean.sh
#
# See documentation/docs/self-hosting/community.md and install.sh for what
# actually happens to the box. Keep this file's defaults (repo URL, install
# dir, port) in sync with those if either changes.

set -euo pipefail

INSTALL_DIR="/opt/valenius"
REPO_URL="${VALENIUS_REPO_URL:-https://github.com/valeniusvpn/valenius.git}"
REF="${VALENIUS_REF:-main}"
BACKEND_PORT="${BACKEND_PORT:-9001}"
ADMIN_EMAIL="${ADMIN_EMAIL:-admin@example.com}"
CREDS_FILE="/root/.valenius_credentials"
LOG_FILE="/var/log/valenius-do-install.log"

exec > >(tee -a "$LOG_FILE") 2>&1

log()  { printf '\033[1;34m==>\033[0m %s\n' "$1"; }
warn() { printf '\033[1;33m!!\033[0m %s\n' "$1" >&2; }
die()  { printf '\033[1;31mERROR:\033[0m %s\n' "$1" >&2; exit 1; }

[ "$(id -u)" = "0" ] || die "Run this as root (it installs packages and writes to /opt and /etc)."

# ── Already installed? Just make sure it's running and reprint the banner ──

if [ -f "$INSTALL_DIR/valenius/.env" ]; then
  log "Existing install found in $INSTALL_DIR/valenius -- leaving secrets untouched, just ensuring the stack is up."
  ( cd "$INSTALL_DIR/valenius" && docker compose up -d )
  log "Done. Credentials are in $CREDS_FILE (if you haven't deleted it) and the SSH login banner."
  exit 0
fi

# ── Prerequisites ────────────────────────────────────────────────────────

log "Installing prerequisites (git, curl)..."
apt-get update -y
apt-get install -y git curl ca-certificates

if ! command -v docker >/dev/null 2>&1 || ! docker compose version >/dev/null 2>&1; then
  log "Installing Docker (official convenience script)..."
  curl -fsSL https://get.docker.com | sh
fi

# ── Get the source ───────────────────────────────────────────────────────

if [ -d "$INSTALL_DIR/.git" ]; then
  log "Existing checkout in $INSTALL_DIR, updating..."
  git -C "$INSTALL_DIR" fetch --depth 1 origin "$REF"
  git -C "$INSTALL_DIR" checkout "$REF"
  git -C "$INSTALL_DIR" reset --hard "origin/$REF" 2>/dev/null || true
else
  log "Cloning $REPO_URL ($REF) into $INSTALL_DIR..."
  git clone --depth 1 --branch "$REF" "$REPO_URL" "$INSTALL_DIR"
fi

chmod +x "$INSTALL_DIR/install.sh" "$INSTALL_DIR/update.sh" 2>/dev/null || true

# ── Run the real installer ──────────────────────────────────────────────

log "Handing off to install.sh..."
( cd "$INSTALL_DIR" && ADMIN_EMAIL="$ADMIN_EMAIL" BACKEND_PORT="$BACKEND_PORT" ./install.sh --yes )

# ── Figure out how to reach this box ────────────────────────────────────

DROPLET_IP="$(curl -fsS --max-time 3 http://169.254.169.254/metadata/v1/interfaces/public/0/ipv4/address 2>/dev/null || true)"
[ -n "$DROPLET_IP" ] || DROPLET_IP="$(curl -fsS --max-time 3 https://ifconfig.me 2>/dev/null || true)"
[ -n "$DROPLET_IP" ] || DROPLET_IP="$(hostname -I 2>/dev/null | awk '{print $1}')"
[ -n "$DROPLET_IP" ] || DROPLET_IP="<this-droplet-ip>"

# ── Firewall ─────────────────────────────────────────────────────────────

if command -v ufw >/dev/null 2>&1; then
  log "Configuring ufw (allow SSH + backend port ${BACKEND_PORT})..."
  ufw allow OpenSSH >/dev/null 2>&1 || true
  ufw allow "${BACKEND_PORT}/tcp" >/dev/null 2>&1 || true
  ufw --force enable >/dev/null 2>&1 || true
fi

# ── Deliver credentials (no TTY on first boot, so: file + SSH login banner) ──

ADMIN_PASSWORD="$(grep -E '^ADMIN_PASSWORD=' "$INSTALL_DIR/valenius/.env" | cut -d= -f2-)"

cat > "$CREDS_FILE" <<EOF
Valenius Community is up on this droplet.

  URL:            http://${DROPLET_IP}:${BACKEND_PORT}/
  Admin email:    ${ADMIN_EMAIL}
  Admin password: ${ADMIN_PASSWORD}

Next steps:
  1. Log in and change the bootstrap password (Admin -> Users), or set up
     OIDC/SSO under Admin -> Settings.
  2. This is plain HTTP on the droplet's public IP -- put a domain + TLS
     reverse proxy in front before real use. See:
     documentation.valenius.com/self-hosting/community (step 6)
  3. Admin -> Customers to create your first customer, then upload a
     WireGuard client .conf under Admin -> [client] -> Details.
  4. Want Pro (automated peer provisioning, MFA, traffic dashboards)?
     cd ${INSTALL_DIR} && ./proupgrade.sh

To update: cd ${INSTALL_DIR} && ./update.sh
Full credentials are saved in ${CREDS_FILE} (root-only). Delete this file
once you've saved them elsewhere to stop it appearing at login:
  rm ${CREDS_FILE}
EOF
chmod 600 "$CREDS_FILE"

cat > /etc/update-motd.d/99-valenius <<'MOTDEOF'
#!/bin/sh
[ -f /root/.valenius_credentials ] && cat /root/.valenius_credentials
MOTDEOF
chmod +x /etc/update-motd.d/99-valenius

log "Done. Credentials written to $CREDS_FILE and will show at the next SSH login."
