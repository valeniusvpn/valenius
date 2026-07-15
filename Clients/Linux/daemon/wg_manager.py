"""Thin asyncio wrapper around wg-quick."""
from __future__ import annotations

import asyncio
import logging
from pathlib import Path

log = logging.getLogger(__name__)


class WgManager:
    async def connect(self, config_path: Path) -> tuple[bool, str]:
        ok, out = await _run(['wg-quick', 'up', str(config_path)])
        if ok:
            log.info("Tunnel %s up", config_path.stem)
        else:
            log.error("wg-quick up failed: %s", out)
        return ok, out

    async def disconnect(self, tunnel_name: str) -> tuple[bool, str]:
        ok, out = await _run(['wg-quick', 'down', tunnel_name])
        if ok:
            log.info("Tunnel %s down", tunnel_name)
        else:
            log.error("wg-quick down failed: %s", out)
        return ok, out

    async def is_active(self, tunnel_name: str) -> bool:
        ok, out = await _run(['wg', 'show', tunnel_name])
        return ok and 'interface:' in out.lower()

    async def get_active_tunnels(self) -> list[str]:
        """Return names of all currently active WireGuard interfaces."""
        ok, out = await _run(['wg', 'show', 'interfaces'])
        if not ok or not out.strip():
            return []
        return out.split()

    async def latest_handshake(self, iface: str) -> int:
        """Most recent peer handshake on `iface` as a unix-second timestamp (0 if none).

        `wg show <iface> latest-handshakes` prints one `pubkey\\t<epoch>` line per
        peer; an epoch of 0 means the peer has never handshaked. We take the max so
        a tunnel with multiple peers counts as alive if any peer is fresh.
        """
        ok, out = await _run(['wg', 'show', iface, 'latest-handshakes'])
        if not ok or not out.strip():
            return 0
        latest = 0
        for line in out.splitlines():
            parts = line.split('\t')
            if len(parts) >= 2:
                try:
                    latest = max(latest, int(parts[-1].strip()))
                except ValueError:
                    continue
        return latest


async def _run(cmd: list[str]) -> tuple[bool, str]:
    try:
        proc = await asyncio.create_subprocess_exec(
            *cmd,
            stdout=asyncio.subprocess.PIPE,
            stderr=asyncio.subprocess.STDOUT,
        )
        stdout, _ = await proc.communicate()
        out = stdout.decode().strip() if stdout else ''
        return proc.returncode == 0, out
    except FileNotFoundError:
        return False, f"Command not found: {cmd[0]}"
    except Exception as e:
        return False, str(e)
