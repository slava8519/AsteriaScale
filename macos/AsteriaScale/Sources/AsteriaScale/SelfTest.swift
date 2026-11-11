import AppKit
import AsteriaScaleKit
import AsteriaScaleVision
import Carbon.HIToolbox
import Darwin

/// The self-test's permission: its frames are rendered in memory, so there
/// is nothing to ask the system (hover.observations never reads the real
/// Screen Recording permission).
private struct FixtureCapturePermission: CapturePermission {
    func isGranted() -> Bool { true }
}

/// A permission that says no, for the end message of a refused hover; like
/// the fixture, it asks the system nothing.
private struct DeniedCapturePermission: CapturePermission {
    func isGranted() -> Bool { false }
}

/// The self-test's game (speaker.editor, DoD 20.3 item 10): what
/// GameIdentity makes of a CrossOver process laid out in memory (Wine's
/// loader, the game's Windows path in its arguments, WINEPREFIX in its
/// environment). No window list, process or file is read: C: maps to the
/// bottle's drive_c without resolving a link.
private struct SyntheticGameResolver: GameResolver {
    static let loader = "/Applications/CrossOver.app/Contents/SharedSupport/CrossOver/lib/wine/x86_64-unix/wine64-preloader"
    static let bottle = "/Selftest/CrossOver/Bottles/Novel"
    static let expectedKey = "path:/Selftest/CrossOver/Bottles/Novel/drive_c/Games/Novel/Novel.exe"

