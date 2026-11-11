import Foundation
import Testing
@testable import AsteriaScaleKit

/// The cursor tracker on a clock the test moves (roadmap 20.3 DoD 7;
/// docs/MACOS.md T37): Windows' HoverTracking rules, poll by poll.
@Suite struct HoverTrackerTests {
    /// A clock that moves only when the test says so.
    final class FakeClock: MonotonicClock, @unchecked Sendable {
        private let lock = NSLock()
        private var value: Duration = .seconds(1000)

        var now: Duration {
            lock.lock()
            defer { lock.unlock() }
            return value
        }

        func advance(_ by: Duration) {
            lock.lock()
            value += by
            lock.unlock()
        }
    }

    /// Optional, as DisplayDescription makes them; a test requires them
    /// before it needs one (no force unwrap: a trap is a crash report).
    static let main = DisplayDescription(id: 1, frame: PointRect(x: 0, y: 0, width: 1512, height: 982),
                                         pixelWidth: 3024, pixelHeight: 1964)
    static let side = DisplayDescription(id: 2, frame: PointRect(x: 1512, y: 0, width: 1920, height: 1080),
                                         pixelWidth: 1920, pixelHeight: 1080)

    @Test func theDisplaysAreAccepted() {
        #expect(Self.main != nil && Self.side != nil)
    }

    /// A sample at whole pixels of `display` (the point is only where a card goes).
    static func sample(_ x: Int, _ y: Int, on display: DisplayDescription? = main, own: Bool = false) -> HoverTracker.Sample {
        let location = display.map { CursorLocation(display: $0, cursor: PixelPoint(x: x, y: y)) }
        return HoverTracker.Sample(point: GlobalPoint(x: Double(x), y: Double(y)), location: location, overOwnWindow: own)
    }

    static func started(_ actions: [HoverTracker.Action]) -> HoverTracker.Hover? {
        for action in actions { if case .startHover(let hover) = action { return hover } }
        return nil
    }

    /// Polls every 90 ms at the same place until a hover starts; returns the
    /// time since the first poll and the hover.
    static func settle(_ tracker: inout HoverTracker, _ clock: FakeClock, at sample: HoverTracker.Sample,
                       delay: Int, limit: Int = 40) -> (elapsed: Duration, hover: HoverTracker.Hover)? {
        var elapsed: Duration = .zero
        for _ in 0..<limit {
            if let hover = started(tracker.tick(sample, delayMilliseconds: delay)) { return (elapsed, hover) }
            clock.advance(HoverTracker.pollInterval)
            elapsed += HoverTracker.pollInterval
        }
        return nil
    }

    @Test func pollIntervalRadiusAndDelayBoundsAreWindows() {
        #expect(HoverTracker.pollInterval == .milliseconds(90))
        #expect(HoverTracker.radiusSquared == 225)
        #expect(HoverTracker.delayRange == 350...1500 && TranslationSettings.delayRange == HoverTracker.delayRange)
        #expect(HoverTracker.delay(milliseconds: 100) == .milliseconds(350))
        #expect(HoverTracker.delay(milliseconds: 349) == .milliseconds(350))
        #expect(HoverTracker.delay(milliseconds: 700) == .milliseconds(700))
        #expect(HoverTracker.delay(milliseconds: 1501) == .milliseconds(1500))
        #expect(HoverTracker.delay(milliseconds: Int.max) == .milliseconds(1500))
        #expect(HoverTracker.delay(milliseconds: Int.min) == .milliseconds(350))
    }

    /// A hover starts on the first poll at least the delay after the cursor
    /// settled: 720 ms for 700 (8 polls of 90 ms), and the clamped bounds.
    @Test(arguments: [(700, 720), (350, 360), (100, 360), (1500, 1530), (5000, 1530)])
    func aSettledCursorStartsOneHoverAfterTheDelay(_ delay: Int, _ expected: Int) throws {
        let clock = FakeClock()
        var tracker = HoverTracker(clock: clock)
        let settled = try #require(Self.settle(&tracker, clock, at: Self.sample(400, 300), delay: delay))
        #expect(settled.elapsed == .milliseconds(expected), "delay \(delay)")
        #expect(settled.hover.location.cursor == PixelPoint(x: 400, y: 300) && settled.hover.location.display.id == 1)
        #expect(settled.hover.anchorId == tracker.anchor?.id && settled.hover.point == GlobalPoint(x: 400, y: 300))
        // One hover per anchor: staying on starts no other.
        for _ in 0..<30 {
            clock.advance(HoverTracker.pollInterval)
            #expect(tracker.tick(Self.sample(400, 300), delayMilliseconds: delay).isEmpty)
        }
    }

