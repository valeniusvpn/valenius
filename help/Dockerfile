# syntax=docker/dockerfile:1
#
# Two-stage build for help.valenius.com:
#   1. Build the static MkDocs site with mkdocs-material.
#   2. Serve the generated HTML with a small nginx image.
#
# Build from the help/ folder (this directory is the context):
#   docker build -t valenius-help:latest .

# ── Stage 1: build the static site ────────────────────────────────────────────
FROM python:3.12-slim AS build

WORKDIR /help

# Pin the major version so an upstream MkDocs 2.0 (which drops the plugin
# system) cannot break the build.
RUN pip install --no-cache-dir "mkdocs-material>=9,<10"

# Copy the docs sources (mkdocs.yml + docs/). site/ is excluded via .dockerignore.
COPY . .

# --strict: any broken internal link fails the build instead of shipping a 404.
RUN mkdocs build --strict

# ── Stage 2: serve with nginx ─────────────────────────────────────────────────
FROM nginx:1.27-alpine

# Replace the default server block with one tuned for a static MkDocs site.
COPY nginx.conf /etc/nginx/conf.d/default.conf

# Ship the generated HTML.
COPY --from=build /help/site /usr/share/nginx/html

EXPOSE 80

# Lightweight liveness check (busybox wget ships with the alpine image).
HEALTHCHECK --interval=30s --timeout=5s --retries=3 \
    CMD wget -q -O - http://localhost/ >/dev/null 2>&1 || exit 1
