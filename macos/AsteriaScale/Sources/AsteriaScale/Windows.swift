import AppKit
import AsteriaScaleKit
import Observation
import SwiftUI

/// An accessory (LSUIElement) app has no Dock icon and no app menu, so its
/// windows activate the app explicitly to come to the front.
@MainActor
func bringToFront(_ window: NSWindow) {
    NSApp.activate()
    window.makeKeyAndOrderFront(nil)
}

// MARK: Settings

@MainActor @Observable
final class SettingsModel {
    var ready = false
    var hoverEnabled = false
    var mode: TranslationMode = .smart
    var delayMs = 700.0
    var stateLine = RU.stateStarting
    var stateDetails: String?
    var senseLine = RU.senseUnknown
    var error: String?
    var hotkeyLines: [String] = []
    var folderLines: [String] = []

    /// Every text the window shows now (the self-test checks them).
    var visibleTexts: [String] {
        [RU.settingsTranslation, RU.menuHoverTranslation, RU.menuTranslationMode, RU.settingsDelay,
         RU.milliseconds(Int(delayMs)), RU.settingsSense, senseLine, RU.settingsHotkeys, RU.settingsService,
         stateLine, RU.menuRestartTranslation, RU.settingsFolders]
            + TranslationMode.allCases.map(\.russianTitle) + hotkeyLines + folderLines
            + [stateDetails, error].compactMap { $0 }
    }
}

struct SettingsView: View {
    @Bindable var model: SettingsModel
    let actions: SettingsActions

    var body: some View {
        Form {
            Section(RU.settingsTranslation) {
                // The values are CoreHost's: each change is a settings.update,
                // and the controls show what CoreHost answered.
                Toggle(RU.menuHoverTranslation, isOn: Binding(
                    get: { model.hoverEnabled },
                    set: { actions.setHoverEnabled($0) }))
                Picker(RU.menuTranslationMode, selection: Binding(
                    get: { model.mode },
                    set: { actions.setMode($0) })) {
                    ForEach(TranslationMode.allCases, id: \.self) { Text($0.russianTitle).tag($0) }
                }
                Slider(value: $model.delayMs, in: Double(TranslationSettings.delayRange.lowerBound)...Double(
                    TranslationSettings.delayRange.upperBound), step: 50) {
                    Text(RU.settingsDelay)
                } onEditingChanged: { editing in
                    if !editing { actions.setDelay(Int(model.delayMs)) }
                }
                Text(RU.milliseconds(Int(model.delayMs))).foregroundStyle(.secondary)
                if let error = model.error {
                    Text(error).foregroundStyle(.red)
                }
            }
            .disabled(!model.ready)
            Section(RU.settingsSense) {
                Text(model.senseLine)
            }
            Section(RU.settingsHotkeys) {
                ForEach(model.hotkeyLines, id: \.self) { Text($0) }
            }
            Section(RU.settingsService) {
                Text(model.stateLine)
                if let details = model.stateDetails {
                    Text(details).foregroundStyle(.secondary)
                }
                Button(RU.menuRestartTranslation, action: actions.restart)
            }
            Section(RU.settingsFolders) {
                ForEach(model.folderLines, id: \.self) {
                    Text($0).font(.system(size: 11)).textSelection(.enabled)
                }
            }
        }
        .formStyle(.grouped)
        .frame(width: 540, height: 600)
    }
}

/// What the settings window asks the app to do; the app sends settings.update.
struct SettingsActions {
    var setHoverEnabled: @MainActor (Bool) -> Void
    var setMode: @MainActor (TranslationMode) -> Void
    var setDelay: @MainActor (Int) -> Void
    var restart: @MainActor () -> Void
}

@MainActor
final class SettingsWindowController {
    let model = SettingsModel()
    let window: NSWindow

