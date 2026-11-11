import Carbon.HIToolbox
import Foundation
import Testing
@testable import AsteriaScaleKit

@Suite struct DiagnosticSelectionTests {
    @Test func bracketsMarkTheHoveredWord() throws {
        let selection = try DiagnosticSelection.parse("  The [bank] was steep\n")
        #expect(selection.text == "The bank was steep")
        #expect(selection.hoveredWord == "bank")
        #expect(selection.characterIndex == 4)
    }

    @Test func indexIsUTF16LikeDotNet() throws {
        // "😀" is two UTF-16 units: C# "😀 [bank]".IndexOf("bank") after the brackets go is 3.
        #expect(try DiagnosticSelection.parse("😀 [bank]").characterIndex == 3)
        let japanese = try DiagnosticSelection.parse("日本語の[本]")
        #expect(japanese.characterIndex == 4 && japanese.hoveredWord == "本")
    }

    @Test func firstWordWithoutBrackets() throws {
        let selection = try DiagnosticSelection.parse("— Well, I'm here")
        #expect(selection.hoveredWord == "Well")
        #expect(selection.characterIndex == 2)
    }

    @Test func limits() {
        #expect(throws: DiagnosticSelectionError.empty) { _ = try DiagnosticSelection.parse("  \n ") }
        #expect(throws: DiagnosticSelectionError.tooLong) {
            _ = try DiagnosticSelection.parse(String(repeating: "a", count: 1201))
        }
        #expect(DiagnosticSelectionError.tooLong.russian == RU.diagnosticTooLong)
    }

    /// The v1 SelectionSource schema: CoreHost rejects unknown or mistyped members.
    @Test func payloadMatchesTheV1Selection() throws {
        let payload = try DiagnosticSelection.parse("He sat on the [bank] of the river.").payload
        #expect(payload == ["kind": "selection", "text": "He sat on the bank of the river.", "hoveredWord": "bank",
                            "characterIndex": 14, "sentenceStable": true])
    }
}

@Suite struct BackoffTests {
    @Test func growsAndCaps() {
        let policy = BackoffPolicy()
        #expect(policy.delay(afterConsecutiveFailures: 1) == .milliseconds(500))
        #expect(policy.delay(afterConsecutiveFailures: 3) == .seconds(2))
        #expect(policy.delay(afterConsecutiveFailures: 99) == .seconds(30))
    }
}

@Suite struct RussianStringsTests {
    static var everyString: [String] {
        RU.allFixedStrings + HotkeyAction.allCases.map(\.russianTitle)
            + [CoreState.stopped, .starting, .restarting(attempt: 1, delay: .seconds(2), reason: RU.reasonNoAnswer),
               .failed(RU.reasonCrashLoop)]
                .flatMap { [$0.russian] + ($0.details.map { [$0] } ?? []) }
            + [CloseReason.shutdown, .exited("x"), .violation("x"), .helloRejected("x"), .deadline("x"),
               .writeFailed(32), .readFailed(5), .killed].map(\.russian)
    }

    /// Rule 9 and M15: every string the app can show is Russian, never names
    /// the host «я-д-р…», and no Windows engine wording leaks onto a Mac.
    @Test func everyStringIsRussianAndNamesTheTranslation() {
        for text in Self.everyString {
            #expect(text.range(of: #"\p{Cyrillic}"#, options: .regularExpression) != nil, "not Russian: \(text)")
            #expect(!text.lowercased().contains(hostWord), "M15: \(text)")
            #expect(!text.contains("NPU") && !text.uppercased().contains("VULKAN"), "\(text)")
        }
    }

