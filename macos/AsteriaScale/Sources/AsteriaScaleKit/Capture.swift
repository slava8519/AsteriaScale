import Foundation

// The screen capture as the hover pipeline sees it (roadmap 20.3 DoD 7 and
// 8; docs/MACOS.md T21, T22): whether this process may capture at all, the
// seams the pipeline takes injected (frames, the permission, the game), the
// failures with their Russian texts, and the recognition line of the menu.
// The capture itself lives in one file of the app (its only caller of the
// system's screen capture and permission APIs); everything here is pure, so
// every branch is tested without a window server and without a privacy
// prompt.

/// Whether this process may capture the screen (docs/MACOS.md T22). Decided
/// once at launch and never revisited: capture is on only for a launch
/// through LaunchServices (Finder, open(1)) of this very bundle, never for a
/// self-test, a direct exec from a script or a terminal, or a process that a
/// script left behind. Any doubt turns it off, so a sign that fails leaves
/// the owner without capture, never an agent with it.
public enum CaptureGate {
    /// Proof that `decide` turned capture on. Only this file can make one
    /// (the initializer is fileprivate, so the compiler holds the rest of Kit
    /// and the app to that), and SourceScanTests pins its one construction
    /// to `decide`'s last line; the app's screen source takes one in its
    /// initializer, so nothing but a decision that said yes can construct it.
    public struct Permit: Sendable, Hashable {
        fileprivate init() {}
    }

    /// Why capture is off, in English for app.log and the self-test.
    public enum Reason: Sendable, Hashable, CustomStringConvertible {
        /// The scripted self-test never captures (it reads frames rendered in memory).
        case selfTest
        /// Not started by launchd: a script, a terminal or another app is the parent.
        case parentIsNotLaunchd(parentPid: Int32)
        /// LaunchServices names the app it launched in this variable; a
        /// direct exec inherits whatever its parent had, or nothing.
        case noLaunchServicesVariable
        /// The variable names another app: the process was started from a
        /// terminal or another app and then left behind (its parent died,
        /// so launchd adopted it).
        case foreignLaunchServicesVariable(String)
        /// This bundle is not the app M13 names (a renamed copy, a bare binary).
        case foreignBundle(String)

        public var description: String {
            switch self {
            case .selfTest: return "the self-test never captures"
            case .parentIsNotLaunchd(let parent):
                return "the parent is pid \(parent), not launchd (pid 1): not a LaunchServices launch"
            case .noLaunchServicesVariable:
                return "\(CaptureGate.launchServicesVariable) is not set: not a LaunchServices launch"
            case .foreignLaunchServicesVariable(let value):
                return "\(CaptureGate.launchServicesVariable) is \(value), not \(AppIdentity.bundleIdentifier)"
            case .foreignBundle(let value):
                return "the bundle id is \(value.isEmpty ? "missing" : value), not \(AppIdentity.bundleIdentifier)"
            }
        }
    }

    public enum Decision: Sendable, Hashable, CustomStringConvertible {
        case on(Permit)
        case off(Reason)

        public var isOn: Bool {
            if case .on = self { return true }
            return false
        }

        public var reason: Reason? {
            if case .off(let reason) = self { return reason }
            return nil
        }

        public var description: String {
            switch self {
            case .on: return "on (launched by LaunchServices as \(AppIdentity.bundleIdentifier))"
            case .off(let reason): return "off: \(reason)"
            }
        }
    }

    public static let selfTestArgument = "--self-test"
    /// What LaunchServices sets in the environment of an app it launches
    /// (undocumented; observed for apps started from Finder, while a shell
    /// inherits its terminal's or its host app's).
    public static let launchServicesVariable = "__CFBundleIdentifier"

    /// The decision for a process with these traits.
    ///
    /// - Parameters:
    ///   - arguments: the command line, the executable first.
    ///   - parentPid: getppid().
    ///   - environment: the process environment.
    ///   - bundleId: this bundle's identifier (Bundle.main), "" when it has none.
    public static func decide(arguments: [String], parentPid: Int32, environment: [String: String],
                              bundleId: String) -> Decision {
        if let reason = offReason(arguments: arguments, parentPid: parentPid, environment: environment,
                                  bundleId: bundleId) {
            return .off(reason)
        }
        return .on(Permit())
    }

    /// The same rules, as only a reason: nil where `decide` would turn
    /// capture on. The self-test reports the decision for its own process
    /// with this (capture.gate), so it never holds a permit.
    public static func offReason(arguments: [String], parentPid: Int32, environment: [String: String],
                                 bundleId: String) -> Reason? {
        if arguments.dropFirst().contains(selfTestArgument) { return .selfTest }
        if parentPid != 1 { return .parentIsNotLaunchd(parentPid: parentPid) }
        guard let variable = environment[launchServicesVariable], !variable.isEmpty else {
            return .noLaunchServicesVariable
        }
        guard variable == AppIdentity.bundleIdentifier else { return .foreignLaunchServicesVariable(variable) }
        guard bundleId == AppIdentity.bundleIdentifier else { return .foreignBundle(bundleId) }
        return nil
    }
}

