"""Auto-connect via NetworkManager D-Bus.

Runs in a background daemon thread with its own GLib MainLoop so it does
not block the asyncio event loop.  Falls back to polling if D-Bus /
NetworkManager is unavailable (e.g. minimal server installs).
"""
from __future__ import annotations

import ipaddress
import json
import logging
import re
import subprocess
import threading
from datetime import datetime, timedelta
from pathlib import Path
from typing import Callable, Optional

log = logging.getLogger(__name__)

_ACTION_COOLDOWN = timedelta(seconds=60)
_FALLBACK_POLL_S = 300


class AutoConnectConfig:
    """Thread-safe auto-connect configuration (persisted to autoconnect.json)."""

    def __init__(self):
        self._lock = threading.Lock()
        self.admin_enabled: bool = False
        self.enabled: bool = False
        self.profile_name: Optional[str] = None
        self.trusted_networks: list[dict] = []

    def update_from_backend(
        self,
        admin_enabled: bool,
        enabled: bool,
        profile_name: Optional[str],
        trusted_networks: list[dict],
    ) -> None:
        with self._lock:
            self.admin_enabled = admin_enabled
            self.enabled = enabled
            self.profile_name = profile_name
            self.trusted_networks = list(trusted_networks)

    def set_user_enabled(self, value: bool) -> None:
        with self._lock:
            self.enabled = value

    def is_enabled(self) -> bool:
        with self._lock:
            return self.enabled

    def get_trusted_networks(self) -> list[dict]:
        with self._lock:
            return list(self.trusted_networks)

    def get_profile_name(self) -> Optional[str]:
        with self._lock:
            return self.profile_name

    def to_dict(self) -> dict:
        with self._lock:
            return {
                'AdminEnabled': self.admin_enabled,
                'Enabled': self.enabled,
                'ProfileName': self.profile_name,
                'TrustedNetworks': list(self.trusted_networks),
            }

    @staticmethod
    def from_dict(d: dict) -> AutoConnectConfig:
        cfg = AutoConnectConfig()
        cfg.admin_enabled = d.get('AdminEnabled', d.get('adminEnabled', False))
        cfg.enabled = d.get('Enabled', d.get('enabled', False))
        cfg.profile_name = d.get('ProfileName', d.get('profileName'))
        nets = d.get('TrustedNetworks', d.get('trustedNetworks', []))
        cfg.trusted_networks = [
            {
                'Subnet': n.get('Subnet', n.get('subnet', '')),
                'PrimaryDns': n.get('PrimaryDns', n.get('primaryDns')),
            }
            for n in nets
        ]
        return cfg

    def save(self, path: Path) -> None:
        path.write_text(json.dumps(self.to_dict(), indent=2))
        path.chmod(0o600)

    @staticmethod
    def load(path: Path) -> Optional[AutoConnectConfig]:
        if not path.exists():
            return None
        try:
            return AutoConnectConfig.from_dict(json.loads(path.read_text()))
        except Exception:
            return None


# ── Network helpers ──────────────────────────────────────────────────────────

def get_local_ipv4_addresses() -> list[tuple[str, str]]:
    """Return [(iface, ip), ...] for non-loopback IPv4 interfaces."""
    try:
        out = subprocess.run(
            ['ip', '-4', 'addr', 'show'],
            capture_output=True, text=True, timeout=5,
        ).stdout
        results: list[tuple[str, str]] = []
        current: Optional[str] = None
        for line in out.splitlines():
            m = re.match(r'^\d+:\s+(\S+):', line)
            if m:
                current = m.group(1).rstrip(':@')
            m = re.match(r'^\s+inet\s+(\d+\.\d+\.\d+\.\d+)/', line)
            if m and current and current != 'lo':
                results.append((current, m.group(1)))
        return results
    except Exception:
        return []


def get_primary_dns() -> Optional[str]:
    """Return the first nameserver from /etc/resolv.conf."""
    try:
        with open('/etc/resolv.conf') as f:
            for line in f:
                if line.startswith('nameserver'):
                    parts = line.split()
                    if len(parts) >= 2:
                        return parts[1]
    except Exception:
        pass
    return None


def is_in_trusted_network(trusted_networks: list[dict]) -> bool:
    if not trusted_networks:
        return False
    local_ips = get_local_ipv4_addresses()
    primary_dns = get_primary_dns()
    for entry in trusted_networks:
        subnet_str = entry.get('Subnet', '')
        required_dns = entry.get('PrimaryDns')
        if not subnet_str:
            continue
        try:
            subnet = ipaddress.IPv4Network(subnet_str, strict=False)
        except ValueError:
            continue
        for _, ip_str in local_ips:
            try:
                if ipaddress.IPv4Address(ip_str) not in subnet:
                    continue
                if required_dns is None or primary_dns == required_dns:
                    return True
            except ValueError:
                continue
    return False


# ── Monitor ──────────────────────────────────────────────────────────────────

class NetworkMonitor:
    """Watches for network changes and fires the on_change callback.

    The monitor is a *dumb trigger*: it fires on every network transition
    regardless of the auto-connect setting. The daemon decides what to do —
    re-verify active tunnels (always) and apply the auto-connect policy (only
    when enabled). Trusted-network evaluation lives in the daemon for the same
    reason.

    Deliberately has no initial startup check of its own — DaemonCore.run_startup_auto_connect_check
    owns that (it needs auto-connect state to decide whether to retry, which this dumb trigger
    doesn't have). This monitor only provides the real D-Bus event trigger and the periodic
    fallback for later, once the daemon is already running.
    """

    def __init__(
        self,
        on_change: Callable[[], None],
    ):
        self._on_change = on_change
        self._stop = threading.Event()

    def start(self) -> None:
        t = threading.Thread(target=self._run, daemon=True, name='net-monitor')
        t.start()

    def stop(self) -> None:
        self._stop.set()

    def check_now(self) -> None:
        self._check()

    def _check(self) -> None:
        log.debug("Network change detected")
        self._on_change()

    def _run(self) -> None:
        try:
            self._run_dbus()
        except Exception as e:
            log.warning("D-Bus monitor failed (%s), using polling", e)
            self._run_poll()

    def _run_dbus(self) -> None:
        import dbus
        import dbus.mainloop.glib
        from gi.repository import GLib

        dbus.mainloop.glib.DBusGMainLoop(set_as_default=True)
        bus = dbus.SystemBus()
        loop = GLib.MainLoop()

        def _on_state_changed(*_args):
            # 3-second debounce mirrors Windows NetworkAddressChanged handling
            GLib.timeout_add_seconds(3, lambda: (self._check(), False)[1])

        bus.add_signal_receiver(
            _on_state_changed,
            signal_name='StateChanged',
            dbus_interface='org.freedesktop.NetworkManager',
            bus_name='org.freedesktop.NetworkManager',
        )

        def _periodic():
            self._check()
            return not self._stop.is_set()

        GLib.timeout_add_seconds(_FALLBACK_POLL_S, _periodic)
        loop.run()

    def _run_poll(self) -> None:
        while not self._stop.wait(timeout=_FALLBACK_POLL_S):
            self._check()
