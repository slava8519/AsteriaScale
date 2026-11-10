import Darwin
import Foundation
import Testing
@testable import AsteriaScaleKit

/// Files of the repository the suite reads in place.
enum Repo {
    /// macos/AsteriaScale/Tests/AsteriaScaleKitTests/<this file> is five levels below the root.
    static let root: URL = {
        var url = URL(fileURLWithPath: #filePath)
        for _ in 0..<5 { url.deleteLastPathComponent() }
        return url
    }()

    static let coreHostTests = root.appendingPathComponent("tests/AsteriaScale.Tests.MacOS/CoreHost")
    static let launchArguments = coreHostTests.appendingPathComponent("launch-args-v1.txt")
    static let golden = coreHostTests.appendingPathComponent("protocol-v1.golden.jsonl")
    /// The framework-dependent CoreHost build tools/macos/test-swift.sh makes
    /// first; Dictionaries/ and Morphology/ lie beside it.
    static let coreHostOutput = root.appendingPathComponent("src/AsteriaScale.CoreHost/bin/Release/net10.0")
    static let fakeCoreHost = root.appendingPathComponent(
        "macos/AsteriaScale/Tests/AsteriaScaleKitTests/Fixtures/fake-corehost.py")
}

/// The internal word for the host that the interface never uses (M15),
/// spelled with escapes so that no source line contains it.
let hostWord = "\u{044F}\u{0434}\u{0440}"

/// Every process the suite starts runs /bin/sleep for this many seconds:
/// tools/macos/test-swift.sh finds leftovers by it.
let sleepMarker = "3141.59"

/// The package's sleep(1) stand-in (Tests/AsteriaTestSleeper), which SwiftPM
/// builds beside the test bundle. A test that needs an executable inside its
/// scratch folder copies this one, never a system binary: a copy of /bin/sleep
/// dies as "Code Signature Invalid" and leaves a crash report.
enum TestSleeper {
    static func url() throws -> URL {
        // <bin>/AsteriaScalePackageTests.xctest/Contents/MacOS/<image>
        var info = Dl_info()
        guard dladdr(#dsohandle, &info) != 0, let name = info.dli_fname else {
            throw POSIXError(.ENOENT)
        }
        var bin = URL(fileURLWithPath: String(cString: name))
        for _ in 0..<4 { bin.deleteLastPathComponent() }
        let url = bin.appendingPathComponent("AsteriaTestSleeper")
        guard FileManager.default.isExecutableFile(atPath: url.path) else { throw POSIXError(.ENOENT) }
        return url
    }
}

/// A private folder tree, removed by the test, under ASTERIA_TEST_TMPDIR:
/// tools/macos/test-swift.sh points that at an empty folder of its work
/// folder and fails if anything is left in it. SwiftPM keeps its own lock
/// files in TMPDIR, so the check cannot be on TMPDIR itself.
final class Scratch: @unchecked Sendable {
    static let base: URL = {
        if let folder = ProcessInfo.processInfo.environment["ASTERIA_TEST_TMPDIR"], !folder.isEmpty {
            return URL(fileURLWithPath: folder, isDirectory: true)
        }
        return FileManager.default.temporaryDirectory
    }()

    let root: URL
    let home: URL
    let tmp: URL
    let cwd: URL

    /// Throws instead of trapping: a trap in the test process would leave a
    /// crash report in the owner's DiagnosticReports.
    init(_ label: String) throws {
        root = Scratch.base
            .appendingPathComponent("kit-\(label)-\(UUID().uuidString.prefix(8))", isDirectory: true)
            .resolvingSymlinksInPath()
        home = root.appendingPathComponent("home", isDirectory: true)
        tmp = root.appendingPathComponent("tmp", isDirectory: true)
        cwd = root.appendingPathComponent("cwd", isDirectory: true)
        for folder in [home.appendingPathComponent("Library/Application Support"), tmp, cwd] {
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        }
    }

    func folder(_ relative: String) throws -> URL {
        let url = root.appendingPathComponent(relative, isDirectory: true)
        try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        return url
    }

    /// Entries below `folder`, relative to it.
    func entries(_ folder: URL) -> [String] {
        guard let enumerator = FileManager.default.enumerator(atPath: folder.path) else { return [] }
        return enumerator.compactMap { $0 as? String }.sorted()
    }

