import Foundation
import Testing
@testable import AsteriaScaleKit

/// Who owns what the overlay shows (roadmap 20.3 DoD 7; docs/MACOS.md T37):
/// a selection hover («Проверка перевода», ⌥V) and hovers over the screen on
/// one panel, as OverlayController carries them out. A mutating call is
/// made before its `#expect`, which cannot take one.
@Suite struct OverlayStateTests {
    typealias Shown = OverlayState.Shown
    typealias Ending = OverlayState.Ending

    /// A ⌥V or «Проверка перевода» card on the panel, and the state after it.
    static func selectionCard() -> (state: OverlayState, token: Int) {
        var state = OverlayState()
        let token = state.beginSelection()
        _ = state.primary(token)
        _ = state.finish(.keep, token: token)
        return (state, token)
    }

    /// A screen hover that has shown nothing owns nothing: the tracker's
    /// hide and its own end without a message leave another hover's card
    /// up (T37), and that card stays a result for the service-loss hide;
    /// its end message replaces the card.
    @Test func aScreenHoverThatShowedNothingLeavesAnotherCardAlone() {
        let made = Self.selectionCard()
        var state = made.state
        let card = made.token
        #expect(state.shown == Shown(token: card, content: .card, screen: false) && state.isShowingResult)
        let screen = state.beginScreen()
        #expect(screen > card && state.token == screen && state.tokenIsScreen)
        #expect(!state.showsScreenHover && state.isShowingResult, "begun, nothing shown: the card is still the selection's")
        let hid = state.trackerHide()
        #expect(!hid && state.shown == Shown(token: card, content: .card, screen: false), "the cursor moved: the card stays")
        for directive in [OverlayDirective.hide, .keep] {
            var ended = state
            let ending = ended.finish(directive, token: screen)
            #expect(ending == Ending.unchanged && ended.shown == Shown(token: card, content: .card, screen: false),
                    "\(directive)")
        }
        // Its end message still replaces the card, under a token of its own.
        var messaged = state
        let ending = messaged.finish(.message("x"), token: screen)
        #expect(ending == .message && messaged.shown == Shown(token: screen + 1, content: .message, screen: true)
                && messaged.token == screen + 1)
    }

    /// A loading card never stays: a superseded selection hover's own end is
    /// dropped by the newer token, so the screen hover's end takes it down.
    @Test func aSupersededLoadingCardStillGoes() {
        for directive in [OverlayDirective.hide, .keep] {
            var state = OverlayState()
            let selection = state.beginSelection()
            #expect(state.showsLoading && !state.isShowingResult)
            let screen = state.beginScreen()
            let late = state.primary(selection)
            let ownEnd = state.finish(.keep, token: selection)
            #expect(late == nil && ownEnd == Ending.unchanged, "the older hover's card and end are dropped")
            let ending = state.finish(directive, token: screen)
            #expect(ending == .hide && state.shown == nil, "\(directive)")
        }
    }

    /// What a screen hover shows is the tracker's to hide; after that the
    /// hover shows nothing more (Windows' HideOverlay generation).
    @Test func theTrackerHidesOnlyScreenContentAndRevokesTheHover() {
        var state = OverlayState()
        let screen = state.beginScreen()
        let loading = state.loading(screen)
        #expect(loading == .hoverPoint && state.shown == Shown(token: screen, content: .loading, screen: true)
                && state.showsScreenHover)
        let hid = state.trackerHide()
        #expect(hid && state.shown == nil)
        let after = [state.loading(screen), state.primary(screen), state.phrase(screen)]
        #expect(after == [nil, nil, nil], "the cursor rested on an own window and came back: nothing more of that hover")
        // The tracker's hide before the screen hover showed anything revokes it too.
        let next = state.beginScreen()
        let hidNothing = state.trackerHide()
        let nextCard = state.primary(next)
        #expect(!hidNothing && nextCard == nil)
        // A new hover starts clean.
        let third = state.beginScreen()
        let thirdCard = state.primary(third)
        #expect(thirdCard == .hoverPoint && state.isShowingResult && state.showsScreenHover)

        // A selection card is never the tracker's, nor its end message.
        let made = Self.selectionCard()
        var selection = made.state
        let keptCard = selection.trackerHide()
        #expect(!keptCard && selection.shown == Shown(token: made.token, content: .card, screen: false))
        let failing = selection.beginSelection()
        let failed = selection.finish(.message("x"), token: failing)
        #expect(failed == .message && !selection.showsScreenHover)
        let keptMessage = selection.trackerHide()
        #expect(!keptMessage && selection.shown != nil)
        // The app's own message neither.
        selection.message()
        let keptOwn = selection.trackerHide()
        #expect(!selection.showsScreenHover && !keptOwn && selection.shown?.content == .message)
        // A screen hover's end message is.
        var screenMessage = OverlayState()
        let ending = screenMessage.beginScreen()
        let ended = screenMessage.finish(.message("x"), token: ending)
        #expect(ended == .message && screenMessage.showsScreenHover)
        let hidMessage = screenMessage.trackerHide()
        #expect(hidMessage && screenMessage.shown == nil)
    }

    /// Where a card goes: in place under its own loading card, at the
    /// hover's point when the panel shows nothing of this hover (another
    /// hover's card, or nothing).
    @Test func aCardGoesWhereItsHoverIs() {
        var state = OverlayState()
        let screen = state.beginScreen()
        let loading = state.loading(screen)
        let card = state.primary(screen)
        #expect(loading == .hoverPoint && card == .inPlace && state.shown == Shown(token: screen, content: .card, screen: true))
        let again = state.loading(screen)
        #expect(again == nil, "no loading card over this hover's own card")
        // Its loading card withheld while another hover's card shows.
        var other = Self.selectionCard().state
        let late = other.beginScreen()
        let lateCard = other.primary(late)
        #expect(lateCard == .hoverPoint && other.shown == Shown(token: late, content: .card, screen: true))
        // A selection hover's card goes in place over its loading card.
        var selection = OverlayState()
        let token = selection.beginSelection()
        let selectionCard = selection.primary(token)
        let selectionPhrase = selection.phrase(token)
        let kept = selection.finish(.keep, token: token)
        #expect(selectionCard == .inPlace && selectionPhrase == .inPlace && kept == Ending.unchanged && selection.isShowingResult)
        // A hover's own content goes at its end; a newer hover drops an older one's updates.
        var ending = OverlayState()
        let first = ending.beginScreen()
        _ = ending.loading(first)
        let firstEnd = ending.finish(.hide, token: first)
        #expect(firstEnd == .hide && ending.shown == nil)
        let second = ending.beginScreen()
        let stale = [ending.loading(first), ending.primary(first)]
        let staleEnd = ending.finish(.hide, token: first)
        #expect(stale == [nil, nil] && staleEnd == Ending.unchanged)
        let secondCard = ending.primary(second)
        #expect(secondCard == .hoverPoint)
    }

    /// A phrase line only under its own hover's card (T37): not without one,
    /// not after the tracker took it down, not for an older hover; a card
    /// that hid itself after its 8 s comes back with the line.
    @Test func aPhraseJoinsOnlyItsOwnCard() {
        var state = OverlayState()
        let screen = state.beginScreen()
        let early = state.phrase(screen)
        #expect(early == nil, "no card of this hover yet")
        _ = state.loading(screen)
        let underLoading = state.phrase(screen)
        #expect(underLoading == nil, "only its loading card")
        let card = state.primary(screen)
        let phrase = state.phrase(screen)
        #expect(card == .inPlace && phrase == .inPlace)
        // The card's own timer hid it: the phrase brings it back where the hover is.
        state.hidden()
        let back = state.phrase(screen)
        #expect(back == .hoverPoint && state.shown == Shown(token: screen, content: .card, screen: true))
        // The tracker took it down: the line never shows.
        let hid = state.trackerHide()
        let dropped = state.phrase(screen)
        #expect(hid && dropped == nil && state.shown == nil)
        // A newer hover's begin drops the older one's line.
        var newer = OverlayState()
        let old = newer.beginScreen()
        _ = newer.primary(old)
        let fresh = newer.beginScreen()
        let phrases = [newer.phrase(old), newer.phrase(fresh)]
        #expect(phrases == [nil, nil])
        #expect(newer.shown == Shown(token: old, content: .card, screen: true), "the older card stays until the tracker or its timer")
    }

    /// While the speaker editor is open (T31): the panel hides, nothing of a
    /// hover over the screen shows, its end message included (Windows checks
    /// the editor before every card), a selection hover still does, and once
    /// it closes a screen hover shows again.
    @Test func whileTheEditorIsOpenNoScreenHoverShows() {
        var state = OverlayState()
        let screen = state.beginScreen()
        _ = state.primary(screen)
        state.suspend()
        #expect(state.suspended && state.shown == nil, "the card hides")
        let inFlight = [state.loading(screen), state.primary(screen), state.phrase(screen)]
        #expect(inFlight == [nil, nil, nil] && state.shown == nil, "the hover in flight shows nothing more")
        let newer = state.beginScreen()
        let parts = [state.loading(newer), state.primary(newer), state.phrase(newer)]
        let ending = state.finish(.message("x"), token: newer)
        #expect(parts == [nil, nil, nil] && ending == Ending.unchanged && state.shown == nil && state.token == newer,
                "a newer screen hover neither, nor its end message")
        let selection = state.beginSelection()
        let card = state.primary(selection)
        #expect(card == .inPlace && state.shown == Shown(token: selection, content: .card, screen: false),
                "the user's own ⌥V or «Проверка перевода» still shows")
        state.resume()
        let after = state.beginScreen()
        let shown = state.primary(after)
        #expect(!state.suspended && shown == .hoverPoint && state.shown == Shown(token: after, content: .card, screen: true))
    }
}
