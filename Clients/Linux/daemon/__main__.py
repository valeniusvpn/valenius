"""Valenius Linux daemon.

Entry point for the systemd service (runs as root).
Usage:  python3 -m daemon
"""
from __future__ import annotations

import asyncio
import logging
import os
import signal
import sys

# Allow running as `python3 -m daemon` from project root
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from daemon import config_manager as configs_mod
from daemon import registration as reg_mod
from daemon.auto_connect import AutoConnectConfig, NetworkMonitor
from daemon.backend import BackendClient
from daemon.core import DaemonCore
from daemon.ipc_server import IpcServer
from daemon.settings import Settings
from daemon.state import DaemonState
from daemon.updater import Updater
from daemon.wg_manager import WgManager

logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s %(levelname)s %(name)s: %(message)s',
    stream=sys.stdout,
)
log = logging.getLogger(__name__)


async def _main() -> None:
    # ── Bootstrap ────────────────────────────────────────────────────────────
    settings = Settings.load()
    configs_mod.ensure_dirs()
    configs_mod.init_temp_dir()
    configs_mod.migrate_plain_configs()

    state = DaemonState()
    reg_mod.load(state)

    loop = asyncio.get_running_loop()
    backend = BackendClient(settings.backend_url, settings.api_key, loop)
    wg = WgManager()

    # Load or init auto-connect config
    from daemon.config_manager import AUTOCONNECT_FILE
    auto_connect = AutoConnectConfig.load(AUTOCONNECT_FILE) or AutoConnectConfig()

    updater = Updater(backend, loop)
    core = DaemonCore(backend, state, wg, auto_connect, updater, loop)
    await core.reconcile_active_tunnels()

    # ── Network monitor ──────────────────────────────────────────────────────
    monitor = NetworkMonitor(core.on_network_change)
    monitor.start()
    core._net_monitor = monitor

    # ── IPC server ───────────────────────────────────────────────────────────
    ipc = IpcServer(core)
    await ipc.start()

    # ── Graceful shutdown ────────────────────────────────────────────────────
    stop_event = asyncio.Event()

    def _shutdown(*_):
        log.info("Shutting down")
        stop_event.set()

    for sig in (signal.SIGINT, signal.SIGTERM):
        loop.add_signal_handler(sig, _shutdown)

    # ── Heartbeat + long-poll loops ──────────────────────────────────────────
    heartbeat_task = asyncio.create_task(core.heartbeat_loop())
    poll_task = asyncio.create_task(core.poll_loop())

    log.info("Valenius daemon started (ClientId=%s)", state.get_client_id())
    await stop_event.wait()

    # ── Cleanup ──────────────────────────────────────────────────────────────
    # Best-effort: tell the backend we're going offline so the admin panel updates
    # immediately on graceful shutdown (service stop / machine shutdown), instead
    # of waiting for the presence threshold to expire.
    try:
        await asyncio.wait_for(backend.notify_offline(state.get_client_id()), timeout=3)
    except Exception:
        pass
    heartbeat_task.cancel()
    poll_task.cancel()
    monitor.stop()
    await ipc.stop()
    reg_mod.save(state)
    log.info("Daemon stopped")

    # Exit immediately instead of returning into asyncio.run()'s teardown. A long-poll
    # (backend.poll, ~65s socket timeout) may be parked in a default-executor thread;
    # asyncio.run() would block in shutdown_default_executor() waiting for it (up to
    # THREAD_JOIN_TIMEOUT = 300s on 3.12+), blowing past systemd's stop timeout. That
    # SIGKILL turns an auto-update `systemctl restart` into a *failed* restart -- the new
    # daemon never starts and dpkg is left half-configured. Everything is persisted above,
    # so abandon the dangling HTTP thread and exit cleanly now.
    logging.shutdown()
    os._exit(0)


if __name__ == '__main__':
    if os.geteuid() != 0:
        sys.exit("valenius-daemon must be run as root")
    asyncio.run(_main())
