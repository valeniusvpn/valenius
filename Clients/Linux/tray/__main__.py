"""Valenius tray app entry point.

Usage:  python3 -m tray
"""
from __future__ import annotations

import fcntl
import logging
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

logging.basicConfig(
    level=logging.WARNING,
    format='%(asctime)s %(levelname)s: %(message)s',
    stream=sys.stderr,
)

from tray.app import TrayApp


def _acquire_single_instance_lock():
    """Hold an flock for the process lifetime; returns None if another tray already holds it.

    Needed now that the tray is launchable both from XDG autostart and from the
    application menu — without this, clicking the launcher while autostart already
    started the tray would spawn a second instance with a duplicate icon.
    """
    runtime_dir = os.environ.get('XDG_RUNTIME_DIR', '/tmp')
    lock_file = open(os.path.join(runtime_dir, 'valenius-tray.lock'), 'w')
    try:
        fcntl.flock(lock_file, fcntl.LOCK_EX | fcntl.LOCK_NB)
    except OSError:
        return None
    # Release the lock on exec: the self-restart-after-update path (TrayApp
    # re-execs via os.execv) must drop this lock so the fresh instance can
    # re-acquire it -- otherwise the inherited fd would still hold it and the
    # new tray would immediately exit as "already running".
    flags = fcntl.fcntl(lock_file, fcntl.F_GETFD)
    fcntl.fcntl(lock_file, fcntl.F_SETFD, flags | fcntl.FD_CLOEXEC)
    return lock_file


if __name__ == '__main__':
    _lock = _acquire_single_instance_lock()
    if _lock is None:
        print('Valenius tray is already running.', file=sys.stderr)
        sys.exit(0)
    TrayApp().run()
