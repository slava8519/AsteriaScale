import AppKit
import AsteriaScaleKit
import AsteriaScaleVision
import Carbon.HIToolbox
import Darwin

/// `AsteriaScale --self-test` (run through tools/macos/build-app.sh
/// --self-test): a scripted launch of the bundled app in a throwaway home that
/// checks what the Swift suite cannot (the window server, Carbon, the real
/// Apple Vision on a frame rendered in memory, the bundled CoreHost and ranker
/// worker, faults while a hover is in flight), prints one
/// `selftest <name>: ok|FAIL|note` line per check and exits 0 or 1. It never
/// reads the pasteboard (hotkey presses are only recorded for the whole
/// run), never activates the app and asks for no permission; the binary is
/// started directly, not through open(1), so the terminal stays the
/// responsible process. It ends the way launchd and `kill` end the app, with
/// SIGTERM to itself, and makes its last checks once that quit has stopped
/// CoreHost. A 120 s watchdog on a background queue (a hung main thread cannot
/// stop it) ends a hung run with exit 1 (docs/MACOS.md T38): the Vision
/// warm-up, the first step, compiles Vision's models in the fresh home, about
/// 24 s on macOS 27 (T25; docs/DEVELOPMENT.md, «Проба Vision»).
/// It never captures the screen: Vision reads only frames rendered here.
@MainActor
final class SelfTest {
    private let app: AppDelegate
    private var failures = 0
    private var checks = 0
    private let started = ContinuousClock.now
    private var synthesizing = false
    private var synthesized: [HotkeyAction] = []
    private var realPresses: [HotkeyAction] = []
    /// Every text the overlay showed during the run.
    private var overlayTexts: Set<String> = []
    /// The Vision warm-up, started before anything else and in parallel with
    /// CoreHost's start (T38): its duration and what it read.
    private var visionWarmUp: Task<(Duration, Result<[RecognizedLine], RecognitionFailure>), Never>?
    /// The 2x fixture and what Vision read in it, for ocr.select.
    private var recognizedFixture: (fixture: RenderedFixture, lines: [RecognizedLine])?

    /// The internal word for the host (M15), spelled with escapes so that no
    /// source line contains it; no visible text may contain it.
    private static let hostWord = "\u{044F}\u{0434}\u{0440}"

    private static let rankedSentence = "The [pitcher] threw a fast ball past him."
    private static let killedSentence = "She deposited the money in the [bank] downtown."
    private static let afterRestartSentence = "The [pitcher] of lemonade was on the table."

    init(app: AppDelegate) { self.app = app }

    func run() {
        // Installed for the whole run (critic M13): a real press during the
        // run is recorded, never acted on, so ⌥V can never read the pasteboard.
        app.hotkeyObserver = { [weak self] action in
            guard let self else { return }
            if self.synthesizing { self.synthesized.append(action) } else { self.realPresses.append(action) }
        }
        let recognizer = VisionTextRecognizer()
        visionWarmUp = Task.detached {
            let started = ContinuousClock.now
            do throws(RecognitionFailure) {
                let lines = try await VisionFixture.warmUp(recognizer)
                return (started.duration(to: .now), .success(lines))
            } catch {
                return (started.duration(to: .now), .failure(error))
            }
        }
        Self.exitAfter(.seconds(120), "selftest watchdog: FAIL — not finished within 120 s")
        Task { @MainActor in
            await steps()
            await quitBySignal()
        }
    }

    /// Prints `line` and exits 1 after `delay`, from a background queue.
    private nonisolated static func exitAfter(_ delay: Duration, _ line: String) {
        let parts = delay.components
        DispatchQueue.global().asyncAfter(deadline: .now() + .seconds(Int(parts.seconds))) {
            print(line)
            fflush(stdout)
            exit(1)
        }
    }

    private func finish() -> Never {
        print("SELFTEST \(failures == 0 ? "PASS" : "FAIL") \(checks - failures)/\(checks) in \(ms(since: started))")
        fflush(stdout)
        exit(failures == 0 ? 0 : 1)
    }

    // MARK: Reporting

