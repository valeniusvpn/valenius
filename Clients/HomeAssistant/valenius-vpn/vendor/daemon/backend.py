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
import urllib.error
import urllib.request
from typing import Optional
from uuid import UUID, uuid4

log = logging.getLogger(__name__)

VERSION = '1.0.0'

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
            return url or None
    except Exception:
        return None


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
            'Content-Type': 'application/json',
            'User-Agent': f'Valenius-Linux/{VERSION}',
        }

    def set_channel(self, channel: Optional[str]) -> None:
        self._channel = 'beta' if (channel or '').lower() == 'beta' else 'stable'

    def set_api_key(self, new_key: Optional[str]) -> None:
        """Adopt a backend-rotated key (heartbeat ClientApiKey): switch + persist so it
        survives a restart. No-op if empty or unchanged. Only sent by the backend while
        this client is still on the previous key during a rotation grace window."""
        if not new_key or new_key == self._headers.get('X-Api-Key'):
            return
        self._headers['X-Api-Key'] = new_key
        try:
            os.makedirs(os.path.dirname(_API_KEY_FILE), mode=0o700, exist_ok=True)
            fd = os.open(_API_KEY_FILE, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
            with os.fdopen(fd, 'w', encoding='utf-8') as f:
                f.write(new_key)
            os.chmod(_API_KEY_FILE, 0o600)
            log.info("Adopted rotated client API key from backend.")
        except Exception as e:
            log.warning("Could not persist rotated API key: %s", e)

    @property
    def base_url(self) -> str:
        return self._base

    @property
    def is_configured(self) -> bool:
        """True once a backend URL is available (from appsettings or the persisted user choice)."""
        return bool(self._base)

    def set_base_url(self, raw: Optional[str]) -> Optional[str]:
        """Set the backend URL from a user-entered DNS (first-run tray prompt): normalize to
        https://host, switch, and persist (root-only, 0600) so it survives a restart/upgrade and
        overrides appsettings. Returns the normalized URL, or None if the input has no usable host."""
        normalized = normalize_backend_url(raw)
        if not normalized:
            return None
        self._base = normalized
        try:
            os.makedirs(os.path.dirname(_BACKEND_URL_FILE), mode=0o700, exist_ok=True)
            fd = os.open(_BACKEND_URL_FILE, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
            with os.fdopen(fd, 'w', encoding='utf-8') as f:
                f.write(normalized)
            os.chmod(_BACKEND_URL_FILE, 0o600)
            log.info("Backend URL set to %s and persisted.", normalized)
        except Exception as e:
            log.warning("Could not persist backend URL: %s", e)
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

    def _request_sync(self, method: str, path: str, body: Optional[dict] = None, timeout: int = 15) -> Optional[dict]:
        if not self._base:
            return None  # not configured yet — nothing to call
        url = self._base + path
        data = json.dumps(body).encode() if body else None
        req = urllib.request.Request(url, data=data, method=method, headers=self._headers)
        try:
            with urllib.request.urlopen(req, timeout=timeout) as resp:
                if resp.status == 204:
                    return None
                raw = resp.read().decode()
                return json.loads(raw) if raw.strip() else None
        except urllib.error.HTTPError as e:
            log.warning("HTTP %d %s %s", e.code, method, path)
            return None
        except Exception as e:
            log.debug("%s %s failed: %s", method, path, e)
            return None

    async def _request(self, method: str, path: str, body: Optional[dict] = None, timeout: int = 15) -> Optional[dict]:
        return await self._loop.run_in_executor(
            None, lambda: self._request_sync(method, path, body, timeout)
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
            'Platform': 'Linux',
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
    ) -> None:
        await self._request('POST', '/api/clients/event', {
            'ClientKey': str(client_id),
            'EventType': event_type,
            'Username': username,
            'TunnelName': tunnel_name,
            'LanIp': lan_ip,
            'WanIp': wan_ip,
        })

    async def get_pending_config(self, client_id: UUID) -> Optional[dict]:
        return await self._request('GET', f'/api/clients/pending-config?clientKey={client_id}')

    async def get_version(self) -> Optional[dict]:
        # Per-OS stream: {version, downloadUrl, sha256, releaseNotes}. The Linux stream is
        # independent of Windows (see backend Admin/Releases + GET /api/version/{os}).
        # ?channel=beta selects the beta stream (falls back to stable when none published).
        path = '/api/version/linux'
        if self._channel == 'beta':
            path += '?channel=beta'
        return await self._request('GET', path)

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
