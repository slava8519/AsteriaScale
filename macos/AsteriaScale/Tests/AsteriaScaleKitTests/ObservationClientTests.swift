import Foundation
import Testing
@testable import AsteriaScaleKit

extension ProcessSuites {
    /// The typed ocr.plan, ocr.select and observations hover against the fake
    /// CoreHost (Fixtures/fake-corehost.py), over one connection and over the
    /// supervisor. The fake's shapes and rules are pinned against CoreHost by
    /// ObservationTests; these pin the typed client on top of them.
    @Suite struct ObservationClientTests {
        static let sentence = Observed.sentence

        /// One observation of the golden line with "bank" under `point` (frame pixels).
        static func frame(_ fingerprint: UInt64, text: String = sentence, bankAt point: (x: Int, y: Int)) -> ObservationFrame {
            let left = Double(point.x - 40)
            let words = zip(text.split(separator: " ").map(String.init), Observed.layout).map { word, box in
                ObservedWord(text: word, box: PixelBox(left: left + Double(box.left), top: Double(point.y - 15),
                                                       right: left + Double(box.right), bottom: Double(point.y + 15)))
            }
            return ObservationFrame(fingerprint: FrameFingerprint(value: fingerprint),
                                    lines: [ObservedLine(text: text, confidence: 1, words: words)])
        }

        static let display = ObservedDisplay(id: 7, width: 3840, height: 2160)

        static func source(_ bounds: PixelRect, cursor: PixelPoint, frames: [ObservationFrame],
                           cached: FrameFingerprint? = nil) -> ObservationsSource {
            ObservationsSource(display: display, captureBounds: bounds, cursor: cursor, observations: frames,
                               cachedFingerprint: cached)
        }

        @Test func selectNeedsOnlyHelloAndPlanNeedsInit() async throws {
            let scratch = try Scratch("typed-select")
            defer { scratch.remove() }
            let connection = try await Fake.connect(scratch)
            _ = try await Fake.hello(connection)
            let client = ObservationClient(core: connection)

            let bounds = PixelRect(x: 0, y: 0, width: 1200, height: 400)
            let cursor = PixelPoint(x: 420, y: 165)
            let chosen = try await client.select(mode: .dictionary, source: ObservationsSource(
                display: ObservedDisplay(id: 1, width: 1200, height: 400), captureBounds: bounds, cursor: cursor,
                observations: [Self.frame(0x0123_4567_89ab_cdef, bankAt: (420, 165))]))
            #expect(chosen.selection?.text == Self.sentence && chosen.selection?.hoveredWord == "bank")
            #expect(chosen.selection?.sentenceStable == true && chosen.selection?.speakerName == nil)
            #expect(chosen.lines == [Self.sentence])

            let nothing = try await client.select(mode: .dictionary, source: ObservationsSource(
                display: ObservedDisplay(id: 1, width: 1200, height: 400), captureBounds: bounds,
                cursor: PixelPoint(x: 420, y: 365), observations: [Self.frame(1, bankAt: (420, 165))]))
            #expect(nothing.selection == nil && nothing.lines == [Self.sentence], "farther than 95 px: an explicit null")

            // A refusal is CoreHost's bad_request, typed.
            do {
                _ = try await client.select(mode: .dictionary, source: ObservationsSource(
                    display: ObservedDisplay(id: 1, width: 1200, height: 400), captureBounds: bounds, cursor: cursor,
                    observations: [Self.frame(1, bankAt: (420, 165)), Self.frame(2, bankAt: (420, 165))]))
                Issue.record("two observations in dictionary mode were accepted")
            } catch let error as CoreError {
                #expect(error.code == "bad_request" && error.message == "Некорректный запрос к службе перевода.")
            }

            do {
                _ = try await client.plan(display: Self.display, cursor: PixelPoint(x: 1925, y: 1430))
                Issue.record("ocr.plan answered before core.init")
            } catch let error as CoreError {
                #expect(error.code == "not_ready")
            }
            #expect(await connection.shutdown() == .exited(0))
            #expect(await invariantHolds(connection))
        }

