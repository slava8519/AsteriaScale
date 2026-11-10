import Foundation

/// The hover translation mode, as CoreHost spells it on the wire.
public enum TranslationMode: String, Sendable, CaseIterable {
    case smart
    case dictionary

    public var russianTitle: String {
        switch self {
        case .smart: return RU.menuModeSmart
        case .dictionary: return RU.menuModeDictionary
        }
    }
}

/// The translation subset of settings.json that CoreHost reports with
/// settings.get, settings.update and core.init. CoreHost is the only writer
/// of settings.json: the app shows these values and asks for changes with
/// settings.update, never writes the file.
public struct TranslationSettings: Sendable, Equatable {
    public var hoverEnabled: Bool
    public var hoverDelayMs: Int
    /// The wire spelling; a settings.json written on Windows may hold a mode
    /// the app does not know, which is shown as it is.
    public var mode: String
    public var senseModel: String
    public var senseBackend: String
    public var senseKeepWarm: Bool

    /// Core's limits for hoverDelayMs (settings.update answers
    /// invalid_settings outside them).
    public static let delayRange = 350...1500

    public init(hoverEnabled: Bool, hoverDelayMs: Int, mode: String, senseModel: String, senseBackend: String,
                senseKeepWarm: Bool) {
        self.hoverEnabled = hoverEnabled
        self.hoverDelayMs = hoverDelayMs
        self.mode = mode
        self.senseModel = senseModel
        self.senseBackend = senseBackend
        self.senseKeepWarm = senseKeepWarm
    }

    /// Decodes the `settings` object of protocol v1; nil when a member the
    /// app relies on is missing or mistyped.
    public init?(_ value: JSONValue) {
        guard let enabled = value["hoverEnabled"]?.boolValue,
              let delay = value["hoverDelayMs"]?.int64Value,
              let mode = value["mode"]?.stringValue,
              let sense = value["sense"], let model = sense["model"]?.stringValue,
              let backend = sense["backend"]?.stringValue else { return nil }
        self.init(hoverEnabled: enabled, hoverDelayMs: Int(delay), mode: mode, senseModel: model,
                  senseBackend: backend, senseKeepWarm: sense["keepWarm"]?.boolValue ?? false)
    }

    public var translationMode: TranslationMode? { TranslationMode(rawValue: mode) }
}

/// settings.get / settings.update / core.init: the settings and their revision.
public struct SettingsSnapshot: Sendable, Equatable {
    public let settings: TranslationSettings
    public let revision: Int64

    public init(settings: TranslationSettings, revision: Int64) {
        self.settings = settings
        self.revision = revision
    }

    public init?(result: JSONValue) {
        guard let settings = result["settings"].flatMap(TranslationSettings.init), let revision = result["revision"]?.int64Value
        else { return nil }
        self.init(settings: settings, revision: revision)
    }
}

/// A settings.update patch: every member optional, an absent one keeps its value.
public struct SettingsPatch: Sendable, Equatable {
    public var hoverEnabled: Bool?
    public var hoverDelayMs: Int?
    public var mode: TranslationMode?
    public var senseModel: String?
    public var senseBackend: String?

    public init(hoverEnabled: Bool? = nil, hoverDelayMs: Int? = nil, mode: TranslationMode? = nil,
                senseModel: String? = nil, senseBackend: String? = nil) {
        self.hoverEnabled = hoverEnabled
        self.hoverDelayMs = hoverDelayMs
        self.mode = mode
        self.senseModel = senseModel
        self.senseBackend = senseBackend
    }

    /// The `patch` member of settings.update, in CoreHost's schema.
    public var payload: JSONValue {
        var patch: [String: JSONValue] = [:]
        if let hoverEnabled { patch["hoverEnabled"] = .bool(hoverEnabled) }
        if let hoverDelayMs { patch["hoverDelayMs"] = .integer(Int64(hoverDelayMs)) }
        if let mode { patch["mode"] = .string(mode.rawValue) }
        var sense: [String: JSONValue] = [:]
        if let senseModel { sense["model"] = .string(senseModel) }
        if let senseBackend { sense["backend"] = .string(senseBackend) }
        if !sense.isEmpty { patch["sense"] = .object(sense) }
        return .object(patch)
    }

    /// Whether `settings` already holds every value of this patch.
    public func isApplied(to settings: TranslationSettings) -> Bool {
        (hoverEnabled.map { $0 == settings.hoverEnabled } ?? true)
            && (hoverDelayMs.map { $0 == settings.hoverDelayMs } ?? true)
            && (mode.map { $0.rawValue == settings.mode } ?? true)
            && (senseModel.map { $0 == settings.senseModel } ?? true)
            && (senseBackend.map { $0 == settings.senseBackend } ?? true)
    }
}

