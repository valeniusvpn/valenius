#!/usr/bin/env bash
# Upgrades an existing Valenius Community install (created by install.sh) in place to
# Pro: backs up the database, swaps the backend to the pre-built Pro image, and adds a
# license key -- keeping the same database, volumes, and API key so existing clients
# never notice. See update.sh (repo root) for routine updates afterwards -- it
# auto-detects Community vs Pro and pulls/rebuilds accordingly, so you only need this
# script again to change the license key or force a backup before a Pro update.
# Mirrors documentation/docs/self-hosting/pro.md ("Upgrading from Community") and
# help/docs/install/backend-pro.md -- keep these in sync if you change this script.
#
# Usage:
#   ./proupgrade.sh                              # interactive, prompts for a license key
#   VALENIUS_LICENSE_KEY=xxxx ./proupgrade.sh --yes   # non-interactive
#   ./proupgrade.sh --skip-backup                # advanced: skip the automatic pg_dump
#
# Safe to re-run: if the stack is already on the Pro image, it only updates the license
# key and restarts (also pulling the latest Pro image along the way).

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

DEPLOY_DIR="valenius"
DEFAULT_IMAGE_TAG="ghcr.io/valeniusvpn/valenius-pro:latest"
LICENSE_KEY="${VALENIUS_LICENSE_KEY:-}"
ASSUME_YES=0
SKIP_BACKUP=0

for arg in "$@"; do
  case "$arg" in
    --yes|-y) ASSUME_YES=1 ;;
    --skip-backup) SKIP_BACKUP=1 ;;
    --license=*) LICENSE_KEY="${arg#--license=}" ;;
    --help|-h)
      sed -n '2,16p' "$0"
      exit 0
      ;;
    *)
      echo "Unknown option: $arg" >&2
      exit 1
      ;;
  esac
done

log()  { printf '\033[1;34m==>\033[0m %s\n' "$1"; }
warn() { printf '\033[1;33m!!\033[0m %s\n' "$1" >&2; }
die()  { printf '\033[1;31mERROR:\033[0m %s\n' "$1" >&2; exit 1; }

# ── Preflight ─────────────────────────────────────────────────────────────

[ -f "$DEPLOY_DIR/docker-compose.yml" ] || die "No $DEPLOY_DIR/docker-compose.yml found here -- this script only upgrades a stack created by ./install.sh. For a manually-built or Portainer-based Community install, follow the manual steps in documentation/docs/self-hosting/pro.md instead."
[ -f "$DEPLOY_DIR/.env" ] || die "$DEPLOY_DIR/.env not found -- the deploy folder looks incomplete."

command -v docker >/dev/null 2>&1 || die "Docker is not installed or not on PATH."
docker compose version >/dev/null 2>&1 || die "The 'docker compose' plugin is not available. Install/update Docker."

COMPOSE_FILE="$DEPLOY_DIR/docker-compose.yml"
ALREADY_PRO=0
if grep -q '\${IMAGE_TAG}' "$COMPOSE_FILE"; then
  ALREADY_PRO=1
elif ! grep -q 'image: valenius-backend:latest' "$COMPOSE_FILE"; then
  die "$COMPOSE_FILE doesn't look like the file install.sh generates (expected an 'image: valenius-backend:latest' line). If you edited it by hand, upgrade manually following documentation/docs/self-hosting/pro.md instead."
fi

if [ "$ASSUME_YES" != "1" ]; then
  echo
  echo "This will switch the backend in $DEPLOY_DIR from Community to Pro, in place:"
  echo "  - same database, same volumes, same clients -- nothing is deleted"
  echo "  - the backend container is replaced with the pre-built Pro image"
  echo "  - a database backup is taken first (unless --skip-backup)"
  echo
  read -r -p "Continue? [y/N] " confirm || true
  case "$confirm" in
    y|Y|yes|YES) ;;
    *) die "Aborted." ;;
  esac
fi

# ── License key ───────────────────────────────────────────────────────────

if [ -z "$LICENSE_KEY" ] && [ "$ASSUME_YES" != "1" ]; then
  read -r -p "Pro/trial license key (leave blank to add later in Admin -> Overview): " LICENSE_KEY || true
fi

if [ -z "$LICENSE_KEY" ]; then
  warn "No license key given -- the backend will run as Community until you add one (Admin -> Overview, or re-run this script)."
fi

# ── Back up the database ─────────────────────────────────────────────────

