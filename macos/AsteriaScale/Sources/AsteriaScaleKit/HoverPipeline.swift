import Foundation

/// One hover over the screen (roadmap 20.3 DoD 7; docs/MACOS.md T19, T21,
/// T24, T27): the plan, the capture, Apple Vision and the observations
/// hover, over injected seams so the whole order is tested with stubs and the
/// fake CoreHost, and the self-test runs the same code on a frame rendered in
/// memory.
///
/// The order, as WindowsAiOcrService.RecognizeNearCursorAsync takes it:
/// 1. the permission (without it the frame source is never called);
/// 2. ocr.plan for the display and cursor: the mode, the capture bounds
///    (900x340 in «Словарь», up to 2400x720 in «Умный словарь») and the
///    cached frame's fingerprint, if CoreHost holds one there;
/// 3. the game under the cursor (hover.resolve's `game`);
/// 4. frame 1 and its fingerprint;
/// 5. in «Умный словарь», when that fingerprint is the plan's cached one,
///    hover.resolve with zero observations and no Vision at all; if CoreHost
///    answers stale_frame (the entry expired or was replaced since the plan),
///    exactly one retry with full observations of the frame already held,
///    and nothing is shown for the stale answer;
/// 6. otherwise Vision on frame 1, and in «Умный словарь» frame 2, captured
///    only after the first recognition (its delay is what lets two frames
///    of a changing screen disagree, as on Windows), and Vision on it;
/// 7. hover.resolve with the observations, whose updates (hover.loading,
///    hover.primary) reach `onUpdate`.
///
/// A hover CoreHost refuses as bad_request may have asked in a mode the user
/// has just left (CoreHost refuses a hover in another mode than its active
/// one): one new plan tells, and a changed mode runs the hover once more from
/// step 4 with it; the same mode returns the refusal. CoreHost's English
/// detail is never parsed.
///
/// Cancelling the calling task stops the hover at the next step: a request in
/// flight (ocr.plan, hover.resolve, the retry, the new plan) is abandoned
/// and CoreHost gets request.cancel for it (CoreConnection); a local step
/// (capture, Vision, the game) is not interrupted midway, but its result is
/// dropped and nothing more is sent, and so is its failure: a capture or
/// recognition that fails after the cursor left ends the hover cancelled,
/// never with a message for a place the user no longer looks at. A frame whose size is not the plan's
/// bounds ends the hover with frame_mismatch, and observations over CoreHost's
/// limits or the 900 KiB budget with observations_too_large; neither is
/// rescaled or cut short.
public struct HoverPipeline: Sendable {
    /// What a hover did, in order: app.log prints it and the tests read it.
    public enum Step: Sendable, Hashable, CustomStringConvertible {
        case plan(CapturePlan)
        case game(String?)
        /// Frame 1 or 2 of these bounds.
        case capture(Int, PixelRect)
        case recognize(Int, lines: Int)
        /// Frame 1 is the plan's cached frame: no Vision.
        case skipRecognition(FrameFingerprint)
        case resolve(TranslationMode, observations: Int, cachedFingerprint: FrameFingerprint?)
        /// stale_frame answered the zero-observation hover; the one retry follows.
        case retryAfterStaleFrame
        /// bad_request answered the hover; this is the one new plan.
        case replan(CapturePlan)

        public var description: String {
            switch self {
            case .plan(let plan): return "plan \(plan.mode.rawValue) \(plan.captureBounds)"
                + (plan.cachedFingerprint.map { " cached \($0)" } ?? "")
            case .game(let key): return "game \(key ?? "none")"
            case .capture(let index, let bounds): return "capture \(index) \(bounds)"
            case .recognize(let index, let lines): return "recognize \(index): \(lines) lines"
            case .skipRecognition(let fingerprint): return "frame \(fingerprint) is cached: no recognition"
            case .resolve(let mode, let observations, let cached):
                return "resolve \(mode.rawValue) with \(observations) observations" + (cached.map { " and cached \($0)" } ?? "")
            case .retryAfterStaleFrame: return "stale frame: retry with full observations"
            case .replan(let plan): return "replan \(plan.mode.rawValue) \(plan.captureBounds)"
            }
        }
    }

    public struct Result: Sendable, Equatable {
        public let outcome: HoverOutcome
        public let steps: [Step]

