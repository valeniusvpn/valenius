"""Per-user authorization, including actual kernel-authenticated Unix socket callers."""
import asyncio
import json
import os
import struct
import sys
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import AsyncMock, Mock, patch

import test_offline_access as cases
from daemon import config_manager as configs, registration, ipc_server
from daemon.auto_connect import AutoConnectConfig
from daemon.core import DaemonCore
from daemon.state import DaemonState
from shared.messages import CommandType, PipeCommand


@unittest.skipUnless(configs._CRYPTO_AVAILABLE, 'Requires runtime cryptography dependency')
class ProfileOwnershipTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        data = self.root / 'data'
        for name, value in {'DATA_DIR': data, 'USERS_DIR': data / 'users',
                'STAGED_DIR': data / 'staged', 'DEVICE_DIR': data / 'device',
                'TEMP_DIR': data / 'temp', '_KEY_FILE': data / 'master.key', '_master_key': None,
                'AUTOCONNECT_FILE': data / 'autoconnect.json'}.items():
            p = patch.object(configs, name, value)
            p.start()
            self.addCleanup(p.stop)
        p = patch.object(registration, 'REGISTRATION_FILE', data / 'registration.json')
        p.start()
        self.addCleanup(p.stop)
        configs.ensure_dirs()
        configs.init_temp_dir()
        self.content = ('[Interface]\nPrivateKey = AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=\n'
            'Address = 10.55.0.2/32\n[Peer]\nPublicKey = AQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQE=\n'
            'Endpoint = 127.0.0.1:51820\nAllowedIPs = 10.55.0.0/24\n')
        configs.save_config('alice', 'native', self.content)
        self.state = DaemonState(clock=cases.Clock())
        self.state.apply_backend_status(cases.status())
        backend = SimpleNamespace(is_configured=True, base_url='https://example.invalid',
            register=AsyncMock(return_value=None), get_wan_ip=AsyncMock(return_value=None),
            log_event=AsyncMock(), set_channel=Mock(), set_api_key=Mock())
        self.wg = SimpleNamespace(connect=AsyncMock(return_value=(True, '')),
            disconnect=AsyncMock(return_value=(True, '')), get_active_tunnels=AsyncMock(return_value=[]))
        self.core = DaemonCore(backend, self.state, self.wg, AutoConnectConfig(), Mock(), asyncio.get_running_loop())
        self.core._verify_after_connect = AsyncMock()
        self.server = ipc_server.IpcServer(self.core)

    async def dispatch(self, command, user='bob', profile='native', **kwargs):
        return await self.server._dispatch(PipeCommand(command, ProfileName=profile), user, **kwargs)

    async def test_cross_user_connect_and_internal_label_accounts_cannot_search_other_users(self):
        for user in ('bob', 'AutoConnect', 'PendingConnect', 'DeviceTunnel'):
            self.assertFalse((await self.dispatch(CommandType.Connect, user)).Success)
        self.wg.connect.assert_not_awaited()
        self.assertTrue((await self.dispatch(CommandType.Connect, 'alice')).Success)

    async def test_named_and_default_disconnect_reject_nonowner_with_same_name_profile(self):
        self.assertTrue((await self.dispatch(CommandType.Connect, 'alice')).Success)
        configs.save_config('bob', 'native', self.content)
        for profile in ('native', None):
            self.assertFalse((await self.dispatch(CommandType.Disconnect, profile=profile)).Success)
        self.assertFalse((await self.dispatch(CommandType.Connect)).Success)
        self.wg.disconnect.assert_not_awaited()
        self.assertTrue(self.state.can_control_tunnel('native', 'alice'))
        self.assertTrue((await self.dispatch(CommandType.Disconnect, 'alice')).Success)

    async def test_owner_comparison_is_exact_and_pseudo_name_is_not_a_capability(self):
        self.state.set_connected('native', 'alice')
        self.assertFalse((await self.dispatch(CommandType.Disconnect, 'Alice')).Success)
        self.state.set_connected('native', 'AutoConnect', system_owned=True)
        self.assertFalse((await self.dispatch(CommandType.Disconnect, 'AutoConnect')).Success)
        self.assertTrue((await self.dispatch(CommandType.Disconnect, 'root', privileged=True)).Success)

    async def test_delete_checks_own_profile_and_active_owner_before_network_or_storage_effects(self):
        self.assertTrue((await self.dispatch(CommandType.Connect, 'alice')).Success)
        self.assertFalse((await self.dispatch(CommandType.DeleteProfile)).Success)
        configs.save_config('bob', 'native', self.content)
        self.assertFalse((await self.dispatch(CommandType.DeleteProfile)).Success)
        self.wg.disconnect.assert_not_awaited()
        self.assertIsNotNone(configs.get_config_path('alice', 'native'))
        self.assertIsNotNone(configs.get_config_path('bob', 'native'))

    async def test_owner_delete_uses_config_teardown_and_failed_teardown_keeps_profile(self):
        self.assertTrue((await self.dispatch(CommandType.Connect, 'alice')).Success)
        self.wg.disconnect.return_value = False, 'injected failure'
        self.assertFalse((await self.dispatch(CommandType.DeleteProfile, 'alice')).Success)
        self.assertIsNotNone(configs.get_config_path('alice', 'native'))
        self.assertTrue(self.state.is_connected('native'))
        self.wg.disconnect.return_value = True, ''
        self.assertTrue((await self.dispatch(CommandType.DeleteProfile, 'alice')).Success)
        self.assertIsNone(configs.get_config_path('alice', 'native'))

    async def test_staged_claim_cannot_replace_another_users_active_tunnel(self):
        self.assertTrue((await self.dispatch(CommandType.Connect, 'alice')).Success)
        configs.save_staged_config('native', self.content)
        self.assertTrue((await self.dispatch(CommandType.GetConfigInfo)).Success)
        self.wg.disconnect.assert_not_awaited()
        self.wg.connect.assert_awaited_once()
        self.assertTrue(self.state.can_control_tunnel('native', 'alice'))

    async def test_actual_daemon_automation_can_search_other_users_without_granting_label_account_control(self):
        self.core.auto_connect.update_from_backend(True, True, 'native', [{'Subnet': '10.0.0.0/8'}])
        with patch('daemon.core.is_in_trusted_network', return_value=False):
            await self.core._auto_connect_action()
        self.wg.connect.assert_awaited_once()
        self.assertTrue(self.state.is_system_owned('native'))
        self.assertFalse((await self.dispatch(CommandType.Disconnect, 'AutoConnect')).Success)

    async def test_pending_disconnect_uses_explicit_privilege(self):
        self.state.set_connected('native', 'alice')
        await self.core._process_status_response(cases.status(PendingDisconnect=True))
        await asyncio.sleep(0)
        self.wg.disconnect.assert_awaited_once()
        self.assertFalse(self.state.is_connected('native'))

    async def test_reconciled_unknown_tunnel_requires_privileged_recovery(self):
        self.wg.get_active_tunnels.return_value = ['native']
        await self.core.reconcile_active_tunnels()
        for user in ('alice', 'Unknown'):
            self.assertFalse((await self.dispatch(CommandType.Disconnect, user)).Success)
        self.assertTrue((await self.dispatch(CommandType.Disconnect, 'root', privileged=True)).Success)

    async def test_concurrent_connects_cannot_overwrite_ownership(self):
        configs.save_config('bob', 'native', self.content)
        entered, release = asyncio.Event(), asyncio.Event()
        async def slow_start(path):
            entered.set()
            await release.wait()
            return True, ''
        self.wg.connect.side_effect = slow_start
        alice = asyncio.create_task(self.core.cmd_connect('alice', 'native'))
        await asyncio.wait_for(entered.wait(), 3)
        bob = asyncio.create_task(self.core.cmd_connect('bob', 'native'))
        await asyncio.sleep(0)
        release.set()
        self.assertTrue((await alice).Success)
        self.assertFalse((await bob).Success)
        self.wg.connect.assert_awaited_once()
        self.assertTrue(self.state.can_control_tunnel('native', 'alice'))

    async def test_invalid_profile_paths_are_rejected_before_claim_or_network(self):
        for name in ('../alice/native', '', 'native\n', '/native'):
            for command in (CommandType.Connect, CommandType.Disconnect, CommandType.DeleteProfile):
                self.assertFalse((await self.dispatch(command, profile=name)).Success)
        self.wg.connect.assert_not_awaited()
        self.wg.disconnect.assert_not_awaited()

    async def test_peer_names_that_alias_another_profile_directory_are_rejected(self):
        sock = Mock()
        sock.getsockopt.return_value = struct.pack('3I', 123, 1000, 1000)
        for name in ('domain\\alice', 'alice@example.com', '.', '..', '', 'álîce'):
            with self.subTest(name=name), patch.object(ipc_server.pwd, 'getpwuid', return_value=SimpleNamespace(pw_name=name)):
                with self.assertRaises(ValueError):
                    ipc_server._get_peer_identity(sock)
        with patch.object(ipc_server.pwd, 'getpwuid', return_value=SimpleNamespace(pw_name='alice.example-1')):
            self.assertEqual(('alice.example-1', 1000), ipc_server._get_peer_identity(sock))

    async def test_fallback_and_teardown_use_original_owners_config_and_preserve_ownership(self):
        configs.save_config('bob', 'native', self.content.replace('51820', '51821'))
        self.assertTrue((await self.dispatch(CommandType.Connect, 'alice')).Success)
        with patch.object(configs, 'get_config_path_by_tunnel_name', side_effect=AssertionError('cross-user lookup')):
            await self.core._try_switch_to_fallback_port('native', 443, 20)
            self.assertTrue((await self.dispatch(CommandType.Disconnect, 'alice')).Success)
        self.assertEqual(2, self.wg.connect.await_count)

    @unittest.skipUnless(os.geteuid() == 0, 'Privilege-dropping socket test requires isolated root container')
    async def test_real_socket_uses_kernel_uid_ignores_forged_fields_and_allows_root_recovery(self):
        self.root.chmod(0o755)
        socket_path = str(self.root / 'ipc.sock')
        probe = '''import json,socket,struct,sys
s=socket.socket(socket.AF_UNIX,socket.SOCK_STREAM); s.connect(sys.argv[1])
b=sys.argv[2].encode(); s.sendall(struct.pack('<i',len(b))+b)
def read(n):
 b=b''
 while len(b)<n:
  chunk=s.recv(n-len(b))
  if not chunk: raise EOFError('socket closed')
  b+=chunk
 return b
print(read(struct.unpack('<i',read(4))[0]).decode())
'''
        async def request(command, uid, profile='native'):
            payload = json.dumps({'Command': int(command), 'ProfileName': profile,
                'Username': 'alice', 'Uid': 0, 'Privileged': True})
            process = await asyncio.create_subprocess_exec(sys.executable, '-c', probe, socket_path,
                payload, user=uid, group=uid, stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE)
            out, err = await asyncio.wait_for(process.communicate(), 5)
            self.assertEqual(0, process.returncode, err.decode())
            return json.loads(out)
        with patch.object(ipc_server, 'SOCKET_PATH', socket_path):
            await self.server.start()
            try:
                self.assertFalse((await request(CommandType.Connect, 65534))['Success'])
                configs.save_config('nobody', 'own', self.content)
                self.assertTrue((await request(CommandType.Connect, 65534, 'own'))['Success'])
                self.assertTrue((await request(CommandType.Disconnect, 65534, 'own'))['Success'])
                self.state.set_connected('native', 'alice')
                self.assertFalse((await request(CommandType.Disconnect, 65534))['Success'])
                self.assertTrue((await request(CommandType.Disconnect, 0))['Success'])
            finally:
                await self.server.stop()
