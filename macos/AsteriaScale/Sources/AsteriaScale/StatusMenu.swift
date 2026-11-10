import AppKit
import AsteriaScaleKit

/// The menu-bar icon, the macOS counterpart of TrayIconService on Windows.
/// Its lines name the translation in the owner's words (M15); a model error
/// is CoreHost's Russian sentence with the code, and CoreHost's other free
/// text never appears here (only in «Проверка перевода» and app.log).
@MainActor
final class StatusMenuController: NSObject, NSMenuDelegate {
    let item: NSStatusItem
    let menu = NSMenu()
    private weak var app: AppDelegate?
    private let stateLine = NSMenuItem(title: RU.stateStarting, action: nil, keyEquivalent: "")
    private let detailLine = NSMenuItem(title: "", action: nil, keyEquivalent: "")
    private let senseLine = NSMenuItem(title: RU.senseUnknown, action: nil, keyEquivalent: "")
    private var hotkeyLines: [NSMenuItem] = []
    private let hoverItem = NSMenuItem(title: RU.menuHoverTranslation, action: nil, keyEquivalent: "")
    private let modeItem = NSMenuItem(title: RU.menuTranslationMode, action: nil, keyEquivalent: "")
    private var modeItems: [TranslationMode: NSMenuItem] = [:]
    private let diagnosticItem = NSMenuItem(title: RU.menuCheckTranslation, action: nil, keyEquivalent: "")
    private let clipboardItem = NSMenuItem(title: RU.menuTranslateClipboard, action: nil, keyEquivalent: "")
    private let settingsItem = NSMenuItem(title: RU.menuSettings, action: nil, keyEquivalent: ",")
    private let restartItem = NSMenuItem(title: RU.menuRestartTranslation, action: nil, keyEquivalent: "")
    private let quitItem = NSMenuItem(title: RU.menuQuit, action: #selector(NSApplication.terminate(_:)),
                                      keyEquivalent: "q")
    private let hotkeySeparator = NSMenuItem.separator()

