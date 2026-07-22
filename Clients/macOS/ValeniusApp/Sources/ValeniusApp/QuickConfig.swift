// Build-time quick-config default backend URL — stamped by build-pkg.sh's
// --quick-config-url (edit-in-place, restored after build, same mechanism as the version
// stamp). Mirrors the mobile client's VALENIUS_QUICK_CONFIG_URL --dart-define: empty in
// dev/OSS builds, where the triple-click-the-logo shortcut in BackendUrlView is a silent
// no-op. Never hardcode the managed-cloud host here — it would ship to the public OSS
// mirror and trip the publish gate.
let quickConfigUrl = ""
