# Build context is THIS add-on's own folder (Clients/HomeAssistant/valenius-vpn/), not
# the repo root -- confirmed the hard way: a manual `docker build -f ... .` from the repo
# root built fine, but Home Assistant Supervisor's real build (context scoped to this
# folder) failed every `COPY Clients/Linux/...` with "not found", since Docker's COPY can
# never reach outside its build context. The Linux daemon modules this add-on reuses are
# therefore vendored into vendor/ by scripts/sync-ha-addon.ps1 (run from the repo root,
# NOT shipped in this add-on -- it's repo-private tooling) rather than copied directly
# from Clients/Linux/ -- see Clients/HomeAssistant/CLAUDE.md for the full story.
#
# python:3.12-slim (Debian/glibc), not an Alpine base: the `cryptography` package (used
# for the AES-256-GCM config-at-rest encryption, reused verbatim from the Linux client)
# ships manylinux (glibc) wheels on PyPI but not musl ones, so Alpine would need a slow
# source build with a Rust toolchain. Debian-slim gets a prebuilt wheel for free.
FROM python:3.12-slim

RUN apt-get update && apt-get install -y --no-install-recommends \
        wireguard-tools \
        iproute2 \
        procps \
    && rm -rf /var/lib/apt/lists/*

RUN pip install --no-cache-dir cryptography paho-mqtt

# Vendored copy of the Linux client's daemon modules -- see the note above.
# core.py's cmd_connect/cmd_disconnect lazily import _ok()/_fail() (PipeResponse
# builders) from ipc_server.py -- not the IpcServer class itself (never instantiated),
# but the module must still be importable; confirmed by an actual container run.
COPY vendor/daemon/ /app/daemon/
COPY vendor/shared/ /app/shared/

# New, add-on-only code.
COPY rootfs/app/ /app/

WORKDIR /app
ENTRYPOINT ["python3", "-u", "/app/run.py"]