    private func report(_ name: String, _ ok: Bool, _ detail: String = "") {
        checks += 1
        if !ok { failures += 1 }
        print("selftest \(name): \(ok ? "ok" : "FAIL")\(detail.isEmpty ? "" : " — \(detail)")")
        fflush(stdout)
    }

    private func note(_ name: String, _ detail: String) {
        print("selftest \(name): note — \(detail)")
        fflush(stdout)
    }

    private func ms(since instant: ContinuousClock.Instant) -> String {
        let parts = instant.duration(to: .now).components
        return String(format: "%.0f ms", Double(parts.seconds) * 1000 + Double(parts.attoseconds) / 1e15)
    }

    private func pause(_ duration: Duration) async { try? await Task.sleep(for: duration) }

    private func waitUntil(_ timeout: Duration, _ condition: () async -> Bool) async -> Bool {
        let end = ContinuousClock.now + timeout
        while ContinuousClock.now < end {
            if await condition() { return true }
            await pause(.milliseconds(10))
        }
        return await condition()
    }

    private func noteOverlay() { overlayTexts.formUnion(app.overlay.model.visibleTexts) }

    private func translate(_ text: String) async -> HoverOutcome {
        let screen = NSScreen.main?.frame ?? .zero
        let outcome = await app.translate(text, near: NSPoint(x: screen.midX, y: screen.midY)).value
        noteOverlay()
        return outcome
    }

    private func pid() async -> pid_t? { await app.supervisor.connection?.pid }

    private static func errorCode(_ outcome: HoverOutcome) -> String? {
        if case .rejected(let error) = outcome { return error.code }
        return nil
    }

    private func modelsStatus() async -> ModelsStatus? { await app.refreshModels().value }

    private func waitForReady(other old: pid_t?, within timeout: Duration) async -> pid_t? {
        var pid: pid_t?
        _ = await waitUntil(timeout) {
            pid = await self.pid()
            return app.coreState.isReady && pid != nil && pid != old
        }
        return app.coreState.isReady && pid != old ? pid : nil
    }

    // MARK: Steps

    private func steps() async {
        await visionChecks()
        launchChecks()
        await overlayChecks()
        hotkeyChecks()
        await windowChecks()
        guard let session = await serviceChecks() else { return }
        await ocrSelectCheck()
        await hoverChecks(session)
        await senseChecks()
        await faultChecks()
        textChecks()
    }

    /// The real Apple Vision on frames rendered in memory (DoD 20.3 items 6
    /// and 10): the warm-up, the configuration T23 requires, and the Latin
    /// fixture at 2x and at 1x (the 1x frame takes the 2x upscale path, T24),
    /// its lines and word boxes in frame pixels against the rendered layout.
    private func visionChecks() async {
        let (took, warmed) = await visionWarmUp?.value ?? (.zero, .failure(.cancelled))
        let caches = URL(fileURLWithPath: NSHomeDirectory()).appendingPathComponent("Library/Caches")
        let compiled = ((try? FileManager.default.contentsOfDirectory(atPath: caches.path)) ?? []).sorted()
            .map { caches.appendingPathComponent($0).appendingPathComponent("com.apple.e5rt.e5bundlecache") }
            .filter { FileManager.default.fileExists(atPath: $0.path) }.map(\.path)
        switch warmed {
        case .success(let lines):
            report("vision.warmup", lines.map(\.text) == [VisionFixture.warmUpLine],
                   "\(Self.milliseconds(took)) in parallel with the translation service's start, read "
                   + "\(lines.map(\.text)); Vision's compile cache: \(compiled.isEmpty ? "none" : compiled.joined(separator: ", "))")
        case .failure(let failure):
            report("vision.warmup", false, "\(Self.milliseconds(took)): \(failure.code): \(failure)")
        }

        let configuration = VisionTextRecognizer.configuration
        let problems = configuration.problems()
        report("vision.config", problems.isEmpty,
               "\(configuration); supported revisions \(VisionTextRecognizer.supportedRevisions)"
               + (problems.isEmpty ? "" : "; " + problems.joined(separator: "; ")))

        let recognizer = VisionTextRecognizer()
        var passed = true
        var details: [String] = []
        for density in [2.0, 1.0] {
            let started = ContinuousClock.now
            do throws(RecognitionFailure) {
                let fixture = try VisionFixture.render(pixelsPerPoint: density)
                let recognition = try await recognizer.recognition(fixture.frame, capturedAt: fixture.bounds)
                let took = started.duration(to: .now)
                let comparison = VisionFixture.compare(recognition.lines, with: fixture.layout)
                // The size Vision was given, not the factor the code intends:
                // the frame-pixel boxes are the same either way (T24).
                let seenProblem = VisionFixture.seenProblem(recognition.seenSize, for: fixture.frame)
                passed = passed && comparison.ok && seenProblem == nil
                details.append("\(Int(density))x frame \(fixture.frame.size), Vision given \(recognition.seenSize), "
                               + "in \(Self.milliseconds(took)): \(comparison.summary)"
                               + (comparison.ok ? "" : "; " + comparison.problems.joined(separator: "; "))
                               + (seenProblem.map { "; " + $0 } ?? ""))
                if density == 2 { recognizedFixture = (fixture, recognition.lines) }
            } catch {
                passed = false
                details.append("\(Int(density))x: \(error.code): \(error)")
            }
        }
        report("vision.fixture", passed, details.joined(separator: "; "))
    }

