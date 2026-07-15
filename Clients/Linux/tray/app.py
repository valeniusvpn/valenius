"""Valenius tray application.

Uses AppIndicator3 (or AyatanaAppIndicator3) for the system tray icon and a
custom GTK3 window as the popup — matching the Windows tray UX.

The popup is shown/hidden by clicking the "Valenius" menu item.
Right-click → "Exit" quits the tray app.
"""
from __future__ import annotations

import logging
import os
import re
import shutil
import subprocess
import sys
import tempfile
import threading
from datetime import datetime
from pathlib import Path
from typing import Optional

import gi
gi.require_version('Gtk', '3.0')
gi.require_version('Gdk', '3.0')
gi.require_version('GLib', '2.0')
gi.require_version('Notify', '0.7')

from gi.repository import Gdk, GdkPixbuf, GLib, Gtk, Notify

# Try AppIndicator3, then AyatanaAppIndicator3
_indicator_mod = None
for _mod_name, _ver in [('AyatanaAppIndicator3', '0.1'), ('AppIndicator3', '0.1')]:
    try:
        gi.require_version(_mod_name, _ver)
        from gi.repository import AyatanaAppIndicator3 as _indicator_mod
        break
    except Exception:
        try:
            from gi.repository import AppIndicator3 as _indicator_mod
            break
        except Exception:
            continue

if _indicator_mod is None:
    sys.exit(
        "Could not load AppIndicator3 or AyatanaAppIndicator3.\n"
        "Install: sudo apt install gir1.2-ayatana-appindicator3-0.1"
    )

from tray import ipc_client as ipc
from shared.messages import TunnelStatus

log = logging.getLogger(__name__)

VERSION = '1.0.0'
APP_NAME = 'valenius'
POLL_INTERVAL_MS = 5000
ICONS_DIR = Path(__file__).resolve().parent.parent / 'icons'
LOGO_PATH = ICONS_DIR / 'valenius.png'


def _load_logo_pixbuf(size: int) -> Optional[GdkPixbuf.Pixbuf]:
    try:
        return GdkPixbuf.Pixbuf.new_from_file_at_scale(str(LOGO_PATH), size, size, True)
    except GLib.Error as e:
        log.warning("Could not load logo %s: %s", LOGO_PATH, e)
        return None

_POPUP_CSS = b"""
/* Light theme: white background, larger fonts throughout. */
window#wgt-popup {
    background-color: #ffffff;
    border-radius: 8px;
    border: 1px solid #dde1e7;
}
.wgt-header-row {
    padding: 12px 16px 8px 16px;
}
.wgt-header {
    font-size: 16px;
    font-weight: bold;
    color: #1a2233;
}
.wgt-status-bar {
    font-size: 13px;
    color: #6b7684;
    padding: 0 16px 10px 16px;
}
.wgt-separator {
    background-color: #e2e5ea;
    min-height: 1px;
    margin: 2px 0;
}
.wgt-profile-row {
    padding: 8px 16px;
    border-radius: 4px;
    margin: 1px 6px;
}
.wgt-profile-row:hover {
    background-color: #f0f3f7;
}
.wgt-dot-active {
    color: #00a85c;
    font-size: 16px;
}
.wgt-dot-verified {
    color: #00a85c;
    font-size: 15px;
    font-weight: bold;
}
.wgt-verified-pill {
    background-color: #00a85c;
    color: #ffffff;
    border-radius: 8px;
    padding: 1px 8px;
    font-size: 11px;
    font-weight: bold;
}
.wgt-connected-tag {
    color: #00a85c;
    font-size: 12px;
}
.wgt-dot-inactive {
    color: #b0b8c4;
    font-size: 16px;
}
.wgt-profile-label {
    color: #1a2233;
    font-size: 14px;
}
.wgt-profile-label-active {
    color: #00a85c;
    font-size: 14px;
    font-weight: bold;
}
.wgt-mfa-row {
    padding: 8px 16px;
    border-radius: 4px;
    margin: 1px 6px;
    background-color: #f0f3f7;
}
.wgt-mfa-row:hover {
    background-color: #e2e5ea;
}
.wgt-mfa-label {
    color: #a67c00;
    font-size: 14px;
}
.wgt-mfa-info {
    color: #6b7684;
    font-size: 13px;
    padding: 4px 16px;
}
.wgt-register-row {
    padding: 12px 16px;
    border-radius: 6px;
    margin: 6px 6px;
    background-color: #2563eb;
}
.wgt-register-row:hover {
    background-color: #1d4ed8;
}
.wgt-register-label {
    color: #ffffff;
    font-size: 16px;
    font-weight: bold;
}
.wgt-profile-row-locked {
    padding: 8px 16px;
    border-radius: 4px;
    margin: 1px 6px;
}
.wgt-profile-label-locked {
    color: #b0b8c4;
    font-size: 14px;
}
.wgt-mfa-unlock-row {
    padding: 8px 16px;
    border-radius: 4px;
    margin: 1px 6px;
    background-color: #fff8e6;
    border-left: 3px solid #a67c00;
}
.wgt-mfa-unlock-row:hover {
    background-color: #fcefc7;
}
.wgt-mfa-unlock-label {
    color: #a67c00;
    font-size: 14px;
    font-weight: bold;
}
.wgt-action-btn {
    background-color: #f0f3f7;
    color: #1a2233;
    border: 1px solid #d5dbe3;
    border-radius: 5px;
    padding: 8px 14px;
    font-size: 14px;
}
.wgt-action-btn:hover {
    background-color: #e2e5ea;
}
.wgt-action-btn.destructive {
    color: #d64545;
    border-color: #d64545;
}
.wgt-footer-btn {
    background: none;
    border: none;
    color: #6b7684;
    font-size: 13px;
    padding: 2px 8px;
}
.wgt-footer-btn:hover {
    color: #1a2233;
}
"""


