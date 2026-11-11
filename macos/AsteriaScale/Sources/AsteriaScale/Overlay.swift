import AppKit
import AsteriaScaleKit
import Observation
import SwiftUI

/// Never key, never main: the game keeps focus and keyboard input.
final class OverlayPanel: NSPanel {
    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }
}

@MainActor @Observable
final class OverlayModel {
    var source = ""
    var translation = ""
    var badge = ""
    var phrase = ""
    var loading = false
    var message: String?

    /// Every text the overlay shows now (the self-test checks them).
    var visibleTexts: [String] {
        if let message { return [message] }
        return [source, translation, badge, phrase].filter { !$0.isEmpty }
    }
}

struct OverlayView: View {
    let model: OverlayModel

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            if let message = model.message {
                Text(message).font(.system(size: 13))
            } else {
                HStack(alignment: .firstTextBaseline) {
                    Text(model.source).font(.system(size: 15, weight: .semibold))
                    Spacer(minLength: 8)
                    if !model.badge.isEmpty {
                        Text(model.badge).font(.system(size: 9, weight: .medium)).opacity(0.7)
                    }
                }
                Text(model.translation)
                    .font(.system(size: model.loading ? 11.5 : 13))
                    .opacity(model.loading ? 0.7 : 1)
                    .lineLimit(6)
                if !model.phrase.isEmpty {
                    Divider().opacity(0.4)
                    Text(model.phrase).font(.system(size: 13)).lineLimit(4)
                }
            }
        }
        .foregroundStyle(.white)
        .padding(10)
        .frame(width: 360, alignment: .leading)
        .background(.black.opacity(0.78), in: RoundedRectangle(cornerRadius: 8))
        .fixedSize(horizontal: false, vertical: true)
    }
}

extension OverlayLevel {
    var windowLevel: NSWindow.Level {
        switch self {
        case .screenSaver: return .screenSaver
        // The bottle's window is at shielding + 1; the card goes one above it.
        case .shielding: return NSWindow.Level(rawValue: Int(CGShieldingWindowLevel()) + 2)
        }
    }
}

/// The click-through card over the game window.
///
/// Level .screenSaver (window server layer 1000) stays above a CrossOver or
/// winemac full-screen window (status level + 1); a bottle with
/// CaptureDisplaysForFullscreen sits at shielding + 1 and may need a higher
/// level (docs/MACOS.md Q3, measured in 20.3/20.4), which is why the level is
/// a preference: "overlayLevel" in macos-app.json, "screenSaver" by default
/// or "shielding" (one above such a bottle); an unknown name is logged and
/// the default used. sharingType .none keeps the card out of other apps'
/// window reads, but the system's screen capture reportedly still captures
/// such windows on macOS 15.4+, so the capture (ScreenSource) also excludes
/// the whole app in its content filter.
///
/// It never stays on a loading or stale card: a hover's end always keeps a
/// card, hides it or replaces it with a message, a card hides itself after
/// 8 s and a message after 4 s, and a translation service that stops being
/// ready hides a shown card. Who owns what it shows is Kit's OverlayState;
/// this class carries its answers out on the panel.
@MainActor
final class OverlayController {
    let panel: OverlayPanel
    let model = OverlayModel()
    private var state = OverlayState()
    private var hideTask: Task<Void, Never>?
    /// Where the current hover's cards go (the cursor's point when it began).
    private var hoverPoint: NSPoint?

