"""Initial enrollment is administrator-owned; an existing backend cannot be reassigned."""
import asyncio
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import AsyncMock, Mock, patch

from daemon import backend as backend_mod, config_manager as configs, ipc_server, registration
from daemon.auto_connect import AutoConnectConfig
from daemon.backend import BackendClient
from daemon.core import DaemonCore
from daemon.state import DaemonState
from shared.messages import CommandType, PipeCommand


class ProvisioningTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.url_file = self.root / 'data/backend_url'
        for obj, name, value in ((backend_mod, '_BACKEND_URL_FILE', str(self.url_file)),
                (backend_mod, '_API_KEY_FILE', str(self.root / 'data/apikey')),
                (registration, 'REGISTRATION_FILE', self.root / 'data/registration.json'),
                (configs, 'USERS_DIR', self.root / 'data/users')):
            p = patch.object(obj, name, value)
            p.start()
            self.addCleanup(p.stop)
        self.backend = BackendClient('', 'test-bootstrap-key', asyncio.get_running_loop())
        self.backend.get_version = AsyncMock(return_value=None)
        self.backend.register = AsyncMock(return_value=None)
        self.state = DaemonState()
        self.core = DaemonCore(self.backend, self.state, Mock(), AutoConnectConfig(), Mock(), asyncio.get_running_loop())
        self.server = ipc_server.IpcServer(self.core)

    async def test_direct_core_and_forged_dispatch_cannot_enroll_or_choose_update_source(self):
        for command in (CommandType.SetBackendUrl, CommandType.SetStandaloneMode):
            raw = json.dumps({'Command': int(command), 'BackendDns': 'attacker.invalid',
                'Uid': 0, 'Privileged': True, 'Username': 'root'})
            response = await self.server._dispatch(PipeCommand.from_json(raw), 'root')
            self.assertFalse(response.Success)
            self.assertIn('administrator', response.Error)
        self.assertFalse((await self.core.cmd_set_backend_url('attacker.invalid')).Success)
        self.assertFalse((await self.core.cmd_set_standalone_mode()).Success)
        self.assertFalse(self.backend.is_configured)
        self.assertFalse(self.state.get_standalone_mode_chosen())
        self.assertFalse(self.url_file.exists())
        self.backend.get_version.assert_not_awaited()
        self.backend.register.assert_not_awaited()

    async def test_provider_requires_explicit_privilege_even_when_core_is_bypassed(self):
        with self.assertRaises(PermissionError):
            self.backend.set_base_url('attacker.invalid')
        self.assertFalse(self.backend.is_configured)

    async def test_initial_administrator_provisioning_is_private_persistent_and_write_once(self):
        response = await self.core.cmd_set_backend_url('original.invalid', privileged=True)
        self.assertTrue(response.Success)
        self.assertEqual('https://original.invalid', self.backend.base_url)
        self.assertEqual(0o600, self.url_file.stat().st_mode & 0o777)
        before = self.url_file.read_bytes()
        with self.assertRaisesRegex(ValueError, 'reassignment'):
            await self.core.cmd_set_backend_url('attacker.invalid', privileged=True)
        fresh = BackendClient('', 'test', asyncio.get_running_loop())
        self.assertEqual('https://original.invalid', fresh.base_url)
        self.assertEqual(before, self.url_file.read_bytes())
        self.assertEqual('test-bootstrap-key', self.backend.api_key)

    async def test_installer_supplied_backend_cannot_be_reassigned_or_select_standalone(self):
        self.backend._base = 'https://original.invalid'
        before = self.state.registration_snapshot()
        with self.assertRaises(ValueError):
            self.backend.set_base_url('attacker.invalid', privileged=True)
        self.assertFalse((await self.core.cmd_set_standalone_mode(privileged=True)).Success)
        self.assertEqual(before, self.state.registration_snapshot())
        self.assertEqual('https://original.invalid', self.backend.base_url)

    async def test_failed_persistence_does_not_select_an_ephemeral_origin(self):
        with patch.object(backend_mod.os, 'replace', side_effect=OSError('injected failure')):
            with self.assertRaises(OSError):
                self.backend.set_base_url('original.invalid', privileged=True)
        self.assertFalse(self.backend.is_configured)
        self.assertFalse(self.url_file.exists())
        self.assertEqual([], list(self.url_file.parent.glob('.backend-*')))

    async def test_unreadable_empty_or_dangling_saved_origin_cannot_fall_back_to_another_origin(self):
        self.url_file.parent.mkdir()
        self.url_file.write_text('')
        with self.assertRaises(RuntimeError):
            BackendClient('https://fallback.invalid', 'test', asyncio.get_running_loop())
        self.url_file.unlink()
        self.url_file.symlink_to(self.root / 'missing')
        with self.assertRaises(RuntimeError):
            BackendClient('https://fallback.invalid', 'test', asyncio.get_running_loop())
        with patch('builtins.open', side_effect=PermissionError('injected read failure')):
            with self.assertRaises(RuntimeError):
                BackendClient('https://fallback.invalid', 'test', asyncio.get_running_loop())

    async def test_only_administrator_can_choose_standalone_before_backend_enrollment(self):
        self.assertTrue((await self.core.cmd_set_standalone_mode(privileged=True)).Success)
        fresh = DaemonState()
        registration.load(fresh)
        self.assertTrue(fresh.get_standalone_mode_chosen())
        self.assertFalse(self.backend.is_configured)

    @unittest.skipUnless(os.geteuid() == 0, 'Real UID tests require an isolated root container')
    async def test_real_socket_denies_nonroot_with_forged_privilege_and_root_helper_provisions(self):
        self.root.chmod(0o755)
        path = str(self.root / 'ipc.sock')
        source = str(Path(backend_mod.__file__).resolve().parents[1])
        helper = 'import sys; sys.path.insert(0,sys.argv[1]); from daemon.provision import main; sys.exit(main(sys.argv[3:],socket_path=sys.argv[2]))'
        raw_probe = '''import json,socket,struct,sys
s=socket.socket(socket.AF_UNIX,socket.SOCK_STREAM); s.settimeout(5); s.connect(sys.argv[1])
b=sys.argv[2].encode(); s.sendall(struct.pack('<i',len(b))+b)
def read(n):
 b=b''
 while len(b)<n:
  c=s.recv(n-len(b))
  if not c: raise EOFError()
  b+=c
 return b
print(read(struct.unpack('<i',read(4))[0]).decode())
'''
        async def child(args, uid):
            process = await asyncio.create_subprocess_exec(sys.executable, '-I', '-c', *args,
                user=uid, group=uid, stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE)
            out, err = await asyncio.wait_for(process.communicate(), 5)
            return process.returncode, out.decode(), err.decode()
        with patch.object(ipc_server, 'SOCKET_PATH', path):
            await self.server.start()
            try:
                for command in (CommandType.SetBackendUrl, CommandType.SetStandaloneMode):
                    payload = json.dumps({'Command': int(command), 'BackendDns': 'attacker.invalid', 'Privileged': True, 'Uid': 0})
                    code, out, err = await child([raw_probe, path, payload], 65534)
                    self.assertEqual(0, code, err)
                    self.assertFalse(json.loads(out)['Success'])
                self.assertFalse(self.url_file.exists())
                self.backend.get_version.assert_not_awaited()
                code, out, err = await child([helper, source, path, 'backend', 'original.invalid'], 0)
                self.assertEqual(0, code, err)
                self.assertEqual('https://original.invalid', self.backend.base_url)
                code, out, err = await child([helper, source, path, 'backend', 'attacker.invalid'], 0)
                self.assertEqual(1, code)
                self.assertIn('reassignment', err)
                code, out, err = await child([helper, source, path, 'standalone'], 65534)
                self.assertEqual(1, code)
                self.assertIn('root', err)
            finally:
                await self.server.stop()
