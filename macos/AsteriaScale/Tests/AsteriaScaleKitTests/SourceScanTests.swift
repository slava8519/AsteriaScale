import Foundation
import Testing
@testable import AsteriaScaleKit

/// What the package's sources may name (roadmap 20.3 DoD 6, 8 and 10;
/// docs/MACOS.md T22, T26, T37). Every scan is a pure function over (path,
/// lines): the folder walks feed it the package's files, so a new file is
/// covered without a list to update, and `theScansFlagEveryForbiddenSpelling`
/// feeds it planted lines, so a scan that stops seeing a spelling fails. The
/// names and planted spellings are assembled from pieces, as `hostWord` is,
/// so this file does not match itself.
@Suite struct SourceScanTests {
    typealias Source = (path: String, lines: [Substring])

    static let package = Repo.root.appendingPathComponent("macos/AsteriaScale")

    /// Every .swift file under `folder` (relative to the package), skipping
    /// SwiftPM's build folders, as (package-relative path, lines).
    static func swiftSources(_ folder: String) throws -> [Source] {
        let root = package.appendingPathComponent(folder)
        let enumerator = try #require(FileManager.default.enumerator(atPath: root.path), "no \(folder)")
        var sources: [Source] = []
        while let relative = enumerator.nextObject() as? String {
            if [".build", ".swiftpm"].contains((relative as NSString).lastPathComponent) {
                enumerator.skipDescendants()
                continue
            }
            guard (relative as NSString).pathExtension == "swift" else { continue }
            let text = try String(contentsOf: root.appendingPathComponent(relative), encoding: .utf8)
            sources.append(("\(folder)/\(relative)", text.split(separator: "\n", omittingEmptySubsequences: false)))
        }
        return sources.sorted { $0.path < $1.path }
    }

    // MARK: Screen capture and permissions

    /// The screen capture and permission names DoD 8 lists. They may appear
    /// only in the files, and as many lines there, as the allow-list says:
    /// the capture commit moves them all into Sources/AsteriaScale/ScreenSource.swift.
    static let captureGroup = [
        ["Screen", "CaptureKit"], ["SC", "ShareableContent"], ["SC", "ScreenshotManager"], ["SC", "ContentFilter"],
        ["CGPreflight", "ScreenCaptureAccess"], ["CGRequest", "ScreenCaptureAccess"],
        ["CGWindowList", "CopyWindowInfo"], ["CGWindowList", "CreateImage"],
    ].map { $0.joined() }

    /// Names that may appear nowhere, ever: the global input monitors, event
    /// taps and trust and access requests that raise the «Мониторинг ввода»
    /// and «Универсальный доступ» prompts (T37; DoD 8 lists the first five,
    /// CoreGraphics' CGEvent.h says the two event-access requests may
    /// prompt), and the older capture calls that raise «Запись экрана», which
    /// the app never uses (its only capture is the one ScreenSource.swift
    /// will hold, T21).
    static let neverGroup = [
        ["addGlobal", "MonitorForEvents"], ["tap", "Create"], ["CGEventTap", "Create"],
        ["AXIsProcess", "TrustedWithOptions"], ["IOHID", "RequestAccess"],
        ["CGRequest", "ListenEventAccess"], ["CGRequest", "PostEventAccess"], ["IOHID", "ManagerOpen"],
        ["CGDisplay", "CreateImage"], ["CGDisplay", "Stream"], ["AVCapture", "ScreenInput"],
    ].map { $0.joined() }

    /// One line, and only that one: 20.2's overlay.windowServer self-test
    /// check reads its own panel's window record (layer, sharing state) with
    /// the window list. That query asks for no permission and raises no
    /// prompt; the capture commit moves it into ScreenSource.swift, which then
    /// becomes the only file here (DoD 8, T22).
    static let captureAllowList: [String: [String: Int]] = [
        ["CGWindowList", "CopyWindowInfo"].joined(): ["Sources/AsteriaScale/SelfTest.swift": 1],
    ]

    /// The capture and permission issues of `sources`: a never-group name
    /// anywhere, a capture-group name outside its allow-list, and, with
    /// `exactAllowance`, an allowed file that names it on more or fewer
    /// lines than the allow-list says.
    static func captureIssues(_ sources: [Source], exactAllowance: Bool) -> [String] {
        var issues: [String] = []
        var allowedLines: [String: [String: Int]] = [:]
        for source in sources {
            for (number, line) in source.lines.enumerated() {
                for name in captureGroup where line.contains(name) {
                    if captureAllowList[name]?[source.path] != nil {
                        allowedLines[name, default: [:]][source.path, default: 0] += 1
                    } else {
                        issues.append("\(name) in \(source.path):\(number + 1)")
                    }
                }
                for name in neverGroup where line.contains(name) {
                    issues.append("\(name) in \(source.path):\(number + 1), which no source may name")
                }
            }
        }
        if exactAllowance {
            for (name, files) in captureAllowList {
                for (path, allowed) in files where (allowedLines[name]?[path] ?? 0) != allowed {
                    issues.append("\(name) on \(allowedLines[name]?[path] ?? 0) lines of \(path), the allow-list says \(allowed)")
                }
            }
        }
        return issues
    }

