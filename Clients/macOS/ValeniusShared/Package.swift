// swift-tools-version:5.10
import PackageDescription

let package = Package(
    name: "ValeniusShared",
    platforms: [.macOS(.v13)],
    products: [
        .library(name: "ValeniusShared", targets: ["ValeniusShared"]),
    ],
    targets: [
        .target(name: "ValeniusShared"),
    ]
)
