import Foundation

/// A size in whole pixels.
public struct PixelSize: Sendable, Hashable, CustomStringConvertible {
    public let width: Int
    public let height: Int

    public init(width: Int, height: Int) {
        self.width = width
        self.height = height
    }

    public var description: String { "\(width)x\(height)" }
}

/// One captured frame as the recognizer and the fingerprint read it: dense
/// BGRA rows, top to bottom, 4 bytes per pixel and no row padding, the
/// layout tests/AsteriaScale.Tests.MacOS/CoreHost/frame-fingerprint-v1.txt
/// pins, so the app and CoreHost fingerprint the same bytes. The display's
/// pixels per point decide whether Vision sees the frame enlarged
/// (docs/MACOS.md T24); the protocol's unit stays the frame's own pixels.
public struct Frame: Sendable, Equatable {
    public static let bytesPerPixel = 4
    /// CoreHost's display side limit (ObservationRules.MaxDisplaySide): no
    /// capture can be larger than its display.
    public static let maxSide = 32768

    public let width: Int
    public let height: Int
    public let pixels: Data
    public let pixelsPerPoint: Double

    public init(width: Int, height: Int, pixels: Data, pixelsPerPoint: Double) throws(RecognitionFailure) {
        guard (1...Frame.maxSide).contains(width), (1...Frame.maxSide).contains(height) else {
            throw .invalidFrame("a frame must be 1..\(Frame.maxSide) pixels on each side, got \(width)x\(height)")
        }
        guard pixels.count == width * height * Frame.bytesPerPixel else {
            throw .invalidFrame("a \(width)x\(height) frame holds \(width * height * Frame.bytesPerPixel) bytes, got \(pixels.count)")
        }
        guard pixelsPerPoint.isFinite, pixelsPerPoint > 0 else {
            throw .invalidFrame("pixels per point must be a positive number, got \(pixelsPerPoint)")
        }
        self.width = width
        self.height = height
        self.pixels = pixels
        self.pixelsPerPoint = pixelsPerPoint
    }

    public var size: PixelSize { PixelSize(width: width, height: height) }

    /// Frame pixels are capture-bounds pixels 1:1 (T18, T24): a frame of any
    /// other size is refused, never scaled to fit, because every box sent to
    /// CoreHost would then be off by that scale.
    public func matches(_ bounds: PixelRect) -> Bool { width == bounds.width && height == bounds.height }
}

/// Why a frame was not recognized. `russian` may reach the user (rule 9);
/// `description` is English, for app.log.
public enum RecognitionFailure: Error, Sendable, Equatable, CustomStringConvertible {
    /// The frame is not the size of its capture bounds (T24).
    case frameMismatch(frame: PixelSize, bounds: PixelSize)
    case invalidFrame(String)
    /// The recognizer failed: its error domain and code.
    case recognizer(domain: String, code: Int)
    case cancelled

    public var code: String {
        switch self {
        case .frameMismatch: return "frame_mismatch"
        case .invalidFrame: return "invalid_frame"
        case .recognizer: return "recognizer_failed"
        case .cancelled: return "cancelled"
        }
    }

    public var russian: String {
        switch self {
        case .frameMismatch: return RU.recognitionFrameMismatch
        case .invalidFrame: return RU.recognitionFailed(code)
        case .recognizer(let domain, let number): return RU.recognitionFailed("\(domain) \(number)")
        case .cancelled: return RU.errorText("cancelled")
        }
    }

    public var description: String {
        switch self {
        case .frameMismatch(let frame, let bounds):
            return "the captured frame is \(frame), its capture bounds are \(bounds); boxes are never rescaled"
        case .invalidFrame(let reason): return "invalid frame: \(reason)"
        case .recognizer(let domain, let number): return "the recognizer failed: \(domain) \(number)"
        case .cancelled: return "cancelled"
        }
    }
}

/// A box in frame pixels: origin at the frame's top-left corner, y down,
/// edges as doubles (the protocol's word box, docs/MACOS.md T18).
public struct PixelBox: Sendable, Hashable, CustomStringConvertible {
    public let left: Double
    public let top: Double
    public let right: Double
    public let bottom: Double

