import Foundation
import Testing
@testable import AsteriaScaleKit

/// What the package's sources may name (roadmap 20.3 DoD 6, 8 and 10;
/// docs/MACOS.md T22, T26, T37). Every scan is a pure function over (path,
/// lines): the folder walks feed it the package's files, so a new file is
/// covered without a list to update, and the tests at the end feed it
/// planted lines, at least one per name and per import form, each checked
/// for the issue its own rule reports, so a scan that stops seeing a
/// spelling fails; every list is also pinned against a second spelling.
/// The names and planted spellings are assembled from pieces, as `hostWord`
/// is, so this file does not match itself.
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

    /// `text` as a planted source's lines.
    static func lines(_ text: String) -> [Substring] { text.split(separator: "\n", omittingEmptySubsequences: false) }

    // MARK: Screen capture and permissions

    /// The screen capture and permission names DoD 8 lists, two of them
    /// widened as T22 states the rule: every name that starts with
    /// CoreGraphics' window-list prefix (DoD 8's two functions, the other
    /// functions and the option type), and the labels the image-from-array
    /// function of that family takes in Swift, where CoreGraphics' API notes
    /// import it as a CGImage initializer that holds no part of the prefix
    /// (any initializer whose labels start the same way is caught). Only
    /// Sources/AsteriaScale/ScreenSource.swift may name them: the first six
    /// anywhere in it, the window-list prefix only in the allow-list's
    /// exact spelling and on as many lines as it says. Matched as
    /// substrings of raw lines, comments and strings included.
    static let captureGroup = [
        ["Screen", "CaptureKit"], ["SC", "ShareableContent"], ["SC", "ScreenshotManager"], ["SC", "ContentFilter"],
        ["CGPreflight", "ScreenCaptureAccess"], ["CGRequest", "ScreenCaptureAccess"],
        ["CGWin", "dowList"], ["window", "ListFrom"],
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

    /// The one file that may name the screen capture (DoD 8, T22).
    static let captureFile = "Sources/AsteriaScale/ScreenSource.swift"

    /// The capture-group names the capture file may hold on any line: the
    /// capture framework, the three of its types the capture uses and the
    /// two Screen Recording calls. Not the window-list prefix nor the
    /// image-from-array labels: the capture is the screenshot manager's, and
    /// the window-list image calls are deprecated.
    static let captureFileNames = Set([
        ["Screen", "CaptureKit"], ["SC", "ShareableContent"], ["SC", "ScreenshotManager"], ["SC", "ContentFilter"],
        ["CGPreflight", "ScreenCaptureAccess"], ["CGRequest", "ScreenCaptureAccess"],
    ].map { $0.joined() })

    /// The exact window-list spellings a file may hold, and on how many
    /// lines. One line, and only that one: the window-list copy call with
    /// which 20.2's overlay.windowServer self-test check reads its own
    /// panel's window record (layer, sharing state), moved from
    /// SelfTest.swift into a static helper of the capture file. That query
    /// asks for no permission and raises no prompt.
    static let captureAllowList: [String: [String: Int]] = [
        ["CGWin", "dowList", "CopyWindowInfo"].joined(): [captureFile: 1],
    ]

    /// The capture and permission issues of `sources`: a never-group name
    /// anywhere, a capture-group name outside the capture file's names and
    /// the allowed spellings, and, with `exactAllowance`, an allowed file
    /// that holds its spelling on more or fewer lines than the allow-list
    /// says. An allowed spelling is taken out of its line before the group
    /// is matched, so the prefix it starts with is allowed there only:
    /// another window-list name beside it, or on any other line of the
    /// file, is still an issue.
    static func captureIssues(_ sources: [Source], exactAllowance: Bool) -> [String] {
        var issues: [String] = []
        var allowedLines: [String: [String: Int]] = [:]
        for source in sources {
            let allowedHere = captureAllowList.keys.filter { captureAllowList[$0]?[source.path] != nil }.sorted()
            for (number, line) in source.lines.enumerated() {
                var rest = String(line)
                for spelling in allowedHere where rest.contains(spelling) {
                    allowedLines[spelling, default: [:]][source.path, default: 0] += 1
                    rest = rest.replacingOccurrences(of: spelling, with: " ")
                }
                for name in captureGroup where rest.contains(name) {
                    if source.path == captureFile, captureFileNames.contains(name) { continue }
                    issues.append("\(name) in \(source.path):\(number + 1)")
                }
                for name in neverGroup where line.contains(name) {
                    issues.append("\(name) in \(source.path):\(number + 1), which no source may name")
                }
            }
        }
        if exactAllowance {
            for (spelling, files) in captureAllowList.sorted(by: { $0.key < $1.key }) {
                for (path, allowed) in files.sorted(by: { $0.key < $1.key })
                where (allowedLines[spelling]?[path] ?? 0) != allowed {
                    issues.append("\(spelling) on \(allowedLines[spelling]?[path] ?? 0) lines of \(path), the allow-list says \(allowed)")
                }
            }
        }
        return issues
    }

    @Test func captureAndPermissionNamesOnlyWhereAllowed() throws {
        let sources = try Self.swiftSources("Sources") + Self.swiftSources("Tests")
        #expect(sources.count >= 30)
        for issue in Self.captureIssues(sources, exactAllowance: true) { Issue.record("\(issue)") }
        // The capture file really is the capture: every one of its names is there.
        let capture = try #require(sources.first { $0.path == Self.captureFile }, "no \(Self.captureFile)")
        for name in Self.captureFileNames.sorted() where !capture.lines.contains(where: { $0.contains(name) }) {
            Issue.record("\(Self.captureFile) does not name \(name)")
        }
    }

    // MARK: Where the screen source is built

    /// Where the screen source is built and the capture gate decides (DoD 8,
    /// T22: built only where the decision is checked, once at launch). The
    /// initializer takes the gate's permit, which only an `.on` decision
    /// carries, so its one construction is the line of the constructor file
    /// that unwraps the decision; besides it only the initializer's own
    /// declaration in the capture file may spell it. A construction is
    /// matched in every spelling the scan knows: the type followed by `(`
    /// or `.init`, and the initializer's label after `init(` on any type
    /// (`.init(permit:`). The decision is matched as its whole statement,
    /// its lines joined, so it is pinned to this process's own traits too:
    /// a literal parent pid, environment, bundle id or argument list would
    /// make a direct exec look like a LaunchServices launch.
    static let constructorFile = "Sources/AsteriaScale/AppDelegate.swift"
    static let constructorLine = ["if case .on(let permit) = captureDecision { screenSource = Screen",
                                  "Source(permit: permit) }"].joined()
    static let declarationLine = ["in", "it(permit: CaptureGate.Permit) {"].joined()
    static let constructorPatterns = [[#"Screen"#, #"Source\s*(?:\(|\.\s*init\b)"#], [#"\bin"#, #"it\s*\(\s*permit\s*:"#]]
        .map { $0.joined() }
    static let decideSpelling = ["CaptureGate", ".decide("].joined()
    static let decideStatement = ["captureDecision = CaptureGate.de", "cide(arguments: CommandLine.arguments, ",
                                  "parentPid: getppid(), environment: ProcessInfo.processInfo.environment, ",
                                  "bundleId: Bundle.main.bundleIdentifier ?? \"\")"].joined()

    /// The statement that starts on `lines[start]`: that line and the next
    /// ones until its parentheses close (at most eight), with every run of
    /// white space made one space.
    static func statement(_ lines: [Substring], from start: Int) -> String {
        var text = ""
        var depth = 0
        var index = start
        while lines.indices.contains(index), index - start < 8 {
            text += " " + String(lines[index])
            depth += lines[index].filter { $0 == "(" }.count - lines[index].filter { $0 == ")" }.count
            if depth <= 0 { break }
            index += 1
        }
        return text.split(whereSeparator: \.isWhitespace).joined(separator: " ")
    }

    /// Every construction of `sources` beyond the one line and the
    /// declaration, and every decision beyond the one statement.
    static func gateIssues(_ sources: [Source]) throws -> [String] {
        let patterns = try constructorPatterns.map { try Regex($0).wordBoundaryKind(.simple) }
        var constructors: [(path: String, line: Int, text: String)] = []
        var decisions: [(path: String, line: Int, statement: String)] = []
        for source in sources {
            for (number, line) in source.lines.enumerated() {
                if patterns.contains(where: { line.contains($0) }) {
                    constructors.append((source.path, number + 1, line.trimmingCharacters(in: .whitespaces)))
                }
                if line.contains(decideSpelling) {
                    decisions.append((source.path, number + 1, statement(source.lines, from: number)))
                }
            }
        }
        var issues: [String] = []
        let expected = ["\(captureFile) \(declarationLine)", "\(constructorFile) \(constructorLine)"].sorted()
        if constructors.map({ "\($0.path) \($0.text)" }).sorted() != expected {
            issues.append("the screen source is built on \(constructors.map { "\($0.path):\($0.line)" }), "
                          + "not only on one line of \(constructorFile) that unwraps the gate's permit")
        }
        if decisions.count != 1 || decisions.first?.path != constructorFile || decisions.first?.statement != decideStatement {
            issues.append("the capture gate decides on \(decisions.map { "\($0.path):\($0.line)" }), "
                          + "not once in \(constructorFile) from this process's own traits")
        }
        return issues
    }

    /// The package's sources; the tests call the pure decision freely and
    /// cannot build the screen source (they do not link the app).
    @Test func theScreenSourceIsBuiltOnlyWhereTheGateDecides() throws {
        let sources = try Self.swiftSources("Sources")
        #expect(sources.contains { $0.path == Self.constructorFile } && sources.contains { $0.path == Self.captureFile })
        for issue in try Self.gateIssues(sources) { Issue.record("\(issue)") }
    }

    // MARK: Imports

    /// `text` as the compiler's tokens see it: every comment (`//` to the end
    /// of its line, `/* … */` nested) and the contents of every string
    /// literal (escapes, the raw `#"…"#` and multi-line forms) become spaces,
    /// with line ends kept so line numbers hold, and the code inside an
    /// interpolation stays code. It walks bytes through bounds checks only,
    /// so no input traps. Bare `/…/` regex literals are not recognized (the
    /// package has none; `everySourceReadsToItsEnd` would notice one that
    /// swallowed code). `complete` is false when a comment or a string is
    /// still open at the end, or a single-line string meets a line end:
    /// whatever follows cannot be trusted, and the scans say so.
    static func codeOnly(_ text: String) -> (code: String, complete: Bool) {
        let bytes = Array(text.utf8)
        var code = bytes
        let slash = UInt8(ascii: "/"), star = UInt8(ascii: "*"), quote = UInt8(ascii: "\""), hash = UInt8(ascii: "#")
        let backslash = UInt8(ascii: "\\"), newline = UInt8(ascii: "\n"), space = UInt8(ascii: " ")
        let open = UInt8(ascii: "("), close = UInt8(ascii: ")")
        func at(_ index: Int) -> UInt8? { index >= 0 && index < bytes.count ? bytes[index] : nil }
        func blank(_ index: Int) {
            if index >= 0, index < code.count, code[index] != newline { code[index] = space }
        }
        func hashes(from index: Int) -> Int {
            var count = 0
            while at(index + count) == hash { count += 1 }
            return count
        }
        // Code at the top level (parens nil) or inside an interpolation (its
        // open parentheses), or the inside of a string literal.
        enum Context { case code(parens: Int?), string(hashes: Int, multiline: Bool) }
        var stack: [Context] = [.code(parens: nil)]
        var complete = true
        var index = 0
        while index < bytes.count, let context = stack.last, let byte = at(index) {
            switch context {
            case .code(let parens):
                if byte == slash, at(index + 1) == slash {
                    while let next = at(index), next != newline {
                        blank(index)
                        index += 1
                    }
                } else if byte == slash, at(index + 1) == star {
                    var depth = 0
                    repeat {
                        if at(index) == slash, at(index + 1) == star {
                            depth += 1
                            blank(index)
                            blank(index + 1)
                            index += 2
                        } else if at(index) == star, at(index + 1) == slash {
                            depth -= 1
                            blank(index)
                            blank(index + 1)
                            index += 2
                        } else {
                            blank(index)
                            index += 1
                        }
                    } while depth > 0 && index < bytes.count
                    if depth > 0 { complete = false }
                } else if byte == quote || byte == hash {
                    let count = hashes(from: index)
                    if at(index + count) == quote {
                        let multiline = at(index + count + 1) == quote && at(index + count + 2) == quote
                        stack.append(.string(hashes: count, multiline: multiline))
                        index += count + (multiline ? 3 : 1)
                    } else {
                        index += max(count, 1)
                    }
                } else if let depth = parens, byte == open {
                    _ = stack.popLast()
                    stack.append(.code(parens: depth + 1))
                    index += 1
                } else if let depth = parens, byte == close {
                    _ = stack.popLast()
                    if depth > 1 { stack.append(.code(parens: depth - 1)) }
                    index += 1
                } else {
                    index += 1
                }
            case .string(let count, let multiline):
                let quotes = multiline ? 3 : 1
                if byte == backslash, hashes(from: index + 1) >= count {
                    let escaped = index + 1 + count
                    if at(escaped) == open {
                        stack.append(.code(parens: 1))
                        index = escaped + 1
                    } else {
                        for position in index...escaped { blank(position) }
                        index = escaped + 1
                    }
                } else if byte == quote, (0..<quotes).allSatisfy({ at(index + $0) == quote }),
                          hashes(from: index + quotes) >= count {
                    _ = stack.popLast()
                    index += quotes + count
                } else if byte == newline, !multiline {
                    complete = false
                    _ = stack.popLast()
                    index += 1
                } else {
                    blank(index)
                    index += 1
                }
            }
        }
        if stack.count != 1 { complete = false }
        return (String(decoding: code, as: UTF8.self), complete)
    }

    /// An import statement in code from `codeOnly`: it starts a line or
    /// follows `;`, then come any attributes (`@preconcurrency`, `@testable`,
    /// `@_exported`, `@_spi(…)`, …) and access modifier, the keyword, an
    /// optional declaration kind (`import struct M.T`) and the module, whose
    /// path counts as its first component (`Carbon` of `Carbon.HIToolbox`).
    /// The whitespace between them may hold line ends (an attribute on a
    /// line of its own, the module on the next line) and, blanked by
    /// `codeOnly`, comments. Typed: a pattern that lost its module group
    /// throws here instead of trapping where the group is read.
    static func importStatement() throws -> Regex<(Substring, Substring)> {
        let attributes = #"(?:(?:@[A-Za-z_][A-Za-z0-9_]*(?:\([^)]*\))?|open|public|package|internal|fileprivate|private)\s+)*"#
        return try Regex(#"(?:^|;)\s*"# + attributes + ["im", "port"].joined() + #"\s+"# + declarationKind
            + #"([A-Za-z_][A-Za-z0-9_]*)"#, as: (Substring, Substring).self).anchorsMatchLineEndings()
    }

    /// The declaration kinds an import may name (`import struct M.T`). Each
    /// is a keyword, never a bare module name, so a reading that takes one
    /// for the module has misread the statement.
    static let declarationKinds: Set<String> = ["class", "struct", "enum", "func", "protocol", "var", "let", "typealias"]
    /// The optional kind both import patterns skip, from the one list above
    /// so the two readings cannot drift apart.
    static let declarationKind = #"(?:(?:"# + declarationKinds.sorted().joined(separator: "|") + #")\s+)?"#

    /// The modules `text` imports, each with the line its name is on, and
    /// whether `codeOnly` read the text to its end. Text in a comment or a
    /// string literal is no import, and a `;` there starts no statement.
    static func importedModules(in text: String, matching statement: Regex<(Substring, Substring)>)
        -> (modules: [(line: Int, module: String)], complete: Bool) {
        let read = codeOnly(text)
        let newline = UInt8(ascii: "\n")
        let modules = read.code.matches(of: statement).map { match in
            (line: read.code[..<match.output.1.startIndex].utf8.reduce(1) { $1 == newline ? $0 + 1 : $0 },
             module: String(match.output.1))
        }
        return (modules, read.complete)
    }

    /// Each import of `sources` outside `allowed` or inside `never`, and
    /// each source `codeOnly` cannot read to its end.
    static func importIssues(_ sources: [Source], allowed: Set<String>, never: Set<String> = []) throws -> [String] {
        let statement = try importStatement()
        var issues: [String] = []
        for source in sources {
            let read = importedModules(in: source.lines.joined(separator: "\n"), matching: statement)
            if !read.complete {
                issues.append("\(source.path) leaves a comment or a string literal open: its imports cannot be read")
            }
            for item in read.modules where !allowed.contains(item.module) || never.contains(item.module) {
                issues.append("\(source.path):\(item.line) imports \(item.module)")
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
            Self.importedModules(in: source.lines.joined(separator: "\n"), matching: statement).modules.map(\.module)
        })
        #expect(visionModules.isSuperset(of: ["Vision", "CoreText", "CryptoKit"]), "\(visionModules)")
        for issue in try Self.importIssues(vision, allowed: Self.visionImports, never: Self.visionNever) {
            Issue.record("\(issue)")
        }
    }

    /// Every file of the package reads to its end as code, and the import
    /// parser finds at least every import a plain reading sees (a line that
    /// starts with the keyword): a reading that swallowed code would
    /// otherwise hide imports without a sound.
    @Test func everySourceReadsToItsEnd() throws {
        let sources = try Self.swiftSources("Sources") + Self.swiftSources("Tests")
        let statement = try Self.importStatement()
        var plainImports = 0
        for source in sources {
            let check = try Self.plainImportCheck(source, matching: statement)
            #expect(check.complete, "\(source.path)")
            #expect(check.missed.isEmpty, "\(check.missed)")
            plainImports += check.seen
        }
        #expect(plainImports >= 80, "\(plainImports) plain imports")
    }

    /// The plain reading `everySourceReadsToItsEnd` holds the parser to: a
    /// line that starts with the keyword, then an optional declaration kind
    /// and the module. Returns how many such lines it saw, each it saw that
    /// the parser does not report on that line (as "path:line module"), and
    /// whether `codeOnly` read the source to its end. A line that ends right
    /// after a kind (the module on the next line) shows it no module, so it
    /// is not counted: a kind is a keyword, never a module.
    static func plainImportCheck(_ source: Source, matching statement: Regex<(Substring, Substring)>) throws
        -> (seen: Int, missed: [String], complete: Bool) {
        let plain = try Regex(["im", "port"].joined() + #"\s+"# + declarationKind + #"([A-Za-z_][A-Za-z0-9_]*)"#,
                              as: (Substring, Substring).self)
        let read = importedModules(in: source.lines.joined(separator: "\n"), matching: statement)
        let parsed = Set(read.modules.map { "\($0.line) \($0.module)" })
        var seen = 0
        var missed: [String] = []
        for (number, line) in source.lines.enumerated() {
            guard let match = line.prefixMatch(of: plain), !declarationKinds.contains(String(match.output.1)) else { continue }
            seen += 1
            let found = "\(number + 1) \(match.output.1)"
            if !parsed.contains(found) { missed.append("\(source.path):\(found)") }
        }
        return (seen, missed, read.complete)
    }

    // MARK: No Vision and no text in the suite

    /// What no test may name (T26): the recognizer and its request types (VN
    /// plus a capital is any Vision type; the Swift API's text request and
    /// every request handler are covered by their suffixes), the renderer,
    /// the fixture and the self-test's frame source that drive them, and
    /// the CoreText, CoreGraphics and
    /// HIToolbox calls that make or draw text (fonts may write caches into
    /// the run's home, which test-swift.sh refuses).
    static let testNames = [
        ["VisionText", "Recognizer"], ["Recognize", "TextRequest"], ["Request", "Handler"],
        ["Fixture", "Renderer"], ["Vision", "Fixture"], ["Fixture", "FrameSource"],
        ["CT", "Font"], ["CT", "Line"], ["CT", "Frame"], ["CT", "Run"], ["CT", "Typesetter"],
        ["CG", "Font"], ["show", "Glyphs"], ["HITheme", "DrawText"],
    ].map { $0.joined() }

    /// The issues of test sources: an import outside `testImports` (as the
    /// import parser reads code), an `import` of Vision, CoreText or AppKit
    /// anywhere on a raw line (in or after a comment too), a Vision type, or
    /// a name in `testNames`. The last three read raw lines, comments and
    /// string literals included.
    static func testIssues(_ sources: [Source]) throws -> [String] {
        var issues = try importIssues(sources, allowed: testImports)
        // Simple word boundaries: Unicode's would join a module, a dot and
        // a type into one word and hide the type.
        let anywhere = try Regex(#"\b"# + ["im", "port"].joined() + #"\s+"# + declarationKind + #"(?:"#
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

    /// Planted lines, never files: each forbidden name is flagged by its own
    /// entry (the exact issue list is checked, so another rule catching it
    /// does not hide a lost entry), each harmless line is not, and every
    /// list is spelled again, from other pieces, so a name dropped from it
    /// fails too.
    @Test func theScansFlagEveryForbiddenSpelling() throws {
        let kitFile = "Sources/AsteriaScaleKit/Planted.swift"
        let selfTest = "Sources/AsteriaScale/SelfTest.swift"
        let captureFile = ["Sources/AsteriaScale/Screen", "Source.swift"].joined()
        #expect(Self.captureFile == captureFile)
        let word = ["im", "port"].joined()

        // DoD 8's names, the window-list prefix (T22) and its Swift labels,
        // each in a call it is flagged for by its own entry.
        let screenCaptureKit = ["ScreenCap", "tureKit"].joined()
        let shareable = ["SCShar", "eableContent"].joined()
        let screenshot = ["SCScreensh", "otManager"].joined()
        let contentFilter = ["SCCont", "entFilter"].joined()
        let preflight = ["CGPreflightScr", "eenCaptureAccess"].joined()
        let request = ["CGRequestScr", "eenCaptureAccess"].joined()
        let windowList = ["CGWind", "owList"].joined()
        let windowListLabels = ["windowLi", "stFrom"].joined()
        #expect(Set(Self.captureGroup) == [screenCaptureKit, shareable, screenshot, contentFilter, preflight, request,
                                           windowList, windowListLabels], "DoD 8's names, widened by T22, no more and no fewer")
        let capturePlants: [(line: String, name: String)] = [
            ("@preconcurrency \(word) " + ["ScreenCapt", "ureKit"].joined(), screenCaptureKit),
            (["let content = try await SCShareable", "Content.current"].joined(), shareable),
            (["let image = try await SCScreenshot", "Manager.captureImage(contentFilter: f, configuration: c)"].joined(),
             screenshot),
            (["let filter = SCContent", "Filter(desktopIndependentWindow: window)"].joined(), contentFilter),
            (["guard CGPreflightScreen", "CaptureAccess() else { return }"].joined(), preflight),
            (["_ = CGRequestScreen", "CaptureAccess()"].joined(), request),
            ("let windows = \(windowList)CopyWindowInfo([.optionOnScreenOnly], kCGNullWindowID)", windowList),
            ("let image = \(windowList)CreateImage(rect, .optionOnScreenOnly, kCGNullWindowID, [])", windowList),
            ("let ids = \(windowList)Create(.optionAll, kCGNullWindowID)", windowList),
            ("let info = \(windowList)CreateDescriptionFromArray(ids)", windowList),
            ("let image = \(windowList)CreateImageFromArray(rect, ids, [])", windowList),
            ("let option: \(windowList)Option = .optionAll", windowList),
            ("let image = CGImage(\(windowListLabels)ArrayScreenBounds: rect, windowArray: ids, imageOption: [])",
             windowListLabels),
        ]
        for item in capturePlants {
            #expect(Self.captureIssues([(kitFile, Self.lines(item.line))], exactAllowance: false)
                    == ["\(item.name) in \(kitFile):1"], "\(item.line)")
        }

        let neverPlants: [(line: String, name: String)] = [
            (["NSEvent.addGlobalMonitor", "ForEvents(matching: .mouseMoved)"].joined(), ["addGlobalMon", "itorForEvents"].joined()),
            (["CGEvent.tap", "Create(tap: .cgSessionEventTap"].joined(), ["tapCr", "eate"].joined()),
            (["CGEventTap", "Create("].joined(), ["CGEventTapCr", "eate"].joined()),
            (["AXIsProcessTrusted", "WithOptions(nil)"].joined(), ["AXIsProcessTr", "ustedWithOptions"].joined()),
            (["IOHIDRequest", "Access(kIOHIDRequestType)"].joined(), ["IOHIDReq", "uestAccess"].joined()),
            (["CGRequestListen", "EventAccess()"].joined(), ["CGRequestLis", "tenEventAccess"].joined()),
            (["CGRequestPost", "EventAccess()"].joined(), ["CGRequestPo", "stEventAccess"].joined()),
            (["IOHIDManager", "Open(manager, 0)"].joined(), ["IOHIDMan", "agerOpen"].joined()),
            (["CGDisplayCreate", "Image(id)"].joined(), ["CGDisplayCr", "eateImage"].joined()),
            (["CGDisplay", "StreamCreate("].joined(), ["CGDisp", "layStream"].joined()),
            (["AVCaptureScreen", "Input(displayID: id)"].joined(), ["AVCaptureScr", "eenInput"].joined()),
        ]
        #expect(Set(Self.neverGroup) == Set(neverPlants.map(\.name)), "the never group, no more and no fewer")
        for item in neverPlants {
            #expect(Self.captureIssues([(selfTest, Self.lines("_ = \(item.line)"))], exactAllowance: false)
                    == ["\(item.name) in \(selfTest):1, which no source may name"], "\(item.line)")
            // The capture file's names stop at the capture: no exception there.
            #expect(Self.captureIssues([(captureFile, Self.lines("_ = \(item.line)"))], exactAllowance: false)
                    == ["\(item.name) in \(captureFile):1, which no source may name"], "\(item.line) in the capture file")
        }

        // The one allowed line is allowed once, in its file only, and only
        // its own call: another window-list name beside it, or on another
        // line of that file, is flagged.
        // The capture file may name the first six anywhere, and nothing else
        // of the group: those six are flagged in every other file (above),
        // and the window-list prefix and labels in it too.
        #expect(Self.captureFileNames == [screenCaptureKit, shareable, screenshot, contentFilter, preflight, request],
                "the capture file's names, no more and no fewer")
        let allowed = [windowList, "CopyWindowInfo"].joined()
        for item in capturePlants {
            // The window-list copy call is the one allowed spelling there.
            let expected = Self.captureFileNames.contains(item.name) || item.line.contains(allowed)
                ? [] : ["\(item.name) in \(captureFile):1"]
            #expect(Self.captureIssues([(captureFile, Self.lines(item.line))], exactAllowance: false) == expected,
                    "\(item.line) in the capture file")
        }

        // The one allowed line is allowed once, in its file only, and only
        // its own call: another window-list name beside it, or on another
        // line of that file, is flagged; the old place no longer allows it.
        #expect(Set(Self.captureAllowList.keys) == [allowed] && Self.captureAllowList[allowed] == [captureFile: 1])
        #expect(Self.captureIssues([(captureFile, Self.lines("let list = \(allowed)([], 1)"))], exactAllowance: true).isEmpty)
        #expect(Self.captureIssues([(kitFile, Self.lines("let list = \(allowed)([], 1)"))], exactAllowance: false)
                == ["\(windowList) in \(kitFile):1"], "outside its file")
        #expect(Self.captureIssues([(selfTest, Self.lines("let list = \(allowed)([], 1)"))], exactAllowance: false)
                == ["\(windowList) in \(selfTest):1"], "the self-test reads it through the capture file now")
        #expect(Self.captureIssues([(captureFile, Self.lines("let a = \(allowed)([], 1)\nlet b = \(allowed)([], 2)"))],
                                   exactAllowance: true) == ["\(allowed) on 2 lines of \(captureFile), the allow-list says 1"])
        #expect(Self.captureIssues([(captureFile, Self.lines("let a = 1"))], exactAllowance: true)
                == ["\(allowed) on 0 lines of \(captureFile), the allow-list says 1"],
                "an allowed line that went away must leave the allow-list too")
        #expect(Self.captureIssues([(captureFile, Self.lines("let image = \(windowList)CreateImage(rect, .optionAll, 0, [])"))],
                                   exactAllowance: false) == ["\(windowList) in \(captureFile):1"],
                "another window-list call in the allowed file")
        #expect(Self.captureIssues([(captureFile, Self.lines("let a = \(allowed)([], 1); let b = \(windowList)Create(.optionAll, 0)"))],
                                   exactAllowance: false) == ["\(windowList) in \(captureFile):1"],
                "another window-list call on the allowed line")
        #expect(Self.captureIssues([(selfTest, Self.lines("_ = " + shareable))], exactAllowance: false)
                == ["\(shareable) in \(selfTest):1"])

        // The screen source: built on one line of AppDelegate.swift, the one
        // that unwraps the gate's permit, in any spelling, besides its
        // declaration in the capture file; the gate decides there once, from
        // this process's own traits, on one line or several.
        let appDelegate = ["Sources/AsteriaScale/App", "Delegate.swift"].joined()
        let hoverDriver = "Sources/AsteriaScale/HoverDriver.swift"
        let source = ["Screen", "Source"].joined()
        let declared: Source = (captureFile, Self.lines(["    in", "it(permit: CaptureGate.Permit) {"].joined()))
        let arguments = "CommandLine.arguments", parent = "getppid()", environment = "ProcessInfo.processInfo.environment"
        let bundle = "Bundle.main.bundleIdentifier ?? \"\""
        func decided(_ arguments: String, _ parent: String, _ environment: String, _ bundle: String) -> String {
            ["        captureDecision = CaptureGate.de", "cide(arguments: \(arguments), parentPid: \(parent),\n",
             "                                             environment: \(environment),\n",
             "                                             bundleId: \(bundle))"].joined()
        }
        let decision = decided(arguments, parent, environment, bundle)
        let built = "        if case .on(let permit) = captureDecision { screenSource = \(source)(permit: permit) }"
        #expect(Self.constructorFile == appDelegate)
        #expect(Self.statement(Self.lines(decision), from: 0) == Self.decideStatement)
        #expect(try Self.gateIssues([(appDelegate, Self.lines("\(decision)\n\(built)")), declared]).isEmpty)
        #expect(try Self.gateIssues([(appDelegate, Self.lines("        \(Self.decideStatement)\n\(built)")), declared]).isEmpty,
                "the decision on one line")
        let constructorIssue = "the screen source is built on"
        let decideIssue = "the capture gate decides on"
        func gate(_ sources: [Source]) throws -> [String] {
            try Self.gateIssues(sources).map {
                $0.hasPrefix(constructorIssue) ? constructorIssue : $0.hasPrefix(decideIssue) ? decideIssue : $0
            }
        }
        #expect(try gate([(appDelegate, Self.lines("let a = 1"))]) == [constructorIssue, decideIssue], "neither is spelled")
        #expect(try gate([(appDelegate, Self.lines("\(decision)\n\(built)"))]) == [constructorIssue], "no declaration")
        let constructions: [(path: String, line: String)] = [
            (selfTest, "let s = \(source)(permit: p)"),
            (hoverDriver, "_ = \(source).init(permit: p)"),
            (hoverDriver, "let s: \(source) = .init(permit: p)"),
            (hoverDriver, "let s = \(source) (permit: p)"),
            (hoverDriver, "let make = \(source).init"),
            (hoverDriver, "let s = \(source)()"),
            (captureFile, ["    convenience in", "it(permit: CaptureGate.Permit, n: Int) { self.init(permit: permit) }"].joined()),
            (appDelegate, "// \(source)(permit:) once"),
        ]
        for item in constructions {
            #expect(try gate([(appDelegate, Self.lines("\(decision)\n\(built)")), declared, (item.path, Self.lines(item.line))])
                    == [constructorIssue], "\(item.line) in \(item.path)")
        }
        #expect(try gate([(appDelegate, Self.lines("\(decision)\nscreenSource = \(source)(permit: permit)")), declared])
                == [constructorIssue], "a construction that does not unwrap the decision's permit")
        #expect(try gate([(appDelegate, Self.lines("\(decision)\n\(built)")), declared, (selfTest, Self.lines(decision))])
                == [decideIssue], "a second decision")
        #expect(try gate([(selfTest, Self.lines(decision)), (appDelegate, Self.lines(built)), declared]) == [decideIssue],
                "the decision in another file")
        for (name, literal) in [
            ("arguments", decided("[\"AsteriaScale\"]", parent, environment, bundle)),
            ("parent", decided(arguments, "1", environment, bundle)),
            ("environment", decided(arguments, parent, "[\"__CFBundleIdentifier\": AppIdentity.bundleIdentifier]", bundle)),
            ("bundle", decided(arguments, parent, environment, "AppIdentity.bundleIdentifier")),
        ] {
            #expect(try gate([(appDelegate, Self.lines("\(literal)\n\(built)")), declared]) == [decideIssue],
                    "a literal \(name) in the decision")
        }
        #expect(try gate([(appDelegate, Self.lines("_ = \(source).ownWindowRecord(1)\n\(decision)\n\(built)")), declared])
                .isEmpty, "the static window-record read is not a construction")

        // What a test may not do, and what it may.
        let testFile = "Tests/AsteriaScaleKitTests/Planted.swift"
        func issues(_ text: String) throws -> [String] { try Self.testIssues([(testFile, Self.lines(text))]) }
        let vision = ["Vi", "sion"].joined()
        let coreText = ["Core", "Text"].joined()
        let anywhereNote = "\(testFile):1 imports a module no test may"
        let typeNote = "\(testFile):1 names a Vision type"
        #expect(Self.testImports == Set(["Testing", "Foundation", "Darwin", "CoreGraphics", "Carbon", "AsteriaScale" + "Kit",
                                         "AsteriaScale" + vision]), "what a test may import, no more and no fewer")

        // T26's names, spelled again, each planted alone (without the VN
        // prefix, which a rule of its own flags) and flagged by its own entry.
        let namePlants: [(line: String, name: String)] = [
            (["_ = VisionT", "extRecognizer().recognize(frame, capturedAt: bounds)"].joined(), ["VisionTextR", "ecognizer"].joined()),
            (["_ = try await RecognizeT", "extRequest().perform(on: image)"].joined(), ["RecognizeTex", "tRequest"].joined()),
            (["let handler = ImageReque", "stHandler(data: data)"].joined(), ["Reques", "tHandler"].joined()),
            (["_ = Fixture", "Renderer.render(lines: [])"].joined(), ["FixtureRen", "derer"].joined()),
            (["try Vision", "Fixture.warmUp()"].joined(), ["VisionFix", "ture"].joined()),
            (["let frames = FixtureFr", "ameSource(cursor: c)"].joined(), ["FixtureFra", "meSource"].joined()),
            (["let font = CTFo", "ntCreateWithName(name, 14, nil)"].joined(), ["CTF", "ont"].joined()),
            (["CTLi", "neDraw(line, context)"].joined(), ["CTL", "ine"].joined()),
            (["let setter = CTFra", "mesetterCreateWithAttributedString(text)"].joined(), ["CTFr", "ame"].joined()),
            (["CTRu", "nDraw(run, context, range)"].joined(), ["CTR", "un"].joined()),
            (["let setter = CTTypes", "etterCreateWithAttributedString(text)"].joined(), ["CTType", "setter"].joined()),
            (["let font = CGFo", "nt(name)"].joined(), ["CGF", "ont"].joined()),
            (["context.showGl", "yphs(glyphs, at: points)"].joined(), ["showGly", "phs"].joined()),
            (["HIThemeDrawT", "extBox(text, &rect, 0, &info, context, 0)"].joined(), ["HIThemeDr", "awText"].joined()),
        ]
        #expect(Set(Self.testNames) == Set(namePlants.map(\.name)), "T26's names, no more and no fewer")
        for item in namePlants {
            #expect(try issues(item.line) == ["\(item.name) in \(testFile):1"], "\(item.line)")
        }

        // A Vision type is VN and a capital, alone or behind the module's dot.
        for line in [["let observation: V", "NRecognizedTextObservation? = nil"], ["let request: Vi", "sion.V", "NRequest? = nil"]] {
            #expect(try issues(line.joined()) == [typeNote], "\(line.joined())")
        }
        #expect(try issues(["let handler = V", "NSequenceReq", "uestHandler()"].joined())
                == [typeNote, ["Reques", "tHandler"].joined() + " in \(testFile):1"])

        // Imports: the allow-list (as the import parser reads code) and,
        // for Vision, CoreText and AppKit, the raw-line rule, which sees
        // them in or after a comment too.
        let importPlants: [(line: String, issues: [String])] = [
            ("\(word) SwiftUI", ["\(testFile):1 imports SwiftUI"]),
            ("@preconcurrency \(word) ImageIO", ["\(testFile):1 imports ImageIO"]),
            ("/* x */ \(word) Cocoa", ["\(testFile):1 imports Cocoa"]),
            ("\(word)\n    Cocoa", ["\(testFile):2 imports Cocoa"]),
            ("@preconcurrency \(word) \(vision)", ["\(testFile):1 imports \(vision)", anywhereNote]),
            ("internal \(word) \(vision)", ["\(testFile):1 imports \(vision)", anywhereNote]),
            ("\(word) Foundation; \(word) \(vision)", ["\(testFile):1 imports \(vision)", anywhereNote]),
            ("\(word) \(coreText)", ["\(testFile):1 imports \(coreText)", anywhereNote]),
            ("@_exported \(word) AppKit", ["\(testFile):1 imports AppKit", anywhereNote]),
            ("/* x */ \(word) \(vision)", ["\(testFile):1 imports \(vision)", anywhereNote]),
            ("/* x */ \(word) \(coreText)", ["\(testFile):1 imports \(coreText)", anywhereNote]),
            ("/* x */ \(word) AppKit", ["\(testFile):1 imports AppKit", anywhereNote]),
            ("// \(word) \(vision)", [anywhereNote]),
            ("// \(word) \(coreText)", [anywhereNote]),
            ("// \(word) AppKit", [anywhereNote]),
            ("// \(word) class AppKit.NSView", [anywhereNote]),
        ]
        for item in importPlants {
            #expect(try issues(item.line) == item.issues, "\(item.line)")
        }

        let devNull = ["DEV", "NULL"].joined()
        let harmless: [String] = [
            "\(word) AsteriaScale\(vision)", "@testable \(word) AsteriaScale\(vision)", "\(word) Carbon.HIToolbox",
            "\(word) Testing", "\(word) CoreGraphics", "\(word) Darwin; \(word) Foundation",
            "// no test \(word)s \(vision) or renders text", "let image = PixelBuffer.visionImage(of: frame)",
            "let path = \"/dev/null\"; let model = \"\(devNull)\"", "let note = \"\(word) SwiftUI\"",
        ]
        for line in harmless {
            #expect(try issues(line).isEmpty, "\(line)")
        }
    }

    /// Import statements in every form the parser reads (attributes, an
    /// access modifier, `;`, a declaration kind, a block comment anywhere in
    /// the statement, line ends inside it), text that is none (in a `//`
    /// comment, a block comment, a string literal of each kind, a `;` in
    /// them included), with the line each module name is on, and the rules
    /// of the Kit and Vision targets on the forms the line-start reading
    /// used to miss.
    @Test func theImportParserReadsEveryForm() throws {
        let word = ["im", "port"].joined()
        let vision = ["Vi", "sion"].joined()
        let cases: [(text: String, found: [String], complete: Bool)] = [
            ("\(word) \(vision)", ["1 \(vision)"], true),
            ("  \(word)\t\(vision)", ["1 \(vision)"], true),
            ("@preconcurrency \(word) \(vision)", ["1 \(vision)"], true),
            ("@preconcurrency @testable \(word) \(vision)", ["1 \(vision)"], true),
            ("@_exported \(word) \(vision)", ["1 \(vision)"], true),
            ("@_implementationOnly \(word) AppKit", ["1 AppKit"], true),
            ("@_spi(Private) \(word) \(vision)", ["1 \(vision)"], true),
            ("internal \(word) AppKit", ["1 AppKit"], true),
            ("public \(word) AppKit", ["1 AppKit"], true),
            ("package \(word) AppKit", ["1 AppKit"], true),
            ("fileprivate \(word) AppKit", ["1 AppKit"], true),
            ("private \(word) \(vision)", ["1 \(vision)"], true),
            // Not a modifier an import takes, read anyway: reading more is the safe side.
            ("open \(word) AppKit", ["1 AppKit"], true),
            ("@preconcurrency internal \(word) \(vision)", ["1 \(vision)"], true),
            ("\(word) Foundation; \(word) \(vision)", ["1 Foundation", "1 \(vision)"], true),
            ("\(word) struct \(vision).Observation", ["1 \(vision)"], true),
            ("\(word) Carbon.HIToolbox", ["1 Carbon"], true),
            ("@testable \(word) AsteriaScaleKit", ["1 AsteriaScaleKit"], true),
            ("\(word) AsteriaScale\(vision)", ["1 AsteriaScale\(vision)"], true),
            // A block comment is whitespace to the compiler, wherever it is.
            ("/* x */ \(word) AppKit", ["1 AppKit"], true),
            ("\(word) /* x */ AppKit", ["1 AppKit"], true),
            ("@preconcurrency /* x */ \(word) AppKit", ["1 AppKit"], true),
            ("/* a /* nested */ b */ \(word) AppKit", ["1 AppKit"], true),
            ("/* one\ntwo */ \(word) AppKit", ["2 AppKit"], true),
            // Line ends inside the statement.
            ("\(word)\n    AppKit", ["2 AppKit"], true),
            ("@preconcurrency\n\(word) \(vision)", ["2 \(vision)"], true),
            ("\(word) struct\n    \(vision).Observation", ["2 \(vision)"], true),
            ("let a = 1\n\n\(word) Darwin", ["3 Darwin"], true),
            // Comments and string literals hold no import, a `;` in them included.
            ("// \(word) \(vision)", [], true),
            ("// x; \(word) AppKit", [], true),
            ("/* \(word) AppKit */", [], true),
            ("\(word) Foundation // ; \(word) AppKit", ["1 Foundation"], true),
            ("let note = \"no test \(word)s \(vision)\"", [], true),
            ("let s = \"a; \(word) AppKit\"", [], true),
            ("let s = \"a\\\"; \(word) AppKit\"", [], true),
            ("let s = #\"a\"; \(word) AppKit\"#", [], true),
            ("let s = ##\"a\"#; \(word) AppKit\"##", [], true),
            ("let s = \"\"\"\n\(word) AppKit\n\"\"\"", [], true),
            // Code resumes after a string, an interpolation and a comment marker inside a string.
            ("let s = \"\\(f(\"x\"))\"; \(word) AppKit", ["1 AppKit"], true),
            ("let s = #\"\\#(f(\"x\"))\"#; \(word) AppKit", ["1 AppKit"], true),
            ("let s = \"// not a comment\"; \(word) AppKit", ["1 AppKit"], true),
            ("let s = \"/* not a comment\"; \(word) AppKit", ["1 AppKit"], true),
            ("let s = \"a\\\\\"; \(word) AppKit", ["1 AppKit"], true),
            // Text left open: nothing after it can be trusted.
            ("let s = \"abc", [], false),
            ("/* open\n\(word) AppKit", [], false),
            ("let s = \"\"\"\n\(word) AppKit", [], false),
            ("let s = \"a\n\(word) AppKit", ["2 AppKit"], false),
        ]
        let statement = try Self.importStatement()
        for item in cases {
            let read = Self.importedModules(in: item.text, matching: statement)
            #expect(read.modules.map { "\($0.line) \($0.module)" } == item.found, "\(item.text)")
            #expect(read.complete == item.complete, "\(item.text)")
        }

        // The plain reading everySourceReadsToItsEnd checks the parser with
        // never takes a declaration kind for the module. It cannot tell a
        // multi-line string's line from code, so such a line that starts with
        // the keyword is reported: loud, never silent.
        let planted = "Tests/AsteriaScaleKitTests/Planted.swift"
        let plainCases: [(text: String, seen: Int, missed: [String])] = [
            ("\(word) Foundation", 1, []),
            ("\(word) struct \(vision).Observation", 1, []),
            ("\(word) func Darwin.exit", 1, []),
            ("\(word) struct\n    \(vision).Observation", 0, []),
            ("\(word) letters", 1, []),
            ("@preconcurrency \(word) \(vision)", 0, []),
            ("let s = \"\"\"\n\(word) AppKit\n\"\"\"", 1, ["\(planted):2 AppKit"]),
        ]
        for item in plainCases {
            let check = try Self.plainImportCheck((planted, Self.lines(item.text)), matching: statement)
            #expect(check.seen == item.seen, "\(item.text)")
            #expect(check.missed == item.missed, "\(item.text)")
            #expect(check.complete, "\(item.text)")
        }
        #expect(Self.declarationKinds == ["let", "var", "typealias", "protocol", "func", "enum", "struct", "class"])

        // The Kit and Vision rules see the forms above.
        let kitFile = "Sources/AsteriaScaleKit/Planted.swift"
        let visionFile = "Sources/AsteriaScaleVision/Planted.swift"
        let hidden: [(text: String, line: Int)] = [
            ("internal \(word) AppKit", 1), ("fileprivate \(word) AppKit", 1),
            ("/* x */ \(word) AppKit", 1), ("\(word) /* x */ AppKit", 1),
            ("@preconcurrency /* x */ \(word) AppKit", 1), ("\(word)\n    AppKit", 2), ("@preconcurrency\n\(word) AppKit", 2),
        ]
        for item in hidden {
            #expect(try Self.importIssues([(kitFile, Self.lines(item.text))], allowed: Self.kitImports)
                    == ["\(kitFile):\(item.line) imports AppKit"], "\(item.text)")
            #expect(try Self.importIssues([(visionFile, Self.lines(item.text))], allowed: Self.visionImports,
                                          never: Self.visionNever) == ["\(visionFile):\(item.line) imports AppKit"], "\(item.text)")
        }
        #expect(try Self.importIssues([(kitFile, Self.lines("\(word) Darwin"))], allowed: Self.kitImports).isEmpty)
        #expect(try Self.importIssues([(kitFile, Self.lines("let note = \"\(word) AppKit\""))], allowed: Self.kitImports).isEmpty)
        #expect(try Self.importIssues([(kitFile, Self.lines("let s = \"abc"))], allowed: Self.kitImports)
                == ["\(kitFile) leaves a comment or a string literal open: its imports cannot be read"])
        #expect(try Self.importIssues([(visionFile, Self.lines("\(word) ImageIO"))], allowed: Self.visionImports,
                                      never: Self.visionNever).isEmpty)
        #expect(Self.kitImports == ["Darwin", "Foundation"], "Kit's imports, no more and no fewer")
        #expect(Self.visionImports == ["CryptoKit", "ImageIO", "CoreText", "CoreGraphics", vision, "Foundation",
                                       "AsteriaScale" + "Kit"], "DoD 6's list, no more and no fewer")
        #expect(Self.visionNever == ["Carbon", "SwiftUI", "AppKit", ["ScreenCap", "tureKit"].joined()])
    }
}
