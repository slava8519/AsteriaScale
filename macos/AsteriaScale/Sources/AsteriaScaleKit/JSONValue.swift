import Foundation

/// A JSON value as protocol v1 carries it. Integers and fractional numbers are
/// kept apart because CoreHost's System.Text.Json rejects 17.0 where it
/// expects an integer, and the client must reject a frame whose `version` or
/// `id` is not an integer literal, exactly as the C# test client does.
public enum JSONValue: Sendable, Hashable {
    case null
    case bool(Bool)
    case integer(Int64)
    case number(Double)
    case string(String)
    case array([JSONValue])
    case object([String: JSONValue])

    public subscript(key: String) -> JSONValue? {
        if case .object(let members) = self { return members[key] }
        return nil
    }

    public var stringValue: String? { if case .string(let value) = self { return value }; return nil }
    public var boolValue: Bool? { if case .bool(let value) = self { return value }; return nil }
    public var int64Value: Int64? { if case .integer(let value) = self { return value }; return nil }
    public var objectValue: [String: JSONValue]? { if case .object(let value) = self { return value }; return nil }
    public var arrayValue: [JSONValue]? { if case .array(let value) = self { return value }; return nil }

    public var doubleValue: Double? {
        switch self {
        case .integer(let value): return Double(value)
        case .number(let value): return value
        default: return nil
        }
    }

    /// The array as strings, or nil when it is not an array of strings only.
    public var stringArray: [String]? {
        guard case .array(let items) = self else { return nil }
        var strings: [String] = []
        for item in items {
            guard let string = item.stringValue else { return nil }
            strings.append(string)
        }
        return strings
    }

    /// Compact JSON with sorted keys and raw UTF-8 (Russian text is never
    /// \u-escaped, `/` is never escaped), so the same value always gives the
    /// same bytes.
    public func serialized() throws(JSONSerializationFailure) -> Data {
        var out = Data()
        try JSONValue.write(self, into: &out)
        return out
    }

    static func write(_ value: JSONValue, into out: inout Data) throws(JSONSerializationFailure) {
        switch value {
        case .null: out.append(contentsOf: Array("null".utf8))
        case .bool(let flag): out.append(contentsOf: Array((flag ? "true" : "false").utf8))
        case .integer(let number): out.append(contentsOf: Array(String(number).utf8))
        case .number(let number):
            guard number.isFinite else { throw JSONSerializationFailure(reason: "a non-finite number has no JSON form") }
            out.append(contentsOf: Array("\(number)".utf8))
        case .string(let text): writeString(text, into: &out)
        case .array(let items):
            out.append(UInt8(ascii: "["))
            for (index, item) in items.enumerated() {
                if index > 0 { out.append(UInt8(ascii: ",")) }
                try write(item, into: &out)
            }
            out.append(UInt8(ascii: "]"))
        case .object(let members):
            out.append(UInt8(ascii: "{"))
            for (index, member) in members.sorted(by: { $0.key < $1.key }).enumerated() {
                if index > 0 { out.append(UInt8(ascii: ",")) }
                writeString(member.key, into: &out)
                out.append(UInt8(ascii: ":"))
                try write(member.value, into: &out)
            }
            out.append(UInt8(ascii: "}"))
        }
    }

    static func writeString(_ text: String, into out: inout Data) {
        out.append(UInt8(ascii: "\""))
        for byte in text.utf8 {
            switch byte {
            case UInt8(ascii: "\""): out.append(contentsOf: Array("\\\"".utf8))
            case UInt8(ascii: "\\"): out.append(contentsOf: Array("\\\\".utf8))
            case 0x0A: out.append(contentsOf: Array("\\n".utf8))
            case 0x0D: out.append(contentsOf: Array("\\r".utf8))
            case 0x09: out.append(contentsOf: Array("\\t".utf8))
            case 0x00..<0x20:
                out.append(contentsOf: Array(String(format: "\\u%04x", byte).utf8))
            default: out.append(byte)
            }
        }
        out.append(UInt8(ascii: "\""))
    }
}