if [ "$SKIP_BACKUP" != "1" ]; then
  mkdir -p "$DEPLOY_DIR/backups"
  BACKUP_FILE="$DEPLOY_DIR/backups/pre-pro-$(date -u +%Y%m%dT%H%M%SZ).sql"
  log "Backing up the database to $BACKUP_FILE..."
  ( cd "$DEPLOY_DIR" && docker compose exec -T db pg_dump -U valenius valenius ) > "$BACKUP_FILE" \
    || die "pg_dump failed -- is the stack running? ('docker compose -f $COMPOSE_FILE ps'). Nothing was changed."
  [ -s "$BACKUP_FILE" ] || die "Backup file is empty -- refusing to continue. Nothing was changed."
else
  warn "Skipping database backup (--skip-backup)."
fi

# ── Rewrite the deploy files ──────────────────────────────────────────────

if [ "$ALREADY_PRO" = "1" ]; then
  log "Stack is already on the Pro image -- updating the license key and pulling the latest image. (For routine updates without a license change, ./update.sh does this too.)"
else
  cp "$COMPOSE_FILE" "$COMPOSE_FILE.pre-pro.bak"
  log "Saved a copy of the old compose file to $COMPOSE_FILE.pre-pro.bak (revert by copying it back and running 'docker compose up -d')."

  sed -i "s|image: valenius-backend:latest|image: \${IMAGE_TAG}|" "$COMPOSE_FILE"
  sed -i "/Valenius__AdminPassword: \${ADMIN_PASSWORD}/a\\      VALENIUS_LICENSE_KEY: \${VALENIUS_LICENSE_KEY:-}" "$COMPOSE_FILE"

  if grep -q '^IMAGE_TAG=' "$DEPLOY_DIR/.env"; then
    sed -i "s|^IMAGE_TAG=.*|IMAGE_TAG=${DEFAULT_IMAGE_TAG}|" "$DEPLOY_DIR/.env"
  else
    printf 'IMAGE_TAG=%s\n' "$DEFAULT_IMAGE_TAG" >> "$DEPLOY_DIR/.env"
  fi
fi

if grep -q '^VALENIUS_LICENSE_KEY=' "$DEPLOY_DIR/.env"; then
  sed -i "s|^VALENIUS_LICENSE_KEY=.*|VALENIUS_LICENSE_KEY=${LICENSE_KEY}|" "$DEPLOY_DIR/.env"
else
  printf 'VALENIUS_LICENSE_KEY=%s\n' "$LICENSE_KEY" >> "$DEPLOY_DIR/.env"
fi

# ── Pull and restart ──────────────────────────────────────────────────────

log "Pulling the Pro image..."
( cd "$DEPLOY_DIR" && docker compose pull backend ) || die "Pull failed -- check internet access on this host. Nothing else was changed; your old compose file is at $COMPOSE_FILE.pre-pro.bak."

log "Restarting the stack..."
( cd "$DEPLOY_DIR" && docker compose up -d )

log "Waiting for the backend to come back up..."
READY=0
for _ in $(seq 1 30); do
  if ( cd "$DEPLOY_DIR" && docker compose logs backend 2>/dev/null ) | grep -q "Now listening on:"; then
    READY=1
    break
  fi
  sleep 2
done

if [ "$READY" != "1" ]; then
  warn "Backend didn't report ready within 60s. Check logs with:"
  warn "  (cd $DEPLOY_DIR && docker compose logs -f backend)"
  warn "Your database backup (if taken) is at $DEPLOY_DIR/backups/ and the old compose file at $COMPOSE_FILE.pre-pro.bak -- nothing was lost."
  exit 1
fi

cat <<EOF

Valenius Pro is up, running on the same database as your Community install.

  Next steps:
    1. Log in and check Admin -> Overview -- the License card shows Valid once
       your key validates (or add one there if you skipped it above).
    2. Existing customers/clients keep working exactly as before (manual .conf
       uploads still work) -- nothing changes for them automatically.
    3. Optional: set up the integrated WireGuard server for automated peer
       provisioning -- see documentation.valenius.com/self-hosting/pro
       (Part B) or help.valenius.com/install/integrated-server.

  To revert to Community: copy $COMPOSE_FILE.pre-pro.bak back over
  $COMPOSE_FILE, then 'docker compose up -d' in $DEPLOY_DIR.

  To update:  ./update.sh   (auto-detects Pro and pulls the latest published image)
EOF
