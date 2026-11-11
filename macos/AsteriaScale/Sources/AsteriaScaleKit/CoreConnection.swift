import Darwin
import Foundation

/// Why a connection ended. Every reason except `.shutdown` makes the
/// supervisor restart CoreHost.
public enum CloseReason: Sendable, Equatable, CustomStringConvertible {
    case shutdown
    case exited(String)
    case violation(String)
    case helloRejected(String)
    case deadline(String)
    case writeFailed(Int32)
    case readFailed(Int32)
    case killed

    public var description: String {
        switch self {
        case .shutdown: return "orderly shutdown"
        case .exited(let how): return "the helper ended (\(how))"
        case .violation(let why): return "protocol violation: \(why)"
        case .helloRejected(let why): return "core.hello rejected: \(why)"
        case .deadline(let command): return "\(command) missed its deadline"
        case .writeFailed(let code): return "write failed: \(String(cString: strerror(code)))"
        case .readFailed(let code): return "read failed: \(String(cString: strerror(code)))"
        case .killed: return "killed by the app"
        }
    }

    /// The Russian reason the status details show (M15: «служба перевода»).
    public var russian: String {
        switch self {
        case .shutdown, .killed: return RU.reasonStopped
        case .exited, .writeFailed, .readFailed: return RU.reasonExitedUnexpectedly
        case .violation: return RU.reasonProtocolBroken
        case .helloRejected: return RU.reasonHelloRejected
        case .deadline: return RU.reasonNoAnswer
        }
    }
}

/// Rule-6 bookkeeping, mirrored from the ranker worker host. At every point
/// sent == answered + drainedAbandoned + lost + inFlight.
public struct ConnectionCounters: Sendable, Equatable {
    public var sent = 0
    public var answered = 0
    public var abandoned = 0
    public var drainedAbandoned = 0
    public var lost = 0
    public var eventsDelivered = 0
    public var eventsDropped = 0
    public var unsolicitedEvents = 0
    public var stderrBytes = 0
}