    @Test func theFirstPollAnchorsAndHides() {
        let clock = FakeClock()
        var tracker = HoverTracker(clock: clock)
        #expect(tracker.tick(Self.sample(10, 10), delayMilliseconds: 700) == [.hideCard])
        #expect(tracker.anchor?.location.cursor == PixelPoint(x: 10, y: 10) && tracker.hoveredAnchorId == nil)
    }

    /// Within 15 display pixels (dx² + dy² <= 225) the anchor holds and its
    /// timer keeps running; one pixel farther it moves and starts over.
    @Test func theRadiusIsFifteenDisplayPixels() throws {
        // The first far points too: dx² + dy² of 226 and 232, just past 225.
        for (dx, dy, near) in [(15, 0, true), (0, -15, true), (9, 12, true), (-12, -9, true), (16, 0, false),
                               (10, 12, false), (11, 11, false), (0, 16, false), (15, 1, false), (1, -15, false),
                               (14, 6, false)] {
            let clock = FakeClock()
            var tracker = HoverTracker(clock: clock)
            _ = tracker.tick(Self.sample(500, 500), delayMilliseconds: 700)
            clock.advance(.milliseconds(400))
            let moved = tracker.tick(Self.sample(500 + dx, 500 + dy), delayMilliseconds: 700)
            clock.advance(.milliseconds(400))
            let later = tracker.tick(Self.sample(500 + dx, 500 + dy), delayMilliseconds: 700)
            if near {
                #expect(moved.isEmpty, "(\(dx), \(dy)) stays")
                // 800 ms since the anchor settled: the hover starts with the
                // cursor of this poll (Windows processes that tick's cursor);
                // the anchor stays where the cursor settled.
                let hover = try #require(Self.started(later), "(\(dx), \(dy))")
                #expect(hover.location.cursor == PixelPoint(x: 500 + dx, y: 500 + dy))
                #expect(tracker.anchor?.location.cursor == PixelPoint(x: 500, y: 500) && hover.anchorId == tracker.anchor?.id)
            } else {
                #expect(moved == [.hideCard], "(\(dx), \(dy)) moves the anchor")
                #expect(later.isEmpty, "(\(dx), \(dy)): only 400 ms at the new anchor")
                #expect(tracker.anchor?.location.cursor == PixelPoint(x: 500 + dx, y: 500 + dy))
            }
        }
    }

