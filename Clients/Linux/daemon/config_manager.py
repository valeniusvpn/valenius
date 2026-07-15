"""Manage WireGuard config files on disk.

Layout (mirrors Windows ProgramData layout):
    /var/lib/valenius/
        master.key                          # 32-byte AES-256 key (0400, root-only)
        users/<username>/<profile>.conf.enc # encrypted at rest (AES-256-GCM)
        users/<username>/managed.json       # admin-pushed profile names
        staged/<filename>.conf              # admin-pushed, awaiting first claim (plain)
        temp/<profile>.conf                 # decrypted on-demand for wg-quick, deleted after up
        autoconnect.json
        registration.json
"""
from __future__ import annotations

import json
import os
import re
import secrets
import shutil
import time
from pathlib import Path
from typing import Optional

try:
    from cryptography.hazmat.primitives.ciphers.aead import AESGCM as _AESGCM
    _CRYPTO_AVAILABLE = True
except ImportError:
    _CRYPTO_AVAILABLE = False

DATA_DIR  = Path('/var/lib/valenius')
USERS_DIR = DATA_DIR / 'users'
STAGED_DIR = DATA_DIR / 'staged'
TEMP_DIR  = DATA_DIR / 'temp'
AUTOCONNECT_FILE  = DATA_DIR / 'autoconnect.json'
REGISTRATION_FILE = DATA_DIR / 'registration.json'
_KEY_FILE  = DATA_DIR / 'master.key'

_ENC_EXT   = '.conf.enc'
_PLAIN_EXT = '.conf'

_VALID_NAME = re.compile(r'^[A-Za-z0-9_-]{1,50}$')
_SAFE_CHAR  = re.compile(r'[^A-Za-z0-9_.-]')

_master_key: Optional[bytes] = None


# ── Encryption (AES-256-GCM, machine-local key) ───────────────────────────────

def _load_or_create_key() -> bytes:
    if _KEY_FILE.exists():
        return _KEY_FILE.read_bytes()
    key = secrets.token_bytes(32)
    _KEY_FILE.write_bytes(key)
    _KEY_FILE.chmod(0o400)
    return key


def _get_key() -> bytes:
    global _master_key
    if _master_key is None:
        _master_key = _load_or_create_key()
    return _master_key


def _encrypt(data: bytes) -> bytes:
    if not _CRYPTO_AVAILABLE:
        raise RuntimeError(
            "python3-cryptography is required for config encryption. "
            "Install: sudo apt install python3-cryptography"
        )
    nonce = os.urandom(12)
    ct = _AESGCM(_get_key()).encrypt(nonce, data, None)
    return nonce + ct


def _decrypt(data: bytes) -> bytes:
    if not _CRYPTO_AVAILABLE:
        raise RuntimeError(
            "python3-cryptography is required for config encryption. "
            "Install: sudo apt install python3-cryptography"
        )
    nonce, ct = data[:12], data[12:]
    return _AESGCM(_get_key()).decrypt(nonce, ct, None)


# ── Startup initialisation ────────────────────────────────────────────────────

def ensure_dirs() -> None:
    DATA_DIR.mkdir(mode=0o700, parents=True, exist_ok=True)
    USERS_DIR.mkdir(mode=0o700, parents=True, exist_ok=True)
    STAGED_DIR.mkdir(mode=0o700, parents=True, exist_ok=True)


def init_temp_dir() -> None:
    """Evict stale temp dir at startup. Mirrors Windows ConfigManager.InitTempDir.

    If deletion fails, rename the whole directory out of the way — rename only
    needs write access on the parent, not on the files inside.
    """
    if TEMP_DIR.exists():
        try:
            shutil.rmtree(TEMP_DIR)
        except Exception:
            try:
                TEMP_DIR.rename(Path(str(TEMP_DIR) + '.' + str(int(time.time()))))
            except Exception:
                pass  # fallback: reuse existing dir; prepare_config_path overwrites files
    TEMP_DIR.mkdir(mode=0o700, parents=True, exist_ok=True)


def migrate_plain_configs() -> None:
    """Encrypt any legacy plain .conf files in user dirs at daemon startup."""
    if not USERS_DIR.is_dir():
        return
    for user_dir in USERS_DIR.iterdir():
        if not user_dir.is_dir():
            continue
        for plain_path in list(user_dir.glob('*' + _PLAIN_EXT)):
            name = _profile_name_from_path(plain_path)
            if not name:
                continue
            enc_path = user_dir / (name + _ENC_EXT)
            if enc_path.exists():
                try:
                    plain_path.unlink()
                except Exception:
                    pass
                continue
            try:
                data = plain_path.read_bytes()
                enc_path.write_bytes(_encrypt(data))
                enc_path.chmod(0o600)
                plain_path.unlink()
            except Exception:
                pass  # leave plain file; retry next start


