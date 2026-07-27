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
