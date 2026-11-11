import Foundation
import Testing
@testable import AsteriaScaleKit

/// The capture gate (roadmap 20.3 DoD 8; docs/MACOS.md T22): every branch,
/// on the traits of the processes it has to tell apart.
@Suite struct CaptureGateTests {
    static let app = "/Applications/Asteria Scale.app/Contents/MacOS/AsteriaScale"
    static let own = AppIdentity.bundleIdentifier
    static let launched: [String: String] = ["__CFBundleIdentifier": own, "XPC_SERVICE_NAME": "application.\(own).1.2"]

    static func decide(_ arguments: [String] = [app], parent: Int32 = 1, environment: [String: String] = launched,
                       bundleId: String = own) -> CaptureGate.Decision {
        CaptureGate.decide(arguments: arguments, parentPid: parent, environment: environment, bundleId: bundleId)
    }

    @Test func onlyALaunchServicesLaunchOfThisBundleCaptures() {
        // Finder or open(1): launchd is the parent and the variable names this app.
        // A permit is made in Capture.swift only (its initializer is
        // fileprivate): the tests see it through decisions alone.
        #expect(Self.decide().isOn && Self.decide().reason == nil)
        #expect(Self.decide() == Self.decide(), "every permit is the same proof")
        // LaunchServices may add arguments of its own.
        #expect(Self.decide([Self.app, "-psn_0_12345"]).isOn)
    }

    @Test func theSelfTestNeverCaptures() {
        #expect(Self.decide([Self.app, "--self-test"]).reason == .selfTest)
        // Even with every other trait of a LaunchServices launch, and wherever the flag is.
        #expect(Self.decide([Self.app, "--ranker-models", "/m", "--self-test"]).reason == .selfTest)
        // The executable's own path is not an argument.
        #expect(Self.decide(["--self-test"]).isOn)
    }

    @Test func aDirectExecNeverCaptures() {
        // build-app.sh --self-test or --run-isolated: the script is the parent.
        #expect(Self.decide(parent: 4242) == .off(.parentIsNotLaunchd(parentPid: 4242)))
        #expect(Self.decide(parent: 0).reason == .parentIsNotLaunchd(parentPid: 0))
        // The parent decides first: a terminal's variable does not matter then.
        #expect(Self.decide(parent: 77, environment: ["__CFBundleIdentifier": "com.apple.Terminal"]).reason
                == .parentIsNotLaunchd(parentPid: 77))
    }

    @Test func withoutTheVariableNothingCaptures() {
        #expect(Self.decide(environment: [:]).reason == .noLaunchServicesVariable)
        #expect(Self.decide(environment: ["__CFBundleIdentifier": ""]).reason == .noLaunchServicesVariable)
        #expect(Self.decide(environment: ["XPC_SERVICE_NAME": "application.\(Self.own).1.2"]).reason == .noLaunchServicesVariable)
    }

    /// A process a terminal or an agent's shell started and then left
    /// behind: launchd adopts it (parent 1), but it inherited that host's
    /// variable.
    @Test(arguments: ["com.apple.Terminal", "com.anthropic.claudefordesktop", "com.googlecode.iterm2",
                      AppIdentity.bundleIdentifier.uppercased(), AppIdentity.bundleIdentifier + ".helper"])
    func anOrphanWithAForeignVariableNeverCaptures(_ variable: String) {
        #expect(Self.decide(environment: ["__CFBundleIdentifier": variable]) == .off(.foreignLaunchServicesVariable(variable)))
    }

    @Test func anotherBundleNeverCaptures() {
        #expect(Self.decide(bundleId: "").reason == .foreignBundle(""))
        #expect(Self.decide(bundleId: "local.asteriascale.app").reason == .foreignBundle("local.asteriascale.app"))
    }

    /// The self-test's report asks for a reason only; it agrees with
    /// `decide` on every branch.
    @Test func theReasonAgreesWithTheDecision() {
        let cases: [([String], Int32, [String: String], String)] = [
            ([Self.app], 1, Self.launched, Self.own), ([Self.app, "--self-test"], 1, Self.launched, Self.own),
            ([Self.app], 99, Self.launched, Self.own), ([Self.app], 1, [:], Self.own),
            ([Self.app], 1, ["__CFBundleIdentifier": "com.apple.Terminal"], Self.own), ([Self.app], 1, Self.launched, ""),
        ]
        for (arguments, parent, environment, bundleId) in cases {
            let decision = CaptureGate.decide(arguments: arguments, parentPid: parent, environment: environment, bundleId: bundleId)
            let reason = CaptureGate.offReason(arguments: arguments, parentPid: parent, environment: environment, bundleId: bundleId)
            #expect(decision.reason == reason && decision.isOn == (reason == nil), "\(arguments) \(parent) \(environment)")
            #expect(!decision.description.isEmpty && !(reason?.description.isEmpty ?? false))
        }
        #expect(CaptureGate.selfTestArgument == "--self-test" && CaptureGate.launchServicesVariable == "__CFBundleIdentifier")
    }
}

