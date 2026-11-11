import Foundation
import Testing
@testable import AsteriaScaleKit

/// Stubs and fixtures of the hover pipeline tests: frames whose first eight
/// bytes are their tag (the stub fingerprint reads it back), a recognizer
/// that reads the golden line with «bank» under the cursor, and a log every
/// stub and the recording core write to, in order.
enum PipelineFixtures {
    /// 1920x1080 points at 2x; the cursor (1925, 1430) of the C# and golden checks.
    static let display = DisplayDescription(id: 7, frame: PointRect(x: 0, y: 0, width: 1920, height: 1080),
                                            pixelWidth: 3840, pixelHeight: 2160)
    static let point = GlobalPoint(x: 962.5, y: 365)
    static let cursor = PixelPoint(x: 1925, y: 1430)
    static let dictionaryBounds = PixelRect(x: 1475, y: 1260, width: 900, height: 340)
    static let smartBounds = PixelRect(x: 725, y: 1070, width: 2400, height: 720)

    static func frame(_ tag: UInt64, width: Int, height: Int) -> Frame? {
        guard width >= 2, height >= 1 else { return nil }
        var pixels = Data(count: width * height * Frame.bytesPerPixel)
        withUnsafeBytes(of: tag.littleEndian) { pixels.replaceSubrange(0..<8, with: $0) }
        return try? Frame(width: width, height: height, pixels: pixels, pixelsPerPoint: 2)
    }

    static func tag(_ pixels: Data) -> UInt64 {
        var value: UInt64 = 0
        for (index, byte) in pixels.prefix(8).enumerated() { value |= UInt64(byte) << (8 * UInt64(index)) }
        return value
    }

    /// The golden line in frame pixels with «bank» (or `word` in its place)
    /// centred on `point`.
    static func lines(bankAt point: PixelPoint, word: String = "bank") -> [RecognizedLine] {
        let texts = Observed.layout.map { $0.text == "bank" ? word : $0.text }
        var offset = 0
        var words: [RecognizedWord] = []
        for (text, box) in zip(texts, Observed.layout) {
            let length = text.utf16.count
            let left = Double(point.x - 40)
            words.append(RecognizedWord(text: text, utf16Range: offset..<(offset + length), box: PixelBox(
                left: left + Double(box.left), top: Double(point.y - 15), right: left + Double(box.right),
                bottom: Double(point.y + 15))))
            offset += length + 1
        }
        return [RecognizedLine(text: texts.joined(separator: " "), confidence: 1, box: nil, words: words)]
    }

    /// Waits for the calling task's cancellation (at most 10 s) and then
    /// returns normally, as the real capture and Vision do: they are not
    /// interrupted midway, so the pipeline must notice on its own.
    static func untilCancelled(_ reached: Box<Bool>) async {
        reached.set(true)
        let end = ContinuousClock.now + .seconds(10)
        while !Task.isCancelled, ContinuousClock.now < end { try? await Task.sleep(for: .milliseconds(5)) }
    }
}

struct TagFingerprinter: FrameFingerprinting {
    func fingerprint(of pixels: Data) -> FrameFingerprint { FrameFingerprint(value: PipelineFixtures.tag(pixels)) }
}

struct StubPermission: CapturePermission {
    let granted: Bool
    func isGranted() -> Bool { granted }
}

/// Frames of the requested bounds (or one pixel narrower), tagged per call.
struct StubFrames: FrameSource {
    let log: Box<[String]>
    /// Call n's tag; the last one repeats.
    let tags: [UInt64]
    var narrower = false
    /// Thrown instead of a frame.
    var failure: CaptureFailure?
    var hook: @Sendable (Int) async -> Void = { _ in }

    func capture(_ bounds: PixelRect, on display: DisplayDescription) async throws(CaptureFailure) -> Frame {
        var call = 0
        log.mutate {
            $0.append("capture \(bounds.width)x\(bounds.height)")
            call = $0.filter { $0.hasPrefix("capture") }.count
        }
        await hook(call)
        if let failure { throw failure }
        let tag = tags.indices.contains(call - 1) ? tags[call - 1] : tags.last ?? 0
        guard let frame = PipelineFixtures.frame(tag, width: bounds.width - (narrower ? 1 : 0), height: bounds.height) else {
            throw .invalidFrame("the stub cannot make a frame of \(bounds)")
        }
        return frame
    }
}

struct StubRecognizer: TextRecognizer {
    let log: Box<[String]>
    var lines: @Sendable (UInt64, PixelRect) -> [RecognizedLine] = { _, bounds in
        PipelineFixtures.lines(bankAt: PixelPoint(x: PipelineFixtures.cursor.x - bounds.x,
                                                  y: PipelineFixtures.cursor.y - bounds.y))
    }
    var hook: @Sendable (Int) async -> Void = { _ in }
    /// Thrown instead of lines.
    var failure: RecognitionFailure?

    func recognize(_ frame: Frame, capturedAt bounds: PixelRect) async throws(RecognitionFailure) -> [RecognizedLine] {
        let tag = PipelineFixtures.tag(frame.pixels)
        var call = 0
        log.mutate {
            $0.append("recognize \(FrameFingerprint(value: tag).hex)")
            call = $0.filter { $0.hasPrefix("recognize") }.count
        }
        await hook(call)
        if let failure { throw failure }
        return lines(tag, bounds)
    }
}

