"""Load daemon configuration from appsettings.json."""
from __future__ import annotations

import json
from dataclasses import dataclass

SETTINGS_PATH = '/etc/valenius/appsettings.json'


@dataclass
class Settings:
    backend_url: str
    api_key: str
    offline_access_days: int = 7

    @staticmethod
    def load(path: str = SETTINGS_PATH) -> Settings:
        # Tolerant of a missing file or missing keys: a fresh install may have no
        # BackendUrl yet (the tray prompts for it — see BackendClient.set_base_url),
        # and the daemon must still start so the tray can talk to it.
        try:
            with open(path) as f:
                raw = json.load(f)
        except (FileNotFoundError, ValueError):
            return Settings(backend_url='', api_key='')
        wt = raw.get('Valenius', raw)
        days = wt.get('OfflineAccessDays', 7)
        if type(days) is not int or not 1 <= days <= 365:
            raise ValueError('Valenius:OfflineAccessDays must be between 1 and 365')
        return Settings(
            backend_url=(wt.get('BackendUrl') or '').rstrip('/'),
            api_key=wt.get('ApiKey') or '',
            offline_access_days=days,
        )
