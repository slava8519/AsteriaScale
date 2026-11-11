import Foundation
import Testing
@testable import AsteriaScaleKit

/// Apple Vision observations as protocol v1 carries them (20.3), built the
/// way the app will send them: display pixels for the display, the cursor
/// and the capture, frame pixels for the word boxes.
enum Observed {
    static let sentence = "He sat on the bank of the river."
    static let fingerprintA = "00000000000000a1"
    static let fingerprintB = "00000000000000b2"
    static let fingerprintC = "00000000000000c3"
    static let fingerprintD = "00000000000000d4"
    static let fingerprintE = "00000000000000e5"

    /// Each word's left and right edge relative to the left edge of "bank".
    static let layout: [(text: String, left: Int64, right: Int64)] = [
        ("He", -280, -240), ("sat", -220, -160), ("on", -140, -100), ("the", -80, -20),
        ("bank", 0, 80), ("of", 100, 140), ("the", 160, 220), ("river.", 240, 360),
    ]

    /// One observation of one line whose "bank" lies under `point` (frame
    /// pixels). The word boxes are the layout's; `words` puts other texts on
    /// its first boxes instead (fewer words, fewer boxes).
    static func frame(_ fingerprint: String, text: String = sentence, words: [String]? = nil,
                      bankAt point: (x: Int64, y: Int64)) -> JSONValue {
        let left = point.x - 40
        let boxes: [JSONValue] = zip(words ?? layout.map(\.text), layout).map { word, box in
            ["text": .string(word), "left": .integer(left + box.left), "top": .integer(point.y - 15),
             "right": .integer(left + box.right), "bottom": .integer(point.y + 15)]
        }
        return ["fingerprint": .string(fingerprint),
                "lines": [["text": .string(text), "confidence": 1, "words": .array(boxes)]]]
    }

    /// A line whose boxes hold its own words (at most eight; "bank" must be the fifth).
    static func line(_ fingerprint: String, _ text: String, bankAt point: (x: Int64, y: Int64)) -> JSONValue {
        frame(fingerprint, text: text, words: text.split(separator: " ").map(String.init), bankAt: point)
    }

    static func display(_ id: Int64 = 7) -> JSONValue { ["id": .integer(id), "width": 3840, "height": 2160] }

    static func point(_ point: (x: Int64, y: Int64)) -> JSONValue { ["x": .integer(point.x), "y": .integer(point.y)] }

    static func source(display id: Int64 = 7, bounds: PixelBounds, cursor: (x: Int64, y: Int64), frames: [JSONValue],
                       cached: String? = nil) -> JSONValue {
        var source: [String: JSONValue] = [
            "kind": "observations", "display": display(id), "captureBounds": bounds.json, "cursor": point(cursor),
            "observations": .array(frames),
        ]
        if let cached { source["cachedFingerprint"] = .string(cached) }
        return .object(source)
    }

    static func plan(display id: Int64 = 7, cursor: (x: Int64, y: Int64)) -> JSONValue {
        ["display": display(id), "cursor": point(cursor)]
    }
}

/// captureBounds on the wire: whole display pixels.
struct PixelBounds: Equatable, CustomStringConvertible {
    let x: Int64
    let y: Int64
    let width: Int64
    let height: Int64

    init(x: Int64, y: Int64, width: Int64, height: Int64) {
        self.x = x
        self.y = y
        self.width = width
        self.height = height
    }

    init?(_ value: JSONValue?) {
        guard let x = value?["x"]?.int64Value, let y = value?["y"]?.int64Value,
              let width = value?["width"]?.int64Value, let height = value?["height"]?.int64Value else { return nil }
        self.init(x: x, y: y, width: width, height: height)
    }

    var json: JSONValue { ["x": .integer(x), "y": .integer(y), "width": .integer(width), "height": .integer(height)] }
    var description: String { "(\(x), \(y), \(width)x\(height))" }

    /// The cursor in the captured frame's pixels.
    func local(_ cursor: (x: Int64, y: Int64)) -> (x: Int64, y: Int64) { (cursor.x - x, cursor.y - y) }
}