    public init(left: Double, top: Double, right: Double, bottom: Double) {
        self.left = left
        self.top = top
        self.right = right
        self.bottom = bottom
    }

    public var isFinite: Bool { left.isFinite && top.isFinite && right.isFinite && bottom.isFinite }
    /// CoreHost's decoder drops such a box, as ExtractRecognition does.
    public var isDegenerate: Bool { !(right > left) || !(bottom > top) }
    public var width: Double { right - left }
    public var height: Double { bottom - top }
    public var centerX: Double { (left + right) / 2 }
    public var centerY: Double { (top + bottom) / 2 }

    public var description: String {
        String(format: "(%.1f, %.1f, %.1f, %.1f)", left, top, right, bottom)
    }
}

/// One word of a recognized line: its text, where it lies in the line's
/// string (UTF-16 offsets, as CoreHost's .NET strings count), and its box.
public struct RecognizedWord: Sendable, Hashable {
    public let text: String
    public let utf16Range: Range<Int>
    public let box: PixelBox

    public init(text: String, utf16Range: Range<Int>, box: PixelBox) {
        self.text = text
        self.utf16Range = utf16Range
        self.box = box
    }
}

/// One recognized line in frame pixels. Vision rates a line, not its words.
public struct RecognizedLine: Sendable, Hashable {
    public let text: String
    public let confidence: Double
    /// The line's own box, when the recognizer gave one.
    public let box: PixelBox?
    public let words: [RecognizedWord]

    public init(text: String, confidence: Double, box: PixelBox?, words: [RecognizedWord]) {
        self.text = text
        self.confidence = confidence
        self.box = box
        self.words = words
    }
}

/// The recognizer behind a hover: Apple Vision in the app
/// (AsteriaScaleVision's VisionTextRecognizer); HoverPipeline takes it
/// injected, so its tests use a stand-in. Kit stays Foundation and Darwin only, so everything that
/// orchestrates a hover can be tested without the real recognizer
/// (docs/MACOS.md T26).
public protocol TextRecognizer: Sendable {
    /// Recognizes `frame`, captured at `bounds` (display pixels). Lines and
    /// word boxes come back in frame pixels. A frame whose size is not the
    /// bounds' size fails with `frameMismatch` before anything is read.
    func recognize(_ frame: Frame, capturedAt bounds: PixelRect) async throws(RecognitionFailure) -> [RecognizedLine]
}

/// What a VNRecognizeTextRequest is set to (docs/MACOS.md T23, M18), as the
/// app's self-test reads it back from a real request (vision.config).
public struct RecognizerConfiguration: Sendable, Equatable, CustomStringConvertible {
    public var revision: Int
    /// "accurate" or "fast".
    public var recognitionLevel: String
    public var usesLanguageCorrection: Bool
    public var minimumTextHeight: Double
    public var recognitionLanguages: [String]
    public var automaticallyDetectsLanguage: Bool
    /// How many times a frame at 1 pixel per point is enlarged for Vision
    /// (T24); VisionTextRecognizer measures it on the image it would give Vision.
    public var lowDensityUpscale: Int

    public init(revision: Int, recognitionLevel: String, usesLanguageCorrection: Bool, minimumTextHeight: Double,
                recognitionLanguages: [String], automaticallyDetectsLanguage: Bool, lowDensityUpscale: Int) {
        self.revision = revision
        self.recognitionLevel = recognitionLevel
        self.usesLanguageCorrection = usesLanguageCorrection
        self.minimumTextHeight = minimumTextHeight
        self.recognitionLanguages = recognitionLanguages
        self.automaticallyDetectsLanguage = automaticallyDetectsLanguage
        self.lowDensityUpscale = lowDensityUpscale
    }

