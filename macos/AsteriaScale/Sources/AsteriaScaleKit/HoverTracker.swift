import Foundation

/// A monotonic clock the hover tracker reads: the system's in the app, a
/// hand-moved one in the tests.
public protocol MonotonicClock: Sendable {
    /// Time since a fixed origin; never goes back.
    var now: Duration { get }
}

/// ContinuousClock, as time since this value was made.
public struct SystemMonotonicClock: MonotonicClock {
    private let origin = ContinuousClock.now

    public init() {}

    public var now: Duration { origin.duration(to: .now) }
}

/// When to start a hover and when to drop one, as the Windows tracker
/// decides it (HoverTracking.cs; docs/MACOS.md T37): the app polls the cursor
/// every 90 ms (NSEvent.mouseLocation, no global event monitor or event tap,
/// so no «Мониторинг ввода» or «Универсальный доступ»), and a cursor that
/// stays within 15 display pixels of where it settled (dx² + dy² <= 225, the
/// Windows RadiusSquared) for the delay from the settings (clamped to
/// 350..1500 ms, Core's EffectiveDelayMs) starts one hover for that anchor.
/// Moving farther cancels it and hides the card; a cursor over one of the
/// app's own windows only hides the card. The hover takes the cursor of the
/// poll that starts it, as Windows processes that tick's cursor, and before
/// its loading card or card is shown the app asks `mayShow` with a fresh
/// sample, which must lie within the radius of that cursor (Windows'
/// CursorIsNear).
///
/// A pure state machine: the app feeds it samples and carries out the
/// actions it returns; the clock is injected, so every timing is tested
/// without waiting.
public struct HoverTracker: Sendable {
    public static let pollInterval: Duration = .milliseconds(90)
    /// HoverTracking.RadiusSquared: 15 display pixels (T18: not rescaled for Retina).
    public static let radiusSquared: Int64 = 225
    /// HoverCoordinator.EffectiveDelayMs' bounds.
    public static let delayRange = 350...1500

    /// The settings' delay, clamped as Core clamps it.
    public static func delay(milliseconds: Int) -> Duration {
        .milliseconds(min(max(milliseconds, delayRange.lowerBound), delayRange.upperBound))
    }

    /// One poll of the cursor.
    public struct Sample: Sendable, Hashable {
        /// Cocoa global points (NSEvent.mouseLocation): where a card goes.
        public let point: GlobalPoint
        /// The display under the cursor and the cursor in its pixels; nil
        /// when no display holds the point.
        public let location: CursorLocation?
        /// The topmost window under the cursor is one of the app's own
        /// (settings, «Проверка перевода»; not the click-through overlay).
        public let overOwnWindow: Bool

        public init(point: GlobalPoint, location: CursorLocation?, overOwnWindow: Bool) {
            self.point = point
            self.location = location
            self.overOwnWindow = overOwnWindow
        }
    }

    /// Where the cursor settled.
    public struct Anchor: Sendable, Hashable {
        /// Increases with every anchor; a hover and its checks name it.
        public let id: Int
        public let point: GlobalPoint
        public let location: CursorLocation
        /// The clock's time when the cursor settled here.
        public let since: Duration
    }

    /// The one hover of an anchor: the cursor of the poll that started it.
    public struct Hover: Sendable, Hashable {
        public let anchorId: Int
        public let point: GlobalPoint
        public let location: CursorLocation
    }

    public enum Action: Sendable, Hashable {
        /// Hide the card (Windows hides the overlay at every new anchor).
        case hideCard
        /// Cancel the hover started for this anchor.
        case cancelHover(anchorId: Int)
        /// Start the one hover of the current anchor.
        case startHover(Hover)
    }

    private let clock: any MonotonicClock
    public private(set) var anchor: Anchor?
    /// The current anchor's hover, once started; one hover per anchor.
    public private(set) var hover: Hover?
    private var nextId = 1

    /// The anchor whose hover has started.
    public var hoveredAnchorId: Int? { hover?.anchorId }

    public init(clock: any MonotonicClock) { self.clock = clock }

