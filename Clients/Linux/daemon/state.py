"""Thread-safe shared daemon state."""
from __future__ import annotations

import threading
from dataclasses import dataclass, field
from datetime import datetime, timedelta
from typing import Optional
from uuid import UUID, uuid4

from shared.messages import ConnectedTunnelInfo

_GRACE_PERIOD = timedelta(days=1)

# The tray polls the daemon over IPC every 5 s (see tray/app.py POLL_INTERVAL_MS).
# Anything we've heard from within 3x that interval counts as "tray running" --
# this is the Linux equivalent of the Windows service checking for
# Valenius.TrayApp.exe in the process list.
_TRAY_ACTIVE_THRESHOLD = timedelta(seconds=15)


@dataclass
class _TunnelState:
    """Backward-compat view of the primary tunnel (single-tunnel callers)."""
    is_connected: bool = False
    tunnel_name: Optional[str] = None
    connected_user: Optional[str] = None
    connected_since: Optional[datetime] = None


@dataclass
class _TunnelEntry:
    user: str
    since: datetime
    verified: bool = False
    verified_via_gateway: bool = False


@dataclass
class _RegistrationState:
    client_id: UUID = field(default_factory=uuid4)
    is_active: bool = False
    last_checked_utc: Optional[datetime] = None

    def to_dict(self) -> dict:
        return {
            'ClientId': str(self.client_id),
            'IsActive': self.is_active,
            'LastCheckedUtc': self.last_checked_utc.isoformat() if self.last_checked_utc else None,
        }

    @staticmethod
    def from_dict(d: dict) -> _RegistrationState:
        s = _RegistrationState()
        s.client_id = UUID(d['ClientId'])
        s.is_active = d.get('IsActive', False)
        raw = d.get('LastCheckedUtc')
        s.last_checked_utc = datetime.fromisoformat(raw) if raw else None
        return s


