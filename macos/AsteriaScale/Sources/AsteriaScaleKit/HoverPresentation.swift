import Foundation

/// What the overlay does once a hover has ended.
public enum OverlayDirective: Sendable, Equatable {
    /// Leave it as the hover's events left it: the card is shown, or a newer
    /// hover or the caller who cancelled owns the overlay now.
    case keep
    case hide
    /// A short Russian message instead of a card (it hides itself).
    case message(String)
}

/// How the app presents the end of one hover: the overlay, and the note the
/// «Проверка перевода» window shows. The overlay never stays on a loading or
/// stale card (danger zone in AGENTS.md), and CoreHost's own free text never
/// reaches it: a failed hover points at the diagnostic window instead.
public struct HoverReport: Sendable, Equatable {
    public let overlay: OverlayDirective
    /// nil leaves the window's note as it is.
    public let diagnosticNote: String?

    public init(overlay: OverlayDirective, diagnosticNote: String?) {
        self.overlay = overlay
        self.diagnosticNote = diagnosticNote
    }

    /// The codes of the app's own capture and recognition failures
    /// (CaptureFailure, RecognitionFailure), whose messages are the app's
    /// own Russian texts.
    public static let ownCodes: Set<String> = [
        "screen_capture_denied", "capture_self_missing", "capture_display_missing", "capture_failed", "capture_invalid",
        "frame_mismatch", "invalid_frame", "recognizer_failed",
    ]

    /// The end of a hover over the screen, for a cursor that is still at
    /// the hover's anchor or not (the tracker's re-check, as before the
    /// loading card and the card): a message for a place the cursor has left
    /// would appear where the user no longer looks, so the overlay hides
    /// instead; every other end stays as it is.
    public func overlay(cursorStillThere: Bool) -> OverlayDirective {
        if !cursorStillThere, case .message = overlay { return .hide }
        return overlay
    }

    /// - Parameter shownPrimary: whether a hover.primary of this hover reached the overlay.
    public static func after(_ outcome: HoverOutcome, shownPrimary: Bool) -> HoverReport {
        switch outcome {
        case .completed:
            // completed always comes with a card; without one nothing may stay on screen.
            return shownPrimary ? HoverReport(overlay: .keep, diagnosticNote: RU.diagnosticShown)
                : HoverReport(overlay: .hide, diagnosticNote: RU.diagnosticNothingToShow)
        case .empty:
            return HoverReport(overlay: .hide, diagnosticNote: RU.diagnosticNothingToShow)
        case .failed:
            return HoverReport(overlay: .message(RU.overlayFailed), diagnosticNote: RU.diagnosticFailed)
        case .superseded, .cancelled:
            return HoverReport(overlay: .keep, diagnosticNote: nil)
        case .staleFrame(let error):
            // HoverPipeline retries once with full observations before a
            // hover ends here; CoreHost's «Кадр на экране изменился.» is
            // never shown on the overlay (DoD 20.3), only noted in the
            // diagnostic window.
            return HoverReport(overlay: .hide, diagnosticNote: RU.diagnosticError(error.message, code: error.code))
        case .rejected(let error):
            switch error.code {
            case "hover_disabled":
                return HoverReport(overlay: .hide, diagnosticNote: RU.diagnosticHoverDisabled)
            case "not_ready", "connection_lost", "timeout", "shutting_down":
                // The service is restarting (or stopping); its state line says why.
                return HoverReport(overlay: .message(RU.overlayUnavailable),
                                   diagnosticNote: RU.diagnosticError(error.message, code: error.code))
            case "observations_too_large":
                // The app's own refusal: its Russian text says what happened.
                return HoverReport(overlay: .message(RU.errorText(error.code)),
                                   diagnosticNote: RU.diagnosticError(error.message, code: error.code))
            case _ where Self.ownCodes.contains(error.code):
                // The app's own capture and recognition: their Russian text
                // (RU, never CoreHost's free text) says what to do, the
                // permission one included.
                return HoverReport(overlay: .message(error.message),
                                   diagnosticNote: RU.diagnosticError(error.message, code: error.code))
            case "not_running":
                // Failed or stopped: nothing restarts it on its own, so the
                // overlay must not promise a restart; the menu has the action.
                return HoverReport(overlay: .message(RU.overlayNotRunning),
                                   diagnosticNote: RU.diagnosticError(error.message, code: error.code))
            default:
                return HoverReport(overlay: .message(RU.overlayFailed),
                                   diagnosticNote: RU.diagnosticError(error.message, code: error.code))
            }
        }
    }
}
