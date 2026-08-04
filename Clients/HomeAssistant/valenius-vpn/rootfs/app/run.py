"""Valenius Home Assistant add-on entry point.

Deliberately small: it drives the Linux client's proven `daemon/core.py` (`DaemonCore`)
directly instead of going through the Unix-socket IPC layer that exists for the
Linux tray -- there is no tray here, no per-user profile picker, and no
trusted-network auto-connect policy (a Home Assistant box is a fixed-location server,
not a roaming laptop). See Clients/HomeAssistant/CLAUDE.md for the full rationale.

What's reused verbatim from Clients/Linux/daemon/: core.py, backend.py,
config_manager.py, wg_manager.py, state.py, auto_connect.py (only its AutoConnectConfig
dataclass -- DaemonCore's constructor requires one, but its trusted-network policy is
never invoked here), updater.py (likewise required by the constructor but effectively
inert -- Home Assistant Supervisor owns image updates, not this app).

What's new: this file (options loading, the always-on supervising loop replacing
auto_connect.py's trusted-network policy) and mqtt_status.py (HA MQTT-discovery entity,
which the Linux client has no equivalent of).
"""
from __future__ import annotations

import asyncio
import json
import logging
import os
import signal
import socket
import sys
from pathlib import Path

# Reused modules live under /app/daemon and /app/shared (see the Dockerfile) --
# import them the same way daemon/__main__.py does.
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s %(levelname)s %(name)s: %(message)s',
    stream=sys.stdout,
)
log = logging.getLogger(__name__)

# Fixed sentinel "user" -- there is no login/session concept in a headless add-on, so
# every daemon API that takes a username (ConnectedUser attribution, per-user profile
# directories) gets this constant instead of a real OS account name.
USERNAME = 'homeassistant'

# Must be bumped alongside config.yaml's `version:` on every release. There is no
# build-time stamping step for this add-on the way packaging/build-deb.sh sed-patches
# daemon/backend.py's VERSION constant for the Linux .deb -- left unpatched, the backend
# always sees that vendored file's hardcoded source-tree default ('1.0.0') no matter what
# actually shipped, which makes Admin -> Clients useless for telling installs apart by
# version. See _install_version_override below for how this gets applied.
ADDON_VERSION = '1.0.4'

DATA_ROOT = Path('/data')
CHECK_INTERVAL_S = 20


def _load_options() -> dict:
    """HA Supervisor writes add-on options to /data/options.json (validated against
    config.yaml's schema). MQTT broker details, when the Mosquitto add-on is present,
    additionally arrive as env vars via this add-on's `services: ["mqtt:want"]`
    declaration in config.yaml -- those take priority over the manual mqtt_* options
    below, which exist only for an external/non-Supervisor broker."""
    options_path = DATA_ROOT / 'options.json'
    try:
        return json.loads(options_path.read_text())
    except FileNotFoundError:
        log.warning("No /data/options.json found -- running with empty options (local dev?).")
        return {}


def _ensure_persistent_data_dir() -> None:
    """daemon/config_manager.py hardcodes /var/lib/valenius as its storage root
    (registration.json, encrypted configs, the AES key). Home Assistant only persists
    /data across add-on restarts/updates, so symlink the unmodified path onto it --
    this is the ONLY accommodation config_manager.py needs; no code there changes."""
    target = DATA_ROOT / 'valenius'
    target.mkdir(parents=True, exist_ok=True)
    link = Path('/var/lib/valenius')
    if link.is_symlink() or link.exists():
        return
    link.parent.mkdir(parents=True, exist_ok=True)
    link.symlink_to(target)


def _install_hostname_override(client_id: str, display_name: str | None) -> None:
    """daemon/core.py (reused verbatim) identifies this client to the backend via
    socket.gethostname() -- same call the desktop Linux client uses, where it's a
    meaningful per-machine name. Inside a Home Assistant add-on container it's
    Supervisor-assigned and IDENTICAL across every physical install of this add-on
    (e.g. every Pi reports "local-valenius_vpn"), so the backend's Admin -> Clients
    list can't tell installs apart and flags every one after the first as a name
    collision (IsDuplicatePending in ClientsController.Register). Fix it here, by
    monkeypatching the stdlib call, rather than touching the vendored core.py: Python
    resolves `socket.gethostname` as a fresh attribute lookup on the module object at
    each call site, so replacing it here reaches core.py's calls too without it ever
    importing anything from this file.

    Falls back to a name derived from the persisted per-install ClientId (stable
    across restarts, unique by construction) when no display_name option is set, so
    two installs never collide even if both are left at defaults.
    """
    name = (display_name or '').strip() or f"valenius-ha-{client_id[:8]}"
    socket.gethostname = lambda: name


def _install_version_override(version: str) -> None:
    """daemon/backend.py's register()/User-Agent code reads the bare name VERSION from
    its own module globals at call time (same mechanism the hostname override above
    relies on), so assigning the attribute on the imported module reaches it with no
    further wiring -- see ADDON_VERSION above for why this is needed at all."""
    from daemon import backend as backend_mod
    backend_mod.VERSION = version


def _install_always_online_override(state) -> None:
    """core.py's heartbeat_once/poll_loop send TrayRunning=state.is_tray_running(), which
    the backend's ClientPresenceTracker uses to decide online/offline (root CLAUDE.md ->
    "Online presence tracking"). On the desktop Linux client that's a real proxy for "is
    the interactive tray UI open" -- state.is_tray_running() only flips true once
    ipc_server.py dispatches a command, which never happens here (this add-on never
    instantiates IpcServer, see the module docstring). Left alone, is_tray_running()
    always returns False, so the backend calls MarkOffline on literally every heartbeat
    and this always-on, headless add-on shows offline no matter what. There is no
    separate "UI" here to be closed while the daemon runs, so the daemon's own liveness
    IS the online signal -- honor it unconditionally instead of chasing IPC recency that
    can never happen. If a heartbeat/poll fails outright, nothing gets sent at all and
    the backend's own 7-minute presence timeout still catches a genuinely dead instance.
    """
    state.is_tray_running = lambda: True


