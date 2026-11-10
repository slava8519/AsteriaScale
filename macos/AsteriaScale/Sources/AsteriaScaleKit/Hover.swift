import Foundation

/// The `source` of hover.resolve for the 20.2 diagnostic window:
/// {"kind":"selection", text, hoveredWord, characterIndex, sentenceStable}.
/// Apple Vision observations replace it in 20.3.
public struct HoverSelection: Sendable, Equatable {
    public let text: String
    public let hoveredWord: String
    /// UTF-16 code units, because Core indexes .NET strings.
    public let characterIndex: Int
    public let sentenceStable: Bool

    public init(text: String, hoveredWord: String, characterIndex: Int, sentenceStable: Bool = true) {
        self.text = text
        self.hoveredWord = hoveredWord
        self.characterIndex = characterIndex
        self.sentenceStable = sentenceStable
    }

    public var payload: JSONValue {
        ["kind": "selection", "text": .string(text), "hoveredWord": .string(hoveredWord),
         "characterIndex": .integer(Int64(characterIndex)), "sentenceStable": .bool(sentenceStable)]
    }
}

public enum DiagnosticSelectionError: Error, Sendable, Equatable {
    case empty
    case tooLong

    public var russian: String {
        switch self {
        case .empty: return RU.diagnosticEmpty
        case .tooLong: return RU.diagnosticTooLong
        }
    }
}

public enum DiagnosticSelection {
    /// The Windows OCR path caps a line at 1200 UTF-16 units (NormalizeLong).
    public static let maxUTF16 = 1200

    /// "The [bank] was steep" gives the text without brackets, hoveredWord
    /// "bank" and the UTF-16 index of "bank". Without brackets the first word
    /// is the hovered one.
    public static func parse(_ input: String) throws(DiagnosticSelectionError) -> HoverSelection {
        var text = input.replacingOccurrences(of: "\r\n", with: " ")
            .replacingOccurrences(of: "\n", with: " ")
            .trimmingCharacters(in: .whitespaces)
        guard !text.isEmpty else { throw .empty }
        var hovered: String?
        var index = 0
        if let open = text.firstIndex(of: "["), let close = text[open...].firstIndex(of: "]") {
            let inner = String(text[text.index(after: open)..<close]).trimmingCharacters(in: .whitespaces)
            if !inner.isEmpty {
                let before = String(text[..<open])
                let after = String(text[text.index(after: close)...])
                text = before + inner + after
                hovered = inner
                index = before.utf16.count
            }
        }
        if hovered == nil, let range = text.range(of: #"[\p{L}\p{N}'’]+"#, options: .regularExpression) {
            hovered = String(text[range])
            index = range.lowerBound.utf16Offset(in: text)
        }
        guard text.utf16.count <= maxUTF16 else { throw .tooLong }
        return HoverSelection(text: text, hoveredWord: hovered ?? text, characterIndex: index)
    }
}

/// The payload of a `hover.primary` event.
public struct HoverPrimary: Sendable, Equatable {
    /// The hover.resolve request id; hover.displayed names the hover by it.
    public let hoverId: Int64
    public let source: String
    public let translation: String
    public let language: String?
    public let isPhrase: Bool
    public let found: Bool
    public let confidence: Double?
    public let provenance: Provenance
    /// cpu or npu, set exactly for the three ranker provenances.
    public let senseProvider: String?
    /// Russian, for history and the log only; the app never parses it.
    public let engineLabel: String
    public let context: String?

    public init(hoverId: Int64, payload: JSONValue) {
        self.hoverId = hoverId
        source = payload["source"]?.stringValue ?? ""
        translation = payload["translation"]?.stringValue ?? ""
        language = payload["language"]?.stringValue
        isPhrase = payload["isPhrase"]?.boolValue ?? false
        found = payload["found"]?.boolValue ?? false
        confidence = payload["confidence"]?.doubleValue
        provenance = Provenance(wire: payload["provenance"]?.stringValue ?? "")
        senseProvider = payload["senseProvider"]?.stringValue
        engineLabel = payload["engineLabel"]?.stringValue ?? ""
        context = payload["context"]?.stringValue
    }

    /// The overlay badge (rule 9).
    public var badge: String { RU.provenance(provenance, found: found) }
}

/// The payload of a `hover.phrase` event (from 20.5; the golden has one).
public struct HoverPhrase: Sendable, Equatable {
    public let hoverId: Int64
    public let source: String
    public let translation: String
    public let status: String
    public let provisional: Bool
    public let fromCache: Bool