# ── Name / path helpers ───────────────────────────────────────────────────────

def is_valid_profile_name(name: Optional[str]) -> bool:
    return bool(name and _VALID_NAME.match(name))


def sanitize_profile_name(filename: str) -> str:
    stem = Path(filename).stem
    safe = re.sub(r'[^A-Za-z0-9_-]', '_', stem)
    return (safe or 'profile')[:50]


def _user_dir(username: str) -> Path:
    safe = _SAFE_CHAR.sub('_', username)
    return USERS_DIR / safe


def _profile_name_from_path(p: Path) -> Optional[str]:
    name = p.name
    if name.endswith(_ENC_EXT):
        stem = name[:-len(_ENC_EXT)]
    elif name.endswith(_PLAIN_EXT):
        stem = name[:-len(_PLAIN_EXT)]
    else:
        return None
    return stem if is_valid_profile_name(stem) else None


def profile_name_from_path(p: Path) -> Optional[str]:
    """Strip .conf or .conf.enc suffix to get the profile / tunnel name."""
    return _profile_name_from_path(p)


# ── Config content validation (local-user → root RCE guard) ───────────────────

# Only these [Interface]/[Peer] settings are permitted in a config the daemon will
# hand to `wg-quick up`. The daemon runs as root, and wg-quick executes the
# PreUp/PostUp/PreDown/PostDown directives as *arbitrary root shell commands* — so a
# config uploaded over the world-writable IPC socket (SO_PEERCRED authenticates the
# UID but every local user may connect) is otherwise a local-user → root RCE. We
# allowlist the keys the backend ever generates (WireGuardHelper.BuildClientConfig)
# plus the extra keys a hand-crafted `.conf` legitimately needs, and reject
# everything else — most importantly the script-hook directives and SaveConfig/Table.
_ALLOWED_CONFIG_KEYS = frozenset({
    'privatekey', 'address', 'dns', 'mtu', 'listenport', 'fwmark',      # [Interface]
    'publickey', 'presharedkey', 'endpoint', 'allowedips', 'persistentkeepalive',  # [Peer]
})
_ALLOWED_CONFIG_SECTIONS = frozenset({'[interface]', '[peer]'})


def validate_config_content(content: Optional[str]) -> Optional[str]:
    """Return None if `content` is a safe WireGuard config, else an error string.

    Enforces a strict key allowlist so that script-hook directives
    (PreUp/PostUp/PreDown/PostDown) and SaveConfig/Table — which wg-quick would run
    as root — can never reach `wg-quick up`. Applied both at the upload boundary and
    again in prepare_config_path() right before wg-quick reads the file, so
    admin-pushed / staged / foreign configs are filtered too (defense in depth).
    """
    if not content or not content.strip():
        return "Config content is empty."
    for raw in content.splitlines():
        line = raw.strip()
        if not line or line.startswith('#'):
            continue
        if line.startswith('['):
            if line.lower() not in _ALLOWED_CONFIG_SECTIONS:
                return f"Unsupported config section: {line}"
            continue
        eq = line.find('=')
        if eq < 0:
            return f"Malformed config line: {line}"
        key = line[:eq].strip().lower()
        if key not in _ALLOWED_CONFIG_KEYS:
            return f"Config directive not allowed: {line[:eq].strip()}"
    return None


# ── Managed-profile tracking ──────────────────────────────────────────────────

def _load_managed(username: str) -> list[str]:
    p = _user_dir(username) / 'managed.json'
    if not p.exists():
        return []
    try:
        return json.loads(p.read_text())
    except Exception:
        return []


def _save_managed(username: str, names: list[str]) -> None:
    p = _user_dir(username) / 'managed.json'
    p.write_text(json.dumps(names))
    p.chmod(0o600)


# ── Profile enumeration ───────────────────────────────────────────────────────

def get_profiles(username: str) -> list[str]:
    d = _user_dir(username)
    if not d.is_dir():
        return []
    names: set[str] = set()
    for p in d.iterdir():
        name = _profile_name_from_path(p)
        if name:
            names.add(name)
    return sorted(names)


def get_deletable_profiles(username: str) -> list[str]:
    managed = set(_load_managed(username))
    return [p for p in get_profiles(username) if p not in managed]


def is_managed(username: str, profile_name: str) -> bool:
    return profile_name in _load_managed(username)


def get_config_path(username: str, profile_name: str) -> Optional[Path]:
    """Return the stored path (.conf.enc preferred, .conf as fallback)."""
    d = _user_dir(username)
    enc   = d / (profile_name + _ENC_EXT)
    plain = d / (profile_name + _PLAIN_EXT)
    if enc.exists():
        return enc
    if plain.exists():
        return plain
    return None


