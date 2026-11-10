import Darwin
import Foundation
import Testing
@testable import AsteriaScaleKit

/// Every suite that starts processes runs serialized under this one: their
/// timing checks (deadlines, crash detection) must not compete with each
/// other for the machine.
@Suite(.serialized, .timeLimit(.minutes(2))) struct ProcessSuites {}

/// Spawns from inside an actor, as the supervisor does: the thread then
/// belongs to the Swift concurrency pool, whose signal mask blocks SIGTERM
/// and SIGCHLD.
actor SpawnProbe {
    static let maskScript = "import signal,sys; m=signal.pthread_sigmask(signal.SIG_BLOCK, []); "
        + "sys.stdout.write(','.join(str(int(s)) for s in sorted(m)))"

    func threadMask() -> Set<Int32> { currentThreadBlockedSignals() }

    /// The child's own report of its blocked signals, spawned through ChildProcess.
    func maskThroughChildProcess(_ scratch: Scratch) throws -> Set<Int32> {
        let child = try ChildProcess.spawn(executable: Fake.python, arguments: ["-I", "-B", "-c", Self.maskScript],
                                           environment: Fake.environment(scratch),
                                           workingDirectory: scratch.cwd.path)
        defer { _ = ChildProcess.reap(child.pid); child.closeParentEnds() }
        return Self.parse(readAll(child.stdoutRead))
    }

    /// The control: the same child through a plain posix_spawn without
    /// SETSIGMASK inherits this thread's mask.
    func maskThroughPlainSpawn(_ scratch: Scratch) throws -> Set<Int32> {
        var ends: [Int32] = [-1, -1]
        guard pipe(&ends) == 0 else { throw SpawnError(call: "pipe", code: errno) }
        var actions: posix_spawn_file_actions_t?
        posix_spawn_file_actions_init(&actions)
        defer { posix_spawn_file_actions_destroy(&actions) }
        posix_spawn_file_actions_adddup2(&actions, ends[1], 1)
        posix_spawn_file_actions_addclose(&actions, ends[0])
        let arguments: [String] = [Fake.python, "-I", "-B", "-c", Self.maskScript]
        let variables: [String] = ["PATH=/usr/bin:/bin", "HOME=\(scratch.home.path)", "TMPDIR=\(scratch.tmp.path)/"]
        let argv: [UnsafeMutablePointer<CChar>?] = arguments.map { strdup($0) } + [nil]
        let envp: [UnsafeMutablePointer<CChar>?] = variables.map { strdup($0) } + [nil]
        defer { argv.forEach { free($0) }; envp.forEach { free($0) } }
        var pid: pid_t = 0
        let result = posix_spawn(&pid, Fake.python, &actions, nil, argv, envp)
        Darwin.close(ends[1])
        defer { Darwin.close(ends[0]) }
        guard result == 0 else { throw SpawnError(call: "posix_spawn", code: result) }
        let output = readAll(ends[0])
        _ = ChildProcess.reap(pid)
        return Self.parse(output)
    }

    static func parse(_ text: String) -> Set<Int32> {
        Set(text.split(separator: ",").compactMap { Int32($0) })
    }
}

extension ProcessSuites {
    @Suite struct ConnectionTests {
        @Test func helloSpawnsInOwnGroupWithCleanInheritance() async throws {
            let scratch = try Scratch("hello")
            defer { scratch.remove() }
            let connection = try await Fake.connect(scratch)
            let hello = try await Fake.hello(connection)
            #expect(hello["protocol"] == 1)
            #expect(hello["pgid"] == .integer(Int64(connection.pid)), "the helper leads its own process group")
            #expect(hello["cwd"]?.stringValue == scratch.cwd.path)
            #expect(hello["tmpdir"]?.stringValue == scratch.tmp.path + "/")
            // CLOEXEC_DEFAULT: only 0, 1 and 2 (plus the descriptor listdir itself opens).
            let fds = hello["fds"]?.arrayValue?.compactMap(\.int64Value) ?? []
            #expect(Array(fds.prefix(3)) == [0, 1, 2] && fds.count <= 4, "inherited fds: \(fds)")
            #expect(hello["blockedSignals"] == [], "an empty signal mask")
            #expect(await connection.shutdown() == .exited(0))
            #expect(await invariantHolds(connection))
            #expect(scratch.entries(scratch.cwd).isEmpty && scratch.entries(scratch.tmp).isEmpty)
        }

