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
    /// The speaker editor (T17, T31). Last on purpose: `carbonId` is the
    /// position in `allCases`, so a new action always goes at the end.
    case editSpeaker

    public var russianTitle: String {
        switch self {
        case .toggleHover: return RU.hotkeyToggleHover
        case .showDiagnostic: return RU.hotkeyShowDiagnostic
        case .translateClipboard: return RU.hotkeyTranslateClipboard
        case .editSpeaker: return RU.hotkeyEditSpeaker
        }
    }

    /// Owner decisions M14 and M20: ⌥T, ⌥D and ⌥V (M14), and ⌃⌥G for the
    /// speaker editor (M20, T40), which types no character in any layout
    /// and is not Option-only. The owner accepts that the first three stop
    /// † ∂ √ from being typed in other apps. Option-only combinations were
    /// reported broken on macOS 15; RegisterEventHotKey accepted ⌥T, ⌥D and
    /// ⌥V (status 0) on macOS 27.0.1 when 20.2 was built, and all four in
    /// 20.3's self-test, and a registration failure is shown as Russian
    /// text, never answered by silently picking other keys. Key codes are
    /// Carbon's kVK_ANSI_*.
    public var defaultSpec: HotkeySpec {
        switch self {
        case .toggleHover: return HotkeySpec(keyCode: 0x11, modifiers: [.option], keyLabel: "T")
        case .showDiagnostic: return HotkeySpec(keyCode: 0x02, modifiers: [.option], keyLabel: "D")
        case .translateClipboard: return HotkeySpec(keyCode: 0x09, modifiers: [.option], keyLabel: "V")
        case .editSpeaker: return HotkeySpec(keyCode: 0x05, modifiers: [.control, .option], keyLabel: "G")
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

/// The overlay's window level as macos-app.json names it. AsteriaScaleKit has
/// no AppKit, so the app maps a level to its NSWindow.Level.
public enum OverlayLevel: String, CaseIterable, Sendable {
    /// NSWindow.Level.screenSaver (window server layer 1000): above a
    /// CrossOver or winemac full-screen window (status level + 1).
    case screenSaver
    /// Above a captured display's shielding window: a bottle with
    /// CaptureDisplaysForFullscreen sits at shielding + 1 (docs/MACOS.md Q3;
    /// measured only in 20.3/20.4).
    case shielding
}

/// Swift-owned preferences (hotkeys, overlay level) in macos-app.json. The
/// translation settings are Core's and live in settings.json, which only
/// CoreHost writes. The app never writes this file; the owner does, as a
/// JSON object:
/// {"hotkeys": {"translateClipboard": {"keyCode": 9, "modifiers": 2304, "keyLabel": "V"}},
///  "overlayLevel": "screenSaver"}
/// Either key may be missing (its default is used), and an action missing
/// from "hotkeys" keeps its default (`defaultSpec`: M14 for ⌥T, ⌥D and ⌥V,
/// M20 for editSpeaker's ⌃⌥G).
public struct AppPreferences: Codable, Sendable, Equatable {
    public static let defaultHotkeys = Dictionary(uniqueKeysWithValues: HotkeyAction.allCases.map { ($0, $0.defaultSpec) })
    public static let defaultOverlayLevel = OverlayLevel.screenSaver.rawValue

    public var hotkeys: [HotkeyAction: HotkeySpec]
    /// As written; `resolvedOverlayLevel` says what it means.
    public var overlayLevel: String

    public init(hotkeys: [HotkeyAction: HotkeySpec] = AppPreferences.defaultHotkeys,
                overlayLevel: String = AppPreferences.defaultOverlayLevel) {
        self.hotkeys = hotkeys
        self.overlayLevel = overlayLevel
    }

    /// The level to use, and a note for app.log when the file names one this
    /// build does not know (the default is used then, never a guess).
    public var resolvedOverlayLevel: (level: OverlayLevel, note: String?) {
        if let level = OverlayLevel(rawValue: overlayLevel) { return (level, nil) }
        let known = OverlayLevel.allCases.map(\.rawValue).joined(separator: ", ")
        return (.screenSaver, "preferences: unknown overlayLevel \"\(overlayLevel)\" (known: \(known)); using screenSaver")
    }

    private enum CodingKeys: String, CodingKey {
        case hotkeys, overlayLevel
    }

    private struct ActionKey: CodingKey {
        let stringValue: String
        init(stringValue: String) { self.stringValue = stringValue }
        var intValue: Int? { nil }
        init?(intValue: Int) { nil }
    }

    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        if !container.contains(.hotkeys) {
            hotkeys = AppPreferences.defaultHotkeys
        } else if let object = try? container.nestedContainer(keyedBy: ActionKey.self, forKey: .hotkeys) {
            var decoded: [HotkeyAction: HotkeySpec] = [:]
            for key in object.allKeys {
                guard let action = HotkeyAction(rawValue: key.stringValue) else {
                    let known = HotkeyAction.allCases.map(\.rawValue).joined(separator: ", ")
                    throw DecodingError.dataCorruptedError(forKey: key, in: object,
                                                           debugDescription: "unknown hotkey action (known: \(known))")
                }
                decoded[action] = try object.decode(HotkeySpec.self, forKey: key)
            }
            hotkeys = decoded
        } else {
            // The [action, spec, …] array Swift's synthesized Codable makes
            // of an enum-keyed dictionary, which an earlier build wrote.
            hotkeys = try container.decode([HotkeyAction: HotkeySpec].self, forKey: .hotkeys)
        }
        overlayLevel = try container.decodeIfPresent(String.self, forKey: .overlayLevel) ?? AppPreferences.defaultOverlayLevel
    }

    public func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        var object = container.nestedContainer(keyedBy: ActionKey.self, forKey: .hotkeys)
        for (action, spec) in hotkeys {
            try object.encode(spec, forKey: ActionKey(stringValue: action.rawValue))
        }
        try container.encode(overlayLevel, forKey: .overlayLevel)
    }

    /// A missing file gives the defaults. A file that cannot be read or
    /// decoded gives them too, but `log` says why (the app passes app.log),
    /// so a typo never silently resets the owner's hotkeys.
    public static func load(from url: URL, log: (String) -> Void) -> AppPreferences {
        guard FileManager.default.fileExists(atPath: url.path) else { return AppPreferences() }
        do {
            return try JSONDecoder().decode(AppPreferences.self, from: Data(contentsOf: url))
        } catch {
            log("preferences: cannot use \(url.path): \(error); using the defaults")
            return AppPreferences()
        }
    }

    public func save(to url: URL) throws {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try encoder.encode(self).write(to: url, options: .atomic)
    }
}