    @Test func captureAndPermissionNamesOnlyWhereAllowed() throws {
        let sources = try Self.swiftSources("Sources") + Self.swiftSources("Tests")
        #expect(sources.count >= 30)
        for issue in Self.captureIssues(sources, exactAllowance: true) { Issue.record("\(issue)") }
    }

    // MARK: Imports

    /// The modules a line imports: every `import` statement on it (`;`
    /// separates statements), after any attributes (`@preconcurrency`,
    /// `@testable`, `@_exported`, `@_spi(…)`, …) and access modifier, with an
    /// optional declaration kind (`import struct M.T`); a module path counts
    /// as its first component (`Carbon` of `Carbon.HIToolbox`). A comment is
    /// not a statement.
    static func importStatement() throws -> Regex<AnyRegexOutput> {
        let attributes = #"(?:(?:@[A-Za-z_][A-Za-z0-9_]*(?:\([^)]*\))?|open|public|package|internal|fileprivate|private)\s+)*"#
        let kind = #"(?:(?:class|struct|enum|func|protocol|var|let|typealias)\s+)?"#
        return try Regex(#"(?:^|;)\s*"# + attributes + ["im", "port"].joined() + #"\s+"# + kind
            + #"([A-Za-z_][A-Za-z0-9_]*)"#)
    }

    static func importedModules(_ line: Substring, matching statement: Regex<AnyRegexOutput>) -> [String] {
        line.matches(of: statement).compactMap { $0.output[1].substring.map(String.init) }
    }

    /// Each import of `sources` outside `allowed` or inside `never`.
    static func importIssues(_ sources: [Source], allowed: Set<String>, never: Set<String> = []) throws -> [String] {
        let statement = try importStatement()
        var issues: [String] = []
        for source in sources {
            for (number, line) in source.lines.enumerated() {
                for module in importedModules(line, matching: statement) where !allowed.contains(module) || never.contains(module) {
                    issues.append("\(source.path):\(number + 1) imports \(module)")
                }
            }
        }
        return issues
    }

    /// Kit stays Foundation and Darwin only (headless and testable).
    static let kitImports: Set<String> = ["Foundation", "Darwin"]
    /// DoD 6's list for the Vision target, and what it may never import.
    static let visionImports: Set<String> = ["AsteriaScaleKit", "Foundation", "Vision", "CoreGraphics", "CoreText",
                                             "ImageIO", "CryptoKit"]
    static let visionNever: Set<String> = ["AppKit", "SwiftUI", "Carbon", ["Screen", "CaptureKit"].joined()]
    /// What a test may import: no Vision, no CoreText or AppKit to render
    /// text with, no capture.
    static let testImports: Set<String> = ["Testing", "Foundation", "Darwin", "CoreGraphics", "Carbon",
                                           "AsteriaScaleKit", "AsteriaScaleVision"]

    /// Kit stays Foundation and Darwin only; the Vision target stays off the
    /// window server and the screen capture (DoD 6).
    @Test func targetsImportOnlyWhatTheyMay() throws {
        let kit = try Self.swiftSources("Sources/AsteriaScaleKit")
        #expect(kit.count >= 10)
        for issue in try Self.importIssues(kit, allowed: Self.kitImports) { Issue.record("\(issue); Kit is Foundation and Darwin only") }
        let vision = try Self.swiftSources("Sources/AsteriaScaleVision")
        let statement = try Self.importStatement()
        let visionModules = Set(vision.flatMap { source in
            source.lines.flatMap { Self.importedModules($0, matching: statement) }
        })
        #expect(visionModules.isSuperset(of: ["Vision", "CoreText", "CryptoKit"]), "\(visionModules)")
        for issue in try Self.importIssues(vision, allowed: Self.visionImports, never: Self.visionNever) {
            Issue.record("\(issue)")
        }
    }

    // MARK: No Vision and no text in the suite

    /// What no test may name (T26): the recognizer and its request types (VN
    /// plus a capital is any Vision type; the Swift API's text request and
    /// every request handler are covered by their suffixes), the renderer
    /// and the fixture that drive them, and the CoreText, CoreGraphics and
    /// HIToolbox calls that make or draw text (fonts may write caches into
    /// the run's home, which test-swift.sh refuses).
    static let testNames = [
        ["VisionText", "Recognizer"], ["Recognize", "TextRequest"], ["Request", "Handler"],
        ["Fixture", "Renderer"], ["Vision", "Fixture"],
        ["CT", "Font"], ["CT", "Line"], ["CT", "Frame"], ["CT", "Run"], ["CT", "Typesetter"],
        ["CG", "Font"], ["show", "Glyphs"], ["HITheme", "DrawText"],
    ].map { $0.joined() }

    /// The issues of test sources: an import outside `testImports`, an
    /// `import` of Vision, CoreText or AppKit anywhere on a line (even after
    /// a comment), a Vision type, or a name in `testNames`.
    static func testIssues(_ sources: [Source]) throws -> [String] {
        var issues = try importIssues(sources, allowed: testImports)
        // Simple word boundaries: Unicode's would join a module, a dot and
        // a type into one word and hide the type.
        let anywhere = try Regex(#"\b"# + ["im", "port"].joined() + #"\s+(?:(?:class|struct|enum|func|protocol|var|let|typealias)\s+)?(?:"#
            + [["Vi", "sion"], ["Core", "Text"], ["App", "Kit"]].map { $0.joined() }.joined(separator: "|") + #")(?:\.|\s|;|$)"#)
            .wordBoundaryKind(.simple)
        let visionType = try Regex(#"\b"# + ["V", "N"].joined() + #"[A-Z]"#).wordBoundaryKind(.simple)
        for source in sources {
            for (number, line) in source.lines.enumerated() {
                if line.contains(anywhere) { issues.append("\(source.path):\(number + 1) imports a module no test may") }
                if line.contains(visionType) { issues.append("\(source.path):\(number + 1) names a Vision type") }
                for name in testNames where line.contains(name) {
                    issues.append("\(name) in \(source.path):\(number + 1)")
                }
            }
        }
        return issues
    }

    @Test func noTestRunsVisionOrRendersText() throws {
        let sources = try Self.swiftSources("Tests/AsteriaScaleKitTests")
        #expect(sources.count >= 10)
        for issue in try Self.testIssues(sources) { Issue.record("\(issue)") }
    }

    // MARK: The scans themselves

    /// Planted lines, never files: each forbidden spelling must be flagged
    /// and each harmless one must not, so a scan that loses a name or an
    /// import form fails here. DoD 8's names are spelled again, from other
    /// pieces, so a name dropped from a group fails too.
    @Test func theScansFlagEveryForbiddenSpelling() throws {
        func lines(_ text: String) -> [Substring] { [Substring(text)] }
        let kitFile = "Sources/AsteriaScaleKit/Planted.swift"
        let selfTest = "Sources/AsteriaScale/SelfTest.swift"

        let dod8Capture = [
            ["ScreenCapture", "Kit"], ["SCShareable", "Content"], ["SCScreenshot", "Manager"], ["SCContent", "Filter"],
            ["CGPreflightScreen", "CaptureAccess"], ["CGRequestScreen", "CaptureAccess"],
            ["CGWindowListCopy", "WindowInfo"], ["CGWindowListCreate", "Image"],
        ].map { $0.joined() }
        let neverSpellings = [
            ["NSEvent.addGlobalMonitor", "ForEvents(matching: .mouseMoved)"], ["CGEvent.tap", "Create(tap: .cgSessionEventTap"],
            ["CGEventTap", "Create("], ["AXIsProcessTrusted", "WithOptions(nil)"], ["IOHIDRequest", "Access(kIOHIDRequestType)"],
            ["CGRequestListen", "EventAccess()"], ["CGRequestPost", "EventAccess()"], ["IOHIDManager", "Open(manager, 0)"],
            ["CGDisplayCreate", "Image(id)"], ["CGDisplay", "StreamCreate("], ["AVCaptureScreen", "Input(displayID: id)"],
        ].map { $0.joined() }
        #expect(Set(Self.captureGroup) == Set(dod8Capture), "DoD 8's capture names, no more and no fewer")
        for name in dod8Capture {
            #expect(Self.captureIssues([(kitFile, lines("_ = \(name)"))], exactAllowance: false).count == 1, "\(name)")
        }
        for spelling in neverSpellings {
            #expect(!Self.captureIssues([(selfTest, lines("_ = \(spelling)"))], exactAllowance: false).isEmpty, "\(spelling)")
        }
        // The one allowed line is allowed once, in its file only.
        let windowList = ["CGWindowList", "CopyWindowInfo"].joined()
        #expect(Self.captureIssues([(selfTest, lines("let list = \(windowList)([], 1)"))], exactAllowance: true).isEmpty)
        let twice = [Substring("let a = \(windowList)([], 1)"), Substring("let b = \(windowList)([], 2)")]
        #expect(Self.captureIssues([(selfTest, twice)], exactAllowance: true).count == 1, "a second window-list line")
        #expect(Self.captureIssues([(selfTest, lines("let a = 1"))], exactAllowance: true).count == 1,
                "an allowed line that went away must leave the allow-list too")
        #expect(Self.captureIssues([(selfTest, lines("_ = " + ["SC", "ShareableContent"].joined()))],
                                   exactAllowance: false).count == 1)

        // Import statements in every form Swift 6 compiles.
        let word = ["im", "port"].joined()
        let vision = ["Vi", "sion"].joined()
        let importCases: [(line: String, modules: [String])] = [
            ("\(word) \(vision)", [vision]),
            ("  \(word)\t\(vision)", [vision]),
            ("@preconcurrency \(word) \(vision)", [vision]),
            ("@preconcurrency @testable \(word) \(vision)", [vision]),
            ("@_exported \(word) \(vision)", [vision]),
            ("@_implementationOnly \(word) AppKit", ["AppKit"]),
            ("@_spi(Private) \(word) \(vision)", [vision]),
            ("internal \(word) AppKit", ["AppKit"]),
            ("public \(word) AppKit", ["AppKit"]),
            ("package \(word) AppKit", ["AppKit"]),
            ("private \(word) \(vision)", [vision]),
            ("@preconcurrency internal \(word) \(vision)", [vision]),
            ("\(word) Foundation; \(word) \(vision)", ["Foundation", vision]),
            ("\(word) struct \(vision).Observation", [vision]),
            ("\(word) Carbon.HIToolbox", ["Carbon"]),
            ("@testable \(word) AsteriaScaleKit", ["AsteriaScaleKit"]),
            ("\(word) AsteriaScale\(vision)", ["AsteriaScale\(vision)"]),
            ("// \(word) \(vision)", []),
            ("let note = \"no test \(word)s \(vision)\"", []),
        ]
        let statement = try Self.importStatement()
        for item in importCases {
            #expect(Self.importedModules(Substring(item.line), matching: statement) == item.modules, "\(item.line)")
        }
        #expect(try Self.importIssues([(kitFile, lines("internal \(word) AppKit"))], allowed: Self.kitImports).count == 1)
        #expect(try Self.importIssues([(kitFile, lines("\(word) Darwin"))], allowed: Self.kitImports).isEmpty)
        let visionFile = "Sources/AsteriaScaleVision/Planted.swift"
        #expect(try Self.importIssues([(visionFile, lines("@preconcurrency \(word) AppKit"))], allowed: Self.visionImports,
                                      never: Self.visionNever).count == 1)
        #expect(try Self.importIssues([(visionFile, lines("\(word) ImageIO"))], allowed: Self.visionImports,
                                      never: Self.visionNever).isEmpty)

        // What a test may not do, and what it may.
        let testFile = "Tests/AsteriaScaleKitTests/Planted.swift"
        let coreText = ["Core", "Text"].joined()
        let swiftRequest = ["Recognize", "TextRequest"].joined()
        let sequenceHandler = ["V", "N", "Sequence", "Request", "Handler"].joined()
        let textRequest = ["V", "N", "RecognizeText", "Request"].joined()
        let typeBehindADot = ["Vi", "sion.V", "N", "Request"].joined()
        var flagged: [String] = [
            "@preconcurrency \(word) \(vision)", "internal \(word) \(vision)", "\(word) Foundation; \(word) \(vision)",
            "/* x */ \(word) \(vision)", "\(word) \(coreText)", "@_exported \(word) AppKit",
        ]
        flagged.append("_ = try await \(swiftRequest)().perform(on: image)")
        flagged.append("let handler = \(sequenceHandler)()")
        flagged.append("let request = \(textRequest)()")
        flagged.append("let request: \(typeBehindADot)? = nil")
        flagged.append(["CT", "LineDraw(line, context)"].joined())
        flagged.append(["CT", "FontCreateWithName(name, 14, nil)"].joined())
        flagged.append(["Fixture", "Renderer.render(lines: [])"].joined())
        flagged.append(["Vision", "Fixture.warmUp()"].joined())
        flagged.append(["context.show", "Glyphs(glyphs, at: points)"].joined())
        for line in flagged {
            #expect(try !Self.testIssues([(testFile, lines(line))]).isEmpty, "\(line)")
        }
        let devNull = ["DEV", "NULL"].joined()
        let harmless: [String] = [
            "\(word) AsteriaScale\(vision)", "@testable \(word) AsteriaScale\(vision)", "\(word) Carbon.HIToolbox",
            "// no test \(word)s \(vision) or renders text", "let image = PixelBuffer.visionImage(of: frame)",
            "let path = \"/dev/null\"; let model = \"\(devNull)\"",
        ]
        for line in harmless {
            #expect(try Self.testIssues([(testFile, lines(line))]).isEmpty, "\(line)")
        }
    }
}
