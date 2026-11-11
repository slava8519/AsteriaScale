import Foundation
import Testing
@testable import AsteriaScaleKit

/// Cocoa points to display pixels and back to the capture's points
/// (docs/MACOS.md T18, T21), on synthetic displays: no window server.
@Suite struct CaptureGeometryTests {
    static func display(_ id: UInt32, _ x: Double, _ y: Double, _ width: Double, _ height: Double,
                        scale: Int) -> DisplayDescription? {
        DisplayDescription(id: id, frame: PointRect(x: x, y: y, width: width, height: height),
                           pixelWidth: Int(width) * scale, pixelHeight: Int(height) * scale)
    }

    /// The primary 1x display, a 2x display left of it, a 1x one right of
    /// it reaching higher (Cocoa y up: its top is 460 points above the
    /// primary's), a 2x one above and a 1x one below.
    static func arrangement() throws -> [DisplayDescription] {
        [
            try #require(display(1, 0, 0, 1920, 1080, scale: 1)),
            try #require(display(2, -1512, 0, 1512, 982, scale: 2)),
            try #require(display(3, 1920, 100, 2560, 1440, scale: 1)),
            try #require(display(4, 0, 1080, 1728, 1117, scale: 2)),
            try #require(display(5, 200, -1080, 1920, 1080, scale: 1)),
        ]
    }

    @Test func cursorOnEachDisplayAndAtItsEdges() throws {
        let displays = try Self.arrangement()
        let cases: [(point: (Double, Double), display: UInt32?, cursor: (Int, Int), comment: Comment)] = [
            ((0, 1080), 1, (0, 0), "the primary's top-left pixel: Cocoa y == maxY is its top row"),
            ((1919.5, 0.5), 1, (1919, 1079), "the primary's bottom-right pixel"),
            ((960.75, 540.25), 1, (960, 539), "floor, not round"),
            ((500, 0), 5, (300, 0), "y == minY belongs to the display below, as its top row"),
            ((0, 500), 1, (0, 580), "x == minX belongs to the display it starts"),
            ((-0.5, 500), 2, (3023, 964), "the 2x display on the left, in its own pixels"),
            ((-1512, 982), 2, (0, 0), "its top-left corner"),
            ((-1511.75, 0.25), 2, (0, 1963), "its bottom row"),
            ((100.25, 2197), 4, (200, 0), "the 2x display above, top row"),
            ((100.25, 1080.25), 4, (200, 2233), "its bottom row; y == 1080 is the primary's top row"),
            ((100, 1080), 1, (100, 0), "the shared edge goes to the lower display's top row"),
            ((1920, 1540), 3, (0, 0), "the right display's top-left corner"),
            ((4479.9, 100.1), 3, (2559, 1439), "its bottom-right pixel"),
            ((2119.5, -1079.5), 5, (1919, 1079), "the display below, bottom-right"),
            ((-100, 1000), nil, (0, 0), "a gap between displays: no hover"),
            ((5000, 500), nil, (0, 0), "beyond every display"),
        ]
        for item in cases {
            let located = CaptureGeometry.locate(GlobalPoint(x: item.point.0, y: item.point.1), on: displays)
            #expect(located?.display.id == item.display, item.comment)
            if item.display != nil {
                #expect(located?.cursor == PixelPoint(x: item.cursor.0, y: item.cursor.1), item.comment)
            }
        }
        #expect(CaptureGeometry.locate(GlobalPoint(x: .nan, y: 10), on: displays) == nil)
        #expect(CaptureGeometry.locate(GlobalPoint(x: 10, y: .infinity), on: displays) == nil)
        #expect(CaptureGeometry.locate(GlobalPoint(x: 10, y: 10), on: []) == nil)
    }

