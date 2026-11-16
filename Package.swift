// swift-tools-version:5.10
import PackageDescription

let package = Package(
    name: "Peekaboo",
    platforms: [.macOS(.v14)],
    targets: [
        .executableTarget(
            name: "Peekaboo",
            path: "Sources/Peekaboo",
            linkerSettings: [.linkedFramework("IOKit")]
        )
    ]
)
