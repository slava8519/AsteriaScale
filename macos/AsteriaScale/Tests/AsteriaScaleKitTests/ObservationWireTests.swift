import Foundation
import Testing
@testable import AsteriaScaleKit

/// The typed observations as protocol v1 carries them: the golden lines,
/// CoreHost's limits and the 900 KiB budget, without a process.
@Suite struct ObservationWireTests {
    /// The golden transcript's observation (requests #5 and #9): one line,
    /// eight word boxes on whole pixels, confidence 1.
    static let goldenWords: [(String, Double, Double)] = [
        ("He", 100, 140), ("sat", 160, 220), ("on", 240, 280), ("the", 300, 360),
        ("bank", 380, 460), ("of", 480, 520), ("the", 540, 600), ("river.", 620, 740),
    ]

    static func goldenSource(text: String = "He sat on the bank of the river.",
                             words: [(String, Double, Double)] = goldenWords) throws -> ObservationsSource {
        let fingerprint = try #require(FrameFingerprint(hex: "0123456789abcdef"))
        let line = ObservedLine(text: text, confidence: 1, words: words.map {
            ObservedWord(text: $0.0, box: PixelBox(left: $0.1, top: 150, right: $0.2, bottom: 180))
        })
        return ObservationsSource(display: ObservedDisplay(id: 1, width: 1200, height: 400),
                                  captureBounds: PixelRect(x: 0, y: 0, width: 1200, height: 400),
                                  cursor: PixelPoint(x: 420, y: 165),
                                  observations: [ObservationFrame(fingerprint: fingerprint, lines: [line])])
    }

    static func goldenRequest(_ id: Int64) throws -> JSONValue {
        for line in try GoldenTranscriptTests.lines() {
            let frame = try StrictJSON.parse(line)
            if frame["id"] == .integer(id), frame["command"] != nil { return frame }
        }
        throw POSIXError(.ENOENT)
    }

    /// The typed encoders write the golden requests' payloads (keys sorted,
    /// values equal, whole pixels as integer literals) under the golden envelope.
    @Test func typedRequestsEncodeAsTheGoldenLines() throws {
        let select = try CoreRequestLine(id: 5, command: "ocr.select", payload: ObservationClient.selectPayload(
            mode: .dictionary, source: Self.goldenSource())).encodedLine()
        #expect(try StrictJSON.parse(select.dropLast()) == Self.goldenRequest(5))
        #expect(String(decoding: select, as: UTF8.self).hasPrefix(#"{"version":1,"id":5,"command":"ocr.select","payload":"#))

        let hover = try CoreRequestLine(id: 9, command: "hover.resolve", payload: ObservationClient.hoverPayload(
            Self.goldenSource(text: "bank", words: [("bank", 380, 460)]), mode: .dictionary, game: nil)).encodedLine()
        #expect(try StrictJSON.parse(hover.dropLast()) == Self.goldenRequest(9))

        // Whole values are integer literals, as System.Text.Json writes them;
        // fractions and exponents stay numbers CoreHost reads as doubles.
        let text = String(decoding: select, as: UTF8.self)
        #expect(text.contains(#""confidence":1,"#) && text.contains(#""left":380,"#) && !text.contains("380.0"))
        #expect(JSONValue.wireNumber(380.5) == .number(380.5) && JSONValue.wireNumber(-0.0) == .integer(0))
        #expect(JSONValue.wireNumber(1e-5) == .number(1e-5))
        let tiny = try JSONValue.wireNumber(1e-5).serialized()
        #expect(try StrictJSON.parse(tiny) == .number(1e-5))
        #expect(JSONValue.wireNumber(9_007_199_254_740_994) == .number(9_007_199_254_740_994))
    }

    @Test func planAndHoverPayloadsAndTheCachedFingerprint() throws {
        let plan = ObservationClient.planPayload(display: ObservedDisplay(id: 69_733_378, width: 3840, height: 2160),
                                                 cursor: PixelPoint(x: 1925, y: 1430))
        #expect(plan == ["display": ["id": 69_733_378, "width": 3840, "height": 2160], "cursor": ["x": 1925, "y": 1430]])

        let cached = try #require(FrameFingerprint(hex: "9f3c0a1b2c3d4e5f"))
        let empty = ObservationsSource(display: ObservedDisplay(id: 7, width: 3840, height: 2160),
                                       captureBounds: PixelRect(x: 725, y: 1070, width: 2400, height: 720),
                                       cursor: PixelPoint(x: 1925, y: 1430), observations: [], cachedFingerprint: cached)
        let payload = ObservationClient.hoverPayload(empty, mode: .smart,
                                                     game: HoverGame(key: "path:/Games/Novel.exe", displayName: "Novel.exe"))
        #expect(payload["mode"] == "smart" && payload["source"]?["kind"] == "observations")
        #expect(payload["source"]?["observations"] == [] && payload["source"]?["cachedFingerprint"] == "9f3c0a1b2c3d4e5f")
        #expect(payload["source"]?["captureBounds"] == ["x": 725, "y": 1070, "width": 2400, "height": 720])
        #expect(payload["game"] == ["key": "path:/Games/Novel.exe", "displayName": "Novel.exe"])
        #expect(try Self.goldenSource().json["cachedFingerprint"] == nil, "only when set")
    }

