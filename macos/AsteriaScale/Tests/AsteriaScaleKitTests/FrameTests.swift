import CoreGraphics
import Foundation
import Testing
@testable import AsteriaScaleKit
@testable import AsteriaScaleVision

/// The frame fingerprint and the frame layout (docs/MACOS.md T27). No test
/// here runs the recognizer or renders text: CGImages are made from bytes.
@Suite struct FrameFingerprintTests {
    struct Pattern {
        let name: String
        let count: Int
        let rule: String
        let fingerprint: String

        /// Byte i of the input, as the file's header defines each rule.
        func bytes() -> Data {
            var data = Data(count: count)
            data.withUnsafeMutableBytes { raw in
                guard let base = raw.bindMemory(to: UInt8.self).baseAddress else { return }
                for index in 0..<count {
                    switch rule {
                    case "ramp": base[index] = UInt8(index % 256)
                    case "opaque": base[index] = index % 4 == 3 ? 255 : 0
                    case "mix": base[index] = UInt8((index * 31 + 7) % 251)
                    default: base[index] = 0
                    }
                }
            }
            return data
        }
    }

    /// tests/AsteriaScale.Tests.MacOS/CoreHost/frame-fingerprint-v1.txt, read
    /// as the C# CoreHost check reads it.
    static func patterns() throws -> [Pattern] {
        let url = Repo.coreHostTests.appendingPathComponent("frame-fingerprint-v1.txt")
        let text = try String(contentsOf: url, encoding: .utf8)
        #expect(!text.contains("\r"), "frame-fingerprint-v1.txt must use LF line ends")
        return try text.split(separator: "\n").filter { !$0.isEmpty && !$0.hasPrefix("#") }.map { line in
            let parts = line.split(separator: " ", omittingEmptySubsequences: false).map(String.init)
            #expect(parts.count == 4, "malformed line: \(line)")
            let count = try #require(parts.count == 4 ? Int(parts[1]) : nil, "malformed count: \(line)")
            #expect(["zero", "ramp", "opaque", "mix"].contains(parts[2]), "unknown rule: \(line)")
            return Pattern(name: parts[0], count: count, rule: parts[2], fingerprint: parts[3])
        }
    }

    @Test func everyPinnedPatternHasItsFingerprint() throws {
        let patterns = try Self.patterns()
        #expect(patterns.count >= 5)
        #expect(patterns.contains { $0.name == "empty" && $0.count == 0 && $0.fingerprint == "141cfc9842c4b0e3" })
        let fingerprinter = SHA256FrameFingerprinter()
        for pattern in patterns {
            let fingerprint = fingerprinter.fingerprint(of: pattern.bytes())
            #expect(fingerprint.hex == pattern.fingerprint, "\(pattern.name): \(fingerprint.hex)")
            #expect(FrameFingerprint(hex: pattern.fingerprint) == fingerprint, "\(pattern.name) round-trips")
        }
    }

    /// The wire form without CryptoKit: the first 8 digest bytes read
    /// little-endian, written as x16, so the digits are those bytes reversed.
    @Test func wireFormIsTheLittleEndianPrefixAsX16() {
        // SHA-256 of nothing starts e3b0c44298fc1c14.
        let digest: [UInt8] = [0xe3, 0xb0, 0xc4, 0x42, 0x98, 0xfc, 0x1c, 0x14, 0x9a, 0xfb]
        let fingerprint = FrameFingerprint(digest: digest)
        #expect(fingerprint?.value == 0x141c_fc98_42c4_b0e3 && fingerprint?.hex == "141cfc9842c4b0e3")
        #expect(FrameFingerprint(digest: digest.prefix(7)) == nil)
        #expect(FrameFingerprint(value: 0xa1).hex == "00000000000000a1")
        #expect(FrameFingerprint(value: .max).hex == "ffffffffffffffff")
        for bad in ["", "00000000000000A1", "00000000000000a", "00000000000000a1f", "0x000000000000a1", "-000000000000001",
                    "00000000000000g1", "+00000000000000a"] {
            #expect(FrameFingerprint(hex: bad) == nil, "\(bad)")
        }
        // The seam fingerprints a frame's bytes as they are.
        let bytes = Data([1, 2, 3, 4])
        let frame = try? Frame(width: 1, height: 1, pixels: bytes, pixelsPerPoint: 2)
        #expect(frame.map(SHA256FrameFingerprinter().fingerprint(of:)) == SHA256FrameFingerprinter().fingerprint(of: bytes))
    }
}

