"""Persist and restore the client registration state (ClientId, IsActive, standalone mode)."""
from __future__ import annotations

import json
import logging
import os
import tempfile

from daemon.config_manager import REGISTRATION_FILE
from daemon.state import DaemonState

log = logging.getLogger(__name__)


def load(state: DaemonState) -> None:
    """Load registration.json into state; generate a new ClientId if missing."""
    try:
        contents = REGISTRATION_FILE.read_text(encoding='utf-8')
    except FileNotFoundError:
        if not save(state):
            raise RuntimeError('Could not persist the new client identity')
        return
    except OSError as e:
        raise RuntimeError('Registration state is unreadable; refusing to replace the client identity') from e
    try:
        state.restore_registration(json.loads(contents))
    except (ValueError, TypeError, KeyError, AttributeError) as e:
        raise RuntimeError('Registration state is invalid; refusing to replace the client identity') from e
    log.info("Loaded ClientId %s (active=%s)", state.get_client_id(), state.is_registration_active())


def save(state: DaemonState) -> bool:
    """Flush one coherent snapshot and atomically replace the root-only cache."""
    temporary = None
    try:
        data = state.registration_snapshot()
        REGISTRATION_FILE.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
        fd, temporary = tempfile.mkstemp(prefix='.registration-', dir=REGISTRATION_FILE.parent)
        with os.fdopen(fd, 'w', encoding='utf-8') as stream:
            json.dump(data, stream, indent=2)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, REGISTRATION_FILE)
        temporary = None
        if os.name == 'posix':
            directory = os.open(REGISTRATION_FILE.parent, os.O_RDONLY | os.O_DIRECTORY)
            try:
                os.fsync(directory)
            finally:
                os.close(directory)
        return True
    except Exception as e:
        log.error("Could not persist registration cache: %s. Current authorization remains in memory; repair storage before restart.", e)
        return False
    finally:
        if temporary is not None:
            try:
                os.unlink(temporary)
            except OSError:
                pass
