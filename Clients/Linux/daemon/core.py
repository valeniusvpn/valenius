"""Daemon business logic — wires together all sub-systems."""
from __future__ import annotations

import asyncio
import json
import logging
import os
import socket
from datetime import datetime, timedelta
from pathlib import Path
from typing import Optional
from uuid import UUID

from shared.messages import (
    PipeResponse, RegistrationResult, TunnelStatus, VersionCheckResult,
)

from daemon import config_manager as configs_mod
from daemon import registration as reg_mod
from daemon.auto_connect import AutoConnectConfig, NetworkMonitor, is_in_trusted_network
from daemon.backend import BackendClient, get_lan_ip
from daemon.state import DaemonState
from daemon.updater import Updater
from daemon.wg_manager import WgManager

log = logging.getLogger(__name__)

# After a network change, let the path settle before probing, then re-verify each
# active tunnel for up to the budget (retrying) before declaring it dead.
_REVERIFY_SETTLE_S = 5
_REVERIFY_BUDGET_S = 15

# Post-connect verification retry loop (mirrors Windows TunnelVerifier): retry every
# _VERIFY_FAST_INTERVAL_S for the first _VERIFY_FAST_WINDOW_S; if nothing has succeeded by
# then, mark the tunnel's confirmation as failed (tray shows an amber tag) but keep retrying
# every _VERIFY_SLOW_INTERVAL_S for as long as the tunnel stays connected, instead of giving
# up permanently after a fixed budget.
_VERIFY_ATTEMPT_BUDGET_S = 4
_VERIFY_FAST_INTERVAL_S = 5
_VERIFY_FAST_WINDOW_S = 60
_VERIFY_SLOW_INTERVAL_S = 20

# Minimum gap between auto-connect actions, to prevent flapping (mirrors Windows).
_ACTION_COOLDOWN = timedelta(seconds=60)

# Startup auto-connect retry (mirrors Windows AutoConnectService.RunStartupCheckAsync): a
# single attempt right after boot can lose a race with the network stack settling
# (DHCP/default route/DNS), so retry a few times before falling back to the NetworkMonitor's
# periodic poll / real network-change events.
_STARTUP_INITIAL_DELAY_S = 15
_STARTUP_RETRY_INTERVAL_S = 10
_STARTUP_RETRY_ATTEMPTS = 6  # ~1 minute total, then fall into the normal triggers.


