"""HTTP client for the Valenius backend API.

Requests use PascalCase JSON and the X-Api-Key header, matching the Windows client.
All network I/O runs in a thread-pool executor so it doesn't block the asyncio loop.
"""
from __future__ import annotations

import asyncio
import json
import logging
import os
import socket
import ssl
import tempfile
import urllib.error
import urllib.request
from typing import Optional
from uuid import UUID, uuid4

log = logging.getLogger(__name__)

VERSION = '1.0.0'

# Overridable the same way VERSION is (see register() below) -- the Home Assistant
# add-on monkeypatches this to 'homeassistant' so the backend's PlatformIcon switch
# (Client.cs) and its release-stream update comparison (ReleaseOsForPlatform, which has
# no Linux-stream entry for 'homeassistant' -- that add-on is Supervisor-updated, not
# Valenius-updated) both treat it distinctly from the desktop Linux client instead of
# reporting a bare 'Linux' that gets compared against the wrong release stream.
PLATFORM = 'Linux'

# A backend-rotated API key is persisted here (root-only) so it survives a daemon
# restart/upgrade. /var/lib/valenius is 0700 root; the file is 0600. On startup a
# persisted key takes precedence over the one from /etc/valenius/appsettings.json,
# so a fresh install bootstraps from appsettings while a rotated deployment keeps
# the new key.
_API_KEY_FILE = '/var/lib/valenius/apikey'

# The backend URL the user entered at first run (the tray prompt shown when the installer
# provided none) is persisted here, root-only, so it survives a restart/upgrade and wins
# over the /etc/valenius/appsettings.json value. Mirrors _API_KEY_FILE / the Windows
# BackendUrlProvider (backend.dat).
_BACKEND_URL_FILE = '/var/lib/valenius/backend_url'


def _load_persisted_api_key() -> Optional[str]:
    try:
        with open(_API_KEY_FILE, 'r', encoding='utf-8') as f:
            key = f.read().strip()
            return key or None
    except Exception:
        return None


def _load_persisted_backend_url() -> Optional[str]:
    try:
        with open(_BACKEND_URL_FILE, 'r', encoding='utf-8') as f:
            url = f.read().strip()
            if not url or not normalize_backend_url(url):
                raise RuntimeError('Persisted backend URL is invalid; refusing to change backend trust')
            return url
    except FileNotFoundError:
        if os.path.lexists(_BACKEND_URL_FILE):
            raise RuntimeError('Persisted backend URL is unreadable; repair storage before startup')
        return None
    except OSError as error:
        raise RuntimeError('Persisted backend URL is unreadable; refusing to change backend trust') from error


def normalize_backend_url(raw: Optional[str]) -> str:
    """Coerce a user-entered DNS/URL to the canonical https form the client assumes:
    strip any scheme the user typed/pasted, drop path/query and surrounding whitespace or
    slashes, then prepend https:// . Returns '' when no host remains. Mirrors the Windows
    BackendUrlProvider.Normalize."""
    s = (raw or '').strip()
    if not s:
        return ''
    idx = s.find('://')
    if idx >= 0:
        s = s[idx + 3:]
    s = s.lstrip('/')
    for sep in ('/', '?', '#'):
        cut = s.find(sep)
        if cut >= 0:
            s = s[:cut]
    s = s.strip().rstrip('.')
    return 'https://' + s if s else ''


async def probe_gateway_health(
    ip: str, port: int, loop: asyncio.AbstractEventLoop, timeout: float = 4.0
) -> bool:
    """GET https://<ip>:<port>/health *through the tunnel*. Returns True on HTTP 200.

    The sidecar gateway uses the internal CA, which the client doesn't trust — but the
    request already travels over the authenticated WireGuard tunnel, so certificate
    verification is intentionally disabled (mirrors the Windows ConnectivityVerifier).
    """
    def _sync() -> bool:
        ctx = ssl.create_default_context()
        ctx.check_hostname = False
        ctx.verify_mode = ssl.CERT_NONE
        try:
            req = urllib.request.Request(f'https://{ip}:{port}/health', method='GET')
            with urllib.request.urlopen(req, timeout=timeout, context=ctx) as resp:
                return resp.status == 200
        except Exception:
            return False
    return await loop.run_in_executor(None, _sync)