extension ProcessSuites {
    /// ocr.select, ocr.plan, the observations hover, hover.loading and
    /// stale_frame against the fake CoreHost, through the plain connection
    /// (ObservationClientTests pins the typed client on top). The fake mirrors
    /// CoreHost's shapes, refusals, Russian messages and labels, its line
    /// and word choice, its agreement rule and which observation it caches:
    /// when they were written, the observation requests here got the same
    /// word, stability, cached fingerprint, events, labels and provenance
    /// from the real CoreHost. Core's own geometry, sentence assembly,
    /// speakers and frame cache are pinned by the C# CoreHost checks.
    @Suite struct ObservationTests {
        /// What one request produced: its events in order, then its terminal.
        struct Answer: Sendable {
            let events: [CoreEvent]
            let result: Result<JSONValue, CoreError>

            var names: [String] { events.map(\.name) }
            var value: JSONValue? { try? result.get() }
            var error: CoreError? {
                if case .failure(let error) = result { return error }
                return nil
            }
        }

        static func send(_ connection: CoreConnection, _ command: String, _ payload: JSONValue) async -> Answer {
            let events = Box<[CoreEvent]>([])
            do {
                let result = try await connection.request(command, payload, deadline: .seconds(5), onEvent: { event in
                    events.mutate { $0.append(event) }
                })
                return Answer(events: events.get, result: .success(result))
            } catch let error as CoreError {
                return Answer(events: events.get, result: .failure(error))
            } catch {
                return Answer(events: events.get, result: .failure(CoreError(code: "unexpected", message: "\(error)")))
            }
        }

        /// core.init, then hover translation on in `mode`.
        static func initialize(_ connection: CoreConnection, mode: String) async throws {
            let initial = try await connection.request("core.init", [:], deadline: .seconds(5))
            let revision = try #require(initial["revision"]?.int64Value)
            let plan = await send(connection, "ocr.plan", Observed.plan(cursor: (1925, 1430)))
            #expect(plan.error?.code == "hover_disabled", "ocr.plan while hover translation is off")
            _ = try await connection.request("settings.update",
                                             ["baseRevision": .integer(revision),
                                              "patch": ["hoverEnabled": true, "mode": .string(mode)]],
                                             deadline: .seconds(5))
        }

