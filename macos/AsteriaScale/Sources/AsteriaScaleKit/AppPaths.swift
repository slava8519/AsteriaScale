import Foundation

/// The app's bundle id, owner decision M13 (docs/MACOS.md): macOS binds
/// privacy grants to it, so it never changes after the first Screen
/// Recording grant (20.3). Bundle/Info.plist and tools/macos/build-app.sh
/// name the same string (a test compares them); the helpers are signed as
/// <id>.corehost and <id>.rankerworker.
public enum AppIdentity {
    public static let bundleIdentifier = "io.github.slava8519.AsteriaScale"
}

/// Every location the app and CoreHost use. The app passes each of them to
/// CoreHost explicitly (the M12 seams), so Core never derives a path from
/// Environment.ProcessPath, SpecialFolder or the working directory.
///
/// .NET's SpecialFolder.LocalApplicationData on macOS ignores HOME: it
/// follows CFFIXED_USER_HOME and returns "" when
/// <home>/Library/Application Support does not exist (measured with .NET 10
/// on macOS 27). CoreHost therefore gets every root as an argument, and both
/// HOME and CFFIXED_USER_HOME point at the app's home, so a stray default
/// path can never reach another home.
public struct AppPaths: Sendable, Equatable {
    /// The CoreHost executable (Contents/Helpers/AsteriaScale.CoreHost in the bundle).
    public let coreHost: URL
    /// The ranker worker (Contents/Helpers/Ranker/AsteriaScale.RankerWorker),
    /// or nil to start CoreHost without one (it then reports worker_missing).
    public let rankerWorker: URL?
    /// Dictionaries/ and Morphology/ (Contents/Resources/Data, read only).
    public let dataRoot: URL
    public let home: URL
    /// ~/Library/Application Support/AsteriaScale: settings, «Мой словарь»,
    /// history and the three caches (docs/MACOS.md T8).
    public let userData: URL
    /// The worker's --model-directory: userData/Models/Ranker, where the owner
    /// copies the ConSeC bundle with ditto (docs/MACOS.md T9), unless a
    /// development run names another folder (the repository's
    /// src/AsteriaScale.RankerWorker/Models, read in place). Models are never
    /// inside the bundle (licence M7, size, the bundle seal).
    public let rankerModels: URL
    /// userData/Models/Ranker, whatever `rankerModels` is.
    public let defaultRankerModels: URL
    /// ~/Library/Logs/AsteriaScale: CoreHost's hover-translation.log and app.log.
    public let logs: URL
    /// userData/Runtime: locks, the CoreHost record and the folders below.
    public let runtime: URL
    /// CoreHost's TMPDIR (docs/MACOS.md T12).
    public let coreHostTemp: URL
    /// CoreHost's working directory: empty, so a stray relative write shows up.
    public let coreHostWorkingDirectory: URL
    /// The app's single-instance lock.
    public let instanceLock: URL
    /// The record of the running CoreHost for the orphan sweep.
    public let coreHostRecord: URL
    /// Swift-owned preferences (hotkeys, overlay level); settings.json is CoreHost's.
    public let preferences: URL

    public init(home: URL, coreHost: URL, dataRoot: URL, rankerWorker: URL?, rankerModels: URL? = nil) {
        self.home = home
        self.coreHost = coreHost
        self.dataRoot = dataRoot
        self.rankerWorker = rankerWorker
        userData = home.appendingPathComponent("Library/Application Support/AsteriaScale", isDirectory: true)
        defaultRankerModels = userData.appendingPathComponent("Models/Ranker", isDirectory: true)
        self.rankerModels = rankerModels ?? defaultRankerModels
        logs = home.appendingPathComponent("Library/Logs/AsteriaScale", isDirectory: true)
        runtime = userData.appendingPathComponent("Runtime", isDirectory: true)
        coreHostTemp = runtime.appendingPathComponent("tmp", isDirectory: true)
        coreHostWorkingDirectory = runtime.appendingPathComponent("cwd", isDirectory: true)
        instanceLock = runtime.appendingPathComponent("app.lock")
        coreHostRecord = runtime.appendingPathComponent("corehost.pid")
        preferences = userData.appendingPathComponent("macos-app.json")
    }