@Suite struct PixelBufferTests {
    /// Opaque pixels whose four bytes all differ from every other pixel's.
    static func pixel(_ x: Int, _ y: Int) -> [UInt8] {
        [UInt8((x * 7 + y * 3) % 256), UInt8((x * 11 + y * 5 + 1) % 256), UInt8((x * 13 + y * 17 + 2) % 256), 255]
    }

    static func image(width: Int, height: Int, stride: Int, order: (_ bgra: [UInt8]) -> [UInt8],
                      info: CGBitmapInfo) -> CGImage? {
        var data = Data(repeating: 0xEE, count: stride * height)
        for y in 0..<height {
            for x in 0..<width {
                let offset = y * stride + x * 4
                data.replaceSubrange(offset..<(offset + 4), with: order(pixel(x, y)))
            }
        }
        guard let provider = CGDataProvider(data: data as CFData),
              let space = CGColorSpace(name: CGColorSpace.sRGB) else { return nil }
        return CGImage(width: width, height: height, bitsPerComponent: 8, bitsPerPixel: 32, bytesPerRow: stride,
                       space: space, bitmapInfo: info, provider: provider, decode: nil, shouldInterpolate: false,
                       intent: .defaultIntent)
    }

    /// Dense BGRA rows, top row first.
    static func expected(width: Int, height: Int) -> Data {
        var data = Data()
        for y in 0..<height {
            for x in 0..<width { data.append(contentsOf: pixel(x, y)) }
        }
        return data
    }