        @Test func childSpawnedFromActorHasEmptySignalMask() async throws {
            let scratch = try Scratch("mask")
            defer { scratch.remove() }
            let probe = SpawnProbe()
            // The premise: the actor's thread blocks SIGTERM and SIGCHLD, and a
            // plain posix_spawn passes that mask on.
            let threadMask = await probe.threadMask()
            #expect(threadMask.isSuperset(of: [SIGTERM, SIGCHLD]), "pool thread mask: \(threadMask.sorted())")
            #expect(try await probe.maskThroughPlainSpawn(scratch) == threadMask)
            // ChildProcess resets it. (kinfo_proc.kp_proc.p_sigmask cannot show
            // this: the kernel always reports 0 there, so the child reports it.)
            #expect(try await probe.maskThroughChildProcess(scratch) == [])
        }

        /// CPython ignores SIGPIPE itself, so this checks /bin/sleep through the
        /// kernel's view (kinfo_proc.p_sigignore) while the parent ignores it.
        @Test func childDoesNotInheritIgnoredSIGPIPE() throws {
            let scratch = try Scratch("sigpipe")
            defer { scratch.remove() }
            let previous = signal(SIGPIPE, SIG_IGN)
            defer { signal(SIGPIPE, previous) }
            let bit = UInt32(1) << (SIGPIPE - 1)
            #expect((ProcessTable.ignoredSignals(getpid()) ?? 0) & bit != 0, "control: the parent ignores SIGPIPE")
            let child = try ChildProcess.spawn(executable: "/bin/sleep", arguments: [sleepMarker],
                                               environment: [:], workingDirectory: scratch.cwd.path)
            defer { killAndReap(child) }
            #expect((ProcessTable.ignoredSignals(child.pid) ?? bit) & bit == 0, "the child has the default SIGPIPE")
            #expect(getpgid(child.pid) == child.pid)
        }

        @Test func eventsArriveBeforeTheirTerminalInOrder() async throws {
            let scratch = try Scratch("events")
            defer { scratch.remove() }
            let connection = try await Fake.connect(scratch)
            let seen = Box<[Int64]>([])
            let result = try await connection.request("test.events", ["count": 5], deadline: .seconds(5), onEvent: { event in
                seen.mutate { $0.append(event.payload["i"]?.int64Value ?? -1) }
            })
            #expect(result["count"] == 5)
            #expect(seen.get == [0, 1, 2, 3, 4])
            _ = await connection.shutdown()
        }

        @Test func unsolicitedStatusEvent() async throws {
            let scratch = try Scratch("status")
            defer { scratch.remove() }
            let texts = Box<[String]>([])
            let connection = try await Fake.connect(scratch, callbacks: .init(onUnsolicited: { event in
                texts.mutate { $0.append(event.payload["text"]?.stringValue ?? "") }
            }))
            _ = try await connection.request("test.unsolicited", deadline: .seconds(5))
            #expect(texts.get == ["Словари загружены"])
            #expect(await connection.counters.unsolicitedEvents == 1)
            _ = await connection.shutdown()
        }

