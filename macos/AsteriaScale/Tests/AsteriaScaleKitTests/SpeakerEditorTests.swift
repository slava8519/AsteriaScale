import Carbon
import Foundation
import Testing
@testable import AsteriaScaleKit

/// The speaker editor's model (roadmap 20.3 DoD 5; docs/MACOS.md T30, T31):
/// whether it opens, its keys, what it sends and what it says, without
/// AppKit.
@Suite struct SpeakerEditorTests {
    static let game = HoverGame(key: "path:/B/One/drive_c/Games/Novel/Novel.exe", displayName: "Novel.exe")
    static let point = GlobalPoint(x: 300, y: 200)

    static func speaker(name: String = "Kotori", gender: String = "unknown", gameKey: String? = game.key,
                        observed: String? = "Kotorl") -> HoverSpeaker? {
        var value: [String: JSONValue] = ["name": .string(name), "gender": .string(gender)]
        if let gameKey { value["gameKey"] = .string(gameKey) }
        if let observed { value["observedName"] = .string(observed) }
        return HoverSpeaker(.object(value))
    }

    @Test func itOpensOnlyOnACardWithASpeakerAndAGame() throws {
        #expect(SpeakerEditor.open(nil) == .failure(.noSpeaker))
        #expect(SpeakerEditor.open(SpeakerTarget(speaker: nil, game: Self.game, point: Self.point)) == .failure(.noSpeaker))
        #expect(SpeakerEditor.open(SpeakerTarget(speaker: Self.speaker(observed: nil), game: Self.game, point: Self.point))
                == .failure(.noSpeaker))
        #expect(SpeakerEditor.open(SpeakerTarget(speaker: Self.speaker(observed: " \u{A0} "), game: Self.game,
                                                 point: Self.point)) == .failure(.noSpeaker))
        #expect(SpeakerEditor.open(SpeakerTarget(speaker: Self.speaker(), game: nil, point: Self.point)) == .failure(.noGame))
        #expect(SpeakerEditor.open(SpeakerTarget(speaker: Self.speaker(gameKey: nil), game: Self.game, point: Self.point))
                == .failure(.noGame), "Core resolved the speaker without a game")
        let editor = try SpeakerEditor.open(SpeakerTarget(speaker: Self.speaker(gender: "feminine"), game: Self.game,
                                                          point: Self.point)).get()
        #expect(editor == SpeakerEditor(observedName: "Kotorl", game: Self.game, initialName: "Kotori", gender: "feminine"))
        #expect(SpeakerEditor.Refusal.noSpeaker.russian == RU.speakerNone && SpeakerEditor.Refusal.noGame.russian == RU.speakerNoGame)
        #expect(RU.speakerNone == "Под курсором нет имени говорящего")
        #expect(RU.speakerNoGame == "Не удалось определить игру — род говорящего не сохранить")
    }

    /// T31: 1, 2, 3 (main row and keypad, as Carbon numbers the physical
    /// keys) and Esc; while the name is edited the digits are text and Esc
    /// still cancels.
    @Test func keysOneTwoThreeAndEscape() {
        let choices: [(Int, SpeakerChoice)] = [(kVK_ANSI_1, .masculine), (kVK_ANSI_2, .feminine), (kVK_ANSI_3, .ignore),
                                               (kVK_ANSI_Keypad1, .masculine), (kVK_ANSI_Keypad2, .feminine),
                                               (kVK_ANSI_Keypad3, .ignore)]
        for (key, choice) in choices {
            #expect(SpeakerEditor.command(keyCode: UInt16(key), editingName: false) == .choose(choice), "\(key)")
            #expect(SpeakerEditor.command(keyCode: UInt16(key), editingName: true) == nil, "\(key) while editing")
        }
        #expect(SpeakerEditor.escapeKey == UInt16(kVK_Escape))
        #expect(SpeakerEditor.command(keyCode: UInt16(kVK_Escape), editingName: false) == .cancel)
        #expect(SpeakerEditor.command(keyCode: UInt16(kVK_Escape), editingName: true) == .cancel)
        for key in [kVK_ANSI_4, kVK_ANSI_0, kVK_Return, kVK_ANSI_G, kVK_Space] {
            #expect(SpeakerEditor.command(keyCode: UInt16(key), editingName: false) == nil, "\(key)")
        }
        #expect(SpeakerChoice.allCases.map(\.digit) == [1, 2, 3])
        #expect(SpeakerChoice.allCases.map(\.russianTitle) == ["Мужской", "Женский", "Не учитывать"])
        #expect(RU.speakerCancel == "Отмена" && RU.speakerEditorTitle == "Род говорящего")
    }

    /// What a choice sends (T30): the game, the card's observedName as it is
    /// (never the name the card shows), the name in the editor as Core keeps
    /// it, the gender; a name Core keeps nothing of is refused.
    @Test func aChoiceSendsTheObservedSpellingBack() throws {
        let editor = try SpeakerEditor.open(SpeakerTarget(speaker: Self.speaker(), game: Self.game, point: Self.point)).get()
        let expected: JSONValue = ["game": ["key": .string(Self.game.key), "displayName": "Novel.exe"],
                                   "observedName": "Kotorl", "name": "Kotori Minami", "gender": "feminine"]
        #expect(editor.request(.feminine, name: "  Kotori \t\u{A0} Minami ") == .send(payload: expected, name: "Kotori Minami"))
        #expect(editor.request(.ignore, name: "Kotori") == .send(payload: [
            "game": Self.game.json, "observedName": "Kotorl", "name": "Kotori", "gender": "ignore"], name: "Kotori"))
        for name in ["", "   ", String(repeating: "a", count: 49)] {
            #expect(editor.request(.masculine, name: name) == .refuse(RU.speakerNameInvalid), "«\(name)»")
        }
        #expect(editor.request(.masculine, name: String(repeating: "a", count: 48))
                == .send(payload: ["game": Self.game.json, "observedName": "Kotorl", "name": .string(String(repeating: "a", count: 48)),
                                   "gender": "masculine"], name: String(repeating: "a", count: 48)))
        // NFC, as Core's NormalizeSpeakerName.
        #expect(SpeakerEditor.normalizedName("Zoe\u{308}") == "Zo\u{EB}")
        #expect(SpeakerChoice.allCases.map(\.rawValue) == ["masculine", "feminine", "ignore"])
    }

    @Test func theStatusCardNamesWhatWasStored() throws {
        let editor = try SpeakerEditor.open(SpeakerTarget(speaker: Self.speaker(), game: Self.game, point: Self.point)).get()
        #expect(editor.savedStatus(name: "Kotori", choice: .feminine) == "Kotori: женский род для Novel.exe")
        #expect(editor.savedStatus(name: "Kotori", choice: .masculine) == "Kotori: мужской род для Novel.exe")
        #expect(editor.savedStatus(name: "Kotori", choice: .ignore) == "Kotori: род не учитывается для Novel.exe")
    }

    /// T31: the previous app gets the keyboard back only when this editor
    /// took it and the app has become active since.
    @Test func focusGoesBackOnlyWhereTheEditorTookIt() {
        #expect(SpeakerEditor.appToReactivate(tookKeyboard: true, appIsActive: true, previousPid: 42, ownPid: 7) == 42)
        #expect(SpeakerEditor.appToReactivate(tookKeyboard: false, appIsActive: true, previousPid: 42, ownPid: 7) == nil)
        #expect(SpeakerEditor.appToReactivate(tookKeyboard: true, appIsActive: false, previousPid: 42, ownPid: 7) == nil)
        #expect(SpeakerEditor.appToReactivate(tookKeyboard: true, appIsActive: true, previousPid: 7, ownPid: 7) == nil)
        #expect(SpeakerEditor.appToReactivate(tookKeyboard: true, appIsActive: true, previousPid: nil, ownPid: 7) == nil)
        #expect(SpeakerEditor.appToReactivate(tookKeyboard: true, appIsActive: true, previousPid: 0, ownPid: 7) == nil)
    }

    /// While the editor is open the cursor is not tracked (the tracking
    /// stops, which cancels the hover in flight and forgets the anchor).
    @Test func theTrackingPausesWhileTheEditorIsOpen() {
        #expect(RecognitionStatus.ready.tracks(hoverEnabled: true, editorOpen: false))
        #expect(!RecognitionStatus.ready.tracks(hoverEnabled: true, editorOpen: true))
        #expect(!RecognitionStatus.ready.tracks(hoverEnabled: false, editorOpen: false))
        for status in [RecognitionStatus.preparing, .idle, .disabledForCheckRun, .needsPermission(requested: false),
                       .unavailable("x")] {
            #expect(!status.tracks(hoverEnabled: true, editorOpen: false), "\(status)")
        }
    }
}

