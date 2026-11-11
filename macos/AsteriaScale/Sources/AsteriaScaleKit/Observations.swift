import Foundation

// Apple Vision observations as protocol v1 carries them (roadmap 20.3;
// docs/MACOS.md T18-T20, T27): the app captures and recognizes, CoreHost
// selects with Core's OcrGeometry and OcrTextRules. The shapes and limits are
// CoreHost's (src/AsteriaScale.CoreHost/Protocol/Wire.cs, Host/Observations.cs);
// the golden transcript pins the bytes. display, cursor and captureBounds are
// whole pixels of the display under the cursor (origin at its top-left
// corner, y down); word boxes are pixels of the frame, which is captureBounds
// 1:1.

extension JSONValue {
    /// A double as the wire writes it: a whole value as an integer literal
    /// (100, as System.Text.Json writes 100.0 and the golden lines carry it),
    /// anything else as a fraction. Non-finite values have no JSON form; the
    /// observation builders never pass one.
    public static func wireNumber(_ value: Double) -> JSONValue {
        if value.isFinite, value == value.rounded(), abs(value) <= 9_007_199_254_740_992 {
            return .integer(Int64(value))
        }
        return .number(value)
    }
}

/// The protocol's display: its CGDirectDisplayID and its size in its own pixels.
public struct ObservedDisplay: Sendable, Hashable {
    public let id: UInt32
    public let width: Int
    public let height: Int

    public init(id: UInt32, width: Int, height: Int) {
        self.id = id
        self.width = width
        self.height = height
    }

    public var json: JSONValue { ["id": .integer(Int64(id)), "width": .integer(Int64(width)), "height": .integer(Int64(height))] }
}

/// One word box of an observation, in frame pixels.
public struct ObservedWord: Sendable, Hashable {
    public let text: String
    public let box: PixelBox

    public init(text: String, box: PixelBox) {
        self.text = text
        self.box = box
    }

    public var json: JSONValue {
        ["text": .string(text), "left": .wireNumber(box.left), "top": .wireNumber(box.top),
         "right": .wireNumber(box.right), "bottom": .wireNumber(box.bottom)]
    }
}

public struct ObservedLine: Sendable, Hashable {
    public let text: String
    public let confidence: Double
    public let words: [ObservedWord]

    public init(text: String, confidence: Double, words: [ObservedWord]) {
        self.text = text
        self.confidence = confidence
        self.words = words
    }

    public var json: JSONValue {
        ["text": .string(text), "confidence": .wireNumber(confidence), "words": .array(words.map(\.json))]
    }
}

/// One recognized frame: what the recognizer read and the frame's fingerprint.
public struct ObservationFrame: Sendable, Hashable {
    public let fingerprint: FrameFingerprint
    public let lines: [ObservedLine]

    public init(fingerprint: FrameFingerprint, lines: [ObservedLine]) {
        self.fingerprint = fingerprint
        self.lines = lines
    }

    /// The recognizer's lines as the protocol takes them. A word CoreHost
    /// would refuse the whole request over is left out, the rest of its line
    /// kept: a box that is not finite, or text outside 1..256 UTF-16 units (a
    /// run without whitespace such as a dotted leader or a long URL;
    /// Windows' ExtractRecognition keeps such a token, but one of them must
    /// not end every hover over its area with observations_too_large). A
    /// line left without words is left out too (CoreHost requires 1..256
    /// per line). A degenerate box is kept for CoreHost's decoder to drop,
    /// as ExtractRecognition does. A confidence outside 0...1 is clamped
    /// into it and a non-finite one becomes 0: CoreHost requires a finite
    /// 0..1 and Core reads it nowhere as a threshold. Every other limit and
    /// the byte budget are refused before sending, never cut short.
    public init(fingerprint: FrameFingerprint, recognized: [RecognizedLine]) {
        var lines: [ObservedLine] = []
        for line in recognized {
            let words = line.words.filter {
                $0.box.isFinite && (1...ObservationLimits.maxWordText).contains($0.text.utf16.count)
            }.map { ObservedWord(text: $0.text, box: $0.box) }
            guard !words.isEmpty else { continue }
            let confidence = line.confidence.isFinite ? min(max(line.confidence, 0), 1) : 0
            lines.append(ObservedLine(text: line.text, confidence: confidence, words: words))
        }
        self.init(fingerprint: fingerprint, lines: lines)
    }

    public var json: JSONValue { ["fingerprint": .string(fingerprint.hex), "lines": .array(lines.map(\.json))] }
}

/// The `source` of hover.resolve and ocr.select for Apple Vision
/// observations.
public struct ObservationsSource: Sendable, Hashable {
    public let display: ObservedDisplay
    public let captureBounds: PixelRect
    public let cursor: PixelPoint
    public let observations: [ObservationFrame]
    /// Only with zero observations, in smart mode: the plan's cached frame.
    public let cachedFingerprint: FrameFingerprint?

    public init(display: ObservedDisplay, captureBounds: PixelRect, cursor: PixelPoint,
                observations: [ObservationFrame], cachedFingerprint: FrameFingerprint? = nil) {
        self.display = display
        self.captureBounds = captureBounds
        self.cursor = cursor
        self.observations = observations
        self.cachedFingerprint = cachedFingerprint
    }

    public var json: JSONValue {
        var source: [String: JSONValue] = [
            "kind": "observations", "display": display.json, "captureBounds": captureBounds.json,
            "cursor": cursor.json, "observations": .array(observations.map(\.json)),
        ]
        if let cachedFingerprint { source["cachedFingerprint"] = .string(cachedFingerprint.hex) }
        return .object(source)
    }
}

