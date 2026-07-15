"""Load daemon configuration from appsettings.json."""
from __future__ import annotations

import json
from dataclasses import dataclass

SETTINGS_PATH = '/etc/valenius/appsettings.json'


@dataclass
class Settings:
    backend_url: str
    api_key: str

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
        return Settings(
            backend_url=(wt.get('BackendUrl') or '').rstrip('/'),
            api_key=wt.get('ApiKey') or '',
        )