        @Test func ocrSelectNeedsNoInitButPlanAndHoverDo() async throws {
            let scratch = try Scratch("observe-select")
            defer { scratch.remove() }
            let connection = try await Fake.connect(scratch)
            let hello = try await Fake.hello(connection)
            #expect(hello["capabilities"]?["hoverSources"] == ["selection", "observations"])

            let bounds = PixelBounds(x: 0, y: 0, width: 1200, height: 400)
            let cursor: (x: Int64, y: Int64) = (420, 165)
            let one = Observed.source(bounds: bounds, cursor: cursor,
                                      frames: [Observed.frame("0123456789abcdef", bankAt: cursor)])
            let dictionary = await Self.send(connection, "ocr.select", ["mode": "dictionary", "source": one])
            let selection = try #require(dictionary.value?["selection"])
            #expect(selection["text"] == .string(Observed.sentence) && selection["hoveredWord"] == "bank")
            #expect(selection["sentenceStable"] == true && selection["speakerName"] == nil)
            #expect(dictionary.value?["lines"] == [.string(Observed.sentence)] && dictionary.events.isEmpty)

            // Smart mode: one observation against none is never stable, two that agree are.
            let lone = await Self.send(connection, "ocr.select", ["mode": "smart", "source": one])
            #expect(lone.value?["selection"]?["sentenceStable"] == false)
            let pair = Observed.source(bounds: bounds, cursor: cursor, frames: [
                Observed.frame(Observed.fingerprintA, bankAt: cursor), Observed.frame(Observed.fingerprintB, bankAt: cursor),
            ])
            let agreed = await Self.send(connection, "ocr.select", ["mode": "smart", "source": pair])
            #expect(agreed.value?["selection"]?["sentenceStable"] == true)

            // Core's agreement rule: the same word and one token sequence inside
            // the other, in order. The richer observation is the choice (a
            // shorter second keeps the first, a longer second wins), stable
            // when it is a complete sentence; another word before "bank"
            // diverges and keeps the first, unstable. The hovered word is its
            // box's text: NormalizeEnglish trims a period, not a quote, so
            // "'bank'" is another word than "bank".
            let full = Observed.frame(Observed.fingerprintA, bankAt: cursor)
            let short = Observed.line(Observed.fingerprintA, "He sat on the bank.", bankAt: cursor)
            let clipped = "He sat on the bank of the"
            let quoted = "He sat on the 'bank' of the river."
            let pairs: [(frames: [JSONValue], text: String, word: String, stable: Bool, comment: Comment)] = [
                ([full, Observed.line(Observed.fingerprintB, "He sat on the bank.", bankAt: cursor)],
                 Observed.sentence, "bank", true, "a shorter second keeps the first"),
                ([short, Observed.frame(Observed.fingerprintB, bankAt: cursor)], Observed.sentence, "bank", true,
                 "a richer second is chosen"),
                ([full, Observed.line(Observed.fingerprintB, "He sat by the bank of the river.", bankAt: cursor)],
                 Observed.sentence, "bank", false, "another word diverges"),
                ([Observed.line(Observed.fingerprintA, clipped, bankAt: cursor),
                  Observed.line(Observed.fingerprintB, clipped, bankAt: cursor)], clipped, "bank", false,
                 "agreeing, but not a complete sentence"),
                ([Observed.line(Observed.fingerprintA, quoted, bankAt: cursor), Observed.frame(Observed.fingerprintB, bankAt: cursor)],
                 quoted, "'bank'", false, "a quoted word diverges"),
            ]
            for fixture in pairs {
                let answer = await Self.send(connection, "ocr.select", ["mode": "smart", "source": Observed.source(
                    bounds: bounds, cursor: cursor, frames: fixture.frames)])
                let chosen = answer.value?["selection"]
                #expect(chosen?["text"] == .string(fixture.text) && chosen?["hoveredWord"] == .string(fixture.word),
                        fixture.comment)
                #expect(chosen?["sentenceStable"] == .bool(fixture.stable), fixture.comment)
            }

            // A frame without lines, and a cursor farther than Core's 95 px
            // from every line, select nothing: an explicit null.
            let blank = Observed.source(bounds: bounds, cursor: cursor,
                                        frames: [["fingerprint": "0123456789abcdef", "lines": []]])
            let nothing = await Self.send(connection, "ocr.select", ["mode": "dictionary", "source": blank])
            #expect(nothing.value?["selection"] == .some(.null) && nothing.value?["lines"] == [])
            let below = Observed.source(bounds: bounds, cursor: (cursor.x, cursor.y + 200),
                                        frames: [Observed.frame("0123456789abcdef", bankAt: cursor)])
            let far = await Self.send(connection, "ocr.select", ["mode": "dictionary", "source": below])
            #expect(far.value?["selection"] == .some(.null) && far.value?["lines"] == [.string(Observed.sentence)])

            let upper = Observed.source(bounds: bounds, cursor: cursor,
                                        frames: [Observed.frame("0123456789ABCDEF", bankAt: cursor)])
            let refusals: [(payload: JSONValue, detail: String)] = [
                (["source": one], "mode is required with an observations source"),
                (["mode": "dictionary", "source": pair],
                 "ocr.select takes 1 observation in dictionary mode and 1 or 2 in smart mode, got 2"),
                (["mode": "dictionary", "source": upper],
                 "source.observations[0]: fingerprint must be 16 lowercase hex digits"),
                (["mode": "smart", "source": Observed.source(bounds: bounds, cursor: cursor,
                                                             frames: [Observed.frame(Observed.fingerprintA, bankAt: cursor)],
                                                             cached: Observed.fingerprintA)],
                 "ocr.select keeps no frame cache and takes no source.cachedFingerprint"),
            ]
            for refusal in refusals {
                let refused = await Self.send(connection, "ocr.select", refusal.payload)
                #expect(refused.error?.code == "bad_request" && refused.error?.detail == refusal.detail)
                #expect(refused.error?.message == "Некорректный запрос к службе перевода.")
            }