def get_first_config_path(username: str) -> Optional[Path]:
    profiles = get_profiles(username)
    if not profiles:
        return None
    return get_config_path(username, profiles[0])


def get_all_profiles() -> list[str]:
    """Return all profiles across all users (for heartbeat payload)."""
    profiles: set[str] = set()
    if USERS_DIR.is_dir():
        for user_dir in USERS_DIR.iterdir():
            if user_dir.is_dir():
                for p in user_dir.iterdir():
                    name = _profile_name_from_path(p)
                    if name:
                        profiles.add(name)
    return sorted(profiles)


def order_profiles(profiles: list[str], server_profile_name: Optional[str]) -> list[str]:
    """Put the server-preferred profile first (mirrors Windows OrderProfiles)."""
    if not server_profile_name or len(profiles) < 2:
        return profiles
    try:
        idx = next(i for i, p in enumerate(profiles) if p.lower() == server_profile_name.lower())
    except StopIteration:
        return profiles
    if idx == 0:
        return profiles
    return [profiles[idx]] + profiles[:idx] + profiles[idx + 1:]


# ── WireGuard config preparation ──────────────────────────────────────────────

def prepare_config_path(stored_path: Path) -> Path:
    """Return a path wg-quick can read. Decrypts to TEMP_DIR if encrypted.

    wg-quick reads the config file only during 'up'; the interface stays active
    without it afterward.  Call cleanup_temp_config() after wg-quick up returns.
    """
    if not stored_path.name.endswith(_ENC_EXT):
        # Legacy plain config — still validate before handing it to wg-quick.
        err = validate_config_content(stored_path.read_text())
        if err:
            raise ValueError(f"Refusing unsafe config '{stored_path.name}': {err}")
        return stored_path
    profile_name = stored_path.name[:-len(_ENC_EXT)]
    temp_path = TEMP_DIR / (profile_name + _PLAIN_EXT)
    data = _decrypt(stored_path.read_bytes())
    err = validate_config_content(data.decode('utf-8', 'replace'))
    if err:
        raise ValueError(f"Refusing unsafe config '{profile_name}': {err}")
    try:
        if temp_path.exists():
            temp_path.unlink()
    except Exception:
        pass
    temp_path.write_bytes(data)
    temp_path.chmod(0o600)
    return temp_path


def cleanup_temp_config(tunnel_name: str) -> None:
    """Delete the decrypted temp file after wg-quick up returns."""
    temp_path = TEMP_DIR / (tunnel_name + _PLAIN_EXT)
    try:
        if temp_path.exists():
            temp_path.unlink()
    except Exception:
        pass


# ── Write / save ──────────────────────────────────────────────────────────────

def save_config(username: str, profile_name: str, content: str, managed: bool = False) -> None:
    d = _user_dir(username)
    d.mkdir(mode=0o700, parents=True, exist_ok=True)
    enc_path   = d / (profile_name + _ENC_EXT)
    plain_path = d / (profile_name + _PLAIN_EXT)
    enc_path.write_bytes(_encrypt(content.encode()))
    enc_path.chmod(0o600)
    if plain_path.exists():
        try:
            plain_path.unlink()
        except Exception:
            pass
    if managed:
        existing = _load_managed(username)
        if profile_name not in existing:
            _save_managed(username, existing + [profile_name])


def delete_user_profile(username: str, profile_name: str) -> bool:
    deleted = False
    for ext in (_PLAIN_EXT, _ENC_EXT):
        p = _user_dir(username) / (profile_name + ext)
        if p.exists():
            try:
                p.unlink()
                deleted = True
            except Exception:
                pass
    return deleted


def delete_profile(profile_name: str) -> bool:
    """Admin-initiated delete (mirrors Windows' ConfigManager.DeleteProfile): unlike
    delete_user_profile, this isn't scoped to one username — heartbeat_loop/poll_loop
    have no associated interactive user to pass in, so a per-user delete there always
    missed the file. Removes the profile from every local user directory, plus any
    unclaimed staged copy (otherwise the tray would just re-claim it right after)."""
    deleted = False
    if USERS_DIR.is_dir():
        for user_dir in USERS_DIR.iterdir():
            if not user_dir.is_dir():
                continue
            for ext in (_PLAIN_EXT, _ENC_EXT):
                p = user_dir / (profile_name + ext)
                if p.exists():
                    try:
                        p.unlink()
                        deleted = True
                    except Exception:
                        pass
    staged = STAGED_DIR / (profile_name + _PLAIN_EXT)
    if staged.exists():
        try:
            staged.unlink()
        except Exception:
            pass
    return deleted


# ── Staged config lifecycle ───────────────────────────────────────────────────

