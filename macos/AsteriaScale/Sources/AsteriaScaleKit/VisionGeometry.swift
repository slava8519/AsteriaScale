import Foundation

/// A point in Vision's normalized image coordinates: 0...1 across the image
/// Vision saw, origin at its bottom-left corner, y up.
public struct UnitPoint: Sendable, Hashable {
    public let x: Double
    public let y: Double

    public init(x: Double, y: Double) {
        self.x = x
        self.y = y
    }
}

/// The four corners of a Vision box (VNRectangleObservation), normalized.
/// A rotated or skewed box keeps its corners; the frame box is their hull.
public struct NormalizedQuad: Sendable, Hashable {
    public let topLeft: UnitPoint
    public let topRight: UnitPoint
    public let bottomRight: UnitPoint
    public let bottomLeft: UnitPoint

    public init(topLeft: UnitPoint, topRight: UnitPoint, bottomRight: UnitPoint, bottomLeft: UnitPoint) {
        self.topLeft = topLeft
        self.topRight = topRight
        self.bottomRight = bottomRight
        self.bottomLeft = bottomLeft
    }

    /// An upright box from Vision's boundingBox (x, y = its bottom-left corner).
    public init(x: Double, y: Double, width: Double, height: Double) {
        self.init(topLeft: UnitPoint(x: x, y: y + height), topRight: UnitPoint(x: x + width, y: y + height),
                  bottomRight: UnitPoint(x: x + width, y: y), bottomLeft: UnitPoint(x: x, y: y))
    }

    public var corners: [UnitPoint] { [topLeft, topRight, bottomRight, bottomLeft] }
}

/// From Vision's normalized boxes to the protocol's frame pixels (origin at
/// the frame's top-left corner, y down; docs/MACOS.md T18, T24). Pure, so
/// the flip, the hull, the 2x inverse and the clipping are tested without
/// Vision.
public enum VisionGeometry {
    /// How many times Vision sees a frame enlarged (T24): twice below 1.5
    /// pixels per point, as it is from 1.5 up. In the 20.3 plan's
    /// measurement Vision silently skipped 9-11 px lines at 1x and read them
    /// enlarged, and it reads 2x text as it is. 1.5 splits the two: a 1x
    /// display, and a scaled mode closer to 1x than to 2x, gives text about
    /// as few pixels as 1x does; a scaled Retina mode (about 1.7-2) about as
    /// many as 2x does.
    public static func upscale(pixelsPerPoint: Double) -> Int {
        pixelsPerPoint < 1.5 ? RecognizerConfiguration.required.lowDensityUpscale : 1
    }

    /// A Vision box over a frame of `frameWidth` x `frameHeight` pixels that
    /// Vision saw enlarged `upscale` times, in frame pixels.
    ///
    /// Each corner goes to the pixels of the image Vision saw (x = nx * W',
    /// y = (1 - ny) * H', W' and H' being the frame's size times `upscale`)
    /// and back to frame pixels (divided by `upscale`); the box is the
    /// corners' axis-aligned hull (as WindowsAiOcrService's BoundsOf), then
    /// clipped to [0, W] x [0, H]. Clipped, every edge lies inside the frame,
    /// where CoreHost accepts it (it refuses edges beyond one pixel outside).
    /// A box clipped to nothing stays degenerate (right <= left or bottom <=
    /// top) and is sent as it is: CoreHost's decoder drops it, as
    /// ExtractRecognition does, so that rule lives in one place. Nil when a
    /// corner is not finite: CoreHost would refuse the whole request.
    public static func frameBox(_ quad: NormalizedQuad, frameWidth: Int, frameHeight: Int, upscale: Int) -> PixelBox? {
        guard frameWidth >= 1, frameHeight >= 1, upscale >= 1 else { return nil }
        let factor = Double(upscale)
        let seenWidth = Double(frameWidth) * factor
        let seenHeight = Double(frameHeight) * factor
        var left = Double.infinity, top = Double.infinity, right = -Double.infinity, bottom = -Double.infinity
        for corner in quad.corners {
            guard corner.x.isFinite, corner.y.isFinite else { return nil }
            let x = corner.x * seenWidth / factor
            let y = (1 - corner.y) * seenHeight / factor
            left = min(left, x)
            right = max(right, x)
            top = min(top, y)
            bottom = max(bottom, y)
        }
        guard left.isFinite, top.isFinite, right.isFinite, bottom.isFinite else { return nil }
        let width = Double(frameWidth), height = Double(frameHeight)
        func clip(_ value: Double, _ side: Double) -> Double { min(max(value, 0), side) }
        return PixelBox(left: clip(left, width), top: clip(top, height), right: clip(right, width),
                        bottom: clip(bottom, height))
    }
}

/// A word of a line string and where it lies in it.
public struct WordRange: Sendable, Hashable {
    public let text: String
    /// UTF-16 offsets into the line, as CoreHost's .NET strings count.
    public let utf16: Range<Int>

    public init(text: String, utf16: Range<Int>) {
        self.text = text
        self.utf16 = utf16
    }
}

/// Splits a recognized line into words, so each word's box can be asked of
/// Vision (VNRecognizedText.boundingBox(for:); in the accurate level a
/// Latin range gives the whole word's box).
public enum WordSplitter {
    /// The maximal runs of scalars that are not Unicode White_Space (space,
    /// tab, no-break space, ideographic space, ...): leading, trailing and
    /// repeated whitespace yields no empty word. Scalars, not UTF-16 units,
    /// are walked, so a surrogate pair is never split; a combining mark is
    /// not whitespace and stays with its letter.
    public static func words(in line: String) -> [WordRange] {
        var words: [WordRange] = []
        let scalars = line.unicodeScalars
        var start: (index: String.UnicodeScalarView.Index, offset: Int)?
        var offset = 0
        var index = scalars.startIndex
        while index != scalars.endIndex {
            let scalar = scalars[index]
            if scalar.properties.isWhitespace {
                if let open = start {
                    words.append(WordRange(text: String(scalars[open.index..<index]), utf16: open.offset..<offset))
                    start = nil
                }
            } else if start == nil {
                start = (index, offset)
            }
            offset += scalar.utf16.count
            index = scalars.index(after: index)
        }
        if let open = start {
            words.append(WordRange(text: String(scalars[open.index..<scalars.endIndex]), utf16: open.offset..<offset))
        }
        return words
    }

    /// The string range of UTF-16 offsets in `line`, or nil when they do not
    /// lie on scalar boundaries inside it.
    public static func stringRange(_ utf16: Range<Int>, in line: String) -> Range<String.Index>? {
        let units = line.utf16
        guard utf16.lowerBound >= 0, utf16.upperBound <= units.count,
              let lower = units.index(units.startIndex, offsetBy: utf16.lowerBound, limitedBy: units.endIndex),
              let upper = units.index(units.startIndex, offsetBy: utf16.upperBound, limitedBy: units.endIndex),
              let lowerScalar = lower.samePosition(in: line.unicodeScalars),
              let upperScalar = upper.samePosition(in: line.unicodeScalars) else { return nil }
        return lowerScalar..<upperScalar
    }
}
