# Build context must be the REPO ROOT (mirrors Backend/Dockerfile reaching
# Shared/Valenius.Shared/) so this can COPY the Linux daemon's core modules directly --
# see Clients/HomeAssistant/CLAUDE.md for why this add-on reuses them unmodified instead
# of forking a second copy.
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

# Reused verbatim from the Linux client -- see Clients/HomeAssistant/CLAUDE.md.
COPY Clients/Linux/daemon/core.py            /app/daemon/core.py
COPY Clients/Linux/daemon/backend.py         /app/daemon/backend.py
COPY Clients/Linux/daemon/config_manager.py  /app/daemon/config_manager.py
COPY Clients/Linux/daemon/wg_manager.py      /app/daemon/wg_manager.py
COPY Clients/Linux/daemon/state.py           /app/daemon/state.py
COPY Clients/Linux/daemon/registration.py    /app/daemon/registration.py
COPY Clients/Linux/daemon/auto_connect.py    /app/daemon/auto_connect.py
COPY Clients/Linux/daemon/updater.py         /app/daemon/updater.py
# core.py's cmd_connect/cmd_disconnect lazily import _ok()/_fail() (PipeResponse
# builders) from here -- not the IpcServer class itself (never instantiated), but the
# module must still be importable. Confirmed by an actual container run: omitting this
# raised ModuleNotFoundError the first time cmd_connect() ran, not at image build time.
COPY Clients/Linux/daemon/ipc_server.py      /app/daemon/ipc_server.py
COPY Clients/Linux/daemon/__init__.py        /app/daemon/__init__.py
COPY Clients/Linux/shared/                   /app/shared/

# New, add-on-only code.
COPY Clients/HomeAssistant/valenius-vpn/rootfs/app/ /app/

WORKDIR /app
ENTRYPOINT ["python3", "-u", "/app/run.py"]
