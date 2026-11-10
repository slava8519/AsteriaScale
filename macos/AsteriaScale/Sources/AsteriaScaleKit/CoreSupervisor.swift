import Darwin
import Foundation

/// Restart delays after consecutive failures; reset after a stable run.
public struct BackoffPolicy: Sendable, Equatable {
    public var delays: [Duration]
    public var stableAfter: Duration
    /// This many failures inside `crashLoopWindow` stop automatic restarts.
    public var crashLoopCount: Int
    public var crashLoopWindow: Duration

    public init(delays: [Duration] = [.milliseconds(500), .seconds(1), .seconds(2), .seconds(4), .seconds(8),
                                      .seconds(16), .seconds(30)],
                stableAfter: Duration = .seconds(60), crashLoopCount: Int = 5,
                crashLoopWindow: Duration = .seconds(120)) {
        self.delays = delays
        self.stableAfter = stableAfter
        self.crashLoopCount = crashLoopCount
        self.crashLoopWindow = crashLoopWindow
    }

    public func delay(afterConsecutiveFailures count: Int) -> Duration {
        delays[min(max(count, 1), delays.count) - 1]
    }
}

/// What core.hello reported, decoded leniently; HelloCheck decides whether
/// the app accepts it.
public struct CoreHelloInfo: Sendable, Equatable {
    public let protocolVersion: Int64?
    public let coreHostVersion: String
    public let coreVersion: String
    public let build: String
    public let pid: Int64?
    public let pgid: Int64?
    public let processGroupLeader: Bool?
    public let testCommands: Bool?
    public let blockedSignals: [String]?
    public let raw: JSONValue

    public init(_ hello: JSONValue) {
        protocolVersion = hello["protocol"]?.int64Value
        coreHostVersion = hello["coreHostVersion"]?.stringValue ?? ""
        coreVersion = hello["coreVersion"]?.stringValue ?? ""
        build = hello["build"]?.stringValue ?? ""
        pid = hello["pid"]?.int64Value
        pgid = hello["pgid"]?.int64Value
        processGroupLeader = hello["processGroupLeader"]?.boolValue
        testCommands = hello["testCommands"]?.boolValue
        blockedSignals = hello["blockedSignals"]?.stringArray
        raw = hello
    }
}

/// The checks the app makes on core.hello before it trusts a CoreHost.
public enum HelloCheck {
    /// Signals the app's stop and CoreHost's own child handling depend on.
    public static let signalsThatMustNotBeBlocked = ["SIGTERM", "SIGCHLD"]

    /// English problems for the log; empty when the hello is acceptable.
    public static func problems(_ hello: CoreHelloInfo, spawnedPid: pid_t, expectedBuild: String?,
                                allowTestCommands: Bool) -> [String] {
        var problems: [String] = []
        if hello.protocolVersion != CoreProtocol.version {
            problems.append("protocol \(hello.protocolVersion.map(String.init) ?? "missing"), expected \(CoreProtocol.version)")
        }
        if hello.pid != Int64(spawnedPid) {
            problems.append("pid \(hello.pid.map(String.init) ?? "missing") is not the spawned \(spawnedPid)")
        }
        // killpg reaches the ranker worker and llama-server only through a
        // group that CoreHost leads.
        if hello.processGroupLeader != true || hello.pgid != Int64(spawnedPid) {
            problems.append("not the leader of its own process group (pgid \(hello.pgid.map(String.init) ?? "missing"))")
        }
        if hello.testCommands != false, !(allowTestCommands && hello.testCommands == true) {
            problems.append("test commands are \(hello.testCommands.map { $0 ? "enabled" : "disabled" } ?? "unreported")")
        }
        if hello.build.isEmpty {
            problems.append("no build")
        } else if let expectedBuild, hello.build != expectedBuild {
            problems.append("build \(hello.build), expected \(expectedBuild)")
        }
        if let blocked = hello.blockedSignals {
            for signal in signalsThatMustNotBeBlocked where blocked.contains(signal) {
                problems.append("\(signal) is blocked in its signal mask")
            }
        } else {
            problems.append("no blockedSignals")
        }
        return problems
    }
}

