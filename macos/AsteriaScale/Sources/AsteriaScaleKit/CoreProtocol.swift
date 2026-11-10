import Foundation

/// Protocol v1 between the app and CoreHost (docs/MACOS.md M11; the table is
/// in docs/DEVELOPMENT.md, «CoreHost»). Every frame is one UTF-8 JSON object on
/// one line ending in \n:
///   request : {"version":1,"id":17,"command":"hover.resolve","payload":{…}}
///   response: {"version":1,"id":17,"ok":true,"result":{…}}
///             {"version":1,"id":17,"ok":false,"error":{"code":…,"message":<ru>,"detail":<en>}}
///   event   : {"version":1,"event":"hover.primary","ref":17,"payload":{…}}  (no ref: unsolicited)
/// The bytes are pinned by tests/AsteriaScale.Tests.MacOS/CoreHost/protocol-v1.golden.jsonl,
/// which the C# conformance check replays and the Swift suite decodes.
public enum CoreProtocol {
    public static let version: Int64 = 1
    /// CoreHost never writes a frame longer than this (its MaxOutboundFrameBytes).
    public static let maxInboundLineBytes = 4 << 20
    /// CoreHost treats a longer request frame, newline included, as a
    /// connection-fatal violation (its MaxInboundFrameBytes), so the client
    /// refuses to send one instead.
    public static let maxRequestFrameBytes = 1 << 20
}

/// One request frame, encoded with the envelope members in the golden
/// transcript's order and without a payload member when there is none.
public struct CoreRequestLine: Sendable, Equatable {
    public let id: Int64
    public let command: String
    public let payload: JSONValue?

    public init(id: Int64, command: String, payload: JSONValue? = nil) {
        self.id = id
        self.command = command
        self.payload = payload
    }

    public func encodedLine() throws(JSONSerializationFailure) -> Data {
        var out = Data("{\"version\":\(CoreProtocol.version),\"id\":\(id),\"command\":".utf8)
        JSONValue.writeString(command, into: &out)
        if let payload {
            guard case .object = payload else { throw JSONSerializationFailure(reason: "a payload must be an object") }
            out.append(contentsOf: Array(",\"payload\":".utf8))
            try JSONValue.write(payload, into: &out)
        }
        out.append(contentsOf: Array("}\n".utf8))
        return out
    }
}

public struct CoreEvent: Sendable, Equatable {
    public let name: String
    public let ref: Int64?
    public let payload: JSONValue

    public init(name: String, ref: Int64?, payload: JSONValue) {
        self.name = name
        self.ref = ref
        self.payload = payload
    }
}

public enum CoreInbound: Sendable, Equatable {
    case terminal(id: Int64, result: Result<JSONValue, CoreError>)
    case event(CoreEvent)
}

/// A line that is not exactly one of the two v1 shapes. The connection kills
/// the CoreHost process group and never resynchronises (the rule-6 analogue).
public struct ProtocolViolation: Error, Sendable, Equatable, CustomStringConvertible {
    public let reason: String
    public init(reason: String) { self.reason = reason }
    public var description: String { "protocol violation: \(reason)" }
}

/// The client half of the v1 envelope contract, as strict as the C# test
/// client (CoreHostClient): no unknown envelope member, an integer version 1,
/// an integer id or ref of at least 1, exactly one of result and error, and an
/// error with a code and a message. Payload contents are command-level and are
/// checked by whoever decodes them.
public enum CoreLineParser {
    private static let responseMembers: Set<String> = ["version", "id", "ok", "result", "error"]
    private static let eventMembers: Set<String> = ["version", "event", "ref", "payload"]