@Suite struct CaptureFailureTests {
    /// -3801, the capture framework's "user declined", is the permission
    /// message; the same code from another domain is not.
    @Test func userDeclinedIsThePermissionMessage() {
        let declined = CaptureFailure.from(domain: ["com.apple.Screen", "CaptureKit.SCStreamErrorDomain"].joined(), code: -3801,
                                           isCaptureDomain: true)
        #expect(declined == .permissionDenied && declined.code == "screen_capture_denied")
        #expect(declined.russian == RU.captureNoPermission && declined.russian.contains(RU.menuAllowScreenRecording))
        #expect(CaptureFailure.from(domain: "NSOSStatusErrorDomain", code: -3801, isCaptureDomain: false)
                == .failed(domain: "NSOSStatusErrorDomain", code: -3801))
        #expect(CaptureFailure.from(domain: "x", code: -3802, isCaptureDomain: true) == .failed(domain: "x", code: -3802))
    }

    @Test func everyFailureIsRussianWithItsCode() {
        let failures: [CaptureFailure] = [.permissionDenied, .ownAppMissing, .displayMissing(7),
                                          .failed(domain: "d", code: 3), .invalidFrame("x"), .cancelled]
        for failure in failures {
            let error = failure.coreError
            #expect(error.code == failure.code && error.message == failure.russian && error.detail == failure.description)
            #expect(failure.russian.range(of: #"\p{Cyrillic}"#, options: .regularExpression) != nil, "\(failure)")
            #expect(!failure.russian.lowercased().contains(hostWord))
        }
        let mismatch = RecognitionFailure.frameMismatch(frame: PixelSize(width: 899, height: 340),
                                                        bounds: PixelSize(width: 900, height: 340)).coreError
        #expect(mismatch.code == "frame_mismatch" && mismatch.message == RU.recognitionFrameMismatch)
    }

    /// The app's own failures put their Russian text on the overlay; a
    /// stale frame shows nothing.
    @Test func ownFailuresAreShownAsTheirText() {
        let failures: [CoreError] = [
            CaptureFailure.permissionDenied.coreError, CaptureFailure.ownAppMissing.coreError,
            CaptureFailure.displayMissing(2).coreError, CaptureFailure.failed(domain: "d", code: 1).coreError,
            CaptureFailure.invalidFrame("x").coreError,
            RecognitionFailure.frameMismatch(frame: PixelSize(width: 1, height: 1), bounds: PixelSize(width: 2, height: 2)).coreError,
            RecognitionFailure.invalidFrame("x").coreError, RecognitionFailure.recognizer(domain: "v", code: 9).coreError,
        ]
        #expect(Set(failures.map(\.code)) == HoverReport.ownCodes)
        for error in failures {
            let report = HoverReport.after(.rejected(error), shownPrimary: false)
            #expect(report.overlay == .message(error.message) && report.diagnosticNote?.contains(error.code) == true)
        }
        let stale = CoreError(code: "stale_frame", message: RU.errorText("stale_frame"))
        #expect(HoverReport.after(.staleFrame(stale), shownPrimary: false).overlay == .hide)
    }
}