extension ProcessSuites {
    /// The editor against the fake CoreHost: the card of a hover that sent
    /// the game, a correction, then a re-edit from the card that shows the
    /// corrected name, which must still reach the spelling the next hover
    /// sends (T30).
    @Suite struct SpeakerEditorFlowTests {
        @Test func theEditorReachesTheSpellingTheNextHoverSends() async throws {
            let scratch = try Scratch("editor")
            defer { scratch.remove() }
            let supervisor = CoreSupervisor(Fake.config("normal", scratch))
            await supervisor.start()
            let hover = HoverClient(supervisor: supervisor)
            let game = HoverGame(key: "path:/B/One/drive_c/Games/Novel/Novel.exe", displayName: "Novel.exe")
            let point = GlobalPoint(x: 10, y: 10)
            func card() async -> SpeakerTarget {
                let primaries = Box<[HoverPrimary]>([])
                let selection = HoverSelection(text: "The bank was steep.", hoveredWord: "bank", characterIndex: 4,
                                               speakerName: "Kotorl")
                _ = await hover.resolve(selection, game: game) { update in
                    if case .primary(let primary) = update { primaries.mutate { $0.append(primary) } }
                }
                return SpeakerTarget(speaker: primaries.get.first?.speaker, game: game, point: point)
            }
            let client = SpeakerClient(core: supervisor)

            let seen = await card()
            let first = try SpeakerEditor.open(seen).get()
            #expect(first.initialName == "Kotorl" && first.observedName == "Kotorl" && first.gender == "unknown")
            guard case .send(let payload, let name) = first.request(.feminine, name: "Kotori") else {
                Issue.record("the correction was refused")
                await supervisor.stop()
                return
            }
            let stored = try await client.set(payload)
            #expect(name == "Kotori" && stored.name == "Kotori" && stored.gender == "feminine" && stored.observedName == "Kotorl")

            let shown = await card()
            let second = try SpeakerEditor.open(shown).get()
            #expect(second.initialName == "Kotori" && second.observedName == "Kotorl" && second.gender == "feminine")
            guard case .send(let again, _) = second.request(.masculine, name: second.initialName) else {
                Issue.record("the re-edit was refused")
                await supervisor.stop()
                return
            }
            #expect(again["observedName"] == "Kotorl", "the re-edit must send the spelling the card carried")
            _ = try await client.set(again)
            let after = await card().speaker
            #expect(after?.name == "Kotori" && after?.gender == "masculine" && after?.observedName == "Kotorl")
            await supervisor.stop()
        }
    }
}
