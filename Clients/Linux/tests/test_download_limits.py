import asyncio
import hashlib
import io
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from daemon import updater


class DownloadLimitsTests(unittest.TestCase):
    def test_limit_removes_partial_file_and_boundary_verifies(self):
        with tempfile.TemporaryDirectory() as directory:
            with patch.object(updater, '_UPDATE_DIR', directory), patch.object(updater, '_MAX_DOWNLOAD', 32):
                with patch.object(updater.urllib.request, 'urlopen', return_value=io.BytesIO(b'x' * 33)):
                    with self.assertRaisesRegex(ValueError, '512 MiB'):
                        updater._download('https://example.invalid/update', 'unused')
                self.assertEqual(list(Path(directory).iterdir()), [])
                data = b'x' * 32
                with patch.object(updater.urllib.request, 'urlopen', return_value=io.BytesIO(data)):
                    result = updater._download('https://example.invalid/update', hashlib.sha256(data).hexdigest())
                self.assertEqual(result.read_bytes(), data)