        @Test func dictionaryPlanAndHoverWithLoadingFirst() async throws {
            let scratch = try Scratch("typed-dictionary")
            defer { scratch.remove() }
            let connection = try await Fake.connect(scratch)
            _ = try await Fake.hello(connection)
            try await ProcessSuites.ObservationTests.initialize(connection, mode: "dictionary")
            let client = ObservationClient(core: connection)

            let cursor = PixelPoint(x: 1925, y: 1430)
            let plan = try await client.plan(display: Self.display, cursor: cursor)
            #expect(plan == CapturePlan(mode: .dictionary, captureBounds: PixelRect(x: 1475, y: 1260, width: 900, height: 340),
                                        observations: 1, cachedFingerprint: nil))
            let local = (x: cursor.x - plan.captureBounds.x, y: cursor.y - plan.captureBounds.y)
            let sent = Box<Int64>(0)
            let updates = Box<[HoverUpdate]>([])
            let outcome = await client.resolve(Self.source(plan.captureBounds, cursor: cursor,
                                                           frames: [Self.frame(0xa1, bankAt: local)]),
                                               mode: plan.mode, onSent: { sent.set($0) },
                                               onUpdate: { update in updates.mutate { $0.append(update) } })
            #expect(outcome == .completed)
            guard updates.get.count == 2, case .loading(let loading) = updates.get[0],
                  case .primary(let primary) = updates.get[1] else {
                Issue.record("expected loading then primary, got \(updates.get)")
                await connection.close(.killed)
                return
            }
            #expect(loading.hoverId == sent.get && primary.hoverId == sent.get && sent.get > 0)
            #expect(loading.source == Self.sentence && loading.engineLabel == "APPLE VISION  •  СЛОВАРЬ" && loading.speaker == nil)
            #expect(primary.source == "bank" && primary.provenance == .dictionary && primary.context == Self.sentence)

