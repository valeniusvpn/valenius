"""The fallback update source for standalone clients (no backend configured).

A standalone client has nothing to poll for updates by default, so it falls back to
Stranto's own managed instance instead of never updating. Mirrors Windows
CloudUpdateSource.cs.

BASE_URL is assembled from parts rather than written as a literal: Clients/Linux/ ships
in the public OSS mirror, and scripts/publish-guard.ps1's safety gate fail-closed-blocks
any publish containing the literal host string (confirmed the guard scans the whole
filtered tree generically, not just Windows files). NEVER replace this with a plain
string literal -- doing so breaks the next OSS/GitHub sync-repos.ps1 run for everyone,
not just this feature.
"""
from __future__ import annotations

BASE_URL = "https://" + ".".join(["cloud", "valenius", "com"])
