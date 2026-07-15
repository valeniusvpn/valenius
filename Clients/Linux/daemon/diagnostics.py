"""Collects Valenius-only diagnostic logs into a gzipped, redacted text bundle.

Fixed allowlist: a summary header + `journalctl -u valenius` (the daemon's own
log — the daemon runs as a systemd unit, see valenius.service). Secrets (API key,
WireGuard private/preshared keys, TOTP secrets, tokens) are redacted before the
bytes ever leave the machine. No other system data is gathered.
"""
from __future__ import annotations

import gzip
import platform
import re
import subprocess
from datetime import datetime, timezone
from typing import Optional

from daemon.backend import VERSION


def collect_bundle(api_key: Optional[str] = None) -> bytes:
    sections = [
        ("Summary", _summary()),
        ("journalctl -u valenius (last 3000 lines)", _journalctl()),
    ]
    text = "\n\n".join(f"===== {title} =====\n{body}" for title, body in sections)
    return gzip.compress(_redact(text, api_key).encode("utf-8", "replace"))


def _summary() -> str:
    return "\n".join([
        f"Collected: {datetime.now(timezone.utc).isoformat()}",
        f"Host:      {platform.node()}",
        f"OS:        {platform.platform()}",
        f"Python:    {platform.python_version()}",
        f"Client:    {VERSION}",
    ])


def _journalctl() -> str:
    try:
        out = subprocess.run(
            ["journalctl", "-u", "valenius", "--no-pager", "-n", "3000"],
            capture_output=True, text=True, timeout=30,
        )
        return out.stdout or out.stderr or "[no output]"
    except Exception as e:  # journalctl missing, permission, timeout, …
        return f"[unavailable: {e}]"


def _redact(text: str, api_key: Optional[str]) -> str:
    if api_key:
        text = text.replace(api_key, "[REDACTED-APIKEY]")
    # WireGuard private / preshared keys.
    text = re.sub(r"(?im)^(\s*(?:PrivateKey|PresharedKey))\s*=\s*.+$", r"\1 = [REDACTED]", text)
    # Any bare base64 32-byte key (44 chars ending '=').
    text = re.sub(r"\b[A-Za-z0-9+/]{43}=", "[REDACTED-KEY]", text)
    # secret / token / authorization / bearer values.
    text = re.sub(r"(?i)\b(secret|token|authorization|bearer|apikey|x-api-key)\b\s*[:=]\s*\S+",
                  r"\1=[REDACTED]", text)
    return text