            // The mode is the plan's; a hover in another mode is CoreHost's bad_request.
            let other = await client.resolve(Self.source(plan.captureBounds, cursor: cursor,
                                                         frames: [Self.frame(1, bankAt: local), Self.frame(2, bankAt: local)]),
                                             mode: .smart, onUpdate: { _ in })
            guard case .rejected(let error) = other else {
                Issue.record("expected bad_request, got \(other)")
                return
            }
            #expect(error.code == "bad_request")
            #expect(await connection.shutdown() == .exited(0))
            #expect(await invariantHolds(connection))
        }

        @Test func smartCacheAndStaleFrameThroughTheSupervisor() async throws {
            let scratch = try Scratch("typed-smart")
            defer { scratch.remove() }
            var config = Fake.config("normal", scratch)
            config.initialize = true
            let supervisor = CoreSupervisor(config)
            await supervisor.start()
            #expect(await eventually { await supervisor.state.isReady })
            let initial = try await supervisor.request("settings.get", deadline: .seconds(2))
            let revision = try #require(initial["revision"]?.int64Value)
            _ = try await supervisor.request("settings.update", ["baseRevision": .integer(revision),
                                                                 "patch": ["hoverEnabled": true, "mode": "smart"]],
                                             deadline: .seconds(5))
            let client = ObservationClient(core: supervisor)

            let cursor = PixelPoint(x: 1925, y: 1430)
            let plan = try await client.plan(display: Self.display, cursor: cursor)
            #expect(plan.mode == .smart && plan.observations == 2 && plan.cachedFingerprint == nil)
            #expect(plan.captureBounds == PixelRect(x: 725, y: 1070, width: 2400, height: 720))
            let local = (x: cursor.x - plan.captureBounds.x, y: cursor.y - plan.captureBounds.y)

            // Zero observations against a frame nobody cached: stale_frame, no update.
            let updates = Box<[HoverUpdate]>([])
            let stale = await client.resolve(Self.source(plan.captureBounds, cursor: cursor, frames: [],
                                                         cached: FrameFingerprint(value: 0xa1)),
                                             mode: .smart, onUpdate: { update in updates.mutate { $0.append(update) } })
            guard case .staleFrame(let error) = stale else {
                Issue.record("expected stale_frame, got \(stale)")
                await supervisor.stop()
                return
            }
            #expect(error.message == "Кадр на экране изменился." && updates.get.isEmpty)
            #expect(HoverReport.after(stale, shownPrimary: false).overlay == .hide)

            // Two agreeing observations are cached; the next plan names the frame
            // and zero observations select in it.
            let agreed = await client.resolve(Self.source(plan.captureBounds, cursor: cursor, frames: [
                Self.frame(0xa1, bankAt: local), Self.frame(0xa1, bankAt: local),
            ]), mode: .smart, onUpdate: { _ in })
            #expect(agreed == .completed)
            let again = try await client.plan(display: Self.display, cursor: cursor)
            #expect(again.cachedFingerprint == FrameFingerprint(value: 0xa1) && again.captureBounds == plan.captureBounds)
            let cachedUpdates = Box<[HoverUpdate]>([])
            let cached = await client.resolve(Self.source(again.captureBounds, cursor: cursor, frames: [],
                                                          cached: again.cachedFingerprint),
                                              mode: .smart, onUpdate: { update in cachedUpdates.mutate { $0.append(update) } })
            #expect(cached == .completed)
            guard case .loading(let loading)? = cachedUpdates.get.first else {
                Issue.record("expected hover.loading first, got \(cachedUpdates.get)")
                await supervisor.stop()
                return
            }
            #expect(loading.source == "bank" && loading.engineLabel == "APPLE VISION  •  CPU-СМЫСЛ")
            await supervisor.stop()
        }

        static let budgetBounds = PixelRect(x: 0, y: 0, width: 1200, height: 400)

        /// `count` lines of 3900 letters (inside CoreHost's 4000) and one
        /// line of `padding` more letters, each with one word.
        static func budgetSource(lines count: Int, padding: Int) -> ObservationsSource {
            let box = PixelBox(left: 100, top: 150, right: 140, bottom: 180)
            var lines = Array(repeating: ObservedLine(text: String(repeating: "a", count: 3900), confidence: 1,
                                                      words: [ObservedWord(text: "a", box: box)]), count: count)
            lines.append(ObservedLine(text: String(repeating: "p", count: padding), confidence: 1,
                                      words: [ObservedWord(text: "p", box: box)]))
            return ObservationsSource(display: ObservedDisplay(id: 1, width: 1200, height: 400), captureBounds: budgetBounds,
                                      cursor: PixelPoint(x: 120, y: 165),
                                      observations: [ObservationFrame(fingerprint: FrameFingerprint(value: 1), lines: lines)])
        }

        /// The line count and padding that land `command`'s request line,
        /// with the connection's next id, on the budget exactly: lines up to
        /// just below it, then the padding line (one letter is one byte).
        static func landing(_ connection: CoreConnection, _ command: String,
                            _ payload: (ObservationsSource) -> JSONValue) async throws -> (lines: Int, padding: Int) {
            let id = await connection.peekNextId
            for lines in 200...255 {
                let bytes = try CoreRequestLine(id: id, command: command,
                                                payload: payload(budgetSource(lines: lines, padding: 0))).encodedLine().count
                let left = ObservationBudget.maxLineBytes - bytes
                if (1...4000).contains(left) {
                    let landed = try CoreRequestLine(id: id, command: command,
                                                     payload: payload(budgetSource(lines: lines, padding: left))).encodedLine()
                    #expect(landed.count == ObservationBudget.maxLineBytes)
                    return (lines, left)
                }
            }
            Issue.record("no line count lands \(command) within one padding line of the budget")
            return (0, 0)
        }

        static func selectPayload(_ source: ObservationsSource) -> JSONValue {
            ObservationClient.selectPayload(mode: .dictionary, source: source)
        }

        static func hoverPayload(_ source: ObservationsSource) -> JSONValue {
            ObservationClient.hoverPayload(source, mode: .dictionary, game: nil)
        }

        /// The budget is measured on the encoded request line, envelope and
        /// newline included, for ocr.select and the observations hover alike:
        /// 921 600 bytes go out, 921 601 are refused here with a Russian
        /// message, before the request takes an id.
        @Test func observationsBudgetByTheEncodedLine() async throws {
            let scratch = try Scratch("typed-budget")
            defer { scratch.remove() }
            let connection = try await Fake.connect(scratch)
            _ = try await Fake.hello(connection)
            let client = ObservationClient(core: connection)
            let bounds = Self.budgetBounds
            let box = PixelBox(left: 100, top: 150, right: 140, bottom: 180)
            func source(lines count: Int, padding: Int) -> ObservationsSource {
                Self.budgetSource(lines: count, padding: padding)
            }
            let (count, padding) = try await Self.landing(connection, "ocr.select", Self.selectPayload)
            try #require(count > 0)

            let sentBefore = await connection.counters.sent
            let idBefore = await connection.peekNextId
            do {
                _ = try await client.select(mode: .dictionary, source: source(lines: count, padding: padding + 1))
                Issue.record("a line one byte over the budget was sent")
            } catch let error as CoreError {
                #expect(error.code == "observations_too_large" && error.message == "Слишком много текста в области распознавания.")
                #expect(error.detail == "ocr.select would be a 921601-byte line, over the 921600-byte observations budget")
            }
            let sentAfterRefusal = await connection.counters.sent
            let idAfterRefusal = await connection.peekNextId
            #expect(sentAfterRefusal == sentBefore && idAfterRefusal == idBefore, "nothing written, no id taken")

            let atBudget = try await client.select(mode: .dictionary, source: source(lines: count, padding: padding))
            #expect(atBudget.selection?.hoveredWord == "a" && atBudget.lines.count == count + 1, "sent whole, answered")
            let sentAtBudget = await connection.counters.sent
            #expect(sentAtBudget == sentBefore + 1)

            // Over one of CoreHost's limits: refused too, never cut short.
            let hover = await client.resolve(ObservationsSource(
                display: ObservedDisplay(id: 1, width: 1200, height: 400), captureBounds: bounds, cursor: PixelPoint(x: 1, y: 1),
                observations: [ObservationFrame(fingerprint: FrameFingerprint(value: 1), lines: [
                    ObservedLine(text: String(repeating: "a", count: 4001), confidence: 1, words: [ObservedWord(text: "a", box: box)]),
                ])]), mode: .dictionary, onUpdate: { _ in })
            guard case .rejected(let error) = hover else {
                Issue.record("expected observations_too_large, got \(hover)")
                return
            }
            #expect(error.code == "observations_too_large" && HoverReport.after(hover, shownPrimary: false).overlay
                    == .message("Слишком много текста в области распознавания."))
            let sentAtEnd = await connection.counters.sent
            #expect(sentAtEnd == sentBefore + 1, "the refused hover wrote nothing")

            // The observations hover: its own landing (a longer command and
            // payload), measured with the id it would take now.
            let hoverLanding = try await Self.landing(connection, "hover.resolve", Self.hoverPayload)
            try #require(hoverLanding.lines > 0)
            let hoverIdBefore = await connection.peekNextId
            let over = await client.resolve(source(lines: hoverLanding.lines, padding: hoverLanding.padding + 1),
                                            mode: .dictionary, onUpdate: { _ in })
            guard case .rejected(let overError) = over else {
                Issue.record("expected observations_too_large for a hover one byte over, got \(over)")
                return
            }
            #expect(overError.code == "observations_too_large" && overError.message == "Слишком много текста в области распознавания.")
            #expect(overError.detail == "hover.resolve would be a 921601-byte line, over the 921600-byte observations budget")
            let sentAfterHoverRefusal = await connection.counters.sent
            let idAfterHoverRefusal = await connection.peekNextId
            #expect(sentAfterHoverRefusal == sentAtEnd && idAfterHoverRefusal == hoverIdBefore, "nothing written, no id taken")
            // At the budget it goes out whole; before core.init the fake
            // answers not_ready, which proves it was read.
            let sentId = Box<Int64>(0)
            let atHoverBudget = await client.resolve(source(lines: hoverLanding.lines, padding: hoverLanding.padding),
                                                     mode: .dictionary, onSent: { sentId.set($0) }, onUpdate: { _ in })
            guard case .rejected(let atError) = atHoverBudget else {
                Issue.record("expected not_ready for a hover at the budget, got \(atHoverBudget)")
                return
            }
            #expect(atError.code == "not_ready" && sentId.get == hoverIdBefore)
            let sentAfterHover = await connection.counters.sent
            #expect(sentAfterHover == sentAtEnd + 1)
            #expect(await connection.shutdown() == .exited(0))
            #expect(await invariantHolds(connection))
        }

        /// The supervisor, the app's only request path, passes the budget on:
        /// a select far enough over it to be over whatever id it takes, and
        /// under CoreHost's 1 MiB, is refused here, never sent.
        @Test func theSupervisorKeepsTheBudget() async throws {
            let scratch = try Scratch("typed-budget-supervisor")
            defer { scratch.remove() }
            let supervisor = CoreSupervisor(Fake.config("normal", scratch))
            await supervisor.start()
            #expect(await eventually { await supervisor.state.isReady })
            let client = ObservationClient(core: supervisor)
            // About 965 000 bytes: over 921 600, under 1 048 576.
            let source = Self.budgetSource(lines: 240, padding: 100)
            let line = try CoreRequestLine(id: 1, command: "ocr.select", payload: Self.selectPayload(source)).encodedLine()
            #expect(line.count > ObservationBudget.maxLineBytes + 1000 && line.count < CoreProtocol.maxRequestFrameBytes - 1000)
            do {
                let answer = try await client.select(mode: .dictionary, source: source)
                Issue.record("the supervisor sent a line over the budget and got \(answer)")
            } catch let error as CoreError {
                #expect(error.code == "observations_too_large" && error.message == "Слишком много текста в области распознавания.")
            }
            let hover = await client.resolve(source, mode: .dictionary, onUpdate: { _ in })
            guard case .rejected(let error) = hover else {
                Issue.record("expected observations_too_large through the supervisor, got \(hover)")
                await supervisor.stop()
                return
            }
            #expect(error.code == "observations_too_large")
            // Still ready: nothing reached the fake, nothing was cut short.
            #expect(await supervisor.state.isReady)
            await supervisor.stop()
        }
    }
}