            // ocr.plan and the observations hover need core.init.
            let plan = await Self.send(connection, "ocr.plan", Observed.plan(cursor: (1925, 1430)))
            #expect(plan.error?.code == "not_ready" && plan.error?.detail == "core.init has not completed")
            let hover = await Self.send(connection, "hover.resolve", ["mode": "dictionary", "source": one])
            #expect(hover.error?.code == "not_ready" && hover.events.isEmpty)
            #expect(await connection.shutdown() == .exited(0))
            #expect(await invariantHolds(connection))
        }

        @Test func dictionaryHoverSendsLoadingBeforePrimary() async throws {
            let scratch = try Scratch("observe-dictionary")
            defer { scratch.remove() }
            let connection = try await Fake.connect(scratch)
            _ = try await Fake.hello(connection)
            try await Self.initialize(connection, mode: "dictionary")

            // 900x340 around the cursor, kept inside the display.
            let cursor: (x: Int64, y: Int64) = (1925, 1430)
            let plan = try #require(await Self.send(connection, "ocr.plan", Observed.plan(cursor: cursor)).value)
            #expect(plan["mode"] == "dictionary" && plan["observations"] == 1 && plan["cachedFingerprint"] == nil)
            let bounds = try #require(PixelBounds(plan["captureBounds"]))
            #expect(bounds == PixelBounds(x: 1475, y: 1260, width: 900, height: 340))
            let corner = await Self.send(connection, "ocr.plan", Observed.plan(cursor: (3830, 5)))
            #expect(PixelBounds(corner.value?["captureBounds"]) == PixelBounds(x: 2940, y: 0, width: 900, height: 340))

            let hoverId = await connection.peekNextId
            let frame = Observed.frame(Observed.fingerprintA, bankAt: bounds.local(cursor))
            let hover = await Self.send(connection, "hover.resolve", [
                "mode": "dictionary", "source": Observed.source(bounds: bounds, cursor: cursor, frames: [frame]),
            ])
            #expect(hover.value?["outcome"] == "completed")
            #expect(hover.names == ["hover.loading", "hover.primary"])
            let loading = try #require(hover.events.first)
            #expect(loading.ref == hoverId && loading.payload["source"] == .string(Observed.sentence),
                    "dictionary mode loads with the recognized line")
            #expect(loading.payload["engineLabel"] == "APPLE VISION  •  СЛОВАРЬ" && loading.payload["speaker"] == nil)
            let primary = HoverPrimary(hoverId: hoverId, payload: hover.events.last?.payload ?? .null)
            #expect(primary.source == "bank" && primary.provenance == .dictionary && primary.context == Observed.sentence)
            let displayed = try await connection.request("hover.displayed", ["hoverId": .integer(hoverId), "part": "primary"],
                                                         deadline: .seconds(2))
            #expect(displayed["recorded"] == true)