    /// ocr.select on the bundled CoreHost with what Vision read in the 2x
    /// fixture, the cursor on the middle of the rendered "bank": Core's
    /// selection must be that word in its line, with the line's text.
    private func ocrSelectCheck() async {
        guard let recognized = recognizedFixture else {
            report("ocr.select", false, "no recognized fixture (vision.fixture failed)")
            return
        }
        let fingerprint = SHA256FrameFingerprinter().fingerprint(of: recognized.fixture.frame)
        guard let source = VisionFixture.selectSource(recognized.fixture, recognized: recognized.lines,
                                                      fingerprint: fingerprint),
              let span = VisionFixture.hoveredSpan else {
            report("ocr.select", false, "the fixture has no \(VisionFixture.hoveredWord)")
            return
        }
        let line = VisionFixture.lines[VisionFixture.hoveredLine]
        do {
            let result = try await ObservationClient(core: app.supervisor).select(mode: .dictionary, source: source)
            let selection = result.selection
            report("ocr.select", selection?.hoveredWord == VisionFixture.hoveredWord && selection?.text == line
                   && selection.map { span.contains($0.characterIndex) } == true
                   && result.lines == recognized.lines.map(\.text),
                   "cursor \(source.cursor) in frame \(fingerprint): «\(selection?.hoveredWord ?? "nothing")» at "
                   + "\(selection?.characterIndex ?? -1) (span \(span)) in «\(selection?.text ?? "")»; \(result.lines.count) lines")
        } catch {
            report("ocr.select", false, "\(error)")
        }
    }

    private static func milliseconds(_ duration: Duration) -> String {
        let parts = duration.components
        return String(format: "%.0f ms", Double(parts.seconds) * 1000 + Double(parts.attoseconds) / 1e15)
    }

    private func launchChecks() {
        let info = Bundle.main.infoDictionary ?? [:]
        let policy = NSApp.activationPolicy()
        report("launch", policy == .accessory && info["LSUIElement"] as? Bool == true
               && Bundle.main.bundleIdentifier == AppDelegate.bundleIdentifier,
               "policy \(policy.rawValue), LSUIElement \(info["LSUIElement"] ?? "absent"), "
               + "id \(Bundle.main.bundleIdentifier ?? "none")")
        report("launch.instance", app.instanceLock?.isHeld == true,
               "\(app.paths.instanceLock.path) held; the NSRunningApplication check is skipped in the self-test")
        let menu = app.statusMenu!
        let expected = [RU.menuHoverTranslation, RU.menuTranslationMode, RU.menuModeSmart, RU.menuModeDictionary,
                        RU.menuCheckTranslation, RU.menuTranslateClipboard, RU.menuSettings,
                        RU.menuRestartTranslation, RU.menuQuit]
        let titles = menu.visibleTitles
        report("statusItem", menu.item.button?.image != nil && expected.allSatisfy(titles.contains)
               && [RU.stateStarting, RU.stateReady, RU.stateRestarting, RU.stateUnavailable].contains(menu.stateText),
               titles.joined(separator: " | "))
    }