class TrayApp:
    def __init__(self):
        Notify.init(APP_NAME)
        self._status: Optional[TunnelStatus] = None
        self._popup: Optional[Gtk.Window] = None
        self._popup_visible = False
        self._claiming_config = False
        self._restarting = False
        self._backend_prompt_open = False
        self._backend_prompt_auto_shown = False
        self._lock = threading.Lock()

        self._setup_css()
        self._setup_indicator()
        self._schedule_poll()

    # ── CSS ──────────────────────────────────────────────────────────────────

    def _setup_css(self):
        provider = Gtk.CssProvider()
        provider.load_from_data(_POPUP_CSS)
        Gtk.StyleContext.add_provider_for_screen(
            Gdk.Screen.get_default(),
            provider,
            Gtk.STYLE_PROVIDER_PRIORITY_APPLICATION,
        )

    # ── Indicator ────────────────────────────────────────────────────────────

    def _setup_indicator(self):
        self._indicator = _indicator_mod.Indicator.new(
            APP_NAME,
            'valenius-disconnected',
            _indicator_mod.IndicatorCategory.APPLICATION_STATUS,
        )
        self._indicator.set_icon_theme_path(str(ICONS_DIR))
        self._indicator.set_status(_indicator_mod.IndicatorStatus.ACTIVE)
        self._indicator.set_menu(self._build_indicator_menu())
        # AppIndicator3 hardcodes left-click to always show the menu (no override
        # possible — there's no "primary activate" signal, unlike Windows' tray
        # icon). Middle-click is the one hook it exposes for bypassing the menu:
        # binding it to the same menu item's "activate" handler opens the popup
        # directly without needing to click "Valenius" first.
        self._indicator.set_secondary_activate_target(self._open_item)

    def _build_indicator_menu(self) -> Gtk.Menu:
        # Single item: AppIndicator on GNOME always requires a menu on left-click
        # (there is no "activate app directly" signal like Windows' tray icon), so
        # this is the unavoidable extra step before the real popup opens on a plain
        # click. Exit lives in the popup footer, not here, so there's only one
        # obvious thing to click. Middle-click bypasses this menu entirely — see
        # set_secondary_activate_target() in _setup_indicator.
        menu = Gtk.Menu()

        self._open_item = Gtk.MenuItem(label='Valenius')
        self._open_item.connect('activate', lambda _: self._toggle_popup())
        menu.append(self._open_item)

        menu.show_all()
        return menu

    def _update_indicator_icon(self, connected: bool):
        icon = 'valenius-connected' if connected else 'valenius-disconnected'
        try:
            self._indicator.set_icon_full(icon, 'Valenius')
        except Exception:
            self._indicator.set_icon(icon)

    # ── Polling ──────────────────────────────────────────────────────────────

    def _schedule_poll(self):
        GLib.timeout_add(POLL_INTERVAL_MS, self._poll)

    def _poll(self) -> bool:
        try:
            status = ipc.get_status()
            prev = self._status
            self._status = status
            # A landed auto-update restarts the daemon (loading new code) but not
            # this per-user tray process, which keeps running stale code (old
            # About version, missing new UI) until logout. When the daemon reports
            # a version different from ours, re-exec to load the new tray code.
            if (status.DaemonVersion and status.DaemonVersion != VERSION
                    and not self._restarting):
                self._restart_for_update(status.DaemonVersion)
                return True
            # No backend URL configured yet (fresh install, installer provided none): the
            # tray's job is to collect it. Auto-open the prompt once; re-prompting on demand
            # happens when the user opens the popup (see _toggle_popup).
            if status.BackendUnconfigured:
                self._update_indicator_icon(False)
                if self._popup_visible:
                    self._hide_popup()
                if not self._backend_prompt_auto_shown and not self._backend_prompt_open:
                    self._action_set_backend_url()
                return True
            self._update_indicator_icon(status.IsConnected)
            if self._popup_visible:
                self._refresh_popup()
            if prev is not None and prev.IsConnected != status.IsConnected:
                if status.IsConnected:
                    self._notify(f"Connected to {status.TunnelName}", "VPN is active.")
                else:
                    self._notify("Disconnected", "VPN tunnel closed.")
            if status.HasStagedConfig and not self._claiming_config:
                self._claiming_config = True
                self._auto_claim_config()
            if prev is not None and not prev.MfaRequired and status.MfaRequired:
                self._notify("VPN authorization required", "Open Valenius to authenticate.")
            if prev is not None and not prev.MfaEnrollmentOpen and status.MfaEnrollmentOpen:
                self._notify("MFA setup required", "Open Valenius to configure two-factor authentication.")
        except ipc.IpcError:
            self._status = None
            self._update_indicator_icon(False)
        return True  # keep repeating

    def _auto_claim_config(self):
        """Mirrors Windows' AutoClaimConfigAsync: as soon as a poll sees HasStagedConfig,
        immediately claim it (moves it into the user's profile dir) with no user action
        required — previously the Linux tray only showed a "New config available" toast
        and never actually claimed it, so staged/pushed profiles never appeared."""
        try:
            info = ipc.get_config_info()
            if info.HasConfig:
                self._notify('Valenius', f'Config installed: {info.TunnelName}')
        except ipc.IpcError as e:
            self._notify('Config install failed', str(e))
        finally:
            self._claiming_config = False
        self._poll()

    def _restart_for_update(self, new_version: str):
        """Re-exec the tray so it loads the on-disk code an auto-update installed.
        The single-instance lock fd is FD_CLOEXEC (tray/__main__.py), so it is
        released on exec and the fresh instance can re-acquire it."""
        self._restarting = True
        log.warning("Daemon reports %s but tray is running %s; restarting tray to load new code",
                    new_version, VERSION)
        try:
            Notify.Notification.new(
                'Valenius updated', f'Loading version {new_version}…', 'valenius').show()
        except Exception:
            pass
        try:
            os.execv(sys.executable, [sys.executable] + sys.argv)
        except Exception as e:
            # execv should not return; if it fails, exit rather than spin. XDG
            # autostart / a manual relaunch will then pick up the new code.
            log.error("Self-restart execv failed: %s", e)
            self._quit()

    # ── Popup window ─────────────────────────────────────────────────────────

    def _toggle_popup(self):
        # Not configured yet: collect the server address instead of showing the empty popup.
        if self._status and self._status.BackendUnconfigured:
            self._action_set_backend_url()
            return
        if self._popup_visible:
            self._hide_popup()
        else:
            self._show_popup()

    def _show_popup(self):
        if self._popup is None:
            self._popup = self._build_popup()
        else:
            self._refresh_popup()
        self._position_popup()
        self._popup.show_all()
        self._popup_visible = True

    def _hide_popup(self):
        if self._popup:
            self._popup.hide()
        self._popup_visible = False

    def _build_popup(self) -> Gtk.Window:
        win = Gtk.Window()
        win.set_name('wgt-popup')
        # Without an RGBA visual, GTK3 can't paint CSS background-color/border-radius
        # on a borderless top-level window — it falls back to opaque black instead of
        # the navy theme, and every custom text color looks unreadable on top of it.
        screen = win.get_screen()
        visual = screen.get_rgba_visual()
        if visual is not None:
            win.set_visual(visual)
        win.set_icon_name('valenius')
        win.set_decorated(False)
        win.set_resizable(False)
        win.set_keep_above(True)
        win.set_skip_taskbar_hint(True)
        win.set_skip_pager_hint(True)
        win.set_type_hint(Gdk.WindowTypeHint.POPUP_MENU)
        win.set_default_size(260, -1)

        win.connect('focus-out-event', lambda w, e: (self._hide_popup(), False)[1])
        win.connect('key-press-event', self._on_key_press)

        self._popup_box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=0)
        win.add(self._popup_box)

        self._populate_popup()
        return win

    def _populate_popup(self):
        box = self._popup_box
        for child in box.get_children():
            box.remove(child)

        status = self._status
        connected_names: set[str] = {t.Name for t in status.ConnectedTunnels} if status else set()
        verified_names: set[str] = {t.Name for t in status.ConnectedTunnels if t.IsVerified} if status else set()

        # ── Header ──────────────────────────────────────────────────────────
        header_row = Gtk.Box(orientation=Gtk.Orientation.HORIZONTAL, spacing=6)
        header_row.get_style_context().add_class('wgt-header-row')

        logo_pixbuf = _load_logo_pixbuf(20)
        if logo_pixbuf is not None:
            header_row.pack_start(Gtk.Image.new_from_pixbuf(logo_pixbuf), False, False, 0)

        header = Gtk.Label(label='Valenius')
        header.get_style_context().add_class('wgt-header')
        header.set_halign(Gtk.Align.START)
        header_row.pack_start(header, False, False, 0)

        box.pack_start(header_row, False, False, 0)

        if status is None:
            status_text = "Daemon not running"
        elif len(connected_names) > 1:
            status_text = f"Connected • {len(connected_names)} tunnels active"
        elif status.IsConnected:
            status_text = f"Connected • {status.TunnelName}"
        else:
            status_text = "Not connected"
        status_lbl = Gtk.Label(label=status_text)
        status_lbl.get_style_context().add_class('wgt-status-bar')
        status_lbl.set_halign(Gtk.Align.START)
        box.pack_start(status_lbl, False, False, 0)

        box.pack_start(_separator(), False, False, 0)

        # ── Register (same condition as Windows: RegistrationIsActive != true) ─
        if status and status.RegistrationIsActive is not True:
            register_row = self._make_register_row('Register Client', self._action_register)
            box.pack_start(register_row, False, False, 0)

        # ── MFA rows (appear above profile list, same as Windows) ─────────────
        mfa_locked = bool(status and status.MfaRequired and status.MfaAuthorizeUrl)
        if status:
            if status.MfaEnrollmentOpen and status.MfaEnrollmentUri:
                uri = status.MfaEnrollmentUri
                row = self._make_mfa_row(
                    "Set up two-factor auth (MFA)…",
                    lambda u=uri: self._action_mfa_enroll(u),
                )
                box.pack_start(row, False, False, 0)
            elif mfa_locked:
                unlock_row = self._make_mfa_unlock_row(status.MfaAuthorizeUrl, status.MfaApproveNumber)
                box.pack_start(unlock_row, False, False, 0)

        # ── Profile list ─────────────────────────────────────────────────────
        profiles = status.AvailableProfiles if status else []

        if profiles:
            for profile in profiles:
                is_active = profile in connected_names
                is_verified = profile in verified_names
                row = self._make_profile_row(profile, is_active, verified=is_verified, mfa_locked=mfa_locked)
                box.pack_start(row, False, False, 0)
        else:
            no_profiles = Gtk.Label(label='No profiles configured')
            no_profiles.get_style_context().add_class('wgt-status-bar')
            no_profiles.set_halign(Gtk.Align.CENTER)
            no_profiles.set_margin_top(8)
            no_profiles.set_margin_bottom(8)
            box.pack_start(no_profiles, False, False, 0)

        # MFA session active: show expiry inline, grouped with the profile list.
        if status and not mfa_locked and status.MfaSessionExpiresAt:
            expires_str = _format_expiry(status.MfaSessionExpiresAt)
            info = Gtk.Label(label=f"Authorized until {expires_str}")
            info.get_style_context().add_class('wgt-mfa-info')
            info.set_halign(Gtk.Align.START)
            info.set_margin_start(14)
            box.pack_start(info, False, False, 0)

        box.pack_start(_separator(), False, False, 0)

        # ── Actions ──────────────────────────────────────────────────────────
        action_box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=4)
        action_box.set_margin_start(8)
        action_box.set_margin_end(8)
        action_box.set_margin_top(6)
        action_box.set_margin_bottom(4)

        if status and len(connected_names) > 1:
            btn_dc_all = Gtk.Button(label='Disconnect All')
            btn_dc_all.get_style_context().add_class('wgt-action-btn')
            btn_dc_all.get_style_context().add_class('destructive')
            btn_dc_all.connect('clicked', lambda _: self._action_disconnect_all())
            action_box.pack_start(btn_dc_all, False, True, 0)
        elif status and status.IsConnected:
            btn_dc = Gtk.Button(label='Disconnect')
            btn_dc.get_style_context().add_class('wgt-action-btn')
            btn_dc.get_style_context().add_class('destructive')
            btn_dc.connect('clicked', lambda _: self._action_disconnect())
            action_box.pack_start(btn_dc, False, True, 0)

        btn_upload = Gtk.Button(label='Upload Config…')
        btn_upload.get_style_context().add_class('wgt-action-btn')
        btn_upload.connect('clicked', lambda _: self._action_upload())
        action_box.pack_start(btn_upload, False, True, 0)

        if status:
            ac_label = 'Auto Connect: On' if status.AutoConnectEnabled else 'Auto Connect: Off'
            btn_ac = Gtk.Button(label=ac_label)
            btn_ac.get_style_context().add_class('wgt-action-btn')
            btn_ac.connect('clicked', lambda _: self._action_toggle_autoconnect(status.AutoConnectEnabled))
            action_box.pack_start(btn_ac, False, True, 0)

        box.pack_start(action_box, False, False, 0)

        box.pack_start(_separator(), False, False, 0)

        # ── Footer ───────────────────────────────────────────────────────────
        footer = Gtk.Box(orientation=Gtk.Orientation.HORIZONTAL, spacing=0)
        footer.set_margin_bottom(6)

        btn_about = Gtk.Button(label='About')
        btn_about.get_style_context().add_class('wgt-footer-btn')
        btn_about.connect('clicked', lambda _: self._action_about())
        footer.pack_start(btn_about, False, False, 0)

        btn_logs = Gtk.Button(label='Send logs')
        btn_logs.get_style_context().add_class('wgt-footer-btn')
        btn_logs.set_tooltip_text(
            'Uploads Valenius-only logs (journalctl -u valenius) to your administrator for '
            'support. Secrets are redacted before sending; no other system data is collected.')
        btn_logs.connect('clicked', lambda _: self._action_send_logs())
        footer.pack_start(btn_logs, False, False, 0)

        # Sync: force an immediate backend heartbeat. Only shown when the client
        # is registered/enabled AND the backend is currently reachable -- there's
        # nothing to sync otherwise, and offering it would just fail.
        if status and status.RegistrationIsActive is True and status.BackendReachable is True:
            btn_sync = Gtk.Button(label='Sync')
            btn_sync.get_style_context().add_class('wgt-footer-btn')
            btn_sync.set_tooltip_text(
                'Sync now with the backend: pushes this client\'s status and pulls any '
                'pending configs, MFA state, or updates immediately instead of waiting '
                'for the next automatic check.')
            btn_sync.connect('clicked', lambda _: self._action_sync())
            footer.pack_start(btn_sync, False, False, 0)

        btn_exit = Gtk.Button(label='Exit')
        btn_exit.get_style_context().add_class('wgt-footer-btn')
        btn_exit.connect('clicked', lambda _: self._quit())
        footer.pack_end(btn_exit, False, False, 0)

        box.pack_start(footer, False, False, 0)

        box.show_all()

    def _make_profile_row(self, profile: str, active: bool, verified: bool = False, mfa_locked: bool = False) -> Gtk.EventBox:
        evbox = Gtk.EventBox()
        if mfa_locked:
            evbox.get_style_context().add_class('wgt-profile-row-locked')
            evbox.set_tooltip_text("MFA required — click Unlock VPN to authenticate")
        else:
            evbox.get_style_context().add_class('wgt-profile-row')

        row = Gtk.Box(orientation=Gtk.Orientation.HORIZONTAL, spacing=8)
        row.set_margin_start(4)

        if mfa_locked:
            dot = Gtk.Label(label='\U0001f512')  # 🔒 lock emoji
        elif active and verified:
            dot = Gtk.Label(label='✓')  # ✓ filled check-badge, same as Windows' verified icon
            dot.get_style_context().add_class('wgt-dot-verified')
        else:
            dot = Gtk.Label(label='●')
            dot.get_style_context().add_class('wgt-dot-active' if active else 'wgt-dot-inactive')
        row.pack_start(dot, False, False, 0)

        lbl = Gtk.Label(label=profile)
        if mfa_locked:
            lbl.get_style_context().add_class('wgt-profile-label-locked')
        else:
            lbl.get_style_context().add_class('wgt-profile-label-active' if active else 'wgt-profile-label')
        lbl.set_halign(Gtk.Align.START)
        row.pack_start(lbl, True, True, 0)

        if active and not mfa_locked:
            if verified:
                tag = Gtk.Label(label='Verified')
                tag.get_style_context().add_class('wgt-verified-pill')
            else:
                tag = Gtk.Label(label='Connected')
                tag.get_style_context().add_class('wgt-connected-tag')
            row.pack_start(tag, False, False, 0)

        evbox.add(row)

        if not mfa_locked:
            if active:
                evbox.connect('button-press-event',
                              lambda w, e, p=profile: self._action_disconnect(p))
                evbox.set_tooltip_text("Click to disconnect")
            else:
                evbox.connect('button-press-event',
                              lambda w, e, p=profile: self._action_connect(p))
        return evbox

    def _make_register_row(self, label_text: str, callback) -> Gtk.EventBox:
        # Prominent call-to-action button — colored background so an unregistered
        # client can't be overlooked (unlike the subtle _make_mfa_row style).
        evbox = Gtk.EventBox()
        evbox.get_style_context().add_class('wgt-register-row')

        lbl = Gtk.Label(label=label_text)
        lbl.get_style_context().add_class('wgt-register-label')
        lbl.set_halign(Gtk.Align.CENTER)
        evbox.add(lbl)
        evbox.connect('button-press-event', lambda w, e: callback())
        return evbox

    def _make_mfa_row(self, label_text: str, callback) -> Gtk.EventBox:
        evbox = Gtk.EventBox()
        evbox.get_style_context().add_class('wgt-mfa-row')

        lbl = Gtk.Label(label=label_text)
        lbl.get_style_context().add_class('wgt-mfa-label')
        lbl.set_halign(Gtk.Align.START)
        lbl.set_margin_start(4)
        evbox.add(lbl)
        evbox.connect('button-press-event', lambda w, e: callback())
        return evbox

    def _make_mfa_unlock_row(self, url: str, approve_number=None) -> Gtk.EventBox:
        evbox = Gtk.EventBox()
        evbox.get_style_context().add_class('wgt-mfa-unlock-row')

        inner = Gtk.Box(orientation=Gtk.Orientation.HORIZONTAL, spacing=6)
        inner.set_margin_start(4)

        icon_lbl = Gtk.Label(label='\U0001f512')  # 🔒
        inner.pack_start(icon_lbl, False, False, 0)

        # With a push-approver, prompt to approve on the phone (tapping still opens
        # the authenticator/TOTP page as the fallback).
        unlock_text = f'Approve {approve_number} on your phone' if approve_number is not None else 'Unlock VPN'
        lbl = Gtk.Label(label=unlock_text)
        lbl.get_style_context().add_class('wgt-mfa-unlock-label')
        lbl.set_halign(Gtk.Align.START)
        inner.pack_start(lbl, True, True, 0)

        evbox.add(inner)
        evbox.connect('button-press-event', lambda w, e: self._action_mfa_authorize(url))
        return evbox

    def _refresh_popup(self):
        if self._popup:
            self._populate_popup()

    def _position_popup(self):
        display = Gdk.Display.get_default()
        monitor = display.get_primary_monitor() or display.get_monitor(0)
        geom = monitor.get_geometry()
        win_w, win_h = 260, 400
        x = geom.x + geom.width - win_w - 12
        y = geom.y + geom.height - win_h - 50
        self._popup.move(x, y)

    # ── Actions ──────────────────────────────────────────────────────────────

    def _action_connect(self, profile: str):
        self._hide_popup()
        try:
            ipc.connect(profile)
            self._poll()
        except ipc.IpcError as e:
            self._show_error(str(e))

    def _action_disconnect(self, profile_name: Optional[str] = None):
        self._hide_popup()
        try:
            ipc.disconnect(profile_name)
            self._poll()
        except ipc.IpcError as e:
            self._show_error(str(e))

    def _action_disconnect_all(self):
        self._hide_popup()
        if not self._status:
            return
        errors = []
        for tunnel in list(self._status.ConnectedTunnels):
            try:
                ipc.disconnect(tunnel.Name)
            except ipc.IpcError as e:
                errors.append(str(e))
        self._poll()
        if errors:
            self._show_error('\n'.join(errors))

    def _action_mfa_authorize(self, url: str):
        self._hide_popup()
        try:
            subprocess.Popen(['xdg-open', url])
        except Exception as e:
            self._show_error(f"Could not open browser: {e}")

    def _action_mfa_enroll(self, uri: str):
        self._hide_popup()
        dlg = _MfaEnrollDialog(uri)
        result = dlg.run()
        code = dlg.get_code()
        dlg.destroy()
        if result == Gtk.ResponseType.OK and code:
            try:
                ipc.mfa_enroll_confirm(code)
                self._notify("MFA setup complete", "Two-factor authentication is now active.")
                self._poll()
            except ipc.IpcError as e:
                self._show_error(str(e))

    def _action_upload(self):
        self._hide_popup()
        dialog = Gtk.FileChooserDialog(
            title='Select WireGuard Config',
            action=Gtk.FileChooserAction.OPEN,
        )
        dialog.add_buttons(
            Gtk.STOCK_CANCEL, Gtk.ResponseType.CANCEL,
            Gtk.STOCK_OPEN, Gtk.ResponseType.OK,
        )
        f = Gtk.FileFilter()
        f.set_name('WireGuard Configs (*.conf)')
        f.add_pattern('*.conf')
        dialog.add_filter(f)

        if dialog.run() == Gtk.ResponseType.OK:
            path = dialog.get_filename()
            dialog.destroy()
            try:
                profile_name = Path(path).stem[:50]
                with open(path) as fp:
                    content = fp.read()
                ipc.upload_config(profile_name, content)
                self._notify('Config uploaded', f'Profile "{profile_name}" saved.')
                self._poll()
            except Exception as e:
                self._show_error(str(e))
        else:
            dialog.destroy()

    def _action_toggle_autoconnect(self, currently_enabled: bool):
        try:
            ipc.set_auto_connect(not currently_enabled)
            self._poll()
            self._show_popup()
        except ipc.IpcError as e:
            self._show_error(str(e))

    def _action_register(self):
        try:
            result = ipc.register()
            self._poll()
            self._notify('Registration', result.Message or ('Active' if result.IsActive else 'Pending admin activation.'))
        except ipc.IpcError as e:
            self._show_error(str(e))

    def _action_set_backend_url(self):
        """First-run prompt for the backend server DNS (shown when the installer set no URL).
        Single-instance; on Cancel the client stays unconfigured and the prompt reappears the
        next time the user opens the tray. Mirrors the Windows BackendUrlForm flow."""
        if self._backend_prompt_open:
            return
        self._backend_prompt_open = True
        self._backend_prompt_auto_shown = True
        self._hide_popup()
        dlg = _BackendUrlDialog()
        try:
            while True:
                if dlg.run() != Gtk.ResponseType.OK:
                    break  # cancelled — stay unconfigured, re-prompt on next open
                dns = dlg.get_dns()
                if not dns:
                    dlg.set_error("Enter your server address, for example vpn.company.com.")
                    continue
                try:
                    warning = ipc.set_backend_url(dns)
                    self._notify('Valenius', warning or 'Server address saved. Connecting…')
                    break
                except ipc.IpcError as e:
                    dlg.set_error(str(e))
                    continue
        finally:
            dlg.destroy()
            self._backend_prompt_open = False
        self._poll()

    def _action_about(self):
        self._hide_popup()
        dlg = Gtk.AboutDialog()
        dlg.set_program_name('Valenius')
        dlg.set_version(VERSION)
        dlg.set_comments('Linux client for Valenius VPN manager.\nAllows non-admin users to manage a WireGuard VPN.')
        dlg.set_website('https://github.com/cgasser/Valenius')
        logo = _load_logo_pixbuf(96)
        if logo is not None:
            dlg.set_logo(logo)

        # Backend reachability + URL (same info Windows' AboutForm shows), so an
        # admin can confirm the client is actually pointed at the right backend.
        status = self._status
        backend_url = status.BackendUrl if status else None
        reachable = status.BackendReachable if status else None
        if reachable is True:
            color, text = '#00a85c', 'Backend reachable'
        elif reachable is False:
            color, text = '#d64545', 'Backend unreachable'
        else:
            color, text = '#b0b8c4', 'Checking backend…'
        markup = f'<span foreground="{color}">●</span> <span foreground="#6b7684" size="small">{text}'
        if backend_url:
            markup += f' — {GLib.markup_escape_text(backend_url)}'
        markup += '</span>'
        status_label = Gtk.Label()
        status_label.set_markup(markup)
        status_label.set_margin_top(6)
        dlg.get_content_area().pack_start(status_label, False, False, 0)
        status_label.show()

        dlg.run()
        dlg.destroy()

    def _action_sync(self):
        self._hide_popup()
        try:
            ipc.sync_status()
            self._notify('Valenius', 'Synced with the backend.')
        except ipc.IpcError as e:
            self._notify('Valenius', f'Sync failed: {e}')
        # Reflect whatever the fresh sync returned (new configs, MFA state, etc.).
        self._poll()

    def _action_send_logs(self):
        self._hide_popup()
        try:
            ipc.send_logs()
            self._notify('Valenius', 'Diagnostic logs are being sent to your administrator.')
        except ipc.IpcError as e:
            self._notify('Valenius', f'Could not send logs: {e}')

    # ── Notifications ────────────────────────────────────────────────────────

    def _notify(self, summary: str, body: str = ''):
        try:
            n = Notify.Notification.new(summary, body, 'valenius')
            n.show()
        except Exception as e:
            log.debug("Notification failed: %s", e)

    def _show_error(self, message: str):
        dlg = Gtk.MessageDialog(
            message_type=Gtk.MessageType.ERROR,
            buttons=Gtk.ButtonsType.OK,
            text=message,
        )
        dlg.run()
        dlg.destroy()

    # ── Misc ─────────────────────────────────────────────────────────────────

    def _on_key_press(self, _widget, event):
        if event.keyval == Gdk.KEY_Escape:
            self._hide_popup()

    def _quit(self):
        self._hide_popup()
        ipc.notify_offline()
        Notify.uninit()
        Gtk.main_quit()

    def run(self):
        Gtk.main()