    init(actions: SettingsActions) {
        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 540, height: 600),
                          styleMask: [.titled, .closable], backing: .buffered, defer: true)
        window.title = RU.settingsTitle
        window.isReleasedWhenClosed = false
        window.contentViewController = NSHostingController(rootView: SettingsView(model: model, actions: actions))
        window.center()
    }

    func update(state: CoreState) {
        model.stateLine = state.russian
        model.stateDetails = state.details
        if !state.isReady { model.ready = false }
    }

    func update(settings: TranslationSettings?, ready: Bool) {
        model.ready = ready && settings != nil
        guard let settings else { return }
        model.hoverEnabled = settings.hoverEnabled
        if let mode = settings.translationMode { model.mode = mode }
        model.delayMs = Double(settings.hoverDelayMs)
    }

    func update(hotkeys: HotkeyCenter) {
        model.hotkeyLines = HotkeyAction.allCases.map { action in
            if let problem = hotkeys.problems[action] { return RU.hotkeyLine(action.russianTitle, problem) }
            return RU.hotkeyLine(action.russianTitle, hotkeys.registered[action]?.display ?? RU.hotkeyNotAssigned)
        }
    }

    func update(paths: AppPaths) {
        model.folderLines = [RU.folderLine(RU.folderUserData, paths.userData.path),
                             RU.folderLine(RU.folderModels, paths.rankerModels.path),
                             RU.folderLine(RU.folderLogs, paths.logs.path)]
    }

    func show() { bringToFront(window) }
}

// MARK: «Проверка перевода»

@MainActor @Observable
final class DiagnosticModel {
    var text = "The [bank] was steep and muddy."
    /// The end of the last hover started here (hover_disabled, an error, …).
    var note: String?
    /// The last card the overlay showed, as one line.
    var card: String?
    var messages: [String] = []

    var visibleTexts: [String] {
        [RU.diagnosticPrompt, RU.diagnosticTranslate, RU.diagnosticServiceMessages]
            + [note, card.map { RU.diagnosticResult + ": " + $0 }].compactMap { $0 }
            + (messages.isEmpty ? [RU.diagnosticNoMessages] : messages)
    }
}

struct DiagnosticView: View {
    @Bindable var model: DiagnosticModel
    var translate: @MainActor (String) -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(RU.diagnosticPrompt).font(.callout).foregroundStyle(.secondary)
            TextField("", text: $model.text, axis: .vertical)
                .lineLimit(3...6)
                .onSubmit { translate(model.text) }
            Button(RU.diagnosticTranslate) { translate(model.text) }
                .keyboardShortcut(.defaultAction)
            if let note = model.note {
                Text(note)
            }
            if let card = model.card {
                Text(RU.diagnosticResult + ": " + card).textSelection(.enabled)
            }
            Divider()
            Text(RU.diagnosticServiceMessages).font(.headline)
            ScrollView {
                VStack(alignment: .leading, spacing: 2) {
                    if model.messages.isEmpty {
                        Text(RU.diagnosticNoMessages).foregroundStyle(.secondary)
                    }
                    ForEach(Array(model.messages.enumerated()), id: \.offset) { _, line in
                        Text(line).font(.system(size: 11)).textSelection(.enabled)
                    }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
            }
            .frame(height: 160)
        }
        .padding(16)
        .frame(width: 520)
    }
}

/// The 20.2 hover source: a typed or pasted sentence goes through
/// hover.resolve and the result appears in the overlay (Apple Vision replaces
/// it in 20.3). CoreHost's free-text messages are shown here and nowhere else.
@MainActor
final class DiagnosticWindowController {
    let model = DiagnosticModel()
    let window: NSWindow

    init(translate: @escaping @MainActor (String) -> Void) {
        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 520, height: 420),
                          styleMask: [.titled, .closable], backing: .buffered, defer: true)
        window.title = RU.diagnosticTitle
        window.isReleasedWhenClosed = false
        window.contentViewController = NSHostingController(rootView: DiagnosticView(model: model, translate: translate))
        window.center()
    }

    func show() { bringToFront(window) }
}