struct StubGames: GameResolver {
    let log: Box<[String]>
    var game: HoverGame? = HoverGame(key: "path:/Games/Example/game.exe", displayName: "Example")
    var hook: @Sendable () async -> Void = {}

    func game(at point: GlobalPoint) async -> HoverGame? {
        log.mutate { $0.append("game") }
        await hook()
        return game
    }
}

/// What went to CoreHost: each request's command, payload and id (nil while
/// unsent, and for one refused before it took an id).
struct SentRequest: Sendable {
    let command: String
    let payload: JSONValue?
    var id: Int64?
}

/// A CoreRequesting that records every request in the shared log and the
/// sent list before passing it to `core`.
struct RecordingCore: CoreRequesting {
    let core: any CoreRequesting
    let log: Box<[String]>
    let sent: Box<[SentRequest]>

    func request(_ command: String, _ payload: JSONValue?, deadline: Duration, cancelInHost: Bool,
                 maxLineBytes: Int?, onSent: (@Sendable (Int64) -> Void)?,
                 onEvent: CoreConnection.EventHandler?) async throws -> JSONValue {
        var slot = 0
        sent.mutate {
            slot = $0.count
            $0.append(SentRequest(command: command, payload: payload, id: nil))
        }
        log.mutate { $0.append("→ \(command)") }
        let sent = self.sent
        let index = slot
        return try await core.request(command, payload, deadline: deadline, cancelInHost: cancelInHost,
                                      maxLineBytes: maxLineBytes, onSent: { id in
                                          sent.mutate { if $0.indices.contains(index) { $0[index].id = id } }
                                          onSent?(id)
                                      }, onEvent: onEvent)
    }
}

/// A core without a process: `answer` decides each request by its command
/// and its place among all requests (1-based).
actor ScriptedCore: CoreRequesting {
    typealias Answer = @Sendable (String, JSONValue?, Int) async throws -> JSONValue
    private let answer: Answer
    private(set) var commands: [String] = []
    /// Requests whose caller was cancelled while they were in flight: the
    /// cancellation CoreConnection turns into request.cancel.
    private(set) var cancelledInFlight: [String] = []

    init(_ answer: @escaping Answer) { self.answer = answer }

    func request(_ command: String, _ payload: JSONValue?, deadline: Duration, cancelInHost: Bool,
                 maxLineBytes: Int?, onSent: (@Sendable (Int64) -> Void)?,
                 onEvent: CoreConnection.EventHandler?) async throws -> JSONValue {
        commands.append(command)
        let index = commands.count
        onSent?(Int64(index))
        do {
            return try await answer(command, payload, index)
        } catch is CancellationError {
            cancelledInFlight.append(command)
            throw CancellationError()
        }
    }

    static func plan(_ mode: TranslationMode, cached: UInt64? = nil, bounds: PixelRect? = nil) -> JSONValue {
        let bounds = bounds ?? (mode == .smart ? PipelineFixtures.smartBounds : PipelineFixtures.dictionaryBounds)
        var result: [String: JSONValue] = ["mode": .string(mode.rawValue), "captureBounds": bounds.json,
                                           "observations": .integer(mode == .smart ? 2 : 1)]
        if let cached { result["cachedFingerprint"] = .string(FrameFingerprint(value: cached).hex) }
        return .object(result)
    }

    /// Blocks until the calling task is cancelled (at most 10 s).
    static func blockUntilCancelled() async throws -> JSONValue {
        try await Task.sleep(for: .seconds(10))
        return ["outcome": "completed"]
    }
}

/// The pipeline without a process (DoD 20.3 item 7): the permission, the
/// display, the plan's bounds, exactly one retry, one re-plan, and a
/// cancellation inside a request the process-backed tests cannot reach.
@Suite struct HoverPipelineScriptedTests {
    static func pipeline(_ core: any CoreRequesting, log: Box<[String]>, tags: [UInt64] = [0xa1, 0xa2],
                         granted: Bool = true) -> HoverPipeline {
        HoverPipeline(client: ObservationClient(core: core), frames: StubFrames(log: log, tags: tags),
                      recognizer: StubRecognizer(log: log), fingerprinter: TagFingerprinter(),
                      games: StubGames(log: log), permission: StubPermission(granted: granted))
    }

    /// Without Screen Recording nothing is planned, captured or recognized,
    /// and the hover ends with the Russian permission message.
    @Test func withoutThePermissionTheFrameSourceIsNeverCalled() async throws {
        let display = try #require(PipelineFixtures.display)
        let core = ScriptedCore { _, _, _ in ScriptedCore.plan(.dictionary) }
        let log = Box<[String]>([])
        let result = await Self.pipeline(core, log: log, granted: false).run(at: PipelineFixtures.point, on: [display],
                                                                            onUpdate: { _ in })
        guard case .rejected(let error) = result.outcome else {
            Issue.record("expected the permission refusal, got \(result.outcome)")
            return
        }
        #expect(error.code == "screen_capture_denied" && error.message == RU.captureNoPermission)
        #expect(log.get.isEmpty && result.steps.isEmpty, "\(log.get)")
        #expect(await core.commands.isEmpty)
        #expect(HoverReport.after(result.outcome, shownPrimary: false).overlay == .message(RU.captureNoPermission))
    }