        public init(outcome: HoverOutcome, steps: [Step]) {
            self.outcome = outcome
            self.steps = steps
        }
    }

    public let client: ObservationClient
    public let frames: any FrameSource
    public let recognizer: any TextRecognizer
    public let fingerprinter: any FrameFingerprinting
    public let games: any GameResolver
    public let permission: any CapturePermission

    public init(client: ObservationClient, frames: any FrameSource, recognizer: any TextRecognizer,
                fingerprinter: any FrameFingerprinting, games: any GameResolver, permission: any CapturePermission) {
        self.client = client
        self.frames = frames
        self.recognizer = recognizer
        self.fingerprinter = fingerprinter
        self.games = games
        self.permission = permission
    }

    /// One hover at `point` (Cocoa global points) over `displays`.
    ///
    /// - Parameters:
    ///   - onPlan: each plan the hover follows (the first, and a changed one).
    ///   - onSent: each hover.resolve's request id, once written.
    ///   - onUpdate: the hover's updates in CoreHost's order.
    public func run(at point: GlobalPoint, on displays: [DisplayDescription],
                    onPlan: (@Sendable (CapturePlan) -> Void)? = nil,
                    onSent: (@Sendable (Int64) -> Void)? = nil,
                    onUpdate: @escaping @Sendable (HoverUpdate) -> Void) async -> Result {
        let run = Run(pipeline: self, onPlan: onPlan, onSent: onSent, onUpdate: onUpdate)
        let outcome = await run.hover(at: point, on: displays)
        return Result(outcome: outcome, steps: run.steps)
    }
}

/// Ends a hover early with its outcome.
private struct Stopped: Error {
    let outcome: HoverOutcome
}

/// One hover's state: the steps so far. Used by one task only.
private final class Run {
    let pipeline: HoverPipeline
    let onPlan: (@Sendable (CapturePlan) -> Void)?
    let onSent: (@Sendable (Int64) -> Void)?
    let onUpdate: @Sendable (HoverUpdate) -> Void
    private(set) var steps: [HoverPipeline.Step] = []
    /// The zero-observation hover is tried once per hover: after its one
    /// stale_frame retry (or a new plan) frames are always recognized.
    private var mayUseCachedFrame = true

    init(pipeline: HoverPipeline, onPlan: (@Sendable (CapturePlan) -> Void)?, onSent: (@Sendable (Int64) -> Void)?,
         onUpdate: @escaping @Sendable (HoverUpdate) -> Void) {
        self.pipeline = pipeline
        self.onPlan = onPlan
        self.onSent = onSent
        self.onUpdate = onUpdate
    }

    func hover(at point: GlobalPoint, on displays: [DisplayDescription]) async -> HoverOutcome {
        do throws(Stopped) {
            return try await perform(at: point, on: displays)
        } catch {
            return error.outcome
        }
    }

    private func perform(at point: GlobalPoint, on displays: [DisplayDescription]) async throws(Stopped) -> HoverOutcome {
        // Before anything else: without Screen Recording no plan is asked
        // for and the frame source is never called (DoD 20.3 item 7).
        guard pipeline.permission.isGranted() else {
            throw Stopped(outcome: .rejected(CaptureFailure.permissionDenied.coreError))
        }
        guard let location = CaptureGeometry.locate(point, on: displays) else {
            // No display holds the point: nothing to capture, nothing to show.
            throw Stopped(outcome: .empty)
        }
        try checkCancellation()
        var plan = try await self.plan(location, replanning: false)
        let game = await pipeline.games.game(at: point)
        steps.append(.game(game?.key))
        try checkCancellation()

        var replanned = false
        while true {
            let outcome = try await attempt(plan, at: location, game: game)
            guard !replanned, case .rejected(let error) = outcome, error.code == "bad_request" else { return outcome }
            replanned = true
            mayUseCachedFrame = false
            try checkCancellation()
            let fresh = try await self.plan(location, replanning: true)
            guard fresh.mode != plan.mode else { return outcome }
            plan = fresh
        }
    }