    /// Rule 9 at start-up: the alert names the reason in Russian and the
    /// error by its code, never by Foundation's or strerror's text, which is
    /// English on an English system. /dev/null/… fails without touching disk.
    @Test func startFailuresAreRussianWithTheirCode() throws {
        var lockError: InstanceLock.Failure?
        do {
            _ = try InstanceLock.acquire(at: URL(fileURLWithPath: "/dev/null/app.lock"))
        } catch {
            lockError = error
        }
        let lock = try #require(lockError)
        let lockText = RU.startLockFailed(lock)
        #expect(lockText == "Asteria Scale не запустился: не удалось открыть файл блокировки экземпляра"
                + " (open, errno \(ENOTDIR)).")
        var foldersError: (any Error)?
        do {
            try FileManager.default.createDirectory(atPath: "/dev/null/Logs", withIntermediateDirectories: true)
        } catch {
            foldersError = error
        }
        let folders = try #require(foldersError) as NSError
        let foldersText = RU.startFoldersFailed(folders)
        #expect(foldersText == "Asteria Scale не запустился: не удалось создать папки приложения в ~/Library"
                + " (\(folders.domain) \(folders.code)).")
        for text in [lockText, foldersText] {
            // Latin only in the app's name, ~/Library and the trailing code.
            let reason = text.replacingOccurrences(of: #" \([^()]*\)\.$"#, with: "", options: .regularExpression)
                .replacingOccurrences(of: RU.appName, with: "").replacingOccurrences(of: "~/Library", with: "")
            #expect(reason.range(of: "[A-Za-z]", options: .regularExpression) == nil, "\(text)")
            #expect(!text.contains(lock.description) && !text.contains(folders.localizedDescription), "\(text)")
        }
    }

    @Test func stateLinesUseTheOwnersWording() {
        #expect(CoreState.ready(CoreSessionInfo(pid: 1, hello: CoreHelloInfo([:]), initResult: nil)).russian
                == "Перевод: готов")
        #expect(CoreState.restarting(attempt: 2, delay: .milliseconds(500), reason: RU.reasonNoAnswer).russian
                == "Перевод: перезапуск…")
        #expect(CoreState.restarting(attempt: 2, delay: .milliseconds(500), reason: RU.reasonNoAnswer).details
                == "Попытка 2 через 500 мс: служба перевода не отвечает")
        #expect(CoreState.failed(RU.reasonCrashLoop).russian == "Перевод: недоступен")
        #expect(RU.menuRestartTranslation == "Перезапустить перевод")
        #expect(RU.settingsService == "Служба перевода")
    }

    /// M15 over the sources themselves: no Swift source or string catalog of
    /// the package names the host, comments included (they are English).
    /// The C# parity check scans the whole macos/ folder the same way.
    @Test func noSourceNamesTheHost() throws {
        let package = Repo.root.appendingPathComponent("macos/AsteriaScale")
        let enumerator = try #require(FileManager.default.enumerator(atPath: package.path))
        var scanned: [String] = []
        while let relative = enumerator.nextObject() as? String {
            if [".build", ".swiftpm"].contains((relative as NSString).lastPathComponent) {
                enumerator.skipDescendants()
                continue
            }
            guard ["swift", "strings", "stringsdict", "xcstrings"].contains((relative as NSString).pathExtension) else { continue }
            scanned.append(relative)
            let text = try String(contentsOf: package.appendingPathComponent(relative), encoding: .utf8)
            for (number, line) in text.split(separator: "\n", omittingEmptySubsequences: false).enumerated()
            where line.lowercased().contains(hostWord) {
                Issue.record("M15: \(relative):\(number + 1)")
            }
        }
        #expect(scanned.count >= 10)
        // The walk reaches every target folder, the Vision target and the probe included.
        for folder in ["Sources/AsteriaScaleKit/", "Sources/AsteriaScale/", "Sources/AsteriaScaleVision/",
                       "Tests/AsteriaScaleKitTests/", "Tests/AsteriaTestSleeper/", "Tests/AsteriaScaleVisionProbe/"] {
            #expect(scanned.contains { $0.hasPrefix(folder) }, "nothing scanned in \(folder)")
        }
    }
}

@Suite struct HotkeyTests {
    @Test func carbonConstantsMatch() {
        #expect(HotkeySpec.Modifiers.command.rawValue == UInt32(cmdKey))
        #expect(HotkeySpec.Modifiers.shift.rawValue == UInt32(shiftKey))
        #expect(HotkeySpec.Modifiers.option.rawValue == UInt32(optionKey))
        #expect(HotkeySpec.Modifiers.control.rawValue == UInt32(controlKey))
        #expect(HotkeyAction.toggleHover.defaultSpec.keyCode == UInt32(kVK_ANSI_T))
        #expect(HotkeyAction.showDiagnostic.defaultSpec.keyCode == UInt32(kVK_ANSI_D))
        #expect(HotkeyAction.translateClipboard.defaultSpec.keyCode == UInt32(kVK_ANSI_V))
        #expect(HotkeyAction.editSpeaker.defaultSpec.keyCode == UInt32(kVK_ANSI_G))
    }

    /// Owner decisions M14 (Option-only ⌥T, ⌥D and ⌥V) and M20 (⌃⌥G for
    /// the speaker editor, T40), the fourth action at the end: carbonId is
    /// the position in allCases, so the first three keep their ids.
    @Test func defaultsAreTheOwnersKeys() {
        #expect(HotkeyAction.allCases.map(\.defaultSpec.display) == ["⌥T", "⌥D", "⌥V", "⌃⌥G"])
        #expect(HotkeyAction.allCases.map(\.defaultSpec.modifiers) == [[.option], [.option], [.option], [.control, .option]])
        #expect(HotkeyAction.allCases.map(\.carbonId) == [1, 2, 3, 4])
        #expect(HotkeyAction.allCases.last == .editSpeaker && HotkeyAction.editSpeaker.rawValue == "editSpeaker")
        for action in HotkeyAction.allCases {
            #expect(HotkeyAction(carbonId: action.carbonId) == action)
        }
        #expect(HotkeySpec(keyCode: 0x11, modifiers: [.control, .option, .shift, .command], keyLabel: "T").display == "⌃⌥⇧⌘T")
        #expect(HotkeyAction(carbonId: 0) == nil && HotkeyAction(carbonId: 5) == nil)
    }
}

