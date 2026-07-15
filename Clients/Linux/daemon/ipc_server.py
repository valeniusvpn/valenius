"""Asyncio Unix-socket IPC server.

Replicates the Windows named-pipe server behaviour:
  • Identifies callers via SO_PEERCRED (uid → username)
  • Same length-prefixed JSON framing
  • Same command dispatch and response types
"""
from __future__ import annotations

import asyncio
import json
import logging
import os
import pwd
import socket
import struct
from pathlib import Path
from typing import Optional

from shared.messages import (
    CommandType, ConfigInfo, ConnectedTunnelInfo, PipeCommand, PipeResponse,
    RegistrationResult, TunnelStatus, VersionCheckResult,
)
from shared.protocol import read_message, write_message

log = logging.getLogger(__name__)

SOCKET_PATH = '/run/valenius.sock'


class IpcServer:
    def __init__(self, daemon):
        # daemon is the DaemonCore instance (avoids circular import)
        self._d = daemon
        self._server: Optional[asyncio.AbstractServer] = None

    async def start(self) -> None:
        if Path(SOCKET_PATH).exists():
            Path(SOCKET_PATH).unlink()

        self._server = await asyncio.start_unix_server(
            self._handle,
            path=SOCKET_PATH,
        )
        # World-writable: SO_PEERCRED (not the file mode) authenticates the caller's
        # UID for per-user command isolation, so every local user needs connect access.
        os.chmod(SOCKET_PATH, 0o666)
        log.info("IPC socket listening at %s", SOCKET_PATH)

    async def stop(self) -> None:
        if self._server:
            self._server.close()
            await self._server.wait_closed()
        try:
            Path(SOCKET_PATH).unlink(missing_ok=True)
        except Exception:
            pass

    async def _handle(self, reader: asyncio.StreamReader, writer: asyncio.StreamWriter) -> None:
        sock: socket.socket = writer.get_extra_info('socket')
        try:
            username = _get_peer_username(sock)
        except Exception as e:
            log.warning("Could not identify peer: %s", e)
            writer.close()
            return

        try:
            raw = await read_message(reader)
            cmd = PipeCommand.from_json(raw)
            log.info("Command %s from %s", cmd.Command.name, username)
            response = await self._dispatch(cmd, username)
        except Exception as e:
            log.error("IPC error from %s: %s", username, e)
            response = PipeResponse(Success=False, Error=str(e))

        try:
            await write_message(writer, response.to_json())
        except Exception:
            pass
        finally:
            writer.close()

    async def _dispatch(self, cmd: PipeCommand, username: str) -> PipeResponse:
        d = self._d
        # Any IPC traffic means the tray is open and talking to us -- this is the
        # Linux equivalent of the Windows service checking for TrayApp.exe in the
        # process list (used to drive the online presence indicator).
        d.state.mark_tray_seen()
        try:
            match cmd.Command:

                case CommandType.Status:
                    return _ok_data(d.build_tunnel_status(username))

                case CommandType.GetConfigInfo:
                    staged = d.configs.get_staged_profile_names()
                    d.configs.claim_staged_config(username)
                    await d._reconnect_if_replaced(username, staged)
                    profiles = d.configs.get_profiles(username)
                    info = ConfigInfo(
                        HasConfig=bool(profiles),
                        TunnelName=profiles[0] if profiles else None,
                        FileName=f'{profiles[0]}.conf' if profiles else None,
                        Profiles=profiles,
                    )
                    return _ok_data(info)

                case CommandType.UploadConfig:
                    if not cmd.ConfigContent:
                        return _fail("Config content is required.")
                    if not d.configs.is_valid_profile_name(cmd.ProfileName):
                        return _fail("Profile name may only contain letters, digits, _ and - (max 50 chars).")
                    content_err = d.configs.validate_config_content(cmd.ConfigContent)
                    if content_err:
                        return _fail(content_err)
                    d.configs.save_config(username, cmd.ProfileName, cmd.ConfigContent)
                    return _ok()

                case CommandType.Connect:
                    return await d.cmd_connect(username, cmd.ProfileName)

                case CommandType.Disconnect:
                    return await d.cmd_disconnect(username, cmd.ProfileName)

                case CommandType.MfaEnrollConfirm:
                    if not cmd.MfaCode:
                        return _fail("A TOTP code is required.")
                    return await d.cmd_mfa_enroll_confirm(username, cmd.MfaCode.strip())

                case CommandType.CheckUpdate:
                    result = await d.cmd_check_update()
                    return _ok_data(result)

                case CommandType.Register:
                    result = await d.cmd_register(username)
                    return _ok_data(result)

                case CommandType.NotifyOffline:
                    asyncio.create_task(d.backend.notify_offline(d.state.get_client_id()))
                    return _ok()

                case CommandType.SendLogs:
                    # User-initiated diagnostic log upload (collected + redacted in the daemon).
                    asyncio.create_task(d._upload_logs('User'))
                    return _ok()

                case CommandType.SetBackendUrl:
                    # First-run: the user provides the backend DNS (the daemon prepends https://).
                    return await d.cmd_set_backend_url(cmd.BackendDns)

                case CommandType.SyncStatus:
                    try:
                        await asyncio.wait_for(
                            asyncio.gather(
                                d.heartbeat_once(),
                                d.cmd_check_update(),
                                return_exceptions=True,
                            ),
                            timeout=8,
                        )
                    except asyncio.TimeoutError:
                        pass
                    return _ok_data(d.build_tunnel_status(username))

                case CommandType.SetAutoConnect:
                    if cmd.AutoConnectEnabled is None:
                        return _fail("AutoConnectEnabled must be specified.")
                    d.auto_connect.set_user_enabled(cmd.AutoConnectEnabled)
                    d.persist_auto_connect()
                    return _ok()

                case CommandType.DeleteProfile:
                    if not d.configs.is_valid_profile_name(cmd.ProfileName):
                        return _fail("Invalid profile name.")
                    if d.configs.is_managed(username, cmd.ProfileName):
                        return _fail(f"'{cmd.ProfileName}' is managed by the administrator.")
                    if d.state.is_connected(cmd.ProfileName):
                        ok, _ = await d.wg.disconnect(cmd.ProfileName)
                        if ok:
                            d.state.set_disconnected(cmd.ProfileName)
                            asyncio.create_task(
                                d.backend.log_event(
                                    d.state.get_client_id(), 'Disconnect',
                                    username, cmd.ProfileName,
                                )
                            )
                    if not d.configs.delete_user_profile(username, cmd.ProfileName):
                        return _fail(f"Profile '{cmd.ProfileName}' not found.")
                    return _ok()

                case _:
                    return _fail(f"Unknown command: {cmd.Command}")

        except asyncio.TimeoutError:
            return _fail("Operation timed out.")
        except Exception as e:
            log.error("Command %s failed: %s", cmd.Command, e)
            return _fail(str(e))


# ── Helpers ──────────────────────────────────────────────────────────────────

def _get_peer_username(sock: socket.socket) -> str:
    # struct ucred { pid_t pid; uid_t uid; gid_t gid; }  (3 × uint32 on Linux)
    raw = sock.getsockopt(socket.SOL_SOCKET, socket.SO_PEERCRED, 12)
    _pid, uid, _gid = struct.unpack('3I', raw)
    return pwd.getpwuid(uid).pw_name


def _ok() -> PipeResponse:
    return PipeResponse(Success=True)


def _ok_data(obj) -> PipeResponse:
    return PipeResponse(Success=True, DataJson=obj.to_json())


def _fail(msg: str) -> PipeResponse:
    return PipeResponse(Success=False, Error=msg)
