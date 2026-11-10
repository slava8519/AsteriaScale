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

/// The click-through card over the game window.
///
/// Level .screenSaver (window server layer 1000) stays above a CrossOver or
/// winemac full-screen window (status level + 1); a bottle with
/// CaptureDisplaysForFullscreen sits at shielding + 1 and may need a higher
/// level (docs/MACOS.md Q3, measured in 20.3/20.4), which is why the level is
/// a preference. sharingType .none keeps the card out of other apps' window
/// reads, but ScreenCaptureKit reportedly still captures such windows on
/// macOS 15.4+, so 20.3/20.4 must also exclude our own windows in their
/// SCContentFilter.
///
/// It never stays on a loading or stale card: a hover's end always keeps a
/// card, hides it or replaces it with a message, a card hides itself after
/// 8 s and a message after 4 s, and a translation service that stops being
/// ready hides a shown card.
@MainActor
final class OverlayController {
    let panel: OverlayPanel
    let model = OverlayModel()
    private(set) var isShowingResult = false
    /// Increases with every hover; a late update of an older hover is dropped.
    private(set) var hoverToken = 0
    private var hideTask: Task<Void, Never>?

    init() {
        panel = OverlayPanel(contentRect: NSRect(x: 0, y: 0, width: 360, height: 80),
                             styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.isFloatingPanel = true
        panel.level = .screenSaver
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

    /// Starts a hover: the loading card (the WPF wording) and a new token.
    func beginHover(word: String, smart: Bool, near point: NSPoint) -> Int {
        hoverToken += 1
        model.message = nil
        model.source = word
        model.translation = smart ? RU.overlayCheckingContext : RU.overlayLookingUpDictionary
        model.badge = ""
        model.phrase = ""
        model.loading = true
        isShowingResult = false
        // No timer: the hover's end always decides (its deadline is bounded).
        present(near: point, autoHide: nil)
        return hoverToken
    }

    func apply(_ update: HoverUpdate, token: Int) {
        guard token == hoverToken else { return }
        switch update {
        case .primary(let primary):
            model.loading = false
            model.message = nil
            model.source = primary.source
            model.translation = primary.found ? primary.translation : RU.overlayNotFound
            model.badge = primary.badge
            isShowingResult = true
        case .phrase(let phrase):
            model.phrase = phrase.translation
        }
        present(near: nil, autoHide: .seconds(8))
    }

    /// The end of the hover with `token`, unless a newer hover owns the overlay.
    func finish(_ directive: OverlayDirective, token: Int, near point: NSPoint) {
        guard token == hoverToken else { return }
        switch directive {
        case .keep:
            if model.loading { hide() }
        case .hide:
            hide()
        case .message(let text):
            showMessage(text, near: point)
        }
    }

    func showMessage(_ text: String, near point: NSPoint) {
        hoverToken += 1
        model.message = text
        model.loading = false
        isShowingResult = false
        present(near: point, autoHide: .seconds(4))
    }

    func hide() {
        hideTask?.cancel()
        hideTask = nil
        panel.orderOut(nil)
        model.loading = false
        isShowingResult = false
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
