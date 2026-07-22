"""Synchronous IPC client for the tray app.

Called from the GLib main thread (GTK) to communicate with the daemon.
"""
from __future__ import annotations

import json
import socket
from typing import Optional

from shared.messages import (
    CommandType, ConfigInfo, PipeCommand, PipeResponse,
    RegistrationResult, TunnelStatus, VersionCheckResult,
)
from shared.protocol import read_message_sync, write_message_sync

SOCKET_PATH = '/run/valenius.sock'
TIMEOUT = 10


class IpcError(Exception):
    pass


class LanConflictError(IpcError):
    """Raised when a Connect is refused because this machine's own local LAN
    overlaps a network reachable through the VPN (or the VPN-assigned IP itself).
    Mirrors Windows' PipeResponse.IsLanConflict -> LanConflictForm split."""
    pass


def _send(cmd: PipeCommand, timeout: int = TIMEOUT) -> PipeResponse:
    try:
        with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as sock:
            sock.settimeout(timeout)
            sock.connect(SOCKET_PATH)
            write_message_sync(sock, cmd.to_json())
            raw = read_message_sync(sock)
            return PipeResponse.from_json(raw)
    except FileNotFoundError:
        raise IpcError("Daemon is not running (socket not found).")
    except ConnectionRefusedError:
        raise IpcError("Daemon is not running.")
    except socket.timeout:
        raise IpcError("Daemon did not respond in time.")
    except Exception as e:
        raise IpcError(str(e))


def get_status() -> TunnelStatus:
    resp = _send(PipeCommand(Command=CommandType.Status))
    if not resp.Success:
        raise IpcError(resp.Error or "Status failed.")
    return TunnelStatus.from_json(resp.DataJson or '{}')


def connect(profile_name: Optional[str] = None) -> None:
    resp = _send(PipeCommand(Command=CommandType.Connect, ProfileName=profile_name))
    if not resp.Success:
        if resp.IsLanConflict:
            raise LanConflictError(resp.Error or "Connect failed.")
        raise IpcError(resp.Error or "Connect failed.")


def disconnect(profile_name: Optional[str] = None) -> None:
    resp = _send(PipeCommand(Command=CommandType.Disconnect, ProfileName=profile_name))
    if not resp.Success:
        raise IpcError(resp.Error or "Disconnect failed.")


def mfa_enroll_confirm(code: str) -> bool:
    """Returns True if the backend accepted the TOTP code."""
    resp = _send(PipeCommand(Command=CommandType.MfaEnrollConfirm, MfaCode=code))
    if not resp.Success:
        raise IpcError(resp.Error or "MFA confirmation failed.")
    return True


def upload_config(profile_name: str, content: str) -> None:
    resp = _send(PipeCommand(
        Command=CommandType.UploadConfig,
        ProfileName=profile_name,
        ConfigContent=content,
    ))
    if not resp.Success:
        raise IpcError(resp.Error or "Upload failed.")


def get_config_info() -> ConfigInfo:
    resp = _send(PipeCommand(Command=CommandType.GetConfigInfo))
    if not resp.Success:
        raise IpcError(resp.Error or "GetConfigInfo failed.")
    return ConfigInfo.from_json(resp.DataJson or '{}')


def sync_status() -> TunnelStatus:
    """Force an immediate backend heartbeat + update check and return the fresh
    status (the daemon caps this at 8 s, under our 10 s socket timeout)."""
    resp = _send(PipeCommand(Command=CommandType.SyncStatus))
    if not resp.Success:
        raise IpcError(resp.Error or "Sync failed.")
    return TunnelStatus.from_json(resp.DataJson or '{}')


def set_auto_connect(enabled: bool) -> None:
    resp = _send(PipeCommand(Command=CommandType.SetAutoConnect, AutoConnectEnabled=enabled))
    if not resp.Success:
        raise IpcError(resp.Error or "SetAutoConnect failed.")


def delete_profile(profile_name: str) -> None:
    resp = _send(PipeCommand(Command=CommandType.DeleteProfile, ProfileName=profile_name))
    if not resp.Success:
        raise IpcError(resp.Error or "Delete failed.")


def notify_offline() -> None:
    """Best-effort: tells the daemon the tray is closing so it can immediately
    inform the backend (admin panel shows offline without waiting ~7 minutes)."""
    try:
        _send(PipeCommand(Command=CommandType.NotifyOffline), timeout=2)
    except IpcError:
        pass


def send_logs() -> None:
    """Ask the daemon to collect its (Valenius-only, redacted) logs and upload them."""
    resp = _send(PipeCommand(Command=CommandType.SendLogs))
    if not resp.Success:
        raise IpcError(resp.Error or "Failed to send logs.")


def register() -> RegistrationResult:
    resp = _send(PipeCommand(Command=CommandType.Register))
    if not resp.Success:
        raise IpcError(resp.Error or "Register failed.")
    return RegistrationResult.from_json(resp.DataJson or '{}')


def set_backend_url(dns: str) -> Optional[str]:
    """First-run: send the backend server DNS (the daemon prepends https:// and persists it).
    Returns a non-fatal advisory (e.g. server unreachable) on success, or None if all is well.
    Raises IpcError only when the daemon rejects the address (invalid) or is unreachable."""
    resp = _send(PipeCommand(Command=CommandType.SetBackendUrl, BackendDns=dns), timeout=15)
    if not resp.Success:
        raise IpcError(resp.Error or "Could not save the server address.")
    return resp.Error
