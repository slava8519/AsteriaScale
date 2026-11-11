import Foundation

/// Who owns what the overlay shows (roadmap 20.3 DoD 7; docs/MACOS.md T37):
/// the pure state behind the app's OverlayController, which carries out
/// each answer on its panel. Two kinds of hover share the one panel: a
/// selection hover («Проверка перевода», ⌥V), whose loading card goes up at
/// once, and a hover over the screen, which shows nothing until its
/// hover.loading or hover.primary passes the cursor re-check. A screen
/// hover that has shown nothing owns nothing: the cursor tracker's hide
/// and its own end without a message leave another hover's card alone
/// (Windows hides any; an end message, which HoverDriver shows only while
/// the cursor is still there, replaces it), only a loading card is never
/// left behind, and a card stays a result for the service-loss hide
/// whoever began last.
public struct OverlayState: Sendable, Equatable {
    /// What the panel shows.
    public enum Content: Sendable, Equatable {
        case loading
        case card
        case message
    }

    /// What the panel shows and whose it is.
    public struct Shown: Sendable, Equatable {
        /// The hover whose content it is (a message has a token of its own,
        /// a notice `noticeToken`).
        public let token: Int
        public let content: Content
        /// Put up by a hover over the screen (its loading card, card or end
        /// message): only that is the cursor tracker's to hide.
        public let screen: Bool

        public init(token: Int, content: Content, screen: Bool) {
            self.token = token
            self.content = content
            self.screen = screen
        }
    }

    /// Where a hover's update goes.
    public enum Placement: Sendable, Equatable {
        /// At the hover's point (the cursor when it began): nothing of this
        /// hover is on the panel, which may show another hover's card.
        case hoverPoint
        /// Where this hover's content already is; the panel only fits it.
        case inPlace
    }

    /// What the end of a hover does to the panel.
    public enum Ending: Sendable, Equatable {
        /// Nothing: an older hover's end, or what shows is another's to keep.
        case unchanged
        case hide
        /// Its end message replaces what shows, under a token of its own.
        case message
    }

    /// The newest hover; an update or the end of an older one is dropped.
    public private(set) var token = 0
    /// The newest hover is one over the screen.
    public private(set) var tokenIsScreen = false
    /// nil while the panel is hidden.
    public private(set) var shown: Shown?
    /// The hover whose card a phrase line may still join: the last one
    /// whose hover.primary reached the panel.
    private var cardToken: Int?
    /// The cursor tracker hid the newest screen hover's cards: nothing more
    /// of it shows, as Windows' HideOverlay bumps the display generation
    /// that every later card of the hover is checked against.
    private var revoked = false
    /// The speaker editor is open (T31): nothing of a hover over the screen
    /// shows, whatever its cursor re-check says, as Windows' ShowLoading and
    /// ShowTranslation check that the editor is closed. A selection hover
    /// («Проверка перевода», ⌥V) is the user's own request and still shows.
    public private(set) var suspended = false

    public init() {}

    /// What shows is a screen hover's (the tracker hides only that).
    public var showsScreenHover: Bool { shown?.screen == true }
    /// What shows is a card with a result (a service that stops being ready hides it).
    public var isShowingResult: Bool { shown?.content == .card }
    public var showsLoading: Bool { shown?.content == .loading }

    /// A selection hover: its loading card goes up at once.
    public mutating func beginSelection() -> Int {
        token += 1
        tokenIsScreen = false
        revoked = false
        shown = Shown(token: token, content: .loading, screen: false)
        return token
    }

    /// A hover over the screen: a new token and nothing shown yet, so what
    /// the panel shows stays whose it was.
    public mutating func beginScreen() -> Int {
        token += 1
        tokenIsScreen = true
        revoked = false
        return token
    }

    /// hover.loading of `token` (a screen hover's own loading card): where
    /// it goes, or nil when it is not shown (an older hover's, one whose
    /// card already shows, or one the tracker took down).
    public mutating func loading(_ token: Int) -> Placement? {
        guard token == self.token, !revoked, !screenSuspended,
              !(shown?.token == token && shown?.content == .card) else { return nil }
        shown = Shown(token: token, content: .loading, screen: tokenIsScreen)
        return .hoverPoint
    }