public struct JSONSerializationFailure: Error, Sendable, Equatable {
    public let reason: String
}

extension JSONValue: ExpressibleByStringLiteral, ExpressibleByIntegerLiteral, ExpressibleByBooleanLiteral,
    ExpressibleByDictionaryLiteral, ExpressibleByArrayLiteral, ExpressibleByNilLiteral, ExpressibleByFloatLiteral
{
    public init(stringLiteral value: String) { self = .string(value) }
    public init(integerLiteral value: Int64) { self = .integer(value) }
    public init(floatLiteral value: Double) { self = .number(value) }
    public init(booleanLiteral value: Bool) { self = .bool(value) }
    public init(dictionaryLiteral elements: (String, JSONValue)...) {
        self = .object(Dictionary(uniqueKeysWithValues: elements))
    }
    public init(arrayLiteral elements: JSONValue...) { self = .array(elements) }
    public init(nilLiteral: ()) { self = .null }
}

/// Why a line is not strict JSON.
public struct JSONParseFailure: Error, Sendable, Equatable, CustomStringConvertible {
    public let reason: String
    public let offset: Int
    public var description: String { "\(reason) at byte \(offset)" }
}

/// A strict RFC 8259 parser for protocol frames. Stricter than
/// JSONSerialization on purpose, to match CoreHost's own reader and the C#
/// test client: invalid UTF-8, a duplicate member, a lone surrogate escape, a
/// raw control character, a byte-order mark, a carriage return or anything
/// after the value is an error, never a silent repair.
public enum StrictJSON {
    public static let maxDepth = 64

    public static func parse(_ data: Data) throws(JSONParseFailure) -> JSONValue {
        let bytes = [UInt8](data)
        var parser = Parser(bytes: bytes)
        if bytes.starts(with: [0xEF, 0xBB, 0xBF]) { throw JSONParseFailure(reason: "byte-order mark", offset: 0) }
        if let carriageReturn = bytes.firstIndex(of: 0x0D) {
            throw JSONParseFailure(reason: "carriage return", offset: carriageReturn)
        }
        parser.skipWhitespace()
        let value = try parser.parseValue(depth: 0)
        parser.skipWhitespace()
        guard parser.index == bytes.count else { throw parser.failure("trailing data") }
        return value
    }

    private struct Parser {
        let bytes: [UInt8]
        var index = 0

        func failure(_ reason: String) -> JSONParseFailure { JSONParseFailure(reason: reason, offset: index) }

        mutating func skipWhitespace() {
            while index < bytes.count, bytes[index] == 0x20 || bytes[index] == 0x09 || bytes[index] == 0x0A {
                index += 1
            }
        }

        mutating func expect(_ literal: String) throws(JSONParseFailure) {
            for byte in literal.utf8 {
                guard index < bytes.count, bytes[index] == byte else { throw failure("invalid literal") }
                index += 1
            }
        }

        mutating func parseValue(depth: Int) throws(JSONParseFailure) -> JSONValue {
            guard depth <= StrictJSON.maxDepth else { throw failure("nesting deeper than \(StrictJSON.maxDepth)") }
            guard index < bytes.count else { throw failure("unexpected end") }
            switch bytes[index] {
            case UInt8(ascii: "{"): return try parseObject(depth: depth)
            case UInt8(ascii: "["): return try parseArray(depth: depth)
            case UInt8(ascii: "\""): return .string(try parseString())
            case UInt8(ascii: "t"): try expect("true"); return .bool(true)
            case UInt8(ascii: "f"): try expect("false"); return .bool(false)
            case UInt8(ascii: "n"): try expect("null"); return .null
            case UInt8(ascii: "-"), UInt8(ascii: "0")...UInt8(ascii: "9"): return try parseNumber()
            default: throw failure("unexpected byte")
            }
        }

