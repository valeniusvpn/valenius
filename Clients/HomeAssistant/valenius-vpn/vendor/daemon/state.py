"""Thread-safe shared daemon state."""
from __future__ import annotations

import threading
import copy
from dataclasses import dataclass, field
from datetime import datetime, timedelta
from typing import Optional
from uuid import UUID, uuid4

from shared.messages import ConnectedTunnelInfo
from daemon.connection_policy import normalize_policy, utc_datetime

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
    system_owned: bool = False
    verified: bool = False
    verified_via_gateway: bool = False
    verification_failed: bool = False
    # UDP port-443 fallback (docs/design/port-443-fallback.md §5.4): the port a successful
    # fallback switch reconnected this tunnel on, 0 if still on the primary port. Sticky for
    # the life of the connection, like Windows' TunnelStateManager.FallbackPortUsed.
    fallback_port_used: int = 0


@dataclass
class _RegistrationState:
    client_id: UUID = field(default_factory=uuid4)
    is_active: bool = False
    last_checked_utc: Optional[datetime] = None

    def to_dict(self) -> dict:
        return {
            'ClientId': str(self.client_id),
            'IsActive': self.is_active,
            'LastCheckedUtc': self.last_checked_utc.isoformat() + 'Z' if self.last_checked_utc else None,
        }


