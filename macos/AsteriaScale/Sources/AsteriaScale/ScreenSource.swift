import AppKit
import AsteriaScaleKit
import AsteriaScaleVision
import CoreGraphics
import CoreVideo
import Darwin
import ScreenCaptureKit

/// The screen capture (roadmap 20.3 DoD 7 and 8; docs/MACOS.md T21, T22):
/// the only file of the package that names ScreenCaptureKit, the Screen
/// Recording preflight and request, and the window-list API
/// (SourceScanTests holds every other file to that).
///
/// An instance exists only in a process whose CaptureGate decision is on: its
/// initializer takes the gate's permit, and AppDelegate builds the one
/// instance at launch, in the one place it checks that decision. So in a
/// self-test, a direct exec or any agent run nothing here except the static
/// window-record read below can run, and that read asks for no permission.
/// SourceScanTests holds the file to that shape: besides its imports it
/// declares only this class, with one initializer, no nested type and no
/// static or class member but `ownWindowRecord`, whose whole text it pins
/// (so its body reads that one record and nothing else), and no extension
/// header in any file names the class and no typealias stands for it.
///
/// Nothing from ScreenCaptureKit is called until the preflight says the
/// permission is granted: `capture` checks it first, and only the menu item
/// «Разрешить запись экрана…» calls `requestPermission`, the one call that may
/// show the system's prompt.
final class ScreenSource: FrameSource, CapturePermission, @unchecked Sendable {
    private let lock = NSLock()
    /// The shareable content (displays and applications), kept until the
    /// screens change: asking for it costs a round trip to the window server
    /// on every hover otherwise.
    private var content: SCShareableContent?
    private var screensObserver: (any NSObjectProtocol)?

    init(permit: CaptureGate.Permit) {
        screensObserver = NotificationCenter.default.addObserver(
            forName: NSApplication.didChangeScreenParametersNotification, object: nil, queue: nil
        ) { [weak self] _ in self?.forgetContent() }
    }

    deinit {
        if let screensObserver { NotificationCenter.default.removeObserver(screensObserver) }
    }

    // MARK: Permission

    /// The Screen Recording preflight: never prompts.
    func isGranted() -> Bool { CGPreflightScreenCaptureAccess() }

    /// «Разрешить запись экрана…»: the one call that may show the system's
    /// prompt (macOS shows it only once), and the Privacy pane whenever the
    /// request did not grant the permission, since the call cannot tell
    /// whether its prompt was shown (docs/MACOS.md T22), or `afterRefusal`:
    /// a tracked hover's capture was refused although the preflight said
    /// yes, and the request may then say yes without showing anything. The
    /// menu then asks for a relaunch, assuming a grant reaches a running app
    /// only after one; whether macOS 27 applies it sooner, and what the
    /// request answers after such a refusal, are the owner's checks.
    @MainActor
    func requestPermission(afterRefusal: Bool) -> Bool {
        let granted = CGRequestScreenCaptureAccess()
        if !granted || afterRefusal,
           let pane = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture") {
            NSWorkspace.shared.open(pane)
        }
        return granted
    }

    // MARK: Capture

    /// One frame of `bounds` on `display`, without this app's windows: the
    /// filter excludes every application with this app's bundle id, so the
    /// overlay never reaches Vision (its sharingType .none alone reportedly
    /// does not keep it out of this capture on macOS 15.4+). The source
    /// rectangle is in the display's points (CaptureGeometry.sourceRect) and
    /// the output size in its pixels, so the frame is the bounds 1:1; the
    /// pipeline refuses any other size.
    func capture(_ bounds: PixelRect, on display: DisplayDescription) async throws(CaptureFailure) -> Frame {
        guard isGranted() else { throw .permissionDenied }
        let content = try await shareableContent()
        guard let screen = content.displays.first(where: { $0.displayID == display.id }) else {
            throw .displayMissing(display.id)
        }
        let own = content.applications.filter { $0.bundleIdentifier == AppIdentity.bundleIdentifier }
        guard !own.isEmpty else { throw .ownAppMissing }
        let filter = SCContentFilter(display: screen, excludingApplications: own, exceptingWindows: [])
        let configuration = SCStreamConfiguration()
        let source = CaptureGeometry.sourceRect(for: bounds, on: display)
        configuration.sourceRect = CGRect(x: source.x, y: source.y, width: source.width, height: source.height)
        configuration.width = bounds.width
        configuration.height = bounds.height
        configuration.showsCursor = false
        configuration.pixelFormat = kCVPixelFormatType_32BGRA
        let image: CGImage
        do {
            image = try await SCScreenshotManager.captureImage(contentFilter: filter, configuration: configuration)
        } catch {
            throw failure(error)
        }
        if Task.isCancelled { throw .cancelled }
        do {
            return try PixelBuffer.frame(of: image, pixelsPerPoint: display.pixelsPerPointX)
        } catch {
            throw .invalidFrame(error.description)
        }
    }

    private func shareableContent() async throws(CaptureFailure) -> SCShareableContent {
        if let cached = cachedContent() { return cached }
        let fresh: SCShareableContent
        do {
            // Every window, not only those on screen: the header does not say
            // whether the application list follows the listed windows, and
            // this app's own must be found even with the overlay ordered out.
            fresh = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: false)
        } catch {
            throw failure(error)
        }
        remember(fresh)
        return fresh
    }

    private func remember(_ fresh: SCShareableContent) {
        lock.lock()
        content = fresh
        lock.unlock()
    }

    private func cachedContent() -> SCShareableContent? {
        lock.lock()
        defer { lock.unlock() }
        return content
    }

    private func forgetContent() {
        lock.lock()
        content = nil
        lock.unlock()
    }

    /// An instance member, as everything here but `ownWindowRecord` is
    /// (SourceScanTests pins that).
    private func failure(_ error: any Error) -> CaptureFailure {
        if error is CancellationError { return .cancelled }
        let error = error as NSError
        return CaptureFailure.from(domain: error.domain, code: error.code, isCaptureDomain: error.domain == SCStreamErrorDomain)
    }

    // MARK: The window server's record of an own window

    /// How the window server lists one of this app's own windows: its layer,
    /// its sharing state and whether it is on screen (20.2's
    /// overlay.windowServer self-test check). It reads that one window's
    /// record only, needs no instance (the self-test has none), calls
    /// nothing from ScreenCaptureKit and asks for no permission.
    static func ownWindowRecord(_ windowNumber: Int) -> (layer: Int?, sharingState: Int?, onscreen: Bool?) {
        guard let number = CGWindowID(exactly: windowNumber) else { return (nil, nil, nil) }
        let list = CGWindowListCopyWindowInfo([.optionIncludingWindow], number) as? [[String: Any]]
        let window = list?.first
        return (window?[kCGWindowLayer as String] as? Int, window?[kCGWindowSharingState as String] as? Int,
                window?[kCGWindowIsOnscreen as String] as? Bool)
    }
}
