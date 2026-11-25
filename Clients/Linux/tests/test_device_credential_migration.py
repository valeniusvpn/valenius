import asyncio
import os
import tempfile
import unittest
from unittest.mock import patch

from daemon import backend


class CredentialMigrationTests(unittest.TestCase):
    def test_failed_save_keeps_legacy_key_and_retry_is_private_and_restart_safe(self):
        loop = asyncio.new_event_loop()
        self.addCleanup(loop.close)
        with tempfile.TemporaryDirectory() as directory:
            path = os.path.join(directory, 'apikey')
            with patch.object(backend, '_API_KEY_FILE', path), patch.object(backend, '_BACKEND_URL_FILE', os.path.join(directory, 'backend')):
                client = backend.BackendClient('https://backend.invalid', 'legacy-key', loop)
                self.assertEqual('1', client._headers['X-Valenius-Device-Auth'])
                with patch.object(backend.os, 'replace', side_effect=OSError('synthetic failure')):
                    client.set_api_key('individual-key')
                self.assertEqual('legacy-key', client.api_key)
                self.assertFalse(os.path.exists(path))
                self.assertEqual([], os.listdir(directory))
                client.set_api_key('individual-key')
                self.assertEqual('individual-key', client.api_key)
                self.assertEqual('individual-key', backend.BackendClient('https://backend.invalid', 'legacy-key', loop).api_key)
                self.assertEqual(0o600, os.stat(path).st_mode & 0o777)
                with patch.object(backend.os, 'replace', side_effect=OSError('synthetic failure')):
                    client.set_api_key('later-key')
                self.assertEqual('individual-key', client.api_key)
                with open(path) as saved:
                    self.assertEqual('individual-key', saved.read())
