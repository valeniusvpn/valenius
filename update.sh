#!/usr/bin/env bash
# Updates a Valenius Community edition install created by install.sh: pulls the latest
# source, rebuilds the backend image, and restarts the stack -- in the right order,
# from the right directory, every time. Mirrors documentation/docs/self-hosting/community.md
# ("Upgrading") -- keep the two in sync if you change this script.
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

# ── Rebuild the backend image ────────────────────────────────────────────

log "Rebuilding $IMAGE_TAG from Backend/Dockerfile (this can take a few minutes)..."
docker build -f Backend/Dockerfile -t "$IMAGE_TAG" .

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

log "Update complete -- the backend is running the newly built image."