    @Test func firstListedDisplayWinsAndTheCursorStaysInside() throws {
        // Mirrored displays share a frame: NSScreen.screens lists the primary first.
        let mirrored = [try #require(Self.display(9, 0, 0, 1440, 900, scale: 2)),
                        try #require(Self.display(8, 0, 0, 1440, 900, scale: 1))]
        #expect(CaptureGeometry.locate(GlobalPoint(x: 10, y: 10), on: mirrored)?.display.id == 9)
        // A scaled mode whose pixels are not a whole multiple of its points.
        let scaled = try #require(DisplayDescription(id: 6, frame: PointRect(x: 0, y: 0, width: 1512, height: 982),
                                                     pixelWidth: 3456, pixelHeight: 2234))
        for point in [GlobalPoint(x: 1511.999999, y: 0.000001), GlobalPoint(x: 0, y: 982)] {
            let cursor = try #require(CaptureGeometry.locate(point, on: [scaled])?.cursor)
            #expect((0..<3456).contains(cursor.x) && (0..<2234).contains(cursor.y), "\(cursor)")
        }
        #expect(CaptureGeometry.locate(GlobalPoint(x: 1511.999999, y: 0.000001), on: [scaled])?.cursor
                == PixelPoint(x: 3455, y: 2233))

        // Points whose floor lands on the display's width or height itself
        // (1512 - 1e-13 rounds to 1512; 982 - the least double is 982): the
        // clamp keeps the cursor inside, where CoreHost accepts it.
        let displays = try Self.arrangement()
        #expect(CaptureGeometry.locate(GlobalPoint(x: -1e-13, y: 500), on: displays)?.cursor == PixelPoint(x: 3023, y: 964))
        #expect(CaptureGeometry.locate(GlobalPoint(x: 10, y: .leastNonzeroMagnitude), on: displays)?.cursor
                == PixelPoint(x: 10, y: 1079))
        #expect(CaptureGeometry.locate(GlobalPoint(x: 10, y: .leastNonzeroMagnitude), on: [scaled])?.cursor
                == PixelPoint(x: 22, y: 2233))
    }

    @Test func displaysOutsideCoreHostsLimitsAreRefused() {
        #expect(Self.display(1, 0, 0, 0, 1080, scale: 1) == nil)
        #expect(DisplayDescription(id: 1, frame: PointRect(x: .nan, y: 0, width: 10, height: 10), pixelWidth: 10,
                                   pixelHeight: 10) == nil)
        #expect(DisplayDescription(id: 1, frame: PointRect(x: 0, y: 0, width: 10, height: 10), pixelWidth: 0,
                                   pixelHeight: 10) == nil)
        #expect(DisplayDescription(id: 1, frame: PointRect(x: 0, y: 0, width: 10, height: 10), pixelWidth: 32769,
                                   pixelHeight: 10) == nil)
        let display = DisplayDescription(id: 4_294_967_295, frame: PointRect(x: 0, y: 0, width: 1728, height: 1117),
                                         pixelWidth: 3456, pixelHeight: 2234)
        #expect(display?.wire == ObservedDisplay(id: 4_294_967_295, width: 3456, height: 2234))
        #expect(display?.wire.json == ["id": 4_294_967_295, "width": 3456, "height": 2234])
    }

    @Test func sourceRectIsTheBoundsInDisplayPointsUnrounded() throws {
        let retina = try #require(Self.display(2, -1512, 0, 1512, 982, scale: 2))
        let plain = try #require(Self.display(1, 0, 0, 1920, 1080, scale: 1))
        let dictionary = PixelRect(x: 1475, y: 1260, width: 900, height: 340)
        #expect(CaptureGeometry.sourceRect(for: dictionary, on: retina) == PointRect(x: 737.5, y: 630, width: 450, height: 170))
        #expect(CaptureGeometry.sourceRect(for: PixelRect(x: 1, y: 3, width: 901, height: 341), on: retina)
                == PointRect(x: 0.5, y: 1.5, width: 450.5, height: 170.5), "half points on a 2x display")
        #expect(CaptureGeometry.sourceRect(for: dictionary, on: plain) == PointRect(x: 1475, y: 1260, width: 900, height: 340))
        // Origin at the display's own top-left corner, whatever its global origin.
        #expect(CaptureGeometry.sourceRect(for: PixelRect(x: 0, y: 0, width: 2, height: 2), on: retina).x == 0)

        #expect(CaptureGeometry.isInside(PixelRect(x: 0, y: 0, width: 3024, height: 1964), display: retina))
        #expect(!CaptureGeometry.isInside(PixelRect(x: 1, y: 0, width: 3024, height: 1964), display: retina))
        #expect(!CaptureGeometry.isInside(PixelRect(x: -1, y: 0, width: 10, height: 10), display: retina))
        #expect(!CaptureGeometry.isInside(PixelRect(x: 0, y: 0, width: 0, height: 10), display: retina))
    }
}