    public static func parse(_ line: Data) throws(ProtocolViolation) -> CoreInbound {
        guard !line.isEmpty else { throw ProtocolViolation(reason: "empty line") }
        let value: JSONValue
        do { value = try StrictJSON.parse(line) } catch {
            throw ProtocolViolation(reason: "not strict JSON (\(error)): \(preview(line))")
        }
        guard case .object(let frame) = value else { throw ProtocolViolation(reason: "not an object: \(preview(line))") }
        guard frame["version"] == .integer(CoreProtocol.version) else {
            throw ProtocolViolation(reason: "missing or unsupported version: \(preview(line))")
        }
        if frame["event"] != nil {
            if let unknown = frame.keys.first(where: { !eventMembers.contains($0) }) {
                throw ProtocolViolation(reason: "unknown event member '\(unknown)'")
            }
            guard let name = frame["event"]?.stringValue, !name.isEmpty else {
                throw ProtocolViolation(reason: "an event needs a name")
            }
            guard let payload = frame["payload"], case .object = payload else {
                throw ProtocolViolation(reason: "event \(name) has no payload object")
            }
            var ref: Int64?
            if let rawRef = frame["ref"] {
                guard let parsed = rawRef.int64Value, parsed >= 1 else {
                    throw ProtocolViolation(reason: "event \(name) has a ref that is not an id")
                }
                ref = parsed
            }
            return .event(CoreEvent(name: name, ref: ref, payload: payload))
        }
        if let unknown = frame.keys.first(where: { !responseMembers.contains($0) }) {
            throw ProtocolViolation(reason: "unknown response member '\(unknown)'")
        }
        guard let id = frame["id"]?.int64Value, id >= 1, let ok = frame["ok"]?.boolValue else {
            throw ProtocolViolation(reason: "neither an event nor a response: \(preview(line))")
        }
        if ok {
            guard let result = frame["result"], case .object = result, frame["error"] == nil else {
                throw ProtocolViolation(reason: "response #\(id) needs exactly one result object")
            }
            return .terminal(id: id, result: .success(result))
        }
        guard let error = frame["error"], case .object = error, frame["result"] == nil,
              let code = error["code"]?.stringValue, !code.isEmpty,
              let message = error["message"]?.stringValue, !message.isEmpty else {
            throw ProtocolViolation(reason: "error response #\(id) needs one error with a code and a message")
        }
        return .terminal(id: id, result: .failure(CoreError(
            code: code, message: message, detail: error["detail"]?.stringValue,
            supersededBy: error["supersededBy"]?.int64Value)))
    }

    static func preview(_ data: Data) -> String {
        String(decoding: data.prefix(120), as: UTF8.self)
    }
}

/// An error a caller sees. `message` is Russian and may reach the user
/// (rule 9); it names the translation service, never the internal host
/// (M15). `code` and `detail` are for details and logs.
public struct CoreError: Error, Sendable, Equatable, CustomStringConvertible {
    public let code: String
    public let message: String
    public let detail: String?
    public let supersededBy: Int64?

    public init(code: String, message: String, detail: String? = nil, supersededBy: Int64? = nil) {
        self.code = code
        self.message = message
        self.detail = detail
        self.supersededBy = supersededBy
    }

    public var description: String { "\(code): \(detail ?? message)" }

    // Client-side codes. CoreHost's own codes arrive in its error terminals.
    public static func connectionLost(_ detail: String) -> CoreError {
        CoreError(code: "connection_lost", message: RU.errorText("connection_lost"), detail: detail)
    }

    public static func requestTooLarge(_ command: String, bytes: Int) -> CoreError {
        CoreError(code: "request_too_large", message: RU.errorText("request_too_large"),
                  detail: "\(command) would be a \(bytes)-byte frame, over \(CoreProtocol.maxRequestFrameBytes)")
    }

    public static let notReady = CoreError(code: "not_ready", message: RU.errorText("not_ready"),
                                           detail: "the translation service is not ready")
}

/// Splits a byte stream into lines: LF only, each bounded, a partial last
/// line kept until its newline arrives.
public struct LineFramer: Sendable {
    public let maxLineBytes: Int
    private var buffer = Data()

    public init(maxLineBytes: Int = CoreProtocol.maxInboundLineBytes) { self.maxLineBytes = maxLineBytes }

    public var pendingBytes: Int { buffer.count }

    public mutating func append(_ chunk: Data) throws(ProtocolViolation) -> [Data] {
        buffer.append(chunk)
        var lines: [Data] = []
        while let newline = buffer.firstIndex(of: 0x0A) {
            let line = buffer[buffer.startIndex..<newline]
            if line.count > maxLineBytes { throw ProtocolViolation(reason: "a line longer than \(maxLineBytes) bytes") }
            lines.append(Data(line))
            buffer.removeSubrange(buffer.startIndex...newline)
        }
        if buffer.count > maxLineBytes { throw ProtocolViolation(reason: "a line longer than \(maxLineBytes) bytes") }
        return lines
    }
}