    /// hover.primary of `token`: where the card goes, or nil when it is not
    /// shown (an older hover's, or one the tracker took down).
    public mutating func primary(_ token: Int) -> Placement? {
        guard token == self.token, !revoked, !screenSuspended else { return nil }
        let placement = place(token)
        shown = Shown(token: token, content: .card, screen: tokenIsScreen)
        cardToken = token
        return placement
    }

    /// hover.phrase of `token`: only under this hover's own card and only
    /// while the tracker has not taken it down (T37). A card that hid itself
    /// after its 8 s comes back with the line, as the WPF card, which has no
    /// timer, would still show it.
    public mutating func phrase(_ token: Int) -> Placement? {
        guard token == self.token, !revoked, !screenSuspended, cardToken == token else { return nil }
        let placement = place(token)
        shown = Shown(token: token, content: .card, screen: tokenIsScreen)
        return placement
    }

    private func place(_ token: Int) -> Placement { shown?.token == token ? .inPlace : .hoverPoint }

    /// The newest hover is one over the screen and the editor is open.
    private var screenSuspended: Bool { suspended && tokenIsScreen }

    /// The speaker editor opens: the panel hides, whatever it showed (Windows
    /// hides the overlay), and nothing of a hover over the screen shows until
    /// `resume`.
    public mutating func suspend() {
        suspended = true
        shown = nil
    }

    /// The speaker editor closed.
    public mutating func resume() {
        suspended = false
    }

    /// The cursor tracker's hideCard: whether the panel hides (only what a
    /// screen hover put up); the newest screen hover shows nothing more.
    public mutating func trackerHide() -> Bool {
        if tokenIsScreen { revoked = true }
        guard shown?.screen == true else { return false }
        shown = nil
        return true
    }

    /// The panel hid: its timer, the end of a hover, a service that stopped
    /// being ready, the quit.
    public mutating func hidden() {
        shown = nil
    }

    /// The end of `token`'s hover with `directive`.
    public mutating func finish(_ directive: OverlayDirective, token: Int) -> Ending {
        guard token == self.token else { return .unchanged }
        switch directive {
        case .keep:
            return hideLoading()
        case .hide:
            // This hover's own content goes; a card or message another hover
            // finished stays to its own timer.
            if shown?.token == token {
                shown = nil
                return .hide
            }
            return hideLoading()
        case .message:
            // While the editor is open a screen hover's end shows nothing.
            if screenSuspended { return hideLoading() }
            self.token += 1
            shown = Shown(token: self.token, content: .message, screen: tokenIsScreen)
            return .message
        }
    }

    /// A loading card never stays: this hover's, or the one of an older
    /// hover whose own end this hover's token now drops.
    private mutating func hideLoading() -> Ending {
        guard shown?.content == .loading else { return .unchanged }
        shown = nil
        return .hide
    }

    /// A message of the app's own (⌥V's, the self-test's), not a hover's.
    public mutating func message() {
        token += 1
        tokenIsScreen = false
        revoked = false
        shown = Shown(token: token, content: .message, screen: false)
    }

    /// What a notice shows under: no hover's token (they start at 1), so a
    /// hover's end never takes a notice for its own content.
    public static let noticeToken = 0

    /// A notice of the app's own that is no hover's end and supersedes no
    /// hover: a refused ⌃⌥G (T31). It takes the panel but not the token, so
    /// a hover in flight still shows what comes after it, which replaces
    /// the notice: a screen hover that has shown nothing yet its loading
    /// card and its card; a hover whose loading card the notice covers (a
    /// ⌥V one, whose loading card went up when it started, or a screen
    /// hover whose hover.loading came) its card only, as that loading card
    /// does not come again. On Windows the refusal goes to the status line
    /// and leaves the overlay and the hover alone.
    /// That hover's end without a card of its own leaves the notice to its
    /// timer, as does the cursor tracker's hide (a notice is no screen
    /// hover's). A phrase line joins no card the notice covered: the card
    /// is gone from the panel.
    public mutating func notice() {
        cardToken = nil
        shown = Shown(token: Self.noticeToken, content: .message, screen: false)
    }
}