/// The menu's recognition line and whether the cursor may be tracked
/// (DoD 20.3 items 6-8).
@Suite struct RecognitionStatusTests {
    static func resolve(gateOn: Bool = true, permitted: Bool = true, refused: Bool = false,
                        _ warmUp: VisionWarmUp) -> RecognitionStatus {
        RecognitionStatus.resolve(gateOn: gateOn, permitted: permitted, captureRefused: refused, warmUp: warmUp)
    }

    @Test func theGateThenThePermissionThenTheWarmUp() {
        for warmUp in [VisionWarmUp.notStarted, .running, .ready, .failed("x")] {
            for refused in [false, true] {
                #expect(Self.resolve(gateOn: false, refused: refused, warmUp) == .disabledForCheckRun)
                #expect(Self.resolve(gateOn: false, permitted: false, refused: refused, warmUp) == .disabledForCheckRun)
                #expect(Self.resolve(permitted: false, refused: refused, warmUp) == .needsPermission(requested: false))
            }
        }
        #expect(Self.resolve(.notStarted) == .idle)
        #expect(Self.resolve(.running) == .preparing)
        #expect(Self.resolve(.ready) == .ready)
        #expect(Self.resolve(.failed("причина")) == .unavailable("причина"))
    }

    /// DoD 20.3 item 7: a capture refused while the preflight still says yes
    /// (-3801) is treated as no permission, whatever the warm-up: the line
    /// and «Разрешить запись экрана…» come back and tracking stops.
    @Test func aRefusedCaptureNeedsThePermissionAgain() {
        for warmUp in [VisionWarmUp.notStarted, .running, .ready, .failed("x")] {
            let status = Self.resolve(refused: true, warmUp)
            #expect(status == .needsPermission(requested: false), "\(warmUp)")
            #expect(status.offersPermissionRequest && !status.allowsTracking && status.menuLine == RU.recognitionNeedsPermission)
        }
        // Which ends count: a refusal from the pipeline's preflight or from
        // the capture framework (-3801), both screen_capture_denied.
        let declined = CaptureFailure.from(domain: "d", code: CaptureFailure.userDeclinedCode, isCaptureDomain: true)
        #expect(HoverOutcome.rejected(declined.coreError).refusesCapture)
        #expect(HoverOutcome.rejected(CaptureFailure.permissionDenied.coreError).refusesCapture)
        let others: [HoverOutcome] = [
            .completed, .empty, .failed, .superseded, .cancelled,
            .staleFrame(CoreError(code: "stale_frame", message: RU.errorText("stale_frame"))),
            .rejected(CaptureFailure.failed(domain: "d", code: -3801).coreError),
            .rejected(CaptureFailure.ownAppMissing.coreError), .rejected(CoreError.notRunning),
        ]
        for outcome in others { #expect(!outcome.refusesCapture, "\(outcome)") }
    }

    @Test func linesItemAndTracking() {
        let lines: [(RecognitionStatus, String?, Bool, Bool)] = [
            (.disabledForCheckRun, "Распознавание отключено: проверочный запуск", false, false),
            (.needsPermission(requested: false), "Распознавание: нужен доступ к записи экрана", true, false),
            (.needsPermission(requested: true), RU.recognitionRestartAfterGrant, true, false),
            (.idle, nil, false, false),
            (.preparing, "Распознавание: подготовка…", false, false),
            (.ready, "Распознавание: готово", false, true),
            (.unavailable(RU.recognitionWarmUpFailed("recognizer_failed")),
             "Распознавание недоступно: прогрев Vision не удался (recognizer_failed)", false, false),
        ]
        for (status, line, item, tracking) in lines {
            #expect(status.menuLine == line && status.offersPermissionRequest == item && status.allowsTracking == tracking,
                    "\(status)")
        }
        #expect(RU.menuAllowScreenRecording == "Разрешить запись экрана…")
    }
}

/// Placing a small frame on a canvas byte by byte (the self-test's fixture
/// frame source), without fonts.
@Suite struct FramePlacementTests {
    static func frame(width: Int, height: Int, seed: UInt8) throws -> Frame {
        var bytes = Data()
        for index in 0..<(width * height) {
            bytes.append(contentsOf: [seed, UInt8(index % 256), UInt8((index / 256) % 256), 255])
        }
        return try Frame(width: width, height: height, pixels: bytes, pixelsPerPoint: 2)
    }