/// One CoreHost process and its JSONL v1 session.
///
/// The actor runs on a private serial dispatch queue, and the stdout and
/// stderr read sources fire on that same queue, so lines are handled
/// synchronously in arrival order (events before their terminal) without
/// hopping through Tasks, which are not ordered. Writes go to a separate
/// serial queue: a write blocked on a hung helper never stalls reading, the
/// caller or the main thread.
///
/// The process group is killed before the leader is reaped (critic M14): a
/// watcher thread waits for the end with WNOWAIT, kills the group while the
/// zombie still holds its pid and process group id, waits until no member is
/// alive, and only then reaps it and reports the exit. After the reap the
/// group id could name somebody else's group, so nothing signals it again.
public actor CoreConnection {
    public nonisolated let pid: pid_t
    private nonisolated let queue: DispatchSerialQueue
    public nonisolated var unownedExecutor: UnownedSerialExecutor { queue.asUnownedSerialExecutor() }

    public typealias EventHandler = @Sendable (CoreEvent) -> Void

    private struct Slot {
        let command: String
        var continuation: CheckedContinuation<JSONValue, any Error>?
        var onEvent: EventHandler?
        var timer: DispatchSourceTimer?
        var abandoned = false
    }

    public struct Callbacks: Sendable {
        public var onUnsolicited: EventHandler
        public var onStderrLine: @Sendable (String) -> Void
        public var onClose: @Sendable (CloseReason) -> Void
        /// After the group is dead and the leader (this pid) reaped.
        public var onExit: @Sendable (pid_t, ExitStatus) -> Void

        public init(onUnsolicited: @escaping EventHandler = { _ in },
                    onStderrLine: @escaping @Sendable (String) -> Void = { _ in },
                    onClose: @escaping @Sendable (CloseReason) -> Void = { _ in },
                    onExit: @escaping @Sendable (pid_t, ExitStatus) -> Void = { _, _ in }) {
            self.onUnsolicited = onUnsolicited
            self.onStderrLine = onStderrLine
            self.onClose = onClose
            self.onExit = onExit
        }
    }

    private let child: SpawnedChild
    private let writeQueue: DispatchQueue
    private let callbacks: Callbacks
    private var stdoutSource: DispatchSourceRead?
    private var stderrSource: DispatchSourceRead?
    private var framer = LineFramer()
    private var stderrPartial = Data()
    private var nextId: Int64 = 1
    private var slots: [Int64: Slot] = [:]
    private var closeReason: CloseReason?
    private var leaderEnded = false
    private var stdinClosed = false
    public private(set) var exitStatus: ExitStatus?
    public private(set) var counters = ConnectionCounters()
    public private(set) var stderrTail: [String] = []

    public var isClosed: Bool { closeReason != nil }
    public var reason: CloseReason? { closeReason }
    public var inFlight: Int { slots.count }
    /// The id the next request gets.
    public var peekNextId: Int64 { nextId }

    private init(child: SpawnedChild, queue: DispatchSerialQueue, callbacks: Callbacks) {
        self.child = child
        self.pid = child.pid
        self.queue = queue
        self.writeQueue = DispatchQueue(label: "asteria.corehost.write.\(child.pid)")
        self.callbacks = callbacks
    }

    public static func launch(executable: String, arguments: [String], environment: [String: String],
                              workingDirectory: String, callbacks: Callbacks = Callbacks()) async throws -> CoreConnection {
        let child = try ChildProcess.spawn(executable: executable, arguments: arguments,
                                           environment: environment, workingDirectory: workingDirectory)
        let queue = DispatchSerialQueue(label: "asteria.corehost.read.\(child.pid)")
        let connection = CoreConnection(child: child, queue: queue, callbacks: callbacks)
        await connection.startIO()
        return connection
    }

    private func startIO() {
        let out = DispatchSource.makeReadSource(fileDescriptor: child.stdoutRead, queue: queue)
        out.setEventHandler { [weak self] in self?.assumeIsolated { $0.drain(stdout: true) } }
        out.setCancelHandler { [fd = child.stdoutRead] in Darwin.close(fd) }
        let err = DispatchSource.makeReadSource(fileDescriptor: child.stderrRead, queue: queue)
        err.setEventHandler { [weak self] in self?.assumeIsolated { $0.drain(stdout: false) } }
        err.setCancelHandler { [fd = child.stderrRead] in Darwin.close(fd) }
        stdoutSource = out
        stderrSource = err
        out.resume()
        err.resume()

        // One dedicated thread per helper: a blocking wait has no race with
        // an early exit, and it never occupies a dispatch worker.
        let pid = child.pid
        let queue = self.queue
        let watcher = Thread { [self] in
            ChildProcess.waitUntilEnded(pid)
            queue.sync { self.assumeIsolated { $0.leaderHasEnded() } }
            ProcessTable.waitUntilGroupDead(pid, timeout: .seconds(1))
            let status = ChildProcess.reap(pid)
            queue.async { self.assumeIsolated { $0.processExited(status) } }
        }
        watcher.name = "asteria.corehost.watch.\(pid)"
        watcher.start()
    }

    // MARK: Requests

    /// Sends one request and waits for its terminal. Events carrying its id
    /// reach `onEvent`, in order, before the call returns. Cancelling the
    /// calling task abandons the request: the caller gets CancellationError
    /// at once, the late terminal is drained and discarded, and with
    /// `cancelInHost` CoreHost also gets request.cancel for it. The deadline
    /// stays armed after abandoning: a request that never ends cannot be
    /// drained, so its helper is killed.
    ///
    /// - Parameter maxLineBytes: a tighter limit on the encoded line than
    ///   CoreHost's 1 MiB (observations: ObservationBudget). A longer line is
    ///   refused with `observations_too_large` before anything is sent and
    ///   before the request takes an id.
    public func request(_ command: String, _ payload: JSONValue? = nil, deadline: Duration,
                        cancelInHost: Bool = false,
                        maxLineBytes: Int? = nil,
                        onSent: (@Sendable (Int64) -> Void)? = nil,
                        onEvent: EventHandler? = nil) async throws -> JSONValue {
        if let closeReason { throw CoreError.connectionLost(closeReason.description) }
        try Task.checkCancellation()
        let id = nextId
        let line: Data
        do {
            line = try CoreRequestLine(id: id, command: command, payload: payload).encodedLine()
        } catch {
            throw CoreError(code: "bad_request", message: RU.errorText("bad_request"), detail: error.reason)
        }
        if let maxLineBytes, line.count > maxLineBytes {
            throw CoreError.observationsTooLarge(command, bytes: line.count, budget: maxLineBytes)
        }
        guard line.count <= CoreProtocol.maxRequestFrameBytes else {
            throw CoreError.requestTooLarge(command, bytes: line.count)
        }
        nextId += 1
        onSent?(id)
        let queue = self.queue
        return try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<JSONValue, any Error>) in
                slots[id] = Slot(command: command, continuation: continuation, onEvent: onEvent,
                                 timer: makeDeadline(id: id, after: deadline))
                counters.sent += 1
                write(line)
                if Task.isCancelled { abandon(id, cancelInHost: cancelInHost) }
            }
        } onCancel: {
            queue.async { [weak self] in self?.assumeIsolated { $0.abandon(id, cancelInHost: cancelInHost) } }
        }
    }

    private func abandon(_ id: Int64, cancelInHost: Bool) {
        guard var slot = slots[id], !slot.abandoned else { return }
        slot.abandoned = true
        slot.onEvent = nil
        let continuation = slot.continuation
        slot.continuation = nil
        slots[id] = slot
        counters.abandoned += 1
        continuation?.resume(throwing: CancellationError())
        if cancelInHost, closeReason == nil {
            Task { _ = try? await self.request("request.cancel", ["target": .integer(id)], deadline: .seconds(2)) }
        }
    }

    private func makeDeadline(id: Int64, after duration: Duration) -> DispatchSourceTimer {
        let timer = DispatchSource.makeTimerSource(queue: queue)
        let parts = duration.components
        let nanoseconds = max(0, parts.seconds) * 1_000_000_000 + parts.attoseconds / 1_000_000_000
        // DispatchTime is the uptime clock: a Mac that sleeps does not miss deadlines.
        timer.schedule(deadline: .now() + .nanoseconds(Int(nanoseconds)))
        timer.setEventHandler { [weak self] in self?.assumeIsolated { $0.deadlineMissed(id) } }
        timer.resume()
        return timer
    }

    private func deadlineMissed(_ id: Int64) {
        guard let slot = slots[id] else { return }
        close(.deadline(slot.command))
    }

    private func write(_ line: Data) {
        // After stdin was closed its descriptor number may already belong to
        // another file, so nothing may be written through it any more.
        guard !stdinClosed else {
            close(.writeFailed(EPIPE))
            return
        }
        let fd = child.stdinWrite
        let queue = self.queue
        writeQueue.async { [weak self] in
            let connection = self
            let failure: Int32? = line.withUnsafeBytes { raw -> Int32? in
                guard let base = raw.baseAddress else { return nil }
                var offset = 0
                while offset < raw.count {
                    let written = Darwin.write(fd, base + offset, raw.count - offset)
                    if written > 0 { offset += written; continue }
                    if written == -1 && errno == EINTR { continue }
                    return errno
                }
                return nil
            }
            if let failure {
                queue.async { connection?.assumeIsolated { $0.close(.writeFailed(failure)) } }
            }
        }
    }

    /// Closes our end of CoreHost's stdin once; the close runs after every
    /// write queued before it.
    private func closeStdinOnce() {
        guard !stdinClosed else { return }
        stdinClosed = true
        let fd = child.stdinWrite
        writeQueue.async { _ = Darwin.close(fd) }
    }

    // MARK: Reading

    private func drain(stdout: Bool) {
        let fd = stdout ? child.stdoutRead : child.stderrRead
        var buffer = [UInt8](repeating: 0, count: 64 * 1024)
        while true {
            if stdout, stdoutSource == nil { return }
            if !stdout, stderrSource == nil { return }
            let count = buffer.withUnsafeMutableBytes { Darwin.read(fd, $0.baseAddress, $0.count) }
            if count > 0 {
                let chunk = Data(buffer[0..<count])
                if stdout { receive(chunk) } else { receiveStderr(chunk) }
                continue
            }
            if count == 0 {
                if stdout {
                    stdoutSource?.cancel()
                    stdoutSource = nil
                    close(.exited(exitStatus?.description ?? "stdout closed"))
                } else {
                    flushStderrPartial()
                    stderrSource?.cancel()
                    stderrSource = nil
                }
                return
            }
            if errno == EINTR { continue }
            if errno == EAGAIN { return }
            if stdout {
                close(.readFailed(errno))
            } else {
                stderrSource?.cancel()
                stderrSource = nil
            }
            return
        }
    }

    private func receive(_ chunk: Data) {
        guard closeReason == nil else { return }
        let lines: [Data]
        do { lines = try framer.append(chunk) } catch { close(.violation(error.reason)); return }
        for line in lines {
            guard closeReason == nil else { return }
            let inbound: CoreInbound
            do { inbound = try CoreLineParser.parse(line) } catch { close(.violation(error.reason)); return }
            handle(inbound)
        }
    }

    private func handle(_ inbound: CoreInbound) {
        switch inbound {
        case .event(let event):
            guard let ref = event.ref else {
                counters.unsolicitedEvents += 1
                callbacks.onUnsolicited(event)
                return
            }
            guard let slot = slots[ref] else {
                // Events only ever precede their own terminal (G1).
                close(.violation("event \(event.name) for unknown or finished #\(ref)"))
                return
            }
            if slot.abandoned {
                counters.eventsDropped += 1
                return
            }
            counters.eventsDelivered += 1
            slot.onEvent?(event)
        case .terminal(let id, let result):
            guard let slot = slots.removeValue(forKey: id) else {
                close(.violation("a terminal for unknown or finished #\(id)"))
                return
            }
            slot.timer?.cancel()
            if slot.abandoned {
                counters.drainedAbandoned += 1
                return
            }
            counters.answered += 1
            switch result {
            case .success(let value): slot.continuation?.resume(returning: value)
            case .failure(let error): slot.continuation?.resume(throwing: error)
            }
            // After an answered core.shutdown CoreHost waits only for stdin
            // EOF (it then exits 0); every later request would be refused.
            if slot.command == "core.shutdown", case .success = result { close(.shutdown) }
        }
    }

    private func receiveStderr(_ chunk: Data) {
        counters.stderrBytes += chunk.count
        stderrPartial.append(chunk)
        while let newline = stderrPartial.firstIndex(of: 0x0A) {
            let line = String(decoding: stderrPartial[stderrPartial.startIndex..<newline].prefix(4096), as: UTF8.self)
            stderrPartial.removeSubrange(stderrPartial.startIndex...newline)
            appendStderrLine(line)
        }
        if stderrPartial.count > 64 * 1024 {
            appendStderrLine(String(decoding: stderrPartial.prefix(4096), as: UTF8.self) + "…")
            stderrPartial.removeAll()
        }
    }

    private func flushStderrPartial() {
        guard !stderrPartial.isEmpty else { return }
        appendStderrLine(String(decoding: stderrPartial.prefix(4096), as: UTF8.self))
        stderrPartial.removeAll()
    }

    private func appendStderrLine(_ line: String) {
        stderrTail.append(line)
        if stderrTail.count > 40 { stderrTail.removeFirst(stderrTail.count - 40) }
        callbacks.onStderrLine(line)
    }

    // MARK: Lifecycle

    /// Signals the whole group, but only while the leader is not yet reaped:
    /// afterwards the id may already name another group.
    private func killGroup(_ signal: Int32) {
        guard !leaderEnded else { return }
        killpg(pid, signal)
    }

    private func leaderHasEnded() {
        killGroup(SIGKILL)
        leaderEnded = true
    }

    private func processExited(_ status: ExitStatus) {
        exitStatus = status
        if stderrSource != nil { drain(stdout: false) }
        flushStderrPartial()
        stderrSource?.cancel()
        stderrSource = nil
        if closeReason == nil { close(.exited(status.description)) }
        callbacks.onExit(pid, status)
    }

    /// Ends the session; idempotent. Fails every request in flight, closes
    /// stdin, and kills the group unless the stop is orderly (then CoreHost
    /// exits by itself at stdin EOF and the watcher still kills what is left).
    public func close(_ reason: CloseReason) {
        guard closeReason == nil else { return }
        closeReason = reason
        if reason != .shutdown { killGroup(SIGKILL) }
        stdoutSource?.cancel()
        stdoutSource = nil
        closeStdinOnce()
        let failing = slots
        slots.removeAll()
        for (_, slot) in failing {
            slot.timer?.cancel()
            counters.lost += 1
            slot.continuation?.resume(throwing: CoreError.connectionLost(reason.description))
        }
        callbacks.onClose(reason)
    }

    /// Closes stdin (EOF) without anything else, as a vanished app would.
    public func closeStdin() {
        closeStdinOnce()
    }

    public func waitForExit() async -> ExitStatus {
        while true {
            if let exitStatus { return exitStatus }
            try? await Task.sleep(for: .milliseconds(5))
        }
    }

    /// The exit status once the group is dead and the leader reaped, or nil
    /// when that did not happen within `timeout`.
    public func waitForExit(timeout: Duration) async -> ExitStatus? {
        let deadline = ContinuousClock.now + timeout
        while ContinuousClock.now < deadline {
            if let exitStatus { return exitStatus }
            try? await Task.sleep(for: .milliseconds(5))
        }
        return exitStatus
    }

    /// Orderly stop: core.shutdown (its deadline is the grace period; a miss
    /// kills the group), stdin EOF, then SIGTERM and after 1 s SIGKILL to the
    /// group. Returns once the group is dead and the leader reaped, so the
    /// group is empty within grace + 1 s whatever the helper does.
    public func shutdown(grace: Duration = .seconds(3)) async -> ExitStatus {
        let deadline = ContinuousClock.now + grace
        if closeReason == nil {
            _ = try? await request("core.shutdown", deadline: grace)
            close(.shutdown)
        }
        if let status = await waitForExit(timeout: deadline - ContinuousClock.now) { return status }
        killGroup(SIGTERM)
        if let status = await waitForExit(timeout: .seconds(1)) { return status }
        killGroup(SIGKILL)
        return await waitForExit()
    }
}
