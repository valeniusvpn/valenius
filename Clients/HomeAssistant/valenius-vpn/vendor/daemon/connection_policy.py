"""Versioned, reusable backend policy. Never cache enrollment secrets or challenges."""
from __future__ import annotations

from datetime import datetime, timezone


def utc_datetime(raw: str) -> datetime:
    value = datetime.fromisoformat(raw.replace('Z', '+00:00'))
    # Historical clients wrote naive UTC; always compare in UTC, including on
    # machines whose local timezone is not UTC.
    return value.astimezone(timezone.utc).replace(tzinfo=None) if value.tzinfo else value


def normalize_policy(source: dict, *, cached: bool = False) -> dict:
    """Validate an allowlisted policy; a legacy/partial cache is not authorization."""
    if not isinstance(source, dict):
        raise ValueError('Missing connection policy')
    def get(key, default=None):
        if cached and key not in source:
            raise ValueError(f'Missing cached policy field: {key}')
        return source.get(key, source.get(key[0].lower() + key[1:], default))

    if cached and (type(source.get('Version')) is not int or source['Version'] != 1):
        raise ValueError('Unsupported cached connection policy')
    policy = {'Version': 1}
    for key in ('ServerProfileName', 'ServerVpnIp', 'ServerHealthUrl', 'MfaAuthorizeUrl', 'MfaSessionExpiresAt'):
        value = get(key)
        if value is not None and not isinstance(value, str):
            raise ValueError(f'Invalid {key}')
        policy[key] = value
    if policy['MfaSessionExpiresAt'] is not None:
        utc_datetime(policy['MfaSessionExpiresAt'])
    for key in ('DeviceTunnelEnabled', 'MfaRequired', 'MfaEnrollmentOpen'):
        value = get(key, False)
        if type(value) is not bool:
            raise ValueError(f'Invalid {key}')
        policy[key] = value
    for key, strings, integers in (
        ('RemoteLanCidrsByProfile', ('ProfileName',), ()),
        ('FallbackEndpoints', ('ProfileName',), ('Port', 'TriggerSeconds')),
    ):
        rows = get(key, []) or []
        if not isinstance(rows, list):
            raise ValueError(f'Invalid {key}')
        normalized = []
        for row in rows:
            if not isinstance(row, dict):
                raise ValueError(f'Invalid {key} entry')
            item = {}
            for field in strings + integers:
                value = row.get(field, row.get(field[0].lower() + field[1:]))
                if field == 'TriggerSeconds' and value is None:
                    value = 20
                if not (isinstance(value, str) if field in strings else type(value) is int):
                    raise ValueError(f'Invalid {key}.{field}')
                item[field] = value
            if key == 'RemoteLanCidrsByProfile':
                cidrs = row.get('Cidrs', row.get('cidrs', []))
                if not isinstance(cidrs, list) or not all(isinstance(c, str) for c in cidrs):
                    raise ValueError('Invalid remote LAN CIDRs')
                item['Cidrs'] = list(cidrs)
            normalized.append(item)
        policy[key] = normalized
    return policy