    @Test func rowsLandWhereTheOriginSays() throws {
        let small = try Self.frame(width: 3, height: 2, seed: 9)
        let placed = try small.placed(onCanvasWidth: 5, height: 4, at: PixelPoint(x: 1, y: 2), background: 255)
        #expect(placed.size == PixelSize(width: 5, height: 4) && placed.pixelsPerPoint == 2)
        let bytes = [UInt8](placed.pixels)
        try #require(bytes.count == 5 * 4 * 4)
        for y in 0..<4 {
            for x in 0..<5 {
                let offset = (y * 5 + x) * 4
                let inside = (1..<4).contains(x) && (2..<4).contains(y)
                let expected: [UInt8] = inside
                    ? [9, UInt8(((y - 2) * 3 + (x - 1)) % 256), 0, 255] : [255, 255, 255, 255]
                #expect(Array(bytes[offset..<(offset + 4)]) == expected, "pixel \(x), \(y)")
            }
        }
    }

    @Test func aFrameThatDoesNotFitIsRefused() throws {
        let small = try Self.frame(width: 3, height: 2, seed: 1)
        for origin in [PixelPoint(x: 3, y: 0), PixelPoint(x: 0, y: 3), PixelPoint(x: -1, y: 0), PixelPoint(x: Int.max, y: 0)] {
            #expect(throws: RecognitionFailure.self) { try small.placed(onCanvasWidth: 5, height: 4, at: origin) }
        }
        #expect(throws: RecognitionFailure.self) { try small.placed(onCanvasWidth: 0, height: 4, at: PixelPoint(x: 0, y: 0)) }
        #expect(throws: RecognitionFailure.self) {
            try small.placed(onCanvasWidth: Frame.maxSide + 1, height: 4, at: PixelPoint(x: 0, y: 0))
        }
        // Exactly at the far corner it fits.
        #expect(try small.placed(onCanvasWidth: 5, height: 4, at: PixelPoint(x: 2, y: 2)).size == PixelSize(width: 5, height: 4))
    }

    @Test func theOriginPutsTheTargetOnThePointOrAsNearAsItFits() {
        let canvas = PixelSize(width: 900, height: 340)
        let size = PixelSize(width: 658, height: 236)
        // Fits: the target lands on the point exactly.
        #expect(Frame.origin(placing: size, target: PixelPoint(x: 252, y: 62), at: PixelPoint(x: 450, y: 170), canvas: canvas)
                == PixelPoint(x: 198, y: 104))
        #expect(Frame.origin(placing: size, target: PixelPoint(x: 252, y: 62), at: PixelPoint(x: 300, y: 100), canvas: canvas)
                == PixelPoint(x: 48, y: 38))
        // Moved the least to fit: against the near and the far edges.
        #expect(Frame.origin(placing: size, target: PixelPoint(x: 252, y: 62), at: PixelPoint(x: 10, y: 5), canvas: canvas)
                == PixelPoint(x: 0, y: 0))
        #expect(Frame.origin(placing: size, target: PixelPoint(x: 0, y: 0), at: PixelPoint(x: 899, y: 339), canvas: canvas)
                == PixelPoint(x: 242, y: 104))
        // Larger than the canvas, or extreme values: no origin, no trap.
        #expect(Frame.origin(placing: PixelSize(width: 901, height: 1), target: PixelPoint(x: 0, y: 0),
                             at: PixelPoint(x: 0, y: 0), canvas: canvas) == nil)
        #expect(Frame.origin(placing: size, target: PixelPoint(x: Int.min, y: 0), at: PixelPoint(x: Int.max, y: 0),
                             canvas: canvas) == nil)
        #expect(Frame.origin(placing: PixelSize(width: 0, height: 1), target: PixelPoint(x: 0, y: 0),
                             at: PixelPoint(x: 0, y: 0), canvas: canvas) == nil)
    }
}
