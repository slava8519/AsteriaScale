import Foundation
import Testing
@testable import AsteriaScaleKit

/// What the menu-bar app (Sources/AsteriaScale) reads and decides through the
/// Kit: settings, model status, CoreHost's messages, the end of a hover, the
/// log, the bundle id and the packaging files build-app.sh uses.
@Suite struct AppSupportTests {
    static let settingsObject: JSONValue = [
        "hoverEnabled": false, "hoverDelayMs": 700, "mode": "smart",
        "sense": ["backend": "cpu", "model": "consec", "sourceLanguage": "auto", "keepWarm": true],
        "phrase": ["mode": "off", "backend": "cpu"],
    ]

    @Test func settingsDecodeAndPatches() throws {
        let snapshot = try #require(SettingsSnapshot(result: ["settings": Self.settingsObject, "revision": 4]))
        #expect(snapshot.revision == 4)
        #expect(snapshot.settings == TranslationSettings(hoverEnabled: false, hoverDelayMs: 700, mode: "smart",
                                                         senseModel: "consec", senseBackend: "cpu", senseKeepWarm: true))
        #expect(snapshot.settings.translationMode == .smart)
        #expect(SettingsSnapshot(result: ["settings": Self.settingsObject]) == nil, "no revision")
        #expect(TranslationSettings(["hoverEnabled": true]) == nil)
        // A settings.json written on Windows may hold a mode the app does not know.
        var windows = try #require(TranslationSettings(Self.settingsObject))
        windows.mode = "translategemma"
        #expect(windows.translationMode == nil)

        // CoreHost's SettingsPatch schema: absent members keep their value.
        #expect(SettingsPatch().payload == .object([:]))
        #expect(SettingsPatch(hoverEnabled: true, hoverDelayMs: 900, mode: .dictionary, senseModel: "consec",
                              senseBackend: "cpu").payload
                == ["hoverEnabled": true, "hoverDelayMs": 900, "mode": "dictionary",
                    "sense": ["model": "consec", "backend": "cpu"]])
        #expect(SettingsPatch(mode: .smart).isApplied(to: snapshot.settings))
        #expect(!SettingsPatch(hoverEnabled: true, mode: .smart).isApplied(to: snapshot.settings))
        #expect(TranslationSettings.delayRange == 350...1500)
        #expect(TranslationMode.allCases.map(\.russianTitle) == [RU.menuModeSmart, RU.menuModeDictionary])
    }

    @Test func modelsStatusAndTheMenuLine() throws {
        func status(_ sense: JSONValue) -> ModelsStatus? {
            ModelsStatus(["dictionary": ["loaded": true, "entries": 881924, "sources": 12, "version": "v",
                                         "morphologyEntries": 154527, "senseMetadataKeys": 0],
                          "sense": sense, "phrase": ["available": false, "requested": false]])
        }
        let running = try #require(status(["inUse": true, "provider": "cpu", "model": "consec", "keepWarm": true,
                                           "running": true, "pid": 4242, "activeProvider": "cpu",
                                           "modelVersion": .string(SenseIdentity.consec)]))
        #expect(running.dictionaryLoaded && running.dictionaryEntries == 881924 && running.morphologyEntries == 154527)
        #expect(running.sense.pid == 4242 && running.sense.modelVersion == SenseIdentity.consec)
        #expect(running.senseLine == "Модель значений: ConSeC, CPU — загружена")
        let idle = try #require(status(["inUse": true, "provider": "cpu", "model": "glossbert", "keepWarm": false,
                                        "running": false]))
        #expect(idle.senseLine == "Модель значений: GlossBERT, CPU — не загружена")
        #expect(try #require(status(["inUse": false, "provider": "cpu", "model": "consec", "keepWarm": true,
                                     "running": false])).senseLine == RU.senseNotInUse)
        // A worker error is CoreHost's own Russian sentence with the code, never a path.
        let failed = try #require(status(["inUse": true, "provider": "cpu", "model": "consec", "keepWarm": true,
                                          "running": false,
                                          "error": ["code": "consec_bundle_missing",
                                                    "message": "Модель значений ConSeC не установлена (consec_bundle_missing).",
                                                    "detail": "/Users/x/Models/Ranker/Consec"]]))
        #expect(failed.sense.errorCode == "consec_bundle_missing")
        #expect(failed.senseLine == "Модель значений ConSeC не установлена (consec_bundle_missing).")
        #expect(status(["inUse": true]) == nil)
    }

    @Test func serviceMessagesStayInTheirLog() throws {
        let at = try #require(Calendar.current.date(from: DateComponents(year: 2026, month: 11, day: 10, hour: 9,
                                                                         minute: 5, second: 7)))
        let free = try #require(ServiceMessage(CoreEvent(name: "status", ref: nil,
                                                         payload: ["area": "translation", "text": "Словарь готов"]), at: at))
        #expect(free.area == "translation" && free.state == nil && free.code == nil)
        #expect(free.line == "09:05:07  Словарь готов")
        let coded = try #require(ServiceMessage(CoreEvent(name: "status", ref: nil, payload: [
            "area": "hover", "state": "error", "code": "hover_failed", "text": "Не удалось перевести текст под курсором."]),
            at: at))
        #expect(coded.line == "09:05:07  Не удалось перевести текст под курсором. (hover_failed)")
        #expect(ServiceMessage(CoreEvent(name: "status", ref: 3, payload: ["area": "x", "text": "y"])) == nil)
        #expect(ServiceMessage(CoreEvent(name: "test.tick", ref: nil, payload: ["area": "x", "text": "y"])) == nil)
        var log = ServiceMessageLog(capacity: 3)
        for _ in 0..<5 { log.append(free) }
        log.append(coded)
        #expect(log.messages.count == 3 && log.messages.last == coded)
    }

    /// The overlay never keeps a loading or stale card, and CoreHost's free
    /// text never reaches it; the diagnostic window says what happened.
    @Test func everyHoverEndHasAPresentation() {
        #expect(HoverReport.after(.completed, shownPrimary: true) == HoverReport(overlay: .keep, diagnosticNote: RU.diagnosticShown))
        #expect(HoverReport.after(.completed, shownPrimary: false).overlay == .hide)
        #expect(HoverReport.after(.empty, shownPrimary: false) == HoverReport(overlay: .hide,
                                                                             diagnosticNote: RU.diagnosticNothingToShow))
        #expect(HoverReport.after(.failed, shownPrimary: false)
                == HoverReport(overlay: .message(RU.overlayFailed), diagnosticNote: RU.diagnosticFailed))
        #expect(RU.overlayFailed == "Перевод не выполнен — подробности в окне «Проверка перевода»")
        #expect(HoverReport.after(.superseded, shownPrimary: true) == HoverReport(overlay: .keep, diagnosticNote: nil))
        #expect(HoverReport.after(.cancelled, shownPrimary: false) == HoverReport(overlay: .keep, diagnosticNote: nil))
        let disabled = CoreError(code: "hover_disabled", message: "Перевод текста выключен — включите его в меню.")
        #expect(HoverReport.after(.rejected(disabled), shownPrimary: false)
                == HoverReport(overlay: .hide, diagnosticNote: "Перевод текста выключен — включите его в меню"))
        for code in ["not_ready", "connection_lost", "timeout", "shutting_down"] {
            let error = CoreError(code: code, message: RU.errorText(code))
            #expect(HoverReport.after(.rejected(error), shownPrimary: true)
                    == HoverReport(overlay: .message(RU.overlayUnavailable),
                                   diagnosticNote: "\(RU.errorText(code)) (\(code))"))
        }
        // A failed or stopped service restarts only from the menu: no promise of a restart.
        #expect(HoverReport.after(.rejected(CoreError.notRunning), shownPrimary: false)
                == HoverReport(overlay: .message(RU.overlayNotRunning),
                               diagnosticNote: "\(RU.errorText("not_running")) (not_running)"))
        #expect(RU.overlayNotRunning == "Перевод недоступен — перезапустите перевод из меню")
        #expect(!RU.overlayNotRunning.contains("перезапускается"))
        let other = CoreError(code: "bad_request", message: RU.errorText("bad_request"))
        #expect(HoverReport.after(.rejected(other), shownPrimary: false).overlay == .message(RU.overlayFailed))
    }

    @Test func appLogAppendsAndRotates() throws {
        let scratch = try Scratch("applog")
        defer { scratch.remove() }
        let url = scratch.root.appendingPathComponent("app.log")
        let log = AppLog(url: url, maxBytes: 200)
        log.write("first «перевод»")
        log.flush()
        let first = try String(contentsOf: url, encoding: .utf8)
        #expect(first.hasSuffix(" first «перевод»\n") && first.count < 80)
        for index in 0..<10 { log.write("line \(index)") }
        log.flush()
        let rotated = try String(contentsOf: url.appendingPathExtension("1"), encoding: .utf8)
        let current = try String(contentsOf: url, encoding: .utf8)
        #expect(current.hasSuffix(" line 9\n"))
        #expect(rotated.utf8.count <= 200 && current.utf8.count <= 200)
        #expect(scratch.entries(scratch.root) == ["app.log", "app.log.1", "cwd", "home", "home/Library",
                                                  "home/Library/Application Support", "tmp"])
    }

    @Test func pathsWithARepositoryModelFolder() throws {
        let scratch = try Scratch("paths")
        defer { scratch.remove() }
        let models = scratch.root.appendingPathComponent("repo/src/AsteriaScale.RankerWorker/Models")
        let paths = AppPaths.bundled(bundle: URL(fileURLWithPath: "/Applications/Asteria Scale.app"), home: scratch.home,
                                     rankerModels: models)
        #expect(paths.rankerModels == models)
        #expect(paths.defaultRankerModels.path == scratch.home.path + "/Library/Application Support/AsteriaScale/Models/Ranker")
        #expect(paths.coreHostArguments().suffix(2) == ["--ranker-models", models.path])
        try paths.createUserDirectories()
        #expect(FileManager.default.fileExists(atPath: paths.defaultRankerModels.path))
        #expect(!FileManager.default.fileExists(atPath: models.path), "a named model folder is read in place, never created")
        #expect(paths.sweepPrefixes == ["/Applications/Asteria Scale.app/Contents/Helpers",
                                        "/Applications/Asteria Scale.app/Contents/Helpers/Ranker",
                                        paths.userData.path + "/Models/TranslateGemma"])
    }

    /// The identity the self-test demands is the one the macOS subset pins.
    @Test func consecIdentityMatchesTheSubset() throws {
        let source = try String(contentsOf: Repo.root.appendingPathComponent("tests/AsteriaScale.Tests.MacOS/MacWorkerTests.cs"),
                                encoding: .utf8)
        let start = try #require(source.range(of: "ExpectedConsecModelVersion ="))
        let end = try #require(source[start.upperBound...].range(of: ";"))
        let literals = source[start.upperBound..<end.lowerBound].split(separator: "\"", omittingEmptySubsequences: false)
            .enumerated().filter { $0.offset % 2 == 1 }.map(\.element)
        #expect(literals.joined() == SenseIdentity.consec)
    }

    /// Owner decision M13 in every place that names the bundle id, and the
    /// entitlements of DoD 8.
    @Test func bundleIdAndEntitlements() throws {
        #expect(AppIdentity.bundleIdentifier == "io.github.slava8519.AsteriaScale")
        let bundle = Repo.root.appendingPathComponent("macos/AsteriaScale/Bundle")
        func plist(_ relative: String) throws -> [String: Any] {
            let data = try Data(contentsOf: bundle.appendingPathComponent(relative))
            return try #require(try PropertyListSerialization.propertyList(from: data, format: nil) as? [String: Any])
        }
        let info = try plist("Info.plist")
        #expect(info["CFBundleIdentifier"] as? String == AppIdentity.bundleIdentifier)
        #expect(info["CFBundleExecutable"] as? String == "AsteriaScale")
        #expect(info["LSUIElement"] as? Bool == true && info["LSMinimumSystemVersion"] as? String == "14.0")
        #expect(info["CFBundleDevelopmentRegion"] as? String == "ru")
        #expect(info["AsteriaCoreHostBuild"] as? String == "@COREHOST_BUILD@")
        #expect(try plist("Entitlements/app.plist").isEmpty)
        #expect(try plist("Entitlements/corehost.plist").keys.sorted() == ["com.apple.security.cs.allow-jit"])
        #expect(try plist("Entitlements/ranker-worker.plist").keys.sorted()
                == ["com.apple.security.cs.allow-jit", "com.apple.security.cs.disable-library-validation"])
        let script = try String(contentsOf: Repo.root.appendingPathComponent("tools/macos/build-app.sh"), encoding: .utf8)
        #expect(script.contains("\nbundle_id=\(AppIdentity.bundleIdentifier)\n"))
    }
}