    /// T23: revision 3 set explicitly (later revisions may change the boxes
    /// the thresholds were measured on), the accurate level (the fast one
    /// misreads outlined and pixel text), language correction, no minimum
    /// height (the default would drop small dialogue text in a large frame),
    /// English only without detection (M18: Japanese waits for roadmap 9
    /// and 16), and 1x frames enlarged twice (T24: in the 20.3 plan's
    /// measurement Vision silently skipped 9-11 px lines at 1x and read them
    /// enlarged).
    public static let required = RecognizerConfiguration(
        revision: 3, recognitionLevel: "accurate", usesLanguageCorrection: true, minimumTextHeight: 0,
        recognitionLanguages: ["en-US"], automaticallyDetectsLanguage: false, lowDensityUpscale: 2)

    /// Every field that differs from `expected`, in English, for the self-test.
    public func problems(against expected: RecognizerConfiguration = .required) -> [String] {
        var problems: [String] = []
        func check<Value: Equatable>(_ name: String, _ actual: Value, _ wanted: Value) {
            if actual != wanted { problems.append("\(name) is \(actual), not \(wanted)") }
        }
        check("revision", revision, expected.revision)
        check("recognitionLevel", recognitionLevel, expected.recognitionLevel)
        check("usesLanguageCorrection", usesLanguageCorrection, expected.usesLanguageCorrection)
        check("minimumTextHeight", minimumTextHeight, expected.minimumTextHeight)
        check("recognitionLanguages", recognitionLanguages, expected.recognitionLanguages)
        check("automaticallyDetectsLanguage", automaticallyDetectsLanguage, expected.automaticallyDetectsLanguage)
        check("lowDensityUpscale", lowDensityUpscale, expected.lowDensityUpscale)
        return problems
    }

    public var description: String {
        "revision \(revision), level \(recognitionLevel), language correction \(usesLanguageCorrection), "
            + "minimumTextHeight \(minimumTextHeight), languages \(recognitionLanguages), "
            + "automaticallyDetectsLanguage \(automaticallyDetectsLanguage), 1x frames upscaled \(lowDensityUpscale)x"
    }
}

/// A frame's fingerprint: OcrTextRules.ComputeFrameFingerprint of its pixel
/// bytes, the first 8 bytes of their SHA-256 read as a little-endian UInt64
/// (docs/MACOS.md T27). On the wire it is exactly 16 lowercase hex digits
/// of that value (a JSON number cannot hold a UInt64), so the digits are the
/// first 8 digest bytes in reverse order.
public struct FrameFingerprint: Sendable, Hashable, CustomStringConvertible {
    public let value: UInt64

    public init(value: UInt64) { self.value = value }

    /// From a SHA-256 digest; nil when it holds fewer than 8 bytes.
    public init?(digest: some Collection<UInt8>) {
        guard digest.count >= 8 else { return nil }
        var value: UInt64 = 0
        for (index, byte) in digest.prefix(8).enumerated() {
            value |= UInt64(byte) << (8 * UInt64(index))
        }
        self.value = value
    }

    /// The wire form back: exactly 16 lowercase hex digits, as CoreHost
    /// accepts it (ObservationRules.TryParseFingerprint).
    public init?(hex: String) {
        guard hex.utf8.count == 16,
              hex.utf8.allSatisfy({ (0x30...0x39).contains($0) || (0x61...0x66).contains($0) }),
              let value = UInt64(hex, radix: 16) else { return nil }
        self.value = value
    }

    /// ToString("x16").
    public var hex: String {
        let digits = String(value, radix: 16)
        return String(repeating: "0", count: 16 - digits.count) + digits
    }

    public var description: String { hex }
}

/// Fingerprints a frame's bytes. The SHA-256 lives behind this seam
/// (AsteriaScaleVision's CryptoKit implementation, T27), so Kit stays
/// Foundation and Darwin only and the hover orchestration can still
/// fingerprint frames.
public protocol FrameFingerprinting: Sendable {
    func fingerprint(of pixels: Data) -> FrameFingerprint
}

extension FrameFingerprinting {
    public func fingerprint(of frame: Frame) -> FrameFingerprint { fingerprint(of: frame.pixels) }
}