    /// The game the resolver names reaches onGame once, before anything is
    /// captured or sent, and goes out as hover.resolve's game; with none,
    /// onGame says so and the hover carries no game (T29).
    @Test func theResolvedGameIsReportedFirstAndSent() async throws {
        let display = try #require(PipelineFixtures.display)
        let named = HoverGame(key: "path:/B/One/drive_c/Games/Novel/Novel.exe", displayName: "Novel.exe")
        for game in [named, nil] as [HoverGame?] {
            let log = Box<[String]>([])
            let payloads = Box<[JSONValue]>([])
            let core = ScriptedCore { command, payload, _ in
                if command == "ocr.plan" { return ScriptedCore.plan(.dictionary) }
                log.mutate { $0.append("→ \(command)") }
                if let payload { payloads.mutate { $0.append(payload) } }
                return ["outcome": "completed"]
            }
            var games = StubGames(log: log)
            games.game = game
            let pipeline = HoverPipeline(client: ObservationClient(core: core), frames: StubFrames(log: log, tags: [0xa1]),
                                         recognizer: StubRecognizer(log: log), fingerprinter: TagFingerprinter(),
                                         games: games, permission: StubPermission(granted: true))
            let result = await pipeline.run(at: PipelineFixtures.point, on: [display],
                                            onGame: { named in log.mutate { $0.append("onGame \(named?.key ?? "none")") } },
                                            onUpdate: { _ in })
            #expect(result.outcome == .completed)
            #expect(log.get == ["game", "onGame \(game?.key ?? "none")", "capture 900x340", "recognize 00000000000000a1",
                                "→ hover.resolve"], "\(log.get)")
            #expect(payloads.get.count == 1 && payloads.get.first?["game"] == game?.json)
        }
    }

    @Test func noDisplayUnderTheCursorIsNoHover() async throws {
        let display = try #require(PipelineFixtures.display)
        let core = ScriptedCore { _, _, _ in ScriptedCore.plan(.dictionary) }
        let log = Box<[String]>([])
        let result = await Self.pipeline(core, log: log).run(at: GlobalPoint(x: -5, y: 365), on: [display],
                                                            onUpdate: { _ in })
        #expect(result.outcome == .empty && log.get.isEmpty && result.steps.isEmpty)
        #expect(await core.commands.isEmpty)
    }

    /// CoreHost keeps its bounds inside the display; bounds that are not
    /// are a bad answer, and nothing is captured for them.
    @Test func planBoundsOutsideTheDisplayAreABadResponse() async throws {
        let display = try #require(PipelineFixtures.display)
        let outside = PixelRect(x: 3500, y: 1260, width: 900, height: 340)
        let core = ScriptedCore { _, _, _ in ScriptedCore.plan(.dictionary, bounds: outside) }
        let log = Box<[String]>([])
        let result = await Self.pipeline(core, log: log).run(at: PipelineFixtures.point, on: [display], onUpdate: { _ in })
        guard case .rejected(let error) = result.outcome else {
            Issue.record("expected bad_response, got \(result.outcome)")
            return
        }
        #expect(error.code == "bad_response" && log.get.isEmpty)
        #expect(await core.commands == ["ocr.plan"])
    }

    /// Exactly one retry: a cached frame that is stale again on the retry
    /// (CoreHost never answers so for full observations, but a stale answer
    /// must not loop) ends the hover silently after two hover.resolve.
    @Test func theStaleFrameRetryHappensExactlyOnce() async throws {
        let display = try #require(PipelineFixtures.display)
        let stale = CoreError(code: "stale_frame", message: RU.errorText("stale_frame"))
        let core = ScriptedCore { command, _, _ in
            if command == "ocr.plan" { return ScriptedCore.plan(.smart, cached: 0xa1) }
            throw stale
        }
        let log = Box<[String]>([])
        let updates = Box<[HoverUpdate]>([])
        let result = await Self.pipeline(core, log: log).run(at: PipelineFixtures.point, on: [display],
                                                            onUpdate: { update in updates.mutate { $0.append(update) } })
        #expect(result.outcome == .staleFrame(stale))
        #expect(await core.commands == ["ocr.plan", "hover.resolve", "hover.resolve"])
        // The retry recognizes the frame already held, then takes frame 2.
        #expect(log.get == ["game", "capture 2400x720", "recognize 00000000000000a1", "capture 2400x720",
                            "recognize 00000000000000a2"])
        #expect(result.steps.contains(.skipRecognition(FrameFingerprint(value: 0xa1))))
        #expect(result.steps.filter { $0 == .retryAfterStaleFrame }.count == 1)
        #expect(updates.get.isEmpty && HoverReport.after(result.outcome, shownPrimary: false).overlay == .hide)
    }

    /// A bad_request in the same mode is returned as it is, after the one
    /// new plan that tells.
    @Test func aBadRequestInTheSameModeIsNotRetried() async throws {
        let display = try #require(PipelineFixtures.display)
        let refused = CoreError(code: "bad_request", message: RU.errorText("bad_request"), detail: "x")
        let core = ScriptedCore { command, _, _ in
            if command == "ocr.plan" { return ScriptedCore.plan(.dictionary) }
            throw refused
        }
        let log = Box<[String]>([])
        let result = await Self.pipeline(core, log: log).run(at: PipelineFixtures.point, on: [display], onUpdate: { _ in })
        #expect(result.outcome == .rejected(refused))
        #expect(await core.commands == ["ocr.plan", "hover.resolve", "ocr.plan"])
        #expect(log.get == ["game", "capture 900x340", "recognize 00000000000000a1"])
    }

    /// A frame source or recognizer that ends on its own cancellation ends
    /// the hover cancelled; one that fails ends it with its Russian text;
    /// either way nothing more runs and no hover is sent.
    @Test func aStepThatFailsOrIsCancelledEndsTheHover() async throws {
        let display = try #require(PipelineFixtures.display)
        let cases: [(frames: CaptureFailure?, recognizer: RecognitionFailure?, code: String?, log: [String])] = [
            (.cancelled, nil, nil, ["game", "capture 900x340"]),
            (.permissionDenied, nil, "screen_capture_denied", ["game", "capture 900x340"]),
            (.ownAppMissing, nil, "capture_self_missing", ["game", "capture 900x340"]),
            (nil, .cancelled, nil, ["game", "capture 900x340", "recognize 00000000000000a1"]),
            (nil, .recognizer(domain: "com.apple.Vision", code: 9), "recognizer_failed",
             ["game", "capture 900x340", "recognize 00000000000000a1"]),
        ]
        for item in cases {
            let core = ScriptedCore { _, _, _ in ScriptedCore.plan(.dictionary) }
            let log = Box<[String]>([])
            var frames = StubFrames(log: log, tags: [0xa1])
            frames.failure = item.frames
            var recognizer = StubRecognizer(log: log)
            recognizer.failure = item.recognizer
            let pipeline = HoverPipeline(client: ObservationClient(core: core), frames: frames, recognizer: recognizer,
                                         fingerprinter: TagFingerprinter(), games: StubGames(log: log),
                                         permission: StubPermission(granted: true))
            let result = await pipeline.run(at: PipelineFixtures.point, on: [display], onUpdate: { _ in })
            if let code = item.code {
                guard case .rejected(let error) = result.outcome else {
                    Issue.record("\(item): expected \(code), got \(result.outcome)")
                    continue
                }
                #expect(error.code == code && error.message.range(of: #"\p{Cyrillic}"#, options: .regularExpression) != nil)
                #expect(HoverReport.after(result.outcome, shownPrimary: false).overlay == .message(error.message))
            } else {
                #expect(result.outcome == .cancelled, "\(item)")
            }
            #expect(log.get == item.log, "\(item)")
            #expect(await core.commands == ["ocr.plan"], "\(item)")
        }
    }

    /// A capture that ends after the cursor left is dropped as cancelled,
    /// whatever it returned: a frame of another size then is no
    /// frame_mismatch to show at the old place.
    @Test func aCaptureThatReturnsAfterCancellationIsDroppedAsCancelled() async throws {
        let display = try #require(PipelineFixtures.display)
        let core = ScriptedCore { _, _, _ in ScriptedCore.plan(.dictionary) }
        let log = Box<[String]>([])
        let reached = Box(false)
        var frames = StubFrames(log: log, tags: [0xa1], narrower: true)
        frames.hook = { call in if call == 1 { await PipelineFixtures.untilCancelled(reached) } }
        let pipeline = HoverPipeline(client: ObservationClient(core: core), frames: frames,
                                     recognizer: StubRecognizer(log: log), fingerprinter: TagFingerprinter(),
                                     games: StubGames(log: log), permission: StubPermission(granted: true))
        let task = Task { await pipeline.run(at: PipelineFixtures.point, on: [display], onUpdate: { _ in }) }
        #expect(await eventually { reached.get })
        task.cancel()
        let result = await task.value
        #expect(result.outcome == .cancelled, "\(result.outcome)")
        #expect(log.get == ["game", "capture 900x340"] && result.steps.last == .capture(1, PipelineFixtures.dictionaryBounds))
        #expect(await core.commands == ["ocr.plan"])
    }

    /// A capture or recognition that fails (not by its own cancellation)
    /// after the cursor left ends the hover cancelled too: its Russian
    /// message would appear at a place the user no longer looks at.
    @Test(arguments: ["capture", "recognize"])
    func aStepThatFailsAfterCancellationEndsCancelled(_ step: String) async throws {
        let display = try #require(PipelineFixtures.display)
        let core = ScriptedCore { _, _, _ in ScriptedCore.plan(.dictionary) }
        let log = Box<[String]>([])
        let reached = Box(false)
        var frames = StubFrames(log: log, tags: [0xa1])
        var recognizer = StubRecognizer(log: log)
        if step == "capture" {
            frames.failure = .failed(domain: "probe", code: 1)
            frames.hook = { call in if call == 1 { await PipelineFixtures.untilCancelled(reached) } }
        } else {
            recognizer.failure = .recognizer(domain: "probe", code: 2)
            recognizer.hook = { call in if call == 1 { await PipelineFixtures.untilCancelled(reached) } }
        }
        let pipeline = HoverPipeline(client: ObservationClient(core: core), frames: frames, recognizer: recognizer,
                                     fingerprinter: TagFingerprinter(), games: StubGames(log: log),
                                     permission: StubPermission(granted: true))
        let task = Task { await pipeline.run(at: PipelineFixtures.point, on: [display], onUpdate: { _ in }) }
        #expect(await eventually { reached.get })
        task.cancel()
        let result = await task.value
        #expect(result.outcome == .cancelled, "\(step): \(result.outcome)")
        #expect(HoverReport.after(result.outcome, shownPrimary: false).overlay == .keep)
        let expected = step == "capture" ? ["game", "capture 900x340"]
            : ["game", "capture 900x340", "recognize 00000000000000a1"]
        #expect(log.get == expected, "\(step)")
        #expect(await core.commands == ["ocr.plan"], "\(step)")
        #expect(await core.cancelledInFlight.isEmpty, "\(step): nothing was in flight")
    }

    /// Cancelled while the retry's hover.resolve or the new plan is in
    /// flight: the request sees the cancellation (CoreConnection sends
    /// request.cancel for it) and nothing follows.
    @Test(arguments: ["retry", "replan"])
    func cancellingTheRetryOrTheNewPlanReachesTheRequest(_ step: String) async throws {
        let display = try #require(PipelineFixtures.display)
        let reached = Box(false)
        let core = ScriptedCore { command, _, index in
            switch (step, command, index) {
            case ("retry", "ocr.plan", _): return ScriptedCore.plan(.smart, cached: 0xa1)
            case ("retry", "hover.resolve", 2):
                throw CoreError(code: "stale_frame", message: RU.errorText("stale_frame"))
            case ("retry", "hover.resolve", _):
                reached.set(true)
                return try await ScriptedCore.blockUntilCancelled()
            case ("replan", "ocr.plan", 1): return ScriptedCore.plan(.dictionary)
            case ("replan", "hover.resolve", _):
                throw CoreError(code: "bad_request", message: RU.errorText("bad_request"))
            default:
                reached.set(true)
                return try await ScriptedCore.blockUntilCancelled()
            }
        }
        let log = Box<[String]>([])
        let pipeline = Self.pipeline(core, log: log)
        let task = Task { await pipeline.run(at: PipelineFixtures.point, on: [display], onUpdate: { _ in }) }
        #expect(await eventually { reached.get })
        task.cancel()
        let result = await task.value
        #expect(result.outcome == .cancelled, "\(step)")
        let expected = step == "retry" ? ["ocr.plan", "hover.resolve", "hover.resolve"]
            : ["ocr.plan", "hover.resolve", "ocr.plan"]
        #expect(await core.commands == expected)
        #expect(await core.cancelledInFlight == [expected[2]])
    }
}

