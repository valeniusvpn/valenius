#!/usr/bin/env bash
# Compile unmodified production sources with Linux platform doubles. Native swift
# test/build and a real installed macOS VPN remain mandatory release checks.
set -euo pipefail
component=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
scratch=$(mktemp -d)
trap 'rm -rf "$scratch"' EXIT
mkdir -p "$scratch/Sources/ValeniusDaemon" "$scratch/Sources/ValeniusShared" "$scratch/Tests/ValeniusDaemonTests"
for name in BackendOriginStore ConnectionPolicy DaemonState DaemonCore DeviceCredentialState JsonObject LocalPeerIdentity ProvisioningCommand Registration Settings TunnelStartGuard; do
    cp "$component/Sources/ValeniusDaemon/$name.swift" "$scratch/Sources/ValeniusDaemon/"
done
cp "$component/../ValeniusShared/Sources/ValeniusShared/IpcMessages.swift" "$scratch/Sources/ValeniusShared/"
cp "$component/../ValeniusShared/Sources/ValeniusShared/Protocol.swift" "$scratch/Sources/ValeniusShared/"
cp "$component/Tests/Portable/Adapters.swift" "$scratch/Sources/ValeniusDaemon/"
cp "$component/Tests/ValeniusDaemonTests/IpcDeadlineTests.swift" "$scratch/Tests/ValeniusDaemonTests/"
cp "$component/Tests/ValeniusDaemonTests/DeviceCredentialMigrationTests.swift" "$scratch/Tests/ValeniusDaemonTests/"
cp "$component/Tests/ValeniusDaemonTests/AuthorizationTests.swift" "$scratch/Tests/ValeniusDaemonTests/"
cp "$component/Tests/ValeniusDaemonTests/LocalOwnershipTests.swift" "$scratch/Tests/ValeniusDaemonTests/"
cp "$component/Tests/ValeniusDaemonTests/ProvisioningCommandTests.swift" "$scratch/Tests/ValeniusDaemonTests/"
cp "$component/Tests/Portable/CoreTests.swift" "$scratch/Tests/ValeniusDaemonTests/"
cp "$component/Tests/Portable/ProfileOwnershipTests.swift" "$scratch/Tests/ValeniusDaemonTests/"
cp "$component/Tests/Portable/ProvisioningTests.swift" "$scratch/Tests/ValeniusDaemonTests/"
cp "$component/../../tests/offline-access-cases.json" "$scratch/Tests/ValeniusDaemonTests/"
cat > "$scratch/Package.swift" <<'PACKAGE'
// swift-tools-version:5.10
import PackageDescription
let package = Package(name: "AuthorizationPortable", targets: [
    .target(name: "ValeniusShared"),
    .target(name: "ValeniusDaemon", dependencies: ["ValeniusShared"]),
    .testTarget(name: "ValeniusDaemonTests", dependencies: ["ValeniusDaemon"],
                resources: [.copy("offline-access-cases.json")])
])
PACKAGE
swift test --package-path "$scratch" "$@"