/// The app's side of settings.json: the last snapshot CoreHost reported and
/// the update that answers a `conflict` (the file changed since the snapshot)
/// by reading the current revision and sending the same patch once more.
public actor SettingsClient {
    public let supervisor: CoreSupervisor
    public private(set) var snapshot: SettingsSnapshot?

    public init(supervisor: CoreSupervisor) { self.supervisor = supervisor }

    /// Takes a snapshot CoreHost reported elsewhere (core.init, a hover's session).
    public func adopt(_ snapshot: SettingsSnapshot) {
        if let current = self.snapshot, current.revision > snapshot.revision { return }
        self.snapshot = snapshot
    }

    public func forget() { snapshot = nil }

    @discardableResult
    public func refresh() async throws -> SettingsSnapshot {
        let result = try await supervisor.request("settings.get", deadline: CoreDeadlines.control)
        guard let fresh = SettingsSnapshot(result: result) else { throw Self.badResponse("settings.get", result) }
        snapshot = fresh
        return fresh
    }

    /// Sends the patch against the known revision; one `conflict` refreshes
    /// and retries, a second one is the caller's error.
    @discardableResult
    public func update(_ patch: SettingsPatch) async throws -> SettingsSnapshot {
        var base: SettingsSnapshot
        if let snapshot { base = snapshot } else { base = try await refresh() }
        for attempt in 0..<2 {
            do {
                let result = try await supervisor.request(
                    "settings.update", ["baseRevision": .integer(base.revision), "patch": patch.payload],
                    deadline: CoreDeadlines.settings)
                guard let fresh = SettingsSnapshot(result: result) else {
                    throw Self.badResponse("settings.update", result)
                }
                snapshot = fresh
                return fresh
            } catch let error as CoreError where error.code == "conflict" && attempt == 0 {
                base = try await refresh()
            }
        }
        throw CoreError(code: "conflict", message: RU.errorText("conflict"))
    }

    static func badResponse(_ command: String, _ result: JSONValue) -> CoreError {
        CoreError(code: "bad_response", message: RU.errorText("bad_response"),
                  detail: "\(command) answered without settings and revision")
    }
}

/// models.status: what the menu's model line and the self-test read.
public struct ModelsStatus: Sendable, Equatable {
    public struct Sense: Sendable, Equatable {
        public let inUse: Bool
        public let provider: String
        public let model: String
        public let running: Bool
        public let pid: Int32?
        public let activeProvider: String?
        /// The identity the running worker reported (for ConSeC the exact bundle).
        public let modelVersion: String?
        /// The last failed worker transaction: its code and CoreHost's Russian
        /// sentence naming it (no paths).
        public let errorCode: String?
        public let errorMessage: String?
    }

    public let dictionaryLoaded: Bool
    public let dictionaryEntries: Int
    public let dictionarySources: Int
    public let morphologyEntries: Int
    public let sense: Sense

    public init?(_ result: JSONValue) {
        guard let dictionary = result["dictionary"], let sense = result["sense"],
              let inUse = sense["inUse"]?.boolValue, let running = sense["running"]?.boolValue else { return nil }
        dictionaryLoaded = dictionary["loaded"]?.boolValue ?? false
        dictionaryEntries = Int(dictionary["entries"]?.int64Value ?? 0)
        dictionarySources = Int(dictionary["sources"]?.int64Value ?? 0)
        morphologyEntries = Int(dictionary["morphologyEntries"]?.int64Value ?? 0)
        self.sense = Sense(
            inUse: inUse, provider: sense["provider"]?.stringValue ?? "", model: sense["model"]?.stringValue ?? "",
            running: running, pid: sense["pid"]?.int64Value.map { Int32(truncatingIfNeeded: $0) },
            activeProvider: sense["activeProvider"]?.stringValue, modelVersion: sense["modelVersion"]?.stringValue,
            errorCode: sense["error"]?["code"]?.stringValue, errorMessage: sense["error"]?["message"]?.stringValue)
    }

    /// The menu's model line. A worker error is shown as CoreHost's Russian
    /// sentence, which names the code; nothing else of CoreHost's free text
    /// reaches the menu.
    public var senseLine: String {
        if let message = sense.errorMessage, !message.isEmpty { return message }
        guard sense.inUse else { return RU.senseNotInUse }
        let name = RU.senseModelName(sense.model, provider: sense.provider)
        return sense.running ? RU.senseRunning(name) : RU.senseIdle(name)
    }
}

/// One `status` event without a ref: an area (dictionary, settings, hover,
/// translation), an optional state and code, and Russian text.
public struct ServiceMessage: Sendable, Equatable {
    public let area: String
    public let state: String?
    public let code: String?
    public let text: String
    public let at: Date

    public init?(_ event: CoreEvent, at: Date = Date()) {
        guard event.name == "status", event.ref == nil, let area = event.payload["area"]?.stringValue,
              let text = event.payload["text"]?.stringValue else { return nil }
        self.area = area
        state = event.payload["state"]?.stringValue
        code = event.payload["code"]?.stringValue
        self.text = text
        self.at = at
    }

    /// One line of the diagnostic window: the local time and the text, with
    /// the code in brackets when there is one.
    public var line: String {
        let parts = Calendar.current.dateComponents([.hour, .minute, .second], from: at)
        let time = String(format: "%02d:%02d:%02d", parts.hour ?? 0, parts.minute ?? 0, parts.second ?? 0)
        return "\(time)  \(text)" + (code.map { " (\($0))" } ?? "")
    }
}

/// The last CoreHost messages for the «Проверка перевода» window. CoreHost's
/// free text (area translation) appears only there and in app.log, never in
/// the menu or the overlay.
public struct ServiceMessageLog: Sendable, Equatable {
    public let capacity: Int
    public private(set) var messages: [ServiceMessage] = []

    public init(capacity: Int = 50) { self.capacity = capacity }

    public mutating func append(_ message: ServiceMessage) {
        messages.append(message)
        if messages.count > capacity { messages.removeFirst(messages.count - capacity) }
    }
}

/// The identity the ConSeC worker reports for the pinned bundle
/// (models.status sense.modelVersion). The same string as
/// MacWorkerTests.ExpectedConsecModelVersion of the macOS subset; a Swift
/// test compares the two. The app's self-test accepts a ConSeC hover only
/// with exactly this identity, so another bundle cannot pass for it.
public enum SenseIdentity {
    public static let consec = "ConSeC:WNGT:9602b5fd:adapter-consec-wngt-positional-extractor-v1:g-0eb0688f3fd2:"
        + "d-1257dc55ebec:t-df91c443c8ea:pretokenizer-v3:context-full-v1:s512:pipeline-consec-independent"
}