/// CoreHost's per-request limits on observations (ObservationRules): a
/// request over any of them is bad_request there, so the app refuses it
/// itself, with the same Russian message as one over the byte budget, and
/// never cuts observations short.
public enum ObservationLimits {
    public static let maxLines = 256
    public static let maxLineText = 4000
    public static let maxWordsPerLine = 256
    public static let maxWordsPerObservation = 4096
    public static let maxWordText = 256

    /// The first limit `source` exceeds, in English, or nil.
    public static func problem(_ source: ObservationsSource) -> String? {
        for (frameIndex, frame) in source.observations.enumerated() {
            if frame.lines.count > maxLines {
                return "observation \(frameIndex) holds \(frame.lines.count) lines, at most \(maxLines) are allowed"
            }
            var words = 0
            for (lineIndex, line) in frame.lines.enumerated() {
                if line.text.utf16.count > maxLineText {
                    return "observation \(frameIndex) line \(lineIndex) holds more than \(maxLineText) UTF-16 units"
                }
                if !(1...maxWordsPerLine).contains(line.words.count) {
                    return "observation \(frameIndex) line \(lineIndex) holds \(line.words.count) words, 1..\(maxWordsPerLine) are allowed"
                }
                words += line.words.count
                if words > maxWordsPerObservation {
                    return "observation \(frameIndex) holds more than \(maxWordsPerObservation) words"
                }
                for word in line.words where !(1...maxWordText).contains(word.text.utf16.count) {
                    return "observation \(frameIndex) line \(lineIndex) has a word of \(word.text.utf16.count) UTF-16 units, "
                        + "1..\(maxWordText) are allowed"
                }
            }
        }
        return nil
    }
}

/// The byte budget of a request that carries observations.
public enum ObservationBudget {
    /// 900 KiB of the encoded request line: UTF-8 bytes of the whole frame,
    /// envelope and newline included. CoreHost ends the connection over an
    /// inbound frame longer than 1 MiB (frame_too_long), and a request
    /// inside every limit above can still be that long, so the app refuses
    /// earlier, with room for the envelope to grow, and never truncates.
    public static let maxLineBytes = 900 * 1024
}

/// ocr.plan's answer: where to capture for the next hover and how many
/// observations it takes.
public struct CapturePlan: Sendable, Hashable {
    public let mode: TranslationMode
    public let captureBounds: PixelRect
    /// 1 in dictionary mode, 2 in smart mode.
    public let observations: Int
    /// The cached frame's fingerprint: when the first capture has it, the
    /// hover may send zero observations.
    public let cachedFingerprint: FrameFingerprint?

    public init(mode: TranslationMode, captureBounds: PixelRect, observations: Int, cachedFingerprint: FrameFingerprint?) {
        self.mode = mode
        self.captureBounds = captureBounds
        self.observations = observations
        self.cachedFingerprint = cachedFingerprint
    }

    /// Nil when the result is not ocr.plan's shape.
    public init?(result: JSONValue) {
        guard let mode = result["mode"]?.stringValue.flatMap(TranslationMode.init(rawValue:)),
              let bounds = PixelRect(json: result["captureBounds"]), bounds.width >= 1, bounds.height >= 1,
              let observations = result["observations"]?.int64Value, (1...2).contains(observations) else { return nil }
        var cached: FrameFingerprint?
        if let raw = result["cachedFingerprint"] {
            guard let text = raw.stringValue, let fingerprint = FrameFingerprint(hex: text) else { return nil }
            cached = fingerprint
        }
        self.init(mode: mode, captureBounds: bounds, observations: Int(observations), cachedFingerprint: cached)
    }
}

/// ocr.select's selection: Core's choice near the cursor.
public struct OcrSelection: Sendable, Hashable {
    public let text: String
    public let hoveredWord: String
    /// UTF-16 units into `text`.
    public let characterIndex: Int
    public let sentenceStable: Bool
    public let speakerName: String?

    public init(text: String, hoveredWord: String, characterIndex: Int, sentenceStable: Bool, speakerName: String?) {
        self.text = text
        self.hoveredWord = hoveredWord
        self.characterIndex = characterIndex
        self.sentenceStable = sentenceStable
        self.speakerName = speakerName
    }
}

/// ocr.select's answer.
public struct OcrSelectResult: Sendable, Hashable {
    /// Nil when nothing near the cursor was selected (an explicit null).
    public let selection: OcrSelection?
    /// The decoded lines of observation 1, in order.
    public let lines: [String]

    public init(selection: OcrSelection?, lines: [String]) {
        self.selection = selection
        self.lines = lines
    }

    /// Nil when the result is not ocr.select's shape.
    public init?(result: JSONValue) {
        guard let rawSelection = result["selection"], let lines = result["lines"]?.stringArray else { return nil }
        if rawSelection == .null {
            self.init(selection: nil, lines: lines)
            return
        }
        guard let text = rawSelection["text"]?.stringValue, let word = rawSelection["hoveredWord"]?.stringValue,
              let index = rawSelection["characterIndex"]?.int64Value, let indexValue = Int(exactly: index),
              let stable = rawSelection["sentenceStable"]?.boolValue else { return nil }
        let speaker = rawSelection["speakerName"]?.stringValue
        self.init(selection: OcrSelection(text: text, hoveredWord: word, characterIndex: indexValue,
                                          sentenceStable: stable, speakerName: speaker), lines: lines)
    }
}

/// The game a hover belongs to, as hover.resolve's `game` and speaker.set's
/// (the host names it, docs/MACOS.md T29; GameIdentity makes it).
public struct HoverGame: Sendable, Hashable {
    public let key: String
    public let displayName: String

    public init(key: String, displayName: String) {
        self.key = key
        self.displayName = displayName
    }

    public var json: JSONValue { ["key": .string(key), "displayName": .string(displayName)] }
}
