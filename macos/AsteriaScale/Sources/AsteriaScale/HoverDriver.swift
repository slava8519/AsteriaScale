import AppKit
import AsteriaScaleKit

/// The glue between the cursor, the hover pipeline and the overlay (roadmap
/// 20.3 DoD 7): every 90 ms it samples NSEvent.mouseLocation (no global
/// event monitor, no event tap, so no input-monitoring or accessibility
/// permission), feeds Kit's HoverTracker and carries out its actions; a
/// settled cursor starts HoverPipeline, whose loading card and card reach
/// the overlay only while the cursor is still at the anchor. Decisions live
/// in Kit; nothing here decides a hover.
///
/// It tracks only while AppDelegate says so: capture on (CaptureGate),
/// Screen Recording granted and no capture refused in this run, Vision
/// warmed, the translation service ready and hover translation on. In a self-test or a direct exec it never
/// tracks; the self-test calls `hover` with a frame rendered in memory.
@MainActor
final class HoverDriver {
    /// What one hover showed, for app.log and the self-test.
    struct Shown {
        let result: HoverPipeline.Result
        /// The hover.loading the overlay showed, and the texts it showed then.
        let loading: HoverLoading?
        let loadingTexts: [String]
        /// The card the overlay showed.
        let primary: HoverPrimary?
        /// "loading" and "primary", in the order the overlay showed them.
        let order: [String]
    }

    private weak var app: AppDelegate?
    private var tracker = HoverTracker(clock: SystemMonotonicClock())
    private var timer: Timer?
    private var pipeline: HoverPipeline?
    /// The hover of the tracker's current anchor.
    private var current: (anchorId: Int, task: Task<Shown, Never>)?
    /// Hovers the tracker started in this run (the self-test checks none).
    private(set) var trackedHovers = 0

    init(app: AppDelegate) { self.app = app }

    var isTracking: Bool { timer != nil }

    /// Starts or stops tracking. Stopping cancels the hover in flight, hides
    /// its card and forgets the anchor.
    func setTracking(_ on: Bool, pipeline: HoverPipeline?) {
        if on, let pipeline {
            self.pipeline = pipeline
            guard timer == nil else { return }
            let timer = Timer(timeInterval: Self.seconds(HoverTracker.pollInterval), repeats: true) { [weak self] _ in
                MainActor.assumeIsolated { self?.tick() }
            }
            RunLoop.main.add(timer, forMode: .common)
            self.timer = timer
            app?.log.write("cursor tracking: on")
            return
        }
        guard let timer else { return }
        timer.invalidate()
        self.timer = nil
        self.pipeline = nil
        perform(tracker.reset())
        app?.log.write("cursor tracking: off")
    }

    /// A settings change while tracking: a new mode or new smart dictionary
    /// settings cancel the hover in flight and forget the anchor, as Windows'
    /// coordinator resets its tracking (HoverTracker.restart decides). Not
    /// tracking, there is nothing to restart.
    func settingsChanged(_ change: HoverSettingsChange) {
        guard timer != nil, change != .none else { return }
        perform(tracker.restart(after: change))
        app?.log.write("cursor tracking: restarted (\(change))")
    }

    private func tick() {
        guard let app, let pipeline else { return }
        let delay = app.settings?.settings.hoverDelayMs ?? 700
        for action in tracker.tick(Self.sample(overlay: app.overlay.panel), delayMilliseconds: delay) {
            perform(action, pipeline: pipeline)
        }
    }

    private func perform(_ actions: [HoverTracker.Action]) {
        for action in actions { perform(action, pipeline: nil) }
    }

    private func perform(_ action: HoverTracker.Action, pipeline: HoverPipeline?) {
        guard let app else { return }
        switch action {
        case .hideCard:
            // Only what a hover over the screen put up (Kit's OverlayState
            // decides): a card of «Проверка перевода» or ⌥V stays while the
            // cursor moves or rests on the diagnostic window, also once a
            // screen hover has begun that has shown nothing yet.
            app.overlay.trackerHide()
        case .cancelHover(let anchorId):
            if let current, current.anchorId == anchorId {
                current.task.cancel()
                self.current = nil
            }
        case .startHover(let started):
            guard let pipeline else { return }
            current?.task.cancel()
            trackedHovers += 1
            let anchorId = started.anchorId
            let task = hover(pipeline, at: started.point, displays: Self.displays(), mayShow: { [weak self] in
                guard let self, let app = self.app else { return false }
                return self.tracker.mayShow(anchorId: anchorId, at: Self.sample(overlay: app.overlay.panel))
            })
            current = (anchorId, task)
        }
    }