    init(app: AppDelegate) {
        self.app = app
        item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        super.init()
        // An SF Symbol as a template image: no asset catalog, no actool.
        let image = NSImage(systemSymbolName: "character.magnify", accessibilityDescription: RU.appName)
        image?.isTemplate = true
        item.button?.image = image
        item.button?.toolTip = RU.appName

        // Enabling is explicit: items that need the translation service are
        // off while it is not ready, and the info lines are never clickable.
        menu.autoenablesItems = false
        menu.delegate = self
        for line in [stateLine, detailLine, senseLine] { line.isEnabled = false }
        detailLine.isHidden = true
        menu.addItem(stateLine)
        menu.addItem(detailLine)
        menu.addItem(senseLine)
        menu.addItem(hotkeySeparator)
        hotkeySeparator.isHidden = true
        menu.addItem(.separator())

        hoverItem.target = self
        hoverItem.action = #selector(toggleHover)
        menu.addItem(hoverItem)
        let modes = NSMenu()
        modes.autoenablesItems = false
        for mode in TranslationMode.allCases {
            let modeChoice = NSMenuItem(title: mode.russianTitle, action: #selector(chooseMode(_:)), keyEquivalent: "")
            modeChoice.target = self
            modeChoice.representedObject = mode.rawValue
            modes.addItem(modeChoice)
            modeItems[mode] = modeChoice
        }
        modeItem.submenu = modes
        menu.addItem(modeItem)
        menu.addItem(.separator())

        diagnosticItem.target = self
        diagnosticItem.action = #selector(showDiagnostic)
        clipboardItem.target = self
        clipboardItem.action = #selector(translateClipboard)
        menu.addItem(diagnosticItem)
        menu.addItem(clipboardItem)
        menu.addItem(.separator())
        settingsItem.target = self
        settingsItem.action = #selector(showSettings)
        restartItem.target = self
        restartItem.action = #selector(restartTranslation)
        menu.addItem(settingsItem)
        menu.addItem(restartItem)
        menu.addItem(.separator())
        menu.addItem(quitItem)
        item.menu = menu
        update(state: .starting, settings: nil)
    }

    /// Every title the menu shows now (the self-test checks them).
    var visibleTitles: [String] {
        func titles(_ menu: NSMenu) -> [String] {
            menu.items.filter { !$0.isHidden && !$0.isSeparatorItem }
                .flatMap { [$0.title] + ($0.submenu.map(titles) ?? []) }
        }
        return titles(menu)
    }

    var stateText: String { stateLine.title }
    var senseText: String { senseLine.title }
    var hoverChecked: Bool { hoverItem.state == .on }
    var checkedMode: TranslationMode? { modeItems.first { $0.value.state == .on }?.key }
    var hotkeyItemsEnabled: Bool { diagnosticItem.isEnabled && clipboardItem.isEnabled }

    func update(state: CoreState, settings: TranslationSettings?) {
        stateLine.title = state.russian
        detailLine.title = state.details ?? ""
        detailLine.isHidden = state.details == nil
        let ready = state.isReady && settings != nil
        hoverItem.isEnabled = ready
        modeItem.isEnabled = ready
        for (_, choice) in modeItems { choice.isEnabled = ready }
        hoverItem.state = settings?.hoverEnabled == true ? .on : .off
        for (mode, choice) in modeItems { choice.state = settings?.translationMode == mode ? .on : .off }
        if !state.isReady { senseLine.title = RU.senseUnknown }
    }

    func update(models: ModelsStatus?) {
        senseLine.title = models?.senseLine ?? RU.senseUnknown
    }

    /// Shows each registered gesture as the item's key equivalent (Carbon
    /// owns the global key; the menu only displays it) and each refused one
    /// as a Russian line at the top (M14: never replaced by another key).
    func update(hotkeys: HotkeyCenter) {
        let items: [HotkeyAction: NSMenuItem] = [.toggleHover: hoverItem, .showDiagnostic: diagnosticItem,
                                                 .translateClipboard: clipboardItem]
        for (action, menuItem) in items {
            if let spec = hotkeys.registered[action], let key = spec.keyLabel.lowercased().first {
                menuItem.keyEquivalent = String(key)
                menuItem.keyEquivalentModifierMask = spec.modifiers.eventModifiers
            } else {
                menuItem.keyEquivalent = ""
            }
        }
        for line in hotkeyLines { menu.removeItem(line) }
        hotkeyLines = HotkeyAction.allCases.compactMap { hotkeys.problems[$0] }.map { problem in
            let line = NSMenuItem(title: problem, action: nil, keyEquivalent: "")
            line.isEnabled = false
            return line
        }
        let anchor = menu.index(of: hotkeySeparator)
        for (offset, line) in hotkeyLines.enumerated() { menu.insertItem(line, at: anchor + 1 + offset) }
        hotkeySeparator.isHidden = hotkeyLines.isEmpty
    }

    func menuWillOpen(_ menu: NSMenu) { app?.refreshModels() }

    @objc private func toggleHover() { app?.toggleHover() }
    @objc private func chooseMode(_ sender: NSMenuItem) {
        guard let raw = sender.representedObject as? String, let mode = TranslationMode(rawValue: raw) else { return }
        app?.setMode(mode)
    }
    @objc private func showDiagnostic() { app?.showDiagnostic() }
    @objc private func translateClipboard() { app?.translateClipboard() }
    @objc private func showSettings() { app?.showSettings() }
    @objc private func restartTranslation() { app?.restartTranslation() }
}

extension HotkeySpec.Modifiers {
    var eventModifiers: NSEvent.ModifierFlags {
        var flags: NSEvent.ModifierFlags = []
        if contains(.control) { flags.insert(.control) }
        if contains(.option) { flags.insert(.option) }
        if contains(.shift) { flags.insert(.shift) }
        if contains(.command) { flags.insert(.command) }
        return flags
    }
}
