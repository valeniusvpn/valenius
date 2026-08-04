# Changelog

## 1.0.4

- Fixed the add-on always showing **offline** in the backend even while connected and
  running. `TrayRunning` (which the backend's presence tracker keys off) could never
  become true because it's normally set by the tray dispatching an IPC command to the
  daemon — this add-on has no tray. It now always reports itself online while the
  daemon is alive.
- Fixed the backend always showing the vendored source-tree default version (`1.0.0`)
  for this client, regardless of the installed add-on version. The client now reports
  the actual add-on version.

## 1.0.3

- Fixed every install of this add-on registering with the backend under the same
  container hostname, which the backend flagged as a name-collision duplicate on every
  install after the first. The client now registers as `display_name` (if set) or a
  generated name unique to that install, instead of the Supervisor-assigned container
  hostname.

## 1.0.2

- Fixed `wg-quick up` failing every attempt with `resolvconf: command not found`,
  which tore the tunnel back down as soon as it came up. Added `openresolv` to the
  image so the profile's `DNS =` line can actually be applied.

## 1.0.1

- Fixed the Docker image failing to build under Home Assistant Supervisor
  ("not found" on every internal file). The daemon modules this add-on reuses are now
  vendored inside the add-on's own folder instead of being copied from elsewhere in the
  repository, which Supervisor's build process can't reach.

## 1.0.0

- Initial release: always-on Valenius WireGuard connector for Home Assistant, with an
  optional MQTT-discovery connected/disconnected entity.
