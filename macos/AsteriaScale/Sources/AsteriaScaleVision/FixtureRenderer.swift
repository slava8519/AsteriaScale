import AsteriaScaleKit
import CoreGraphics
import CoreText
import Foundation

/// Where the renderer put the text, in frame pixels (origin at the top-left
/// corner, y down): each line's typographic box (ascent to descent, pen
/// start to pen end) and each word's span of it.
public struct FixtureLayout: Sendable, Hashable {
    public struct Word: Sendable, Hashable {
        public let text: String
        public let box: PixelBox
    }

    public struct Line: Sendable, Hashable {
        public let text: String
        public let box: PixelBox
        public let words: [Word]
    }

    public let lines: [Line]
    /// The font's size in frame pixels (points times pixels per point).
    public let fontPixels: Double
}

/// A rendered fixture: the frame, captured as if at (0, 0) of a display
/// exactly its size, and where its text lies.
public struct RenderedFixture: Sendable {
    public let frame: Frame
    public let layout: FixtureLayout

    /// The capture bounds the frame stands for.
    public var bounds: PixelRect { PixelRect(x: 0, y: 0, width: frame.width, height: frame.height) }
}

/// Renders known Latin text with CoreText into a frame in memory, black on
/// white, for the Vision warm-up, the self-test and the DoD 6 probe: no
/// image file is committed (game frames are screenshots, roadmap card 9),
/// and nothing touches the screen. Never called by the Swift suite: fonts
/// may write caches into the home it runs in.
public enum FixtureRenderer {
    /// Helvetica ships with every macOS.
    public static let fontName = "Helvetica"

    /// - Parameters:
    ///   - lines: one string per line, top to bottom.
    ///   - pointSize: the font size in points.
    ///   - pixelsPerPoint: the display density the frame stands for (2 on
    ///     Retina; 1 takes Vision's 2x upscale path).
    ///   - margin: the blank border in points.
    ///   - lineAdvance: the distance between lines in font sizes.
    public static func render(lines: [String], pointSize: Double, pixelsPerPoint: Double, margin: Double = 24,
                              lineAdvance: Double = 2.0) throws(RecognitionFailure) -> RenderedFixture {
        guard !lines.isEmpty, pointSize > 0, pixelsPerPoint > 0 else {
            throw .invalidFrame("a fixture needs lines, a point size and a density")
        }
        let fontPixels = pointSize * pixelsPerPoint
        let font = CTFontCreateWithName(fontName as CFString, CGFloat(fontPixels), nil)
        let black = CGColor(gray: 0, alpha: 1)
        let attributes = [kCTFontAttributeName: font, kCTForegroundColorAttributeName: black] as CFDictionary
        let marginPixels = (margin * pixelsPerPoint).rounded()

        var typeset: [(line: CTLine, text: String, ascent: Double, descent: Double, width: Double)] = []
        for text in lines {
            guard let string = CFAttributedStringCreate(nil, text as CFString, attributes) else {
                throw .invalidFrame("cannot make the attributed string of \(text)")
            }
            let line = CTLineCreateWithAttributedString(string)
            var ascent: CGFloat = 0, descent: CGFloat = 0, leading: CGFloat = 0
            let width = CTLineGetTypographicBounds(line, &ascent, &descent, &leading)
            typeset.append((line, text, Double(ascent), Double(descent), width))
        }
        let advance = fontPixels * lineAdvance
        let widest = typeset.map(\.width).max() ?? 0
        let width = Int((marginPixels * 2 + widest).rounded(.up))
        let height = Int((marginPixels * 2 + advance * Double(lines.count - 1) + (typeset.last.map { $0.ascent + $0.descent } ?? 0))
            .rounded(.up))
        guard width >= 1, height >= 1, width <= Frame.maxSide, height <= Frame.maxSide,
              let context = CGContext(data: nil, width: width, height: height, bitsPerComponent: 8,
                                      bytesPerRow: width * Frame.bytesPerPixel, space: PixelBuffer.sRGB,
                                      bitmapInfo: PixelBuffer.bitmapInfo.rawValue) else {
            throw .invalidFrame("cannot make a \(width)x\(height) bitmap")
        }
        context.setFillColor(CGColor(gray: 1, alpha: 1))
        context.fill(CGRect(x: 0, y: 0, width: width, height: height))
        context.setShouldAntialias(true)

        var layoutLines: [FixtureLayout.Line] = []
        for (index, item) in typeset.enumerated() {
            // Top-down: the line's top edge, then its baseline one ascent lower.
            let top = marginPixels + advance * Double(index)
            let baseline = top + item.ascent
            context.textPosition = CGPoint(x: marginPixels, y: Double(height) - baseline)
            CTLineDraw(item.line, context)
            let bottom = baseline + item.descent
            var words: [FixtureLayout.Word] = []
            for word in WordSplitter.words(in: item.text) {
                let start = CTLineGetOffsetForStringIndex(item.line, word.utf16.lowerBound, nil)
                let end = CTLineGetOffsetForStringIndex(item.line, word.utf16.upperBound, nil)
                words.append(FixtureLayout.Word(text: word.text, box: PixelBox(
                    left: marginPixels + Double(start), top: top, right: marginPixels + Double(end), bottom: bottom)))
            }
            layoutLines.append(FixtureLayout.Line(
                text: item.text, box: PixelBox(left: marginPixels, top: top, right: marginPixels + item.width, bottom: bottom),
                words: words))
        }
        guard let pixels = PixelBuffer.bytes(of: context) else { throw .invalidFrame("cannot read the bitmap") }
        let frame = try Frame(width: pixels.width, height: pixels.height, pixels: pixels.bytes, pixelsPerPoint: pixelsPerPoint)
        return RenderedFixture(frame: frame, layout: FixtureLayout(lines: layoutLines, fontPixels: fontPixels))
    }
}
