import AppKit
import AsteriaScaleKit
import AsteriaScaleVision
import Darwin

/// The menu-bar app. The status item, the overlay, the hotkeys and the
/// windows never depend on CoreHost being alive: they observe the
/// supervisor's states, and everything that needs the translation service
/// is disabled or degrades to a Russian message while it is not ready.
@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    static let bundleIdentifier = AppIdentity.bundleIdentifier

    let options: LaunchOptions
    private(set) var paths: AppPaths!
    private(set) var log: AppLog!
    private(set) var supervisor: CoreSupervisor!
    private(set) var hover: HoverClient!
    private(set) var settingsClient: SettingsClient!
    private(set) var statusMenu: StatusMenuController!
    private(set) var overlay: OverlayController!
    private(set) var hotkeys: HotkeyCenter!
    private(set) var settingsWindow: SettingsWindowController!
    private(set) var diagnosticWindow: DiagnosticWindowController!
    private(set) var instanceLock: InstanceLock?
    /// Whether this process may capture the screen, decided once at launch
    /// (docs/MACOS.md T22) and never revisited; nil until then, which
    /// captures nothing either (the self-test requires the decision made).
    private(set) var captureDecision: CaptureGate.Decision?
    /// The screen capture: built at launch only when that decision is on, so
    /// it never exists in a self-test, a direct exec or any agent run.
    private(set) var screenSource: ScreenSource?
    private(set) var hoverDriver: HoverDriver!
    /// «Род говорящего» (⌃⌥G, T31).
    private(set) var speakerEditor: SpeakerEditorController!
    /// The card ⌃⌥G opens the editor on: the last card of a hover over the
    /// screen, with the game that hover sent, while the overlay still shows
    /// it (the token says which card).
    private(set) var speakerTarget: (target: SpeakerTarget, token: Int)?
    /// The speaker.set of the open editor: true once saved.
    private(set) var speakerSave: Task<Bool, Never>?
    /// The app's Vision warm-up (T25): only with capture on, once hover
    /// translation is on (the self-test warms Vision as its own first step).
    private(set) var visionWarmUp: VisionWarmUp = .notStarted
    private(set) var recognition: RecognitionStatus = .disabledForCheckRun
    /// «Разрешить запись экрана…» was chosen in this run.
    private var screenRecordingRequested = false
    /// A tracked hover's capture was refused in this run (-3801 after a
    /// preflight that said yes, or a permission revoked while tracking):
    /// the menu offers the permission again and tracking stops. Nothing but
    /// a relaunch clears it, since the preflight said yes before the
    /// refusal too (DoD 20.3 item 7).
    private var captureRefused = false
    private(set) var coreState: CoreState = .stopped
    private(set) var settings: SettingsSnapshot?
    private(set) var models: ModelsStatus?
    private(set) var serviceMessages = ServiceMessageLog()
    /// The last card the overlay showed.
    private(set) var lastPrimary: HoverPrimary?
    /// The request id of the last hover.resolve written to CoreHost.
    private(set) var lastHoverRequestId: Int64?
    /// The CoreHost build this bundle was assembled with (build-app.sh
    /// stamps it into Info.plist); hello must report exactly this one.
    private(set) var expectedBuild: String?
    /// The self-test installs it for its whole run: hotkey presses are then
    /// only recorded, never acted on, so the pasteboard is never read.
    var hotkeyObserver: ((HotkeyAction) -> Void)?
    /// The running self-test; the app keeps it until it exits.
    private var selfTest: SelfTest?
    /// The self-test's last checks, run once the quit has stopped CoreHost.
    var onWillTerminate: (@MainActor () -> Void)?
    /// Set when shutdown() has stopped CoreHost and released the lock.
    private(set) var shutdownFinished = false
    private var terminating = false
    private var shutDown = false

    init(options: LaunchOptions) {
        self.options = options
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        paths = AppPaths.current(bundle: Bundle.main.bundleURL, rankerModels: options.rankerModels)
        do {
            try paths.createUserDirectories()
        } catch {
            // No app.log yet (its folder is among those that failed): the
            // description goes to stderr, the alert stays Russian.
            FileHandle.standardError.write(Data("cannot create the app folders: \(error)\n".utf8))
            refuseToStart(RU.startFoldersFailed(error))
            return
        }
        log = AppLog(url: paths.logs.appendingPathComponent("app.log"))
        log.write("launch pid \(getpid()) bundle \(Bundle.main.bundlePath) home \(paths.home.path)"
                  + (options.selfTest ? " self-test" : "") + " ranker models \(paths.rankerModels.path)")

        // One app per home: the flock on Runtime/app.lock, which the kernel
        // drops when the app dies. A real launch also refuses a second copy
        // of the bundle id in this session; the self-test runs in a throwaway
        // home beside a possibly running real app, so it relies on the lock.
        if !options.selfTest {
            let others = NSRunningApplication.runningApplications(withBundleIdentifier: Self.bundleIdentifier)
                .filter { $0.processIdentifier != getpid() }
            if !others.isEmpty {
                log.write("another instance is running (pid \(others.map(\.processIdentifier))); quitting")
                refuseToStart(RU.alreadyRunning)
                return
            }
        }
        do {
            instanceLock = try InstanceLock.acquire(at: paths.instanceLock)
        } catch {
            log.write("cannot take \(paths.instanceLock.path): \(error)")
            refuseToStart(RU.startLockFailed(error))
            return
        }
        guard instanceLock != nil else {
            log.write("\(paths.instanceLock.path) is held by another instance; quitting")
            refuseToStart(RU.alreadyRunning)
            return
        }

        // The one place the capture decision is made and the screen source
        // built (SourceScanTests pins both): on only for a LaunchServices
        // launch of this bundle without --self-test. Building it asks for
        // nothing; the permission is first read when the menu needs it.
        captureDecision = CaptureGate.decide(arguments: CommandLine.arguments, parentPid: getppid(),
                                             environment: ProcessInfo.processInfo.environment,
                                             bundleId: Bundle.main.bundleIdentifier ?? "")
        if case .on(let permit) = captureDecision { screenSource = ScreenSource(permit: permit) }
        log.write("screen capture: \(captureDecision.map(String.init(describing:)) ?? "undecided")")

        expectedBuild = Bundle.main.object(forInfoDictionaryKey: "AsteriaCoreHostBuild") as? String
        var config = CoreSupervisor.Configuration(
            executable: paths.coreHost.path, arguments: paths.coreHostArguments(),
            environment: paths.coreHostEnvironment(), workingDirectory: paths.coreHostWorkingDirectory.path)
        config.expectedBuild = expectedBuild
        config.initialize = true
        config.hostRecord = paths.coreHostRecord
        config.sweepPrefixes = paths.sweepPrefixes
        let log = self.log!
        supervisor = CoreSupervisor(config, onUnsolicited: { [weak self] event in
            DispatchQueue.main.async { MainActor.assumeIsolated { self?.serviceEvent(event) } }
        }, log: { line in log.write(line) })
        hover = HoverClient(supervisor: supervisor)
        settingsClient = SettingsClient(supervisor: supervisor)

        let preferences = AppPreferences.load(from: paths.preferences, log: { log.write($0) })
        let overlayLevel = preferences.resolvedOverlayLevel
        if let note = overlayLevel.note { log.write(note) }
        overlay = OverlayController(level: overlayLevel.level)
        log.write("overlay level \(overlayLevel.level.rawValue) (\(overlay.panel.level.rawValue))")
        speakerEditor = SpeakerEditorController(level: overlay.panel.level)
        speakerEditor.onChoose = { [weak self] editor, choice, name in self?.speakerChosen(editor, choice, name: name) }
        speakerEditor.onCancel = { [weak self] in self?.closeSpeakerEditor(reason: "cancelled") }
        hoverDriver = HoverDriver(app: self)
        statusMenu = StatusMenuController(app: self)
        updateRecognition()
        settingsWindow = SettingsWindowController(actions: SettingsActions(
            setHoverEnabled: { [weak self] in self?.updateSettings(SettingsPatch(hoverEnabled: $0)) },
            setMode: { [weak self] in self?.setMode($0) },
            setDelay: { [weak self] in self?.updateSettings(SettingsPatch(hoverDelayMs: $0)) },
            restart: { [weak self] in self?.restartTranslation() }))
        settingsWindow.update(paths: paths)
        diagnosticWindow = DiagnosticWindowController { [weak self] text in
            guard let self else { return }
            self.translate(text, near: NSEvent.mouseLocation)
        }
        hotkeys = HotkeyCenter { [weak self] action in self?.hotkeyPressed(action) }
        for action in HotkeyAction.allCases {
            let spec = preferences.hotkeys[action] ?? action.defaultSpec
            if let problem = hotkeys.register(action, spec) {
                log.write("hotkey \(action.rawValue) \(spec.display): status \(hotkeys.statuses[action] ?? 0) «\(problem)»")
            } else {
                log.write("hotkey \(action.rawValue) \(spec.display): registered")
            }
        }
        statusMenu.update(hotkeys: hotkeys)
        settingsWindow.update(hotkeys: hotkeys)

        Task { [weak self] in
            guard let states = self?.supervisor.states else { return }
            for await state in states { self?.coreStateChanged(state) }
        }
        let supervisor = self.supervisor!
        Task { await supervisor.start() }

        if options.selfTest {
            selfTest = SelfTest(app: self)
            selfTest?.run()
        }
    }

    /// Shows why the app cannot run (a second instance, an unwritable home)
    /// and quits; nothing has been started yet.
    private func refuseToStart(_ text: String) {
        if options.selfTest {
            print("selftest launch: FAIL — \(text)")
            exit(1)
        }
        let alert = NSAlert()
        alert.messageText = RU.appName
        alert.informativeText = text
        NSApp.activate()
        alert.runModal()
        exit(0)
    }

    // MARK: State

    private func coreStateChanged(_ state: CoreState) {
        coreState = state
        log.write("translation state: \(state.russian)" + (state.details.map { " (\($0))" } ?? ""))
        if case .ready(let session) = state {
            if let initResult = session.initResult, let snapshot = SettingsSnapshot(result: initResult) {
                adopt(snapshot)
            }
            refreshModels()
        } else {
            // Danger zone (AGENTS.md): never leave a card on screen that the
            // service can no longer stand behind.
            if overlay.isShowingResult { overlay.hide() }
            settings = nil
            models = nil
            let client = settingsClient!
            Task { await client.forget() }
            statusMenu.update(models: nil)
        }
        statusMenu.update(state: state, settings: settings?.settings)
        settingsWindow.update(state: state)
        settingsWindow.update(settings: settings?.settings, ready: state.isReady)
        settingsWindow.model.senseLine = statusMenu.senseText
        updateRecognition()
    }

    private func adopt(_ snapshot: SettingsSnapshot) {
        // A new mode or new smart settings restart a tracked hover, as
        // Windows' coordinator does (CoreHost leaves that to the app).
        let change = snapshot.settings.hoverChange(from: settings?.settings)
        settings = snapshot
        let client = settingsClient!
        Task { await client.adopt(snapshot) }
        statusMenu.update(state: coreState, settings: snapshot.settings)
        settingsWindow.update(settings: snapshot.settings, ready: coreState.isReady)
        hoverDriver.settingsChanged(change)
        updateRecognition()
    }

    // MARK: Recognition and the cursor (20.3)

    /// The recognition line, the permission item and whether the cursor is
    /// tracked, from the capture decision, the permission, the Vision
    /// warm-up, the service and hover translation. The permission is read
    /// (the preflight, which never prompts) only through the screen source,
    /// so a process whose capture is off never reads it.
    func updateRecognition() {
        let enabled = coreState.isReady && settings?.settings.hoverEnabled == true
        if screenSource != nil, enabled, visionWarmUp == .notStarted { startVisionWarmUp() }
        var status = RecognitionStatus.resolve(gateOn: screenSource != nil, permitted: screenSource?.isGranted() ?? false,
                                               captureRefused: captureRefused, warmUp: visionWarmUp)
        if case .needsPermission = status, screenRecordingRequested { status = .needsPermission(requested: true) }
        if status != recognition { log.write("recognition: \(status.menuLine ?? "idle")") }
        recognition = status
        statusMenu.update(recognition: status)
        // Paused while the speaker editor is open: stopping cancels the
        // hover in flight and forgets the anchor (T31; pinned by
        // SourceScanTests, as no run of the self-test tracks).
        let track = status.tracks(hoverEnabled: enabled, editorOpen: speakerEditor.isOpen)
        hoverDriver.setTracking(track, pipeline: track ? screenPipeline() : nil)
    }

    /// The hover over the screen: ScreenSource for frames, the permission
    /// and the game under the cursor (T29), the real Vision and CryptoKit's
    /// fingerprint. Only with capture on: no other process resolves a game.
    private func screenPipeline() -> HoverPipeline? {
        guard let screenSource else { return nil }
        return HoverPipeline(client: ObservationClient(core: supervisor), frames: screenSource,
                             recognizer: VisionTextRecognizer(), fingerprinter: SHA256FrameFingerprinter(),
                             games: screenSource, permission: screenSource)
    }

    /// Vision compiles its models on its first call in a home (about 24 s,
    /// T25): done once, when hover translation is first on, and no hover
    /// starts before it ends.
    private func startVisionWarmUp() {
        visionWarmUp = .running
        let started = ContinuousClock.now
        Task { [weak self] in
            let state: VisionWarmUp
            do throws(RecognitionFailure) {
                let lines = try await VisionFixture.warmUp(VisionTextRecognizer())
                self?.log.write("vision warm-up: \(started.duration(to: .now)), read \(lines.map(\.text))")
                state = .ready
            } catch {
                self?.log.write("vision warm-up failed after \(started.duration(to: .now)): \(error)")
                state = .failed(RU.recognitionWarmUpFailed(error.code))
            }
            self?.visionWarmUp = state
            self?.updateRecognition()
        }
    }

    /// «Разрешить запись экрана…»: only with capture on. After a refused
    /// capture it opens the Privacy pane too, since the request may then
    /// say yes without showing anything.
    func requestScreenRecording() {
        guard let screenSource else { return }
        screenRecordingRequested = true
        let granted = screenSource.requestPermission(afterRefusal: captureRefused)
        log.write("screen recording requested: granted \(granted)" + (captureRefused ? " (after a refused capture)" : ""))
        updateRecognition()
    }

    /// The end of a tracked hover (HoverDriver, before its end message goes
    /// up): a refused capture (`HoverOutcome.refusesCapture`) brings back the
    /// menu's permission line and item and stops the tracking until a
    /// relaunch. Only with capture on: the self-test's hovers never count.
    func screenHoverEnded(_ outcome: HoverOutcome) {
        guard screenSource != nil, !captureRefused, outcome.refusesCapture else { return }
        captureRefused = true
        log.write("screen capture refused while tracking: the permission line and item return until a relaunch")
        updateRecognition()
    }

    /// A status event without a ref. CoreHost's free text (and every other
    /// status) goes to «Проверка перевода» and app.log only.
    private func serviceEvent(_ event: CoreEvent) {
        guard let message = ServiceMessage(event) else {
            log.write("unsolicited event \(event.name)")
            return
        }
        serviceMessages.append(message)
        diagnosticWindow.model.messages = serviceMessages.messages.map(\.line)
        log.write("service status [\(message.area)\(message.state.map { "/\($0)" } ?? "")"
                  + "\(message.code.map { "/\($0)" } ?? "")]: \(message.text)")
        if message.area != "translation" { refreshModels() }
    }

    /// models.status for the menu's model line; cheap (a snapshot), so it
    /// runs when the menu opens and after anything that may change it.
    @discardableResult
    func refreshModels() -> Task<ModelsStatus?, Never> {
        let supervisor = self.supervisor!
        return Task { [weak self] in
            guard let result = try? await supervisor.request("models.status", deadline: CoreDeadlines.control),
                  let status = ModelsStatus(result) else { return nil }
            guard let self, self.coreState.isReady else { return status }
            self.models = status
            self.statusMenu.update(models: status)
            self.settingsWindow.model.senseLine = self.statusMenu.senseText
            return status
        }
    }

    // MARK: Actions (menu, hotkeys, windows)

    func hotkeyPressed(_ action: HotkeyAction) {
        log.write("hotkey pressed: \(action.rawValue)" + (hotkeyObserver != nil ? " (self-test: recorded only)" : ""))
        if let hotkeyObserver {
            hotkeyObserver(action)
            return
        }
        switch action {
        case .toggleHover: toggleHover()
        case .showDiagnostic: showDiagnostic()
        case .translateClipboard: translateClipboard()
        case .editSpeaker: editSpeaker()
        }
    }

    /// ⌥T and «Перевод текста»: Core's hoverEnabled. Switching it on in the
    /// smart mode starts Core's warm-up of the sense model.
    @discardableResult
    func toggleHover() -> Task<SettingsSnapshot?, Never>? {
        guard let current = settings?.settings, coreState.isReady else { return nil }
        return updateSettings(SettingsPatch(hoverEnabled: !current.hoverEnabled))
    }

    @discardableResult
    func setMode(_ mode: TranslationMode) -> Task<SettingsSnapshot?, Never>? {
        guard coreState.isReady else { return nil }
        return updateSettings(SettingsPatch(mode: mode))
    }

    /// Every change of settings.json goes through CoreHost (its only writer).
    @discardableResult
    func updateSettings(_ patch: SettingsPatch) -> Task<SettingsSnapshot?, Never> {
        let client = settingsClient!
        settingsWindow.model.error = nil
        return Task { [weak self] in
            do {
                let snapshot = try await client.update(patch)
                self?.adopt(snapshot)
                self?.refreshModels()
                return snapshot
            } catch {
                let coreError = error as? CoreError
                self?.log.write("settings.update \(patch.payload) failed: \(error)")
                self?.settingsWindow.model.error = coreError.map { RU.diagnosticError($0.message, code: $0.code) }
                    ?? RU.errorText("unexpected")
                // Show what CoreHost holds now rather than the refused value.
                if let snapshot = await client.snapshot { self?.adopt(snapshot) }
                return nil
            }
        }
    }

    func showDiagnostic() { diagnosticWindow.show() }
    func showSettings() { settingsWindow.show() }

    /// «Перезапустить перевод»: also clears the crash-loop guard.
    func restartTranslation() {
        let supervisor = self.supervisor!
        Task { await supervisor.restartNow() }
    }

    /// ⌥V. Reading the general pasteboard from a global hotkey is
    /// programmatic access, which macOS 15.4+ may confirm with the user
    /// first; pasting into «Проверка перевода» (⌘V) never asks.
    func translateClipboard() {
        let pasteboard = NSPasteboard.general
        if #available(macOS 15.4, *), pasteboard.accessBehavior == .alwaysDeny {
            overlay.showMessage(RU.diagnosticClipboardDenied, near: NSEvent.mouseLocation)
            return
        }
        guard let text = pasteboard.string(forType: .string), !text.isEmpty else {
            overlay.showMessage(RU.diagnosticClipboardEmpty, near: NSEvent.mouseLocation)
            return
        }
        translate(text, near: NSEvent.mouseLocation)
    }

    /// Resolves one diagnostic selection and shows the result near `point`.
    /// The returned task ends with the hover's outcome once the overlay and
    /// the diagnostic window show its end.
    @discardableResult
    func translate(_ input: String, near point: NSPoint) -> Task<HoverOutcome, Never> {
        let selection: HoverSelection
        do {
            selection = try DiagnosticSelection.parse(input)
        } catch {
            diagnosticWindow.model.note = error.russian
            let refusal = CoreError(code: "bad_request", message: error.russian)
            return Task { .rejected(refusal) }
        }
        let smart = settings?.settings.translationMode == .smart
        let token = overlay.beginHover(word: selection.hoveredWord, smart: smart, near: point)
        diagnosticWindow.model.note = nil
        let hover = self.hover!
        let shown = Box(false)
        return Task { [weak self] in
            let outcome = await hover.resolve(selection, onSent: { id in
                DispatchQueue.main.async { MainActor.assumeIsolated { self?.lastHoverRequestId = id } }
            }) { update in
                // DispatchQueue.main is FIFO: the primary before the phrase, as
                // CoreHost sent them (unstructured Tasks are not ordered).
                DispatchQueue.main.async {
                    MainActor.assumeIsolated { self?.hoverUpdate(update, token: token, shown: shown) }
                }
            }
            // The updates were queued before the terminal resumed this task;
            // one more turn of the main queue runs every one of them first.
            await withCheckedContinuation { continuation in DispatchQueue.main.async { continuation.resume() } }
            guard let self else { return outcome }
            let report = HoverReport.after(outcome, shownPrimary: shown.value)
            self.log.write("hover \(selection.hoveredWord): \(outcome)")
            // A newer hover owns the overlay and the note.
            if token == self.overlay.hoverToken, let note = report.diagnosticNote {
                self.diagnosticWindow.model.note = note
            }
            self.overlay.finish(report.overlay, token: token, near: point)
            return outcome
        }
    }

    private func hoverUpdate(_ update: HoverUpdate, token: Int, shown: Box<Bool>) {
        guard token == overlay.hoverToken, overlay.apply(update, token: token),
              case .primary(let primary) = update else { return }
        shown.value = true
        cardShown(primary)
    }

    /// The request id of a hover.resolve just written.
    func hoverSent(_ id: Int64) { lastHoverRequestId = id }

    // MARK: The speaker editor (20.3, T17, T29-T31)

    /// A hover over the screen showed its card (HoverDriver): ⌃⌥G opens the
    /// editor on it while the overlay still shows it.
    func speakerCardShown(_ target: SpeakerTarget, token: Int) {
        speakerTarget = (target, token)
    }

    /// ⌃⌥G: the editor on the card of a hover over the screen that shows
    /// now; without a speaker or a game on it, the Russian reason on the
    /// overlay. Opening hides the card, keeps every later card of a screen
    /// hover off the overlay and pauses the tracking (which cancels the
    /// hover in flight) until the editor closes. `takeKeyboard` false (the
    /// self-test) shows the editor without making it key, so nothing of the
    /// owner's session changes. Returns why it did not open, if it did not.
    @discardableResult
    func editSpeaker(takeKeyboard: Bool = true) -> SpeakerEditor.Refusal? {
        if speakerEditor.isOpen {
            speakerEditor.bringToFront(takeKeyboard: takeKeyboard)
            return nil
        }
        let shown = speakerTarget.flatMap { overlay.showsCard(of: $0.token) ? $0.target : nil }
        switch SpeakerEditor.open(shown) {
        case .failure(let refusal):
            log.write("speaker editor: not opened (\(refusal))")
            let point = shown.map { NSPoint(x: $0.point.x, y: $0.point.y) } ?? NSEvent.mouseLocation
            overlay.showMessage(refusal.russian, near: point)
            return refusal
        case .success(let editor):
            guard let shown else { return .noSpeaker }
            // These three stay together (SourceScanTests pins them): the
            // self-test never tracks, so no run would notice the pause gone.
            overlay.suspend()
            speakerEditor.open(editor, target: shown, takeKeyboard: takeKeyboard)
            updateRecognition()
            log.write("speaker editor: open for \(editor.game.key), observed \(editor.observedName)")
            return nil
        }
    }

    /// A choice in the editor: speaker.set, then the editor closes and the
    /// status card names what was stored; a refusal or a failed save keeps
    /// the editor open with the Russian reason.
    private func speakerChosen(_ editor: SpeakerEditor, _ choice: SpeakerChoice, name: String) {
        switch editor.request(choice, name: name) {
        case .refuse(let text):
            speakerEditor.showError(text)
        case .send(let payload, let stored):
            speakerEditor.busy = true
            let client = SpeakerClient(core: supervisor)
            speakerSave = Task { [weak self] in
                do {
                    let saved = try await client.set(payload)
                    guard let self else { return true }
                    self.log.write("speaker.set \(choice.rawValue) in \(editor.game.key): \(saved.name)/\(saved.gender), "
                                   + "observed \(saved.observedName ?? "none")")
                    let point = self.speakerEditor.target.map { NSPoint(x: $0.point.x, y: $0.point.y) }
                        ?? NSEvent.mouseLocation
                    self.closeSpeakerEditor(reason: "saved")
                    self.overlay.showMessage(editor.savedStatus(name: stored, choice: choice), near: point)
                    return true
                } catch {
                    let coreError = error as? CoreError
                    self?.log.write("speaker.set failed: \(error)")
                    self?.speakerEditor.busy = false
                    self?.speakerEditor.showError(coreError.map { RU.diagnosticError($0.message, code: $0.code) }
                                                  ?? RU.errorText("unexpected"))
                    return false
                }
            }
        }
    }

    /// The editor closes (saved or cancelled): screen hovers may show again
    /// and the tracking resumes from a fresh anchor.
    func closeSpeakerEditor(reason: String) {
        guard speakerEditor.isOpen else { return }
        speakerEditor.close()
        overlay.resume()
        updateRecognition()
        log.write("speaker editor: closed (\(reason))")
    }

    /// A card the overlay showed: the diagnostic window, app.log and
    /// hover.displayed, so history records only what the user saw.
    func cardShown(_ primary: HoverPrimary) {
        lastPrimary = primary
        diagnosticWindow.model.card = RU.diagnosticCard(source: primary.source, translation: primary.translation,
                                                        badge: primary.badge)
        log.write("hover card: \(primary.source) [\(primary.provenance.wire)\(primary.senseProvider.map { "/\($0)" } ?? "")]"
                  + " \(primary.engineLabel)")
        // History records only what the user saw (hover.displayed).
        let hover = self.hover!
        Task { await hover.displayed(hoverId: primary.hoverId, part: .primary) }
    }

    // MARK: Termination

    func terminateFromSignal(_ number: Int32) {
        log?.write("signal \(number): quitting")
        NSApp.terminate(nil)
    }

    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        guard supervisor != nil else { return .terminateNow }
        guard !terminating else { return .terminateCancel }
        terminating = true
        Task { [weak self] in
            await self?.shutdown()
            sender.reply(toApplicationShouldTerminate: true)
        }
        return .terminateLater
    }

    /// The orderly end: hotkeys off, the overlay hidden, CoreHost stopped
    /// (core.shutdown, stdin EOF, then SIGTERM and SIGKILL to its group; it
    /// returns once the group is dead), the instance lock released.
    func shutdown() async {
        guard !shutDown else { return }
        shutDown = true
        hotkeys.unregisterAll()
        speakerEditor.close()
        hoverDriver.setTracking(false, pipeline: nil)
        overlay.hide()
        await supervisor.stop()
        instanceLock?.release()
        log.write("quit")
        log.flush()
        shutdownFinished = true
    }

    func applicationWillTerminate(_ notification: Notification) {
        onWillTerminate?()
    }
}

/// A value shared between a hover's callbacks and its task, on the main queue.
final class Box<Value>: @unchecked Sendable {
    var value: Value
    init(_ value: Value) { self.value = value }
}