    private func overlayChecks() async {
        let overlay = app.overlay!
        let panel = overlay.panel
        let screen = NSScreen.main?.frame ?? .zero
        overlay.showMessage(RU.overlaySelfTest, near: NSPoint(x: screen.midX, y: screen.midY))
        noteOverlay()
        await pause(.milliseconds(300))
        let behavior: NSWindow.CollectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
        report("overlay.config", panel.level == .screenSaver && panel.collectionBehavior.isSuperset(of: behavior)
               && panel.ignoresMouseEvents && panel.sharingType == .none && !panel.canBecomeKey
               && !panel.canBecomeMain && panel.styleMask.contains(.nonactivatingPanel),
               "level \(panel.level.rawValue), behavior \(panel.collectionBehavior.rawValue), "
               + "sharing \(panel.sharingType.rawValue)")
        let list = CGWindowListCopyWindowInfo([.optionIncludingWindow], CGWindowID(panel.windowNumber)) as? [[String: Any]]
        let window = list?.first
        let layer = window?[kCGWindowLayer as String] as? Int
        let sharing = window?[kCGWindowSharingState as String] as? Int
        let onscreen = window?[kCGWindowIsOnscreen as String] as? Bool
        report("overlay.windowServer", layer == 1000 && sharing == 0 && onscreen == true,
               "layer \(layer.map(String.init) ?? "?"), sharingState \(sharing.map(String.init) ?? "?"), "
               + "onscreen \(onscreen.map { String($0) } ?? "?")")
        overlay.hide()
    }

    private func hotkeyChecks() {
        let hotkeys = app.hotkeys!
        let statuses = HotkeyAction.allCases.map { "\($0.defaultSpec.display) \(hotkeys.statuses[$0] ?? -1)" }
        let detail = "handler \(hotkeys.handlerStatus), " + statuses.joined(separator: ", ")
        let taken = HotkeyAction.allCases.filter { hotkeys.statuses[$0] == OSStatus(eventHotKeyExistsErr) }
        let otherInstance = !NSRunningApplication.runningApplications(withBundleIdentifier: AppDelegate.bundleIdentifier)
            .filter { $0.processIdentifier != getpid() }.isEmpty
        let allRegistered = HotkeyAction.allCases.allSatisfy { hotkeys.statuses[$0] == noErr }
        if hotkeys.handlerStatus == noErr, !allRegistered, otherInstance,
           HotkeyAction.allCases.allSatisfy({ hotkeys.statuses[$0] == noErr || taken.contains($0) }) {
            // The owner's own copy holds them; this run cannot prove registration.
            note("hotkeys.registered", "another Asteria Scale is running and holds them: \(detail)")
        } else {
            report("hotkeys.registered", hotkeys.handlerStatus == noErr && allRegistered
                   && HotkeyAction.allCases.map(\.defaultSpec.display) == ["⌥T", "⌥D", "⌥V"]
                   && hotkeys.problems.isEmpty, detail)
        }
        synthesizing = true
        let sent = HotkeyAction.allCases.map(hotkeys.synthesizePress)
        synthesizing = false
        report("hotkeys.dispatch", sent.allSatisfy { $0 == noErr } && synthesized == HotkeyAction.allCases,
               "sent \(sent), fired \(synthesized.map(\.rawValue)); presses are recorded, not acted on")
    }

    private func windowChecks() async {
        // Ordered front without activating the app.
        for (name, window) in [("settingsWindow", app.settingsWindow.window),
                               ("diagnosticWindow", app.diagnosticWindow.window)] {
            window.orderFrontRegardless()
            await pause(.milliseconds(200))
            let title = name == "settingsWindow" ? RU.settingsTitle : RU.diagnosticTitle
            report(name, window.isVisible && window.contentView != nil && window.title == title, window.title)
            window.orderOut(nil)
        }
    }