extension ProcessSuites {
    /// HoverPipeline against the fake CoreHost (DoD 20.3 item 7), with stub
    /// frames, a stub recognizer and a stub game: one frame in «Словарь»,
    /// two in «Умный словарь» with the second taken after the first
    /// recognition, Vision skipped on the plan's cached frame, exactly one
    /// retry on stale_frame, one new plan when the mode changes,
    /// request.cancel for a request in flight and nothing more sent after a
    /// cancelled local step, frame_mismatch and the 900 KiB budget.
    @Suite struct HoverPipelineTests {
        struct Harness: Sendable {
            let connection: CoreConnection
            let log: Box<[String]>
            let sent: Box<[SentRequest]>
            let stderr: Box<[String]>
            let display: DisplayDescription

            var core: RecordingCore { RecordingCore(core: connection, log: log, sent: sent) }

            func pipeline(frames: StubFrames? = nil, recognizer: StubRecognizer? = nil, games: StubGames? = nil)
                -> HoverPipeline {
                HoverPipeline(client: ObservationClient(core: core), frames: frames ?? StubFrames(log: log, tags: [0xa1, 0xa2]),
                              recognizer: recognizer ?? StubRecognizer(log: log), fingerprinter: TagFingerprinter(),
                              games: games ?? StubGames(log: log), permission: StubPermission(granted: true))
            }