    /// The installed layout: helpers and data inside the bundle.
    public static func bundled(bundle: URL, home: URL, rankerModels: URL? = nil) -> AppPaths {
        let contents = bundle.appendingPathComponent("Contents", isDirectory: true)
        return AppPaths(home: home,
                        coreHost: contents.appendingPathComponent("Helpers/AsteriaScale.CoreHost"),
                        dataRoot: contents.appendingPathComponent("Resources/Data", isDirectory: true),
                        rankerWorker: contents.appendingPathComponent("Helpers/Ranker/AsteriaScale.RankerWorker"),
                        rankerModels: rankerModels)
    }

    /// The real app: NSHomeDirectory() follows CFFIXED_USER_HOME, which the
    /// isolated runs point at a throwaway home.
    public static func current(bundle: URL, rankerModels: URL? = nil) -> AppPaths {
        bundled(bundle: bundle, home: URL(fileURLWithPath: NSHomeDirectory(), isDirectory: true),
                rankerModels: rankerModels)
    }

    /// Creates the app's own folders. Never a model folder named on the
    /// command line: that one is read in place and must already exist.
    public func createUserDirectories() throws {
        for folder in [userData, defaultRankerModels, logs, runtime, coreHostTemp, coreHostWorkingDirectory] {
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        }
    }

    /// What the orphan sweep may kill: the bundle's helpers (CoreHost and
    /// the ranker worker) and, from 20.5, the TranslateGemma runtime.
    public var sweepPrefixes: [String] {
        var prefixes = [coreHost.deletingLastPathComponent().path]
        if let rankerWorker { prefixes.append(rankerWorker.deletingLastPathComponent().path) }
        prefixes.append(userData.appendingPathComponent("Models/TranslateGemma", isDirectory: true).path)
        return prefixes
    }

    /// How the app uses each launch argument of protocol v1. It must name
    /// exactly the required and optional lines of
    /// tests/AsteriaScale.Tests.MacOS/CoreHost/launch-args-v1.txt (a test
    /// reads that file); test and reserved arguments are never passed.
    public enum ArgumentUse: String, Sendable {
        case always
        case withRankerWorker
        case never
    }

    public static let launchArgumentUse: [String: ArgumentUse] = [
        "--data-root": .always,
        "--user-data": .always,
        "--log-dir": .always,
        // The caches live with the user data (T8), which is CoreHost's default.
        "--cache-dir": .never,
        "--ranker-worker": .withRankerWorker,
        "--ranker-models": .withRankerWorker,
    ]

    /// CoreHost's command line. Every path is absolute; CoreHost exits 2 otherwise.
    public func coreHostArguments() -> [String] {
        var arguments = ["--data-root", dataRoot.path, "--user-data", userData.path, "--log-dir", logs.path]
        if let rankerWorker {
            arguments += ["--ranker-worker", rankerWorker.path, "--ranker-models", rankerModels.path]
        }
        return arguments
    }

    /// A minimal, explicit environment (docs/MACOS.md T12):
    /// DOTNET_EnableDiagnostics=0, so no debugger FIFOs and no diagnostics
    /// socket appear in TMPDIR, and a private TMPDIR under Runtime.
    public func coreHostEnvironment(inherited: [String: String] = ProcessInfo.processInfo.environment) -> [String: String] {
        var environment = [
            "HOME": home.path,
            "CFFIXED_USER_HOME": home.path,
            "TMPDIR": coreHostTemp.path + "/",
            "PATH": "/usr/bin:/bin:/usr/sbin:/sbin",
            "DOTNET_EnableDiagnostics": "0",
        ]
        // USER and LOGNAME as the account has them; DOTNET_ROOT lets a
        // framework-dependent development build find its runtime.
        for key in ["USER", "LOGNAME", "DOTNET_ROOT"] {
            if let value = inherited[key], !value.isEmpty { environment[key] = value }
        }
        return environment
    }
}