class BackendClient:
    def __init__(self, base_url: str, api_key: str, loop: asyncio.AbstractEventLoop):
        # A persisted user-entered URL (first-run tray prompt) wins over appsettings.
        self._base = normalize_backend_url(_load_persisted_backend_url() or base_url)
        self._loop = loop
        self._channel = 'stable'  # auto-update channel, refreshed from the heartbeat
        self._headers = {
            'X-Api-Key': _load_persisted_api_key() or api_key,
            'X-Valenius-Device-Auth': '1',
            'Content-Type': 'application/json',
            'User-Agent': f'Valenius-Linux/{VERSION}',
        }

    def set_channel(self, channel: Optional[str]) -> None:
        self._channel = 'beta' if (channel or '').lower() == 'beta' else 'stable'

    def set_api_key(self, new_key: Optional[str]) -> None:
        """Persist an offered individual credential before using it to confirm migration.
        A failed write retains the current key so the same offer can be retried."""
        if not new_key or new_key == self._headers.get('X-Api-Key'):
            return
        temporary = None
        try:
            directory = os.path.dirname(_API_KEY_FILE)
            os.makedirs(directory, mode=0o700, exist_ok=True)
            fd, temporary = tempfile.mkstemp(prefix='.apikey-', dir=directory)
            with os.fdopen(fd, 'w', encoding='utf-8') as f:
                f.write(new_key)
                f.flush()
                os.fsync(f.fileno())
            os.replace(temporary, _API_KEY_FILE)
            temporary = None
            if os.name == 'posix':
                directory_fd = os.open(directory, os.O_RDONLY | os.O_DIRECTORY)
                try:
                    os.fsync(directory_fd)
                finally:
                    os.close(directory_fd)
            self._headers['X-Api-Key'] = new_key
            log.info("Persisted and adopted individual client API credential.")
        except Exception as e:
            log.warning("Could not persist API credential (%s); retaining current credential for retry.", type(e).__name__)
        finally:
            if temporary is not None:
                os.unlink(temporary)

    @property
    def base_url(self) -> str:
        return self._base

    @property
    def is_configured(self) -> bool:
        """True once a backend URL is available (from appsettings or the persisted user choice)."""
        return bool(self._base)

    def set_base_url(self, raw: Optional[str], *, privileged: bool = False) -> Optional[str]:
        """Administrator-owned initial provisioning, persisted before use (0600).
        Existing origins cannot be reassigned; None means the input has no usable host."""
        if not privileged:
            raise PermissionError('An administrator must provision the backend address')
        normalized = normalize_backend_url(raw)
        if not normalized:
            return None
        if self._base:
            if self._base.casefold() == normalized.casefold():
                return self._base
            raise ValueError('Backend reassignment is not supported through IPC. Administrator migration is required.')
        temporary = None
        try:
            directory = os.path.dirname(_BACKEND_URL_FILE)
            os.makedirs(directory, mode=0o700, exist_ok=True)
            fd, temporary = tempfile.mkstemp(prefix='.backend-', dir=directory)
            with os.fdopen(fd, 'w', encoding='utf-8') as f:
                f.write(normalized)
                f.flush()
                os.fsync(f.fileno())
            os.replace(temporary, _BACKEND_URL_FILE)
            temporary = None
            if os.name == 'posix':
                directory_fd = os.open(directory, os.O_RDONLY | os.O_DIRECTORY)
                try:
                    os.fsync(directory_fd)
                finally:
                    os.close(directory_fd)
            self._base = normalized
            log.info("Backend URL set to %s and persisted.", normalized)
        finally:
            if temporary is not None:
                os.unlink(temporary)
        return normalized

    @property
    def api_key(self) -> str:
        return self._headers.get('X-Api-Key', '')

    async def upload_logs(self, client_id: UUID, gz_bytes: bytes, trigger: str = 'Admin') -> bool:
        """POST a gzipped, redacted log bundle to /api/clients/logs as multipart/form-data.
        Built by hand (stdlib only — no `requests`); the daemon has no PyPI deps."""
        return await self._loop.run_in_executor(
            None, lambda: self._upload_logs_sync(client_id, gz_bytes, trigger)
        )

    def _upload_logs_sync(self, client_id: UUID, gz_bytes: bytes, trigger: str) -> bool:
        boundary = '----ValeniusBoundary' + uuid4().hex

        def field(name: str, value: str) -> bytes:
            return (f'--{boundary}\r\n'
                    f'Content-Disposition: form-data; name="{name}"\r\n\r\n'
                    f'{value}\r\n').encode()

        body = (
            field('clientKey', str(client_id))
            + field('trigger', trigger)
            + (f'--{boundary}\r\n'
               f'Content-Disposition: form-data; name="file"; filename="valenius-logs.log.gz"\r\n'
               f'Content-Type: application/gzip\r\n\r\n').encode()
            + gz_bytes + b'\r\n'
            + f'--{boundary}--\r\n'.encode()
        )
        headers = {
            'X-Api-Key': self._headers['X-Api-Key'],
            'User-Agent': self._headers['User-Agent'],
            'Content-Type': f'multipart/form-data; boundary={boundary}',
        }
        req = urllib.request.Request(self._base + '/api/clients/logs', data=body,
                                     method='POST', headers=headers)
        try:
            with urllib.request.urlopen(req, timeout=30) as resp:
                return resp.status == 200
        except Exception as e:
            log.warning("log upload failed: %s", e)
            return False

    # ── Low-level HTTP ───────────────────────────────────────────────────────

    def _request_sync(
        self, method: str, path: str, body: Optional[dict] = None, timeout: int = 15,
        base_override: Optional[str] = None,
    ) -> Optional[dict]:
        base = base_override if base_override is not None else self._base
        if not base:
            return None  # not configured yet — nothing to call
        url = base + path
        data = json.dumps(body).encode() if body else None
        req = urllib.request.Request(url, data=data, method=method, headers=self._headers)
        try:
            with urllib.request.urlopen(req, timeout=timeout) as resp:
                if resp.status == 204:
                    return None
                raw_bytes = resp.read(16 * 1024 * 1024 + 1)
                if len(raw_bytes) > 16 * 1024 * 1024:
                    raise ValueError('Backend response exceeds 16 MiB')
                raw = raw_bytes.decode()
                return json.loads(raw) if raw.strip() else None
        except urllib.error.HTTPError as e:
            log.warning("HTTP %d %s %s", e.code, method, path)
            return None
        except Exception as e:
            log.debug("%s %s failed: %s", method, path, e)
            return None

    async def _request(
        self, method: str, path: str, body: Optional[dict] = None, timeout: int = 15,
        base_override: Optional[str] = None,
    ) -> Optional[dict]:
        return await self._loop.run_in_executor(
            None, lambda: self._request_sync(method, path, body, timeout, base_override)
        )

    # ── API calls ────────────────────────────────────────────────────────────

    async def register(
        self,
        client_id: UUID,
        hostname: str,
        username: str,
        profiles: list[str],
        tray_running: bool = False,
    ) -> Optional[dict]:
        return await self._request('POST', '/api/clients/register', {
            'ClientKey': str(client_id),
            'Hostname': hostname,
            'HostSid': '',
            'Username': username,
            'UserSid': '',
            'Version': VERSION,
            'TrayRunning': tray_running,
            'Profiles': profiles,
            'Platform': PLATFORM,
        })

    async def poll(self, client_id: UUID, tray_running: bool = False) -> Optional[dict]:
        """GET /api/clients/poll — the backend holds the connection for up to 55 s and
        returns as soon as an admin action changes this client's state (mirrors the
        Windows client's long-poll loop). Timeout must exceed the server's hold time."""
        return await self._request(
            'GET', f'/api/clients/poll?clientKey={client_id}&trayRunning={tray_running}',
            timeout=65,
        )

    async def notify_offline(self, client_id: UUID) -> None:
        """Best-effort: tells the backend to immediately drop this client from the
        online presence tracker (tray closed / daemon stopping)."""
        await self._request('POST', f'/api/clients/offline?clientKey={client_id}')

    async def log_event(
        self,
        client_id: UUID,
        event_type: str,
        username: str,
        tunnel_name: str,
        lan_ip: str = '',
        wan_ip: str = '',
        detail: Optional[str] = None,
    ) -> None:
        body = {
            'ClientKey': str(client_id),
            'EventType': event_type,
            'Username': username,
            'TunnelName': tunnel_name,
            'LanIp': lan_ip,
            'WanIp': wan_ip,
        }
        if detail is not None:
            body['Detail'] = detail
        await self._request('POST', '/api/clients/event', body)

    async def get_pending_config(self, client_id: UUID) -> Optional[dict]:
        return await self._request('GET', f'/api/clients/pending-config?clientKey={client_id}')

    async def get_version(self) -> Optional[dict]:
        # Per-OS stream: {version, downloadUrl, sha256, releaseNotes}. The Linux stream is
        # independent of Windows (see backend Admin/Releases + GET /api/version/{os}).
        # ?channel=beta selects the beta stream (falls back to stable when none published).
        return await self.get_version_at(self._base)

    async def get_version_at(self, base_url: Optional[str]) -> Optional[dict]:
        """Same as get_version() but against an explicit base URL rather than self._base --
        used by the standalone-mode auto-update fallback (see daemon/cloud_update_source.py),
        which checks Stranto's own managed instance instead of a (nonexistent) configured
        backend."""
        path = '/api/version/linux'
        if self._channel == 'beta':
            path += '?channel=beta'
        return await self._request('GET', path, base_override=base_url)

    async def archive_profile(self, client_id: UUID, profile_name: str, content: str) -> bool:
        """POST /api/clients/profiles/archive -- archives a local profile's content server-side
        without marking it managed. Used by the standalone-mode local-profile bulk transfer
        (DaemonCore._transfer_local_profiles) once a client that was standalone configures a
        backend and activates. Mirrors Windows ClientRegistrationService.ArchiveProfileAsync.

        Builds its own request (like confirm_mfa_enrollment) rather than going through
        _request/_request_sync: the endpoint returns 200 with an empty body on success, which
        _request_sync's JSON-or-None parsing can't distinguish from a failed call."""
        def _sync() -> bool:
            if not self._base:
                return False
            url = self._base + '/api/clients/profiles/archive'
            body = json.dumps({
                'ClientKey': str(client_id), 'ProfileName': profile_name, 'Content': content,
            }).encode()
            req = urllib.request.Request(url, data=body, method='POST', headers=self._headers)
            try:
                with urllib.request.urlopen(req, timeout=15) as resp:
                    return resp.status < 300
            except urllib.error.HTTPError:
                return False
            except Exception as e:
                log.debug("profiles/archive failed: %s", e)
                return False
        return await self._loop.run_in_executor(None, _sync)

    async def get_profiles(self, client_id: UUID) -> Optional[list[dict]]:
        """GET /api/clients/profiles -- every ClientConfigArchive row for this machine
        ({FileName, Content} each), regardless of platform or which identity claimed it.
        Used to bootstrap the device-tunnel machine-level config store (see
        DaemonCore._ensure_device_tunnel_config)."""
        return await self._request('GET', f'/api/clients/profiles?clientKey={client_id}')

    async def confirm_mfa_enrollment(self, client_id: UUID, code: str) -> bool:
        """POST /api/clients/mfa/enroll-confirm. Returns True on HTTP 200."""
        def _sync() -> bool:
            url = self._base + '/api/clients/mfa/enroll-confirm'
            body = json.dumps({'ClientKey': str(client_id), 'Code': code}).encode()
            req = urllib.request.Request(url, data=body, method='POST', headers=self._headers)
            try:
                with urllib.request.urlopen(req, timeout=15) as resp:
                    return resp.status < 300
            except urllib.error.HTTPError:
                return False
            except Exception as e:
                log.debug("mfa/enroll-confirm failed: %s", e)
                return False
        return await self._loop.run_in_executor(None, _sync)

    async def get_wan_ip(self) -> str:
        def _fetch() -> str:
            try:
                req = urllib.request.Request(
                    'https://api4.ipify.org',
                    headers={'User-Agent': f'Valenius-Linux/{VERSION}'},
                )
                with urllib.request.urlopen(req, timeout=5) as r:
                    return r.read().decode().strip()
            except Exception:
                return ''
        return await self._loop.run_in_executor(None, _fetch)


def get_lan_ip() -> str:
    """Best-effort: first non-loopback IPv4 address."""
    try:
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as s:
            s.connect(('8.8.8.8', 80))
            return s.getsockname()[0]
    except Exception:
        return ''