class _MfaEnrollDialog(Gtk.Dialog):
    """GTK dialog for MFA enrollment: QR code (if qrencode available) + TOTP code entry."""

    def __init__(self, uri: str):
        super().__init__(title="Set Up Two-Factor Authentication", flags=0)
        self.add_buttons(
            Gtk.STOCK_CANCEL, Gtk.ResponseType.CANCEL,
            "Confirm", Gtk.ResponseType.OK,
        )
        self.set_default_size(360, -1)
        self._code_entry = Gtk.Entry()

        content = self.get_content_area()
        content.set_spacing(8)
        content.set_margin_start(16)
        content.set_margin_end(16)
        content.set_margin_top(12)
        content.set_margin_bottom(12)

        # Try to generate and show QR image via qrencode (optional package).
        # The otpauth:// URI embeds the TOTP secret, so the PNG must NOT land in shared
        # /tmp under a predictable, world-readable name (any local user could read it and
        # clone the victim's second factor — audit M7). Render into a private per-user temp
        # dir (0700, unpredictable name), then delete it immediately once GTK has loaded the
        # pixbuf into memory. Prefer $XDG_RUNTIME_DIR (per-user, mode 0700) as the parent.
        qr_ok = False
        qr_dir = None
        try:
            xdg = os.environ.get('XDG_RUNTIME_DIR')
            qr_dir = tempfile.mkdtemp(prefix='valenius-mfa-', dir=xdg if xdg else None)
            qr_path = os.path.join(qr_dir, 'qr.png')
            r = subprocess.run(
                ['qrencode', '-o', qr_path, '-s', '5', uri],
                timeout=5, capture_output=True,
            )
            if r.returncode == 0 and Path(qr_path).exists():
                os.chmod(qr_path, 0o600)
                img = Gtk.Image.new_from_file(qr_path)  # loads the pixbuf synchronously
                content.pack_start(img, False, False, 0)
                qr_ok = True
        except Exception:
            qr_ok = False
        finally:
            if qr_dir:
                shutil.rmtree(qr_dir, ignore_errors=True)

        if not qr_ok:
            hint = Gtk.Label(label="Scan this URI with your authenticator app,\nor enter the key manually:")
            hint.set_line_wrap(True)
            content.pack_start(hint, False, False, 0)
            uri_lbl = Gtk.Label(label=uri)
            uri_lbl.set_selectable(True)
            uri_lbl.set_line_wrap(True)
            content.pack_start(uri_lbl, False, False, 0)

        secret = _extract_totp_secret(uri)
        if secret:
            key_box = Gtk.Box(orientation=Gtk.Orientation.HORIZONTAL, spacing=6)
            key_box.pack_start(Gtk.Label(label="Manual key:"), False, False, 0)
            key_lbl = Gtk.Label(label=secret)
            key_lbl.set_selectable(True)
            key_box.pack_start(key_lbl, False, False, 0)
            content.pack_start(key_box, False, False, 0)

        content.pack_start(Gtk.Separator(orientation=Gtk.Orientation.HORIZONTAL), False, False, 4)

        code_box = Gtk.Box(orientation=Gtk.Orientation.HORIZONTAL, spacing=8)
        code_box.pack_start(Gtk.Label(label="Enter 6-digit code:"), False, False, 0)
        self._code_entry.set_max_length(6)
        self._code_entry.set_width_chars(8)
        self._code_entry.connect('activate', lambda _: self.response(Gtk.ResponseType.OK))
        code_box.pack_start(self._code_entry, False, False, 0)
        content.pack_start(code_box, False, False, 0)

        content.show_all()

    def get_code(self) -> str:
        return self._code_entry.get_text().strip()


