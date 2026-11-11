import AsteriaScaleKit
import CoreGraphics
import Foundation
import Vision

/// One recognition with what Vision was given: the self-test and the DoD 6
/// probe check that a 1x frame really reached Vision enlarged (T24), which
/// the frame-pixel boxes alone cannot show (Vision's boxes are normalized to
/// whatever image it saw).
public struct VisionRecognition: Sendable {
    public let lines: [RecognizedLine]
    /// The size of the image handed to VNImageRequestHandler, in its pixels.
    public let seenSize: PixelSize
}

/// Apple Vision behind Kit's TextRecognizer (docs/MACOS.md T14, T23, T24,
/// M18). Only the app (its self-test included) and the DoD 6 probe run it,
/// each only in a throwaway home when an agent starts them: the first
/// request in a fresh home compiles Vision's models into that home's
/// Library/Caches for many seconds (T25, T26).
public struct VisionTextRecognizer: TextRecognizer {
    public init() {}

    /// A request set exactly as T23 says. Every value is set explicitly,
    /// the revision included, so an OS default that changes cannot change
    /// what the self-test measured.
    static func makeRequest() -> VNRecognizeTextRequest {
        let request = VNRecognizeTextRequest()
        request.revision = VNRecognizeTextRequestRevision3
        request.recognitionLevel = .accurate
        request.usesLanguageCorrection = true
        request.minimumTextHeight = 0
        request.recognitionLanguages = RecognizerConfiguration.required.recognitionLanguages
        request.automaticallyDetectsLanguage = false
        return request
    }

    /// The configuration as `recognize` uses it (the self-test's vision.config
    /// prints and checks it): the six request values read back from a
    /// request made as `recognize` makes it, and the enlargement of a frame
    /// at 1 pixel per point measured on the image `recognize` would hand to
    /// Vision (no Vision call).
    public static var configuration: RecognizerConfiguration {
        let request = makeRequest()
        return RecognizerConfiguration(
            revision: request.revision,
            recognitionLevel: request.recognitionLevel == .accurate ? "accurate" : "fast",
            usesLanguageCorrection: request.usesLanguageCorrection,
            minimumTextHeight: Double(request.minimumTextHeight),
            recognitionLanguages: request.recognitionLanguages,
            automaticallyDetectsLanguage: request.automaticallyDetectsLanguage,
            lowDensityUpscale: measuredLowDensityUpscale())
    }

    /// How many times the image Vision gets for a 2x1 frame at 1 pixel per
    /// point is larger than the frame; 0 when it cannot be made or is not
    /// enlarged evenly.
    static func measuredLowDensityUpscale() -> Int {
        guard let frame = try? Frame(width: 2, height: 1, pixels: Data(repeating: 255, count: 8), pixelsPerPoint: 1),
              let seen = PixelBuffer.visionImage(of: frame),
              seen.image.width % frame.width == 0, seen.image.height % frame.height == 0,
              seen.image.width / frame.width == seen.image.height / frame.height else { return 0 }
        return seen.image.width / frame.width
    }

    /// The revisions this system's Vision offers for the request.
    public static var supportedRevisions: [Int] { Array(VNRecognizeTextRequest.supportedRevisions).sorted() }

    public func recognize(_ frame: Frame, capturedAt bounds: PixelRect) async throws(RecognitionFailure) -> [RecognizedLine] {
        try await recognition(frame, capturedAt: bounds).lines
    }

    /// Recognizes the frame on a global queue (a request blocks its thread
    /// for tens of milliseconds warm, 35-49 ms on the fixtures on macOS 27,
    /// and for about 24 s on a fresh home's first call). A frame whose size
    /// is not the bounds' size is refused before anything is read (T24). The
    /// request itself is not cancelled midway; a cancelled caller drops the
    /// result.
    public func recognition(_ frame: Frame, capturedAt bounds: PixelRect) async throws(RecognitionFailure)
        -> VisionRecognition {
        guard frame.matches(bounds) else { throw .frameMismatch(frame: frame.size, bounds: bounds.size) }
        let result = await withCheckedContinuation { (continuation: CheckedContinuation<Result<VisionRecognition, RecognitionFailure>, Never>) in
            DispatchQueue.global(qos: .userInitiated).async {
                continuation.resume(returning: Self.run(frame))
            }
        }
        if Task.isCancelled { throw .cancelled }
        return try result.get()
    }

    /// One synchronous recognition: the frame (enlarged for Vision at 1
    /// pixel per point), one request, and every line's words with their
    /// boxes in frame pixels. A word whose box Vision cannot give is left
    /// out; a line keeps its text either way.
    static func run(_ frame: Frame) -> Result<VisionRecognition, RecognitionFailure> {
        guard let vision = PixelBuffer.visionImage(of: frame) else {
            return .failure(.invalidFrame("the frame has no image for Vision"))
        }
        let seen = vision.image, upscale = vision.upscale
        let request = makeRequest()
        let handler = VNImageRequestHandler(cgImage: seen, orientation: .up, options: [:])
        do {
            try handler.perform([request])
        } catch {
            let failure = error as NSError
            return .failure(.recognizer(domain: failure.domain, code: failure.code))
        }
        var lines: [RecognizedLine] = []
        for observation in request.results ?? [] {
            guard let candidate = observation.topCandidates(1).first else { continue }
            let text = candidate.string
            var words: [RecognizedWord] = []
            for word in WordSplitter.words(in: text) {
                guard let range = WordSplitter.stringRange(word.utf16, in: text),
                      let rectangle = try? candidate.boundingBox(for: range),
                      let box = VisionGeometry.frameBox(quad(of: rectangle), frameWidth: frame.width,
                                                        frameHeight: frame.height, upscale: upscale) else { continue }
                words.append(RecognizedWord(text: word.text, utf16Range: word.utf16, box: box))
            }
            let lineBox = VisionGeometry.frameBox(quad(of: observation), frameWidth: frame.width,
                                                  frameHeight: frame.height, upscale: upscale)
            lines.append(RecognizedLine(text: text, confidence: confidence(candidate.confidence), box: lineBox,
                                        words: words))
        }
        return .success(VisionRecognition(lines: lines, seenSize: PixelSize(width: seen.width, height: seen.height)))
    }

    static func quad(of rectangle: VNRectangleObservation) -> NormalizedQuad {
        func point(_ point: CGPoint) -> UnitPoint { UnitPoint(x: Double(point.x), y: Double(point.y)) }
        return NormalizedQuad(topLeft: point(rectangle.topLeft), topRight: point(rectangle.topRight),
                              bottomRight: point(rectangle.bottomRight), bottomLeft: point(rectangle.bottomLeft))
    }

    /// Vision's Float as the shortest double that prints the same (0.3, not
    /// 0.30000001192092896): the wire then carries what Vision reported.
    static func confidence(_ value: Float) -> Double {
        Double(String(describing: value)) ?? Double(value)
    }
}
