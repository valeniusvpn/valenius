"""Persist and restore the client registration state (ClientId, IsActive)."""
from __future__ import annotations

import json
import logging
from pathlib import Path
from uuid import UUID, uuid4

from daemon.config_manager import REGISTRATION_FILE
from daemon.state import DaemonState

log = logging.getLogger(__name__)


def load(state: DaemonState) -> None:
    """Load registration.json into state; generate a new ClientId if missing."""
    if not REGISTRATION_FILE.exists():
        _save(state)
        return
    try:
        d = json.loads(REGISTRATION_FILE.read_text())
        raw_id = d.get('ClientId') or d.get('clientId')
        if raw_id:
            state.set_client_id(UUID(raw_id))
        state.update_registration(d.get('IsActive', d.get('isActive', False)))
        log.info("Loaded ClientId %s (active=%s)", state.get_client_id(), state.is_registration_active())
    except Exception as e:
        log.warning("Could not read registration.json: %s — generating new client ID", e)
        _save(state)


def save(state: DaemonState) -> None:
    _save(state)


def _save(state: DaemonState) -> None:
    data = {
        'ClientId': str(state.get_client_id()),
        'IsActive': state.is_registration_active(),
        'LastCheckedUtc': None,
    }
    try:
        REGISTRATION_FILE.parent.mkdir(parents=True, exist_ok=True)
        REGISTRATION_FILE.write_text(json.dumps(data, indent=2))
        REGISTRATION_FILE.chmod(0o600)
    except Exception as e:
        log.error("Could not write registration.json: %s", e)