    private func serviceChecks() async -> CoreSessionInfo? {
        let readyBy = await waitUntil(.seconds(30)) {
            if case .failed = app.coreState { return true }
            return app.coreState.isReady
        }
        guard readyBy, case .ready(let session) = app.coreState else {
            report("service.ready", false, "\(app.coreState.russian) \(app.coreState.details ?? "")")
            return nil
        }
        let hello = session.hello
        report("service.ready", app.statusMenu.stateText == RU.stateReady && !hello.build.isEmpty
               && hello.build == app.expectedBuild,
               "pid \(session.pid), build \(hello.build), stamped \(app.expectedBuild ?? "none"), "
               + "\(ms(since: started)) after launch; menu «\(app.statusMenu.stateText)»")

        // proc_pidpath names the real path (/private/tmp/…).
        let bundle = ProcessTable.realPath(Bundle.main.bundlePath)
        let executable = ProcessTable.executablePath(session.pid) ?? "?"
        let paths = hello.raw["paths"]
        let dataRoot = paths?["dataRoot"]?.stringValue ?? "?"
        let worker = paths?["rankerWorker"]?.stringValue ?? "?"
        let models = paths?["rankerModels"]?.stringValue ?? "?"
        report("service.fromBundle", executable == bundle + "/Contents/Helpers/AsteriaScale.CoreHost"
               && dataRoot == app.paths.dataRoot.path && dataRoot.hasPrefix(Bundle.main.bundlePath + "/Contents/Resources/")
               && worker == app.paths.rankerWorker?.path && worker.hasPrefix(Bundle.main.bundlePath + "/Contents/Helpers/Ranker/")
               && models == app.paths.rankerModels.path && !models.hasPrefix(Bundle.main.bundlePath),
               "CoreHost \(executable), data \(dataRoot), worker \(worker), models \(models)")

        let initResult = session.initResult
        let entries = initResult?["dictionary"]?["entries"]?.int64Value ?? 0
        let sources = initResult?["dictionary"]?["sources"]?.int64Value ?? 0
        let morphology = initResult?["morphologyEntries"]?.int64Value ?? 0
        let firstRun = initResult?["firstRun"]
        report("service.data", entries > 0 && sources > 0 && morphology > 0
               && firstRun?["senseModel"]?.stringValue == "consec" && firstRun?["senseBackend"]?.stringValue == "cpu"
               && firstRun?["hoverEnabled"]?.boolValue == false && app.settings?.settings.hoverEnabled == false,
               "core.init: \(entries) entries from \(sources) dictionaries, \(morphology) morphology entries; "
               + "first run: sense \(firstRun?["senseModel"]?.stringValue ?? "?")/"
               + "\(firstRun?["senseBackend"]?.stringValue ?? "?"), hover \(firstRun?["hoverEnabled"]?.boolValue ?? true)")
        return session
    }