    public init(hoverId: Int64, payload: JSONValue) {
        self.hoverId = hoverId
        source = payload["source"]?.stringValue ?? ""
        translation = payload["translation"]?.stringValue ?? ""
        status = payload["status"]?.stringValue ?? ""
        provisional = payload["provisional"]?.boolValue ?? false
        fromCache = payload["fromCache"]?.boolValue ?? false
    }
}

public enum HoverUpdate: Sendable, Equatable {
    case primary(HoverPrimary)
    case phrase(HoverPhrase)
}

public enum HoverOutcome: Sendable, Equatable {
    /// CoreHost's own outcomes. On `failed` it has sent a Russian status.
    case completed
    case empty
    case failed
    /// A newer hover replaced this one (CoreHost allows one at a time).
    case superseded
    /// The caller cancelled; CoreHost got request.cancel.
    case cancelled
    /// An error terminal (hover_disabled, not_ready, …) or a lost connection.
    case rejected(CoreError)
}

public enum HoverPart: String, Sendable {
    case primary
    case phrase
}

/// Deadlines per command, in one place.
public enum CoreDeadlines {
    public static let control: Duration = .seconds(2)          // ping, request.cancel, hover.displayed
    public static let hello: Duration = .seconds(15)
    public static let initialize: Duration = .seconds(120)     // dictionaries and morphology
    public static let settings: Duration = .seconds(5)
    public static let warmUp: Duration = .seconds(130)         // llama-server start is 2 min (20.5)
    public static let hover: Duration = .seconds(95)           // ranker 60 s, llama-server 90 s, plus margin
}

/// Hover requests over the supervisor. CoreHost runs one hover at a time and
/// answers a replaced one with `superseded` before the newer one's events
/// (G3), so the client needs no cancel of its own when a new hover starts;
/// events reach `onUpdate` in CoreHost's order on the connection queue, and a
/// caller that hops to the main thread must use a FIFO (DispatchQueue.main),
/// not unordered Tasks.
public struct HoverClient: Sendable {
    public let supervisor: CoreSupervisor

    public init(supervisor: CoreSupervisor) { self.supervisor = supervisor }

    public func resolve(_ selection: HoverSelection, mode: String? = nil,
                        onUpdate: @escaping @Sendable (HoverUpdate) -> Void) async -> HoverOutcome {
        var payload: [String: JSONValue] = ["source": selection.payload]
        if let mode { payload["mode"] = .string(mode) }
        let hoverId = Locked<Int64>(0)
        do {
            let result = try await supervisor.request(
                "hover.resolve", .object(payload), deadline: CoreDeadlines.hover, cancelInHost: true,
                onSent: { hoverId.set($0) },
                onEvent: { event in
                    switch event.name {
                    case "hover.primary": onUpdate(.primary(HoverPrimary(hoverId: hoverId.get, payload: event.payload)))
                    case "hover.phrase": onUpdate(.phrase(HoverPhrase(hoverId: hoverId.get, payload: event.payload)))
                    default: break
                    }
                })
            switch result["outcome"]?.stringValue {
            case "completed": return .completed
            case "empty": return .empty
            case "failed": return .failed
            default:
                return .rejected(CoreError(code: "bad_response", message: RU.errorText("bad_response"),
                                           detail: "hover.resolve outcome \(String(describing: result["outcome"]))"))
            }
        } catch is CancellationError {
            return .cancelled
        } catch let error as CoreError {
            switch error.code {
            case "superseded": return .superseded
            case "cancelled": return .cancelled
            default: return .rejected(error)
            }
        } catch {
            return .rejected(CoreError(code: "unexpected", message: RU.errorText("unexpected"), detail: "\(error)"))
        }
    }

    /// Tells CoreHost the user saw a part; history records only that.
    @discardableResult
    public func displayed(hoverId: Int64, part: HoverPart) async -> Bool {
        let result = try? await supervisor.request(
            "hover.displayed", ["hoverId": .integer(hoverId), "part": .string(part.rawValue)],
            deadline: CoreDeadlines.control)
        return result?["recorded"]?.boolValue ?? false
    }
}

/// A value shared between a request's callbacks.
final class Locked<Value: Sendable>: @unchecked Sendable {
    private let lock = NSLock()
    private var value: Value

    init(_ value: Value) { self.value = value }

    var get: Value {
        lock.lock()
        defer { lock.unlock() }
        return value
    }

    func set(_ newValue: Value) {
        lock.lock()
        value = newValue
        lock.unlock()
    }
}
