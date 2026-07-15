#!/usr/bin/env bash
# Remove the Valenius macOS client. Run with sudo.
#   sudo ./uninstall.sh            # remove app + services, KEEP /Library/Application Support/Valenius
#   sudo ./uninstall.sh --purge    # also delete all state (configs, keys, registration)
set -u

PURGE=0
[[ "${1:-}" == "--purge" ]] && PURGE=1

DAEMON_LABEL="com.stranto.valenius.daemon"
APP_LABEL="com.stranto.valenius.app"
SUPPORT="/Library/Application Support/Valenius"

echo "Stopping services…"
/bin/launchctl bootout "system/${DAEMON_LABEL}" 2>/dev/null || true
CONSOLE_UID=$(/usr/bin/stat -f%u /dev/console 2>/dev/null)
if [ -n "$CONSOLE_UID" ] && [ "$CONSOLE_UID" != "0" ]; then
    /bin/launchctl bootout "gui/${CONSOLE_UID}/${APP_LABEL}" 2>/dev/null || true
fi
/usr/bin/killall ValeniusApp 2>/dev/null || true
/usr/bin/killall wireguard-go 2>/dev/null || true

echo "Removing files…"
/bin/rm -f "/Library/LaunchDaemons/${DAEMON_LABEL}.plist"
/bin/rm -f "/Library/LaunchAgents/${APP_LABEL}.plist"
/bin/rm -rf "/Applications/Valenius.app"
# Any stray one-shot updater jobs.
/bin/rm -f /Library/LaunchDaemons/com.stranto.valenius.update.*.plist 2>/dev/null || true
/usr/sbin/pkgutil --forget com.stranto.valenius.pkg 2>/dev/null || true

if [ "$PURGE" -eq 1 ]; then
    echo "Purging state at ${SUPPORT}…"
    /bin/rm -rf "$SUPPORT"
else
    echo "Kept ${SUPPORT} (config, keys, registration). Use --purge to remove."
fi

echo "Done."