@Suite struct PathsTests {
    @Test func bundledLayoutAndArguments() {
        let paths = AppPaths.bundled(bundle: URL(fileURLWithPath: "/Applications/Asteria Scale.app"),
                                     home: URL(fileURLWithPath: "/Users/x"))
        #expect(paths.coreHost.path == "/Applications/Asteria Scale.app/Contents/Helpers/AsteriaScale.CoreHost")
        #expect(paths.rankerWorker?.path == "/Applications/Asteria Scale.app/Contents/Helpers/Ranker/AsteriaScale.RankerWorker")
        #expect(paths.dataRoot.path == "/Applications/Asteria Scale.app/Contents/Resources/Data")
        #expect(paths.userData.path == "/Users/x/Library/Application Support/AsteriaScale")
        #expect(paths.rankerModels.path == "/Users/x/Library/Application Support/AsteriaScale/Models/Ranker")
        #expect(paths.logs.path == "/Users/x/Library/Logs/AsteriaScale")
        #expect(paths.instanceLock.path == "/Users/x/Library/Application Support/AsteriaScale/Runtime/app.lock")
        #expect(paths.coreHostRecord.lastPathComponent == "corehost.pid")
        #expect(paths.coreHostArguments() == [
            "--data-root", "/Applications/Asteria Scale.app/Contents/Resources/Data",
            "--user-data", "/Users/x/Library/Application Support/AsteriaScale",
            "--log-dir", "/Users/x/Library/Logs/AsteriaScale",
            "--ranker-worker", "/Applications/Asteria Scale.app/Contents/Helpers/Ranker/AsteriaScale.RankerWorker",
            "--ranker-models", "/Users/x/Library/Application Support/AsteriaScale/Models/Ranker",
        ])
        // Models are never inside the bundle (licence M7, size, the seal).
        #expect(!paths.rankerModels.path.hasPrefix("/Applications/"))
    }

    @Test func environmentIsMinimalWithAPrivateTemp() {
        let paths = AppPaths.bundled(bundle: URL(fileURLWithPath: "/A.app"), home: URL(fileURLWithPath: "/Users/x"))
        let environment = paths.coreHostEnvironment(inherited: ["USER": "x", "SECRET": "s", "TMPDIR": "/var/t/",
                                                                "DOTNET_ROOT": "/opt/dotnet", "LOGNAME": ""])
        #expect(environment == [
            "HOME": "/Users/x", "CFFIXED_USER_HOME": "/Users/x",
            "TMPDIR": "/Users/x/Library/Application Support/AsteriaScale/Runtime/tmp/",
            "PATH": "/usr/bin:/bin:/usr/sbin:/sbin", "DOTNET_EnableDiagnostics": "0",
            "USER": "x", "DOTNET_ROOT": "/opt/dotnet",
        ])
    }

    @Test func preferencesRoundTrip() throws {
        let scratch = try Scratch("prefs")
        defer { scratch.remove() }
        let url = scratch.root.appendingPathComponent("macos-app.json")
        let lines = Box<[String]>([])
        let log: (String) -> Void = { line in lines.mutate { $0.append(line) } }
        var preferences = AppPreferences()
        preferences.overlayLevel = "shielding"
        try preferences.save(to: url)
        #expect(AppPreferences.load(from: url, log: log) == preferences)
        // Written as a JSON object keyed by action name, the form people write.
        let saved = try JSONSerialization.jsonObject(with: Data(contentsOf: url)) as? [String: Any]
        #expect((saved?["hotkeys"] as? [String: Any])?.keys.sorted()
                == ["editSpeaker", "showDiagnostic", "toggleHover", "translateClipboard"])
        #expect(AppPreferences.load(from: url.appendingPathExtension("missing"), log: log) == AppPreferences())
        #expect(AppPreferences().hotkeys[.translateClipboard]?.display == "⌥V")
        #expect(lines.get.isEmpty, "\(lines.get)")
    }

    /// macos-app.json as the owner writes it: a JSON object, either key
    /// optional; the array form an earlier build's synthesized Codable wrote
    /// still loads; a file that cannot be decoded is logged, not dropped
    /// silently.
    @Test func preferencesFromHandWrittenJSON() throws {
        let scratch = try Scratch("prefs-json")
        defer { scratch.remove() }
        let url = scratch.root.appendingPathComponent("macos-app.json")
        let lines = Box<[String]>([])
        let log: (String) -> Void = { line in lines.mutate { $0.append(line) } }
        func load(_ json: String) throws -> AppPreferences {
            try Data(json.utf8).write(to: url)
            return AppPreferences.load(from: url, log: log)
        }
        let remapped = HotkeySpec(keyCode: 9, modifiers: [.option, .command], keyLabel: "V")

        let object = try load(#"{"hotkeys":{"translateClipboard":{"keyCode":9,"modifiers":2304,"keyLabel":"V"}},"overlayLevel":"screenSaver"}"#)
        #expect(object.hotkeys == [.translateClipboard: remapped] && object.overlayLevel == "screenSaver")
        #expect(object.hotkeys[.translateClipboard]?.display == "⌥⌘V")
        #expect(try load(#"{"overlayLevel":"shielding"}"#) == AppPreferences(overlayLevel: "shielding"))
        #expect(try load(#"{"hotkeys":{}}"#).hotkeys.isEmpty)
        let array = try load(#"{"hotkeys":["translateClipboard",{"keyCode":9,"modifiers":2304,"keyLabel":"V"}],"overlayLevel":"screenSaver"}"#)
        #expect(array.hotkeys == [.translateClipboard: remapped])
        #expect(lines.get.isEmpty, "\(lines.get)")

        let broken = [
            (#"{"hotkeys":{"translateClipboardX":{"keyCode":9,"modifiers":2304,"keyLabel":"V"}}}"#, "unknown hotkey action"),
            (#"{"hotkeys":{"translateClipboard":{"keyCode":9}}}"#, "modifiers"),
            ("{not json", "dataCorrupted"),
        ]
        for (json, cause) in broken {
            lines.set([])
            #expect(try load(json) == AppPreferences())
            #expect(lines.get.count == 1 && lines.get.first?.hasPrefix("preferences: cannot use \(url.path): ") == true
                    && lines.get.first?.contains(cause) == true, "\(json): \(lines.get)")
        }
    }

    @Test func overlayLevelPreference() {
        let standard = AppPreferences().resolvedOverlayLevel
        #expect(standard.level == .screenSaver && standard.note == nil)
        let shielding = AppPreferences(overlayLevel: "shielding").resolvedOverlayLevel
        #expect(shielding.level == .shielding && shielding.note == nil)
        let unknown = AppPreferences(overlayLevel: "Shielding").resolvedOverlayLevel
        #expect(unknown.level == .screenSaver)
        #expect(unknown.note == "preferences: unknown overlayLevel \"Shielding\" (known: screenSaver, shielding); using screenSaver")
    }
}

@Suite struct ProvenanceAndHelloTests {
    @Test func provenanceWireNamesRoundTrip() {
        let wires = ["dictionary", "heuristic", "tatoeba", "resultCache", "ranker", "rankerAbstained", "rankerUnavailable"]
        #expect(Provenance.allKnown.map(\.wire) == wires)
        for wire in wires { #expect(Provenance(wire: wire).wire == wire) }
        #expect(Provenance(wire: "npu-ocr") == .unknown("npu-ocr"))
        #expect(Provenance.allKnown.filter(\.isRanker) == [.ranker, .rankerAbstained, .rankerUnavailable])
        #expect(RU.provenance(.dictionary, found: false) == "НЕ НАЙДЕНО")
    }

    static let goodHello: [String: JSONValue] = [
        "protocol": 1, "build": "b", "pid": 42, "pgid": 42, "processGroupLeader": true, "testCommands": false,
        "blockedSignals": [],
    ]

    @Test(arguments: [
        ("protocol", JSONValue.integer(2), "protocol 2, expected 1"),
        ("pid", .integer(43), "pid 43 is not the spawned 42"),
        ("pgid", .integer(1), "not the leader of its own process group (pgid 1)"),
        ("processGroupLeader", .bool(false), "not the leader of its own process group (pgid 42)"),
        ("testCommands", .bool(true), "test commands are enabled"),
        ("build", .string("other"), "build other, expected b"),
        ("blockedSignals", .array(["SIGTERM"]), "SIGTERM is blocked in its signal mask"),
        ("blockedSignals", .array(["SIGINT", "SIGCHLD"]), "SIGCHLD is blocked in its signal mask"),
        ("blockedSignals", .null, "no blockedSignals"),
    ])
    func helloChecks(_ member: String, _ value: JSONValue, _ problem: String) {
        #expect(HelloCheck.problems(CoreHelloInfo(.object(Self.goodHello)), spawnedPid: 42, expectedBuild: "b",
                                    allowTestCommands: false).isEmpty)
        var hello = Self.goodHello
        hello[member] = value
        #expect(HelloCheck.problems(CoreHelloInfo(.object(hello)), spawnedPid: 42, expectedBuild: "b",
                                    allowTestCommands: false) == [problem])
    }
}