/// Vision's normalized boxes to frame pixels (T18, T24).
@Suite struct VisionGeometryTests {
    @Test func flipsIntoTopLeftFramePixels() throws {
        // Vision's origin is bottom-left: a box in the upper half of the image
        // has the larger y.
        let box = try #require(VisionGeometry.frameBox(NormalizedQuad(x: 0.25, y: 0.5, width: 0.5, height: 0.25),
                                                       frameWidth: 400, frameHeight: 200, upscale: 1))
        #expect(box == PixelBox(left: 100, top: 50, right: 300, bottom: 100))
        #expect(!box.isDegenerate && box.width == 200 && box.height == 50)
    }

    @Test func twiceEnlargedFramesMapBackExactly() throws {
        let quads = [NormalizedQuad(x: 0.1234567, y: 0.7654321, width: 0.3333333, height: 0.0123457),
                     NormalizedQuad(x: 0, y: 0, width: 1, height: 1),
                     NormalizedQuad(x: 0.999, y: 0.001, width: 0.0005, height: 0.0007)]
        for quad in quads {
            for (width, height) in [(900, 340), (2400, 720), (1, 1), (1201, 333)] {
                let direct = VisionGeometry.frameBox(quad, frameWidth: width, frameHeight: height, upscale: 1)
                let enlarged = VisionGeometry.frameBox(quad, frameWidth: width, frameHeight: height, upscale: 2)
                #expect(direct != nil && direct == enlarged, "\(quad) on \(width)x\(height)")
            }
        }
        #expect(VisionGeometry.upscale(pixelsPerPoint: 1) == 2 && VisionGeometry.upscale(pixelsPerPoint: 2) == 1)
        #expect(VisionGeometry.upscale(pixelsPerPoint: 1.25) == 2 && VisionGeometry.upscale(pixelsPerPoint: 3) == 1)
    }

    @Test func aRotatedQuadBecomesItsHull() throws {
        let quad = NormalizedQuad(topLeft: UnitPoint(x: 0.1, y: 0.9), topRight: UnitPoint(x: 0.5, y: 0.95),
                                  bottomRight: UnitPoint(x: 0.55, y: 0.8), bottomLeft: UnitPoint(x: 0.15, y: 0.75))
        let box = try #require(VisionGeometry.frameBox(quad, frameWidth: 1000, frameHeight: 100, upscale: 1))
        #expect(abs(box.left - 100) < 1e-9 && abs(box.right - 550) < 1e-9)
        #expect(abs(box.top - 5) < 1e-9 && abs(box.bottom - 25) < 1e-9)
    }

    @Test func clipsToTheFrameAndKeepsADegenerateBox() throws {
        let past = try #require(VisionGeometry.frameBox(NormalizedQuad(x: -0.1, y: -0.05, width: 1.3, height: 1.15),
                                                        frameWidth: 900, frameHeight: 340, upscale: 2))
        #expect(past == PixelBox(left: 0, top: 0, right: 900, bottom: 340))
        // Wholly outside: clipped to an edge, degenerate, sent for CoreHost's
        // decoder to drop (ExtractRecognition's rule), never refused there.
        let outside = try #require(VisionGeometry.frameBox(NormalizedQuad(x: 1.1, y: 0.2, width: 0.1, height: 0.1),
                                                           frameWidth: 900, frameHeight: 340, upscale: 1))
        #expect(outside.left == 900 && outside.right == 900 && outside.isDegenerate && outside.isFinite)
        let nan = NormalizedQuad(topLeft: UnitPoint(x: .nan, y: 1), topRight: UnitPoint(x: 1, y: 1),
                                 bottomRight: UnitPoint(x: 1, y: 0), bottomLeft: UnitPoint(x: 0, y: 0))
        #expect(VisionGeometry.frameBox(nan, frameWidth: 10, frameHeight: 10, upscale: 1) == nil)
        #expect(VisionGeometry.frameBox(NormalizedQuad(x: 0, y: 0, width: .infinity, height: 1), frameWidth: 10,
                                        frameHeight: 10, upscale: 1) == nil)
        #expect(VisionGeometry.frameBox(NormalizedQuad(x: 0, y: 0, width: 1, height: 1), frameWidth: 0,
                                        frameHeight: 10, upscale: 1) == nil)
    }

    @Test func recognizedLinesBecomeWhatCoreHostAccepts() throws {
        let fingerprint = FrameFingerprint(value: 0xa1)
        let good = PixelBox(left: 10, top: 20, right: 50, bottom: 40)
        let degenerate = PixelBox(left: 60, top: 20, right: 60, bottom: 40)
        let nan = PixelBox(left: .nan, top: 20, right: 70, bottom: 40)
        let dots = String(repeating: ".", count: 257)
        let longest = String(repeating: "w", count: 256)
        let lines = [
            RecognizedLine(text: "bank river", confidence: 0.5, box: nil, words: [
                RecognizedWord(text: "bank", utf16Range: 0..<4, box: good),
                RecognizedWord(text: "river", utf16Range: 5..<10, box: degenerate),
            ]),
            RecognizedLine(text: "lost", confidence: 1, box: nil,
                           words: [RecognizedWord(text: "lost", utf16Range: 0..<4, box: nan)]),
            RecognizedLine(text: "no words", confidence: 1, box: nil, words: []),
            RecognizedLine(text: "loud", confidence: 1.7, box: nil,
                           words: [RecognizedWord(text: "loud", utf16Range: 0..<4, box: good)]),
            RecognizedLine(text: "odd", confidence: .nan, box: nil,
                           words: [RecognizedWord(text: "odd", utf16Range: 0..<3, box: good)]),
            // A run without whitespace longer than CoreHost's 256 UTF-16 units
            // per word is left out; 256 itself goes.
            RecognizedLine(text: "Index " + dots + " 12", confidence: 1, box: nil, words: [
                RecognizedWord(text: "Index", utf16Range: 0..<5, box: good),
                RecognizedWord(text: dots, utf16Range: 6..<263, box: good),
                RecognizedWord(text: "12", utf16Range: 264..<266, box: good),
            ]),
            RecognizedLine(text: dots, confidence: 1, box: nil,
                           words: [RecognizedWord(text: dots, utf16Range: 0..<257, box: good)]),
            RecognizedLine(text: longest, confidence: 1, box: nil,
                           words: [RecognizedWord(text: longest, utf16Range: 0..<256, box: good)]),
        ]
        let frame = ObservationFrame(fingerprint: fingerprint, recognized: lines)
        #expect(frame.lines.map(\.text) == ["bank river", "loud", "odd", "Index " + dots + " 12", longest])
        // Required before indexing: a trap would leave a crash report.
        try #require(frame.lines.count == 5)
        #expect(frame.lines[0].words.map(\.text) == ["bank", "river"], "a degenerate box is kept for CoreHost to drop")
        #expect(frame.lines[3].words.map(\.text) == ["Index", "12"], "the over-long run is left out, the line kept")
        #expect(frame.lines.map(\.confidence) == [0.5, 1, 0, 1, 1])
        let source = ObservationsSource(display: ObservedDisplay(id: 1, width: 100, height: 100),
                                        captureBounds: PixelRect(x: 0, y: 0, width: 100, height: 100),
                                        cursor: PixelPoint(x: 1, y: 1), observations: [frame])
        #expect(ObservationLimits.problem(source) == nil, "what the recognizer read is inside CoreHost's limits")
        #expect(frame.json["fingerprint"] == "00000000000000a1")
    }
}

