# Changelog

## 1.0.1

- Fixed the Docker image failing to build under Home Assistant Supervisor
  ("not found" on every internal file). The daemon modules this add-on reuses are now
  vendored inside the add-on's own folder instead of being copied from elsewhere in the
  repository, which Supervisor's build process can't reach.

## 1.0.0

- Initial release: always-on Valenius WireGuard connector for Home Assistant, with an
  optional MQTT-discovery connected/disconnected entity.
