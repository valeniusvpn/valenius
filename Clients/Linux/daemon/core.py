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

# Minimum gap between auto-connect actions, to prevent flapping (mirrors Windows).
_ACTION_COOLDOWN = timedelta(seconds=60)


def _health_port_from_url(url: Optional[str]) -> int:
    """Port from a ServerHealthUrl, defaulting to 8443 (mirrors the Windows client)."""
    if url:
        try:
            from urllib.parse import urlparse
            parsed = urlparse(url)
            if parsed.port:
                return parsed.port
        except Exception:
            pass
    return 8443


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
        self._last_auto_action = datetime.min
        self._handling_network_change = False

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
            # 'Unknown' — real owner is unrecoverable across a restart, so this is
            # listed alongside 'AutoConnect'/'PendingConnect' in cmd_disconnect's
            # system_tunnel bypass: no legitimate "wrong owner" to protect against.
            self.state.set_connected(name, 'Unknown')
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
            # No backend URL yet (fresh install, installer provided none): the tray prompts
            # the user for the server DNS. False whenever a URL is configured.
            BackendUnconfigured=not self.backend.is_configured,
            # Installed package version (the daemon is restarted by postinst on
            # update, so this reflects the on-disk code). The tray compares it to
            # its own in-memory VERSION and self-restarts on a mismatch, so an
            # auto-update refreshes the tray UI without a logout/login.
            DaemonVersion=VERSION,
        )

    # ── Commands ─────────────────────────────────────────────────────────────

    async def cmd_connect(self, username: str, profile_name: Optional[str]) -> PipeResponse:
        from daemon.ipc_server import _ok, _fail

        if not self.state.is_allowed_to_connect():
            ok, error = await self._live_register()
            if not ok:
                cid = self.state.get_client_id()
                msg = f"Not authorized — activate key {cid} in the admin panel."
                return _fail(error or msg)

        configs_mod.claim_staged_config(username)

        if profile_name:
            config_path = configs_mod.get_config_path(username, profile_name)
            if config_path is None:
                return _fail(f"Profile '{profile_name}' not found.")
        else:
            config_path = configs_mod.get_first_config_path(username)
            if config_path is None:
                return _fail("No config found. Please upload a WireGuard .conf file first.")

        tunnel_name = configs_mod.profile_name_from_path(config_path)
        if not tunnel_name:
            return _fail(f"Could not determine tunnel name from config path: {config_path}")

        # Already connected to this tunnel — nothing to do.
        if self.state.is_connected(tunnel_name):
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

        wan_ip = await self.backend.get_wan_ip()
        try:
            wg_path = configs_mod.prepare_config_path(config_path)
        except ValueError as e:
            return _fail(str(e))
        ok, output = await self.wg.connect(wg_path)
        configs_mod.cleanup_temp_config(tunnel_name)
        if not ok:
            return _fail(f"Failed to start tunnel: {output}")

        self.state.set_connected(tunnel_name, username)
        asyncio.create_task(
            self.backend.log_event(
                self.state.get_client_id(), 'Connect',
                username, tunnel_name,
                lan_ip=get_lan_ip(), wan_ip=wan_ip,
            )
        )
        asyncio.create_task(self._verify_after_connect(tunnel_name))
        return _ok()

    async def _verify_after_connect(self, tunnel_name: str) -> None:
        """Post-connect verification (mirrors Windows' RunPhase2Async, called right
        after PipeServer's Connect handler): a sidecar profile with a gateway target
        probes /health through the tunnel; anything else falls back to a handshake
        check. Without this, IsVerified never becomes true until the next network
        change triggers a re-verify — so a freshly-connected tunnel would show as
        plain "Connected" indefinitely instead of getting the "Verified" badge."""
        gateway = self.state.get_gateway_probe(tunnel_name)
        if gateway:
            ip, port = gateway
            deadline = datetime.utcnow().timestamp() + _REVERIFY_BUDGET_S
            if await self._probe_gateway_until(ip, port, deadline):
                self.state.set_verified(tunnel_name, via_gateway=True)
                return
        for _ in range(5):
            if not self.state.is_connected(tunnel_name):
                return
            epoch = await self.wg.latest_handshake(tunnel_name)
            now = int(datetime.utcnow().timestamp())
            if epoch > 0 and now - epoch < 180:
                self.state.set_verified(tunnel_name)
                return
            await asyncio.sleep(2)

    async def cmd_disconnect(self, username: str, profile_name: Optional[str] = None) -> PipeResponse:
        from daemon.ipc_server import _ok, _fail

        if profile_name:
            # Disconnect a specific named tunnel.
            if not self.state.is_connected(profile_name):
                return _fail(f"Tunnel '{profile_name}' is not active.")
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

        system_tunnel = tunnel.connected_user in ('AutoConnect', 'PendingConnect', 'Unknown')
        if not system_tunnel and tunnel.connected_user and \
                tunnel.connected_user.lower() != username.lower():
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

    async def _wg_down(self, tunnel_name: str) -> tuple[bool, str]:
        """`wg-quick down` re-reads the same config file `up` used (for its PostDown
        and DNS-restore directives) — it isn't purely a kernel-interface teardown.
        We delete the decrypted temp copy immediately after `up` succeeds (see
        prepare_config_path), so without this, `down` fails outright: passed just
        a bare tunnel name, wg-quick searches /etc/wireguard/<name>.conf, which
        never existed, and reports a plain "does not exist" error. Re-decrypt the
        stored config to a temp path and pass that explicit path instead — same
        pattern already used for `up`."""
        stored = configs_mod.get_config_path_by_tunnel_name(tunnel_name)
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
            return await self.wg.disconnect(str(wg_path))
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

    async def cmd_set_backend_url(self, dns: Optional[str]) -> PipeResponse:
        """First-run: set the backend URL from a user-entered DNS (the tray prompt shown when the
        installer provided none). Persists it (always — "save anyway"), probes reachability, and
        kicks a registration so activation starts immediately. Mirrors the Windows
        ClientRegistrationService.SetBackendUrlAsync."""
        from daemon.ipc_server import _fail

        normalized = self.backend.set_base_url(dns)
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
        is_active = resp.get('IsActive', resp.get('isActive', False))
        self.state.update_registration(is_active)

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
        self.state.set_server_profile_name(resp.get('ServerProfileName', resp.get('serverProfileName')))
        # Auto-update channel (stable|beta) — the updater polls the matching Linux release stream.
        self.backend.set_channel(resp.get('UpdateChannel', resp.get('updateChannel', 'stable')))

        # Backend API-key rotation: adopt the new key (persisted) so subsequent requests
        # authenticate with it. Only present while this client is still on the previous key.
        self.backend.set_api_key(resp.get('ClientApiKey', resp.get('clientApiKey')))

        # Gateway /health target for the native server tunnel — used to re-verify the
        # tunnel through the VPN after a network change.
        server_vpn_ip = resp.get('ServerVpnIp', resp.get('serverVpnIp'))
        server_health_url = resp.get('ServerHealthUrl', resp.get('serverHealthUrl'))
        self.state.set_server_gateway(server_vpn_ip, _health_port_from_url(server_health_url))

        # MFA state — pass through to tray via build_tunnel_status.
        mfa_expires_raw = resp.get('MfaSessionExpiresAt', resp.get('mfaSessionExpiresAt'))
        self.state.set_mfa_state(
            required=resp.get('MfaRequired', resp.get('mfaRequired', False)),
            authorize_url=resp.get('MfaAuthorizeUrl', resp.get('mfaAuthorizeUrl')),
            session_expires_at=mfa_expires_raw.isoformat() if isinstance(mfa_expires_raw, datetime) else mfa_expires_raw,
            enrollment_open=resp.get('MfaEnrollmentOpen', resp.get('mfaEnrollmentOpen', False)),
            enrollment_uri=resp.get('MfaEnrollmentUri', resp.get('mfaEnrollmentUri')),
            approve_number=resp.get('MfaApproveNumber', resp.get('mfaApproveNumber')),
        )

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
        pending_connect = resp.get('PendingConnect', resp.get('pendingConnect'))
        if pending_connect and not self.state.is_connected(pending_connect):
            asyncio.create_task(self.cmd_connect('PendingConnect', pending_connect))

        pending_disconnect = resp.get('PendingDisconnect', resp.get('pendingDisconnect'))
        if pending_disconnect and self.state.is_connected(pending_disconnect):
            asyncio.create_task(self.cmd_disconnect('PendingDisconnect', pending_disconnect))

        # MFA session expired (the gate returned) while the gated tunnel is still up: the sidecar
        # already dropped the peer server-side, so the local tunnel is dead but still active and
        # would linger showing "connected". Tear down the primary/server tunnel so local state
        # matches the server and the user must re-authorize. Foreign tunnels aren't gated here.
        if resp.get('MfaRequired', resp.get('mfaRequired', False)):
            server_profile = self.state.get_server_profile_name()
            if server_profile and self.state.is_connected(server_profile):
                asyncio.create_task(self.cmd_disconnect('MfaSessionExpired', server_profile))

        # Handle pending delete.
        delete_name = resp.get('PendingDeleteProfileName', resp.get('pendingDeleteProfileName'))
        if delete_name:
            self._handle_pending_delete(delete_name)

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
                    ok, output = await self._wg_down(name)
                    if not ok:
                        log.warning("Re-verify: disconnect of '%s' failed: %s", name, output)
                        continue
                    self.state.set_disconnected(name)
                    asyncio.create_task(
                        self.backend.log_event(
                            self.state.get_client_id(), 'Disconnect',
                            tunnel.ConnectedUser or 'ConnectionLost', name,
                        )
                    )
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
        """Apply the trusted-network auto-connect/disconnect policy (when enabled)."""
        if not self.auto_connect.is_enabled():
            return
        networks = self.auto_connect.get_trusted_networks()
        if not networks:
            return

        now = datetime.utcnow()
        if now - self._last_auto_action < _ACTION_COOLDOWN:
            return

        in_trusted = is_in_trusted_network(networks)
        server_profile = self.auto_connect.get_profile_name() or self.state.get_server_profile_name()
        is_server_up = self.state.is_connected(server_profile) if server_profile else self.state.is_connected()

        if not is_server_up and not in_trusted:
            log.info("Auto-connect: outside trusted network, connecting")
            self._last_auto_action = now
            await self.cmd_connect('AutoConnect', server_profile)
        elif is_server_up and in_trusted:
            log.info("Auto-connect: inside trusted network, disconnecting")
            self._last_auto_action = now
            await self.cmd_disconnect('AutoConnect', server_profile)

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
            self.state.set_backend_reachable(True)
            active = resp.get('IsActive', resp.get('isActive', False))
            self.state.update_registration(active)
            return active, None
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
        try:
            if self.state.is_connected(profile_name):
                asyncio.create_task(self._disconnect_and_delete(profile_name))
            else:
                configs_mod.delete_profile(profile_name)
        except Exception as e:
            log.error("Pending delete failed: %s", e)

    async def _disconnect_and_delete(self, profile_name: str) -> None:
        await self._wg_down(profile_name)
        self.state.set_disconnected(profile_name)
        configs_mod.delete_profile(profile_name)

    async def _reconnect_if_replaced(self, username: str, staged_names: list[str]) -> None:
        """If an active tunnel's config was just replaced, reconnect to pick up new keys."""
        for staged_name in staged_names:
            if not self.state.is_connected(staged_name):
                continue
            ok, _ = await self._wg_down(staged_name)
            if not ok:
                continue
            self.state.set_disconnected(staged_name)
            config_path = configs_mod.get_config_path(username, staged_name)
            if config_path:
                wan_ip = await self.backend.get_wan_ip()
                try:
                    wg_path = configs_mod.prepare_config_path(config_path)
                except ValueError as e:
                    log.warning("Not reconnecting '%s': %s", staged_name, e)
                    continue
                ok, _ = await self.wg.connect(wg_path)
                configs_mod.cleanup_temp_config(staged_name)
                if ok:
                    self.state.set_connected(staged_name, username)
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