    /// ocr.plan, its bounds checked against the display: CoreHost keeps them
    /// inside it, and a capture outside it could not be the bounds' size.
    private func plan(_ location: CursorLocation, replanning: Bool) async throws(Stopped) -> CapturePlan {
        let plan: CapturePlan
        do {
            plan = try await pipeline.client.plan(display: location.display.wire, cursor: location.cursor)
        } catch {
            throw Stopped(outcome: HoverOutcome(error: error))
        }
        steps.append(replanning ? .replan(plan) : .plan(plan))
        try checkCancellation()
        guard CaptureGeometry.isInside(plan.captureBounds, display: location.display) else {
            throw Stopped(outcome: .rejected(CoreError(
                code: "bad_response", message: RU.errorText("bad_response"),
                detail: "ocr.plan's bounds \(plan.captureBounds) do not lie inside \(location.display)")))
        }
        onPlan?(plan)
        return plan
    }

    /// Steps 4-7 with one plan.
    private func attempt(_ plan: CapturePlan, at location: CursorLocation, game: HoverGame?) async throws(Stopped)
        -> HoverOutcome {
        let bounds = plan.captureBounds
        let first = try await capture(1, bounds, on: location.display)
        let firstPrint = pipeline.fingerprinter.fingerprint(of: first)
        if mayUseCachedFrame, plan.mode == .smart, let cached = plan.cachedFingerprint, cached == firstPrint {
            mayUseCachedFrame = false
            steps.append(.skipRecognition(firstPrint))
            let outcome = try await resolve(plan.mode, location, bounds, [], cached: cached, game: game)
            guard case .staleFrame = outcome else { return outcome }
            steps.append(.retryAfterStaleFrame)
            try checkCancellation()
        }
        var observations = [ObservationFrame(fingerprint: firstPrint, recognized: try await recognize(1, first, bounds))]
        if plan.observations == 2 {
            let second = try await capture(2, bounds, on: location.display)
            let secondPrint = pipeline.fingerprinter.fingerprint(of: second)
            observations.append(ObservationFrame(fingerprint: secondPrint, recognized: try await recognize(2, second, bounds)))
        }
        return try await resolve(plan.mode, location, bounds, observations, cached: nil, game: game)
    }

    private func capture(_ index: Int, _ bounds: PixelRect, on display: DisplayDescription) async throws(Stopped) -> Frame {
        try checkCancellation()
        steps.append(.capture(index, bounds))
        let frame: Frame
        do throws(CaptureFailure) {
            frame = try await pipeline.frames.capture(bounds, on: display)
        } catch .cancelled {
            throw Stopped(outcome: .cancelled)
        } catch {
            // A failure that ends after the cancellation is dropped like a
            // frame would be: its message belongs to a hover already left.
            try checkCancellation()
            throw Stopped(outcome: .rejected(error.coreError))
        }
        // The capture is not interrupted midway: its frame is dropped here.
        try checkCancellation()
        guard frame.matches(bounds) else {
            throw Stopped(outcome: .rejected(RecognitionFailure.frameMismatch(frame: frame.size, bounds: bounds.size).coreError))
        }
        return frame
    }

    private func recognize(_ index: Int, _ frame: Frame, _ bounds: PixelRect) async throws(Stopped) -> [RecognizedLine] {
        try checkCancellation()
        let lines: [RecognizedLine]
        do throws(RecognitionFailure) {
            lines = try await pipeline.recognizer.recognize(frame, capturedAt: bounds)
        } catch .cancelled {
            throw Stopped(outcome: .cancelled)
        } catch {
            // As for the capture: a failure after the cancellation is dropped.
            try checkCancellation()
            throw Stopped(outcome: .rejected(error.coreError))
        }
        try checkCancellation()
        steps.append(.recognize(index, lines: lines.count))
        return lines
    }

    private func resolve(_ mode: TranslationMode, _ location: CursorLocation, _ bounds: PixelRect,
                         _ observations: [ObservationFrame], cached: FrameFingerprint?, game: HoverGame?) async throws(Stopped)
        -> HoverOutcome {
        try checkCancellation()
        steps.append(.resolve(mode, observations: observations.count, cachedFingerprint: cached))
        let source = ObservationsSource(display: location.display.wire, captureBounds: bounds, cursor: location.cursor,
                                        observations: observations, cachedFingerprint: cached)
        return await pipeline.client.resolve(source, mode: mode, game: game, onSent: onSent, onUpdate: onUpdate)
    }

    private func checkCancellation() throws(Stopped) {
        if Task.isCancelled { throw Stopped(outcome: .cancelled) }
    }
}