    func remove() { try? FileManager.default.removeItem(at: root) }
}

/// The fake CoreHost (Fixtures/fake-corehost.py) under /usr/bin/python3,
/// which the Command Line Tools always provide. -I isolates it from the
/// user's site packages, -B keeps bytecode out of the repository, -u
/// unbuffers its pipes.
enum Fake {
    static let python = "/usr/bin/python3"

    static func arguments(_ mode: String, _ extra: [String] = []) -> [String] {
        ["-I", "-B", "-u", Repo.fakeCoreHost.path, mode] + extra
    }

    static func environment(_ scratch: Scratch) -> [String: String] {
        ["HOME": scratch.home.path, "CFFIXED_USER_HOME": scratch.home.path, "TMPDIR": scratch.tmp.path + "/",
         "PATH": "/usr/bin:/bin"]
    }

    static func connect(_ scratch: Scratch, mode: String = "normal", extra: [String] = [],
                        callbacks: CoreConnection.Callbacks = CoreConnection.Callbacks()) async throws -> CoreConnection {
        try await CoreConnection.launch(executable: python, arguments: arguments(mode, extra),
                                        environment: environment(scratch), workingDirectory: scratch.cwd.path,
                                        callbacks: callbacks)
    }

    static func config(_ mode: String = "normal", _ scratch: Scratch) -> CoreSupervisor.Configuration {
        var config = CoreSupervisor.Configuration(executable: python, arguments: arguments(mode),
                                                  environment: environment(scratch),
                                                  workingDirectory: scratch.cwd.path)
        config.expectedBuild = "fake-build"
        config.helloDeadline = .seconds(3)
        config.pingInterval = .milliseconds(200)
        config.pingDeadline = .milliseconds(700)
        config.shutdownGrace = .seconds(1)
        config.backoff = BackoffPolicy(delays: [.milliseconds(50), .milliseconds(100)], stableAfter: .seconds(60),
                                       crashLoopCount: 4, crashLoopWindow: .seconds(30))
        return config
    }

    static func hello(_ connection: CoreConnection) async throws -> JSONValue {
        try await connection.request("core.hello", ["client": "kit-tests", "protocolMin": 1, "protocolMax": 1],
                                     deadline: .seconds(5))
    }
}

final class Box<Value: Sendable>: @unchecked Sendable {
    private let lock = NSLock()
    private var value: Value

    init(_ value: Value) { self.value = value }

    var get: Value {
        lock.lock()
        defer { lock.unlock() }
        return value
    }

    func set(_ newValue: Value) {
        lock.lock()
        value = newValue
        lock.unlock()
    }

    func mutate(_ change: (inout Value) -> Void) {
        lock.lock()
        change(&value)
        lock.unlock()
    }
}

func eventually(_ timeout: Duration = .seconds(5), _ condition: () async -> Bool) async -> Bool {
    let end = ContinuousClock.now + timeout
    while ContinuousClock.now < end {
        if await condition() { return true }
        try? await Task.sleep(for: .milliseconds(10))
    }
    return await condition()
}

func invariantHolds(_ connection: CoreConnection) async -> Bool {
    let counters = await connection.counters
    let inFlight = await connection.inFlight
    return counters.sent == counters.answered + counters.drainedAbandoned + counters.lost + inFlight
}

func isReady(_ state: CoreState) -> Bool { state.isReady }

func isFailed(_ state: CoreState) -> Bool {
    if case .failed = state { return true }
    return false
}

/// Kills and reaps a child the test spawned itself.
func killAndReap(_ child: SpawnedChild) {
    Darwin.kill(child.pid, SIGKILL)
    _ = ChildProcess.reap(child.pid)
    child.closeParentEnds()
}

/// Reads everything a child writes to stdout until EOF.
func readAll(_ fd: Int32) -> String {
    var data = Data()
    var buffer = [UInt8](repeating: 0, count: 4096)
    while true {
        let count = buffer.withUnsafeMutableBytes { Darwin.read(fd, $0.baseAddress, $0.count) }
        if count > 0 { data.append(contentsOf: buffer[0..<count]); continue }
        if count == -1, errno == EINTR || errno == EAGAIN { usleep(1_000); continue }
        return String(decoding: data, as: UTF8.self)
    }
}

/// The blocked signals of the calling thread, by number.
func currentThreadBlockedSignals() -> Set<Int32> {
    var mask = sigset_t()
    pthread_sigmask(SIG_BLOCK, nil, &mask)
    return Set((1...31).filter { mask & (UInt32(1) << ($0 - 1)) != 0 })
}