    @Test func bgraImagesAreCopiedTopRowFirstWithoutPadding() throws {
        for stride in [5 * 4, 5 * 4 + 12, 64] {
            let image = try #require(Self.image(width: 5, height: 3, stride: stride, order: { $0 },
                                                info: PixelBuffer.bitmapInfo))
            let pixels = try #require(PixelBuffer.bgra(of: image))
            #expect(pixels.width == 5 && pixels.height == 3)
            #expect(pixels.bytes == Self.expected(width: 5, height: 3), "bytesPerRow \(stride)")
            let frame = try PixelBuffer.frame(of: image, pixelsPerPoint: 2)
            #expect(frame.pixels == Self.expected(width: 5, height: 3) && frame.size == PixelSize(width: 5, height: 3))
        }
    }

    @Test func otherLayoutsAreDrawnIntoBGRA() throws {
        // RGBA in memory (big-endian words, alpha last), opaque, same colour space.
        let rgba = CGBitmapInfo(rawValue: CGImageAlphaInfo.premultipliedLast.rawValue | CGBitmapInfo.byteOrder32Big.rawValue)
        let image = try #require(Self.image(width: 4, height: 2, stride: 4 * 4 + 8,
                                            order: { [$0[2], $0[1], $0[0], $0[3]] }, info: rgba))
        let pixels = try #require(PixelBuffer.bgra(of: image))
        #expect(pixels.bytes == Self.expected(width: 4, height: 2))
    }

    @Test func aFrameRoundTripsThroughItsImage() throws {
        let frame = try Frame(width: 3, height: 2, pixels: Self.expected(width: 3, height: 2), pixelsPerPoint: 1)
        let image = try #require(PixelBuffer.image(of: frame))
        #expect(image.width == 3 && image.height == 2)
        #expect(PixelBuffer.bgra(of: image)?.bytes == frame.pixels)
    }

    /// A frame at 1 pixel per point reaches Vision enlarged twice (T24): a
    /// 16x12 frame, black in its top-left 8x6 quarter and white elsewhere,
    /// becomes a 32x24 image with the black quarter at the top left. Only
    /// block interiors are sampled: high-quality interpolation rings at the
    /// edges.
    @Test func lowDensityFramesReachVisionEnlargedTwice() throws {
        let width = 16, height = 12
        var bytes = Data()
        for y in 0..<height {
            for x in 0..<width {
                bytes.append(contentsOf: x < width / 2 && y < height / 2 ? [0, 0, 0, 255] : [255, 255, 255, 255])
            }
        }
        let low = try Frame(width: width, height: height, pixels: bytes, pixelsPerPoint: 1)
        let seen = try #require(PixelBuffer.visionImage(of: low))
        // Required before any pixel is read: an index past the bytes would
        // trap, and a trap leaves a crash report in the owner's
        // DiagnosticReports (AGENTS.md).
        try #require(seen.upscale == 2 && seen.image.width == 32 && seen.image.height == 24,
                     "Vision would get \(seen.image.width)x\(seen.image.height), enlarged \(seen.upscale)x")
        let pixels = try #require(PixelBuffer.bgra(of: seen.image))
        try #require(pixels.bytes.count == 32 * 24 * Frame.bytesPerPixel)
        func pixel(_ x: Int, _ y: Int) -> [UInt8] {
            let offset = (y * pixels.width + x) * Frame.bytesPerPixel
            guard offset >= 0, offset + 4 <= pixels.bytes.count else { return [] }
            return Array(pixels.bytes[(pixels.bytes.startIndex + offset)..<(pixels.bytes.startIndex + offset + 4)])
        }
        func isBlack(_ value: [UInt8]) -> Bool {
            value.count == 4 && value[0] <= 16 && value[1] <= 16 && value[2] <= 16 && value[3] == 255
        }
        func isWhite(_ value: [UInt8]) -> Bool {
            value.count == 4 && value[0] >= 239 && value[1] >= 239 && value[2] >= 239 && value[3] == 255
        }
        #expect(isBlack(pixel(4, 3)) && isBlack(pixel(10, 7)), "the black quarter, doubled, top left")
        #expect(isWhite(pixel(27, 3)) && isWhite(pixel(4, 20)) && isWhite(pixel(27, 20)), "white elsewhere, not flipped")

        // At 2x and above Vision gets the frame's own bytes.
        let retina = try Frame(width: width, height: height, pixels: bytes, pixelsPerPoint: 2)
        let same = try #require(PixelBuffer.visionImage(of: retina))
        #expect(same.upscale == 1 && PixelBuffer.bgra(of: same.image)?.bytes == bytes)
        #expect(PixelBuffer.upscaled(same.image, by: 0) == nil)
    }

    @Test func framesAreCheckedAndMatchTheirBoundsExactly() throws {
        #expect(throws: RecognitionFailure.self) { _ = try Frame(width: 0, height: 1, pixels: Data(), pixelsPerPoint: 1) }
        #expect(throws: RecognitionFailure.self) {
            _ = try Frame(width: 2, height: 1, pixels: Data(count: 4), pixelsPerPoint: 1)
        }
        #expect(throws: RecognitionFailure.self) {
            _ = try Frame(width: 1, height: 1, pixels: Data(count: 4), pixelsPerPoint: .nan)
        }
        let frame = try Frame(width: 900, height: 340, pixels: Data(count: 900 * 340 * 4), pixelsPerPoint: 2)
        #expect(frame.matches(PixelRect(x: 1475, y: 1260, width: 900, height: 340)))
        // Not even one pixel off: a frame is never rescaled to its bounds (T24).
        #expect(!frame.matches(PixelRect(x: 0, y: 0, width: 901, height: 340)))
        #expect(!frame.matches(PixelRect(x: 0, y: 0, width: 900, height: 339)))
        #expect(!frame.matches(PixelRect(x: 0, y: 0, width: 450, height: 170)))
        let mismatch = RecognitionFailure.frameMismatch(frame: frame.size, bounds: PixelSize(width: 450, height: 170))
        #expect(mismatch.code == "frame_mismatch" && mismatch.russian == RU.recognitionFrameMismatch)
        #expect(mismatch.description.contains("900x340") && mismatch.description.contains("450x170"))
    }
}