class DaemonState:
    def __init__(self):
        self._lock = threading.Lock()
        self._active: dict[str, _TunnelEntry] = {}   # keyed by tunnel name
        self._reg = _RegistrationState()
        self._update_available = False
        self._server_profile_name: Optional[str] = None
        self._server_vpn_ip: Optional[str] = None
        self._server_health_port: int = 8443
        # Per-profile physical LAN CIDR(s) behind that profile's own sidecar, self-reported
        # via /info and relayed by the backend. Refreshed on every heartbeat/poll. Used by
        # the pre-connect LAN-conflict check to detect the remote network for full-tunnel
        # profiles. Keyed by profile name — see set_remote_lan_cidrs.
        self._remote_lan_cidrs_by_profile: dict[str, list[str]] = {}
        self._heartbeat_interval = 5
        self._tray_last_seen: Optional[datetime] = None
        # MFA state — written by heartbeat_once, read by build_tunnel_status
        self._mfa_required = False
        self._mfa_authorize_url: Optional[str] = None
        self._mfa_session_expires_at: Optional[str] = None
        self._mfa_enrollment_open = False
        self._mfa_enrollment_uri: Optional[str] = None
        self._mfa_approve_number: Optional[int] = None
        # None until the first heartbeat/poll/register completes — lets the tray
        # show a neutral "Checking backend..." instead of a false "unreachable".
        self._backend_reachable: Optional[bool] = None

    # ── Multi-tunnel state ───────────────────────────────────────────────────

    def set_connected(self, tunnel_name: str, user: str) -> None:
        with self._lock:
            self._active[tunnel_name] = _TunnelEntry(user=user, since=datetime.utcnow())

    def set_disconnected(self, tunnel_name: Optional[str] = None) -> None:
        """Remove one tunnel by name, or clear all if tunnel_name is None."""
        with self._lock:
            if tunnel_name:
                self._active.pop(tunnel_name, None)
            else:
                self._active.clear()

    def set_verified(self, tunnel_name: str, via_gateway: bool = False) -> None:
        with self._lock:
            entry = self._active.get(tunnel_name)
            if entry:
                entry.verified = True
                entry.verified_via_gateway = via_gateway

    def set_unverified(self, tunnel_name: str) -> None:
        """Clear the verified flag while a network-change re-verify is in flight.

        Keeps verified_via_gateway so the re-verifier can still tell whether this
        tunnel was previously confirmed end-to-end via the gateway probe.
        """
        with self._lock:
            entry = self._active.get(tunnel_name)
            if entry:
                entry.verified = False

    def is_verified_via_gateway(self, tunnel_name: str) -> bool:
        with self._lock:
            entry = self._active.get(tunnel_name)
            return bool(entry and entry.verified_via_gateway)

    def is_connected(self, tunnel_name: Optional[str] = None) -> bool:
        with self._lock:
            if tunnel_name:
                return tunnel_name in self._active
            return bool(self._active)

    def get_all_connected(self) -> list[ConnectedTunnelInfo]:
        with self._lock:
            return [
                ConnectedTunnelInfo(
                    Name=name,
                    IsVerified=entry.verified,
                    ConnectedUser=entry.user,
                    ConnectedSince=entry.since.isoformat(),
                )
                for name, entry in self._active.items()
            ]

    def get_tunnel(self, preferred: Optional[str] = None) -> _TunnelState:
        """Backward-compat: returns the server profile, or the preferred tunnel, or the first."""
        with self._lock:
            if not self._active:
                return _TunnelState()
            server = self._server_profile_name
            name = None
            if preferred and preferred in self._active:
                name = preferred
            elif server and server in self._active:
                name = server
            else:
                name = next(iter(self._active))
            entry = self._active[name]
            return _TunnelState(True, name, entry.user, entry.since)

    # ── Registration ─────────────────────────────────────────────────────────

    def get_client_id(self) -> UUID:
        with self._lock:
            return self._reg.client_id

    def set_client_id(self, uid: UUID) -> None:
        with self._lock:
            self._reg.client_id = uid

    def get_registration(self) -> _RegistrationState:
        with self._lock:
            r = self._reg
            return _RegistrationState(r.client_id, r.is_active, r.last_checked_utc)

    def update_registration(self, is_active: bool) -> None:
        with self._lock:
            self._reg.is_active = is_active
            self._reg.last_checked_utc = datetime.utcnow()

    def is_registration_active(self) -> bool:
        with self._lock:
            return self._reg.is_active

    def is_allowed_to_connect(self) -> bool:
        with self._lock:
            if self._reg.is_active:
                return True
            if self._reg.last_checked_utc:
                return datetime.utcnow() - self._reg.last_checked_utc < _GRACE_PERIOD
            return False

    # ── MFA state ────────────────────────────────────────────────────────────

    def set_mfa_state(
        self,
        required: bool,
        authorize_url: Optional[str],
        session_expires_at: Optional[str],
        enrollment_open: bool,
        enrollment_uri: Optional[str],
        approve_number: Optional[int] = None,
    ) -> None:
        with self._lock:
            self._mfa_required = required
            self._mfa_authorize_url = authorize_url
            self._mfa_session_expires_at = session_expires_at
            self._mfa_enrollment_open = enrollment_open
            self._mfa_enrollment_uri = enrollment_uri
            self._mfa_approve_number = approve_number

    def get_mfa_state(self) -> tuple[bool, Optional[str], Optional[str], bool, Optional[str], Optional[int]]:
        with self._lock:
            return (
                self._mfa_required,
                self._mfa_authorize_url,
                self._mfa_session_expires_at,
                self._mfa_enrollment_open,
                self._mfa_enrollment_uri,
                self._mfa_approve_number,
            )

    def set_backend_reachable(self, reachable: bool) -> None:
        with self._lock:
            self._backend_reachable = reachable

    def is_backend_reachable(self) -> Optional[bool]:
        with self._lock:
            return self._backend_reachable

    # ── Tray presence ─────────────────────────────────────────────────────────

    def mark_tray_seen(self) -> None:
        with self._lock:
            self._tray_last_seen = datetime.utcnow()

    def is_tray_running(self) -> bool:
        with self._lock:
            if self._tray_last_seen is None:
                return False
            return datetime.utcnow() - self._tray_last_seen < _TRAY_ACTIVE_THRESHOLD

    # ── Update / misc ─────────────────────────────────────────────────────────

    def set_update_available(self, v: bool) -> None:
        with self._lock:
            self._update_available = v

    def get_update_available(self) -> bool:
        with self._lock:
            return self._update_available

    def set_heartbeat_interval(self, minutes: int) -> None:
        with self._lock:
            self._heartbeat_interval = max(1, minutes)

    def get_heartbeat_interval(self) -> int:
        with self._lock:
            return self._heartbeat_interval

    def set_server_profile_name(self, name: Optional[str]) -> None:
        with self._lock:
            self._server_profile_name = name

    def get_server_profile_name(self) -> Optional[str]:
        with self._lock:
            return self._server_profile_name

    def set_server_gateway(self, vpn_ip: Optional[str], health_port: int) -> None:
        with self._lock:
            self._server_vpn_ip = vpn_ip or None
            self._server_health_port = health_port

    def set_remote_lan_cidrs(self, entries: Optional[list[dict]]) -> None:
        """entries: [{'ProfileName': ..., 'Cidrs': [...]}] from RemoteLanCidrsByProfile.

        MUST stay keyed by profile name — the native server profile and each foreign
        profile each report their OWN source customer's LAN CIDRs. Applying one profile's
        entry to a different profile's connect would misattribute a conflict (a real bug
        found in testing where a native sidecar's conflicting LAN wrongly blocked an
        unrelated foreign profile).
        """
        with self._lock:
            by_profile: dict[str, list[str]] = {}
            for entry in entries or []:
                name = entry.get('ProfileName') or entry.get('profileName')
                cidrs = entry.get('Cidrs') or entry.get('cidrs') or []
                if name and cidrs:
                    by_profile[name] = list(cidrs)
            self._remote_lan_cidrs_by_profile = by_profile

    def get_remote_lan_cidrs(self, profile_name: Optional[str]) -> list[str]:
        """The remote LAN CIDR(s) reported for profile_name's own sidecar, or [] when none
        are known for that specific profile."""
        with self._lock:
            if not profile_name:
                return []
            return list(self._remote_lan_cidrs_by_profile.get(profile_name, []))

    def get_gateway_probe(self, profile: Optional[str]) -> Optional[tuple[str, int]]:
        """Gateway /health target (ip, port) for the server profile, or None.

        Mirrors the Windows RegistrationManager.GetGatewayProbe — only the native
        server tunnel has a gateway target; foreign profiles fall back to handshake.
        """
        with self._lock:
            if (profile and self._server_vpn_ip
                    and profile == self._server_profile_name):
                return (self._server_vpn_ip, self._server_health_port)
            return None
