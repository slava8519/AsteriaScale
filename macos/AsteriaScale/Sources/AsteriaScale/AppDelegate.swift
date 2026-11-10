import AppKit
import AsteriaScaleKit
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
        statusMenu = StatusMenuController(app: self)
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
    }

    private func adopt(_ snapshot: SettingsSnapshot) {
        settings = snapshot
        let client = settingsClient!
        Task { await client.adopt(snapshot) }
        statusMenu.update(state: coreState, settings: snapshot.settings)
        settingsWindow.update(settings: snapshot.settings, ready: coreState.isReady)
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
        guard token == overlay.hoverToken else { return }
        overlay.apply(update, token: token)
        guard case .primary(let primary) = update else { return }
        shown.value = true
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