class DaemonCore:
    def __init__(self, backend: BackendClient, state: DaemonState, wg: WgManager,
                 auto_connect: AutoConnectConfig, updater: Updater,
                 loop: asyncio.AbstractEventLoop):
        self.backend = backend
        self.state = state
        self.wg = wg
        self.auto_connect = auto_connect
        self.updater = updater
        self.configs = configs_mod
        self._loop = loop
        self._net_monitor: Optional[NetworkMonitor] = None
        # Per-target cooldown (keyed by target label -- 'AutoConnect' / 'DeviceTunnel'), so one
        # target's action never blocks the other's in the same check. Mirrors Windows
        # AutoConnectService's _lastActionByTarget.
        self._last_auto_action: dict[str, datetime] = {}
        self._handling_network_change = False
        # Guards DaemonCore._transfer_local_profiles against a heartbeat/poll race —
        # both loops call _process_status_response and could otherwise fire it twice.
        self._transfer_in_progress = False
        self._tunnel_mutation = asyncio.Lock()
        self._provisioning_lock = asyncio.Lock()
        self._tunnel_config_paths: dict[str, Path] = {}

    async def reconcile_active_tunnels(self) -> None:
        """Adopt any WireGuard interfaces already up at startup into daemon state.

        A kernel interface survives a daemon restart (upgrade, crash, manual
        restart) independently of our process. Without this, a fresh DaemonState
        starts empty and thinks nothing is connected, so a user's next Connect
        click runs `wg-quick up` against an interface that already exists (fails:
        "already exists") and Disconnect can't find anything in state to tear
        down either — the daemon and the kernel silently disagree about reality.
        """
        for name in await self.wg.get_active_tunnels():
            # Ownership is lost across restart; require trusted/root control.
            self.state.set_connected(name, 'Unknown', system_owned=True)
            log.info("Adopted already-active tunnel '%s' into daemon state at startup", name)

    # ── Status helpers ───────────────────────────────────────────────────────

    def build_tunnel_status(self, username: str) -> TunnelStatus:
        from daemon.backend import VERSION
        connected_tunnels = self.state.get_all_connected()
        primary = self.state.get_tunnel()
        mfa = self.state.get_mfa_state()
        profiles = configs_mod.order_profiles(
            configs_mod.get_profiles(username),
            self.state.get_server_profile_name(),
        )
        return TunnelStatus(
            IsConnected=primary.is_connected,
            IsVerified=connected_tunnels[0].IsVerified if connected_tunnels else False,
            TunnelName=primary.tunnel_name,
            ConnectedUser=primary.connected_user,
            ConnectedSince=primary.connected_since.isoformat() if primary.connected_since else None,
            ConnectedTunnels=connected_tunnels,
            RegistrationIsActive=self.state.is_registration_active(),
            HasStagedConfig=configs_mod.has_staged_config(),
            UpdateAvailable=self.state.get_update_available(),
            AvailableProfiles=profiles,
            DeletableProfiles=configs_mod.get_deletable_profiles(username),
            AutoConnectEnabled=self.auto_connect.is_enabled(),
            MfaRequired=mfa[0],
            MfaAuthorizeUrl=mfa[1],
            MfaSessionExpiresAt=mfa[2],
            MfaEnrollmentOpen=mfa[3],
            MfaEnrollmentUri=mfa[4],
            MfaApproveNumber=mfa[5],
            BackendUrl=self.backend.base_url,
            BackendReachable=self.state.is_backend_reachable(),
            OfflineAccessExpiresAt=self.state.offline_access_expires_at(),
            # No backend URL yet (fresh install, installer provided none): the tray prompts
            # the user for the server DNS. False whenever a URL is configured.
            BackendUnconfigured=not self.backend.is_configured,
            # True once the user explicitly chose "Use standalone" at first run. The tray
            # uses this to stop prompting for a backend URL and hide backend-dependent rows.
            StandaloneMode=self.state.get_standalone_mode_chosen(),
            # Installed package version (the daemon is restarted by postinst on
            # update, so this reflects the on-disk code). The tray compares it to
            # its own in-memory VERSION and self-restarts on a mismatch, so an
            # auto-update refreshes the tray UI without a logout/login.
            DaemonVersion=VERSION,
        )

    # ── Commands ─────────────────────────────────────────────────────────────

    async def cmd_connect(self, username: str, profile_name: Optional[str], *, privileged: bool = False) -> PipeResponse:
        async with self._tunnel_mutation:
            return await self._connect(username, profile_name, privileged=privileged)

    async def _connect(self, username: str, profile_name: Optional[str], *, privileged: bool) -> PipeResponse:
        from daemon.ipc_server import _ok, _fail
        if profile_name is not None and not configs_mod.is_valid_profile_name(profile_name):
            return _fail('Invalid profile name.')

        # Standalone (no backend configured at all): skip the authorization gate entirely —
        # there is nothing to authorize against. The moment a backend URL is set, normal
        # authorization applies from then on, same as any brand-new client. Mirrors Windows
        # PipeServer's Connect handler.
        if self.backend.is_configured and not self.state.is_allowed_to_connect():
            _, error = await self._live_register()
            if not self.state.is_allowed_to_connect():
                return _fail(f"{self.state.connection_denied_reason()} {error or ''}".strip())

        configs_mod.claim_staged_config(username)

        if profile_name:
            config_path = configs_mod.get_config_path(username, profile_name)
            if config_path is None and privileged:
                # Only trusted daemon actions/authenticated root may search other users.
                config_path = configs_mod.get_config_path_by_tunnel_name(profile_name)
            if config_path is None and privileged and username == 'DeviceTunnel':
                # Last resort: the machine-level store, for a never-logged-into machine
                # where no per-user copy exists yet. Mirrors Windows
                # AutoConnectService.FindConfigPath's two-step lookup.
                config_path = configs_mod.get_device_tunnel_config_path(profile_name)
            if config_path is None:
                return _fail(f"Profile '{profile_name}' not found.")
        else:
            config_path = configs_mod.get_first_config_path(username)
            if config_path is None:
                return _fail("No config found. Please upload a WireGuard .conf file first.")

        tunnel_name = configs_mod.profile_name_from_path(config_path)
        if not tunnel_name:
            return _fail(f"Could not determine tunnel name from config path: {config_path}")

        if self.backend.is_configured and not self.state.is_profile_allowed_to_connect(tunnel_name):
            return _fail(self.state.connection_denied_reason(tunnel_name))

        # Already connected to this tunnel — nothing to do.
        if self.state.is_connected(tunnel_name):
            if not privileged and not self.state.can_control_tunnel(tunnel_name, username):
                return _fail("Not authorized to control another user's tunnel.")
            return _ok()

        # AllowedIPs conflict check — mirrors Windows WireGuardController.FindAllowedIpsConflict.
        active_names = [t.Name for t in self.state.get_all_connected()]
        if active_names:
            conflict = configs_mod.find_allowed_ips_conflict(config_path, active_names)
            if conflict:
                return _fail(
                    f"AllowedIPs in '{tunnel_name}' overlap with the active tunnel '{conflict}'. "
                    f"Disconnect '{conflict}' first."
                )

        # Local-LAN conflict check — mirrors Windows WireGuardController.FindLocalLanConflict /
        # macOS findLocalLanConflict. No override: the user must resolve the conflict first.
        lan_conflict = configs_mod.find_local_lan_conflict(config_path, self.state.get_remote_lan_cidrs(tunnel_name))
        if lan_conflict:
            return _fail(lan_conflict, is_lan_conflict=True)

        wan_ip = await self.backend.get_wan_ip()
        try:
            ok, output = await self._start_tunnel(config_path, tunnel_name)
        except ValueError as e:
            return _fail(str(e))
        if not ok:
            return _fail(f"Failed to start tunnel: {output}")

        self.state.set_connected(tunnel_name, username, system_owned=privileged)
        asyncio.create_task(
            self.backend.log_event(
                self.state.get_client_id(), 'Connect',
                username, tunnel_name,
                lan_ip=get_lan_ip(), wan_ip=wan_ip,
            )
        )
        asyncio.create_task(self._verify_after_connect(tunnel_name))
        return _ok()

    async def _start_tunnel(self, config_path: Path, tunnel_name: str,
                            endpoint_port_override: Optional[int] = None) -> tuple[bool, str]:
        """Every wg-quick up, including internal reconnects, shares this gate."""
        if self.backend.is_configured and not self.state.is_profile_allowed_to_connect(tunnel_name):
            return False, self.state.connection_denied_reason(tunnel_name)
        wg_path = configs_mod.prepare_config_path(config_path, endpoint_port_override=endpoint_port_override)
        self._tunnel_config_paths[tunnel_name] = config_path
        try:
            ok, output = await self.wg.connect(wg_path)
        finally:
            configs_mod.cleanup_temp_config(tunnel_name)
        # A heartbeat can withdraw access while the subprocess is starting.
        if ok and self.backend.is_configured and not self.state.is_profile_allowed_to_connect(tunnel_name):
            stopped, _ = await self._wg_down(tunnel_name)
            if stopped:
                self.state.set_disconnected(tunnel_name)
            else:
                self.state.set_connected(tunnel_name, 'Unknown', system_owned=True)
                log.error("Access withdrawn but tunnel '%s' could not be stopped", tunnel_name)
            return False, 'VPN authorization changed while connecting. Disconnect and refresh access.'
        return ok, output

    async def _verify_after_connect(self, tunnel_name: str) -> None:
        """Post-connect verification retry loop (mirrors Windows' TunnelVerifier, run right
        after PipeServer's Connect handler / cmd_connect): a sidecar profile with a gateway
        target probes /health through the tunnel, falling back to a handshake check; anything
        else goes straight to the handshake check.

        Retries every _VERIFY_FAST_INTERVAL_S for the first _VERIFY_FAST_WINDOW_S. If nothing
        has succeeded by then, marks the tunnel verification-failed (tray shows an amber
        "Confirmation failed" tag) but keeps retrying every _VERIFY_SLOW_INTERVAL_S for as long
        as the tunnel stays connected, rather than giving up permanently after a fixed budget --
        a real customer network was observed needing well over a minute for the WireGuard
        handshake to complete even though the same sidecar confirmed in seconds for other
        clients on the same network (a per-connection timing issue, not a server-side one). A
        later success still clears the failed flag and promotes to "Verified", no reconnect
        needed. Before this loop existed, IsVerified never became true until the next network
        change triggered a re-verify — so a freshly-connected tunnel would show as plain
        "Connected" indefinitely instead of getting the "Verified" badge.

        Also owns the port-443 fallback retry (docs/design/port-443-fallback.md §5, mirrors
        Windows TunnelVerifier): if the customer's admin-configured trigger window passes with
        no successful verification and this profile has a confirmed fallback port, rewrites the
        Endpoint port on a fresh temp copy of the config and reinstalls the tunnel once, then
        lets this same loop keep polling. Does not reset the fast/slow-window timing above."""
        gateway = self.state.get_gateway_probe(tunnel_name)
        start = datetime.utcnow().timestamp()
        failed_marked = False
        fallback_tried = False

        while self.state.is_connected(tunnel_name):
            ok = False
            via_gateway = False
            try:
                if gateway:
                    ip, port = gateway
                    attempt_deadline = datetime.utcnow().timestamp() + _VERIFY_ATTEMPT_BUDGET_S
                    if await self._probe_gateway_until(ip, port, attempt_deadline):
                        ok, via_gateway = True, True
                if not ok:
                    epoch = await self.wg.latest_handshake(tunnel_name)
                    now = int(datetime.utcnow().timestamp())
                    if epoch > 0 and now - epoch < 180:
                        ok = True
            except Exception as e:
                log.debug("Verification attempt failed for tunnel '%s' (retrying): %s", tunnel_name, e)

            if ok:
                self.state.set_verified(tunnel_name, via_gateway=via_gateway)
                log.info(
                    "Verification succeeded for tunnel '%s' (%s)%s.",
                    tunnel_name, 'gateway' if via_gateway else 'handshake',
                    ' after a prior confirmation failure' if failed_marked else '',
                )
                # Mirrors Windows' TunnelVerifier reporting a "Verified" event upstream —
                # required so the admin client list's "connected" star (which now demands
                # actual gateway verification for sidecar clients, not just Connect) ever
                # lights up for Linux. Deliberately only the gateway-verified branch reports;
                # a handshake-only pass does NOT, matching Windows (handshake freshness isn't
                # a reliable enough signal to gate a display indicator on).
                if via_gateway:
                    asyncio.create_task(
                        self.backend.log_event(
                            self.state.get_client_id(), 'Verified', '', tunnel_name,
                        )
                    )
                return

            if not fallback_tried:
                fallback = self.state.get_fallback_config(tunnel_name)
                if fallback and datetime.utcnow().timestamp() - start >= fallback[1]:
                    fallback_tried = True
                    await self._try_switch_to_fallback_port(tunnel_name, fallback[0], fallback[1])

            if not failed_marked and datetime.utcnow().timestamp() - start >= _VERIFY_FAST_WINDOW_S:
                failed_marked = True
                self.state.set_verification_failed(tunnel_name)
                log.warning(
                    "Confirmation failed for tunnel '%s' after %ds — still retrying in the background.",
                    tunnel_name, _VERIFY_FAST_WINDOW_S,
                )

            await asyncio.sleep(_VERIFY_SLOW_INTERVAL_S if failed_marked else _VERIFY_FAST_INTERVAL_S)

    async def _try_switch_to_fallback_port(self, tunnel_name: str, fallback_port: int, trigger_seconds: int) -> None:
        async with self._tunnel_mutation:
            if self.state.is_connected(tunnel_name):
                await self._switch_to_fallback_port(tunnel_name, fallback_port, trigger_seconds)

    async def _switch_to_fallback_port(self, tunnel_name: str, fallback_port: int, trigger_seconds: int) -> None:
        """Reinstalls tunnel_name against its UDP fallback port (already confirmed active by
        the caller -- state.get_fallback_config) after trigger_seconds passed with no
        successful verification. Runs at most once per connect (guarded by the caller's
        fallback_tried flag) -- a single alternate-port attempt, not a permanent pin; a later
        reconnect always starts on the primary port again. Mirrors Windows
        TunnelVerifier.TrySwitchToFallbackPortAsync."""
        stored = self._tunnel_config_paths.get(tunnel_name) or configs_mod.get_config_path_by_tunnel_name(tunnel_name)
        if stored is None:
            return

        log.info(
            "No successful verification for tunnel '%s' after %ds — retrying via UDP fallback port %d.",
            tunnel_name, trigger_seconds, fallback_port,
        )
        try:
            owner = self.state.get_tunnel(tunnel_name).connected_user or 'Unknown'
            system_owned = self.state.is_system_owned(tunnel_name)
            stopped, _ = await self._wg_down(tunnel_name)
            if not stopped:
                return
            self.state.set_disconnected(tunnel_name)
            ok, output = await self._start_tunnel(stored, tunnel_name, endpoint_port_override=fallback_port)
            if not ok:
                log.warning("Fallback-port reconnect failed for tunnel '%s': %s", tunnel_name, output)
                return
            self.state.set_connected(tunnel_name, owner, system_owned=system_owned)
            self.state.mark_used_fallback_port(tunnel_name, fallback_port)
            detail = f"Switched to UDP {fallback_port} after {trigger_seconds}s with no confirmation on the primary port."
            asyncio.create_task(
                self.backend.log_event(
                    self.state.get_client_id(), 'PortFallback', '', tunnel_name, detail=detail,
                )
            )
        except Exception as e:
            log.warning("Fallback-port reconnect threw for tunnel '%s': %s", tunnel_name, e)

    async def cmd_disconnect(self, username: str, profile_name: Optional[str] = None, *, privileged: bool = False) -> PipeResponse:
        async with self._tunnel_mutation:
            return await self._disconnect(username, profile_name, privileged=privileged)

    async def _disconnect(self, username: str, profile_name: Optional[str], *, privileged: bool) -> PipeResponse:
        from daemon.ipc_server import _ok, _fail
        if profile_name is not None and not configs_mod.is_valid_profile_name(profile_name):
            return _fail('Invalid profile name.')

        if profile_name:
            # Disconnect a specific named tunnel.
            if not self.state.is_connected(profile_name):
                return _fail(f"Tunnel '{profile_name}' is not active.")
            if not privileged and not self.state.can_control_tunnel(profile_name, username):
                return _fail("Not authorized to disconnect another user's tunnel.")
            ok, output = await self._wg_down(profile_name)
            if not ok:
                return _fail(f"Failed to stop tunnel: {output}")
            self.state.set_disconnected(profile_name)
            asyncio.create_task(
                self.backend.log_event(
                    self.state.get_client_id(), 'Disconnect', username, profile_name,
                )
            )
            return _ok()

        # No profile specified — disconnect the primary/server tunnel.
        tunnel = self.state.get_tunnel()
        if not tunnel.is_connected or not tunnel.tunnel_name:
            return _fail("No tunnel is currently active.")

        if not privileged and not self.state.can_control_tunnel(tunnel.tunnel_name, username):
            return _fail("Not authorized to disconnect another user's tunnel.")

        ok, output = await self._wg_down(tunnel.tunnel_name)
        if not ok:
            return _fail(f"Failed to stop tunnel: {output}")

        caller = tunnel.connected_user or username
        tname = tunnel.tunnel_name
        self.state.set_disconnected(tname)
        asyncio.create_task(
            self.backend.log_event(
                self.state.get_client_id(), 'Disconnect', caller, tname,
            )
        )
        return _ok()

    async def cmd_delete_profile(self, username: str, profile_name: Optional[str], *, privileged: bool = False) -> PipeResponse:
        from daemon.ipc_server import _ok, _fail
        async with self._tunnel_mutation:
            if not configs_mod.is_valid_profile_name(profile_name):
                return _fail('Invalid profile name.')
            if configs_mod.get_config_path(username, profile_name) is None:
                return _fail(f"Profile '{profile_name}' not found.")
            if configs_mod.is_managed(username, profile_name):
                return _fail(f"'{profile_name}' is managed by the administrator.")
            if self.state.is_connected(profile_name):
                result = await self._disconnect(username, profile_name, privileged=privileged)
                if not result.Success:
                    return result
            if not configs_mod.delete_user_profile(username, profile_name):
                return _fail(f"Profile '{profile_name}' not found.")
            return _ok()

    async def _wg_down(self, tunnel_name: str) -> tuple[bool, str]:
        """`wg-quick down` re-reads the same config file `up` used (for its PostDown
        and DNS-restore directives) — it isn't purely a kernel-interface teardown.
        We delete the decrypted temp copy immediately after `up` succeeds (see
        prepare_config_path), so without this, `down` fails outright: passed just
        a bare tunnel name, wg-quick searches /etc/wireguard/<name>.conf, which
        never existed, and reports a plain "does not exist" error. Re-decrypt the
        stored config to a temp path and pass that explicit path instead — same
        pattern already used for `up`."""
        stored = self._tunnel_config_paths.get(tunnel_name) or configs_mod.get_config_path_by_tunnel_name(tunnel_name)
        if stored is None:
            return await self.wg.disconnect(tunnel_name)
        try:
            wg_path = configs_mod.prepare_config_path(stored)
        except ValueError as e:
            # A config that fails the safety allowlist (e.g. a pre-fix tunnel carrying
            # PostDown hooks) must not be fed to wg-quick — that would run the hook as
            # root. Fall back to a bare-name teardown, which never reads/executes it.
            log.warning("Not decrypting '%s' for teardown: %s", tunnel_name, e)
            return await self.wg.disconnect(tunnel_name)
        try:
            result = await self.wg.disconnect(str(wg_path))
            if result[0]:
                self._tunnel_config_paths.pop(tunnel_name, None)
            return result
        finally:
            configs_mod.cleanup_temp_config(tunnel_name)

    async def cmd_mfa_enroll_confirm(self, username: str, code: str) -> PipeResponse:
        from daemon.ipc_server import _ok, _fail
        ok = await self.backend.confirm_mfa_enrollment(self.state.get_client_id(), code)
        if ok:
            asyncio.create_task(self.heartbeat_once())
        return _ok() if ok else _fail("That code was not accepted. Please try again.")

    async def cmd_check_update(self) -> VersionCheckResult:
        from daemon.backend import VERSION
        available, latest, _url, _sha = await self.updater.check()
        self.state.set_update_available(available)
        if available:
            asyncio.create_task(self.updater.check_and_apply())
        return VersionCheckResult(
            UpdateAvailable=available,
            CurrentVersion=VERSION,
            LatestVersion=latest,
        )

    async def cmd_set_backend_url(self, dns: Optional[str], *, privileged: bool = False) -> PipeResponse:
        from daemon.ipc_server import _fail
        if not privileged:
            return _fail('An administrator must provision this machine. Use the root provisioning command or installer configuration.')
        async with self._provisioning_lock:
            return await self._provision_backend(dns)

    async def _provision_backend(self, dns: Optional[str]) -> PipeResponse:
        """First-run: set the backend URL from a user-entered DNS (the tray prompt shown when the
        installer provided none). Persists it (always — "save anyway"), probes reachability, and
        kicks a registration so activation starts immediately. Mirrors the Windows
        ClientRegistrationService.SetBackendUrlAsync."""
        from daemon.ipc_server import _fail

        normalized = self.backend.set_base_url(dns, privileged=True)
        if not normalized:
            return _fail("Enter a valid server address, for example vpn.example.com")

        # Best-effort reachability probe against the public version endpoint (no API key needed).
        warning: Optional[str] = None
        try:
            if await self.backend.get_version() is None:
                warning = ("Saved, but the server could not be reached yet. "
                           "Valenius will keep trying to connect in the background.")
        except Exception:
            warning = ("Saved, but the server could not be reached yet. "
                       "Valenius will keep trying to connect in the background.")

        # Start registering right away rather than waiting for the next heartbeat cycle.
        asyncio.create_task(self.heartbeat_once())
        return PipeResponse(Success=True, Error=warning)

    async def cmd_set_standalone_mode(self, *, privileged: bool = False) -> PipeResponse:
        """First-run "Use standalone" choice: no backend contacted, just remember the
        decision so the tray stops prompting and shows the normal popup. Mirrors Windows
        PipeServer's SetStandaloneMode case."""
        if not privileged:
            return PipeResponse(Success=False, Error='An administrator must provision this machine.')
        async with self._provisioning_lock:
            if self.backend.is_configured:
                return PipeResponse(Success=False, Error='A managed client cannot switch to standalone mode through IPC.')
            self.state.set_standalone_mode_chosen(True)
            if not reg_mod.save(self.state):
                return PipeResponse(Success=False, Error='Could not persist standalone provisioning; repair storage before restart.')
        return PipeResponse(Success=True)

    async def cmd_register(self, username: str) -> RegistrationResult:
        ok, msg = await self._live_register(username=username)
        message = {
            True:  "VPN access is active.",
            False: "Registration submitted. Contact your administrator to activate this machine.",
            None:  "Could not reach the registration server. Please try again later.",
        }[ok]
        return RegistrationResult(IsActive=bool(ok), Message=message)

    # ── Heartbeat ────────────────────────────────────────────────────────────

    async def heartbeat_once(self, username: str = '') -> None:
        # No backend URL yet: nothing to call. The tray is prompting the user for it
        # (BackendUnconfigured); skip quietly instead of hammering an empty URL.
        if not self.backend.is_configured:
            return
        client_id = self.state.get_client_id()
        hostname = socket.gethostname()
        all_profiles = configs_mod.get_all_profiles()
        resp = await self.backend.register(
            client_id, hostname, username or hostname, all_profiles,
            tray_running=self.state.is_tray_running(),
        )
        if resp is None:
            self.state.set_backend_reachable(False)
            return
        await self._process_status_response(resp, username)

    async def _process_status_response(self, resp: dict, username: str = '') -> None:
        """Shared by heartbeat_once and poll_loop (mirrors Windows'
        ProcessStatusResponseAsync, called from both TryRegisterAsync and the
        long-poll loop) so admin actions apply identically regardless of which
        request delivered them."""
        self.state.set_backend_reachable(True)
        self.state.apply_backend_status(resp)
        reg_mod.save(self.state)
        is_active = self.state.is_registration_active()

        # A standalone client that later configured a backend and got activated: bulk-
        # transfer its local unmanaged profiles once. Mirrors Windows
        # ClientRegistrationService.ProcessStatusResponseAsync's TransferLocalProfilesAsync
        # trigger.
        if is_active and self.state.get_standalone_mode_chosen() and not self.state.get_profiles_transferred():
            asyncio.create_task(self._transfer_local_profiles())

        ac_enabled = resp.get('AutoConnectEnabled', resp.get('autoConnectEnabled', False))
        ac_profile = resp.get('AutoConnectProfileName', resp.get('autoConnectProfileName'))
        networks_raw = resp.get('TrustedNetworks', resp.get('trustedNetworks')) or []
        networks = [
            {
                'Subnet': n.get('Subnet', n.get('subnet', '')),
                'PrimaryDns': n.get('PrimaryDns', n.get('primaryDns')),
            }
            for n in networks_raw
        ]
        self.auto_connect.update_from_backend(ac_enabled, ac_enabled, ac_profile, networks)
        self.persist_auto_connect()

        self.state.set_heartbeat_interval(resp.get('HeartbeatIntervalMinutes', resp.get('heartbeatIntervalMinutes', 5)))
        server_profile_name = self.state.get_server_profile_name()
        # Device tunnel: connect the native profile before any login (root daemon, not tied
        # to a session). Runs on every heartbeat/poll regardless of login state, so the
        # machine-level config store (config_manager.DEVICE_DIR) stays populated even on a
        # never-logged-into machine. Mirrors Windows ClientRegistrationService's
        # EnsureDeviceTunnelConfigAsync call site.
        device_tunnel_enabled = self.state.get_device_tunnel_enabled()
        if device_tunnel_enabled and server_profile_name:
            asyncio.create_task(self._ensure_device_tunnel_config(server_profile_name))
        # Auto-update channel (stable|beta) — the updater polls the matching Linux release stream.
        self.backend.set_channel(resp.get('UpdateChannel', resp.get('updateChannel', 'stable')))

        # Backend API-key rotation: adopt the new key (persisted) so subsequent requests
        # authenticate with it. Only present while this client is still on the previous key.
        self.backend.set_api_key(resp.get('ClientApiKey', resp.get('clientApiKey')))

        # Gateway, route/fallback metadata and MFA were applied atomically with
        # activation above, before persistence or any network operation.

        # Admin pressed "Update now" — run the check immediately.
        if resp.get('ForceUpdate', resp.get('forceUpdate', False)):
            asyncio.create_task(self.updater.check_and_apply())

        # Admin requested a diagnostic log upload — collect + send (Valenius-only, redacted).
        if resp.get('LogUploadRequested', resp.get('logUploadRequested', False)):
            asyncio.create_task(self._upload_logs('Admin'))

        # Pending config (native sidecar).
        if resp.get('HasPendingConfig', resp.get('hasPendingConfig', False)):
            await self._fetch_pending_config()

        # Inline foreign configs (Pro multi-customer feature).
        for entry in resp.get('PendingForeignConfigs', resp.get('pendingForeignConfigs') or []):
            filename = entry.get('FileName', entry.get('fileName', ''))
            content = entry.get('Content', entry.get('content', ''))
            if filename and content:
                configs_mod.save_staged_config(filename, content)
                log.info("Staged foreign config: %s", filename)

        # Backend-initiated connect/disconnect for the server/primary tunnel only.
        # PendingConnect/PendingDisconnect are plain bools (StatusResponse), not tunnel
        # names — resolve the actual tunnel via get_server_profile_name(), same as MfaRequired below.
        if resp.get('PendingConnect', resp.get('pendingConnect', False)):
            server_profile = self.state.get_server_profile_name()
            if server_profile and not self.state.is_connected(server_profile):
                asyncio.create_task(self.cmd_connect('PendingConnect', server_profile, privileged=True))

        if resp.get('PendingDisconnect', resp.get('pendingDisconnect', False)):
            server_profile = self.state.get_server_profile_name()
            if server_profile and self.state.is_connected(server_profile):
                asyncio.create_task(self.cmd_disconnect('PendingDisconnect', server_profile, privileged=True))

        # MFA session expired (the gate returned) while the gated tunnel is still up: the sidecar
        # already dropped the peer server-side, so the local tunnel is dead but still active and
        # would linger showing "connected". Tear down the primary/server tunnel so local state
        # matches the server and the user must re-authorize. Foreign tunnels aren't gated here.
        if resp.get('MfaRequired', resp.get('mfaRequired', False)):
            server_profile = self.state.get_server_profile_name()
            if server_profile and self.state.is_connected(server_profile):
                asyncio.create_task(self.cmd_disconnect('MfaSessionExpired', server_profile, privileged=True))

        # Handle pending delete.
        delete_name = resp.get('PendingDeleteProfileName', resp.get('pendingDeleteProfileName'))
        if delete_name:
            self._handle_pending_delete(delete_name)

        # Last, and deliberately so: this tears the process down, so anything queued above
        # must have been started first. The one-shot flag is already cleared server-side, and
        # the restart itself waits a few seconds before stopping the service, so this response
        # finishes processing normally rather than being cut off mid-way. Mirrors Windows
        # ClientRegistrationService.ProcessStatusResponseAsync's ServiceRestartRequested check.
        if resp.get('ServiceRestartRequested', resp.get('serviceRestartRequested', False)):
            log.info("Service restart requested by admin — scheduling a restart of the valenius service.")
            asyncio.create_task(self._request_service_restart())

    async def _transfer_local_profiles(self) -> None:
        """One-time bulk archive of every local unmanaged profile once a client that was
        standalone configures a backend and activates. Guarded by state.get_profiles_transferred()
        (persisted) so a partial failure retries the whole batch on the next heartbeat/poll
        rather than silently dropping profiles, and by _transfer_in_progress so a heartbeat/poll
        race doesn't fire it twice concurrently. Deliberately does NOT mark profiles managed —
        they stay private/user-deletable, exactly like a self-uploaded profile on an
        always-connected client. Mirrors Windows ClientRegistrationService.TransferLocalProfilesAsync."""
        if self._transfer_in_progress:
            return
        self._transfer_in_progress = True
        try:
            profiles = configs_mod.get_all_unmanaged_profiles_with_content()
            if not profiles:
                self.state.set_profiles_transferred(True)
                reg_mod.save(self.state)
                return

            client_id = self.state.get_client_id()
            all_ok = True
            for _username, profile_name, content in profiles:
                ok = await self.backend.archive_profile(client_id, profile_name, content)
                if not ok:
                    all_ok = False
                    log.warning("Local profile transfer: failed to archive '%s' — will retry.", profile_name)

            if all_ok:
                self.state.set_profiles_transferred(True)
                reg_mod.save(self.state)
                log.info("Transferred %d local profile(s) to the backend.", len(profiles))
        finally:
            self._transfer_in_progress = False

    async def _ensure_device_tunnel_config(self, profile_name: str) -> None:
        """Best-effort: pulls GET /api/clients/profiles and saves the entry matching
        profile_name into the machine-level device-tunnel store, so the config is available
        even on a machine nobody has ever logged into (no per-user copy exists yet). Runs on
        every heartbeat/poll while device tunnel is enabled. Mirrors Windows
        ClientRegistrationService.EnsureDeviceTunnelConfigAsync."""
        try:
            entries = await self.backend.get_profiles(self.state.get_client_id())
            if not entries:
                return
            for entry in entries:
                filename = entry.get('FileName', entry.get('fileName', ''))
                if filename.lower() == profile_name.lower():
                    content = entry.get('Content', entry.get('content', ''))
                    if content:
                        configs_mod.save_device_tunnel_config(profile_name, content)
                    return
        except Exception as e:
            log.debug("Device tunnel config sync for '%s' failed (will retry): %s", profile_name, e)

    async def _request_service_restart(self) -> None:
        """Admin recovery hatch for stale in-memory state (e.g. a tunnel removed behind the
        daemon's back, leaving it reporting Connected with no tunnel present, which a
        non-admin user has no way back from). Waits a few seconds so the heartbeat/poll
        response that requested this finishes processing and the server-side one-shot flag
        stays cleared, then restarts the systemd service via a DETACHED transient unit --
        fire-and-forget, no --pipe/--wait, since this action is about to kill the very
        process issuing it (mirrors daemon/updater.py's self-update install, which solves the
        identical problem). reconcile_active_tunnels() re-adopts any live kernel interface at
        the fresh daemon's startup, so this alone also clears the stale-state scenario above --
        no separate phantom-state fix is needed. Mirrors Windows ServiceRestarter."""
        import subprocess
        await asyncio.sleep(3)
        try:
            result = await self._loop.run_in_executor(None, lambda: subprocess.run(
                ['systemd-run', '--collect', '--quiet',
                 '--unit', 'valenius-remote-restart',
                 '--property', 'StandardOutput=journal',
                 '--property', 'StandardError=journal',
                 '--', 'systemctl', 'restart', 'valenius.service'],
                capture_output=True, text=True,
            ))
            if result.returncode != 0:
                log.warning(
                    "Remote service restart: systemd-run failed (exit %d): %s",
                    result.returncode, result.stderr,
                )
        except Exception as e:
            log.warning("Remote service restart failed: %s", e)

    async def _upload_logs(self, trigger: str) -> None:
        """Collect the (Valenius-only, redacted) diagnostic bundle and upload it. Called on an
        admin heartbeat request ("Admin") and by the tray "Send logs" action ("User")."""
        try:
            from daemon import diagnostics
            gz = await self._loop.run_in_executor(
                None, lambda: diagnostics.collect_bundle(self.backend.api_key))
            ok = await self.backend.upload_logs(self.state.get_client_id(), gz, trigger)
            log.info("Diagnostic logs uploaded (%s, %d bytes): %s", trigger, len(gz), ok)
        except Exception as e:
            log.warning("Diagnostic log upload (%s) failed: %s", trigger, e)

    async def heartbeat_loop(self) -> None:
        await asyncio.sleep(5)  # short initial delay
        while True:
            try:
                await self.heartbeat_once()
            except Exception as e:
                log.error("Heartbeat failed: %s", e)
            interval = self.state.get_heartbeat_interval()
            await asyncio.sleep(interval * 60)

    async def poll_loop(self) -> None:
        """Continuously long-polls GET /api/clients/poll (mirrors Windows'
        RunLongPollLoopAsync). The backend holds each request for up to 55 s and
        returns immediately when an admin action changes this client's state, so
        activation, pending configs, connect/disconnect, MFA, etc. apply within
        ~1 s instead of waiting for the next heartbeat_loop cycle (up to 60 min)."""
        await asyncio.sleep(10)  # let the heartbeat register first
        while True:
            try:
                # Not configured yet — don't long-poll an empty URL; wait for the tray
                # prompt to set it (SetBackendUrl), then the next iteration picks it up.
                if not self.backend.is_configured:
                    await asyncio.sleep(5)
                    continue
                resp = await self.backend.poll(
                    self.state.get_client_id(),
                    tray_running=self.state.is_tray_running(),
                )
                if resp is not None:
                    await self._process_status_response(resp)
                # None (network error, 404 before first registration, etc.): the
                # heartbeat loop will re-register: just retry after a short backoff.
                else:
                    self.state.set_backend_reachable(False)
                    await asyncio.sleep(15)
            except Exception as e:
                log.debug("Long poll failed: %s", e)
                self.state.set_backend_reachable(False)
                await asyncio.sleep(15)

    # ── Auto-connect triggers ────────────────────────────────────────────────

    async def run_startup_auto_connect_check(self) -> None:
        """Applies the auto-connect policy shortly after daemon start, retrying if the first
        attempt doesn't result in a connection. This is the path that reconnects the tunnel
        after a service restart (a normal boot, or the restart a self-update triggers) --
        wg-quick tunnels never come back up on their own. A single attempt right after boot
        can lose a race with the network stack settling (DHCP/default route/DNS); mirrors
        Windows' AutoConnectService.RunStartupCheckAsync."""
        await asyncio.sleep(_STARTUP_INITIAL_DELAY_S)
        for attempt in range(1, _STARTUP_RETRY_ATTEMPTS + 1):
            await self._handle_network_change()
            if not self._is_auto_connect_pending():
                return
            if attempt < _STARTUP_RETRY_ATTEMPTS:
                log.debug("Auto-connect startup check attempt %d did not connect -- retrying.", attempt)
                await asyncio.sleep(_STARTUP_RETRY_INTERVAL_S)

    def _get_auto_connect_targets(self) -> list[tuple[str, Optional[str]]]:
        """Enumerate active auto-connect targets: the generic admin-configured profile (when
        enabled), plus the device tunnel's native profile (when the backend has it enabled for
        this client). Evaluated fresh on every check. Mirrors Windows
        AutoConnectService.GetTargets."""
        targets: list[tuple[str, Optional[str]]] = []
        if self.auto_connect.is_enabled():
            profile = self.auto_connect.get_profile_name() or self.state.get_server_profile_name()
            targets.append(('AutoConnect', profile))
        if self.state.get_device_tunnel_enabled():
            targets.append(('DeviceTunnel', self.state.get_server_profile_name()))
        return targets

    def _is_auto_connect_pending(self) -> bool:
        """True if any auto-connect target still has something to do: trusted networks
        configured, current network not trusted, and that target's profile not yet connected.
        Mirrors AutoConnectService.IsAutoConnectPending -- used only to decide whether the
        startup retry loop should keep going."""
        targets = self._get_auto_connect_targets()
        if not targets:
            return False
        networks = self.auto_connect.get_trusted_networks()
        if not networks:
            return False
        if is_in_trusted_network(networks):
            return False
        return any(not (profile and self.state.is_connected(profile)) for _label, profile in targets)

    def on_network_change(self) -> None:
        """Called from the NetworkMonitor thread on every network transition."""
        asyncio.run_coroutine_threadsafe(self._handle_network_change(), self._loop)

    async def _handle_network_change(self) -> None:
        """Re-verify active tunnels (always), then apply the auto-connect policy.

        The settle delay lets the new network path come up (DHCP, default route,
        WireGuard endpoint roam) before we probe — mirrors the Windows client. A
        guard collapses overlapping events so rapid transitions don't pile up.
        """
        if self._handling_network_change:
            return
        self._handling_network_change = True
        try:
            await asyncio.sleep(_REVERIFY_SETTLE_S)
            try:
                await self._reverify_active_tunnels()
            except Exception as e:
                log.warning("Re-verify after network change failed: %s", e)
            await self._auto_connect_action()
        finally:
            self._handling_network_change = False

    async def _reverify_active_tunnels(self) -> None:
        """Re-confirm each active tunnel after a network change; tear down only the ones
        we can be confident are dead.

        The trusted "dead" signal is a tunnel that was verified end-to-end via the
        gateway /health probe and whose probe now fails for the whole budget. Tunnels
        without a gateway target (foreign profiles, plain uploaded configs) are left
        connected — a stale handshake can't tell "dead" from "idle/roaming", so tearing
        them down would risk dropping a working VPN. The verified flag is refreshed
        either way. Mirrors the Windows AutoConnectService re-verifier.
        """
        active = self.state.get_all_connected()
        if not active:
            return

        deadline = datetime.utcnow().timestamp() + _REVERIFY_BUDGET_S

        for tunnel in active:
            name = tunnel.Name
            was_via_gateway = self.state.is_verified_via_gateway(name)
            gateway = self.state.get_gateway_probe(name)
            self.state.set_unverified(name)

            if gateway:
                ip, port = gateway
                if await self._probe_gateway_until(ip, port, deadline):
                    self.state.set_verified(name, via_gateway=True)
                    log.debug("Re-verify: tunnel '%s' gateway-confirmed after network change", name)
                    continue
                if was_via_gateway:
                    log.warning(
                        "Re-verify: tunnel '%s' gateway unreachable after network change — disconnecting", name)
                    await self.cmd_disconnect(tunnel.ConnectedUser or 'ConnectionLost', name, privileged=True)
                    continue
                log.debug(
                    "Re-verify: gateway probe for '%s' failed but it was never gateway-verified "
                    "(health likely firewalled) — keeping it", name)

            # Non-gateway (or firewalled-health) tunnels: best-effort handshake refresh, no teardown.
            epoch = await self.wg.latest_handshake(name)
            now = int(datetime.utcnow().timestamp())
            if epoch > 0 and now - epoch < 180:
                self.state.set_verified(name)

    async def _probe_gateway_until(self, ip: str, port: int, deadline: float) -> bool:
        """Poll the gateway /health endpoint until it answers 200 or the deadline passes."""
        from daemon.backend import probe_gateway_health
        while datetime.utcnow().timestamp() < deadline:
            if await probe_gateway_health(ip, port, self._loop):
                return True
            await asyncio.sleep(2)
        return False

    async def _auto_connect_action(self) -> None:
        """Apply the trusted-network auto-connect/disconnect policy to every active target
        (the generic admin-configured profile, plus the device tunnel when enabled) --
        mirrors Windows AutoConnectService.CheckAsync. Cooldown is tracked per target label so
        one target's action never blocks the other's in the same check."""
        targets = self._get_auto_connect_targets()
        if not targets:
            return
        networks = self.auto_connect.get_trusted_networks()
        if not networks:
            return

        in_trusted = is_in_trusted_network(networks)
        now = datetime.utcnow()

        for label, profile in targets:
            if now - self._last_auto_action.get(label, datetime.min) < _ACTION_COOLDOWN:
                continue
            is_up = self.state.is_connected(profile) if profile else self.state.is_connected()

            if not is_up and not in_trusted:
                log.info("Auto-connect (%s): outside trusted network, connecting", label)
                self._last_auto_action[label] = now
                await self.cmd_connect(label, profile, privileged=True)
            elif is_up and in_trusted:
                log.info("Auto-connect (%s): inside trusted network, disconnecting", label)
                self._last_auto_action[label] = now
                await self.cmd_disconnect(label, profile, privileged=True)

    # ── Private helpers ──────────────────────────────────────────────────────

    async def _live_register(self, username: str = '') -> tuple[Optional[bool], Optional[str]]:
        if not self.backend.is_configured:
            return None, "No server address configured yet. Set it in Valenius first."
        try:
            hostname = socket.gethostname()
            resp = await self.backend.register(
                self.state.get_client_id(), hostname,
                username or hostname,
                configs_mod.get_all_profiles(),
                tray_running=self.state.is_tray_running(),
            )
            if resp is None:
                self.state.set_backend_reachable(False)
                return None, "Registration server unreachable."
            await self._process_status_response(resp, username)
            return self.state.is_registration_active(), None
        except Exception as e:
            self.state.set_backend_reachable(False)
            return None, str(e)

    async def _fetch_pending_config(self) -> None:
        data = await self.backend.get_pending_config(self.state.get_client_id())
        if data is None:
            return
        filename = data.get('FileName', data.get('fileName', ''))
        content = data.get('Content', data.get('content', ''))
        if filename and content:
            configs_mod.save_staged_config(filename, content)
            log.info("Staged config: %s", filename)

    def _handle_pending_delete(self, profile_name: str) -> None:
        asyncio.create_task(self._disconnect_and_delete(profile_name))

    async def _disconnect_and_delete(self, profile_name: str) -> None:
        async with self._tunnel_mutation:
            if self.state.is_connected(profile_name):
                ok, _ = await self._wg_down(profile_name)
                if not ok:
                    return
                self.state.set_disconnected(profile_name)
            configs_mod.delete_profile(profile_name)

    async def _reconnect_if_replaced(self, username: str, staged_names: list[str], *, privileged: bool = False) -> None:
        async with self._tunnel_mutation:
            await self._reconnect_replaced(username, staged_names, privileged=privileged)

    async def _reconnect_replaced(self, username: str, staged_names: list[str], *, privileged: bool) -> None:
        """If an active tunnel's config was just replaced, reconnect to pick up new keys."""
        for staged_name in staged_names:
            if not self.state.is_connected(staged_name):
                continue
            if not privileged and not self.state.can_control_tunnel(staged_name, username):
                continue
            ok, _ = await self._wg_down(staged_name)
            if not ok:
                continue
            self.state.set_disconnected(staged_name)
            config_path = configs_mod.get_config_path(username, staged_name)
            if config_path:
                wan_ip = await self.backend.get_wan_ip()
                try:
                    ok, _ = await self._start_tunnel(config_path, staged_name)
                except ValueError as e:
                    log.warning("Not reconnecting '%s': %s", staged_name, e)
                    continue
                if ok:
                    self.state.set_connected(staged_name, username, system_owned=privileged)
                    asyncio.create_task(
                        self.backend.log_event(
                            self.state.get_client_id(), 'Connect',
                            username, staged_name,
                            lan_ip=get_lan_ip(), wan_ip=wan_ip,
                        )
                    )

    def persist_auto_connect(self) -> None:
        from daemon.config_manager import AUTOCONNECT_FILE
        try:
            self.auto_connect.save(AUTOCONNECT_FILE)
        except Exception as e:
            log.error("Could not persist autoconnect config: %s", e)
