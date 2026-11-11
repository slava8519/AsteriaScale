import Foundation

/// A gender the speaker editor stores: speaker.set's `gender`.
public enum SpeakerChoice: String, CaseIterable, Sendable {
    case masculine
    case feminine
    case ignore

    /// The editor's button (rule 9).
    public var russianTitle: String {
        switch self {
        case .masculine: return RU.speakerMasculine
        case .feminine: return RU.speakerFeminine
        case .ignore: return RU.speakerIgnore
        }
    }

    /// The editor's key for it (T31): 1, 2, 3 in the button order.
    public var digit: Int {
        switch self {
        case .masculine: return 1
        case .feminine: return 2
        case .ignore: return 3
        }
    }
}

/// The card a speaker choice is made on: the speaker a hover over the
/// screen showed, the game that hover sent (T29; nil when its resolver named
/// none) and where the hover was. HoverDriver records it when the card is
/// shown, so ⌃⌥G stores the choice under the game Core resolved the speaker
/// for, as Windows keeps the hover's speaker target.
public struct SpeakerTarget: Sendable, Equatable {
    public let speaker: HoverSpeaker?
    public let game: HoverGame?
    public let point: GlobalPoint

    public init(speaker: HoverSpeaker?, game: HoverGame?, point: GlobalPoint) {
        self.speaker = speaker
        self.game = game
        self.point = point
    }
}

/// The speaker editor «Род говорящего» as pure state (roadmap 20.3 DoD 5;
/// docs/MACOS.md T17, T30, T31): whether it opens on a card, what its keys
/// do and what it sends, so all of it is tested without AppKit. The app's
/// panel shows it and carries its answers out.
///
/// It sends speaker.set with the hover's game, the card's observedName as
/// it is (the spelling the text source saw, which the next hover reads,
/// however the card now spells the name, T30) and the name in the editor,
/// which may correct the spelling.
public struct SpeakerEditor: Sendable, Equatable {
    /// The spelling the text source saw, sent back unchanged.
    public let observedName: String
    public let game: HoverGame
    /// The name the editor starts with: the card's, which is the corrected
    /// spelling once the user gave one.
    public let initialName: String
    /// The card's gender (unknown, masculine, feminine or ignore).
    public let gender: String

    /// Why the editor does not open.
    public enum Refusal: Error, Sendable, Equatable {
        /// No card of a hover over the screen shows, or its text named no speaker.
        case noSpeaker
        /// The hover could name no game, so there is nothing to store the choice under.
        case noGame

        public var russian: String {
            switch self {
            case .noSpeaker: return RU.speakerNone
            case .noGame: return RU.speakerNoGame
            }
        }
    }

    /// The editor for `target`, or why there is none: no card, no speaker or
    /// no observedName to send back is no speaker; no game, or a speaker
    /// Core resolved without one, is no game.
    public static func open(_ target: SpeakerTarget?) -> Result<SpeakerEditor, Refusal> {
        guard let speaker = target?.speaker, let observed = speaker.observedName,
              normalizedName(observed) != nil else { return .failure(.noSpeaker) }
        guard let game = target?.game, speaker.gameKey != nil else { return .failure(.noGame) }
        return .success(SpeakerEditor(observedName: observed, game: game, initialName: speaker.name,
                                      gender: speaker.gender))
    }

    /// What a key does in the editor (T31).
    public enum Command: Sendable, Equatable {
        case choose(SpeakerChoice)
        case cancel
    }

    /// Carbon's virtual key codes (Events.h): the physical keys 1, 2 and 3
    /// of the main row and of the keypad, whatever the layout types there,
    /// and Escape.
    public static let escapeKey: UInt16 = 0x35
    static let digitKeys: [UInt16: SpeakerChoice] = [0x12: .masculine, 0x13: .feminine, 0x14: .ignore,
                                                     0x53: .masculine, 0x54: .feminine, 0x55: .ignore]