            func run(_ pipeline: HoverPipeline, updates: Box<[HoverUpdate]> = Box([])) async -> HoverPipeline.Result {
                await pipeline.run(at: PipelineFixtures.point, on: [display],
                                   onUpdate: { update in updates.mutate { $0.append(update) } })
            }

            func setMode(_ mode: String) async throws {
                let current = try await connection.request("settings.get", deadline: .seconds(2))
                let revision = try #require(current["revision"]?.int64Value)
                _ = try await connection.request("settings.update", ["baseRevision": .integer(revision),
                                                                     "patch": ["mode": .string(mode)]],
                                                 deadline: .seconds(5))
            }

            func commands(from start: Int = 0) -> [String] { Array(sent.get.dropFirst(start).map(\.command)) }

            func clear() {
                log.set([])
                sent.set([])
            }
        }

        /// A fake CoreHost after core.init with hover translation on in `mode`;
        /// the connection is shut down (or killed on a failure) at the end.
        static func withFake(_ label: String, mode: String,
                             _ body: (Harness) async throws -> Void) async throws {
            let scratch = try Scratch(label)
            defer { scratch.remove() }
            let display = try #require(PipelineFixtures.display)
            let stderr = Box<[String]>([])
            let connection = try await Fake.connect(scratch, callbacks: CoreConnection.Callbacks(
                onStderrLine: { line in stderr.mutate { $0.append(line) } }))
            do {
                _ = try await Fake.hello(connection)
                try await ProcessSuites.ObservationTests.initialize(connection, mode: mode)
                try await body(Harness(connection: connection, log: Box([]), sent: Box([]), stderr: stderr, display: display))
            } catch {
                await connection.close(.killed)
                throw error
            }
            #expect(await connection.shutdown() == .exited(0))
            #expect(await invariantHolds(connection))
        }

