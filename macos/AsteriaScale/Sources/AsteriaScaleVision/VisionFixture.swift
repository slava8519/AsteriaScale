import AsteriaScaleKit
import Foundation

/// The Latin fixture the warm-up, the app's self-test (vision.fixture,
/// ocr.select) and the DoD 6 probe recognize with the real Vision, and the
/// check of what Vision read against what was rendered. Shared so the probe
/// proves exactly what the self-test then runs.
public enum VisionFixture {
    /// English only (M18); words a language model cannot "correct" into
    /// others, and "bank" for ocr.select (the golden transcript's word).
    public static let lines = [
        "He sat on the bank of the river.",
        "The quick brown fox jumps over the lazy dog.",
        "Sphinx of black quartz, judge my vow.",
    ]
    public static let pointSize = 14.0
    /// The word ocr.select hovers: the fifth of the first line.
    public static let hoveredLine = 0
    public static let hoveredWord = "bank"

    /// One short line for the warm-up: it compiles the same models.
    public static let warmUpLine = "The bank was steep."

    /// A box edge may differ from the rendered one by this share of the
    /// font size in frame pixels (0.5 em: 7 px at 1x, 14 px at 2x). The
    /// renderer's boxes are typographic (pen advance, ascent to descent);
    /// Vision's are its own estimate of the word, which the probe measured
    /// well inside this on macOS 27 (docs/DEVELOPMENT.md, the Vision probe).
    public static let edgeTolerance = 0.5

    public static func render(pixelsPerPoint: Double) throws(RecognitionFailure) -> RenderedFixture {
        try FixtureRenderer.render(lines: lines, pointSize: pointSize, pixelsPerPoint: pixelsPerPoint)
    }

    /// The warm-up: one short line at 2x, recognized once. The first call in
    /// a fresh home compiles Vision's models (docs/MACOS.md T25).
    public static func warmUp(_ recognizer: any TextRecognizer = VisionTextRecognizer()) async throws(RecognitionFailure)
        -> [RecognizedLine] {
        let fixture = try FixtureRenderer.render(lines: [warmUpLine], pointSize: pointSize, pixelsPerPoint: 2)
        return try await recognizer.recognize(fixture.frame, capturedAt: fixture.bounds)
    }

    /// How a recognition compares with the rendered layout.
    public struct Comparison: Sendable {
        public let problems: [String]
        /// The largest difference of a word box edge from the rendered one, in frame pixels.
        public let maxEdgeError: Double
        public let tolerance: Double
        public let lines: Int
        public let words: Int

        public var ok: Bool { problems.isEmpty }
        public var summary: String {
            String(format: "%d lines, %d words, largest edge error %.1f px (tolerance %.1f px)",
                   lines, words, maxEdgeError, tolerance)
        }
    }

    /// The lines must read exactly as rendered (in the rendered order, top
    /// to bottom), each with the same words, and each word box's four edges
    /// within `edgeTolerance` of the rendered word's.
    public static func compare(_ recognized: [RecognizedLine], with layout: FixtureLayout) -> Comparison {
        let tolerance = edgeTolerance * layout.fontPixels
        var problems: [String] = []
        var maxError = 0.0
        var words = 0
        let ordered = recognized.sorted { ($0.box?.top ?? 0) < ($1.box?.top ?? 0) }
        if ordered.count != layout.lines.count {
            problems.append("read \(ordered.count) lines, rendered \(layout.lines.count): \(ordered.map(\.text))")
        }
        for (line, expected) in zip(ordered, layout.lines) {
            guard line.text == expected.text else {
                problems.append("read «\(line.text)», rendered «\(expected.text)»")
                continue
            }
            guard line.words.map(\.text) == expected.words.map(\.text) else {
                problems.append("words of «\(expected.text)»: \(line.words.map(\.text))")
                continue
            }
            for (word, rendered) in zip(line.words, expected.words) {
                words += 1
                let error = [abs(word.box.left - rendered.box.left), abs(word.box.top - rendered.box.top),
                             abs(word.box.right - rendered.box.right), abs(word.box.bottom - rendered.box.bottom)].max() ?? 0
                maxError = max(maxError, error)
                if !(error <= tolerance) {
                    problems.append("«\(word.text)» at \(word.box), rendered at \(rendered.box)")
                }
            }
        }
        return Comparison(problems: problems, maxEdgeError: maxError, tolerance: tolerance, lines: ordered.count,
                          words: words)
    }