    /// The command of a key press: Escape cancels, also while the name is
    /// being edited (as on Windows); 1, 2 and 3 choose, unless the name is
    /// being edited, where they are text. Nil passes the key on.
    public static func command(keyCode: UInt16, editingName: Bool) -> Command? {
        if keyCode == escapeKey { return .cancel }
        guard !editingName, let choice = digitKeys[keyCode] else { return nil }
        return .choose(choice)
    }

    /// What choosing sends: the speaker.set payload and the name it stores,
    /// or the Russian reason it sends nothing (a name Core would store
    /// nothing under).
    public enum Request: Sendable, Equatable {
        case send(payload: JSONValue, name: String)
        case refuse(String)
    }

    public func request(_ choice: SpeakerChoice, name: String) -> Request {
        guard let name = Self.normalizedName(name) else { return .refuse(RU.speakerNameInvalid) }
        let payload: JSONValue = ["game": game.json, "observedName": .string(observedName), "name": .string(name),
                                  "gender": .string(choice.rawValue)]
        return .send(payload: payload, name: name)
    }

    /// Core's speaker-name rule (SpeakerGenderPreferences.NormalizeSpeakerName):
    /// NFC, every run of white space one space, none at either end, at most
    /// 48 UTF-16 units (SpeakerMetadataParser.MaximumNameLength); nil when
    /// Core would keep nothing (CoreHost then refuses speaker.set).
    public static let maxNameLength = 48

    public static func normalizedName(_ text: String) -> String? {
        var result = ""
        var pendingSpace = false
        for scalar in text.precomposedStringWithCanonicalMapping.unicodeScalars {
            if scalar.properties.isWhitespace {
                pendingSpace = !result.isEmpty
                continue
            }
            if pendingSpace {
                result.unicodeScalars.append(" ")
                pendingSpace = false
            }
            result.unicodeScalars.append(scalar)
        }
        return result.isEmpty || result.utf16.count > maxNameLength ? nil : result
    }

    /// The status card after a saved choice: «Kotori: женский род для Novel.exe».
    public func savedStatus(name: String, choice: SpeakerChoice) -> String {
        RU.speakerSaved(name: name, choice: choice, game: game.displayName)
    }

    /// The app to give the keyboard back to when the editor closes (T31):
    /// the one active when it opened, and only when this editor took the
    /// keyboard (its panel became key) and the app has become active since,
    /// so a close never pulls focus to an app the user has left. The panel
    /// does not activate the app, so normally the previous app keeps being
    /// active and nothing is done.
    public static func appToReactivate(tookKeyboard: Bool, appIsActive: Bool, previousPid: Int32?,
                                       ownPid: Int32) -> Int32? {
        guard tookKeyboard, appIsActive, let previousPid, previousPid > 0, previousPid != ownPid else { return nil }
        return previousPid
    }
}

/// speaker.set over the translation service: the speaker as it now
/// resolves. Throws CoreError (bad_request, settings_save_failed, not_ready,
/// bad_response for an answer without a speaker).
public struct SpeakerClient: Sendable {
    public let core: any CoreRequesting

    public init(core: any CoreRequesting) { self.core = core }

    public func set(_ payload: JSONValue) async throws -> HoverSpeaker {
        let result = try await core.request("speaker.set", payload, deadline: CoreDeadlines.settings, cancelInHost: false,
                                            maxLineBytes: nil, onSent: nil, onEvent: nil)
        guard let speaker = HoverSpeaker(result["speaker"]) else { throw ObservationClient.badResponse("speaker.set", result) }
        return speaker
    }
}

extension RecognitionStatus {
    /// Whether the cursor is tracked: recognition allows it, hover
    /// translation is on and the speaker editor is closed (while it is open
    /// the tracking pauses, as Windows stops its timer, T31).
    public func tracks(hoverEnabled: Bool, editorOpen: Bool) -> Bool {
        allowsTracking && hoverEnabled && !editorOpen
    }
}
