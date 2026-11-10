import Foundation

/// A global hotkey as Carbon's RegisterEventHotKey takes it. Carbon needs no
/// privacy permission, unlike an NSEvent global monitor (Accessibility) or an
/// event tap (Input Monitoring).
public struct HotkeySpec: Sendable, Hashable, Codable {
    /// Carbon modifier masks (Carbon.HIToolbox Events.h); a test compares them.
    public struct Modifiers: OptionSet, Sendable, Hashable, Codable {
        public let rawValue: UInt32
        public init(rawValue: UInt32) { self.rawValue = rawValue }
        public static let command = Modifiers(rawValue: 1 << 8)    // cmdKey
        public static let shift = Modifiers(rawValue: 1 << 9)      // shiftKey
        public static let option = Modifiers(rawValue: 1 << 11)    // optionKey
        public static let control = Modifiers(rawValue: 1 << 12)   // controlKey
    }

    public var keyCode: UInt32
    public var modifiers: Modifiers
    public var keyLabel: String

    public init(keyCode: UInt32, modifiers: Modifiers, keyLabel: String) {
        self.keyCode = keyCode
        self.modifiers = modifiers
        self.keyLabel = keyLabel
    }

    /// Apple's order: ⌃ ⌥ ⇧ ⌘, then the key.
    public var display: String {
        var text = ""
        if modifiers.contains(.control) { text += "⌃" }
        if modifiers.contains(.option) { text += "⌥" }
        if modifiers.contains(.shift) { text += "⇧" }
        if modifiers.contains(.command) { text += "⌘" }
        return text + keyLabel
    }
}

public enum HotkeyAction: String, CaseIterable, Sendable, Codable {
    case toggleHover
    case showDiagnostic
    case translateClipboard

    public var russianTitle: String {
        switch self {
        case .toggleHover: return RU.hotkeyToggleHover
        case .showDiagnostic: return RU.hotkeyShowDiagnostic
        case .translateClipboard: return RU.hotkeyTranslateClipboard
        }
    }

    /// Owner decision M14: ⌥T, ⌥D and ⌥V. The owner accepts that these
    /// system-wide hotkeys stop † ∂ √ from being typed in other apps. Option-
    /// only combinations were reported broken on macOS 15; RegisterEventHotKey
    /// accepted all three (status 0) on macOS 27.0.1 when 20.2 was built, and
    /// a registration failure is shown as Russian text, never answered by
    /// silently picking other keys. Key codes are Carbon's kVK_ANSI_*.
    public var defaultSpec: HotkeySpec {
        switch self {
        case .toggleHover: return HotkeySpec(keyCode: 0x11, modifiers: [.option], keyLabel: "T")
        case .showDiagnostic: return HotkeySpec(keyCode: 0x02, modifiers: [.option], keyLabel: "D")
        case .translateClipboard: return HotkeySpec(keyCode: 0x09, modifiers: [.option], keyLabel: "V")
        }
    }

    /// Carbon EventHotKeyID.id; the signature is 'ASTS'.
    public var carbonId: UInt32 { UInt32(HotkeyAction.allCases.firstIndex(of: self)! + 1) }
    public static let carbonSignature: UInt32 = 0x4153_5453

    public init?(carbonId: UInt32) {
        let index = Int(carbonId) - 1
        guard HotkeyAction.allCases.indices.contains(index) else { return nil }
        self = HotkeyAction.allCases[index]
    }
}

/// Swift-owned preferences (hotkeys, overlay level) in macos-app.json. The
/// translation settings are Core's and live in settings.json, which only
/// CoreHost writes.
public struct AppPreferences: Codable, Sendable, Equatable {
    public var hotkeys: [HotkeyAction: HotkeySpec]
    public var overlayLevel: String

    public init(hotkeys: [HotkeyAction: HotkeySpec] = Dictionary(uniqueKeysWithValues: HotkeyAction.allCases.map { ($0, $0.defaultSpec) }),
                overlayLevel: String = "screenSaver") {
        self.hotkeys = hotkeys
        self.overlayLevel = overlayLevel
    }

    /// A missing or unreadable file gives the defaults.
    public static func load(from url: URL) -> AppPreferences {
        guard let data = try? Data(contentsOf: url),
              let preferences = try? JSONDecoder().decode(AppPreferences.self, from: data) else { return AppPreferences() }
        return preferences
    }

    public func save(to url: URL) throws {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try encoder.encode(self).write(to: url, options: .atomic)
    }
}
