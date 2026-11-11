import AppKit
import AsteriaScaleKit

/// The speaker editor's window: a non-activating panel, so it can take the
/// keyboard without activating the app and the game stays the active app
/// (T31), and it never becomes main.
final class SpeakerEditorPanel: NSPanel {
    /// The editor's key handling; true when it took the key.
    var onKey: ((_ keyCode: UInt16, _ editingName: Bool) -> Bool)?

    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { false }

    /// Whether the name field is being edited (its field editor has the keyboard).
    var isEditingName: Bool { firstResponder is NSText }

    /// Every key press passes the editor first, the name field's included:
    /// Escape cancels there too, and 1, 2, 3 choose unless the name is being
    /// edited (SpeakerEditor.command decides).
    override func sendEvent(_ event: NSEvent) {
        if event.type == .keyDown, let onKey, onKey(event.keyCode, isEditingName) { return }
        super.sendEvent(event)
    }
}

/// «Род говорящего» (roadmap 20.3 DoD 5; docs/MACOS.md T17, T31): the
/// card's name (editable, to correct a misread spelling), the game, the
/// three genders and «Отмена», keys 1, 2, 3 and Esc. Kit's SpeakerEditor
/// decides what opens, what a key does and what is sent; the app sends it
/// and closes the editor. The cursor is never moved (T31: Windows puts it
/// back at the anchor). When the editor took the keyboard, the app that had
/// it gets it back if this app became active meanwhile.
@MainActor
final class SpeakerEditorController: NSObject, NSTextFieldDelegate {
    let panel: SpeakerEditorPanel
    private let nameField = NSTextField(string: "")
    private let gameLabel = NSTextField(wrappingLabelWithString: "")
    private let errorLabel = NSTextField(wrappingLabelWithString: "")
    private var choiceButtons: [NSButton] = []
    private let cancelButton = NSButton(title: RU.speakerCancel, target: nil, action: nil)
    /// The open editor and its card; nil while closed.
    private(set) var editor: SpeakerEditor?
    private(set) var target: SpeakerTarget?
    /// This open made the panel key (the self-test never does).
    private(set) var tookKeyboard = false
    /// The app that was active when the editor took the keyboard.
    private var previousApp: NSRunningApplication?
    /// speaker.set is in flight: keys and buttons wait for its answer.
    var busy = false {
        didSet { for button in choiceButtons + [cancelButton] { button.isEnabled = !busy } }
    }
    /// A choice: the app checks and sends it, then closes the editor.
    var onChoose: ((SpeakerEditor, SpeakerChoice, String) -> Void)?
    /// Escape or «Отмена».
    var onCancel: (() -> Void)?

    init(level: NSWindow.Level) {
        panel = SpeakerEditorPanel(contentRect: NSRect(x: 0, y: 0, width: 420, height: 220),
                                   styleMask: [.titled, .nonactivatingPanel, .utilityWindow], backing: .buffered,
                                   defer: true)
        super.init()
        panel.title = RU.speakerEditorTitle
        panel.isFloatingPanel = true
        panel.level = level
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        panel.hidesOnDeactivate = false
        panel.becomesKeyOnlyIfNeeded = false
        panel.isReleasedWhenClosed = false
        panel.animationBehavior = .none
        panel.onKey = { [weak self] keyCode, editingName in
            self?.keyPressed(keyCode: keyCode, editingName: editingName) ?? false
        }
        buildContent()
    }

    var isOpen: Bool { editor != nil }
    /// The name as the field holds it now.
    var name: String { nameField.stringValue }
    var errorText: String? { errorLabel.isHidden ? nil : errorLabel.stringValue }
    /// Every text the editor shows (the self-test's ui.text check), the
    /// name the user types excepted.
    var visibleTexts: [String] {
        [panel.title, RU.speakerEditorName, gameLabel.stringValue, RU.speakerEditorNote, RU.speakerEditorKeys,
         cancelButton.title] + choiceButtons.map(\.title) + [errorText].compactMap { $0 }
    }