            // Admitted, then refused: the hover's mode must be the active one.
            let smart = await Self.send(connection, "hover.resolve", [
                "mode": "smart", "source": Observed.source(bounds: bounds, cursor: cursor, frames: [frame, frame]),
            ])
            #expect(smart.error?.code == "bad_request" && smart.events.isEmpty)
            #expect(smart.error?.detail
                    == "hover.resolve asked for mode smart, the active mode is dictionary; change it with settings.update")

            // A selection hover keeps 20.2's events: hover.primary first, no
            // hover.loading. The hover.phrase after it is the fake's own
            // (CoreHost sends none before 20.5).
            let selection = await Self.send(connection, "hover.resolve", [
                "source": HoverSelection(text: "The bank was steep", hoveredWord: "bank", characterIndex: 4).payload,
            ])
            #expect(selection.value?["outcome"] == "completed" && selection.names.first == "hover.primary")
            #expect(!selection.names.contains("hover.loading"))
            #expect(await connection.shutdown() == .exited(0))
            #expect(await invariantHolds(connection))
        }

        @Test func smartFrameCacheAndStaleFrame() async throws {
            let scratch = try Scratch("observe-smart")
            defer { scratch.remove() }
            let connection = try await Fake.connect(scratch)
            _ = try await Fake.hello(connection)
            try await Self.initialize(connection, mode: "smart")

            let cursor: (x: Int64, y: Int64) = (1925, 1430)
            let first = try #require(await Self.send(connection, "ocr.plan", Observed.plan(cursor: cursor)).value)
            #expect(first["mode"] == "smart" && first["observations"] == 2 && first["cachedFingerprint"] == nil)
            let bounds = try #require(PixelBounds(first["captureBounds"]))
            #expect(bounds == PixelBounds(x: 725, y: 1070, width: 2400, height: 720))
            let local = bounds.local(cursor)

            func hover(_ frames: [JSONValue], at point: (x: Int64, y: Int64)? = nil, display: Int64 = 7,
                       in capture: PixelBounds? = nil, cached: String? = nil) async -> Answer {
                await Self.send(connection, "hover.resolve", ["mode": "smart", "source": Observed.source(
                    display: display, bounds: capture ?? bounds, cursor: point ?? cursor, frames: frames, cached: cached)])
            }
            func expectStale(_ answer: Answer, _ comment: Comment) {
                #expect(answer.error?.code == "stale_frame" && answer.events.isEmpty, comment)
                #expect(answer.error?.message == "Кадр на экране изменился.", comment)
                #expect(answer.error?.message.lowercased().contains(hostWord) == false)
            }

            // Zero observations before anything was cached.
            expectStale(await hover([], cached: Observed.fingerprintA), "an empty cache")

            // Observations that diverge under Core's rule (another word before
            // "bank", or the word in quotes) are shown but not cached. The
            // loading word is the selection's, the card names the word the
            // dictionary looked up.
            let diverged = await hover([Observed.frame(Observed.fingerprintA, bankAt: local),
                                        Observed.line(Observed.fingerprintB, "He sat by the bank of the river.", bankAt: local)])
            #expect(diverged.value?["outcome"] == "completed" && diverged.names == ["hover.loading", "hover.primary"])
            let quoted = await hover([Observed.line(Observed.fingerprintA, "He sat on the 'bank' of the river.", bankAt: local),
                                      Observed.frame(Observed.fingerprintB, bankAt: local)])
            #expect(quoted.events.first?.payload["source"] == "'bank'" && quoted.events.last?.payload["source"] == "bank")
            let afterDivergence = await Self.send(connection, "ocr.plan", Observed.plan(cursor: cursor))
            #expect(afterDivergence.value?["cachedFingerprint"] == nil)

            // Observations that agree are cached. Smart mode loads with the
            // word under the ranker's label; without a ranker worker the card
            // is the heuristic's, with its own label.
            let agreed = await hover([Observed.frame(Observed.fingerprintA, bankAt: local),
                                      Observed.frame(Observed.fingerprintA, bankAt: local)])
            #expect(agreed.value?["outcome"] == "completed" && agreed.names == ["hover.loading", "hover.primary"])
            #expect(agreed.events.first?.payload["source"] == "bank"
                    && agreed.events.first?.payload["engineLabel"] == "APPLE VISION  •  CPU-СМЫСЛ")
            let agreedPrimary = HoverPrimary(hoverId: 0, payload: agreed.events.last?.payload ?? .null)
            #expect(agreedPrimary.provenance == .heuristic && agreedPrimary.engineLabel == "APPLE VISION  •  SMART-FAST")

            // Inside the reuse area the plan keeps the bounds and names the frame.
            let moved: (x: Int64, y: Int64) = (cursor.x + 150, cursor.y)
            let reuse = try #require(await Self.send(connection, "ocr.plan", Observed.plan(cursor: moved)).value)
            #expect(PixelBounds(reuse["captureBounds"]) == bounds && reuse["cachedFingerprint"] == .string(Observed.fingerprintA))
            let otherDisplay = await Self.send(connection, "ocr.plan", Observed.plan(display: 8, cursor: moved))
            #expect(otherDisplay.value?["cachedFingerprint"] == nil, "the cache is per display")
            let outside = try #require(await Self.send(connection, "ocr.plan", Observed.plan(cursor: (800, 1430))).value)
            let outsideBounds = try #require(PixelBounds(outside["captureBounds"]))
            #expect(outsideBounds == PixelBounds(x: 0, y: 1070, width: 2400, height: 720) && outside["cachedFingerprint"] == nil)

            // Zero observations select in the cached frame at the new cursor.
            let cachedHover = await hover([], at: moved, cached: Observed.fingerprintA)
            #expect(cachedHover.value?["outcome"] == "completed" && cachedHover.names == ["hover.loading", "hover.primary"])
            #expect(cachedHover.events.first?.payload["source"] == "the")
            // Two observations whose first is the cached frame select in it too.
            let fog = "A bank of fog."
            let firstCached = await hover([Observed.frame(Observed.fingerprintA, text: fog, bankAt: local),
                                           Observed.frame(Observed.fingerprintC, text: fog, bankAt: local)])
            #expect(firstCached.events.last?.payload["context"] == .string(Observed.sentence), "the cached frame's line")

            // Anything else with zero observations is stale_frame, before any card.
            expectStale(await hover([], at: (800, 1430), in: outsideBounds, cached: Observed.fingerprintA), "other bounds")
            expectStale(await hover([], at: moved, cached: Observed.fingerprintB), "another fingerprint")
            expectStale(await hover([], at: moved, display: 8, cached: Observed.fingerprintA), "another display")

            // A richer second observation is Core's choice, and the cache holds
            // it (CoreHost stores the chosen observation, as Windows does):
            // ocr.plan names observation 2, and zero observations select in its
            // frame, whose line goes on past "bank." to the second "the". The
            // one entry replaces the first.
            let richer = await hover([Observed.line(Observed.fingerprintD, "He sat on the bank.", bankAt: local),
                                      Observed.frame(Observed.fingerprintE, bankAt: local)])
            #expect(richer.value?["outcome"] == "completed" && richer.events.first?.payload["source"] == "bank")
            let afterRicher = await Self.send(connection, "ocr.plan", Observed.plan(cursor: cursor))
            #expect(afterRicher.value?["cachedFingerprint"] == .string(Observed.fingerprintE))
            let onSecond = await hover([], at: moved, cached: Observed.fingerprintE)
            #expect(onSecond.events.first?.payload["source"] == "the"
                    && onSecond.events.last?.payload["context"] == .string(Observed.sentence), "frame 2's word and line")
            expectStale(await hover([], at: moved, cached: Observed.fingerprintA), "the replaced entry")

            // Refused at admission, the bad hover does not replace the running one.
            let slowId = await connection.peekNextId
            let slow = Task { () -> String in
                do {
                    _ = try await connection.request("hover.resolve", ["source": HoverSelection(
                        text: "a slow one", hoveredWord: "slow", characterIndex: 2).payload], deadline: .seconds(15))
                    return "ok"
                } catch let error as CoreError {
                    return error.code
                } catch {
                    return "unexpected \(error)"
                }
            }
            #expect(await eventually { await connection.peekNextId > slowId })
            try await Task.sleep(for: .milliseconds(50))
            let lone = await hover([Observed.frame(Observed.fingerprintA, bankAt: local)])
            #expect(lone.error?.code == "bad_request" && lone.error?.detail
                    == "smart mode takes 2 observations, or 0 together with source.cachedFingerprint (and the fingerprint only then), got 1")
            let unnamed = await Self.send(connection, "hover.resolve", ["source": Observed.source(
                bounds: bounds, cursor: cursor, frames: [], cached: Observed.fingerprintA)])
            #expect(unnamed.error?.detail == "mode is required with an observations source")
            let ack = try await connection.request("request.cancel", ["target": .integer(slowId)], deadline: .seconds(2))
            #expect(ack["cancelled"] == true)
            #expect(await slow.value == "cancelled", "not superseded by a refused hover")
            #expect(await connection.shutdown() == .exited(0))
            #expect(await invariantHolds(connection))
        }
    }
}
