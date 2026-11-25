"""Run against vendor/ or the built image with VALENIUS_HA_APP=/app.

All storage is temporary; backend/WireGuard adapters never touch real networking.
The shared Linux regression suite imports the add-on's modules, not Linux sources.
"""
import asyncio
import importlib.util
import json
import os
import sys
import tempfile
import unittest
from datetime import timedelta
from pathlib import Path
from unittest.mock import AsyncMock, Mock, patch

COMPONENT = Path(__file__).resolve().parents[1]
ADDON = COMPONENT / 'valenius-vpn'
APP = Path(os.environ['VALENIUS_HA_APP']) if 'VALENIUS_HA_APP' in os.environ else ADDON / 'rootfs' / 'app'
DAEMON_ROOT = APP if 'VALENIUS_HA_APP' in os.environ else ADDON / 'vendor'
sys.path[:0] = [str(APP), str(DAEMON_ROOT)]

import run as addon
from daemon import backend, core, registration, state
from daemon import config_manager as configs

spec = importlib.util.spec_from_file_location('linux_offline_cases', COMPONENT.parent / 'Linux/tests/test_offline_access.py')
shared = importlib.util.module_from_spec(spec)
spec.loader.exec_module(shared)
credential_spec = importlib.util.spec_from_file_location('linux_credential_cases', COMPONENT.parent / 'Linux/tests/test_device_credential_migration.py')
credential_cases = importlib.util.module_from_spec(credential_spec)
credential_spec.loader.exec_module(credential_cases)


class StartupTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.data = self.root / 'data'
        self.data.mkdir()
        self.link = self.root / 'var/lib/valenius'
        for name, value in (('DATA_ROOT', self.data), ('DAEMON_DATA_DIR', self.link)):
            p = patch.object(addon, name, value)
            p.start()
            self.addCleanup(p.stop)

    def options(self, **changes):
        value = {'backend_url': 'https://example.invalid', 'api_key': 'test-key', **changes}
        (self.data / 'options.json').write_text(json.dumps(value))
        return addon._load_options()

    def test_imports_use_addon_payload_and_versions_match(self):
        for module in (core, state, registration, configs):
            self.assertTrue(Path(module.__file__).resolve().is_relative_to(DAEMON_ROOT.resolve()))
        self.assertTrue(Path(addon.__file__).resolve().is_relative_to(APP.resolve()))
        self.assertIn(f'version: "{addon.ADDON_VERSION}"', (ADDON / 'config.yaml').read_text())

    def test_missing_or_empty_backend_cannot_enable_standalone_bypass(self):
        with self.assertRaises(ValueError):
            addon._load_options()
        for value in ('', '   ', 'https://', '/', None, True):
            with self.subTest(value=value), self.assertRaises(ValueError):
                self.options(backend_url=value)
        for value in (None, 3):
            with self.subTest(key=value), self.assertRaises(ValueError):
                self.options(api_key=value)

    def test_empty_key_allows_new_pending_device_enrollment(self):
        self.assertEqual('', self.options(api_key='')['api_key'])
        (self.data / 'options.json').write_text(json.dumps({'backend_url': 'https://example.invalid'}))
        self.assertNotIn('api_key', addon._load_options())

    def test_options_must_be_object_and_duration_is_bounded_integer(self):
        for value in ([], None, 'invalid'):
            (self.data / 'options.json').write_text(json.dumps(value))
            with self.assertRaises(ValueError):
                addon._load_options()
        self.assertEqual(7, self.options().get('offline_access_days', 7))
        for days in (1, 7, 365):
            self.assertEqual(days, self.options(offline_access_days=days)['offline_access_days'])
        for days in (0, 366, -1, True, '7', 7.5, None):
            with self.subTest(days=days), self.assertRaises(ValueError):
                self.options(offline_access_days=days)

    def test_persistent_symlink_keeps_identity_and_original_expiry_across_restart(self):
        addon._ensure_persistent_data_dir()
        self.assertEqual((self.data / 'valenius').resolve(), self.link.resolve())
        self.assertEqual(0o700, self.link.stat().st_mode & 0o777)
        clock = shared.Clock()
        first = state.DaemonState(clock=clock)
        first.apply_backend_status(shared.status())
        with patch.object(registration, 'REGISTRATION_FILE', self.link / 'registration.json'):
            self.assertTrue(registration.save(first))
            clock.now += timedelta(days=6)
            addon._ensure_persistent_data_dir()
            second = state.DaemonState(clock=clock)
            registration.load(second)
            self.assertEqual(first.get_client_id(), second.get_client_id())
            self.assertEqual(first.offline_access_expires_at(), second.offline_access_expires_at())
            self.assertTrue(second.is_allowed_to_connect())
            self.assertEqual(0o600, (self.data / 'valenius/registration.json').stat().st_mode & 0o777)

    def test_wrong_or_ephemeral_data_path_is_refused_without_deleting_state(self):
        self.link.mkdir(parents=True)
        existing = self.link / 'registration.json'
        existing.write_text('preserve')
        with self.assertRaises(RuntimeError):
            addon._ensure_persistent_data_dir()
        self.assertEqual('preserve', existing.read_text())
        existing.unlink()
        self.link.rmdir()
        self.link.symlink_to(self.root / 'wrong-target')
        with self.assertRaises(RuntimeError):
            addon._ensure_persistent_data_dir()
        self.assertTrue(self.link.is_symlink())