    /// One poll. `delayMilliseconds` is the settings' hoverDelayMs.
    public mutating func tick(_ sample: Sample, delayMilliseconds: Int) -> [Action] {
        // HoverTracking.OnTimer: over an own window the card goes and nothing
        // else changes, so coming back within the radius starts no new hover.
        if sample.overOwnWindow { return [.hideCard] }
        let now = clock.now
        guard let location = sample.location else {
            // No display holds the point: nothing to anchor, nothing to hover.
            return drop()
        }
        if let anchor, Self.isNear(location, anchor.location) {
            // Inclusive, as Windows' `>=`: a poll exactly on the delay starts it.
            guard hoveredAnchorId != anchor.id, now - anchor.since >= Self.delay(milliseconds: delayMilliseconds)
            else { return [] }
            let started = Hover(anchorId: anchor.id, point: sample.point, location: location)
            hover = started
            return [.startHover(started)]
        }
        var actions = drop()
        if actions.isEmpty { actions = [.hideCard] }
        anchor = Anchor(id: nextId, point: sample.point, location: location, since: now)
        nextId += 1
        return actions
    }

    /// Whether the card of the hover started for `anchorId` may be shown
    /// with the cursor at `sample`: that anchor is still the current one,
    /// the cursor is within the radius of the hover's cursor on the same
    /// display, and not over an own window.
    public func mayShow(anchorId: Int, at sample: Sample) -> Bool {
        guard let anchor, anchor.id == anchorId, let hover, hover.anchorId == anchorId, !sample.overOwnWindow,
              let location = sample.location else { return false }
        return Self.isNear(location, hover.location)
    }

    /// Tracking stops (translation off, the service lost, the app quitting):
    /// the hover in flight is cancelled, the card hidden and the anchor
    /// forgotten, so tracking that resumes starts from a fresh anchor.
    public mutating func reset() -> [Action] {
        let actions = drop()
        return actions.isEmpty ? [.hideCard] : actions
    }

    /// A settings change while tracking, as Windows' HoverCoordinator takes
    /// it: a new mode (SetMode) or new smart dictionary settings
    /// (ApplySmartDictionarySettings) reset the tracking, so the hover in
    /// flight is cancelled and the anchor forgotten, and a cursor that
    /// rests where it is starts a hover with the new settings after the
    /// delay. A new mode hides the card at once (HideAllOverlays); new smart
    /// settings leave it to the next poll that finds the cursor on a
    /// display, which has no anchor and so hides it as any new anchor does
    /// (Windows' OnTimer after ResetTracking). Other changes leave the
    /// tracking alone.
    public mutating func restart(after change: HoverSettingsChange) -> [Action] {
        switch change {
        case .none: return []
        case .smartSettings: return drop().filter { $0 != .hideCard }
        case .mode: return reset()
        }
    }

    private mutating func drop() -> [Action] {
        guard let anchor else { return [] }
        let started = hoveredAnchorId == anchor.id
        self.anchor = nil
        hover = nil
        return started ? [.cancelHover(anchorId: anchor.id), .hideCard] : [.hideCard]
    }

    /// Same display, and dx² + dy² <= 225 in its pixels. Every step reports
    /// its overflow instead of trapping (a trap leaves a crash report): a
    /// cursor is any Int a caller built, and one that overflows is far.
    static func isNear(_ location: CursorLocation, _ anchor: CursorLocation) -> Bool {
        guard location.display.id == anchor.display.id else { return false }
        let dx = location.cursor.x.subtractingReportingOverflow(anchor.cursor.x)
        let dy = location.cursor.y.subtractingReportingOverflow(anchor.cursor.y)
        guard !dx.overflow, !dy.overflow else { return false }
        let xx = dx.partialValue.multipliedReportingOverflow(by: dx.partialValue)
        let yy = dy.partialValue.multipliedReportingOverflow(by: dy.partialValue)
        guard !xx.overflow, !yy.overflow else { return false }
        let sum = xx.partialValue.addingReportingOverflow(yy.partialValue)
        return !sum.overflow && Int64(sum.partialValue) <= radiusSquared
    }
}

/// What a settings change means for hover tracking (HoverTracker.restart).
public enum HoverSettingsChange: Sendable, Hashable {
    case none
    /// The smart dictionary's model, backend or keep-warm changed.
    case smartSettings
    /// The translation mode changed.
    case mode
}

extension TranslationSettings {
    /// What changed for hover tracking since `previous` (nil: nothing to
    /// compare with, as for the first settings of a session). The mode wins
    /// over the smart settings, as SetMode does more than
    /// ApplySmartDictionarySettings; the delay and the switch itself are not
    /// changes here (the tracker reads the delay on every poll, and turning
    /// translation off stops the tracking).
    public func hoverChange(from previous: TranslationSettings?) -> HoverSettingsChange {
        guard let previous else { return .none }
        if mode != previous.mode { return .mode }
        if senseModel != previous.senseModel || senseBackend != previous.senseBackend
            || senseKeepWarm != previous.senseKeepWarm {
            return .smartSettings
        }
        return .none
    }
}
