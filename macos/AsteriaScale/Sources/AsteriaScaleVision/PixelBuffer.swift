import AsteriaScaleKit
import CoreGraphics
import Foundation

/// CGImage to and from the frame layout the fingerprint is defined on
/// (tests/AsteriaScale.Tests.MacOS/CoreHost/frame-fingerprint-v1.txt): dense
/// BGRA rows, top to bottom, 4 bytes per pixel, no row padding. The app's
/// SHA-256 and CoreHost's ComputeFrameFingerprint must read the same bytes,
/// or a cached frame never matches.
public enum PixelBuffer {
    /// B, G, R, A in memory: 32-bit little-endian words with alpha first.
    public static let bitmapInfo = CGBitmapInfo(
        rawValue: CGImageAlphaInfo.premultipliedFirst.rawValue | CGBitmapInfo.byteOrder32Little.rawValue)

    static let sRGB = CGColorSpace(name: CGColorSpace.sRGB) ?? CGColorSpaceCreateDeviceRGB()

    /// The image's pixels in the frame layout. An image that already holds
    /// 8-bit BGRA (a screen capture does) is copied row by row as it is, so
    /// no colour conversion or premultiplication touches the bytes; any
    /// other image is drawn into a BGRA bitmap of its own RGB colour space
    /// (sRGB for another model). Nil when the image cannot be read.
    public static func bgra(of image: CGImage) -> (width: Int, height: Int, bytes: Data)? {
        let width = image.width, height = image.height
        guard width >= 1, height >= 1 else { return nil }
        let dense = width * Frame.bytesPerPixel
        let alphaFirst: [CGImageAlphaInfo] = [.premultipliedFirst, .first, .noneSkipFirst]
        if image.bitsPerComponent == 8, image.bitsPerPixel == 32, image.byteOrderInfo == .order32Little,
           image.pixelFormatInfo == .packed, alphaFirst.contains(image.alphaInfo), image.bytesPerRow >= dense,
           let source = image.dataProvider?.data as Data?, source.count >= image.bytesPerRow * (height - 1) + dense {
            if image.bytesPerRow == dense { return (width, height, Data(source.prefix(dense * height))) }
            var bytes = Data(capacity: dense * height)
            for row in 0..<height {
                let start = source.startIndex + row * image.bytesPerRow
                bytes.append(source[start..<(start + dense)])
            }
            return (width, height, bytes)
        }
        let space = image.colorSpace.flatMap { $0.model == .rgb ? $0 : nil } ?? sRGB
        guard let context = CGContext(data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: dense,
                                      space: space, bitmapInfo: bitmapInfo.rawValue) else { return nil }
        context.draw(image, in: CGRect(x: 0, y: 0, width: width, height: height))
        return bytes(of: context)
    }

    /// A frame of `image` (see `bgra(of:)`).
    public static func frame(of image: CGImage, pixelsPerPoint: Double) throws(RecognitionFailure) -> Frame {
        guard let pixels = bgra(of: image) else { throw .invalidFrame("the image's pixels cannot be read") }
        return try Frame(width: pixels.width, height: pixels.height, pixels: pixels.bytes, pixelsPerPoint: pixelsPerPoint)
    }

    /// The frame's bytes as an sRGB BGRA image, without copying or converting them.
    public static func image(of frame: Frame) -> CGImage? {
        guard let provider = CGDataProvider(data: frame.pixels as CFData) else { return nil }
        return CGImage(width: frame.width, height: frame.height, bitsPerComponent: 8, bitsPerPixel: 32,
                       bytesPerRow: frame.width * Frame.bytesPerPixel, space: sRGB, bitmapInfo: bitmapInfo,
                       provider: provider, decode: nil, shouldInterpolate: false, intent: .defaultIntent)
    }

    /// The image the recognizer hands to Vision for `frame`, and how many
    /// times it is enlarged: the frame's own bytes, enlarged twice below 1.5
    /// pixels per point (docs/MACOS.md T24, VisionGeometry.upscale). One
    /// function for both, so the self-test can check the size Vision was
    /// really given and the suite can check the enlargement without Vision.
    public static func visionImage(of frame: Frame) -> (image: CGImage, upscale: Int)? {
        guard let image = image(of: frame) else { return nil }
        let upscale = VisionGeometry.upscale(pixelsPerPoint: frame.pixelsPerPoint)
        guard upscale > 1 else { return (image, 1) }
        guard let enlarged = upscaled(image, by: upscale) else { return nil }
        return (enlarged, upscale)
    }

    /// `image` enlarged `factor` times with high-quality interpolation, for
    /// Vision only (docs/MACOS.md T24); boxes go back to frame pixels.
    public static func upscaled(_ image: CGImage, by factor: Int) -> CGImage? {
        guard factor >= 1 else { return nil }
        let width = image.width * factor, height = image.height * factor
        guard let context = CGContext(data: nil, width: width, height: height, bitsPerComponent: 8,
                                      bytesPerRow: width * Frame.bytesPerPixel, space: image.colorSpace ?? sRGB,
                                      bitmapInfo: bitmapInfo.rawValue) else { return nil }
        context.interpolationQuality = .high
        context.draw(image, in: CGRect(x: 0, y: 0, width: width, height: height))
        return context.makeImage()
    }

    /// A bitmap context's pixels in the frame layout. Its memory holds the
    /// top row first, however user space is oriented.
    static func bytes(of context: CGContext) -> (width: Int, height: Int, bytes: Data)? {
        guard let base = context.data else { return nil }
        let width = context.width, height = context.height, stride = context.bytesPerRow
        let dense = width * Frame.bytesPerPixel
        guard stride >= dense else { return nil }
        var bytes = Data(capacity: dense * height)
        for row in 0..<height {
            bytes.append(base.advanced(by: row * stride).assumingMemoryBound(to: UInt8.self), count: dense)
        }
        return (width, height, bytes)
    }
}
