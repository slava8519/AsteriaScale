import AsteriaScaleKit
import CryptoKit
import Foundation

/// The frame fingerprint (docs/MACOS.md T27): the SHA-256 of the frame's
/// bytes in the PixelBuffer layout, its first 8 bytes read as a
/// little-endian UInt64, exactly as CoreHost's OcrTextRules.ComputeFrameFingerprint.
/// CryptoKit stays here, behind Kit's FrameFingerprinting seam, so Kit keeps
/// to Foundation and Darwin.
public struct SHA256FrameFingerprinter: FrameFingerprinting {
    public init() {}

    public func fingerprint(of pixels: Data) -> FrameFingerprint {
        let digest = Array(SHA256.hash(data: pixels))
        // A SHA-256 digest always has 32 bytes, so the fallback is unreachable;
        // it exists because a trap would leave a crash report.
        return FrameFingerprint(digest: digest) ?? FrameFingerprint(value: 0)
    }
}