        @Test func cancelInFlightGivesExactlyOneTerminal() async throws {
            let scratch = try Scratch("cancel")
            defer { scratch.remove() }
            let connection = try await Fake.connect(scratch)
            let slowId = await connection.peekNextId
            let slow = Task { try await connection.request("test.sleep", ["ms": 5000], deadline: .seconds(10)) }
            #expect(await eventually { await connection.peekNextId > slowId })
            try await Task.sleep(for: .milliseconds(50))
            let ack = try await connection.request("request.cancel", ["target": .integer(slowId)], deadline: .seconds(5))
            #expect(ack["cancelled"] == true)
            await #expect(throws: CoreError.self) { _ = try await slow.value }
            do { _ = try await slow.value } catch let error as CoreError { #expect(error.code == "cancelled") }
            let counters = await connection.counters
            #expect(counters.sent == 2 && counters.answered == 2 && counters.lost == 0)
            _ = await connection.shutdown()
        }

        @Test func abandonedRequestIsDrainedNotAViolation() async throws {
            let scratch = try Scratch("abandon")
            defer { scratch.remove() }
            let connection = try await Fake.connect(scratch)
            let slow = Task { try await connection.request("test.sleep", ["ms": 300], deadline: .seconds(5)) }
            try await Task.sleep(for: .milliseconds(50))
            slow.cancel()
            await #expect(throws: CancellationError.self) { try await slow.value }
            #expect(await eventually { await connection.counters.drainedAbandoned == 1 })
            _ = try await connection.request("core.ping", deadline: .seconds(2))
            #expect(await connection.isClosed == false)
            #expect(await invariantHolds(connection))
            _ = await connection.shutdown()
        }

        /// With cancelInHost the helper also hears about it: request.cancel
        /// stops the work, whose `cancelled` terminal is drained.
        @Test func abandoningWithCancelInHostSendsRequestCancel() async throws {
            let scratch = try Scratch("hostcancel")
            defer { scratch.remove() }
            let connection = try await Fake.connect(scratch)
            let started = ContinuousClock.now
            let slow = Task {
                try await connection.request("test.sleep", ["ms": 10_000], deadline: .seconds(20), cancelInHost: true)
            }
            try await Task.sleep(for: .milliseconds(100))
            slow.cancel()
            await #expect(throws: CancellationError.self) { try await slow.value }
            #expect(await eventually {
                let counters = await connection.counters
                let inFlight = await connection.inFlight
                return counters.drainedAbandoned == 1 && inFlight == 0
            })
            #expect(started.duration(to: .now) < .seconds(5), "the sleep was cut short in the helper")
            let counters = await connection.counters
            #expect(counters.sent == 2 && counters.answered == 1 && counters.lost == 0)
            _ = await connection.shutdown()
        }