        static func cards(_ updates: [HoverUpdate]) -> [String] {
            updates.map { update in
                switch update {
                case .loading(let loading): return "loading \(loading.source)"
                case .primary(let primary): return "primary \(primary.source)"
                case .phrase(let phrase): return "phrase \(phrase.source)"
                }
            }
        }

        static func observationCount(_ request: SentRequest?) -> Int? {
            request?.payload?["source"]?["observations"]?.arrayValue?.count
        }

        @Test func dictionaryModeTakesOneFrame() async throws {
            try await Self.withFake("pipeline-dictionary", mode: "dictionary") { harness in
                let updates = Box<[HoverUpdate]>([])
                let result = await harness.run(harness.pipeline(), updates: updates)
                #expect(result.outcome == .completed)
                #expect(harness.log.get == ["→ ocr.plan", "game", "capture 900x340", "recognize 00000000000000a1",
                                            "→ hover.resolve"])
                #expect(Self.cards(updates.get) == ["loading \(Observed.sentence)", "primary bank"])
                let hover = harness.sent.get.last
                #expect(hover?.command == "hover.resolve" && hover?.id != nil && Self.observationCount(hover) == 1)
                #expect(hover?.payload?["mode"]?.stringValue == "dictionary")
                #expect(hover?.payload?["source"]?["observations"]?.arrayValue?.first?["fingerprint"]?.stringValue
                        == "00000000000000a1")
                #expect(hover?.payload?["source"]?["captureBounds"] == PipelineFixtures.dictionaryBounds.json)
                #expect(hover?.payload?["source"]?["cursor"] == PipelineFixtures.cursor.json)
                #expect(hover?.payload?["game"]?["key"]?.stringValue == "path:/Games/Example/game.exe")
                #expect(result.steps == [
                    .plan(CapturePlan(mode: .dictionary, captureBounds: PipelineFixtures.dictionaryBounds, observations: 1,
                                      cachedFingerprint: nil)),
                    .game("path:/Games/Example/game.exe"), .capture(1, PipelineFixtures.dictionaryBounds),
                    .recognize(1, lines: 1), .resolve(.dictionary, observations: 1, cachedFingerprint: nil),
                ])
            }
        }

        /// Two frames, the second captured only after the first was
        /// recognized; then the plan names the cached frame and a capture
        /// with its fingerprint skips Vision.
        @Test func smartModeTakesTwoFramesThenSkipsVisionOnTheCachedOne() async throws {
            try await Self.withFake("pipeline-smart", mode: "smart") { harness in
                let updates = Box<[HoverUpdate]>([])
                let first = await harness.run(harness.pipeline(), updates: updates)
                #expect(first.outcome == .completed)
                #expect(harness.log.get == ["→ ocr.plan", "game", "capture 2400x720", "recognize 00000000000000a1",
                                            "capture 2400x720", "recognize 00000000000000a2", "→ hover.resolve"])
                let hover = harness.sent.get.last
                #expect(Self.observationCount(hover) == 2)
                #expect(hover?.payload?["source"]?["observations"]?.arrayValue?.map { $0["fingerprint"]?.stringValue }
                        == ["00000000000000a1", "00000000000000a2"])
                #expect(Self.cards(updates.get) == ["loading bank", "primary bank"])

                // The two agreeing observations were cached (the first, as
                // Core chooses for equal texts): frame a1 again needs no Vision.
                harness.clear()
                let again = Box<[HoverUpdate]>([])
                let second = await harness.run(harness.pipeline(frames: StubFrames(log: harness.log, tags: [0xa1])),
                                               updates: again)
                #expect(second.outcome == .completed)
                #expect(harness.log.get == ["→ ocr.plan", "game", "capture 2400x720", "→ hover.resolve"])
                #expect(second.steps.contains(.skipRecognition(FrameFingerprint(value: 0xa1))))
                let cached = harness.sent.get.last
                #expect(Self.observationCount(cached) == 0)
                #expect(cached?.payload?["source"]?["cachedFingerprint"]?.stringValue == "00000000000000a1")
                #expect(Self.cards(again.get) == ["loading bank", "primary bank"])

                // Another frame there is recognized twice as usual.
                harness.clear()
                let changed = await harness.run(harness.pipeline(frames: StubFrames(log: harness.log, tags: [0xb1, 0xb2])))
                #expect(changed.outcome == .completed)
                #expect(harness.log.get.filter { $0.hasPrefix("recognize") } == ["recognize 00000000000000b1",
                                                                                 "recognize 00000000000000b2"])
            }
        }

        /// The cached frame is replaced between the plan and the hover:
        /// stale_frame, shown nowhere, then exactly one retry with full
        /// observations of frame 1 (already held) and a new frame 2.
        @Test func aStaleCachedFrameIsRetriedOnceWithFullObservations() async throws {
            try await Self.withFake("pipeline-stale", mode: "smart") { harness in
                #expect(await harness.run(harness.pipeline()).outcome == .completed)
                harness.clear()
                let connection = harness.connection
                // During capture 1, another display's stable hover takes the
                // fake's one cache entry, as CoreHost's single entry would go.
                let frames = StubFrames(log: harness.log, tags: [0xa1, 0xc2], hook: { call in
                    guard call == 1 else { return }
                    let client = ObservationClient(core: connection)
                    let local = (x: 1200, y: 360)
                    let elsewhere = ObservationsSource(
                        display: ObservedDisplay(id: 8, width: 3840, height: 2160), captureBounds: PipelineFixtures.smartBounds,
                        cursor: PipelineFixtures.cursor, observations: [
                            ProcessSuites.ObservationClientTests.frame(0xb1, bankAt: local),
                            ProcessSuites.ObservationClientTests.frame(0xb1, bankAt: local),
                        ])
                    _ = await client.resolve(elsewhere, mode: .smart, onUpdate: { _ in })
                })
                let updates = Box<[HoverUpdate]>([])
                let result = await harness.run(harness.pipeline(frames: frames), updates: updates)
                #expect(result.outcome == .completed)
                #expect(harness.commands() == ["ocr.plan", "hover.resolve", "hover.resolve"])
                let resolves = harness.sent.get.filter { $0.command == "hover.resolve" }
                try #require(resolves.count == 2)
                #expect(Self.observationCount(resolves[0]) == 0 && Self.observationCount(resolves[1]) == 2)
                #expect(resolves[1].payload?["source"]?["observations"]?.arrayValue?.map { $0["fingerprint"]?.stringValue }
                        == ["00000000000000a1", "00000000000000c2"])
                #expect(harness.log.get == ["→ ocr.plan", "game", "capture 2400x720", "→ hover.resolve",
                                            "recognize 00000000000000a1", "capture 2400x720", "recognize 00000000000000c2",
                                            "→ hover.resolve"])
                #expect(result.steps.filter { $0 == .retryAfterStaleFrame }.count == 1)
                #expect(Self.cards(updates.get) == ["loading bank", "primary bank"], "nothing for the stale answer")
            }
        }

        /// The mode changes between the plan and the hover: CoreHost refuses
        /// the hover (bad_request), one new plan names the new mode, and the
        /// hover runs once more in it. A second change is not chased.
        @Test func aModeChangeIsReplannedOnce() async throws {
            try await Self.withFake("pipeline-mode", mode: "dictionary") { harness in
                let frames = StubFrames(log: harness.log, tags: [0xd1, 0xd2, 0xd3], hook: { call in
                    if call == 1 { try? await harness.setMode("smart") }
                })
                let updates = Box<[HoverUpdate]>([])
                let result = await harness.run(harness.pipeline(frames: frames), updates: updates)
                #expect(result.outcome == .completed)
                #expect(harness.commands() == ["ocr.plan", "hover.resolve", "ocr.plan", "hover.resolve"])
                #expect(result.steps.contains(.replan(CapturePlan(mode: .smart, captureBounds: PipelineFixtures.smartBounds,
                                                                  observations: 2, cachedFingerprint: nil))))
                let last = harness.sent.get.last
                #expect(last?.payload?["mode"]?.stringValue == "smart" && Self.observationCount(last) == 2)
                #expect(harness.log.get.filter { $0.hasPrefix("capture") } == ["capture 900x340", "capture 2400x720",
                                                                               "capture 2400x720"])
                #expect(Self.cards(updates.get) == ["loading bank", "primary bank"])

                // Changed again during the second round: the refusal stands.
                try await harness.setMode("dictionary")
                harness.clear()
                let flipping = StubFrames(log: harness.log, tags: [0xe1], hook: { call in
                    if call == 1 { try? await harness.setMode("smart") }
                    if call == 2 { try? await harness.setMode("dictionary") }
                })
                let twice = await harness.run(harness.pipeline(frames: flipping))
                guard case .rejected(let error) = twice.outcome else {
                    Issue.record("expected the second refusal, got \(twice.outcome)")
                    return
                }
                #expect(error.code == "bad_request")
                #expect(harness.commands() == ["ocr.plan", "hover.resolve", "ocr.plan", "hover.resolve"])
            }
        }

        /// A frame that is not the plan's size ends the hover before Vision.
        @Test func aFrameOfAnotherSizeIsFrameMismatch() async throws {
            try await Self.withFake("pipeline-mismatch", mode: "dictionary") { harness in
                let frames = StubFrames(log: harness.log, tags: [0xa1], narrower: true)
                let result = await harness.run(harness.pipeline(frames: frames))
                guard case .rejected(let error) = result.outcome else {
                    Issue.record("expected frame_mismatch, got \(result.outcome)")
                    return
                }
                #expect(error.code == "frame_mismatch" && error.message == RU.recognitionFrameMismatch)
                #expect(error.detail?.contains("899x340") == true && error.detail?.contains("900x340") == true)
                #expect(harness.log.get == ["→ ocr.plan", "game", "capture 900x340"])
                #expect(HoverReport.after(result.outcome, shownPrimary: false).overlay == .message(RU.recognitionFrameMismatch))
            }
        }

        /// Observations over the 900 KiB budget are refused before sending,
        /// never cut short.
        @Test func observationsOverTheBudgetAreRefused() async throws {
            try await Self.withFake("pipeline-budget", mode: "dictionary") { harness in
                let box = PixelBox(left: 400, top: 150, right: 440, bottom: 180)
                let recognizer = StubRecognizer(log: harness.log, lines: { _, _ in
                    (0..<240).map { _ in
                        RecognizedLine(text: String(repeating: "a", count: 3900), confidence: 1, box: nil,
                                       words: [RecognizedWord(text: "a", utf16Range: 0..<1, box: box)])
                    }
                })
                let before = await harness.connection.counters.sent
                let result = await harness.run(harness.pipeline(recognizer: recognizer))
                guard case .rejected(let error) = result.outcome else {
                    Issue.record("expected observations_too_large, got \(result.outcome)")
                    return
                }
                #expect(error.code == "observations_too_large"
                        && error.message == "Слишком много текста в области распознавания.")
                let after = await harness.connection.counters.sent
                #expect(after == before + 1, "only ocr.plan was written")
                let hover = harness.sent.get.last
                #expect(hover?.command == "hover.resolve" && hover?.id == nil, "refused before it took an id")
            }
        }

        /// Cancelled at each step: a request in flight (ocr.plan, held back by
        /// test.delayNextWork; hover.resolve, held by the fake's "slow" card)
        /// gets request.cancel and its terminal is drained; a local step
        /// (the game, a capture, a recognition) runs to its end, and nothing
        /// more is sent.
        @Test(arguments: ["plan", "game", "capture1", "recognize1", "capture2", "recognize2", "resolve"])
        func cancellingAtAStepStopsTheHover(_ step: String) async throws {
            try await Self.withFake("pipeline-cancel-\(step)", mode: "smart") { harness in
                let reached = Box(false)
                var frames = StubFrames(log: harness.log, tags: [0xa1, 0xa2])
                var recognizer = StubRecognizer(log: harness.log)
                var games = StubGames(log: harness.log)
                switch step {
                case "plan":
                    _ = try await harness.connection.request("test.delayNextWork", ["ms": 5000], deadline: .seconds(2))
                case "game":
                    games.hook = { await PipelineFixtures.untilCancelled(reached) }
                case "capture1", "capture2":
                    let target = step == "capture1" ? 1 : 2
                    frames.hook = { call in if call == target { await PipelineFixtures.untilCancelled(reached) } }
                case "recognize1", "recognize2":
                    let target = step == "recognize1" ? 1 : 2
                    recognizer.hook = { call in if call == target { await PipelineFixtures.untilCancelled(reached) } }
                default:
                    recognizer.lines = { _, bounds in
                        PipelineFixtures.lines(bankAt: PixelPoint(x: PipelineFixtures.cursor.x - bounds.x,
                                                                  y: PipelineFixtures.cursor.y - bounds.y), word: "slow")
                    }
                }
                let pipeline = harness.pipeline(frames: frames, recognizer: recognizer, games: games)
                let updates = Box<[HoverUpdate]>([])
                let task = Task { await harness.run(pipeline, updates: updates) }
                let inFlight = ["plan": "ocr.plan", "resolve": "hover.resolve"][step]
                #expect(await eventually {
                    if let inFlight { return harness.sent.get.contains { $0.command == inFlight && $0.id != nil } }
                    return reached.get
                }, "\(step) reached")
                // A moment inside the request, so the cancel is not a race with its writing.
                try await Task.sleep(for: .milliseconds(50))
                let logBefore = harness.log.get
                task.cancel()
                let result = await task.value
                #expect(result.outcome == .cancelled, "\(step): \(result.outcome)")
                #expect(updates.get.isEmpty, "\(step): nothing shown")
                if let inFlight {
                    let id = try #require(harness.sent.get.last { $0.command == inFlight }?.id)
                    #expect(harness.commands().last == inFlight, "\(step): nothing sent after it")
                    #expect(await eventually { harness.stderr.get.contains("[fake] request.cancel target \(id): cancelled") },
                            "\(step): \(harness.stderr.get)")
                    #expect(await eventually { await harness.connection.counters.drainedAbandoned == 1 })
                } else {
                    #expect(harness.log.get == logBefore, "\(step): nothing after the cancelled step: \(harness.log.get)")
                    #expect(!harness.commands().contains("hover.resolve"), "\(step)")
                    #expect(!harness.stderr.get.contains { $0.contains("request.cancel") }, "\(step): nothing was in flight")
                }
            }
        }
    }
}