        mutating func parseObject(depth: Int) throws(JSONParseFailure) -> JSONValue {
            index += 1
            var members: [String: JSONValue] = [:]
            skipWhitespace()
            if index < bytes.count, bytes[index] == UInt8(ascii: "}") { index += 1; return .object(members) }
            while true {
                skipWhitespace()
                guard index < bytes.count, bytes[index] == UInt8(ascii: "\"") else { throw failure("expected a member name") }
                let start = index
                let key = try parseString()
                guard members[key] == nil else {
                    throw JSONParseFailure(reason: "duplicate member '\(key)'", offset: start)
                }
                skipWhitespace()
                guard index < bytes.count, bytes[index] == UInt8(ascii: ":") else { throw failure("expected ':'") }
                index += 1
                skipWhitespace()
                members[key] = try parseValue(depth: depth + 1)
                skipWhitespace()
                guard index < bytes.count else { throw failure("unterminated object") }
                if bytes[index] == UInt8(ascii: ",") { index += 1; continue }
                if bytes[index] == UInt8(ascii: "}") { index += 1; return .object(members) }
                throw failure("expected ',' or '}'")
            }
        }

        mutating func parseArray(depth: Int) throws(JSONParseFailure) -> JSONValue {
            index += 1
            var items: [JSONValue] = []
            skipWhitespace()
            if index < bytes.count, bytes[index] == UInt8(ascii: "]") { index += 1; return .array(items) }
            while true {
                skipWhitespace()
                items.append(try parseValue(depth: depth + 1))
                skipWhitespace()
                guard index < bytes.count else { throw failure("unterminated array") }
                if bytes[index] == UInt8(ascii: ",") { index += 1; continue }
                if bytes[index] == UInt8(ascii: "]") { index += 1; return .array(items) }
                throw failure("expected ',' or ']'")
            }
        }

        mutating func parseNumber() throws(JSONParseFailure) -> JSONValue {
            let start = index
            if bytes[index] == UInt8(ascii: "-") { index += 1 }
            guard index < bytes.count, isDigit(bytes[index]) else { throw failure("invalid number") }
            if bytes[index] == UInt8(ascii: "0") {
                index += 1
                if index < bytes.count, isDigit(bytes[index]) { throw failure("leading zero") }
            } else {
                while index < bytes.count, isDigit(bytes[index]) { index += 1 }
            }
            var integral = true
            if index < bytes.count, bytes[index] == UInt8(ascii: ".") {
                integral = false
                index += 1
                guard index < bytes.count, isDigit(bytes[index]) else { throw failure("invalid fraction") }
                while index < bytes.count, isDigit(bytes[index]) { index += 1 }
            }
            if index < bytes.count, bytes[index] == UInt8(ascii: "e") || bytes[index] == UInt8(ascii: "E") {
                integral = false
                index += 1
                if index < bytes.count, bytes[index] == UInt8(ascii: "+") || bytes[index] == UInt8(ascii: "-") { index += 1 }
                guard index < bytes.count, isDigit(bytes[index]) else { throw failure("invalid exponent") }
                while index < bytes.count, isDigit(bytes[index]) { index += 1 }
            }
            let text = String(decoding: bytes[start..<index], as: UTF8.self)
            if integral, let value = Int64(text) { return .integer(value) }
            guard let value = Double(text), value.isFinite else { throw failure("number out of range") }
            return .number(value)
        }

        func isDigit(_ byte: UInt8) -> Bool { byte >= UInt8(ascii: "0") && byte <= UInt8(ascii: "9") }