        @Test(arguments: ["test.violate", "test.unknownId", "test.duplicate", "test.eventAfterTerminal",
                          "test.unknownMember", "test.badVersion"])
        func protocolViolationsKillTheGroup(_ command: String) async throws {
            let scratch = try Scratch("violation")
            defer { scratch.remove() }
            let closed = Box<CloseReason?>(nil)
            let connection = try await Fake.connect(scratch, callbacks: .init(onClose: { closed.set($0) }))
            let grandchild = try await connection.request("test.spawnGrandchild", deadline: .seconds(5))
            let grandchildPid = pid_t(try #require(grandchild["pid"]?.int64Value))
            #expect(grandchild["pgid"] == .integer(Int64(connection.pid)))
            _ = try? await connection.request(command, deadline: .seconds(5))
            #expect(await eventually { closed.get != nil })
            if case .violation? = closed.get {} else { Issue.record("expected a violation, got \(String(describing: closed.get))") }
            #expect(await connection.waitForExit(timeout: .seconds(3)) == .signaled(SIGKILL))
            #expect(!ProcessTable.isAlive(grandchildPid), "the grandchild dies with the group")
            #expect(await invariantHolds(connection))
        }

        @Test func crashMidRequestFailsInFlightAsLost() async throws {
            let scratch = try Scratch("crash")
            defer { scratch.remove() }
            let connection = try await Fake.connect(scratch)
            let slow = Task { try await connection.request("test.sleep", ["ms": 5000], deadline: .seconds(10)) }
            try await Task.sleep(for: .milliseconds(50))
            let started = ContinuousClock.now
            _ = try? await connection.request("test.crash", deadline: .seconds(5))
            do {
                _ = try await slow.value
                Issue.record("expected a failure")
            } catch let error as CoreError {
                #expect(error.code == "connection_lost")
                #expect(error.message == "Служба перевода перезапустилась, запрос прерван.")
            }
            let detected = started.duration(to: .now)
            #expect(detected < .milliseconds(500), "crash detected after \(detected)")
            #expect(await connection.waitForExit(timeout: .seconds(2)) == .exited(70))
            let counters = await connection.counters
            #expect(counters.lost == 2 && counters.sent == 2)
        }

        @Test func hangIsKilledAtTheDeadline() async throws {
            let scratch = try Scratch("hang")
            defer { scratch.remove() }
            let connection = try await Fake.connect(scratch)
            let started = ContinuousClock.now
            await #expect(throws: CoreError.self) { _ = try await connection.request("test.hang", deadline: .milliseconds(300)) }
            let took = started.duration(to: .now)
            #expect(took >= .milliseconds(290) && took < .seconds(2), "\(took)")
            #expect(await connection.reason == .deadline("test.hang"))
            #expect(await connection.waitForExit(timeout: .seconds(2)) == .signaled(SIGKILL))
        }

        @Test func stderrFloodNeverBlocks() async throws {
            let scratch = try Scratch("flood")
            defer { scratch.remove() }
            let connection = try await Fake.connect(scratch)
            let result = try await connection.request("test.stderrFlood", ["bytes": .integer(16 << 20)], deadline: .seconds(10))
            #expect(result["flooded"] == .integer(16 << 20))
            _ = try await connection.request("core.ping", deadline: .seconds(2))
            #expect(await eventually { await connection.counters.stderrBytes >= 16 << 20 })
            #expect(await connection.stderrTail.count == 40)
            _ = await connection.shutdown()
        }

        @Test func writeToDeadHelperDoesNotRaiseSIGPIPE() async throws {
            // The test process keeps the default SIGPIPE disposition on purpose:
            // F_SETNOSIGPIPE on the write end alone must prevent a crash.
            let previous = signal(SIGPIPE, SIG_DFL)
            defer { signal(SIGPIPE, previous) }
            let scratch = try Scratch("deadwrite")
            defer { scratch.remove() }
            let connection = try await Fake.connect(scratch)
            Darwin.kill(connection.pid, SIGKILL)
            _ = await connection.waitForExit(timeout: .seconds(2))
            for _ in 0..<3 { _ = try? await connection.request("core.ping", deadline: .milliseconds(200)) }
            #expect(await connection.isClosed)
        }

        @Test func requestOverTheInboundLimitIsRefusedLocally() async throws {
            let scratch = try Scratch("toolarge")
            defer { scratch.remove() }
            let connection = try await Fake.connect(scratch)
            let text = JSONValue.string(String(repeating: "a", count: CoreProtocol.maxRequestFrameBytes))
            do {
                _ = try await connection.request("hover.resolve", ["text": text], deadline: .seconds(2))
                Issue.record("expected request_too_large")
            } catch let error as CoreError {
                #expect(error.code == "request_too_large")
            }
            #expect(await connection.counters.sent == 0)
            #expect(await connection.isClosed == false)
            _ = try await connection.request("core.ping", deadline: .seconds(2))
            _ = await connection.shutdown()
        }

        @Test func stdinEOFWithoutShutdownEndsTheHelper() async throws {
            let scratch = try Scratch("eof")
            defer { scratch.remove() }
            let connection = try await Fake.connect(scratch)
            _ = try await connection.request("core.ping", deadline: .seconds(2))
            await connection.closeStdin()
            #expect(await connection.waitForExit(timeout: .seconds(2)) == .exited(4))
        }

        /// Critic M14: the group dies before the leader is reaped. The
        /// grandchild keeps the fake's stdout open, so no EOF can trigger the
        /// kill; only the watcher's kill between WNOWAIT and the reap can.
        @Test func grandchildGoneBeforeProcessExited() async throws {
            let scratch = try Scratch("grandchild")
            defer { scratch.remove() }
            let grandchild = Box<pid_t>(0)
            let aliveAtExit = Box<Bool?>(nil)
            let connection = try await Fake.connect(scratch, callbacks: .init(onExit: { _, _ in
                aliveAtExit.set(ProcessTable.isAlive(grandchild.get))
            }))
            let spawned = try await connection.request("test.spawnGrandchild", ["inherit": true], deadline: .seconds(5))
            grandchild.set(pid_t(try #require(spawned["pid"]?.int64Value)))
            #expect(ProcessTable.isAlive(grandchild.get))
            Darwin.kill(connection.pid, SIGKILL)
            #expect(await connection.waitForExit(timeout: .seconds(3)) == .signaled(SIGKILL))
            #expect(aliveAtExit.get == false, "the grandchild was dead before the exit was reported")
            #expect(ProcessTable.liveMembers(connection.pid).isEmpty)
        }

        @Test func orderlyShutdownEmptiesTheGroup() async throws {
            let scratch = try Scratch("orderly")
            defer { scratch.remove() }
            let connection = try await Fake.connect(scratch)
            let spawned = try await connection.request("test.spawnGrandchild", ["inherit": true], deadline: .seconds(5))
            let grandchild = pid_t(try #require(spawned["pid"]?.int64Value))
            #expect(await connection.shutdown(grace: .seconds(2)) == .exited(0))
            #expect(await connection.reason == .shutdown)
            #expect(!ProcessTable.isAlive(grandchild))
            #expect(ProcessTable.liveMembers(connection.pid).isEmpty)
        }

        /// The golden transcript, replayed through the real transport: the
        /// fake answers each request with the golden's outbound lines, so the
        /// connection must deliver G1-G4 exactly as CoreHost orders them.
        @Test func goldenTranscriptReplaysThroughTheConnection() async throws {
            let scratch = try Scratch("golden")
            defer { scratch.remove() }
            let unsolicited = Box<[String]>([])
            let connection = try await Fake.connect(scratch, mode: "golden", extra: [Repo.golden.path], callbacks: .init(
                onUnsolicited: { event in unsolicited.mutate { $0.append(event.payload["code"]?.stringValue ?? "") } }))
            var requests: [(command: String, payload: JSONValue?)] = []
            for line in try GoldenTranscriptTests.lines() {
                let frame = try StrictJSON.parse(line)
                if let command = frame["command"]?.stringValue { requests.append((command, frame["payload"])) }
            }
            var outcomes: [Int64: String] = [:]
            var events: [Int64: [String]] = [:]
            var index = 0
            while index < requests.count {
                let id = await connection.peekNextId
                // #14/#15 and #17/#18 are pipelined in the golden: the second
                // goes out before the first one's terminal.
                let pipelined = id == 14 || id == 17
                let count = pipelined ? 2 : 1
                var tasks: [Task<String, Never>] = []
                let seen = Box<[String]>([])
                for offset in 0..<count {
                    let request = requests[index + offset]
                    let expectedId = id + Int64(offset)
                    tasks.append(Task {
                        do {
                            let result = try await connection.request(
                                request.command, request.payload, deadline: .seconds(5),
                                onEvent: { event in seen.mutate { $0.append("#\(expectedId) \(event.name)") } })
                            return "ok \(result["outcome"]?.stringValue ?? "")"
                        } catch let error as CoreError {
                            return "error \(error.code)"
                        } catch {
                            return "unexpected \(error)"
                        }
                    })
                    if offset == 0, pipelined {
                        #expect(await eventually { await connection.peekNextId == id + 1 })
                    }
                }
                for (offset, task) in tasks.enumerated() { outcomes[id + Int64(offset)] = await task.value }
                events[id] = seen.get
                index += count
            }
            #expect(outcomes[1] == "error hello_required")
            #expect(outcomes[2] == "ok " && outcomes[3] == "ok ")
            #expect(outcomes[4] == "error unknown_command")
            #expect((5...10).map { outcomes[Int64($0)] } == ["error not_implemented", "error not_implemented",
                     "error not_implemented", "error not_implemented", "error not_implemented", "error not_ready"])
            #expect(outcomes[11] == "error bad_request" && outcomes[12] == "error bad_request" && outcomes[13] == "ok ")
            #expect(outcomes[14] == "error cancelled" && outcomes[15] == "ok ")
            #expect(outcomes[17] == "error superseded" && outcomes[18] == "ok completed")
            #expect(outcomes[20] == "ok failed" && outcomes[21] == "error bad_request" && outcomes[22] == "ok ")
            #expect(events[17] == ["#18 hover.primary", "#18 hover.phrase", "#18 hover.phrase"])
            #expect(unsolicited.get == ["hover_failed"])
            let counters = await connection.counters
            #expect(counters.sent == 22 && counters.answered == 22 && counters.eventsDelivered == 3)
            // The answered core.shutdown (#22) closed the session: stdin EOF, exit 0.
            #expect(await connection.reason == .shutdown)
            #expect(await connection.waitForExit(timeout: .seconds(3)) == .exited(0))
        }
    }
}
