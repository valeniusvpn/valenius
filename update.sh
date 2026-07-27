#!/usr/bin/env bash
# Updates a Valenius install created by install.sh, whichever edition it's running:
# pulls the latest source, then either rebuilds the Community image locally or pulls
# the latest published Pro image, and restarts the stack -- in the right order, from
# the right directory, every time. Edition is auto-detected from the ${IMAGE_TAG}
# marker proupgrade.sh writes into valenius/docker-compose.yml when it converts a
# stack to Pro. Mirrors documentation/docs/self-hosting/community.md ("Upgrading") and
# documentation/docs/self-hosting/pro.md ("Upgrading from Community") -- keep these in
# sync if you change this script.
#
# Usage:
#   ./update.sh

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

DEPLOY_DIR="valenius"
IMAGE_TAG="valenius-backend:latest"

log()  { printf '\033[1;34m==>\033[0m %s\n' "$1"; }
warn() { printf '\033[1;33m!!\033[0m %s\n' "$1" >&2; }
die()  { printf '\033[1;31mERROR:\033[0m %s\n' "$1" >&2; exit 1; }

# ── Preflight ─────────────────────────────────────────────────────────────

[ -f "Backend/Dockerfile" ] || die "Run this from the repo root (Backend/Dockerfile not found here)."
[ -f "$DEPLOY_DIR/docker-compose.yml" ] || die "No $DEPLOY_DIR/docker-compose.yml found here -- run ./install.sh first to set up the stack."

# proupgrade.sh switches the backend service's image line to ${IMAGE_TAG} (the
# pre-built Pro image) when it converts a stack to Pro -- detect that so we rebuild
# the OSS image locally on a Community stack, or pull the published image on a Pro
# one, instead of silently doing the wrong thing (rebuilding OSS would be a no-op
# against a Pro stack: it'd report success without ever updating the running
# container).
IS_PRO=0
if grep -q '\${IMAGE_TAG}' "$DEPLOY_DIR/docker-compose.yml"; then
  IS_PRO=1
fi

command -v docker >/dev/null 2>&1 || die "Docker is not installed or not on PATH."
docker compose version >/dev/null 2>&1 || die "The 'docker compose' plugin is not available. Install/update Docker."
git rev-parse --git-dir >/dev/null 2>&1 || die "This isn't a git checkout, so there's no source to pull. Re-clone from https://github.com/valeniusvpn/valenius and copy your $DEPLOY_DIR/.env over."

# Same fix install.sh applies: chmod +x on these scripts is a tracked executable-bit
# change, which otherwise collides with the "git pull" below ("your local changes to
# install.sh/update.sh would be overwritten"). Harmless to set again if already set.
git config core.fileMode false

# ── Pull the latest source ───────────────────────────────────────────────

log "Pulling the latest source..."
git pull --ff-only || die "git pull failed -- you likely have local changes. Run 'git status' to see what changed, then 'git stash' (to keep them) or 'git checkout -- <file>' (to discard them) before retrying."

# ── Update the backend image ─────────────────────────────────────────────

if [ "$IS_PRO" = "1" ]; then
  log "Pro stack detected -- pulling the latest published Pro image..."
  ( cd "$DEPLOY_DIR" && docker compose pull backend ) || die "Pull failed -- check internet access on this host."
else
  log "Rebuilding $IMAGE_TAG from Backend/Dockerfile (this can take a few minutes)..."
  docker build -f Backend/Dockerfile -t "$IMAGE_TAG" .
fi

# ── Restart the stack ─────────────────────────────────────────────────────

log "Restarting the stack in $DEPLOY_DIR..."
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
  exit 1
fi

if [ "$IS_PRO" = "1" ]; then
  log "Update complete -- the backend is running the newly pulled Pro image."
else
  log "Update complete -- the backend is running the newly built image."
fi