    init(level: OverlayLevel) {
        panel = OverlayPanel(contentRect: NSRect(x: 0, y: 0, width: 360, height: 80),
                             styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.isFloatingPanel = true
        panel.level = level.windowLevel
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
        panel.ignoresMouseEvents = true
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = false
        panel.hidesOnDeactivate = false
        panel.sharingType = .none
        panel.isReleasedWhenClosed = false
        panel.animationBehavior = .none
        let host = NSHostingView(rootView: OverlayView(model: model))
        host.sizingOptions = [.intrinsicContentSize]
        panel.contentView = host
    }

    var isVisible: Bool { panel.isVisible }
    /// Increases with every hover; a late update of an older hover is dropped.
    var hoverToken: Int { state.token }
    /// A card with a result shows (whoever began last).
    var isShowingResult: Bool { state.isShowingResult }
    /// The speaker editor is open: nothing of a hover over the screen shows.
    var isSuspended: Bool { state.suspended }

    /// Whether the card of the hover with `token` shows now (the speaker
    /// editor opens only on a card the user still sees).
    func showsCard(of token: Int) -> Bool { state.shown == OverlayState.Shown(token: token, content: .card, screen: true) }

    /// The speaker editor opens: the panel hides, and nothing of a hover
    /// over the screen shows until `resume` (OverlayState.suspend).
    func suspend() {
        state.suspend()
        hidePanel()
    }

    func resume() { state.resume() }

    /// Starts a hover: the loading card (the WPF wording) and a new token.
    func beginHover(word: String, smart: Bool, near point: NSPoint) -> Int {
        let token = state.beginSelection()
        model.message = nil
        model.source = word
        model.translation = smart ? RU.overlayCheckingContext : RU.overlayLookingUpDictionary
        model.badge = ""
        model.phrase = ""
        model.loading = true
        hoverPoint = point
        // No timer: the hover's end always decides (its deadline is bounded).
        present(near: point, autoHide: nil)
        return token
    }

    /// Starts a hover over the screen: a new token and nothing shown yet, so
    /// a card of «Проверка перевода» or ⌥V that still shows stays theirs.
    /// Its loading card comes with hover.loading (showLoading), as on
    /// Windows, and only if the cursor is still at the anchor then.
    func beginObservationsHover(near point: NSPoint) -> Int {
        hoverPoint = point
        return state.beginScreen()
    }

    /// hover.loading of an observations hover: what the Windows loading card
    /// shows (the line in «Словарь», the word in «Умный словарь») with the
    /// WPF wording, unless a card of this hover already shows; whether it
    /// was shown.
    @discardableResult
    func showLoading(_ loading: HoverLoading, smart: Bool, token: Int) -> Bool {
        guard state.loading(token) != nil else { return false }
        model.message = nil
        model.source = loading.source
        model.translation = smart ? RU.overlayCheckingContext : RU.overlayLookingUpDictionary
        model.badge = ""
        model.phrase = ""
        model.loading = true
        // No timer: the hover's end always decides (its deadline is bounded).
        present(near: hoverPoint, autoHide: nil)
        return true
    }

    /// A hover's card or phrase line; whether it reached the panel.
    @discardableResult
    func apply(_ update: HoverUpdate, token: Int) -> Bool {
        let placement: OverlayState.Placement?
        switch update {
        case .loading:
            // A selection hover's loading card is already up (beginHover);
            // an observations hover shows its own through showLoading, with
            // the mode of its plan.
            return false
        case .primary(let primary):
            placement = state.primary(token)
            guard placement != nil else { return false }
            model.loading = false
            model.message = nil
            model.source = primary.source
            model.translation = primary.found ? primary.translation : RU.overlayNotFound
            model.badge = primary.badge
            // A card without a phrase of its own shows none of an earlier
            // card's (Windows collapses the line for every such card).
            model.phrase = ""
        case .phrase(let phrase):
            placement = state.phrase(token)
            guard placement != nil else { return false }
            model.phrase = phrase.translation
        }
        // A card whose hover has nothing on the panel yet (its loading card
        // was not shown, or it hid) goes to the hover's point.
        present(near: placement == .inPlace ? nil : hoverPoint, autoHide: .seconds(8))
        return true
    }

    /// The end of the hover with `token`, unless a newer hover owns the overlay.
    func finish(_ directive: OverlayDirective, token: Int, near point: NSPoint) {
        switch state.finish(directive, token: token) {
        case .unchanged:
            return
        case .hide:
            hidePanel()
        case .message:
            // The hover's own end: a screen hover's message stays its own.
            if case .message(let text) = directive { presentMessage(text, near: point) }
        }
    }

    /// A message of its own (⌥V, the self-test), not a screen hover's.
    func showMessage(_ text: String, near point: NSPoint) {
        state.message()
        presentMessage(text, near: point)
    }

    private func presentMessage(_ text: String, near point: NSPoint) {
        model.message = text
        model.loading = false
        present(near: point, autoHide: .seconds(4))
    }

    /// The cursor tracker's hideCard: only what a hover over the screen put
    /// up hides, and the newest such hover shows nothing more.
    func trackerHide() {
        if state.trackerHide() { hidePanel() }
    }

    func hide() {
        state.hidden()
        hidePanel()
    }

    private func hidePanel() {
        hideTask?.cancel()
        hideTask = nil
        panel.orderOut(nil)
        model.loading = false
    }

    private func present(near point: NSPoint?, autoHide: Duration?) {
        if let point { place(near: point) } else { fitToContent() }
        panel.orderFrontRegardless()
        hideTask?.cancel()
        hideTask = nil
        if let autoHide {
            hideTask = Task { @MainActor [weak self] in
                try? await Task.sleep(for: autoHide)
                if !Task.isCancelled { self?.hide() }
            }
        }
    }

    private func fitToContent() {
        guard let size = panel.contentView?.fittingSize else { return }
        var frame = panel.frame
        frame.origin.y += frame.height - size.height
        frame.size = size
        panel.setFrame(frame, display: true)
    }

    /// Below and to the right of the point, inside the visible frame of its screen.
    private func place(near point: NSPoint) {
        let size = panel.contentView?.fittingSize ?? panel.frame.size
        let screen = NSScreen.screens.first { NSMouseInRect(point, $0.frame, false) } ?? NSScreen.main
        let visible = screen?.visibleFrame ?? NSRect(x: 0, y: 0, width: 1440, height: 900)
        var origin = NSPoint(x: point.x + 16, y: point.y - 20 - size.height)
        origin.x = min(max(origin.x, visible.minX + 4), visible.maxX - size.width - 4)
        if origin.y < visible.minY + 4 { origin.y = point.y + 20 }
        origin.y = min(origin.y, visible.maxY - size.height - 4)
        panel.setFrame(NSRect(origin: origin, size: size), display: true)
    }
}
