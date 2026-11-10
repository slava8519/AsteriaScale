import Darwin
import Foundation
import Testing
@testable import AsteriaScaleKit

/// Collects every state a supervisor publishes.
final class StateLog: @unchecked Sendable {
    let states = Box<[CoreState]>([])
    private var task: Task<Void, Never>?

    init(_ supervisor: CoreSupervisor) {
        let states = self.states
        task = Task { for await state in supervisor.states { states.mutate { $0.append(state) } } }
    }

    func cancel() { task?.cancel() }
}

extension ProcessSuites {
    @Suite struct SupervisorTests {
        @Test func startsReadyAndRecoversFromKill9() async throws {
            let scratch = try Scratch("kill9")
            defer { scratch.remove() }
            var config = Fake.config("normal", scratch)
            let record = try scratch.folder("runtime").appendingPathComponent("corehost.pid")
            config.hostRecord = record
            config.sweepPrefixes = ["/Library/Developer/CommandLineTools/", "/Applications/Xcode.app/"]
            let supervisor = CoreSupervisor(config)
            await supervisor.start()
            #expect(await supervisor.state.isReady)
            let firstPid = try #require(await supervisor.connection?.pid)
            #expect(OrphanSweep.read(record)?.pid == firstPid, "the record names the live host")
            let killedAt = ContinuousClock.now
            Darwin.kill(firstPid, SIGKILL)
            #expect(await eventually {
                let pid = await supervisor.connection?.pid
                return await supervisor.state.isReady && pid != nil && pid != firstPid
            })
            #expect(killedAt.duration(to: .now) < .seconds(3), "ready again within 3 s")
            // stdout EOF or the reaped exit, whichever the connection saw first.
            let reason = await supervisor.lastCloseReason
            if case .exited? = reason {} else { Issue.record("expected an unexpected exit, got \(String(describing: reason))") }
            let pong = try await supervisor.request("core.ping", deadline: .seconds(2))
            #expect(pong["uptimeMs"] != nil)
            let secondPid = try #require(await supervisor.connection?.pid)
            #expect(await eventually { OrphanSweep.read(record)?.pid == secondPid })
            await supervisor.stop()
            #expect(await supervisor.state == .stopped)
            #expect(ProcessTable.liveMembers(secondPid).isEmpty)
            #expect(!FileManager.default.fileExists(atPath: record.path), "the record goes with the host")
        }

        @Test func pingDeadlineRestartsAHungHost() async throws {
            let scratch = try Scratch("pinghang")
            defer { scratch.remove() }
            let supervisor = CoreSupervisor(Fake.config("normal", scratch))
            await supervisor.start()
            let firstPid = try #require(await supervisor.connection?.pid)
            // test.hang blocks the fake's reader, so its pings go unanswered.
            Task { _ = try? await supervisor.request("test.hang", deadline: .seconds(30)) }
            #expect(await eventually {
                let pid = await supervisor.connection?.pid
                return await supervisor.state.isReady && pid != nil && pid != firstPid
            })
            #expect(await supervisor.lastCloseReason == .deadline("core.ping"))
            #expect(ProcessTable.liveMembers(firstPid).isEmpty)
            await supervisor.stop()
        }

        @Test func crashLoopStopsAutomaticRestarts() async throws {
            let scratch = try Scratch("crashloop")
            defer { scratch.remove() }
            let supervisor = CoreSupervisor(Fake.config("crash-on-start", scratch))
            await supervisor.start()
            #expect(await eventually { isFailed(await supervisor.state) })
            #expect(await supervisor.state == .failed(RU.reasonCrashLoop))
            #expect(await supervisor.state.russian == "Перевод: недоступен")
            #expect(await supervisor.restarts == 3)
            await supervisor.stop()
        }

        @Test func wrongProtocolVersionIsRejected() async throws {
            let scratch = try Scratch("protocol")
            defer { scratch.remove() }
            let supervisor = CoreSupervisor(Fake.config("bad-protocol", scratch))
            await supervisor.start()
            #expect(await eventually {
                if case .helloRejected(let why)? = await supervisor.lastCloseReason { return why.contains("protocol 99") }
                return false
            })
            await supervisor.stop()
        }

        /// Each unacceptable hello is a failure: a restart, and at the crash-loop
        /// limit the visible `failed` state, never a trusted session.
        @Test(arguments: [("not-leader", "not the leader of its own process group"),
                          ("test-commands", "test commands are enabled"),
                          ("wrong-build", "build other-build, expected fake-build"),
                          ("blocked-sigterm", "SIGTERM is blocked in its signal mask")])
        func unacceptableHelloRestartsThenFails(_ mode: String, _ problem: String) async throws {
            let scratch = try Scratch("hello-\(mode)")
            defer { scratch.remove() }
            let supervisor = CoreSupervisor(Fake.config(mode, scratch))
            let log = StateLog(supervisor)
            defer { log.cancel() }
            await supervisor.start()
            #expect(await eventually { isFailed(await supervisor.state) })
            let reason = await supervisor.lastCloseReason
            guard case .helloRejected(let why)? = reason else {
                Issue.record("expected a rejected hello, got \(String(describing: reason))")
                return
            }
            #expect(why.contains(problem), "\(why)")
            #expect(log.states.get.contains { if case .restarting(_, _, let reason) = $0 { return reason == RU.reasonHelloRejected }; return false })
            #expect(!log.states.get.contains(where: isReady), "an unacceptable host never became ready")
            await supervisor.stop()
        }

        @Test func requestsWhileNotReadyFailFast() async throws {
            let scratch = try Scratch("notready")
            defer { scratch.remove() }
            let supervisor = CoreSupervisor(Fake.config("hang-on-hello", scratch))
            let starting = Task { await supervisor.start() }
            try await Task.sleep(for: .milliseconds(100))
            let started = ContinuousClock.now
            await #expect(throws: CoreError.notReady) { _ = try await supervisor.request("core.ping", deadline: .seconds(1)) }
            #expect(started.duration(to: .now) < .milliseconds(100))
            await supervisor.stop()
            await starting.value
        }

        @Test func missingExecutableIsAVisibleFailure() async throws {
            let scratch = try Scratch("missing")
            defer { scratch.remove() }
            var config = Fake.config("normal", scratch)
            config.executable = scratch.root.appendingPathComponent("AsteriaScale.CoreHost").path
            let supervisor = CoreSupervisor(config)
            await supervisor.start()
            #expect(await supervisor.state == .failed(RU.reasonHelperMissing))
            #expect(await supervisor.state.details == "не найден исполняемый файл службы перевода")
            // Failed: no restart is scheduled, so a request is not_running
            // (not not_ready), and the overlay does not promise a restart.
            await #expect(throws: CoreError.notRunning) { _ = try await supervisor.request("core.ping", deadline: .seconds(1)) }
            let outcome = await HoverClient(supervisor: supervisor).resolve(try DiagnosticSelection.parse("[bank]"),
                                                                            onUpdate: { _ in })
            #expect(outcome == .rejected(CoreError.notRunning))
            #expect(HoverReport.after(outcome, shownPrimary: false).overlay == .message(RU.overlayNotRunning))
        }

        /// A refused core.init (data missing in the bundle) does not loop: the
        /// state names CoreHost's own reason and code.
        @Test func refusedInitStopsWithItsReason() async throws {
            let scratch = try Scratch("initfails")
            defer { scratch.remove() }
            var config = Fake.config("init-fails", scratch)
            config.initialize = true
            let supervisor = CoreSupervisor(config)
            await supervisor.start()
            #expect(await supervisor.state == .failed("Не найдены словари перевода. (dictionary_missing)"))
            #expect(await supervisor.restarts == 0)
            #expect(await supervisor.connection == nil)
        }

        @Test func manualRestartStartsAFreshHost() async throws {
            let scratch = try Scratch("manual")
            defer { scratch.remove() }
            var config = Fake.config("normal", scratch)
            config.initialize = true
            let supervisor = CoreSupervisor(config)
            await supervisor.start()
            guard case .ready(let first) = await supervisor.state else { Issue.record("not ready"); return }
            #expect(first.initResult?["dictionary"]?["entries"] == 3)
            await supervisor.restartNow()
            guard case .ready(let second) = await supervisor.state else { Issue.record("not ready again"); return }
            #expect(second.pid != first.pid)
            #expect(ProcessTable.liveMembers(first.pid).isEmpty)
            await supervisor.stop()
        }

        /// A host that ignores core.shutdown and SIGTERM still has an empty
        /// group within grace + 1 s.
        @Test func stubbornHostIsGoneWithinGracePlusOneSecond() async throws {
            let scratch = try Scratch("stubborn")
            defer { scratch.remove() }
            let supervisor = CoreSupervisor(Fake.config("stubborn", scratch))
            await supervisor.start()
            let pid = try #require(await supervisor.connection?.pid)
            let spawned = try await supervisor.request("test.spawnGrandchild", ["inherit": true], deadline: .seconds(5))
            let grandchild = pid_t(try #require(spawned["pid"]?.int64Value))
            let started = ContinuousClock.now
            await supervisor.stop()
            let took = started.duration(to: .now)
            #expect(took < .seconds(2) + .milliseconds(300), "stop took \(took) with a 1 s grace")
            #expect(ProcessTable.liveMembers(pid).isEmpty && !ProcessTable.isAlive(grandchild))
        }
    }

    @Suite struct HoverClientTests {
        @Test func supersededHoverEndsAsSuperseded() async throws {
            let scratch = try Scratch("supersede")
            defer { scratch.remove() }
            let supervisor = CoreSupervisor(Fake.config("normal", scratch))
            await supervisor.start()
            let hover = HoverClient(supervisor: supervisor)
            let slow = try DiagnosticSelection.parse("a [slow] one")
            let first = Task { await hover.resolve(slow, onUpdate: { _ in }) }
            try await Task.sleep(for: .milliseconds(150))
            let updates = Box<[HoverUpdate]>([])
            let second = await hover.resolve(try DiagnosticSelection.parse("The [bank] was steep"),
                                             onUpdate: { update in updates.mutate { $0.append(update) } })
            #expect(second == .completed)
            #expect(await first.value == .superseded)
            guard updates.get.count == 2, case .primary(let primary) = updates.get[0], case .phrase(let phrase) = updates.get[1] else {
                Issue.record("expected primary then phrase, got \(updates.get)")
                return
            }
            #expect(primary.translation == "перевод: bank" && primary.provenance == .dictionary && primary.badge == "СЛОВАРЬ")
            #expect(primary.hoverId == phrase.hoverId && phrase.provisional)
            #expect(await hover.displayed(hoverId: primary.hoverId, part: .primary))
            #expect(await hover.displayed(hoverId: primary.hoverId + 100, part: .primary) == false)
            let connection = try #require(await supervisor.connection)
            #expect(await eventually { await invariantHolds(connection) })
            await supervisor.stop()
        }

        @Test func cancelledHoverTellsTheHost() async throws {
            let scratch = try Scratch("hovercancel")
            defer { scratch.remove() }
            let supervisor = CoreSupervisor(Fake.config("normal", scratch))
            await supervisor.start()
            let connection = try #require(await supervisor.connection)
            let hover = HoverClient(supervisor: supervisor)
            let slow = try DiagnosticSelection.parse("a [slow] one")
            let pending = Task { await hover.resolve(slow, onUpdate: { _ in }) }
            try await Task.sleep(for: .milliseconds(150))
            pending.cancel()
            #expect(await pending.value == .cancelled)
            // request.cancel reached the fake: the slow hover's terminal is drained.
            #expect(await eventually { await connection.counters.drainedAbandoned == 1 })
            #expect(await invariantHolds(connection))
            await supervisor.stop()
        }

        @Test func outcomesAndRefusals() async throws {
            let scratch = try Scratch("outcomes")
            defer { scratch.remove() }
            let supervisor = CoreSupervisor(Fake.config("normal", scratch))
            let hover = HoverClient(supervisor: supervisor)
            // Not started yet (stopped): nothing will start it on its own.
            let early = await hover.resolve(try DiagnosticSelection.parse("[bank]"), onUpdate: { _ in })
            #expect(early == .rejected(CoreError.notRunning))
            await supervisor.start()
            #expect(await hover.resolve(try DiagnosticSelection.parse("[nothing]"), onUpdate: { _ in }) == .empty)
            #expect(await hover.resolve(try DiagnosticSelection.parse("[fail]"), onUpdate: { _ in }) == .failed)
            guard case .rejected(let error) = await hover.resolve(try DiagnosticSelection.parse("[disabled]"), onUpdate: { _ in }) else {
                Issue.record("expected hover_disabled")
                return
            }
            #expect(error.code == "hover_disabled" && error.message == "Перевод текста выключен — включите его в меню.")
            await supervisor.stop()
        }
    }

    /// The CoreHost tools/macos/test-swift.sh builds, launched exactly as the
    /// app launches it: AppPaths arguments and environment, a throwaway home.
    @Suite struct RealCoreHostTests {
        @Test func realCoreHostReachesHello() async throws {
            let executable = Repo.coreHostOutput.appendingPathComponent("AsteriaScale.CoreHost")
            guard FileManager.default.isExecutableFile(atPath: executable.path) else {
                Issue.record("\(executable.path) is missing; run the suite through tools/macos/test-swift.sh")
                return
            }
            guard let expectedBuild = ProcessInfo.processInfo.environment["ASTERIA_COREHOST_BUILD"], !expectedBuild.isEmpty else {
                Issue.record("ASTERIA_COREHOST_BUILD is not set; run the suite through tools/macos/test-swift.sh")
                return
            }
            let scratch = try Scratch("corehost")
            defer { scratch.remove() }
            let paths = AppPaths(home: scratch.home, coreHost: executable, dataRoot: Repo.coreHostOutput, rankerWorker: nil)
            try paths.createUserDirectories()
            var config = CoreSupervisor.Configuration(
                executable: executable.path, arguments: paths.coreHostArguments(),
                environment: paths.coreHostEnvironment(), workingDirectory: paths.coreHostWorkingDirectory.path)
            config.expectedBuild = expectedBuild
            config.initialize = true
            config.hostRecord = paths.coreHostRecord
            config.sweepPrefixes = [Repo.coreHostOutput.path]
            let statuses = Box<[String]>([])
            let supervisor = CoreSupervisor(config, onUnsolicited: { event in
                if event.name == "status" { statuses.mutate { $0.append(event.payload["text"]?.stringValue ?? "") } }
            })
            await supervisor.start()
            let state = await supervisor.state
            guard case .ready(let session) = state else {
                let reason = await supervisor.lastCloseReason
                Issue.record("CoreHost did not become ready: \(state) (\(String(describing: reason)))")
                await supervisor.stop()
                return
            }
            #expect(session.hello.blockedSignals == [], "spawned from an actor, yet an empty mask")
            #expect(session.hello.processGroupLeader == true && session.hello.testCommands == false)
            #expect(session.hello.build == expectedBuild)
            #expect(session.hello.raw["paths"]?["userData"]?.stringValue == paths.userData.path)
            #expect(session.hello.raw["platform"] == "macos")
            #expect((session.initResult?["dictionary"]?["entries"]?.int64Value ?? 0) > 0)
            #expect((session.initResult?["morphologyEntries"]?.int64Value ?? 0) > 0)
            #expect(OrphanSweep.read(paths.coreHostRecord)?.pid == session.pid)
            _ = try await supervisor.request("core.ping", deadline: .seconds(2))

            // The app's settings path against the real CoreHost: the first-run
            // choice (docs/MACOS.md T7), then a stale revision, which CoreHost
            // answers with conflict and the client recovers from once.
            let initial = try #require(session.initResult.flatMap { SettingsSnapshot(result: $0) })
            #expect(!initial.settings.hoverEnabled && initial.settings.senseModel == "consec"
                    && initial.settings.senseBackend == "cpu")
            let settingsClient = SettingsClient(supervisor: supervisor)
            await settingsClient.adopt(SettingsSnapshot(settings: initial.settings, revision: initial.revision - 1))
            let updated = try await settingsClient.update(SettingsPatch(hoverEnabled: true, mode: .dictionary))
            #expect(updated.revision == initial.revision + 1 && updated.settings.hoverEnabled
                    && updated.settings.translationMode == .dictionary)
            let status = try #require(ModelsStatus(try await supervisor.request("models.status", deadline: .seconds(2))))
            // (The dictionaries may be reloading for the new mode right now.)
            #expect(status.morphologyEntries > 0)
            #expect(status.senseLine.range(of: #"\p{Cyrillic}"#, options: .regularExpression) != nil, "\(status.senseLine)")
            await supervisor.stop()

            #expect(ProcessTable.liveMembers(session.pid).isEmpty)
            #expect(scratch.entries(paths.coreHostWorkingDirectory).isEmpty, "CoreHost's cwd stays empty")
            #expect(scratch.entries(paths.coreHostTemp).isEmpty, "CoreHost's TMPDIR stays empty")
            #expect(!FileManager.default.fileExists(atPath: paths.coreHostRecord.path))
            #expect(scratch.entries(scratch.home.appendingPathComponent("Library")).allSatisfy {
                $0 == "Application Support" || $0 == "Logs" || $0.hasPrefix("Application Support/AsteriaScale")
                    || $0.hasPrefix("Logs/AsteriaScale")
            })
            for text in statuses.get {
                #expect(!text.lowercased().contains(hostWord) && !text.contains("NPU OCR") && !text.contains("VULKAN"), "\(text)")
            }
        }
    }
}