class DaemonState:
    def __init__(self, offline_access_days: int = 7, *, clock=None):
        if type(offline_access_days) is not int or not 1 <= offline_access_days <= 365:
            raise ValueError('Valenius:OfflineAccessDays must be between 1 and 365')
        self._offline_access_period = timedelta(days=offline_access_days)
        self._clock = clock or datetime.utcnow
        self._connection_policy: Optional[dict] = None
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
        # profiles. Keyed by profile name — refreshed with connection policy.
        self._remote_lan_cidrs_by_profile: dict[str, list[str]] = {}
        # Per-profile UDP fallback port + trigger window (seconds), refreshed on every
        # heartbeat/poll. Present only when the serving sidecar has confirmed the redirect is
        # actually active (docs/design/port-443-fallback.md §4.4) -- mirrors Windows
        # RegistrationManager's _fallbackPorts. Keyed by profile name, like remote LAN CIDRs.
        self._fallback_ports: dict[str, tuple[int, int]] = {}
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
        # Standalone mode (see registration.py for persistence): once chosen, the tray
        # stops prompting for a backend URL and hides backend-dependent rows. Loaded
        # from registration.json at startup, mirrors Windows RegistrationState.
        self._standalone_mode_chosen = False
        # One-shot bulk transfer of local unmanaged profiles once a standalone client
        # later configures a backend and activates. Guards against re-transferring on
        # every heartbeat once done. Mirrors Windows RegistrationState.ProfilesTransferredToBackend.
        self._profiles_transferred = False
        # Refreshed with backend policy; persisted together with its activation lease.
        self._device_tunnel_enabled = False

    # ── Multi-tunnel state ───────────────────────────────────────────────────

    def set_connected(self, tunnel_name: str, user: str, *, system_owned: bool = False) -> None:
        with self._lock:
            self._active[tunnel_name] = _TunnelEntry(user=user, since=datetime.utcnow(), system_owned=system_owned)

    def can_control_tunnel(self, tunnel_name: str, username: str) -> bool:
        with self._lock:
            entry = self._active.get(tunnel_name)
            return bool(entry and not entry.system_owned and entry.user == username)

    def is_system_owned(self, tunnel_name: str) -> bool:
        with self._lock:
            entry = self._active.get(tunnel_name)
            return bool(entry and entry.system_owned)

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
                entry.verification_failed = False

    def set_verification_failed(self, tunnel_name: str) -> None:
        """Marks that the initial fast verification window elapsed with no success. Not
        terminal -- verification keeps retrying at a slower cadence afterward (see
        DaemonCore._verify_after_connect), and a later success still clears this via
        set_verified. No-op if the tunnel disconnected in the meantime."""
        with self._lock:
            entry = self._active.get(tunnel_name)
            if entry:
                entry.verification_failed = True

    def set_unverified(self, tunnel_name: str) -> None:
        """Clear the verified flag while a network-change re-verify is in flight.

        Keeps verified_via_gateway so the re-verifier can still tell whether this
        tunnel was previously confirmed end-to-end via the gateway probe.
        """
        with self._lock:
            entry = self._active.get(tunnel_name)
            if entry:
                entry.verified = False
                entry.verification_failed = False

    def is_verified_via_gateway(self, tunnel_name: str) -> bool:
        with self._lock:
            entry = self._active.get(tunnel_name)
            return bool(entry and entry.verified_via_gateway)

    def mark_used_fallback_port(self, tunnel_name: str, port: int) -> None:
        """Records that tunnel_name is now connected via its UDP fallback port, for the
        tray tag (see get_all_connected). No-op if the tunnel disconnected in the meantime."""
        with self._lock:
            entry = self._active.get(tunnel_name)
            if entry:
                entry.fallback_port_used = port

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
                    VerificationFailed=entry.verification_failed,
                    ConnectedUser=entry.user,
                    ConnectedSince=entry.since.isoformat(),
                    FallbackPortUsed=entry.fallback_port_used,
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


    def _now(self) -> datetime:
        return utc_datetime(self._clock().isoformat())

    def apply_backend_status(self, response: dict) -> None:
        """Apply activation and its policy together, before persistence or any await."""
        try:
            policy = normalize_policy(response)
        except (ValueError, TypeError, OverflowError):
            policy = None
        active = response.get('IsActive', response.get('isActive'))
        with self._lock:
            self._reg.is_active = active is True
            self._reg.last_checked_utc = self._now()
            self._apply_policy_locked(policy)
            # These are deliberately volatile even though other MFA fields persist.
            self._mfa_enrollment_uri = response.get('MfaEnrollmentUri', response.get('mfaEnrollmentUri'))
            self._mfa_approve_number = response.get('MfaApproveNumber', response.get('mfaApproveNumber'))

    def _apply_policy_locked(self, policy: Optional[dict]) -> None:
        self._connection_policy = policy
        p = policy or {}
        self._server_profile_name = p.get('ServerProfileName')
        self._server_vpn_ip = p.get('ServerVpnIp')
        from urllib.parse import urlparse
        try:
            self._server_health_port = urlparse(p.get('ServerHealthUrl') or '').port or 8443
        except ValueError:
            self._server_health_port = 8443
        self._device_tunnel_enabled = p.get('DeviceTunnelEnabled', False)
        self._mfa_required = p.get('MfaRequired', False)
        self._mfa_authorize_url = p.get('MfaAuthorizeUrl')
        self._mfa_session_expires_at = p.get('MfaSessionExpiresAt')
        self._mfa_enrollment_open = p.get('MfaEnrollmentOpen', False)
        self._remote_lan_cidrs_by_profile = {r['ProfileName']: list(r['Cidrs']) for r in p.get('RemoteLanCidrsByProfile', [])}
        self._fallback_ports = {r['ProfileName']: (r['Port'], r['TriggerSeconds']) for r in p.get('FallbackEndpoints', [])
                                if r['Port'] > 0}

    def registration_snapshot(self) -> dict:
        with self._lock:
            return {**self._reg.to_dict(), 'ConnectionPolicy': copy.deepcopy(self._connection_policy),
                    'StandaloneModeChosen': self._standalone_mode_chosen,
                    'ProfilesTransferredToBackend': self._profiles_transferred}

    def restore_registration(self, data: dict) -> None:
        # Read identity separately from authorization: missing/invalid legacy policy
        # requires a live refresh, never a new client identity or a new lease.
        reg = _RegistrationState()
        reg.client_id = UUID(data.get('ClientId') or data['clientId'])
        if reg.client_id.int == 0:
            raise ValueError('Empty client identity')
        reg.is_active = data.get('IsActive', data.get('isActive')) is True
        try:
            raw = data.get('LastCheckedUtc', data.get('lastCheckedUtc'))
            reg.last_checked_utc = utc_datetime(raw) if isinstance(raw, str) else None
            policy = normalize_policy(data['ConnectionPolicy'], cached=True)
        except (ValueError, TypeError, KeyError, OverflowError):
            reg.last_checked_utc = None
            policy = None
        with self._lock:
            self._reg = reg
            self._apply_policy_locked(policy)
            self._mfa_enrollment_uri = None
            self._mfa_approve_number = None
            self._standalone_mode_chosen = data.get('StandaloneModeChosen', data.get('standaloneModeChosen')) is True
            self._profiles_transferred = data.get('ProfilesTransferredToBackend', data.get('profilesTransferredToBackend')) is True

    def is_registration_active(self) -> bool:
        with self._lock:
            return self._reg.is_active

    def is_allowed_to_connect(self) -> bool:
        with self._lock:
            return self._activation_allowed_locked()

    def _activation_allowed_locked(self) -> bool:
        checked = self._reg.last_checked_utc
        now = self._now()
        return bool(self._reg.is_active and self._connection_policy is not None and checked
                    and checked <= now + timedelta(minutes=5)
                    and now - checked < self._offline_access_period)

    def _mfa_required_locked(self) -> bool:
        return self._mfa_required or bool(self._mfa_session_expires_at
            and self._now() >= utc_datetime(self._mfa_session_expires_at))

    def is_profile_allowed_to_connect(self, profile_name: str) -> bool:
        with self._lock:
            return self._activation_allowed_locked() and not (self._mfa_required_locked()
                and (not self._server_profile_name or profile_name == self._server_profile_name))

    def connection_denied_reason(self, profile_name: Optional[str] = None) -> str:
        with self._lock:
            if not self._reg.is_active:
                return 'This device is not activated. Contact your administrator.'
            if not self._activation_allowed_locked():
                return 'Cached VPN access expired or is unavailable. Reconnect to the management server to refresh access.'
            return 'MFA authorization is required for this VPN. Unlock it through Valenius before connecting.'

    def offline_access_expires_at(self) -> Optional[str]:
        with self._lock:
            checked = self._reg.last_checked_utc
            if not self._reg.is_active or self._connection_policy is None or checked is None:
                return None
            try:
                return (checked + self._offline_access_period).isoformat() + 'Z'
            except OverflowError:
                return None

    # ── MFA state ────────────────────────────────────────────────────────────


    def get_mfa_state(self) -> tuple[bool, Optional[str], Optional[str], bool, Optional[str], Optional[int]]:
        with self._lock:
            return (
                self._mfa_required_locked(),
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

    # ── Standalone mode ──────────────────────────────────────────────────────

    def get_standalone_mode_chosen(self) -> bool:
        with self._lock:
            return self._standalone_mode_chosen

    def set_standalone_mode_chosen(self, value: bool) -> None:
        with self._lock:
            self._standalone_mode_chosen = value

    def get_profiles_transferred(self) -> bool:
        with self._lock:
            return self._profiles_transferred

    def set_profiles_transferred(self, value: bool) -> None:
        with self._lock:
            self._profiles_transferred = value

    # ── Device tunnel ────────────────────────────────────────────────────────

    def get_device_tunnel_enabled(self) -> bool:
        with self._lock:
            return self._device_tunnel_enabled


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


    def get_server_profile_name(self) -> Optional[str]:
        with self._lock:
            return self._server_profile_name


    def get_remote_lan_cidrs(self, profile_name: Optional[str]) -> list[str]:
        """The remote LAN CIDR(s) reported for profile_name's own sidecar, or [] when none
        are known for that specific profile."""
        with self._lock:
            if not profile_name:
                return []
            return list(self._remote_lan_cidrs_by_profile.get(profile_name, []))


    def get_fallback_config(self, profile_name: Optional[str]) -> Optional[tuple[int, int]]:
        """(port, trigger_seconds) advertised for profile_name, or None when no fallback is
        offered (no customer opt-in, or opted-in but not confirmed active)."""
        with self._lock:
            if not profile_name:
                return None
            return self._fallback_ports.get(profile_name)

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