/// Words of a recognized line, with UTF-16 ranges as CoreHost counts.
@Suite struct WordSplitterTests {
    static func split(_ line: String) -> [String] { WordSplitter.words(in: line).map(\.text) }

    @Test func whitespaceRunsSeparateAndTrim() {
        let words = WordSplitter.words(in: "  He  sat\ton the bank. ")
        #expect(words.map(\.text) == ["He", "sat", "on", "the", "bank."])
        #expect(words.map(\.utf16) == [2..<4, 6..<9, 10..<12, 13..<16, 17..<22])
        #expect(WordSplitter.words(in: "").isEmpty && WordSplitter.words(in: " \t \n ").isEmpty)
        #expect(Self.split("a\u{00A0}b\u{3000}c\u{2009}d") == ["a", "b", "c", "d"], "Unicode White_Space separates")
        #expect(Self.split("a\u{200B}b") == ["a\u{200B}b"], "a zero-width space is not whitespace")
    }

    @Test func surrogatePairsAndCombiningMarksStayWhole() throws {
        let emoji = WordSplitter.words(in: "a 😀b c")
        #expect(emoji.map(\.text) == ["a", "😀b", "c"] && emoji.map(\.utf16) == [0..<1, 2..<5, 6..<7])
        let combining = WordSplitter.words(in: "cafe\u{301} ok")
        #expect(combining.map(\.text) == ["cafe\u{301}", "ok"] && combining.map(\.utf16) == [0..<5, 6..<8])
        // A combining mark after a space starts a word, as .NET's whitespace split does.
        #expect(Self.split("a \u{301}b") == ["a", "\u{301}b"])
    }

    @Test func rangesMapBackToTheLine() throws {
        for line in ["  He  sat\ton the bank. ", "a 😀b c", "cafe\u{301} ok", "日本語 テキスト"] {
            for word in WordSplitter.words(in: line) {
                let range = try #require(WordSplitter.stringRange(word.utf16, in: line), "\(word) in \(line)")
                #expect(String(line[range]) == word.text)
            }
        }
        #expect(WordSplitter.stringRange(0..<9, in: "short") == nil)
        #expect(WordSplitter.stringRange(3..<4, in: "a 😀b") == nil, "inside a surrogate pair")
        #expect(WordSplitter.stringRange(-1..<1, in: "ab") == nil)
    }
}
