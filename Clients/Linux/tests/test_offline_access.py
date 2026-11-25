"""Offline authorization regressions. No root, network, GTK or VPN operations."""
import asyncio
import json
import os
import tempfile
import unittest
from datetime import datetime, timedelta
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import AsyncMock, Mock, patch

from daemon import registration
from daemon import config_manager as real_configs
from daemon.auto_connect import AutoConnectConfig
from daemon.connection_policy import utc_datetime
from daemon.core import DaemonCore
from daemon.state import DaemonState
from shared.messages import TunnelStatus


class Clock:
    def __init__(self):
        self.now = datetime(2026, 10, 26, 12)

    def __call__(self):
        return self.now


def status(**changes):
    return {'IsActive': True, 'ServerProfileName': 'native', 'ServerVpnIp': '10.0.0.1',
            'ServerHealthUrl': 'https://10.0.0.1:8444/health',
            'RemoteLanCidrsByProfile': [{'ProfileName': 'native', 'Cidrs': ['10.20.0.0/24']}],
            'FallbackEndpoints': [{'ProfileName': 'native', 'Port': 443, 'TriggerSeconds': 30}],
            **changes}


class PersistenceTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.path = Path(self.directory.name) / 'data' / 'registration.json'
        self.patch = patch.object(registration, 'REGISTRATION_FILE', self.path)
        self.patch.start()
        self.addCleanup(self.patch.stop)
        self.clock = Clock()
        self.state = DaemonState(clock=self.clock)

    def restart(self):
        fresh = DaemonState(clock=self.clock)
        registration.load(fresh)
        self.assertEqual(self.state.get_client_id(), fresh.get_client_id())
        return fresh

    def test_contract_fixture_cases(self):
        path = Path(__file__).resolve().parents[2] / 'tests' / 'offline-access-cases.json'
        for case in json.loads(path.read_text()):
            with self.subTest(case=case['name']):
                clock = Clock()
                state = DaemonState(case['days'], clock=clock)
                state.apply_backend_status(status(MfaRequired=case.get('mfaRequired', False),
                    MfaSessionExpiresAt=case.get('mfaExpires')))
                snapshot = state.registration_snapshot()
                snapshot['IsActive'] = case['active']
                snapshot['LastCheckedUtc'] = case['checked']
                state.restore_registration(snapshot)
                clock.now = utc_datetime(case['now'])
                self.assertEqual(case['activation'], state.is_allowed_to_connect())
                self.assertEqual(case['native'], state.is_profile_allowed_to_connect('native'))
                self.assertEqual(case['activation'], state.is_profile_allowed_to_connect('foreign'))

    def test_repeated_restarts_and_unrelated_saves_never_renew_deadline(self):
        self.state.apply_backend_status(status())
        deadline = self.state.offline_access_expires_at()
        self.assertTrue(registration.save(self.state))
        for day in (1, 3, 6, 7, 30):
            self.clock.now = datetime(2026, 10, 26, 12) + timedelta(days=day)
            self.state = self.restart()
            self.state.set_standalone_mode_chosen(True)
            registration.save(self.state)
            self.assertEqual(deadline, self.state.offline_access_expires_at())
            self.assertEqual(day < 7, self.state.is_allowed_to_connect())

    def test_explicit_deactivation_survives_restart_and_network_failure(self):
        self.state.apply_backend_status(status())
        registration.save(self.state)
        self.state.apply_backend_status(status(IsActive=False))
        registration.save(self.state)
        fresh = self.restart()
        fresh.set_backend_reachable(False)
        self.assertFalse(fresh.is_allowed_to_connect())
        self.assertIsNone(fresh.offline_access_expires_at())

    def test_only_successful_backend_refresh_renews_deadline(self):
        self.state.apply_backend_status(status())
        first = self.state.offline_access_expires_at()
        self.clock.now += timedelta(days=2)
        self.state.set_backend_reachable(False)
        self.assertEqual(first, self.state.offline_access_expires_at())
        self.state.apply_backend_status(status())
        self.assertEqual('2026-11-04T12:00:00Z', self.state.offline_access_expires_at())

    def test_legacy_cache_preserves_identity_and_flags_but_requires_refresh(self):
        self.path.parent.mkdir()
        legacy = {'ClientId': str(self.state.get_client_id()), 'IsActive': True,
                  'LastCheckedUtc': None, 'StandaloneModeChosen': True, 'ProfilesTransferredToBackend': True}
        self.path.write_text(json.dumps(legacy))
        fresh = self.restart()
        self.assertFalse(fresh.is_allowed_to_connect())
        self.assertTrue(fresh.get_standalone_mode_chosen())
        self.assertTrue(fresh.get_profiles_transferred())
        fresh.apply_backend_status(status())
        self.assertTrue(fresh.is_allowed_to_connect())

    def test_partial_unknown_or_malformed_policy_fails_closed_without_identity_loss(self):
        self.state.apply_backend_status(status())
        for change in ({'ConnectionPolicy': None}, {'ConnectionPolicy': {'Version': 99}},
                       {'LastCheckedUtc': 'not-a-date'}, {'LastCheckedUtc': None}):
            with self.subTest(change=change):
                fresh = DaemonState(clock=self.clock)
                fresh.restore_registration({**self.state.registration_snapshot(), **change})
                self.assertEqual(self.state.get_client_id(), fresh.get_client_id())
                self.assertFalse(fresh.is_allowed_to_connect())

    def test_non_boolean_activation_is_never_approval(self):
        for value in ('false', 'true', 1, None):
            with self.subTest(value=value):
                self.state.apply_backend_status(status(IsActive=value))
                self.assertFalse(self.state.is_allowed_to_connect())

    def test_bad_mfa_deadline_denies_instead_of_becoming_non_mfa(self):
        self.state.apply_backend_status(status(MfaSessionExpiresAt='broken'))
        self.assertFalse(self.state.is_allowed_to_connect())
        registration.save(self.state)
        self.assertFalse(self.restart().is_allowed_to_connect())

    def test_mfa_deadline_and_policy_survive_reboot_without_secrets(self):
        self.state.apply_backend_status(status(DeviceTunnelEnabled=True, MfaSessionExpiresAt='2026-10-26T13:00:00Z',
            MfaAuthorizeUrl='https://example.invalid/mfa', MfaEnrollmentOpen=True,
            MfaEnrollmentUri='otpauth://secret-marker', MfaApproveNumber=42))
        registration.save(self.state)
        self.assertNotIn('secret-marker', self.path.read_text())
        self.assertNotIn('MfaApproveNumber', self.path.read_text())
        fresh = self.restart()
        self.assertIsNone(fresh.get_mfa_state()[4])
        self.assertIsNone(fresh.get_mfa_state()[5])
        self.assertTrue(fresh.get_device_tunnel_enabled())
        self.assertEqual(('10.0.0.1', 8444), fresh.get_gateway_probe('native'))
        self.assertEqual(['10.20.0.0/24'], fresh.get_remote_lan_cidrs('native'))
        self.assertEqual((443, 30), fresh.get_fallback_config('native'))
        self.assertTrue(fresh.is_profile_allowed_to_connect('native'))
        self.clock.now += timedelta(hours=1)
        self.assertTrue(fresh.get_mfa_state()[0])
        self.assertFalse(fresh.is_profile_allowed_to_connect('native'))
        self.assertTrue(fresh.is_profile_allowed_to_connect('foreign'))

    def test_mfa_without_known_native_profile_cannot_be_bypassed_by_profile_choice(self):
        self.state.apply_backend_status(status(ServerProfileName=None, MfaRequired=True))
        self.assertFalse(self.state.is_profile_allowed_to_connect('foreign'))

    def test_replace_failure_preserves_previous_file_and_current_memory(self):
        self.state.apply_backend_status(status())
        registration.save(self.state)
        before = self.path.read_bytes()
        self.state.apply_backend_status(status(IsActive=False))
        with patch.object(registration.os, 'replace', side_effect=PermissionError('injected replace failure')):
            self.assertFalse(registration.save(self.state))
        self.assertFalse(self.state.is_allowed_to_connect())
        self.assertEqual(before, self.path.read_bytes())
        self.assertEqual([], list(self.path.parent.glob('.registration-*')))

    def test_interrupted_write_and_fsync_failure_do_not_truncate_cache(self):
        self.state.apply_backend_status(status())
        registration.save(self.state)
        before = self.path.read_bytes()
        for target in ('json.dump', 'os.fsync'):
            with self.subTest(target=target), patch('daemon.registration.' + target, side_effect=OSError('injected write failure')):
                self.assertFalse(registration.save(self.state))
                self.assertEqual(before, self.path.read_bytes())
        self.assertEqual([], list(self.path.parent.glob('.registration-*')))

    def test_corrupt_or_unreadable_existing_file_is_never_replaced_with_new_identity(self):
        self.path.parent.mkdir()
        self.path.write_text('{corrupt')
        before = self.path.read_bytes()
        with self.assertRaises(RuntimeError):
            registration.load(self.state)
        self.assertEqual(before, self.path.read_bytes())

        with patch.object(Path, 'read_text', side_effect=PermissionError('injected read failure')):
            with self.assertRaises(RuntimeError):
                registration.load(self.state)
        self.assertEqual(before, self.path.read_bytes())

    def test_empty_client_identity_is_not_accepted_or_replaced(self):
        self.path.parent.mkdir()
        self.path.write_text(json.dumps({'ClientId': '00000000-0000-0000-0000-000000000000', 'IsActive': True}))
        before = self.path.read_bytes()
        with self.assertRaises(RuntimeError):
            registration.load(self.state)
        self.assertEqual(before, self.path.read_bytes())

    def test_new_identity_must_be_persisted_before_use(self):
        with patch.object(registration.os, 'replace', side_effect=OSError('injected creation failure')):
            with self.assertRaises(RuntimeError):
                registration.load(self.state)
        self.assertFalse(self.path.exists())

    @unittest.skipUnless(os.name == 'posix', 'POSIX permission contract')
    def test_cache_and_temporary_file_are_private_before_replacement(self):
        replace = registration.os.replace
        def inspect(source, destination):
            self.assertEqual(0o600, os.stat(source).st_mode & 0o777)
            replace(source, destination)
        with patch.object(registration.os, 'replace', side_effect=inspect):
            self.assertTrue(registration.save(self.state))
        self.assertEqual(0o600, self.path.stat().st_mode & 0o777)
        self.assertEqual(0o700, self.path.parent.stat().st_mode & 0o777)

    def test_settings_default_valid_and_invalid_periods(self):
        from daemon.settings import Settings
        path = Path(self.directory.name) / 'appsettings.json'
        self.assertEqual(7, Settings.load(str(path)).offline_access_days)
        for days in (1, 7, 365):
            path.write_text(json.dumps({'Valenius': {'OfflineAccessDays': days}}))
            self.assertEqual(days, Settings.load(str(path)).offline_access_days)
        for days in (0, 366, -1, True, '7', 7.5, None):
            with self.subTest(days=days):
                path.write_text(json.dumps({'Valenius': {'OfflineAccessDays': days}}))
                with self.assertRaises(ValueError):
                    Settings.load(str(path))
                with self.assertRaises(ValueError):
                    DaemonState(days)


class DaemonFlowTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.path = Path(self.directory.name) / 'registration.json'
        self.patch = patch.object(registration, 'REGISTRATION_FILE', self.path)
        self.patch.start()
        self.addCleanup(self.patch.stop)
        self.clock = Clock()
        self.state = DaemonState(clock=self.clock)
        self.state.apply_backend_status(status())
        self.backend = SimpleNamespace(is_configured=True, base_url='https://example.invalid',
            register=AsyncMock(return_value=None), get_wan_ip=AsyncMock(return_value=None),
            log_event=AsyncMock(), set_channel=Mock(), set_api_key=Mock())
        self.wg = SimpleNamespace(connect=AsyncMock(return_value=(True, '')), get_active_tunnels=AsyncMock(return_value=[]))
        self.auto = AutoConnectConfig()
        self.core = DaemonCore(self.backend, self.state, self.wg, self.auto, Mock(), asyncio.get_running_loop())
        self.core.persist_auto_connect = Mock()
        self.core._verify_after_connect = AsyncMock()
        self.core._wg_down = AsyncMock(return_value=(True, ''))
        self.core._ensure_device_tunnel_config = AsyncMock()
        self.configs = SimpleNamespace(is_valid_profile_name=real_configs.is_valid_profile_name,
            claim_staged_config=Mock(), get_config_path=Mock(return_value=Path('/fake/native.conf.enc')),
            get_config_path_by_tunnel_name=Mock(return_value=Path('/fake/native.conf.enc')),
            get_device_tunnel_config_path=Mock(return_value=Path('/fake/device/native.conf.enc')),
            get_first_config_path=Mock(return_value=Path('/fake/native.conf.enc')),
            profile_name_from_path=Mock(return_value='native'), prepare_config_path=Mock(return_value=Path('/fake/native.conf')),
            cleanup_temp_config=Mock(), find_allowed_ips_conflict=Mock(return_value=None),
            find_local_lan_conflict=Mock(return_value=None), get_all_profiles=Mock(return_value=['native']),
            order_profiles=Mock(return_value=['native']), get_profiles=Mock(return_value=['native']),
            get_deletable_profiles=Mock(return_value=[]), has_staged_config=Mock(return_value=False))
        p = patch('daemon.core.configs_mod', self.configs)
        p.start()
        self.addCleanup(p.stop)

    def reboot(self):
        registration.save(self.state)
        fresh = DaemonState(clock=self.clock)
        registration.load(fresh)
        self.state = fresh
        self.core.state = fresh

    async def test_cached_non_mfa_connect_after_restart_does_not_require_backend(self):
        self.reboot()
        self.state.set_backend_reachable(False)
        self.clock.now += timedelta(days=6)
        self.assertTrue((await self.core.cmd_connect('alice', 'native')).Success)
        self.backend.register.assert_not_awaited()
        self.wg.connect.assert_awaited_once()

    async def test_expired_cache_denies_offline_connect_and_reports_refresh_requirement(self):
        self.clock.now += timedelta(days=7)
        result = await self.core.cmd_connect('alice', 'native')
        self.assertFalse(result.Success)
        self.assertIn('expired', result.Error)
        self.backend.register.assert_awaited_once()
        self.wg.connect.assert_not_awaited()

    async def test_live_refresh_on_connect_applies_mfa_before_starting_tunnel(self):
        self.clock.now += timedelta(days=8)
        self.backend.register.return_value = status(MfaRequired=True)
        result = await self.core.cmd_connect('alice', 'native')
        self.assertFalse(result.Success)
        self.assertIn('MFA', result.Error)
        self.wg.connect.assert_not_awaited()
        self.assertTrue(json.loads(self.path.read_text())['ConnectionPolicy']['MfaRequired'])

    async def test_explicit_denial_cannot_be_bypassed_by_internal_connect_identities(self):
        self.state.apply_backend_status(status(IsActive=False))
        for username in ('alice', 'AutoConnect', 'PendingConnect', 'DeviceTunnel'):
            with self.subTest(username=username):
                self.assertFalse((await self.core.cmd_connect(username, 'native')).Success)
        self.wg.connect.assert_not_awaited()

    async def test_mfa_expiry_after_reboot_blocks_native_but_not_foreign_profile(self):
        self.state.apply_backend_status(status(MfaSessionExpiresAt='2026-10-26T13:00:00Z'))
        self.reboot()
        self.clock.now += timedelta(hours=1)
        self.assertFalse((await self.core.cmd_connect('alice', 'native')).Success)
        self.configs.profile_name_from_path.return_value = 'foreign'
        self.assertTrue((await self.core.cmd_connect('alice', 'foreign')).Success)

    async def test_revocation_during_wan_lookup_is_rechecked_before_wg_start(self):
        async def revoke():
            self.state.apply_backend_status(status(IsActive=False))
        self.backend.get_wan_ip.side_effect = revoke
        self.assertFalse((await self.core.cmd_connect('alice', 'native')).Success)
        self.wg.connect.assert_not_awaited()

    async def test_revocation_during_wg_start_is_compensated_with_teardown(self):
        async def revoke(_):
            self.state.apply_backend_status(status(IsActive=False))
            return True, ''
        self.wg.connect.side_effect = revoke
        self.assertFalse((await self.core.cmd_connect('alice', 'native')).Success)
        self.core._wg_down.assert_awaited_once_with('native')
        self.assertFalse(self.state.is_connected('native'))
        self.configs.cleanup_temp_config.assert_called_once_with('native')

    async def test_failed_compensating_teardown_remains_visible(self):
        async def revoke(_):
            self.state.apply_backend_status(status(IsActive=False))
            return True, ''
        self.wg.connect.side_effect = revoke
        self.core._wg_down.return_value = False, 'injected stop failure'
        self.assertFalse((await self.core.cmd_connect('alice', 'native')).Success)
        self.assertTrue(self.state.is_connected('native'))

    async def test_fallback_cannot_reinstall_after_revocation(self):
        self.state.set_connected('native', 'alice')
        self.state.apply_backend_status(status(IsActive=False))
        await self.core._try_switch_to_fallback_port('native', 443, 20)
        self.wg.connect.assert_not_awaited()
        self.assertFalse(self.state.is_connected('native'))

    async def test_allowed_fallback_preserves_owner_and_records_alternate_port(self):
        self.state.set_connected('native', 'alice')
        await self.core._try_switch_to_fallback_port('native', 443, 20)
        self.wg.connect.assert_awaited_once()
        self.configs.prepare_config_path.assert_called_once_with(Path('/fake/native.conf.enc'), endpoint_port_override=443)
        self.assertEqual('alice', self.state.get_tunnel('native').connected_user)
        self.assertEqual(443, self.state.get_all_connected()[0].FallbackPortUsed)

    async def test_config_replacement_cannot_reinstall_after_revocation(self):
        self.state.set_connected('native', 'alice')
        self.state.apply_backend_status(status(IsActive=False))
        await self.core._reconnect_if_replaced('alice', ['native'])
        self.wg.connect.assert_not_awaited()
        self.assertFalse(self.state.is_connected('native'))

    async def test_allowed_config_replacement_still_reconnects(self):
        self.state.set_connected('native', 'alice')
        await self.core._reconnect_if_replaced('alice', ['native'])
        self.wg.connect.assert_awaited_once()
        self.assertTrue(self.state.is_connected('native'))

    async def test_pending_connect_and_auto_connect_use_activation_gate(self):
        await self.core._process_status_response(status(IsActive=False, PendingConnect=True))
        await asyncio.sleep(0)
        self.auto.update_from_backend(True, True, 'native', [{'Subnet': '10.0.0.0/8'}])
        with patch('daemon.core.is_in_trusted_network', return_value=False):
            await self.core._auto_connect_action()
        self.wg.connect.assert_not_awaited()

    async def test_cached_device_tunnel_policy_and_machine_config_work_offline(self):
        self.state.apply_backend_status(status(DeviceTunnelEnabled=True))
        self.reboot()
        self.assertIn(('DeviceTunnel', 'native'), self.core._get_auto_connect_targets())
        self.configs.get_config_path.return_value = None
        self.configs.get_config_path_by_tunnel_name.return_value = None
        self.assertTrue((await self.core.cmd_connect('DeviceTunnel', 'native', privileged=True)).Success)
        self.configs.get_device_tunnel_config_path.assert_called_once_with('native')

    async def test_heartbeat_poll_and_live_registration_persist_same_complete_snapshot(self):
        response = {key[0].lower() + key[1:]: value for key, value in status(MfaRequired=True).items()}
        self.backend.register.return_value = response
        await self.core.heartbeat_once()
        first = json.loads(self.path.read_text())
        self.backend.poll = AsyncMock(side_effect=[response, asyncio.CancelledError()])
        with patch('daemon.core.asyncio.sleep', new=AsyncMock()):
            with self.assertRaises(asyncio.CancelledError):
                await self.core.poll_loop()
        self.assertEqual(first, json.loads(self.path.read_text()))
        await self.core._live_register('alice')
        self.assertEqual(first, json.loads(self.path.read_text()))

    async def test_standalone_keeps_local_connect_without_managed_approval(self):
        self.backend.is_configured = False
        self.state = DaemonState(clock=self.clock)
        self.core.state = self.state
        self.assertTrue((await self.core.cmd_connect('alice', 'native')).Success)
        self.backend.register.assert_not_awaited()

    async def test_wg_exception_still_cleans_decrypted_temporary_config(self):
        self.wg.connect.side_effect = OSError('injected process error')
        with self.assertRaises(OSError):
            await self.core._start_tunnel(Path('/fake/native.conf.enc'), 'native')
        self.configs.cleanup_temp_config.assert_called_once_with('native')

    async def test_status_ipc_carries_deadline_and_dynamic_mfa_without_changing_activation(self):
        self.state.apply_backend_status(status(MfaSessionExpiresAt='2026-10-26T13:00:00Z'))
        self.clock.now += timedelta(hours=1)
        value = TunnelStatus.from_json(self.core.build_tunnel_status('alice').to_json())
        self.assertTrue(value.RegistrationIsActive)
        self.assertTrue(value.MfaRequired)
        self.assertEqual('2026-11-02T12:00:00Z', value.OfflineAccessExpiresAt)