    /// The image Vision must have been given for a fixture frame (T24):
    /// twice the frame at 1 pixel per point, the frame itself at 2. Spelled
    /// out for the two fixture densities rather than taken from
    /// VisionGeometry, so a recognizer that skips the enlargement fails here.
    public static func expectedSeenSize(of frame: Frame) -> PixelSize {
        frame.pixelsPerPoint == 1 ? PixelSize(width: frame.width * 2, height: frame.height * 2) : frame.size
    }

    /// Nil when Vision was given the image T24 requires for `frame`, else why not.
    public static func seenProblem(_ seen: PixelSize, for frame: Frame) -> String? {
        let expected = expectedSeenSize(of: frame)
        return seen == expected ? nil : "Vision was given \(seen) for the \(frame.size) frame, not \(expected)"
    }

    /// The display, capture bounds and cursor ocr.select gets for a fixture:
    /// the frame stands for a whole display captured at (0, 0), and the
    /// cursor rests on the middle of the hovered word's rendered box.
    public static func selectSource(_ fixture: RenderedFixture, recognized: [RecognizedLine],
                                    fingerprint: FrameFingerprint) -> ObservationsSource? {
        guard fixture.layout.lines.indices.contains(hoveredLine),
              let word = fixture.layout.lines[hoveredLine].words.first(where: { $0.text == hoveredWord }) else { return nil }
        let display = ObservedDisplay(id: 1, width: fixture.frame.width, height: fixture.frame.height)
        let cursor = PixelPoint(x: Int(word.box.centerX.rounded(.down)), y: Int(word.box.centerY.rounded(.down)))
        return ObservationsSource(display: display, captureBounds: fixture.bounds, cursor: cursor,
                                  observations: [ObservationFrame(fingerprint: fingerprint, recognized: recognized)])
    }

    /// The UTF-16 span of the hovered word in its line.
    public static var hoveredSpan: Range<Int>? {
        WordSplitter.words(in: lines[hoveredLine]).first { $0.text == hoveredWord }?.utf16
    }
}

/// The self-test's frame source (hover.observations): the Latin fixture,
/// rendered at the display's density and placed on a white frame of exactly
/// the plan's bounds, so that the middle of the rendered «bank» lies under
/// the cursor (or as near as the frame allows). It never touches the screen;
/// the real capture is the app's ScreenSource, which no check runs.
public struct FixtureFrameSource: FrameSource {
    /// The cursor in the display's pixels, as the hover sends it.
    public let cursor: PixelPoint

    public init(cursor: PixelPoint) { self.cursor = cursor }

    public func capture(_ bounds: PixelRect, on display: DisplayDescription) async throws(CaptureFailure) -> Frame {
        let fixture: RenderedFixture
        do {
            fixture = try VisionFixture.render(pixelsPerPoint: display.pixelsPerPointX)
        } catch {
            throw .invalidFrame(error.description)
        }
        let limit = Frame.maxSide
        guard fixture.layout.lines.indices.contains(VisionFixture.hoveredLine),
              let word = fixture.layout.lines[VisionFixture.hoveredLine].words.first(where: { $0.text == VisionFixture.hoveredWord }),
              let x = Int(exactly: word.box.centerX.rounded(.down)), let y = Int(exactly: word.box.centerY.rounded(.down)),
              (0...limit).contains(cursor.x), (0...limit).contains(cursor.y), (0...limit).contains(bounds.x),
              (0...limit).contains(bounds.y),
              let origin = Frame.origin(placing: fixture.frame.size, target: PixelPoint(x: x, y: y),
                                        at: PixelPoint(x: cursor.x - bounds.x, y: cursor.y - bounds.y), canvas: bounds.size)
        else {
            throw .invalidFrame("the \(fixture.frame.size) fixture cannot be placed under \(cursor) in \(bounds)")
        }
        do {
            return try fixture.frame.placed(onCanvasWidth: bounds.width, height: bounds.height, at: origin)
        } catch {
            throw .invalidFrame(error.description)
        }
    }
}