    /// Leaving the anchor cancels its hover and hides the card; the new
    /// anchor gets its own hover after its own delay.
    @Test func leavingTheAnchorCancelsItsHover() throws {
        let clock = FakeClock()
        var tracker = HoverTracker(clock: clock)
        let first = try #require(Self.settle(&tracker, clock, at: Self.sample(100, 100), delay: 350)).hover
        clock.advance(HoverTracker.pollInterval)
        #expect(tracker.tick(Self.sample(130, 100), delayMilliseconds: 350) == [.cancelHover(anchorId: first.anchorId), .hideCard])
        #expect(tracker.hoveredAnchorId == nil && tracker.hover == nil && tracker.anchor?.id != first.anchorId)
        let second = try #require(Self.settle(&tracker, clock, at: Self.sample(130, 100), delay: 350)).hover
        #expect(second.anchorId > first.anchorId && second.location.cursor == PixelPoint(x: 130, y: 100))
        // Another display is never near, whatever its pixels.
        clock.advance(HoverTracker.pollInterval)
        #expect(tracker.tick(Self.sample(130, 100, on: Self.side), delayMilliseconds: 350)
                == [.cancelHover(anchorId: second.anchorId), .hideCard])
        // No display under the cursor: the hover goes, and no anchor is kept.
        let third = try #require(Self.settle(&tracker, clock, at: Self.sample(130, 100, on: Self.side), delay: 350)).hover
        clock.advance(HoverTracker.pollInterval)
        #expect(tracker.tick(Self.sample(0, 0, on: nil), delayMilliseconds: 350) == [.cancelHover(anchorId: third.anchorId), .hideCard])
        #expect(tracker.anchor == nil)
        #expect(tracker.tick(Self.sample(0, 0, on: nil), delayMilliseconds: 350).isEmpty)
    }

    /// Over an own window the card hides and nothing else changes
    /// (HoverTracking.OnTimer): the anchor and its timer stay, no hover
    /// starts there, and back within the radius the started hover is not
    /// started again.
    @Test func anOwnWindowOnlyHidesTheCard() throws {
        let clock = FakeClock()
        var tracker = HoverTracker(clock: clock)
        _ = tracker.tick(Self.sample(200, 200), delayMilliseconds: 700)
        clock.advance(.milliseconds(800))
        #expect(tracker.tick(Self.sample(200, 200, own: true), delayMilliseconds: 700) == [.hideCard])
        #expect(tracker.hoveredAnchorId == nil, "no hover starts over an own window")
        let hover = try #require(Self.started(tracker.tick(Self.sample(205, 200), delayMilliseconds: 700)))
        clock.advance(HoverTracker.pollInterval)
        #expect(tracker.tick(Self.sample(800, 800, own: true), delayMilliseconds: 700) == [.hideCard])
        #expect(tracker.anchor?.id == hover.anchorId && tracker.hover == hover, "an own window cancels nothing")
        clock.advance(HoverTracker.pollInterval)
        #expect(tracker.tick(Self.sample(200, 200), delayMilliseconds: 700).isEmpty)
    }

    /// The re-check before the loading card and the card: the started
    /// anchor, still current, the cursor within its radius on its display
    /// and not over an own window.
    @Test func theCursorIsCheckedAgainBeforeShowing() throws {
        let clock = FakeClock()
        var tracker = HoverTracker(clock: clock)
        #expect(!tracker.mayShow(anchorId: 1, at: Self.sample(300, 300)), "nothing started")
        _ = tracker.tick(Self.sample(300, 300), delayMilliseconds: 350)
        let anchorId = try #require(tracker.anchor?.id)
        #expect(!tracker.mayShow(anchorId: anchorId, at: Self.sample(300, 300)), "settled but not started")
        let hover = try #require(Self.settle(&tracker, clock, at: Self.sample(300, 300), delay: 350)).hover
        #expect(hover.anchorId == anchorId)
        #expect(tracker.mayShow(anchorId: anchorId, at: Self.sample(300, 300)))
        #expect(tracker.mayShow(anchorId: anchorId, at: Self.sample(309, 312)))
        #expect(!tracker.mayShow(anchorId: anchorId, at: Self.sample(310, 312)), "out of the radius before the next poll")
        #expect(!tracker.mayShow(anchorId: anchorId, at: Self.sample(315, 301)), "dx² + dy² = 226, just past the radius")
        #expect(!tracker.mayShow(anchorId: anchorId, at: Self.sample(300, 300, on: Self.side)))
        #expect(!tracker.mayShow(anchorId: anchorId, at: Self.sample(300, 300, on: nil)))
        #expect(!tracker.mayShow(anchorId: anchorId, at: Self.sample(300, 300, own: true)))
        #expect(!tracker.mayShow(anchorId: anchorId + 1, at: Self.sample(300, 300)), "another anchor's hover")
        // After the cursor left, the old hover's late card is not shown even back at its place.
        clock.advance(HoverTracker.pollInterval)
        _ = tracker.tick(Self.sample(400, 400), delayMilliseconds: 350)
        #expect(!tracker.mayShow(anchorId: anchorId, at: Self.sample(300, 300)))

        // The check is against the hover's own cursor, not where the cursor
        // first settled (Windows' CursorIsNear on the processed point).
        var drifting = HoverTracker(clock: clock)
        _ = drifting.tick(Self.sample(600, 600), delayMilliseconds: 350)
        clock.advance(.milliseconds(400))
        let drifted = try #require(Self.started(drifting.tick(Self.sample(610, 600), delayMilliseconds: 350)))
        #expect(drifted.location.cursor == PixelPoint(x: 610, y: 600))
        #expect(drifting.mayShow(anchorId: drifted.anchorId, at: Self.sample(624, 600)), "14 px from the hover, 24 from the anchor")
        #expect(!drifting.mayShow(anchorId: drifted.anchorId, at: Self.sample(594, 600)), "16 px from the hover, 6 from the anchor")
    }

    /// Tracking that stops cancels the hover in flight and forgets the
    /// anchor; it starts again from a fresh one.
    @Test func resetCancelsAndForgets() throws {
        let clock = FakeClock()
        var tracker = HoverTracker(clock: clock)
        #expect(tracker.reset() == [.hideCard])
        let hover = try #require(Self.settle(&tracker, clock, at: Self.sample(50, 60), delay: 350)).hover
        #expect(tracker.reset() == [.cancelHover(anchorId: hover.anchorId), .hideCard])
        #expect(tracker.anchor == nil && tracker.hoveredAnchorId == nil && tracker.hover == nil)
        let again = try #require(Self.settle(&tracker, clock, at: Self.sample(50, 60), delay: 350))
        #expect(again.hover.anchorId > hover.anchorId && again.elapsed == .milliseconds(360))
    }

    /// A new mode or new smart settings while tracking (Windows' SetMode and
    /// ApplySmartDictionarySettings reset the tracking): the hover in flight
    /// is cancelled and the anchor forgotten, a new mode also hides the
    /// card, and the resting cursor starts a hover again after the delay.
    @Test func aSettingsChangeRestartsTheHover() throws {
        for (change, expected) in [(HoverSettingsChange.smartSettings, false), (.mode, true)] {
            let clock = FakeClock()
            var tracker = HoverTracker(clock: clock)
            let hover = try #require(Self.settle(&tracker, clock, at: Self.sample(70, 80), delay: 350)).hover
            let actions = tracker.restart(after: change)
            #expect(actions == (expected ? [.cancelHover(anchorId: hover.anchorId), .hideCard]
                                          : [.cancelHover(anchorId: hover.anchorId)]), "\(change)")
            #expect(tracker.anchor == nil && tracker.hover == nil, "\(change)")
            let again = try #require(Self.settle(&tracker, clock, at: Self.sample(70, 80), delay: 350), "\(change)")
            #expect(again.hover.anchorId > hover.anchorId && again.elapsed == .milliseconds(360), "\(change)")
        }
        // Before any hover: nothing to cancel; a new mode still hides the card.
        var idle = HoverTracker(clock: FakeClock())
        let idleSmart = idle.restart(after: .smartSettings)
        let idleMode = idle.restart(after: .mode)
        #expect(idleSmart.isEmpty && idleMode == [.hideCard])
        // Any other change leaves the anchor and its hover alone.
        let clock = FakeClock()
        var tracker = HoverTracker(clock: clock)
        let hover = try #require(Self.settle(&tracker, clock, at: Self.sample(70, 80), delay: 350)).hover
        let unchanged = tracker.restart(after: .none)
        #expect(unchanged.isEmpty && tracker.hover == hover && tracker.anchor?.id == hover.anchorId)
    }

    /// Which settings changes restart the tracking: the mode first, then the
    /// smart dictionary's model, backend and keep-warm; not the delay, not
    /// the switch, and nothing for the first settings of a session.
    @Test func whichSettingsChangesRestartTheHover() {
        let base = TranslationSettings(hoverEnabled: true, hoverDelayMs: 700, mode: "smart", senseModel: "consec",
                                       senseBackend: "cpu", senseKeepWarm: false)
        func changed(_ edit: (inout TranslationSettings) -> Void) -> TranslationSettings {
            var settings = base
            edit(&settings)
            return settings
        }
        #expect(base.hoverChange(from: nil) == .none)
        #expect(base.hoverChange(from: base) == .none)
        #expect(changed { $0.mode = "dictionary" }.hoverChange(from: base) == .mode)
        #expect(changed { $0.mode = "dictionary"; $0.senseModel = "glossbert" }.hoverChange(from: base) == .mode)
        #expect(changed { $0.senseModel = "glossbert" }.hoverChange(from: base) == .smartSettings)
        #expect(changed { $0.senseBackend = "npu" }.hoverChange(from: base) == .smartSettings)
        #expect(changed { $0.senseKeepWarm = true }.hoverChange(from: base) == .smartSettings)
        #expect(changed { $0.hoverDelayMs = 1200 }.hoverChange(from: base) == .none)
        #expect(changed { $0.hoverEnabled = false }.hoverChange(from: base) == .none)
    }

    /// Cursor values far outside any display report their overflow instead
    /// of trapping: they are simply far.
    @Test func extremeCursorsAreFarAndNeverTrap() throws {
        let main = try #require(Self.main)
        let near = CursorLocation(display: main, cursor: PixelPoint(x: Int.max, y: Int.min))
        let far = CursorLocation(display: main, cursor: PixelPoint(x: Int.min, y: Int.max))
        #expect(!HoverTracker.isNear(near, far) && !HoverTracker.isNear(far, near))
        #expect(HoverTracker.isNear(near, near))
        let big = CursorLocation(display: main, cursor: PixelPoint(x: 1 << 40, y: 0))
        #expect(!HoverTracker.isNear(big, CursorLocation(display: main, cursor: PixelPoint(x: -(1 << 40), y: 0))))
    }
}
