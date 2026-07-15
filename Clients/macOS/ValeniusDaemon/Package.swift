// swift-tools-version:5.10
import PackageDescription

let package = Package(
    name: "ValeniusDaemon",
    platforms: [.macOS(.v13)],
    dependencies: [
        .package(path: "../ValeniusShared"),
    ],
    targets: [
        .executableTarget(
            name: "ValeniusDaemon",
            dependencies: [
                .product(name: "ValeniusShared", package: "ValeniusShared"),
            ]
        ),
        .testTarget(
            name: "ValeniusDaemonTests",
            dependencies: ["ValeniusDaemon"]
        ),
    ]
)