    private func buildContent() {
        let nameLabel = NSTextField(labelWithString: RU.speakerEditorName)
        nameField.delegate = self
        nameField.widthAnchor.constraint(equalToConstant: 300).isActive = true
        let nameRow = NSStackView(views: [nameLabel, nameField])
        nameRow.spacing = 8
        let note = NSTextField(wrappingLabelWithString: RU.speakerEditorNote)
        let keys = NSTextField(labelWithString: RU.speakerEditorKeys)
        for label in [note, keys] {
            label.font = .systemFont(ofSize: 11)
            label.textColor = .secondaryLabelColor
        }
        for label in [gameLabel, note, errorLabel] { label.preferredMaxLayoutWidth = 380 }
        errorLabel.textColor = .systemRed
        errorLabel.isHidden = true
        choiceButtons = SpeakerChoice.allCases.map { choice in
            let button = NSButton(title: "\(choice.digit)  \(choice.russianTitle)", target: self,
                                  action: #selector(choiceClicked(_:)))
            button.tag = choice.digit
            return button
        }
        cancelButton.target = self
        cancelButton.action = #selector(cancelClicked)
        let choices = NSStackView(views: choiceButtons + [cancelButton])
        choices.spacing = 8
        let stack = NSStackView(views: [nameRow, gameLabel, note, choices, keys, errorLabel])
        stack.orientation = .vertical
        stack.alignment = .leading
        stack.spacing = 10
        stack.edgeInsets = NSEdgeInsets(top: 14, left: 16, bottom: 14, right: 16)
        panel.contentView = stack
    }

    /// Shows the editor near `point` (Cocoa global). `takeKeyboard`: the
    /// panel becomes key (⌃⌥G); the self-test passes false, so nothing of
    /// the owner's session changes: no app is activated and no window loses
    /// the keyboard.
    func open(_ editor: SpeakerEditor, target: SpeakerTarget, takeKeyboard: Bool) {
        self.editor = editor
        self.target = target
        busy = false
        nameField.stringValue = editor.initialName
        gameLabel.stringValue = RU.speakerEditorGame(editor.game.displayName)
        errorLabel.stringValue = ""
        errorLabel.isHidden = true
        place(near: NSPoint(x: target.point.x, y: target.point.y))
        tookKeyboard = takeKeyboard
        if takeKeyboard {
            previousApp = NSWorkspace.shared.frontmostApplication
            panel.makeKeyAndOrderFront(nil)
            // Not into the name field: 1, 2 and 3 choose at once; a click
            // there edits the name, and Return ends the edit.
            panel.makeFirstResponder(nil)
        } else {
            previousApp = nil
            panel.orderFrontRegardless()
        }
    }

    /// ⌃⌥G while the editor is open: it comes to the front again.
    func bringToFront(takeKeyboard: Bool) {
        if takeKeyboard {
            if !tookKeyboard { previousApp = NSWorkspace.shared.frontmostApplication }
            tookKeyboard = true
            panel.makeKeyAndOrderFront(nil)
        } else {
            panel.orderFrontRegardless()
        }
    }

    /// The panel's key path (and the self-test's): what SpeakerEditor says
    /// the key does; true when it took the key.
    @discardableResult
    func keyPressed(keyCode: UInt16, editingName: Bool) -> Bool {
        guard isOpen, let command = SpeakerEditor.command(keyCode: keyCode, editingName: editingName) else { return false }
        guard !busy else { return true }
        switch command {
        case .choose(let choice): choose(choice)
        case .cancel: onCancel?()
        }
        return true
    }

    /// The name as if typed (the self-test's correction of a misread spelling).
    func setName(_ text: String) { nameField.stringValue = text }

    func showError(_ text: String) {
        errorLabel.stringValue = text
        errorLabel.isHidden = false
    }

    /// Hides the editor; the app that had the keyboard gets it back when
    /// this open took it and the app became active since (SpeakerEditor).
    func close() {
        let reactivate = SpeakerEditor.appToReactivate(tookKeyboard: tookKeyboard, appIsActive: NSApp.isActive,
                                                       previousPid: previousApp?.processIdentifier, ownPid: getpid())
        let previous = previousApp
        editor = nil
        target = nil
        busy = false
        tookKeyboard = false
        previousApp = nil
        panel.orderOut(nil)
        if let reactivate, let previous, previous.processIdentifier == reactivate, !previous.isTerminated {
            NSApp.yieldActivation(to: previous)
            _ = previous.activate()
        }
    }

    private func choose(_ choice: SpeakerChoice) {
        guard let editor else { return }
        onChoose?(editor, choice, nameField.stringValue)
    }

    @objc private func choiceClicked(_ sender: NSButton) {
        guard !busy, let choice = SpeakerChoice.allCases.first(where: { $0.digit == sender.tag }) else { return }
        choose(choice)
    }

    @objc private func cancelClicked() {
        guard !busy else { return }
        onCancel?()
    }

    /// Return in the name field ends the edit, so 1, 2 and 3 choose again.
    func control(_ control: NSControl, textView: NSTextView, doCommandBy selector: Selector) -> Bool {
        guard selector == #selector(NSResponder.insertNewline(_:)) else { return false }
        panel.makeFirstResponder(nil)
        return true
    }

    /// Below and to the right of the point, inside the visible frame of its screen.
    private func place(near point: NSPoint) {
        let size = panel.contentView?.fittingSize ?? panel.frame.size
        panel.setContentSize(size)
        let frame = panel.frame.size
        let screen = NSScreen.screens.first { NSMouseInRect(point, $0.frame, false) } ?? NSScreen.main
        let visible = screen?.visibleFrame ?? NSRect(x: 0, y: 0, width: 1440, height: 900)
        var origin = NSPoint(x: point.x + 16, y: point.y - 20 - frame.height)
        origin.x = min(max(origin.x, visible.minX + 4), visible.maxX - frame.width - 4)
        if origin.y < visible.minY + 4 { origin.y = point.y + 20 }
        origin.y = min(origin.y, visible.maxY - frame.height - 4)
        panel.setFrameOrigin(origin)
    }
}