def _mqtt_settings(options: dict) -> dict | None:
    host = os.environ.get('MQTT_HOST') or options.get('mqtt_host')
    if not host:
        return None
    return {
        'host': host,
        'port': int(os.environ.get('MQTT_PORT') or options.get('mqtt_port') or 1883),
        'username': os.environ.get('MQTT_USERNAME') or options.get('mqtt_username') or None,
        'password': os.environ.get('MQTT_PASSWORD') or options.get('mqtt_password') or None,
    }


async def _always_on_loop(core, mqtt, profile_hint: str | None) -> None:
    """Replaces daemon/auto_connect.py's trusted-network policy: this box has no
    "back in the office" case, so just keep asserting "connected" unconditionally.

    core.cmd_connect() is already idempotent -- it no-ops quickly once the target
    tunnel is up -- so calling it every tick is cheap and doubles as the
    reconnect-on-drop behavior with no separate "is it still connected" bookkeeping.
    _reverify_active_tunnels() (reused from DaemonCore's network-change re-verifier)
    tears down a tunnel whose gateway health probe has actually failed, so the
    cmd_connect() call right after it re-establishes it.
    """
    while True:
        try:
            await core._reverify_active_tunnels()
        except Exception:
            log.warning("Liveness re-verify failed", exc_info=True)

        try:
            resp = await core.cmd_connect(USERNAME, profile_hint)
            if not resp.Success:
                log.debug("Connect attempt: %s", resp.Error)
        except Exception:
            log.warning("Connect attempt failed", exc_info=True)

        if mqtt is not None:
            try:
                tunnel = core.state.get_tunnel()
                mqtt.publish_state(connected=tunnel.is_connected, verified=bool(
                    core.state.get_all_connected() and core.state.get_all_connected()[0].IsVerified))
            except Exception:
                log.warning("MQTT status publish failed", exc_info=True)

        await asyncio.sleep(CHECK_INTERVAL_S)


async def _main() -> None:
    options = _load_options()
    _ensure_persistent_data_dir()

    from daemon import config_manager as configs_mod
    from daemon import registration as reg_mod
    from daemon.auto_connect import AutoConnectConfig
    from daemon.backend import BackendClient
    from daemon.core import DaemonCore
    from daemon.state import DaemonState
    from daemon.updater import Updater
    from daemon.wg_manager import WgManager

    configs_mod.ensure_dirs()
    configs_mod.init_temp_dir()
    configs_mod.migrate_plain_configs()

    state = DaemonState()
    reg_mod.load(state)
    _install_hostname_override(str(state.get_client_id()), options.get('display_name'))
    _install_version_override(ADDON_VERSION)
    _install_always_online_override(state)

    loop = asyncio.get_running_loop()
    backend_url = options.get('backend_url', '')
    api_key = options.get('api_key', '')
    backend = BackendClient(backend_url, api_key, loop)
    wg = WgManager()

    # DaemonCore's constructor requires an AutoConnectConfig/Updater -- _process_status_response
    # mirrors the backend's generic AutoConnectEnabled/Profile fields into the former and can
    # invoke the latter on a ForceUpdate push, but this add-on never reads/gates on either: no
    # trusted-network policy is ever evaluated, and there is no matching Linux-style release
    # stream for this platform, so a stray ForceUpdate just logs a harmless failure. Home
    # Assistant Supervisor owns image updates instead.
    auto_connect = AutoConnectConfig()
    updater = Updater(backend, loop)
    core = DaemonCore(backend, state, wg, auto_connect, updater, loop)
    await core.reconcile_active_tunnels()

    mqtt = None
    mqtt_cfg = _mqtt_settings(options)
    if mqtt_cfg is not None:
        from mqtt_status import MqttStatus
        mqtt = MqttStatus(
            client_id=str(state.get_client_id()),
            display_name=options.get('display_name') or 'Valenius VPN',
            **mqtt_cfg,
        )
        mqtt.connect()
    else:
        log.info("No MQTT broker configured -- running without a Home Assistant status entity.")

    stop_event = asyncio.Event()

    def _shutdown(*_):
        log.info("Shutting down")
        stop_event.set()

    for sig in (signal.SIGINT, signal.SIGTERM):
        loop.add_signal_handler(sig, _shutdown)

    heartbeat_task = asyncio.create_task(core.heartbeat_loop())
    poll_task = asyncio.create_task(core.poll_loop())
    profile_hint = options.get('profile_name') or None
    connect_task = asyncio.create_task(_always_on_loop(core, mqtt, profile_hint))

    log.info("Valenius Home Assistant add-on started (ClientId=%s)", state.get_client_id())
    await stop_event.wait()

    try:
        await asyncio.wait_for(backend.notify_offline(state.get_client_id()), timeout=3)
    except Exception:
        pass
    heartbeat_task.cancel()
    poll_task.cancel()
    connect_task.cancel()
    if mqtt is not None:
        mqtt.disconnect()
    reg_mod.save(state)
    log.info("Add-on stopped")
    logging.shutdown()
    os._exit(0)


if __name__ == '__main__':
    asyncio.run(_main())