    /// One hover through `pipeline` into the overlay; `mayShow` is asked
    /// right before the loading card, the card, a phrase line and an end
    /// message are shown (the tracker's re-check of the cursor; the
    /// self-test passes true, and false to see that nothing shows).
    /// History records only a card that was shown (hover.displayed). A
    /// refused capture is reported to the app (`screenHoverEnded`), which
    /// may stop the tracking before the end message goes up.
    @discardableResult
    func hover(_ pipeline: HoverPipeline, at point: GlobalPoint, displays: [DisplayDescription],
               mayShow: @escaping @MainActor () -> Bool) -> Task<Shown, Never> {
        guard let app else {
            return Task { Shown(result: HoverPipeline.Result(outcome: .cancelled, steps: []), loading: nil,
                                loadingTexts: [], primary: nil, order: []) }
        }
        let overlay = app.overlay!
        let near = NSPoint(x: point.x, y: point.y)
        let token = overlay.beginObservationsHover(near: near)
        let state = Box(ShownState(point: point))
        return Task { [weak self] in
            let result = await pipeline.run(at: point, on: displays, onPlan: { plan in
                DispatchQueue.main.async { state.value.smart = plan.mode == .smart }
            }, onGame: { game in
                // Queued before any update of this hover (FIFO): its card
                // finds the game the hover sent.
                DispatchQueue.main.async { state.value.game = game }
            }, onSent: { id in
                DispatchQueue.main.async { MainActor.assumeIsolated { self?.app?.hoverSent(id) } }
            }, onUpdate: { update in
                // DispatchQueue.main is FIFO: loading, primary and phrase in
                // CoreHost's order (unstructured Tasks are not ordered).
                DispatchQueue.main.async {
                    MainActor.assumeIsolated { self?.show(update, token: token, state: state, mayShow: mayShow) }
                }
            })
            // The updates were queued before the terminal resumed this task;
            // one more turn of the main queue runs every one of them first.
            await withCheckedContinuation { continuation in DispatchQueue.main.async { continuation.resume() } }
            let shown = state.value
            // An end message gets the same re-check as the cards: none for a
            // place the cursor has left. Asked before the app hears of a
            // refusal, which stops the tracking: no anchor is current then.
            let report = HoverReport.afterScreenHover(result.outcome, card: shown.card, cursorStillThere: mayShow())
            if let app = self?.app {
                app.log.write("screen hover at (\(point.x), \(point.y)): \(result.outcome); "
                              + result.steps.map(\.description).joined(separator: "; "))
                if token == app.overlay.hoverToken, let note = report.diagnosticNote {
                    app.diagnosticWindow.model.note = note
                }
                // Before the end message: stopping the tracking hides what a
                // screen hover put up, and the message must outlast that.
                app.screenHoverEnded(result.outcome)
                app.overlay.finish(report.overlay, token: token, near: near)
            }
            return Shown(result: result, loading: shown.loading, loadingTexts: shown.loadingTexts, primary: shown.primary,
                         order: shown.order)
        }
    }

    private struct ShownState {
        /// Where the hover is (Cocoa global points).
        let point: GlobalPoint
        /// The game the hover sent (the speaker editor stores a choice under it).
        var game: HoverGame?
        var smart = false
        var loading: HoverLoading?
        var loadingTexts: [String] = []
        var primary: HoverPrimary?
        /// A hover.primary came that the re-check or the overlay withheld.
        var withheld = false
        var order: [String] = []

        var card: HoverReport.ScreenCard { primary != nil ? .shown : withheld ? .withheld : .absent }
    }

    private func show(_ update: HoverUpdate, token: Int, state: Box<ShownState>, mayShow: @MainActor () -> Bool) {
        guard let app, token == app.overlay.hoverToken else { return }
        switch update {
        case .loading(let loading):
            guard mayShow(), app.overlay.showLoading(loading, smart: state.value.smart, token: token) else { return }
            state.value.loading = loading
            state.value.loadingTexts = app.overlay.model.visibleTexts
            state.value.order.append("loading")
        case .primary(let primary):
            // Not shown, not recorded: hover.displayed goes only for a card
            // the overlay showed (the overlay also withholds one of a hover
            // whose cards the tracker took down).
            guard mayShow(), app.overlay.apply(update, token: token) else {
                state.value.withheld = true
                return
            }
            state.value.primary = primary
            state.value.order.append("primary")
            app.cardShown(primary)
            // What ⌃⌥G opens the speaker editor on while this card shows.
            app.speakerCardShown(SpeakerTarget(speaker: primary.speaker, game: state.value.game, point: state.value.point),
                                 token: token)
        case .phrase:
            // Only while the cursor is still there (Windows posts every card
            // of a hover with RequireAnchorCurrent), and only under this
            // hover's own card that the tracker has not taken down (the
            // overlay's OverlayState decides).
            guard mayShow() else { return }
            app.overlay.apply(update, token: token)
        }
    }

    /// The cursor now: its point, the display under it and whether it is
    /// over one of the app's own windows (not the click-through overlay).
    static func sample(overlay: NSWindow) -> HoverTracker.Sample {
        let mouse = NSEvent.mouseLocation
        let point = GlobalPoint(x: Double(mouse.x), y: Double(mouse.y))
        return HoverTracker.Sample(point: point, location: CaptureGeometry.locate(point, on: displays()),
                                   overOwnWindow: overOwnWindow(mouse, overlay: overlay))
    }

    static func overOwnWindow(_ point: NSPoint, overlay: NSWindow) -> Bool {
        var number = NSWindow.windowNumber(at: point, belowWindowWithWindowNumber: 0)
        if number == overlay.windowNumber {
            number = NSWindow.windowNumber(at: point, belowWindowWithWindowNumber: overlay.windowNumber)
        }
        return number > 0 && NSApp.windows.contains { $0.windowNumber == number && $0.isVisible && $0 !== overlay }
    }

    /// The displays as Kit's geometry takes them: NSScreen's id and frame
    /// and the pixel size of its current mode.
    static func displays() -> [DisplayDescription] {
        NSScreen.screens.compactMap { screen in
            guard let number = screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber,
                  let mode = CGDisplayCopyDisplayMode(number.uint32Value) else { return nil }
            let frame = screen.frame
            return DisplayDescription(id: number.uint32Value,
                                      frame: PointRect(x: Double(frame.minX), y: Double(frame.minY),
                                                       width: Double(frame.width), height: Double(frame.height)),
                                      pixelWidth: mode.pixelWidth, pixelHeight: mode.pixelHeight)
        }
    }

    private static func seconds(_ duration: Duration) -> TimeInterval {
        let parts = duration.components
        return Double(parts.seconds) + Double(parts.attoseconds) / 1e18
    }
}