/// Why a frame could not be captured. `russian` may reach the overlay (rule
/// 9); `description` is English, for app.log.
public enum CaptureFailure: Error, Sendable, Hashable, CustomStringConvertible {
    /// No Screen Recording permission: the preflight said no, or the capture
    /// framework answered "user declined" (-3801).
    case permissionDenied
    /// This app is missing from the capture's list of applications, so its
    /// overlay could not be excluded: an error, never a capture with it.
    case ownAppMissing
    /// The display under the cursor is not in the capture's list of displays.
    case displayMissing(UInt32)
    /// The capture failed with this error domain and code.
    case failed(domain: String, code: Int)
    /// A frame came back that the app cannot read.
    case invalidFrame(String)
    case cancelled

    /// The capture framework's "user declined" code (its stream error
    /// domain): the permission was refused or revoked.
    public static let userDeclinedCode = -3801

    /// A thrown capture error by its domain and code; `isCaptureDomain`
    /// says whether the domain is the capture framework's stream error domain.
    public static func from(domain: String, code: Int, isCaptureDomain: Bool) -> CaptureFailure {
        if isCaptureDomain, code == userDeclinedCode { return .permissionDenied }
        return .failed(domain: domain, code: code)
    }

    public var code: String {
        switch self {
        case .permissionDenied: return "screen_capture_denied"
        case .ownAppMissing: return "capture_self_missing"
        case .displayMissing: return "capture_display_missing"
        case .failed: return "capture_failed"
        case .invalidFrame: return "capture_invalid"
        case .cancelled: return "cancelled"
        }
    }

    public var russian: String {
        switch self {
        case .permissionDenied: return RU.captureNoPermission
        case .ownAppMissing: return RU.captureSelfMissing
        case .displayMissing: return RU.captureDisplayMissing
        case .failed(let domain, let number): return RU.captureFailed("\(domain) \(number)")
        case .invalidFrame: return RU.captureFailed(code)
        case .cancelled: return RU.errorText("cancelled")
        }
    }

    public var description: String {
        switch self {
        case .permissionDenied: return "no Screen Recording permission"
        case .ownAppMissing: return "this app is not in the capture's application list; its windows cannot be excluded"
        case .displayMissing(let id): return "display \(id) is not in the capture's display list"
        case .failed(let domain, let number): return "the capture failed: \(domain) \(number)"
        case .invalidFrame(let reason): return "unreadable frame: \(reason)"
        case .cancelled: return "cancelled"
        }
    }

    /// As the hover's error terminal.
    public var coreError: CoreError { CoreError(code: code, message: russian, detail: description) }
}

extension RecognitionFailure {
    /// As the hover's error terminal.
    public var coreError: CoreError { CoreError(code: code, message: russian, detail: description) }
}

/// Where a hover's frames come from: the screen capture in the app, a frame
/// rendered in memory in the self-test, a stub in the tests.
public protocol FrameSource: Sendable {
    /// Captures `bounds` (whole pixels of `display`, origin at its top-left
    /// corner). The frame should be the bounds' size; the pipeline refuses
    /// one that is not (frame_mismatch) rather than rescale its boxes.
    func capture(_ bounds: PixelRect, on display: DisplayDescription) async throws(CaptureFailure) -> Frame
}

/// Whether the screen may be captured now. The app's screen source answers
/// with the system's preflight, which never prompts; the hover pipeline asks
/// before anything else, so without the permission its frame source is
/// never called.
public protocol CapturePermission: Sendable {
    func isGranted() -> Bool
}

/// The game a hover belongs to, named by the app (docs/MACOS.md T29): the
/// executable of the window's owner under the cursor, or the .exe a Wine
/// process runs (GameIdentity). The app's screen source reads it, so only a
/// process whose capture is on resolves a game; without capture there is no
/// hover over the screen at all. NoGameResolver and the self-test's
/// synthetic resolver stand in for it where nothing may be read.
public protocol GameResolver: Sendable {
    func game(at point: GlobalPoint) async -> HoverGame?
}

/// Resolves no game: hovers then carry none, Core applies no speaker gender
/// (as on Windows without a game) and the speaker editor says it cannot
/// name the game.
public struct NoGameResolver: GameResolver {
    public init() {}
    public func game(at point: GlobalPoint) async -> HoverGame? { nil }
}

/// The Vision warm-up of the app (docs/MACOS.md T25): started when hover
/// translation is turned on while capture is on, once per process.
public enum VisionWarmUp: Sendable, Hashable {
    case notStarted
    case running
    case ready
    /// The Russian reason with its code.
    case failed(String)
}

/// The recognition line of the menu and whether the cursor may be tracked.
public enum RecognitionStatus: Sendable, Hashable {
    /// The capture gate is off (a self-test or a direct exec, T22).
    case disabledForCheckRun
    /// Capture is on but Screen Recording is not granted, or a tracked hover's
    /// capture was refused in this run although the preflight said yes
    /// (-3801, DoD 20.3 item 7). `requested`: the menu item asked for it in
    /// this run (a grant is assumed to reach the running app only after a
    /// relaunch; the owner checks it).
    case needsPermission(requested: Bool)
    /// Capture is on and permitted; hover translation has not been turned on
    /// yet, so Vision has not been warmed.
    case idle
    case preparing
    case ready
    /// The warm-up failed: its Russian reason.
    case unavailable(String)