@unittest.skipUnless(real_configs._CRYPTO_AVAILABLE, 'Install the runtime dependency python3-cryptography')
class EncryptedProfileTests(unittest.IsolatedAsyncioTestCase):
    """Use real AES-GCM profile storage and temp preparation with a fake wg process."""
    async def asyncSetUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        for name, value in {'DATA_DIR': self.root, 'USERS_DIR': self.root / 'users',
                'STAGED_DIR': self.root / 'staged', 'DEVICE_DIR': self.root / 'device',
                'TEMP_DIR': self.root / 'temp', '_KEY_FILE': self.root / 'master.key', '_master_key': None,
                'AUTOCONNECT_FILE': self.root / 'autoconnect.json'}.items():
            p = patch.object(real_configs, name, value)
            p.start()
            self.addCleanup(p.stop)
        p = patch.object(registration, 'REGISTRATION_FILE', self.root / 'registration.json')
        p.start()
        self.addCleanup(p.stop)
        real_configs.ensure_dirs()
        real_configs.init_temp_dir()
        self.content = ('[Interface]\nPrivateKey = AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=\n'
            'Address = 10.55.0.2/32\n[Peer]\nPublicKey = AQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQE=\n'
            'Endpoint = 127.0.0.1:51820\nAllowedIPs = 10.55.0.0/24\n')
        self.clock = Clock()
        state = DaemonState(clock=self.clock)
        state.apply_backend_status(status(DeviceTunnelEnabled=True))
        registration.save(state)
        self.state = DaemonState(clock=self.clock)
        registration.load(self.state)
        backend = SimpleNamespace(is_configured=True, base_url='https://example.invalid',
            register=AsyncMock(return_value=None), get_wan_ip=AsyncMock(return_value=None), log_event=AsyncMock())
        async def inspect(path):
            self.assertEqual(self.content, path.read_text())
            self.assertEqual(0o600, path.stat().st_mode & 0o777)
            return True, ''
        self.wg = SimpleNamespace(connect=AsyncMock(side_effect=inspect))
        self.core = DaemonCore(backend, self.state, self.wg, AutoConnectConfig(), Mock(), asyncio.get_running_loop())
        self.core._verify_after_connect = AsyncMock()

    async def test_real_cached_encrypted_user_profile_connects_offline_after_restart(self):
        real_configs.save_config('alice', 'native', self.content)
        stored = real_configs.get_config_path('alice', 'native')
        before = stored.read_bytes()
        self.assertNotIn(b'PrivateKey', before)
        self.assertTrue((await self.core.cmd_connect('alice', 'native')).Success)
        self.wg.connect.assert_awaited_once()
        self.assertEqual(before, stored.read_bytes())
        self.assertEqual([], list(real_configs.TEMP_DIR.iterdir()))
        self.core.backend.register.assert_not_awaited()

    async def test_real_machine_profile_connects_offline_without_any_user_directory(self):
        real_configs.save_device_tunnel_config('native', self.content)
        self.assertEqual([], list(real_configs.USERS_DIR.iterdir()))
        self.assertTrue((await self.core.cmd_connect('DeviceTunnel', 'native', privileged=True)).Success)
        self.assertEqual([], list(real_configs.TEMP_DIR.iterdir()))
        self.core.backend.register.assert_not_awaited()


if __name__ == '__main__':
    unittest.main()