def has_staged_config() -> bool:
    return bool(list(STAGED_DIR.glob('*' + _PLAIN_EXT)))


def get_staged_profile_names() -> list[str]:
    return [p.stem for p in STAGED_DIR.glob('*' + _PLAIN_EXT) if is_valid_profile_name(p.stem)]


def save_staged_config(filename: str, content: str) -> None:
    """`filename` is a bare profile name (no extension) — the backend's
    PendingConfigFileName/FileName fields never carry one (mirrors Windows'
    ConfigManager.SaveStagedConfig, which does `profileName + PlainExt`).
    Every staged-config check in this module globs for `*.conf`, so without
    appending the extension here the file is written but silently invisible
    to has_staged_config()/claim_staged_config() forever."""
    STAGED_DIR.mkdir(mode=0o700, parents=True, exist_ok=True)
    p = STAGED_DIR / (filename + _PLAIN_EXT)
    p.write_text(content)
    p.chmod(0o600)


def claim_staged_config(username: str) -> list[str]:
    """Move all staged configs to the user's directory (marked managed, encrypted).

    Returns the list of profile names that were claimed.
    """
    claimed = []
    for staged in list(STAGED_DIR.glob('*' + _PLAIN_EXT)):
        name = sanitize_profile_name(staged.name)
        try:
            save_config(username, name, staged.read_text(), managed=True)
            staged.unlink()
            claimed.append(name)
        except Exception:
            pass  # leave staged file for next attempt
    return claimed


# ── AllowedIPs conflict checking ──────────────────────────────────────────────

def read_allowed_ips(stored_path: Path) -> list[str]:
    """Read AllowedIPs CIDRs from a stored config. Decrypts in-memory if .conf.enc.

    Returns [] on any error (fail-open — same as Windows ReadAllowedIPs).
    """
    try:
        if stored_path.name.endswith(_ENC_EXT):
            content = _decrypt(stored_path.read_bytes()).decode()
        else:
            content = stored_path.read_text()
        result = []
        for raw in content.splitlines():
            line = raw.strip()
            if not line.lower().startswith('allowedips'):
                continue
            eq = line.find('=')
            if eq < 0:
                continue
            for part in line[eq + 1:].split(','):
                part = part.strip()
                if part:
                    result.append(part)
        return result
    except Exception:
        return []


def _parse_cidr_v4(cidr: str) -> tuple[int, int]:
    addr, _, prefix = cidr.partition('/')
    prefix_len = int(prefix) if prefix else 32
    parts = addr.split('.')
    if len(parts) != 4:
        raise ValueError(f"Not IPv4: {cidr}")
    addr_int = 0
    for p in parts:
        addr_int = (addr_int << 8) | int(p)
    mask = (0xFFFFFFFF << (32 - prefix_len)) & 0xFFFFFFFF
    return addr_int & mask, prefix_len


def _cidrs_overlap(a: str, b: str) -> bool:
    try:
        a_net, a_pfx = _parse_cidr_v4(a)
        b_net, b_pfx = _parse_cidr_v4(b)
        min_pfx = min(a_pfx, b_pfx)
        mask = (0xFFFFFFFF << (32 - min_pfx)) & 0xFFFFFFFF
        return (a_net & mask) == (b_net & mask)
    except Exception:
        return False


def get_config_path_by_tunnel_name(tunnel_name: str) -> Optional[Path]:
    """Search all user dirs for a stored config matching the given tunnel name."""
    if not USERS_DIR.is_dir():
        return None
    for user_dir in USERS_DIR.iterdir():
        if not user_dir.is_dir():
            continue
        for ext in (_ENC_EXT, _PLAIN_EXT):
            p = user_dir / (tunnel_name + ext)
            if p.exists():
                return p
    return None


def find_allowed_ips_conflict(new_path: Path, active_tunnel_names: list[str]) -> Optional[str]:
    """Return the name of the first active tunnel whose AllowedIPs overlap new_path's,
    or None if there is no conflict.

    A full-tunnel (0.0.0.0/0) in either config blocks all coexistence.
    Mirrors Windows WireGuardController.FindAllowedIpsConflict.
    """
    new_cidrs = read_allowed_ips(new_path)
    if not new_cidrs or not active_tunnel_names:
        return None
    is_full_new = any(c.startswith('0.0.0.0/0') for c in new_cidrs)

    for name in active_tunnel_names:
        stored = get_config_path_by_tunnel_name(name)
        if stored is None:
            continue
        existing_cidrs = read_allowed_ips(stored)
        if any(c.startswith('0.0.0.0/0') for c in existing_cidrs) or is_full_new:
            return name
        for new_cidr in new_cidrs:
            for ex_cidr in existing_cidrs:
                if _cidrs_overlap(new_cidr, ex_cidr):
                    return name
    return None
