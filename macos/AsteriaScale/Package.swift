// swift-tools-version: 6.0
import PackageDescription

// The native macOS host of Asteria Scale (roadmap 20.2, docs/MACOS.md M2 and
// M11). Built with the Command Line Tools only: there is no Xcode project, so
// this file is the single source of truth for targets. Swift 6 language mode
// (strict concurrency) comes with tools version 6.0.
//
// Build and test only through tools/macos/test-swift.sh: it keeps SwiftPM's
// cache, configuration and security state under .build/swiftpm-state instead
// of ~/Library and ~/.swiftpm, runs the suite in a throwaway home and adds the
// framework flags Swift Testing needs with the Command Line Tools. Never use
// #Preview or other Xcode-only macros (the Command Line Tools ship no
// PreviewsMacros plugin) and never XCTest (the Command Line Tools have none).
let package = Package(
    name: "AsteriaScale",
    // ONNX Runtime 1.27.1 (the ranker worker) needs 14.0; docs/MACOS.md T14.
    platforms: [.macOS(.v14)],
    targets: [
        // Everything that runs without a window server: the JSONL protocol v1
        // client, the CoreHost supervisor, the single-instance lock, the
        // orphan sweep, paths and launch arguments, hotkey descriptions and
        // the Russian strings. Foundation and Darwin only, so all of it is
        // testable headless.
        .target(
            name: "AsteriaScaleKit",
            path: "Sources/AsteriaScaleKit"
        ),
        // Swift Testing only. Fixtures/ holds the fake CoreHost (stdlib
        // python3), which is data, not a source.
        .testTarget(
            name: "AsteriaScaleKitTests",
            dependencies: ["AsteriaScaleKit"],
            path: "Tests/AsteriaScaleKitTests",
            exclude: ["Fixtures"]
        ),
    ]
)