    /// - Parameters:
    ///   - gateOn: the capture decision of this process (CaptureGate).
    ///   - permitted: the Screen Recording preflight, read through the screen source.
    ///   - captureRefused: a tracked hover ended with the capture refused
    ///     (`HoverOutcome.refusesCapture`) in this run. The preflight cannot
    ///     clear it, since it said yes before that refusal too, so the app
    ///     keeps it until a relaunch: it ranks with a missing permission,
    ///     below the gate and above the warm-up.
    ///   - warmUp: the app's Vision warm-up.
    public static func resolve(gateOn: Bool, permitted: Bool, captureRefused: Bool,
                               warmUp: VisionWarmUp) -> RecognitionStatus {
        guard gateOn else { return .disabledForCheckRun }
        guard permitted, !captureRefused else { return .needsPermission(requested: false) }
        switch warmUp {
        case .notStarted: return .idle
        case .running: return .preparing
        case .ready: return .ready
        case .failed(let reason): return .unavailable(reason)
        }
    }

    /// The menu line; nil hides it.
    public var menuLine: String? {
        switch self {
        case .disabledForCheckRun: return RU.recognitionDisabledCheckRun
        case .needsPermission(let requested): return requested ? RU.recognitionRestartAfterGrant : RU.recognitionNeedsPermission
        case .idle: return nil
        case .preparing: return RU.recognitionPreparing
        case .ready: return RU.recognitionReady
        case .unavailable(let reason): return RU.recognitionUnavailable(reason)
        }
    }

    /// Whether the menu offers «Разрешить запись экрана…».
    public var offersPermissionRequest: Bool {
        if case .needsPermission = self { return true }
        return false
    }

    /// No hover before Vision is ready (DoD 20.3 item 6) and none without
    /// capture or its permission (item 7).
    public var allowsTracking: Bool { self == .ready }
}

extension HoverOutcome {
    /// The hover ended because the screen capture was refused: the
    /// pipeline's preflight said no, or the capture framework answered
    /// "user declined" (-3801, CaptureFailure.permissionDenied). A tracked
    /// hover that ends so takes the menu back to the permission line and
    /// item (RecognitionStatus.resolve's `captureRefused`).
    public var refusesCapture: Bool {
        if case .rejected(let error) = self { return error.code == CaptureFailure.permissionDenied.code }
        return false
    }
}

extension Frame {
    /// `self` drawn onto a canvas of `width` x `height` filled with
    /// `background` (every byte, so 255 is opaque white), its top-left
    /// corner at `origin`. The self-test's fixture frame source makes a
    /// frame of a plan's bounds this way; nothing is scaled, and a frame
    /// that does not fit wholly is refused.
    public func placed(onCanvasWidth width: Int, height: Int, at origin: PixelPoint,
                       background: UInt8 = 255) throws(RecognitionFailure) -> Frame {
        guard (1...Frame.maxSide).contains(width), (1...Frame.maxSide).contains(height) else {
            throw .invalidFrame("a canvas must be 1..\(Frame.maxSide) pixels on each side, got \(width)x\(height)")
        }
        guard origin.x >= 0, origin.y >= 0, origin.x <= width - self.width, origin.y <= height - self.height else {
            throw .invalidFrame("a \(size) frame at \(origin) does not fit a \(width)x\(height) canvas")
        }
        let rowBytes = self.width * Frame.bytesPerPixel
        let canvasRow = width * Frame.bytesPerPixel
        var canvas = Data(repeating: background, count: canvasRow * height)
        for row in 0..<self.height {
            let from = pixels.startIndex + row * rowBytes
            let to = canvas.startIndex + (origin.y + row) * canvasRow + origin.x * Frame.bytesPerPixel
            canvas.replaceSubrange(to..<(to + rowBytes), with: pixels[from..<(from + rowBytes)])
        }
        return try Frame(width: width, height: height, pixels: canvas, pixelsPerPoint: pixelsPerPoint)
    }

    /// Where a `size` frame goes on a canvas so that its point `target`
    /// lands on the canvas point `at`, moved the least needed to fit wholly;
    /// nil when it cannot fit at all.
    public static func origin(placing size: PixelSize, target: PixelPoint, at point: PixelPoint,
                              canvas: PixelSize) -> PixelPoint? {
        // Bounded first, so no subtraction below can overflow (a trap leaves
        // a crash report): every value here is a frame or display pixel.
        let limit = Frame.maxSide
        guard size.width >= 1, size.height >= 1, size.width <= canvas.width, size.height <= canvas.height,
              canvas.width <= limit, canvas.height <= limit,
              [target.x, target.y, point.x, point.y].allSatisfy({ (-limit...limit).contains($0) }) else {
            return nil
        }
        let x = min(max(point.x - target.x, 0), canvas.width - size.width)
        let y = min(max(point.y - target.y, 0), canvas.height - size.height)
        return PixelPoint(x: x, y: y)
    }
}