        mutating func parseString() throws(JSONParseFailure) -> String {
            index += 1
            var raw: [UInt8] = []
            while true {
                guard index < bytes.count else { throw failure("unterminated string") }
                let byte = bytes[index]
                if byte == UInt8(ascii: "\"") {
                    index += 1
                    break
                }
                if byte < 0x20 { throw failure("raw control character in a string") }
                if byte != UInt8(ascii: "\\") {
                    raw.append(byte)
                    index += 1
                    continue
                }
                index += 1
                guard index < bytes.count else { throw failure("unterminated escape") }
                let escape = bytes[index]
                index += 1
                switch escape {
                case UInt8(ascii: "\""): raw.append(0x22)
                case UInt8(ascii: "\\"): raw.append(0x5C)
                case UInt8(ascii: "/"): raw.append(0x2F)
                case UInt8(ascii: "b"): raw.append(0x08)
                case UInt8(ascii: "f"): raw.append(0x0C)
                case UInt8(ascii: "n"): raw.append(0x0A)
                case UInt8(ascii: "r"): raw.append(0x0D)
                case UInt8(ascii: "t"): raw.append(0x09)
                case UInt8(ascii: "u"):
                    var scalar = try parseHex4()
                    if (0xD800...0xDBFF).contains(scalar) {
                        guard index + 1 < bytes.count, bytes[index] == UInt8(ascii: "\\"),
                              bytes[index + 1] == UInt8(ascii: "u") else { throw failure("lone high surrogate") }
                        index += 2
                        let low = try parseHex4()
                        guard (0xDC00...0xDFFF).contains(low) else { throw failure("invalid low surrogate") }
                        scalar = 0x10000 + ((scalar - 0xD800) << 10) + (low - 0xDC00)
                    } else if (0xDC00...0xDFFF).contains(scalar) {
                        throw failure("lone low surrogate")
                    }
                    guard let unicode = Unicode.Scalar(scalar) else { throw failure("invalid \\u escape") }
                    raw.append(contentsOf: Array(String(Character(unicode)).utf8))
                default: throw failure("invalid escape")
                }
            }
            guard UTF8Validation.isValid(raw) else { throw failure("invalid UTF-8 in a string") }
            return String(decoding: raw, as: UTF8.self)
        }

        mutating func parseHex4() throws(JSONParseFailure) -> UInt32 {
            guard index + 4 <= bytes.count else { throw failure("short \\u escape") }
            var value: UInt32 = 0
            for _ in 0..<4 {
                let byte = bytes[index]
                let digit: UInt32
                switch byte {
                case UInt8(ascii: "0")...UInt8(ascii: "9"): digit = UInt32(byte - UInt8(ascii: "0"))
                case UInt8(ascii: "a")...UInt8(ascii: "f"): digit = UInt32(byte - UInt8(ascii: "a") + 10)
                case UInt8(ascii: "A")...UInt8(ascii: "F"): digit = UInt32(byte - UInt8(ascii: "A") + 10)
                default: throw failure("invalid \\u escape")
                }
                value = value << 4 | digit
                index += 1
            }
            return value
        }
    }
}

/// Well-formed UTF-8 per Unicode table 3-7: no overlong forms, no surrogates,
/// nothing above U+10FFFF. String(decoding:) would repair such bytes instead.
enum UTF8Validation {
    static func isValid(_ bytes: [UInt8]) -> Bool {
        var index = 0
        while index < bytes.count {
            let lead = bytes[index]
            if lead < 0x80 { index += 1; continue }
            let length: Int
            var low: UInt8 = 0x80, high: UInt8 = 0xBF
            switch lead {
            case 0xC2...0xDF: length = 2
            case 0xE0: length = 3; low = 0xA0
            case 0xE1...0xEC, 0xEE...0xEF: length = 3
            case 0xED: length = 3; high = 0x9F
            case 0xF0: length = 4; low = 0x90
            case 0xF1...0xF3: length = 4
            case 0xF4: length = 4; high = 0x8F
            default: return false
            }
            guard index + length <= bytes.count else { return false }
            guard bytes[index + 1] >= low, bytes[index + 1] <= high else { return false }
            if length > 2 {
                for offset in 2..<length {
                    guard bytes[index + offset] >= 0x80, bytes[index + offset] <= 0xBF else { return false }
                }
            }
            index += length
        }
        return true
    }
}