class _BackendUrlDialog(Gtk.Dialog):
    """First-run dialog to collect the backend server DNS. The user types only the host; the
    'https://' scheme is a fixed, non-editable prefix. Mirrors the Windows BackendUrlForm."""

    def __init__(self):
        super().__init__(title="Connect to your Valenius server", flags=0)
        self.add_buttons(
            Gtk.STOCK_CANCEL, Gtk.ResponseType.CANCEL,
            "Save", Gtk.ResponseType.OK,
        )
        self.set_default_size(400, -1)
        self.set_default_response(Gtk.ResponseType.OK)

        content = self.get_content_area()
        content.set_spacing(8)
        content.set_margin_start(16)
        content.set_margin_end(16)
        content.set_margin_top(12)
        content.set_margin_bottom(12)

        intro = Gtk.Label(label=(
            "Enter the address of your Valenius server. Your administrator provided this — "
            "just the server name, for example vpn.company.com."))
        intro.set_line_wrap(True)
        intro.set_xalign(0.0)
        content.pack_start(intro, False, False, 0)

        row = Gtk.Box(orientation=Gtk.Orientation.HORIZONTAL, spacing=0)
        # Fixed, non-editable scheme prefix — the user cannot change it.
        scheme = Gtk.Label(label="https://")
        row.pack_start(scheme, False, False, 0)
        self._entry = Gtk.Entry()
        self._entry.set_placeholder_text("vpn.company.com")
        self._entry.set_hexpand(True)
        self._entry.set_activates_default(True)
        row.pack_start(self._entry, True, True, 0)
        content.pack_start(row, False, False, 0)

        hint = Gtk.Label(label="Do not include https:// or any path — just the server name.")
        hint.set_xalign(0.0)
        hint.get_style_context().add_class('dim-label')
        content.pack_start(hint, False, False, 0)

        self._error = Gtk.Label()
        self._error.set_xalign(0.0)
        self._error.set_line_wrap(True)
        content.pack_start(self._error, False, False, 0)

        content.show_all()

    def get_dns(self) -> str:
        return self._entry.get_text().strip()

    def set_error(self, message: str) -> None:
        self._error.set_markup(
            f'<span foreground="#d64545">{GLib.markup_escape_text(message)}</span>')
        self._entry.grab_focus()


# ── Module-level helpers ──────────────────────────────────────────────────────

def _separator() -> Gtk.Box:
    sep = Gtk.Box()
    sep.get_style_context().add_class('wgt-separator')
    sep.set_size_request(-1, 1)
    return sep


def _extract_totp_secret(uri: str) -> Optional[str]:
    m = re.search(r'[?&]secret=([A-Z2-7a-z]+)', uri)
    return m.group(1) if m else None


def _format_expiry(iso_str: str) -> str:
    """Format an ISO datetime string as a local HH:MM time."""
    try:
        dt = datetime.fromisoformat(iso_str.replace('Z', '+00:00'))
        local = dt.astimezone()
        return local.strftime('%H:%M')
    except Exception:
        return iso_str