/// A CoreHost the supervisor accepted: its hello and, with `initialize`, the
/// core.init result.
public struct CoreSessionInfo: Sendable, Equatable {
    public let pid: pid_t
    public let hello: CoreHelloInfo
    public let initResult: JSONValue?
}

public enum CoreState: Sendable, Equatable {
    case stopped
    case starting
    case ready(CoreSessionInfo)
    case restarting(attempt: Int, delay: Duration, reason: String)
    case failed(String)

    /// The state line under the menu-bar icon (M15).
    public var russian: String {
        switch self {
        case .stopped: return RU.stateStopped
        case .starting: return RU.stateStarting
        case .ready: return RU.stateReady
        case .restarting: return RU.stateRestarting
        case .failed: return RU.stateUnavailable
        }
    }

    /// Russian details for the state, when there are any.
    public var details: String? {
        switch self {
        case .stopped, .starting, .ready: return nil
        case .restarting(let attempt, let delay, let reason):
            return RU.restartDetail(attempt: attempt, delay: delay, reason: reason)
        case .failed(let reason): return reason
        }
    }

    public var isReady: Bool { if case .ready = self { return true }; return false }
}

/// Owns the CoreHost process: orphan sweep, spawn, hello checks, optional
/// core.init, heartbeat, deadlines, restart with backoff, orderly stop. The
/// menu bar, the overlays and (20.4) the upscaler never depend on CoreHost
/// being alive; they only observe `states`.
public actor CoreSupervisor {
    public struct Configuration: Sendable {
        public var executable: String
        public var arguments: [String]
        public var environment: [String: String]
        public var workingDirectory: String
        public var client = "asteria-scale-macos"
        /// CoreHost's informational version the app was built with; nil skips
        /// the comparison (never in a shipped app).
        public var expectedBuild: String?
        /// Only the suites' fakes may run with test commands.
        public var allowTestCommands = false
        /// Sends core.init after hello; ready then means Core is loaded.
        public var initialize = false
        public var helloDeadline = CoreDeadlines.hello
        public var initDeadline = CoreDeadlines.initialize
        public var pingInterval: Duration = .seconds(5)
        public var pingDeadline = CoreDeadlines.control
        public var shutdownGrace: Duration = .seconds(3)
        public var backoff = BackoffPolicy()
        /// Runtime/corehost.pid; nil disables the record and the sweep.
        public var hostRecord: URL?
        /// Executable prefixes the orphan sweep may kill.
        public var sweepPrefixes: [String] = []

        public init(executable: String, arguments: [String], environment: [String: String],
                    workingDirectory: String) {
            self.executable = executable
            self.arguments = arguments
            self.environment = environment
            self.workingDirectory = workingDirectory
        }
    }

    public nonisolated let states: AsyncStream<CoreState>
    private let stateContinuation: AsyncStream<CoreState>.Continuation
    private let config: Configuration
    private let onUnsolicited: CoreConnection.EventHandler
    private let log: @Sendable (String) -> Void
    public private(set) var state: CoreState = .stopped
    public private(set) var connection: CoreConnection?
    private var generation = 0
    private var consecutiveFailures = 0
    private var failureTimes: [ContinuousClock.Instant] = []
    private var readySince: ContinuousClock.Instant?
    private var pingTask: Task<Void, Never>?
    private var restartTask: Task<Void, Never>?
    private var stopping = false
    public private(set) var restarts = 0
    public private(set) var lastCloseReason: CloseReason?
    public private(set) var lastSweep: SweepReport?

    public init(_ config: Configuration, onUnsolicited: @escaping CoreConnection.EventHandler = { _ in },
                log: @escaping @Sendable (String) -> Void = { _ in }) {
        self.config = config
        self.onUnsolicited = onUnsolicited
        self.log = log
        (states, stateContinuation) = AsyncStream.makeStream(of: CoreState.self, bufferingPolicy: .bufferingNewest(32))
        stateContinuation.yield(.stopped)
    }

    private func set(_ new: CoreState) {
        guard new != state else { return }
        state = new
        stateContinuation.yield(new)
    }

    /// First start. Sweeps what a previous app left behind, then launches.
    public func start() async {
        stopping = false
        restartTask?.cancel()
        restartTask = nil
        if let record = config.hostRecord {
            let report = OrphanSweep.sweep(recordAt: record, allowedPrefixes: config.sweepPrefixes)
            lastSweep = report
            if !report.killed.isEmpty || !report.spared.isEmpty {
                log("corehost: orphan sweep: \(report.note); killed \(report.killed), spared \(report.spared)")
            }
        }
        set(.starting)
        await launch()
    }

    /// The menu's «Перезапустить перевод»: also clears the crash-loop guard.
    public func restartNow() async {
        consecutiveFailures = 0
        failureTimes.removeAll()
        restartTask?.cancel()
        restartTask = nil
        await stopCurrent(orderly: false)
        stopping = false
        set(.starting)
        await launch()
    }

    /// Orderly stop; returns once CoreHost's group is dead.
    public func stop() async {
        stopping = true
        restartTask?.cancel()
        restartTask = nil
        await stopCurrent(orderly: true)
        set(.stopped)
    }

    /// After a wake from sleep: check the helper now instead of within 5 s.
    public func pingNow() async {
        guard state.isReady, let connection else { return }
        _ = try? await connection.request("core.ping", deadline: config.pingDeadline)
    }

    private func stopCurrent(orderly: Bool) async {
        pingTask?.cancel()
        pingTask = nil
        generation += 1   // the old connection's callbacks are ignored from now on
        guard let connection else { return }
        self.connection = nil
        if orderly {
            _ = await connection.shutdown(grace: config.shutdownGrace)
        } else {
            await connection.close(.killed)
            _ = await connection.waitForExit()
        }
        removeRecord(naming: connection.pid)
    }

    private func launch() async {
        generation += 1
        let current = generation
        guard FileManager.default.isExecutableFile(atPath: config.executable) else {
            log("corehost: no executable at \(config.executable)")
            set(.failed(RU.reasonHelperMissing))
            return
        }
        let callbacks = CoreConnection.Callbacks(
            onUnsolicited: onUnsolicited,
            onStderrLine: { [log] line in log("corehost stderr: \(line)") },
            onClose: { [weak self] reason in Task { await self?.connectionClosed(current, reason) } },
            onExit: { [weak self] pid, _ in Task { await self?.removeRecord(naming: pid) } })
        let connection: CoreConnection
        do {
            connection = try await CoreConnection.launch(
                executable: config.executable, arguments: config.arguments, environment: config.environment,
                workingDirectory: config.workingDirectory, callbacks: callbacks)
        } catch {
            log("corehost: spawn failed: \(error)")
            await failed(reason: RU.reasonSpawnFailed)
            return
        }
        guard current == generation else {
            await connection.close(.killed)
            return
        }
        self.connection = connection
        if let record = config.hostRecord {
            do { try OrphanSweep.record(pid: connection.pid, executable: config.executable, to: record) } catch {
                log("corehost: cannot record pid \(connection.pid): \(error.reason)")
            }
        }

        let hello: CoreHelloInfo
        do {
            let result = try await connection.request(
                "core.hello", ["client": .string(config.client), "protocolMin": .integer(CoreProtocol.version),
                               "protocolMax": .integer(CoreProtocol.version)],
                deadline: config.helloDeadline)
            hello = CoreHelloInfo(result)
        } catch {
            log("corehost: hello failed: \(error)")
            await connection.close(.helloRejected("\(error)"))
            return
        }
        guard current == generation else { return }
        let problems = HelloCheck.problems(hello, spawnedPid: connection.pid, expectedBuild: config.expectedBuild,
                                           allowTestCommands: config.allowTestCommands)
        guard problems.isEmpty else {
            log("corehost: hello rejected: \(problems.joined(separator: "; "))")
            await connection.close(.helloRejected(problems.joined(separator: "; ")))
            return
        }

        var initResult: JSONValue?
        if config.initialize {
            do {
                initResult = try await connection.request("core.init", deadline: config.initDeadline)
            } catch {
                guard current == generation else { return }
                guard let refusal = error as? CoreError, !(await connection.isClosed) else {
                    // The connection is gone; its close starts the restart.
                    log("corehost: core.init interrupted: \(error)")
                    return
                }
                // CoreHost refused (morphology_missing, dictionary_missing):
                // missing data does not come back by restarting, so show its
                // reason and stop instead of looping.
                log("corehost: core.init failed: \(refusal)")
                await stopCurrent(orderly: true)
                set(.failed(RU.reasonInitFailed(refusal.message, code: refusal.code)))
                return
            }
            guard current == generation else { return }
        }

        readySince = .now
        set(.ready(CoreSessionInfo(pid: connection.pid, hello: hello, initResult: initResult)))
        startPing(connection)
    }

    private func startPing(_ connection: CoreConnection) {
        pingTask?.cancel()
        let interval = config.pingInterval, deadline = config.pingDeadline
        pingTask = Task {
            while !Task.isCancelled {
                try? await Task.sleep(for: interval)
                if Task.isCancelled { return }
                // A missed deadline closes the connection itself: kill, restart.
                _ = try? await connection.request("core.ping", deadline: deadline)
            }
        }
    }

    private func connectionClosed(_ closedGeneration: Int, _ reason: CloseReason) async {
        guard closedGeneration == generation else { return }
        lastCloseReason = reason
        pingTask?.cancel()
        pingTask = nil
        connection = nil
        log("corehost: connection closed: \(reason)")
        if stopping || reason == .shutdown { return }
        await failed(reason: reason.russian)
    }

    /// The record names one pid, so a newer CoreHost's record is never removed.
    private func removeRecord(naming pid: pid_t) {
        guard let record = config.hostRecord, OrphanSweep.read(record)?.pid == pid else { return }
        try? FileManager.default.removeItem(at: record)
    }

    private func failed(reason: String) async {
        let now = ContinuousClock.now
        if let since = readySince, since.duration(to: now) >= config.backoff.stableAfter {
            consecutiveFailures = 0
            failureTimes.removeAll()
        }
        readySince = nil
        consecutiveFailures += 1
        failureTimes.append(now)
        failureTimes.removeAll { $0.duration(to: now) > config.backoff.crashLoopWindow }
        if failureTimes.count >= config.backoff.crashLoopCount {
            log("corehost: \(failureTimes.count) failures in \(config.backoff.crashLoopWindow); no more automatic restarts")
            set(.failed(RU.reasonCrashLoop))
            return
        }
        let delay = config.backoff.delay(afterConsecutiveFailures: consecutiveFailures)
        set(.restarting(attempt: consecutiveFailures, delay: delay, reason: reason))
        restarts += 1
        let scheduled = generation
        restartTask = Task { [weak self] in
            try? await Task.sleep(for: delay)
            guard !Task.isCancelled else { return }
            await self?.restartAfterBackoff(scheduled)
        }
    }

    private func restartAfterBackoff(_ scheduled: Int) async {
        guard !stopping, scheduled == generation else { return }
        await launch()
    }

    /// Forwards one request to the live CoreHost. Throws not_ready while it
    /// is starting or restarting, and not_running when it is failed or
    /// stopped, where no restart is scheduled; callers degrade (hide the
    /// overlay or say why).
    public func request(_ command: String, _ payload: JSONValue? = nil, deadline: Duration,
                        cancelInHost: Bool = false,
                        onSent: (@Sendable (Int64) -> Void)? = nil,
                        onEvent: CoreConnection.EventHandler? = nil) async throws -> JSONValue {
        guard state.isReady, let connection else {
            switch state {
            case .failed, .stopped: throw CoreError.notRunning
            case .starting, .restarting, .ready: throw CoreError.notReady
            }
        }
        return try await connection.request(command, payload, deadline: deadline, cancelInHost: cancelInHost,
                                            onSent: onSent, onEvent: onEvent)
    }
}