extension ProcessSuites {
    @Suite struct SettingsClientTests {
        /// A stale revision: the fake answers conflict, the client reads the
        /// current settings and sends the same patch once more.
        @Test func conflictRefreshesAndRetriesOnce() async throws {
            let scratch = try Scratch("settings")
            defer { scratch.remove() }
            var config = Fake.config("normal", scratch)
            config.initialize = true
            let supervisor = CoreSupervisor(config)
            await supervisor.start()
            guard case .ready(let session) = await supervisor.state else {
                Issue.record("not ready")
                return
            }
            let initial = try #require(session.initResult.flatMap { SettingsSnapshot(result: $0) })
            #expect(initial.revision == 1 && !initial.settings.hoverEnabled)
            let client = SettingsClient(supervisor: supervisor)
            await client.adopt(SettingsSnapshot(settings: initial.settings, revision: 0))
            let updated = try await client.update(SettingsPatch(hoverEnabled: true, mode: .dictionary))
            #expect(updated.revision == 2 && updated.settings.hoverEnabled && updated.settings.translationMode == .dictionary)
            #expect(await client.snapshot == updated)
            // An older snapshot never replaces a newer one.
            await client.adopt(initial)
            #expect(await client.snapshot == updated)
            #expect(try await client.refresh() == updated)

            let hover = HoverClient(supervisor: supervisor)
            let sent = Box<[Int64]>([])
            let updates = Box<[HoverUpdate]>([])
            let outcome = await hover.resolve(try DiagnosticSelection.parse("The [bank] was steep"),
                                              onSent: { id in sent.mutate { $0.append(id) } },
                                              onUpdate: { update in updates.mutate { $0.append(update) } })
            #expect(outcome == .completed)
            guard case .primary(let primary)? = updates.get.first else {
                Issue.record("no primary")
                return
            }
            #expect(sent.get == [primary.hoverId], "onSent names the request the events belong to")
            await supervisor.stop()
        }
    }
}