    func game(at point: GlobalPoint) async -> HoverGame? {
        let record = ProcessArguments.encode(executablePath: Self.loader,
                                             arguments: [Self.loader, #"C:\Games\Novel\Novel.exe"#],
                                             environment: ["WINEPREFIX=\(Self.bottle)", "WINEDEBUG=-all"])
        return GameIdentity.game(executablePath: Self.loader, processArguments: record, resolveLink: { _ in nil })
    }
}

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
/// It never captures the screen: the capture gate is off for it (T22), so
/// no screen source exists and the cursor is never tracked, and Vision reads
/// only frames rendered here (hover.observations runs the hover pipeline on
/// one through a fixture frame source and a stub permission). speaker.editor
/// drives the speaker editor on cards shown here, with its game from a stub
/// resolver, without making it key or activating the app.
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
        await speakerEditorCheck()
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
        captureGateCheck()
        let menu = app.statusMenu!
        let expected = [RU.menuHoverTranslation, RU.menuTranslationMode, RU.menuModeSmart, RU.menuModeDictionary,
                        RU.menuCheckTranslation, RU.menuTranslateClipboard, RU.menuSettings,
                        RU.menuRestartTranslation, RU.menuQuit]
        let titles = menu.visibleTitles
        report("statusItem", menu.item.button?.image != nil && expected.allSatisfy(titles.contains)
               && [RU.stateStarting, RU.stateReady, RU.stateRestarting, RU.stateUnavailable].contains(menu.stateText),
               titles.joined(separator: " | "))
    }

    /// The capture gate for this very process (DoD 20.3 item 8): the same
    /// rules without --self-test say off, because build-app.sh, not launchd,
    /// started it. Only a reason is asked for, never a permit; the app's own
    /// decision at launch is off for the self-test, so no screen source exists.
    private func captureGateCheck() {
        let arguments = CommandLine.arguments.filter { $0 != CaptureGate.selfTestArgument }
        let reason = CaptureGate.offReason(arguments: arguments, parentPid: getppid(),
                                           environment: ProcessInfo.processInfo.environment,
                                           bundleId: Bundle.main.bundleIdentifier ?? "")
        var parentIsNotLaunchd = false
        if case .parentIsNotLaunchd? = reason { parentIsNotLaunchd = true }
        // The decision the app made at launch: nil would mean it was never made.
        let decided = app.captureDecision
        report("capture.gate", parentIsNotLaunchd && decided == .off(.selfTest) && app.screenSource == nil,
               "without --self-test: " + (reason.map { "off: \($0)" } ?? "on")
               + "; this run: \(decided.map(String.init(describing:)) ?? "undecided"); screen source "
               + (app.screenSource == nil ? "not created" : "created"))
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
        // The window server's own record of the panel, read in the one file
        // that may name the window-list API (DoD 8); no instance, no capture.
        let (layer, sharing, onscreen) = ScreenSource.ownWindowRecord(panel.windowNumber)
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
                   && HotkeyAction.allCases.map(\.defaultSpec.display) == ["⌥T", "⌥D", "⌥V", "⌃⌥G"]
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
        captureDisabledCheck()

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

    /// Translation is on, yet nothing captures (DoD 20.3 item 8): no screen
    /// source, no cursor tracking, no hover from the tracker, no app-side
    /// Vision warm-up, and the menu says why, without the permission item.
    private func captureDisabledCheck() {
        let menu = app.statusMenu!
        let driver = app.hoverDriver!
        report("capture.disabled", app.settings?.settings.hoverEnabled == true && app.screenSource == nil
               && !driver.isTracking && driver.trackedHovers == 0 && app.visionWarmUp == .notStarted
               && app.recognition == .disabledForCheckRun && menu.recognitionText == RU.recognitionDisabledCheckRun
               && !menu.permissionItemVisible,
               "hover on \(app.settings?.settings.hoverEnabled ?? false), screen source "
               + "\(app.screenSource == nil ? "not created" : "created"), tracking \(driver.isTracking), "
               + "\(driver.trackedHovers) tracked hovers, app warm-up \(app.visionWarmUp), "
               + "menu «\(menu.recognitionText ?? "")», permission item \(menu.permissionItemVisible ? "shown" : "hidden")")
    }

    /// The hover pipeline end to end on a frame rendered in memory (DoD 20.3
    /// item 10), in «Словарь» with hover translation on: ocr.plan, the
    /// fixture frame of the plan's bounds, the real Vision, hover.resolve
    /// with observations in the bundled CoreHost, the loading card, the card
    /// with an Apple Vision label, and the history entry after
    /// hover.displayed. The display is synthetic (2x, 1000x600 points), the
    /// cursor on its middle, and the permission a stub: nothing asks the
    /// system and nothing is captured. First the same hover with the cursor
    /// re-check saying no (DoD 20.3 item 7: the cursor is checked against
    /// the anchor before anything shows): it completes, but no loading card,
    /// no card and no history entry may appear, the window notes the card as
    /// withheld, and hover.afterRestart's «door» card, which such a hover
    /// never owned, stays (T37). Then a hover whose permission says no: with
    /// the cursor gone its end message is not shown, with the cursor there it
    /// is (HoverDriver's re-check before an end message).
    private func observationsHoverCheck() async {
        guard let display = DisplayDescription(id: 1, frame: PointRect(x: 0, y: 0, width: 1000, height: 600),
                                               pixelWidth: 2000, pixelHeight: 1200) else {
            report("hover.observations", false, "the synthetic display was refused")
            return
        }
        let point = GlobalPoint(x: 500, y: 300)
        guard let location = CaptureGeometry.locate(point, on: [display]) else {
            report("hover.observations", false, "the synthetic display does not hold \(point)")
            return
        }
        // The synthetic CrossOver game (as speaker.editor's): CoreHost must
        // accept the key GameIdentity makes (T32), and HoverDriver must hand
        // the game the hover sent to the speaker editor with the shown card.
        let pipeline = HoverPipeline(client: ObservationClient(core: app.supervisor),
                                     frames: FixtureFrameSource(cursor: location.cursor), recognizer: VisionTextRecognizer(),
                                     fingerprinter: SHA256FrameFingerprinter(), games: SyntheticGameResolver(),
                                     permission: FixtureCapturePermission())
        // The fixture's line, as the loading card and the history entry of
        // this hover carry it (hover.dictionary's «bank» came from another
        // sentence).
        let line = VisionFixture.lines[VisionFixture.hoveredLine]
        let lineStart = String(line.prefix(18))
        func isThisHover(_ entry: JSONValue) -> Bool {
            entry["source"]?.stringValue == VisionFixture.hoveredWord
                && entry["context"]?.stringValue?.hasPrefix(lineStart) == true
        }
        func historyEntries() async -> [JSONValue] {
            (try? await app.supervisor.request("history.list", deadline: CoreDeadlines.control))?["entries"]?
                .arrayValue ?? []
        }

        let primaryBefore = app.lastPrimary
        func doorShows() -> Bool {
            app.overlay.isVisible && app.overlay.isShowingResult && app.overlay.model.source == "door"
                && app.overlay.model.message == nil
        }
        let doorBefore = doorShows()
        let targetBefore = app.speakerTarget?.target
        let unshown = await app.hoverDriver.hover(pipeline, at: point, displays: [display], mayShow: { false }).value
        let targetKept = app.speakerTarget?.target == targetBefore
        let unshownRecorded = await historyEntries().contains(where: isThisHover)
        let doorKept = doorBefore && doorShows()
        let unshownNote = app.diagnosticWindow.model.note
        let unshownOK = unshown.result.outcome == .completed && unshown.order.isEmpty && unshown.loading == nil
            && unshown.primary == nil && doorKept && unshownNote == RU.diagnosticWithheld
            && app.lastPrimary == primaryBefore && !unshownRecorded && targetKept

        let denied = HoverPipeline(client: ObservationClient(core: app.supervisor),
                                   frames: FixtureFrameSource(cursor: location.cursor), recognizer: VisionTextRecognizer(),
                                   fingerprinter: SHA256FrameFingerprinter(), games: NoGameResolver(),
                                   permission: DeniedCapturePermission())
        let refusedGone = await app.hoverDriver.hover(denied, at: point, displays: [display], mayShow: { false }).value
        let goneMessage = app.overlay.model.message
        let refusedHere = await app.hoverDriver.hover(denied, at: point, displays: [display], mayShow: { true }).value
        let hereMessage = app.overlay.isVisible ? app.overlay.model.message : nil
        noteOverlay()
        let refusedOK = Self.errorCode(refusedGone.result.outcome) == "screen_capture_denied" && goneMessage == nil
            && Self.errorCode(refusedHere.result.outcome) == "screen_capture_denied" && hereMessage == RU.captureNoPermission
        app.overlay.hide()

        let started = ContinuousClock.now
        let shown = await app.hoverDriver.hover(pipeline, at: point, displays: [display], mayShow: { true }).value
        let took = started.duration(to: .now)
        noteOverlay()
        var history: [JSONValue] = []
        _ = await waitUntil(.seconds(3)) {
            history = await historyEntries()
            return history.contains(where: isThisHover)
        }
        let entry = history.first(where: isThisHover)
        let loading = shown.loading, primary = shown.primary
        let target = app.speakerTarget?.target
        let targetOK = target?.point == point && target?.speaker == nil
            && target?.game?.key == SyntheticGameResolver.expectedKey
        let vision = "APPLE VISION"
        report("hover.observations", unshownOK && refusedOK && shown.result.outcome == .completed
               && shown.order == ["loading", "primary"]
               && loading?.source.hasPrefix(lineStart) == true && loading?.engineLabel.hasPrefix(vision) == true
               && shown.loadingTexts.contains(RU.overlayLookingUpDictionary)
               && primary?.source == VisionFixture.hoveredWord && primary?.provenance == .dictionary
               && primary?.engineLabel.hasPrefix(vision) == true && app.overlay.isVisible
               && app.overlay.model.source == VisionFixture.hoveredWord && entry != nil && targetOK,
               "\(Self.milliseconds(took)): \(shown.result.steps.map(\.description).joined(separator: "; ")); shown "
               + "\(shown.order); loading «\(loading?.source ?? "")» [\(loading?.engineLabel ?? "none")] "
               + "\(shown.loadingTexts); card «\(primary?.source ?? "")» → «\(primary?.translation.prefix(30) ?? "")» "
               + "[\(primary?.provenance.wire ?? "?")] \(primary?.engineLabel ?? "none"); history "
               + (entry.map { "«\($0["source"]?.stringValue ?? "")» [\($0["engine"]?.stringValue ?? "")]" } ?? "none")
               + "; speaker editor's card: game \(target?.game?.key ?? "none"), speaker "
               + "\(target?.speaker?.name ?? "none")\(targetOK ? "" : " (expected the hover's game, no speaker)")"
               + "; outcome \(shown.result.outcome); with the cursor gone: \(unshown.result.outcome), shown "
               + "\(unshown.order), «door» card \(doorKept ? "kept" : "not kept"), editor's card "
               + "\(targetKept ? "kept" : "replaced"), note «\(unshownNote ?? "")», history "
               + (unshownRecorded ? "recorded" : "none") + "; refused: \(refusedGone.result.outcome) with the cursor gone, "
               + "message «\(goneMessage ?? "none")»; with it there «\(hereMessage ?? "none")»")
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

    /// The speaker editor (DoD 20.3 items 5 and 10) on the bundled CoreHost
    /// in «Умный словарь», the one mode in which Core puts a speaker on a
    /// card, with the game from SyntheticGameResolver. The cards come from
    /// selection hovers that name the speaker «Kotorl» (a misread «Kotori»)
    /// and send that game, each shown on the overlay as a hover over the
    /// screen shows it, and ⌃⌥G's own path opens the editor on it, with
    /// the keys going through the panel's own key path. Checked: no card and
    /// a card without a game give their Russian messages and open nothing;
    /// opening hides the card and keeps a screen hover's card off the
    /// overlay (the tracking is never on in a self-test, so its pause is
    /// pinned by SourceScanTests instead); the name corrected to «Kotori» and 2
    /// store «feminine» under both spellings in settings.json of the
    /// throwaway home, with the status card «Kotori: женский род для
    /// Novel.exe»; the next card shows «Kotori», and the re-edit with 1
    /// reaches the record under «Kotorl», the spelling the card carried as
    /// observedName; a digit while the name is edited is text, and Esc sends
    /// nothing. The editor never becomes key, and the app never becomes
    /// active or frontmost. The frontmost app's pid before and after is
    /// printed, not compared: the owner may switch apps during the run.
    private func speakerEditorCheck() async {
        let editor = app.speakerEditor!
        let overlay = app.overlay!
        let frontmost = NSWorkspace.shared.frontmostApplication?.processIdentifier
        let activeBefore = NSApp.isActive
        var focus: [String] = []
        func noteFocus(_ step: String) {
            if NSApp.isActive != activeBefore { focus.append("\(step): app active \(NSApp.isActive)") }
            if editor.panel.isKeyWindow { focus.append("\(step): editor key") }
            // The owner may switch apps meanwhile; only this app coming to
            // the front is the editor's doing.
            let now = NSWorkspace.shared.frontmostApplication?.processIdentifier
            if now == getpid() { focus.append("\(step): this app frontmost") }
        }
        let screen = NSScreen.main?.frame ?? .zero
        let point = GlobalPoint(x: Double(screen.midX), y: Double(screen.midY))
        let near = NSPoint(x: point.x, y: point.y)
        guard let game = await SyntheticGameResolver().game(at: point) else {
            report("speaker.editor", false, "GameIdentity refused the synthetic CrossOver process")
            return
        }
        var problems: [String] = []
        func expect(_ ok: Bool, _ problem: @autoclosure () -> String) { if !ok { problems.append(problem()) } }
        expect(game == HoverGame(key: SyntheticGameResolver.expectedKey, displayName: "Novel.exe"),
               "the synthetic game is \(game.key) «\(game.displayName)»")
        let settingsFile = app.paths.userData.appendingPathComponent("settings.json")
        func records() -> [String] {
            guard let data = try? Data(contentsOf: settingsFile),
                  let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let genders = object["SpeakerGenders"] as? [[String: Any]] else { return ["unreadable"] }
            return genders.filter { $0["GameKey"] as? String == game.key }.map {
                "\($0["SpeakerName"] as? String ?? "?")>\($0["DisplayName"] as? String ?? "?"):\($0["Gender"] as? String ?? "?")"
            }.sorted()
        }
        let observed = "Kotorl", corrected = "Kotori"
        func card() async -> HoverPrimary? {
            let shown = Box<HoverPrimary?>(nil)
            let selection = HoverSelection(text: "The river was cold.", hoveredWord: "river", characterIndex: 4,
                                           speakerName: observed)
            _ = await app.hover.resolve(selection, game: game) { update in
                guard case .primary(let primary) = update else { return }
                DispatchQueue.main.async { MainActor.assumeIsolated { shown.value = primary } }
            }
            await withCheckedContinuation { continuation in DispatchQueue.main.async { continuation.resume() } }
            return shown.value
        }
        func showAsScreenCard(_ primary: HoverPrimary, game: HoverGame?) {
            let token = overlay.beginObservationsHover(near: near)
            overlay.apply(.primary(primary), token: token)
            app.speakerCardShown(SpeakerTarget(speaker: primary.speaker, game: game, point: point), token: token)
        }
        func speaker(_ primary: HoverPrimary?) -> String {
            guard let speaker = primary?.speaker else { return "none" }
            return "\(speaker.name)/\(speaker.gender)/\(speaker.gameKey ?? "no game")/observed \(speaker.observedName ?? "none")"
        }

        // Refusals: no card of a screen hover, then a card without a game.
        overlay.hide()
        let noCard = app.editSpeaker(takeKeyboard: false)
        let noCardMessage = overlay.model.message
        noteOverlay()
        expect(noCard == .noSpeaker && noCardMessage == RU.speakerNone && !editor.isOpen,
               "no card: \(String(describing: noCard)), «\(noCardMessage ?? "none")»")
        let seen = await card()
        expect(seen?.speaker?.name == observed && seen?.speaker?.gender == "unknown" && seen?.speaker?.gameKey == game.key
               && seen?.speaker?.observedName == observed, "first card \(speaker(seen))")
        guard let seen else {
            report("speaker.editor", false, "no card for the speaker: \(problems.joined(separator: "; "))")
            return
        }
        showAsScreenCard(seen, game: nil)
        let noGame = app.editSpeaker(takeKeyboard: false)
        let noGameMessage = overlay.model.message
        noteOverlay()
        expect(noGame == .noGame && noGameMessage == RU.speakerNoGame && !editor.isOpen,
               "no game: \(String(describing: noGame)), «\(noGameMessage ?? "none")»")

        // Open, correct the name, 2.
        showAsScreenCard(seen, game: game)
        let cardShown = overlay.isVisible && overlay.model.source == seen.source
        let opened = app.editSpeaker(takeKeyboard: false)
        noteFocus("open")
        let probe = overlay.beginObservationsHover(near: near)
        let withheld = !overlay.apply(.primary(seen), token: probe)
        expect(opened == nil && editor.isOpen && cardShown && !overlay.isVisible && overlay.isSuspended && withheld
               && editor.name == observed && editor.target?.game == game,
               "open: refusal \(String(describing: opened)), open \(editor.isOpen), card shown before \(cardShown), "
               + "overlay visible \(overlay.isVisible), suspended \(overlay.isSuspended), screen card withheld \(withheld), "
               + "name «\(editor.name)»")
        editor.setName(corrected)
        let typed = editor.keyPressed(keyCode: UInt16(kVK_ANSI_1), editingName: true)
        expect(!typed && editor.isOpen, "a digit while the name is edited was taken")
        editor.keyPressed(keyCode: UInt16(kVK_ANSI_2), editingName: false)
        let firstSaved = await app.speakerSave?.value ?? false
        noteFocus("feminine")
        let firstStatus = overlay.isVisible ? overlay.model.message : nil
        noteOverlay()
        let firstRecords = records()
        expect(firstSaved && !editor.isOpen && !overlay.isSuspended
               && firstStatus == RU.speakerSaved(name: corrected, choice: .feminine, game: game.displayName)
               && firstRecords == ["\(corrected)>\(corrected):feminine", "\(observed)>\(corrected):feminine"],
               "2: saved \(firstSaved), open \(editor.isOpen), status «\(firstStatus ?? "none")», settings \(firstRecords), "
               + "error «\(editor.errorText ?? "none")»")

        // The re-edit from the card that shows the corrected name, 1.
        let again = await card()
        expect(again?.speaker?.name == corrected && again?.speaker?.gender == "feminine"
               && again?.speaker?.observedName == observed, "second card \(speaker(again))")
        if let again { showAsScreenCard(again, game: game) }
        let reopened = app.editSpeaker(takeKeyboard: false)
        noteFocus("reopen")
        let reopenedName = editor.name
        editor.keyPressed(keyCode: UInt16(kVK_ANSI_1), editingName: false)
        let secondSaved = await app.speakerSave?.value ?? false
        noteOverlay()
        let secondRecords = records()
        expect(reopened == nil && reopenedName == corrected && secondSaved && !editor.isOpen
               && secondRecords == ["\(corrected)>\(corrected):masculine", "\(observed)>\(corrected):masculine"],
               "1: reopened \(String(describing: reopened)), name «\(reopenedName)», saved \(secondSaved), "
               + "settings \(secondRecords), error «\(editor.errorText ?? "none")»")

        // Esc sends nothing.
        let third = await card()
        expect(third?.speaker?.gender == "masculine" && third?.speaker?.observedName == observed, "third card \(speaker(third))")
        if let third { showAsScreenCard(third, game: game) }
        let bytes = try? Data(contentsOf: settingsFile)
        let saveBefore = app.speakerSave
        let escOpened = app.editSpeaker(takeKeyboard: false)
        noteFocus("esc")
        editor.keyPressed(keyCode: SpeakerEditor.escapeKey, editingName: false)
        await pause(.milliseconds(200))
        let unchanged = (try? Data(contentsOf: settingsFile)) == bytes && bytes != nil
        expect(escOpened == nil && !editor.isOpen && !overlay.isSuspended && unchanged && app.speakerSave == saveBefore,
               "Esc: opened \(String(describing: escOpened)), open \(editor.isOpen), settings unchanged \(unchanged)")
        noteFocus("closed")
        // A failed step must not leave the editor open: the overlay would
        // stay suspended and fail the checks after this one too.
        if editor.isOpen { app.closeSpeakerEditor(reason: "self-test") }
        overlay.hide()
        report("speaker.editor", problems.isEmpty && focus.isEmpty,
               "game «\(game.displayName)» \(game.key == SyntheticGameResolver.expectedKey ? "" : "UNEXPECTED ")\(game.key); "
               + "cards \(speaker(seen)), \(speaker(again)), \(speaker(third)); no card «\(noCardMessage ?? "none")»; "
               + "no game «\(noGameMessage ?? "none")»; 2 → «\(firstStatus ?? "none")» \(firstRecords); "
               + "re-edit 1 → \(secondRecords); Esc: settings unchanged \(unchanged); app active \(activeBefore) "
               + "before and after, frontmost pid \(frontmost.map { String($0) } ?? "none") before and "
               + "\(NSWorkspace.shared.frontmostApplication.map { String($0.processIdentifier) } ?? "none") after, editor never key"
               + (problems.isEmpty ? "" : "; problems: " + problems.joined(separator: "; "))
               + (focus.isEmpty ? "" : "; focus: " + focus.joined(separator: "; ")))
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

        // Here, after «door»: history drops a hover whose source and
        // translation repeat its newest entry, so right after
        // hover.dictionary's «bank» this hover's «bank» would not be recorded.
        await observationsHoverCheck()

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
        let windows = Set(app.settingsWindow.model.visibleTexts + app.diagnosticWindow.model.visibleTexts
                          + app.speakerEditor.visibleTexts)
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