    @Test func answersDecodeStrictly() throws {
        let plan = try #require(CapturePlan(result: ["mode": "smart", "captureBounds": ["x": 725, "y": 1070, "width": 2400,
                                                                                          "height": 720],
                                                     "observations": 2, "cachedFingerprint": "9f3c0a1b2c3d4e5f"]))
        #expect(plan.mode == .smart && plan.captureBounds == PixelRect(x: 725, y: 1070, width: 2400, height: 720))
        #expect(plan.observations == 2 && plan.cachedFingerprint?.hex == "9f3c0a1b2c3d4e5f")
        let bounds: JSONValue = ["x": 0, "y": 0, "width": 900, "height": 340]
        for bad: JSONValue in [
            ["mode": "other", "captureBounds": bounds, "observations": 1],
            ["mode": "dictionary", "captureBounds": ["x": 0, "y": 0, "width": 900.5, "height": 340], "observations": 1],
            ["mode": "dictionary", "captureBounds": bounds, "observations": 3],
            ["mode": "dictionary", "captureBounds": bounds, "observations": 1, "cachedFingerprint": "9F3C0A1B2C3D4E5F"],
            ["mode": "dictionary", "observations": 1],
        ] {
            #expect(CapturePlan(result: bad) == nil, "\(bad)")
        }

        let chosen = try #require(OcrSelectResult(result: [
            "selection": ["text": "He sat.", "hoveredWord": "sat", "characterIndex": 4, "sentenceStable": false,
                          "speakerName": "Sakuya"],
            "lines": ["He sat."],
        ]))
        #expect(chosen.selection == OcrSelection(text: "He sat.", hoveredWord: "sat", characterIndex: 4, sentenceStable: false,
                                                 speakerName: "Sakuya"))
        #expect(OcrSelectResult(result: ["selection": nil, "lines": []]) == OcrSelectResult(selection: nil, lines: []))
        #expect(OcrSelectResult(result: ["lines": []]) == nil, "selection is written as null, never left out")
        #expect(OcrSelectResult(result: ["selection": ["text": "x"], "lines": []]) == nil)
    }

    /// CoreHost's per-request limits (ObservationRules), refused before sending.
    @Test func limitsMirrorCoreHost() throws {
        #expect(ObservationLimits.problem(try Self.goldenSource()) == nil)
        func line(_ text: String, words: Int, wordText: String = "w") -> ObservedLine {
            ObservedLine(text: text, confidence: 1, words: Array(repeating: ObservedWord(
                text: wordText, box: PixelBox(left: 0, top: 0, right: 1, bottom: 1)), count: words))
        }
        func source(_ lines: [ObservedLine]) -> ObservationsSource {
            ObservationsSource(display: ObservedDisplay(id: 1, width: 10, height: 10),
                               captureBounds: PixelRect(x: 0, y: 0, width: 10, height: 10), cursor: PixelPoint(x: 1, y: 1),
                               observations: [ObservationFrame(fingerprint: FrameFingerprint(value: 1), lines: lines)])
        }
        let atLimits = [line(String(repeating: "a", count: 4000), words: 256, wordText: String(repeating: "b", count: 256))]
            + Array(repeating: line("x", words: 15), count: 255)
        #expect(ObservationLimits.problem(source(atLimits)) == nil, "256 lines, 4000 UTF-16, 256 words of 256, 4081 words")
        let over: [(lines: [ObservedLine], comment: Comment)] = [
            (Array(repeating: line("x", words: 1), count: 257), "257 lines"),
            ([line(String(repeating: "😀", count: 2001), words: 1)], "4002 UTF-16 units in a line"),
            ([line("x", words: 0)], "a line without words"),
            ([line("x", words: 257)], "257 words in a line"),
            (Array(repeating: line("x", words: 256), count: 16) + [line("x", words: 1)], "4097 words"),
            (Array(repeating: line("x", words: 256), count: 15) + [line("x", words: 255), line("x", words: 2)],
             "4097 words over 17 lines"),
            ([line("x", words: 1, wordText: String(repeating: "b", count: 257))], "a word of 257 units"),
            ([line("x", words: 1, wordText: "")], "an empty word"),
        ]
        for item in over {
            #expect(ObservationLimits.problem(source(item.lines)) != nil, item.comment)
        }
        // CoreHost refuses more than 4096 words, so exactly 4096 go.
        #expect(ObservationLimits.problem(source(Array(repeating: line("x", words: 256), count: 16))) == nil,
                "exactly 4096 words")
        #expect(ObservationBudget.maxLineBytes == 921_600 && ObservationBudget.maxLineBytes < CoreProtocol.maxRequestFrameBytes)
    }

    /// A canned answer for every request, no process: what the typed client
    /// sends (each command's line limit) and what it makes of an answer.
    actor CannedCore: CoreRequesting {
        let answer: JSONValue
        private(set) var sent: [String] = []
        private(set) var limits: [Int?] = []

        init(_ answer: JSONValue) { self.answer = answer }

        func request(_ command: String, _ payload: JSONValue?, deadline: Duration, cancelInHost: Bool,
                     maxLineBytes: Int?, onSent: (@Sendable (Int64) -> Void)?,
                     onEvent: CoreConnection.EventHandler?) async throws -> JSONValue {
            sent.append(command)
            limits.append(maxLineBytes)
            return answer
        }
    }

    /// An answer of another shape is bad_response with the Russian message,
    /// never an empty selection or a made-up plan; every request that carries
    /// observations goes out under the 900 KiB budget, ocr.plan under
    /// CoreHost's frame limit alone.
    @Test func answersOfAnotherShapeAreBadResponses() async throws {
        let core = CannedCore(["mode": "other", "lines": 1])
        let client = ObservationClient(core: core)
        do {
            let plan = try await client.plan(display: ObservedDisplay(id: 1, width: 1200, height: 400),
                                             cursor: PixelPoint(x: 1, y: 1))
            Issue.record("ocr.plan accepted an answer of another shape: \(plan)")
        } catch let error as CoreError {
            #expect(error.code == "bad_response" && error.message == "Служба перевода ответила в неверном формате.")
            #expect(error.detail?.hasPrefix("ocr.plan answered ") == true, "\(String(describing: error.detail))")
        }
        do {
            let selection = try await client.select(mode: .dictionary, source: try Self.goldenSource())
            Issue.record("ocr.select accepted an answer of another shape: \(selection)")
        } catch let error as CoreError {
            #expect(error.code == "bad_response" && error.message == "Служба перевода ответила в неверном формате.")
            #expect(error.detail?.hasPrefix("ocr.select answered ") == true, "\(String(describing: error.detail))")
        }
        let outcome = await client.resolve(try Self.goldenSource(), mode: .dictionary, onUpdate: { _ in })
        guard case .rejected(let error) = outcome else {
            Issue.record("hover.resolve without an outcome ended \(outcome)")
            return
        }
        #expect(error.code == "bad_response")
        #expect(await core.sent == ["ocr.plan", "ocr.select", "hover.resolve"])
        #expect(await core.limits == [nil, ObservationBudget.maxLineBytes, ObservationBudget.maxLineBytes])
    }

    @Test func hoverEndsArePresented() {
        let stale = CoreError(code: "stale_frame", message: "Кадр на экране изменился.", detail: "no cached frame")
        #expect(HoverOutcome(error: stale) == .staleFrame(stale))
        let report = HoverReport.after(.staleFrame(stale), shownPrimary: false)
        #expect(report.overlay == .hide, "the frame changed: nothing on the overlay, the hover retries")
        #expect(report.diagnosticNote == "Кадр на экране изменился. (stale_frame)")
        let large = CoreError.observationsTooLarge("hover.resolve", bytes: 921_601, budget: 921_600)
        #expect(large.code == "observations_too_large" && large.message == "Слишком много текста в области распознавания.")
        #expect(large.detail == "hover.resolve would be a 921601-byte line, over the 921600-byte observations budget")
        #expect(HoverReport.after(.rejected(large), shownPrimary: false).overlay == .message(large.message))
        #expect(HoverOutcome(error: CancellationError()) == .cancelled)
        #expect(HoverOutcome(result: ["outcome": "completed"]) == .completed)
    }
}