    private func hoverChecks(_ session: CoreSessionInfo) async {
        // The first run leaves hover translation off (docs/MACOS.md T7).
        let disabled = await translate("The [bank] was steep and muddy.")
        report("hover.disabled", Self.errorCode(disabled) == "hover_disabled"
               && app.diagnosticWindow.model.note == RU.diagnosticHoverDisabled && !app.overlay.isVisible,
               "outcome \(disabled), note «\(app.diagnosticWindow.model.note ?? "")»")

        // The same path as ⌥T and the menu item.
        let toggled = await app.toggleHover()?.value
        report("menu.toggleHover", toggled?.settings.hoverEnabled == true && app.statusMenu.hoverChecked
               && (toggled?.revision ?? 0) > 1,
               "settings revision \(toggled?.revision ?? -1), menu checked \(app.statusMenu.hoverChecked)")
        let moded = await app.setMode(.dictionary)?.value
        report("menu.mode", moded?.settings.translationMode == .dictionary && app.statusMenu.checkedMode == .dictionary,
               "mode \(moded?.settings.mode ?? "?"), menu \(app.statusMenu.checkedMode?.rawValue ?? "none")")

        let started = ContinuousClock.now
        let outcome = await translate("The [bank] was steep and muddy.")
        let model = app.overlay.model
        let primary = app.lastPrimary
        let cyrillic = model.translation.range(of: #"\p{Cyrillic}"#, options: .regularExpression) != nil
        var history: [JSONValue] = []
        _ = await waitUntil(.seconds(3)) {
            history = (try? await self.app.supervisor.request("history.list", deadline: CoreDeadlines.control))?["entries"]?
                .arrayValue ?? []
            return history.contains { $0["source"]?.stringValue == "bank" }
        }
        report("hover.dictionary", outcome == .completed && model.source == "bank" && cyrillic && !model.loading
               && model.badge == RU.provenance(.dictionary, found: true) && primary?.provenance == .dictionary
               && app.overlay.isVisible && history.contains { $0["source"]?.stringValue == "bank" },
               "\(ms(since: started)) «\(model.source)» → «\(model.translation.prefix(40))» [\(model.badge)]; "
               + "history \(history.count) entries")

        let status = await modelsStatus()
        report("models.status", status != nil && status?.dictionaryLoaded == true && (status?.morphologyEntries ?? 0) > 0
               && app.statusMenu.senseText == status?.senseLine
               && app.statusMenu.senseText.range(of: #"\p{Cyrillic}"#, options: .regularExpression) != nil,
               "morphology \(status?.morphologyEntries ?? -1), menu «\(app.statusMenu.senseText)»")
    }

    private func senseChecks() async {
        let selected = await app.updateSettings(SettingsPatch(mode: .smart, senseModel: "consec", senseBackend: "cpu")).value
        report("sense.select", selected?.settings.translationMode == .smart && selected?.settings.senseModel == "consec"
               && selected?.settings.senseBackend == "cpu" && app.statusMenu.checkedMode == .smart,
               "settings.update: mode \(selected?.settings.mode ?? "?"), sense \(selected?.settings.senseModel ?? "?")/"
               + "\(selected?.settings.senseBackend ?? "?")")

        let bundleFile = app.paths.rankerModels.appendingPathComponent("Consec/tokenizer.json")
        let bundleData = app.paths.rankerModels.appendingPathComponent("Consec/Cpu/consec-wngt-fp32.onnx.data")
        guard FileManager.default.fileExists(atPath: bundleFile.path),
              FileManager.default.fileExists(atPath: bundleData.path) else {
            await rankerUnavailableChecks()
            let missing = FileManager.default.fileExists(atPath: bundleFile.path) ? bundleData : bundleFile
            note("consec.killInFlight", "absent: \(missing.path)")
            return
        }
        await consecChecks()
    }

    /// Without the owner's bundle: the card says so and the menu names the
    /// worker's code in Russian; nothing ranks with another model (rule 3).
    private func rankerUnavailableChecks() async {
        let outcome = await translate(Self.rankedSentence)
        let primary = app.lastPrimary
        let status = await modelsStatus()
        let line = app.statusMenu.senseText
        let code = status?.sense.errorCode ?? ""
        report("sense.unavailable", outcome == .completed && primary?.provenance == .rankerUnavailable
               && app.overlay.model.badge == RU.provenance(.rankerUnavailable, found: true)
               && !code.isEmpty && line == status?.sense.errorMessage && line.contains("(\(code))")
               && !line.contains("/") && line.range(of: #"\p{Cyrillic}"#, options: .regularExpression) != nil,
               "card [\(app.overlay.model.badge)], code \(code), menu «\(line)»")
    }

    /// The owner's ConSeC bundle, read in place: a ranked hover with the
    /// exact identity, then CoreHost killed while the next one is in flight.
    private func consecChecks() async {
        let started = ContinuousClock.now
        let outcome = await translate(Self.rankedSentence)
        let primary = app.lastPrimary
        var status: ModelsStatus?
        _ = await waitUntil(.seconds(5)) {
            status = await self.modelsStatus()
            return status?.sense.running == true && status?.sense.modelVersion != nil
        }
        let hostPid = await pid()
        let workerPid = status?.sense.pid
        let workerGroup = workerPid.map { getpgid($0) }
        report("consec.hover", outcome == .completed && primary?.provenance == .ranker && primary?.senseProvider == "cpu"
               && status?.sense.modelVersion == SenseIdentity.consec && status?.sense.activeProvider == "cpu"
               && hostPid != nil && workerGroup == hostPid,
               "\(ms(since: started)) «\(primary?.source ?? "")» → «\(primary?.translation.prefix(40) ?? "")» "
               + "[\(primary?.provenance.wire ?? "?")/\(primary?.senseProvider ?? "?")], worker \(workerPid ?? -1) "
               + "in group \(workerGroup ?? -1), identity \(status?.sense.modelVersion ?? "none")")

        guard let hostPid else {
            report("consec.killInFlight", false, "no CoreHost")
            return
        }
        let screen = NSScreen.main?.frame ?? .zero
        let previousRequest = app.lastHoverRequestId
        let inFlight = app.translate(Self.killedSentence, near: NSPoint(x: screen.midX, y: screen.midY))
        // Kill once the hover is written and CoreHost has had a moment to
        // start on it; a ConSeC hover on the CPU takes far longer than that.
        let sent = await waitUntil(.seconds(2)) { self.app.lastHoverRequestId != previousRequest }
        await pause(.milliseconds(15))
        let killedAt = ContinuousClock.now
        Darwin.kill(hostPid, SIGKILL)
        let killedOutcome = await inFlight.value
        noteOverlay()
        let overlay = app.overlay!
        let overlayOk = !overlay.isVisible || overlay.model.message == RU.overlayUnavailable
        let workerGone = await waitUntil(.seconds(1)) { workerPid.map { !ProcessTable.isAlive($0) } ?? false }
        let groupEmpty = ProcessTable.liveMembers(hostPid).isEmpty
        let newPid = await waitForReady(other: hostPid, within: .seconds(10))
        let back = killedAt.duration(to: .now)
        let lost = Self.errorCode(killedOutcome) == "connection_lost"
        report("consec.killInFlight", sent && lost && overlayOk && workerGone && groupEmpty && newPid != nil
               && back < .seconds(3),
               "outcome \(killedOutcome); overlay "
               + (overlay.isVisible ? "«\(overlay.model.message ?? overlay.model.translation)»" : "hidden")
               + "; worker \(workerPid ?? -1) gone \(workerGone); group \(hostPid) empty \(groupEmpty); "
               + "ready again in \(ms(since: killedAt))")

        let again = await translate(Self.afterRestartSentence)
        let after = app.lastPrimary
        var restarted: ModelsStatus?
        _ = await waitUntil(.seconds(5)) {
            restarted = await self.modelsStatus()
            return restarted?.sense.running == true && restarted?.sense.modelVersion != nil
        }
        let ranked = after.map { [Provenance.ranker, .rankerAbstained].contains($0.provenance) } ?? false
        report("consec.afterRestart", again == .completed && ranked && after?.senseProvider == "cpu" && restarted?.sense.modelVersion == SenseIdentity.consec,
               "«\(after?.source ?? "")» → «\(after?.translation.prefix(40) ?? "")» [\(after?.provenance.wire ?? "?")], "
               + "worker \(restarted?.sense.pid ?? -1)")
    }

    private func faultChecks() async {
        // CoreHost killed while idle: the app stays up and gets it back.
        guard let first = await pid() else {
            report("fault.kill9", false, "no CoreHost")
            return
        }
        let killedAt = ContinuousClock.now
        Darwin.kill(first, SIGKILL)
        var sawRestarting = false
        var second: pid_t?
        let end = ContinuousClock.now + .seconds(15)
        while ContinuousClock.now < end {
            if case .restarting = app.coreState { sawRestarting = true }
            if app.coreState.isReady, let current = await pid(), current != first {
                second = current
                break
            }
            await pause(.milliseconds(5))
        }
        // Each kill counts as a failure of the backoff (0.5, 1, 2 s, …): this
        // one follows the in-flight kill, so it may take its 1 s delay.
        report("fault.kill9", second != nil && sawRestarting && ProcessTable.liveMembers(first).isEmpty,
               "ready again in \(ms(since: killedAt)), "
               + "old group \(first) empty \(ProcessTable.liveMembers(first).isEmpty), restarting seen \(sawRestarting)")

        _ = await app.setMode(.dictionary)?.value
        let again = await translate("Close the [door].")
        report("hover.afterRestart", again == .completed && app.lastPrimary?.source == "door",
               "«\(app.overlay.model.translation.prefix(30))»")

        // A hover while CoreHost is down degrades to a message, never a hang.
        if let current = await pid() { Darwin.kill(current, SIGKILL) }
        await pause(.milliseconds(30))
        let degraded = await translate("The [bank] again.")
        switch degraded {
        case .rejected(let error):
            report("hover.whileRestarting", ["not_ready", "connection_lost"].contains(error.code)
                   && (!app.overlay.isVisible || app.overlay.model.message == RU.overlayUnavailable),
                   "\(error.code): overlay «\(app.overlay.model.message ?? "hidden")»")
        default:
            report("hover.whileRestarting", degraded == .completed, "outcome \(degraded)")
        }
        _ = await waitUntil(.seconds(30)) { app.coreState.isReady }
    }

    /// M15, rule 9 and DoD 3 over everything shown during the run, and
    /// CoreHost's free text only in «Проверка перевода».
    private func textChecks() {
        noteOverlay()
        let menu = Set(app.statusMenu.visibleTitles)
        let windows = Set(app.settingsWindow.model.visibleTexts + app.diagnosticWindow.model.visibleTexts)
        let free = Set(app.serviceMessages.messages.filter { $0.area == "translation" }.map(\.text))
        let shown = menu.union(overlayTexts).union(windows).union(app.serviceMessages.messages.map(\.text))
        let bad = shown.filter {
            $0.lowercased().contains(Self.hostWord) || $0.contains("NPU OCR") || $0.uppercased().contains("VULKAN")
        }
        let leaked = free.filter { text in menu.contains { $0.contains(text) } || overlayTexts.contains { $0.contains(text) } }
        report("ui.text", bad.isEmpty && leaked.isEmpty && !free.isEmpty
               && free.allSatisfy { text in app.diagnosticWindow.model.messages.contains { $0.contains(text) } },
               "\(shown.count) texts, \(free.count) free-text messages only in «\(RU.diagnosticTitle)»"
               + (bad.isEmpty ? "" : "; forbidden: \(bad.sorted())") + (leaked.isEmpty ? "" : "; leaked: \(leaked.sorted())"))
    }

    /// The quit launchd and `kill` use: SIGTERM, then the signal source,
    /// NSApp.terminate, applicationShouldTerminate (.terminateLater), the
    /// orderly shutdown, the reply and applicationWillTerminate, where the
    /// last checks run. A quit that never gets there fails after 20 s.
    private func quitBySignal() async {
        let last = await pid() ?? 0
        let worker = app.models?.sense.pid
        let stopping = ContinuousClock.now
        app.onWillTerminate = { [self] in
            quitChecks(last: last, worker: worker, stopping: stopping)
        }
        Self.exitAfter(.seconds(20), "selftest quit.signal: FAIL — SIGTERM did not quit the app within 20 s")
        Darwin.kill(getpid(), SIGTERM)
    }

    private func quitChecks(last: pid_t, worker: Int32?, stopping: ContinuousClock.Instant) {
        report("quit.signal", app.shutdownFinished, "SIGTERM → orderly quit in \(ms(since: stopping))")
        report("quit.groupGone", last > 0 && ProcessTable.liveMembers(last).isEmpty
               && (worker.map { !ProcessTable.isAlive($0) } ?? true),
               "group \(last) empty" + (worker.map { ", worker \($0) gone" } ?? ""))

        let runtime = app.paths.runtime
        let fileManager = FileManager.default
        let leftovers = [app.paths.coreHostWorkingDirectory, app.paths.coreHostTemp].flatMap { folder in
            ((try? fileManager.contentsOfDirectory(atPath: folder.path)) ?? []).map { "\(folder.lastPathComponent)/\($0)" }
        }
        let record = fileManager.fileExists(atPath: app.paths.coreHostRecord.path)
        report("noStrayFiles", leftovers.isEmpty && !record,
               "\(runtime.path): cwd and tmp " + (leftovers.isEmpty ? "empty" : "hold \(leftovers)")
               + (record ? ", corehost.pid left" : ""))
        if !realPresses.isEmpty {
            note("hotkeys.realPresses", "\(realPresses.map(\.rawValue)) recorded, not acted on")
        }
        finish()
    }
}