class AddonFlowTests(shared.EncryptedProfileTests):
    async def asyncSetUp(self):
        await super().asyncSetUp()
        self.core._reverify_active_tunnels = AsyncMock()
        self.core._wg_down = AsyncMock(return_value=(True, ''))
        self.core.updater = addon.SupervisorUpdater()
        self.core.backend.set_channel = Mock()
        self.core.backend.set_api_key = Mock()
        self.core.persist_auto_connect = Mock()
        self.core._ensure_device_tunnel_config = AsyncMock()

    async def tick(self, mqtt=None):
        configs.save_config(addon.USERNAME, 'native', self.content)
        with patch.object(addon.asyncio, 'sleep', side_effect=asyncio.CancelledError):
            with self.assertRaises(asyncio.CancelledError):
                await addon._always_on_loop(self.core, mqtt, 'native')

    async def test_always_on_reconnect_uses_cached_policy_without_backend_and_reports_mqtt(self):
        self.state.set_backend_reachable(False)
        self.clock.now += timedelta(days=6)
        mqtt = Mock()
        await self.tick(mqtt)
        self.wg.connect.assert_awaited_once()
        self.core.backend.register.assert_not_awaited()
        self.assertEqual(addon.USERNAME, self.state.get_tunnel('native').connected_user)
        mqtt.publish_state.assert_called_once_with(connected=True, verified=False)
        self.assertEqual([], list(configs.TEMP_DIR.iterdir()))

    async def test_always_on_cannot_restart_at_exact_expiry_or_after_explicit_denial(self):
        for change in ('expired', 'inactive'):
            self.state.apply_backend_status(shared.status())
            if change == 'expired':
                self.clock.now += timedelta(days=7)
            else:
                self.state.apply_backend_status(shared.status(IsActive=False))
            await self.tick()
        self.wg.connect.assert_not_awaited()

    async def test_cached_mfa_expiry_blocks_always_on_reconnect_after_restart(self):
        self.state.apply_backend_status(shared.status(MfaSessionExpiresAt='2026-10-26T13:00:00Z'))
        registration.save(self.state)
        fresh = state.DaemonState(clock=self.clock)
        registration.load(fresh)
        self.core.state = fresh
        self.clock.now += timedelta(hours=1)
        await self.tick()
        self.wg.connect.assert_not_awaited()

    async def test_force_update_never_queries_or_installs_linux_package(self):
        with patch('daemon.updater.Updater.check_and_apply', new_callable=AsyncMock) as linux_update:
            await self.core._process_status_response(shared.status(ForceUpdate=True))
            await asyncio.sleep(0)
            linux_update.assert_not_awaited()
        self.assertEqual((False, addon.ADDON_VERSION, None, None), await self.core.updater.check())

    async def test_main_wires_real_core_with_duration_and_preserves_identity_on_shutdown(self):
        credential = 'vlnd_' + 'A' * 64
        options = {'backend_url': 'https://example.invalid', 'api_key': credential, 'offline_access_days': 3}
        callbacks = {}
        started = asyncio.Event()
        done = asyncio.Event()
        instances = []
        real_state = state.DaemonState

        def make_state(**kwargs):
            value = real_state(clock=self.clock, **kwargs)
            instances.append(value)
            return value

        async def idle(*args):
            started.set()
            await done.wait()

        class ProcessExit(Exception):
            pass

        loop = asyncio.get_running_loop()
        with patch.object(addon, '_load_options', return_value=options), \
             patch.object(addon, '_ensure_persistent_data_dir'), \
             patch.object(state, 'DaemonState', side_effect=make_state), \
             patch.object(backend, '_load_persisted_backend_url', return_value=None), \
             patch.object(backend, '_load_persisted_api_key', return_value='legacy-fleet-key'), \
             patch.object(backend, '_API_KEY_FILE', str(self.root / 'apikey')), \
             patch.object(addon, '_install_hostname_override'), \
             patch.object(addon, '_install_version_override'), \
             patch.object(addon, '_install_platform_override'), \
             patch('daemon.wg_manager.WgManager.get_active_tunnels', new=AsyncMock(return_value=[])), \
             patch.object(core.DaemonCore, 'heartbeat_loop', side_effect=idle), \
             patch.object(core.DaemonCore, 'poll_loop', side_effect=idle), \
             patch.object(addon, '_always_on_loop', side_effect=idle), \
             patch.object(backend.BackendClient, 'notify_offline', new=AsyncMock()), \
             patch.object(loop, 'add_signal_handler', side_effect=lambda sig, cb: callbacks.setdefault(sig, cb)), \
             patch.object(addon.logging, 'shutdown'), \
             patch.object(addon.os, '_exit', side_effect=ProcessExit):
            task = asyncio.create_task(addon._main())
            await asyncio.wait_for(started.wait(), timeout=3)
            self.assertEqual(credential, (self.root / 'apikey').read_text())
            self.assertEqual(0o600, (self.root / 'apikey').stat().st_mode & 0o777)
            callbacks[addon.signal.SIGTERM]()
            with self.assertRaises(ProcessExit):
                await task
        self.assertEqual(self.state.get_client_id(), instances[0].get_client_id())
        self.assertEqual(self.clock.now + timedelta(days=3), shared.utc_datetime(instances[0].offline_access_expires_at()))
        self.assertTrue(instances[0].is_tray_running())

    async def test_main_refuses_corrupt_identity_before_migrating_profiles(self):
        path = self.root / 'registration.json'
        path.write_text('{corrupt')
        options = {'backend_url': 'https://example.invalid', 'api_key': 'test'}
        with patch.object(addon, '_load_options', return_value=options), \
             patch.object(addon, '_ensure_persistent_data_dir'), \
             patch.object(configs, 'migrate_plain_configs') as migrate:
            with self.assertRaisesRegex(RuntimeError, 'refusing to replace'):
                await addon._main()
        migrate.assert_not_called()
        self.assertEqual('{corrupt', path.read_text())


def load_tests(loader, tests, pattern):
    suite = unittest.TestSuite()
    # Settings is desktop-only; the add-on has its own validated Supervisor options.
    for cls in (shared.PersistenceTests, shared.DaemonFlowTests):
        for name in loader.getTestCaseNames(cls):
            if name != 'test_settings_default_valid_and_invalid_periods':
                suite.addTest(cls(name))
    suite.addTests(loader.loadTestsFromTestCase(StartupTests))
    suite.addTests(loader.loadTestsFromTestCase(AddonFlowTests))
    suite.addTests(loader.loadTestsFromTestCase(credential_cases.CredentialMigrationTests))
    return suite


if __name__ == '__main__':
    unittest.main()
