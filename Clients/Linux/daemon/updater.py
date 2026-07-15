"""Auto-updater for the Linux daemon.

Flow:
  1. GET /api/version  →  compare version strings
  2. If a newer version is available and linuxUrl + linuxSha256 are set:
     a. Download the .deb to /var/lib/valenius (a real, writable, non-private
        path — see _install for why /tmp won't do)
     b. Verify SHA-256 before touching anything
     c. Run `dpkg -i <file>` via `systemd-run` so it executes OUTSIDE the
        daemon's systemd sandbox — the postinst will restart the systemd
        service, which kills the currently running process (fine and expected)

The daemon runs as root so no privilege escalation is needed.
"""
from __future__ import annotations

import asyncio
import hashlib
import logging
import os
import tempfile
import urllib.request
from pathlib import Path
from typing import Optional
from urllib.parse import urljoin

from daemon.backend import VERSION, BackendClient

log = logging.getLogger(__name__)

_DOWNLOAD_TIMEOUT = 300  # seconds for large .deb files
_CHUNK = 65_536          # 64 KiB read chunks
# The .deb must land somewhere the *unsandboxed* dpkg (spawned via systemd-run, see
# _install) can read. The daemon runs PrivateTmp=true, so a file in /tmp lives in the
# daemon's private tmpfs and is invisible to any process outside the service. This dir
# is a real path on the host and is in the unit's ReadWritePaths, so the daemon can
# write it and the transient dpkg unit can read it.
_UPDATE_DIR = '/var/lib/valenius'


class Updater:
    def __init__(self, backend: BackendClient, loop: asyncio.AbstractEventLoop):
        self._backend = backend
        self._loop = loop
        self._applying = False

    async def check(self) -> tuple[bool, str, Optional[str], Optional[str]]:
        """Return (update_available, latest_version, url, sha256) from the Linux stream
        (GET /api/version/linux → {version, downloadUrl, sha256})."""
        data = await self._backend.get_version()
        if data is None:
            return False, VERSION, None, None

        latest = data.get('version', '')
        url = data.get('downloadUrl') or None
        sha256 = data.get('sha256') or None
        # The backend returns a root-relative download URL (e.g. "/api/download/foo.deb",
        # see OssStartup.DownloadUrlFor). urllib.urlopen rejects a schemeless URL with
        # "unknown url type", so resolve it against the backend base first. urljoin
        # leaves an already-absolute URL untouched.
        if url:
            url = urljoin(self._backend.base_url + '/', url)
        available = _version_gt(latest, VERSION) and bool(url) and bool(sha256)
        return available, latest, url, sha256

    async def check_and_apply(self) -> None:
        """Check for an update and install it if one is available."""
        if self._applying:
            log.debug("Update already in progress, skipping")
            return

        available, latest, linux_url, linux_sha256 = await self.check()
        if not available:
            return

        log.info("Update available: %s → %s — downloading %s", VERSION, latest, linux_url)
        self._applying = True
        try:
            await self._loop.run_in_executor(None, self._download_and_install, linux_url, linux_sha256, latest)
        except Exception as e:
            log.error("Update failed: %s", e)
        finally:
            self._applying = False

    # ── Private ──────────────────────────────────────────────────────────────

    def _download_and_install(self, url: str, expected_sha256: str, version: str) -> None:
        """Blocking: download, verify, and hand off to the detached install unit.

        Runs in a thread executor."""
        tmp_path: Optional[Path] = None
        handed_off = False
        try:
            tmp_path = _download(url, expected_sha256)
            log.info("Downloaded %s (%d bytes) — SHA-256 verified", tmp_path.name, tmp_path.stat().st_size)
            _install(tmp_path)
            handed_off = True
            log.info("Self-update unit started for version %s", version)
        finally:
            # Once the detached unit is running it owns (and removes) the .deb; deleting
            # it here would race the unit's dpkg read. Only clean up if handoff failed.
            if tmp_path and not handed_off and tmp_path.exists():
                try:
                    tmp_path.unlink()
                except Exception:
                    pass


def _download(url: str, expected_sha256: str) -> Path:
    """Download url to a temp file and verify its SHA-256.  Returns the temp path."""
    fd, tmp_name = tempfile.mkstemp(suffix='.deb', prefix='valenius-update-', dir=_UPDATE_DIR)
    tmp_path = Path(tmp_name)
    os.close(fd)

    sha256 = hashlib.sha256()
    try:
        req = urllib.request.Request(url, headers={'User-Agent': f'Valenius-Linux/{VERSION}'})
        with urllib.request.urlopen(req, timeout=_DOWNLOAD_TIMEOUT) as resp, \
             open(tmp_path, 'wb') as out:
            while True:
                chunk = resp.read(_CHUNK)
                if not chunk:
                    break
                out.write(chunk)
                sha256.update(chunk)
    except Exception:
        tmp_path.unlink(missing_ok=True)
        raise

    actual = sha256.hexdigest().lower()
    expected = expected_sha256.lower()
    if actual != expected:
        tmp_path.unlink(missing_ok=True)
        raise ValueError(
            f"SHA-256 mismatch — expected {expected[:16]}… got {actual[:16]}…\n"
            "Update aborted to protect against a corrupted or tampered package."
        )

    return tmp_path


def _install(deb_path: Path) -> None:
    """Start `dpkg -i` as a DETACHED transient unit, outside the daemon's sandbox.

    Two problems this solves:
      1. The daemon's unit sets ProtectSystem=strict, so /usr, /var/lib/dpkg, /etc are
         read-only in its mount namespace — a direct `dpkg -i` (or any forked child,
         which inherits that namespace) fails with "Read-only file system". PID 1
         spawns the transient unit fresh: no sandbox, full read-write.
      2. dpkg's own maintainer scripts restart valenius.service, which kills this daemon.
         The unit must NOT be tied to the daemon's lifetime, so this is fire-and-forget:
         no --pipe/--wait. A --pipe/--wait client is a child of this daemon and would be
         killed with it, tearing the dpkg unit down mid-configure (package left
         "unpacked", service disabled). A detached unit is owned by PID 1 in its own
         cgroup and runs to completion regardless. It removes the .deb itself when done;
         its output goes to the journal (`journalctl -u valenius-self-update`).
    """
    import subprocess
    log.info("Starting detached self-update unit (dpkg outside sandbox): %s", deb_path)
    wrapper = 'dpkg -i "$1"; rm -f "$1"'
    result = subprocess.run(
        ['systemd-run', '--collect', '--quiet',
         '--unit', 'valenius-self-update',
         '--property', 'StandardOutput=journal',
         '--property', 'StandardError=journal',
         '--', '/bin/sh', '-c', wrapper, '_', str(deb_path)],
        capture_output=True,
        text=True,
    )
    if result.returncode != 0:
        raise RuntimeError(
            f"systemd-run failed to start the self-update unit (exit {result.returncode}):\n"
            f"{result.stdout}\n{result.stderr}"
        )


def _version_gt(a: str, b: str) -> bool:
    def _parts(v: str) -> list[int]:
        try:
            return [int(x) for x in v.split('.')]
        except Exception:
            return [0]
    return _parts(a) > _parts(b)
