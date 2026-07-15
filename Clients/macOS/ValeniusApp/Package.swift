// swift-tools-version:5.10
import PackageDescription

// The menu bar app. Built as a SwiftPM executable (AppKit is in the macOS SDK) rather than an
// .xcodeproj — keeps the whole client `swift build`-able and CI-friendly. The packaging step
// (M7) assembles the binary + Info.plist + icons into Valenius.app; menu-bar (no-Dock)
// behavior is set in code via NSApp.setActivationPolicy(.accessory), not an Info.plist key.
import PackageDescription

let package = Package(
    name: "ValeniusApp",
    platforms: [.macOS(.v13)],
    dependencies: [
        .package(path: "../ValeniusShared"),
    ],
    targets: [
        .executableTarget(
            name: "ValeniusApp",
            dependencies: [
                .product(name: "ValeniusShared", package: "ValeniusShared"),
            ]
        ),
    ]
)
