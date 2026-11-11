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
    /// the app never uses (its only capture is the screenshot-manager call
    /// in ScreenSource.swift, T21).
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
    /// lines. Two lines, and only those: the window-list copy call with
    /// which 20.2's overlay.windowServer self-test check reads its own
    /// panel's window record (layer, sharing state), moved from
    /// SelfTest.swift into a static helper of the capture file, and the one
    /// with which the screen source's instance finds the window under the
    /// cursor for the game (T29: owner pid, layer and bounds only). Neither
    /// query asks for a permission or raises a prompt.
    static let captureAllowList: [String: [String: Int]] = [
        ["CGWin", "dowList", "CopyWindowInfo"].joined(): [captureFile: 2],
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

    // MARK: What the game read may name

    /// What the game read may name (DoD 20.3 item 5; docs/MACOS.md T29):
    /// - of the window list's keys only the three the game read takes (the
    ///   owner's pid, the layer, the bounds) and the two of 20.2's own-window
    ///   record (sharing state, on screen); any other key of that family is
    ///   an issue on any line of any source, comments included, above all
    ///   the window's name, which needs Screen Recording and which T29 never
    ///   reads;
    /// - another process's arguments-and-environment record is named in
    ///   code only in the capture file, whose members other than the pinned
    ///   window-record read are instance members (the shape rule above), so
    ///   only a process whose capture is on reads one, and no test and no
    ///   self-test reads a real process's arguments or environment. Read as
    ///   code (comments and strings blanked), so the docs may name it; its
    ///   sysctl name (lower case, dotted after `kern`: the spelling that
    ///   `sysctlbyname` and `sysctlnametomib` take, `processRecordName`)
    ///   appears on no line of any source, the capture file's included,
    ///   which reads through the constant. The bare number behind the
    ///   constant is beyond a name scan.
    static let windowKeyPrefix = ["kCGWin", "dow"].joined()
    static let allowedWindowKeys = Set(["OwnerPID", "Layer", "Bounds", "SharingState", "IsOnscreen"]
        .map { windowKeyPrefix + $0 })
    static let processRecord = ["KERN_PROC", "ARGS"].joined()
    static let processRecordName = ["kern.proc", "args"].joined()
    static let gameReadIssue = "the game read"

    static func gameReadIssues(_ sources: [Source]) throws -> [String] {
        let key = try Regex(windowKeyPrefix + "[A-Za-z0-9_]*", as: Substring.self)
        var issues: [String] = []
        for source in sources {
            for (number, line) in source.lines.enumerated() {
                for match in line.matches(of: key) where !allowedWindowKeys.contains(String(match.output)) {
                    issues.append("\(gameReadIssue): \(match.output) in \(source.path):\(number + 1)")
                }
                if line.contains(processRecordName) {
                    issues.append("\(gameReadIssue): \(processRecordName) in \(source.path):\(number + 1)")
                }
            }
            let read = codeOnly(source.lines.joined(separator: "\n"))
            guard read.complete else {
                issues.append("\(gameReadIssue): \(source.path) \(unreadIssue): a comment or a string literal is left open")
                continue
            }
            if source.path != captureFile, read.code.contains(processRecord) {
                issues.append("\(gameReadIssue): \(processRecord) in the code of \(source.path)")
            }
        }
        return issues
    }

    @Test func theGameReadNamesOnlyItsKeysAndOnlyTheCaptureFileReadsAProcess() throws {
        let sources = try Self.swiftSources("Sources") + Self.swiftSources("Tests")
        for issue in try Self.gameReadIssues(sources) { Issue.record("\(issue)") }
        // The read really is there: the record and the three keys.
        let capture = try #require(sources.first { $0.path == Self.captureFile }, "no \(Self.captureFile)")
        let code = Self.codeOnly(capture.lines.joined(separator: "\n")).code
        for name in [Self.processRecord] + ["OwnerPID", "Layer", "Bounds"].map({ Self.windowKeyPrefix + $0 }) {
            #expect(code.contains(name), "\(Self.captureFile) does not read \(name)")
        }

        // Planted lines, each flagged by this rule alone, and what stays free.
        let kitFile = "Sources/AsteriaScaleKit/Planted.swift"
        let testFile = "Tests/AsteriaScaleKitTests/Planted.swift"
        let captureFile = ["Sources/AsteriaScale/Screen", "Source.swift"].joined()
        let name = ["kCGWin", "dowName"].joined(), owner = ["kCGWin", "dowOwnerName"].joined()
        let alpha = ["kCGWin", "dowAlpha"].joined(), record = ["KERN_PROCA", "RGS2"].joined()
        #expect(Self.allowedWindowKeys == Set(["kCGWindowOwnerPID", "kCGWindowLayer", "kCGWindowBounds",
                                               "kCGWindowSharingState", "kCGWindowIsOnscreen"]),
                "the window keys, no more and no fewer")
        #expect(Self.processRecord + "2" == record)
        func issues(_ path: String, _ line: String) throws -> [String] {
            try Self.gameReadIssues([(path, Self.lines(line))])
        }
        let flagged: [(String, String, String)] = [
            (captureFile, "let title = info[\(name) as String] as? String", name),
            (captureFile, "/// reads \(name) too", name),
            (kitFile, "let app = info[\(owner) as String]", owner),
            (testFile, "_ = \(alpha)", alpha),
            (kitFile, "// \(name)", name),
        ]
        for (path, line, key) in flagged {
            #expect(try issues(path, line) == ["\(Self.gameReadIssue): \(key) in \(path):1"], "\(line) in \(path)")
        }
        for path in [kitFile, testFile, "Sources/AsteriaScale/SelfTest.swift"] {
            #expect(try issues(path, "var mib: [Int32] = [CTL_KERN, \(record), pid]")
                    == ["\(Self.gameReadIssue): \(Self.processRecord) in the code of \(path)"], "\(path)")
        }
        #expect(try issues(captureFile, "var mib: [Int32] = [CTL_KERN, \(record), pid]").isEmpty)
        let recordName = ["kern.proca", "rgs2"].joined()
        #expect(Self.processRecordName + "2" == recordName)
        for (path, line) in [(kitFile, "sysctlbyname(\"\(recordName)\", &buffer, &size, nil, 0)"),
                             (captureFile, "sysctlnametomib(\"\(recordName)\", &mib, &count)"),
                             ("Sources/AsteriaScale/SelfTest.swift", "let name = \"\(recordName)\"")] {
            #expect(try issues(path, line) == ["\(Self.gameReadIssue): \(Self.processRecordName) in \(path):1"], "\(path)")
        }
        for line in ["/// the \(record) record", "let note = \"\(record)\"",
                     "let pid = info[\(Self.windowKeyPrefix)OwnerPID as String]",
                     "let layer = info[\(Self.windowKeyPrefix)Layer as String]"] {
            #expect(try issues(kitFile, line).isEmpty, "\(line)")
        }
    }

    /// How the capture file reads for the game (T29), pinned as code with
    /// white space made one space, since no check runs it (the Swift suite
    /// does not link the app, and no self-test builds a ScreenSource): the
    /// whole of `game(at:)`, so each of its steps is what review left it:
    /// the window list of on-screen windows without the desktop, read on one
    /// line; the owner Kit picks, with the one Wine check a window above
    /// layer 0 may pass (the executable's path through GameIdentity.isWine,
    /// no owner Wine's when it has none); another process's
    /// arguments-and-environment record read only for Wine (any other
    /// program's environment may hold secrets, and GameIdentity would not
    /// look at it); a drive's `dosdevices` link read, never followed. Beside
    /// it the record's reader is named three times in the file and nowhere
    /// else (its declaration, the guarded call, the argument label), so no
    /// call, stored reference or function value reads a record elsewhere;
    /// the reader is pinned whole too, and the record's constant is named
    /// once, so the one read of a record is that body's (a wrapper over a
    /// helper that another member calls is no reader); Kit's window rule is
    /// called once.
    static let gameWindowList = ["let list = ", "CGWin", "dowList", "CopyWindowInfo([.optionOnScreenOnly, ",
                                 ".excludeDesktopElements], kCGNullWindowID) as? [[String: Any]]"].joined()
    static let wineOwner = "let isWine: (Int32) -> Bool = { pid in ProcessTable.executablePath(pid).map(GameIdentity.isWine) "
        + "?? false } guard let owner = GameIdentity.windowOwner(at: point, windows: windows, ownPid: getpid(), "
        + "primaryHeight: Double(primary.height), isWine: isWine), let path = ProcessTable.executablePath(owner) "
        + "else { return nil }"
    static let wineOnlyRecord = "let record = GameIdentity.isWine(path) ? processArguments(owner) : nil"
    static let gameReturn = "return GameIdentity.game(executablePath: path, processArguments: record, "
        + "readLink: GameIdentity.readLink)"
    static let gameRead = ["func game(at point: GlobalPoint) async -> HoverGame? {", gameWindowList,
                           "let windows = (list ?? []).compactMap { windowRecord($0) }",
                           "let primary = CGDisplayBounds(CGMainDisplayID())", wineOwner, wineOnlyRecord, gameReturn, "}"]
        .joined(separator: " ")
    static let recordReader = ["private func processArguments(_ pid: pid_t) -> [UInt8]? {",
                               "var mib: [Int32] = [CTL_KERN, \(processRecord)2, pid]", "var size = 0",
                               "guard sysctl(&mib, 3, nil, &size, nil, 0) == 0, (1...(4 << 20)).contains(size) else { return nil }",
                               "var buffer = [UInt8](repeating: 0, count: size)",
                               "guard sysctl(&mib, 3, &buffer, &size, nil, 0) == 0, size <= buffer.count else { return nil }",
                               "return Array(buffer.prefix(size))", "}"].joined(separator: " ")
    static let recordReadIssue = "another process's record is read"

    static func processRecordIssues(_ sources: [Source]) throws -> [String] {
        guard let file = sources.first(where: { $0.path == captureFile }) else {
            return ["\(recordReadIssue) nowhere: no \(captureFile)"]
        }
        let read = collapsedCode(file.lines.joined(separator: "\n"))
        guard read.complete else { return ["\(captureFile) \(unreadIssue): a comment or a string literal is left open"] }
        let code = read.code
        let reads = code.ranges(of: gameRead).count
        let readers = code.ranges(of: recordReader).count
        let mentions = code.matches(of: try regex(#"\bprocessArguments\b"#)).count
        let owners = code.matches(of: try regex(#"\bwindowOwner\s*\("#)).count
        let records = code.ranges(of: processRecord).count
        guard reads == 1, readers == 1, mentions == 3, owners == 1, records == 1 else {
            return ["\(recordReadIssue) otherwise than in the pinned game read, once and for Wine only: game reads "
                    + "\(reads), pinned readers \(readers), processArguments \(mentions), windowOwner( \(owners), "
                    + "\(processRecord) \(records)"]
        }
        return []
    }

    @Test func anotherProcessIsReadOnlyForWine() throws {
        for issue in try Self.processRecordIssues(try Self.swiftSources("Sources")) { Issue.record("\(issue)") }

        // Planted capture files: the game read as it is, and each way it
        // could stop being Wine-only, once, from on-screen windows, with
        // the app's Wine check, or a reader of the link alone.
        let captureFile = ["Sources/AsteriaScale/Screen", "Source.swift"].joined()
        let record = ["KERN_PROCA", "RGS2"].joined()
        let readerBody = ["        var mib: [Int32] = [CTL_KERN, \(record), pid]", "        var size = 0",
                          "        guard sysctl(&mib, 3, nil, &size, nil, 0) == 0, (1...(4 << 20)).contains(size) else { return nil }",
                          "        var buffer = [UInt8](repeating: 0, count: size)",
                          "        guard sysctl(&mib, 3, &buffer, &size, nil, 0) == 0, size <= buffer.count else { return nil }",
                          "        return Array(buffer.prefix(size))", "    }"]
        let declaration = (["    private func processArguments(_ pid: pid_t) -> [UInt8]? {"] + readerBody).joined(separator: "\n")
        // The same read behind a one-line wrapper, in a helper another
        // member calls too.
        let helper = (["    private func processArguments(_ pid: pid_t) -> [UInt8]? { record(of: pid) }",
                       "    private func record(of pid: pid_t) -> [UInt8]? {"] + readerBody
                      + ["    private func probe(_ owners: [Int32]) -> [[UInt8]] { owners.compactMap { record(of: $0) } }"])
            .joined(separator: "\n")
        let wineCheck = "        let isWine: (Int32) -> Bool = { pid in ProcessTable.executablePath(pid).map(GameIdentity.isWine) ?? false }"
        let body = ["    func game(at point: GlobalPoint) async -> HoverGame? {", "        \(Self.gameWindowList)",
                    "        let windows = (list ?? []).compactMap { windowRecord($0) }",
                    "        let primary = CGDisplayBounds(CGMainDisplayID())", "        // the owner, Wine above layer 0", wineCheck,
                    "        guard let owner = GameIdentity.windowOwner(at: point, windows: windows, ownPid: getpid(),",
                    "                                                   primaryHeight: Double(primary.height), isWine: isWine),",
                    "              let path = ProcessTable.executablePath(owner) else { return nil }",
                    "        \(Self.wineOnlyRecord)", "        \(Self.gameReturn)", "    }"]
        func issues(_ lines: [String], _ tail: String = declaration) throws -> [String] {
            try Self.processRecordIssues([(captureFile, Self.lines((lines + [tail]).joined(separator: "\n")))])
                .map { $0.hasPrefix(Self.recordReadIssue) ? Self.recordReadIssue : $0 }
        }
        func replacing(_ old: String, _ new: String) -> [String] { body.map { $0.replacingOccurrences(of: old, with: new) } }
        #expect(try issues(body).isEmpty)
        #expect(try issues(replacing("? processArguments(owner) : nil", "?\n            processArguments(owner) : nil"))
                .isEmpty, "the guard over two lines")
        #expect(try issues(replacing("= { pid in ", "= {\n            pid in ")).isEmpty, "the Wine check over two lines")
        let member = "\n    private func probe(_ owners: [Int32]) -> [[UInt8]] { "
        let reverted: [(String, [String], String)] = [
            ("read for every program", replacing("GameIdentity.isWine(path) ? ", "").map {
                $0.replacingOccurrences(of: "(owner) : nil", with: "(owner)") }, declaration),
            ("the guard inverted", replacing("= GameIdentity.isWine(path)", "= !GameIdentity.isWine(path)"), declaration),
            ("a second read", body + ["        let all = processArguments(owner)"], declaration),
            ("a second record", body, declaration + "\n    let other: [Int32] = [CTL_KERN, \(record), 1]"),
            ("every window", replacing(".optionOnScreenOnly, ", ""), declaration),
            ("no window list", body.filter { !$0.contains("let list") }, declaration),
            ("read point-free", body, declaration + member + "owners.compactMap(processArguments) }"),
            ("a stored reader", body, declaration + "\n    private lazy var reader = processArguments"),
            ("a parenthesized call", body, declaration + member + "[(processArguments)(1)].compactMap { $0 } }"),
            ("a function value", body, declaration + member + "owners.compactMap(self.processArguments) }"),
            ("every owner above 0 Wine's", replacing("{ pid in ProcessTable.executablePath(pid).map(GameIdentity.isWine) ?? false }",
                                                     "{ _ in true }"), declaration),
            ("no owner above 0 Wine's", replacing("{ pid in ProcessTable.executablePath(pid).map(GameIdentity.isWine) ?? false }",
                                                  "{ _ in false }"), declaration),
            ("an owner without a path Wine's", replacing("?? false }", "?? true }"), declaration),
            ("a literal check at the call", replacing("isWine: isWine)", "isWine: { _ in true })"), declaration),
            ("no Wine at the call", replacing("isWine: isWine)", "isWine: { _ in false })"), declaration),
            ("a second window rule", body, declaration + "\n    private func other(_ windows: [GameIdentity.WindowRecord]) "
                + "-> Int32? { GameIdentity.windowOwner(at: GlobalPoint(x: 0, y: 0), windows: windows, ownPid: 1, "
                + "primaryHeight: 900, isWine: { _ in true }) }"),
            ("the link followed", replacing("readLink: GameIdentity.readLink)", "readLink: { ProcessTable.realPath($0) })"),
             declaration),
            ("another body", replacing("let primary = CGDisplayBounds(CGMainDisplayID())",
                                       "let primary = CGDisplayBounds(CGMainDisplayID())\n        if point.x < 0 { return nil }"),
             declaration),
            ("the read moved to a helper", body, helper),
            ("the reader keeps a copy", body, declaration.replacingOccurrences(
                of: "        return Array(buffer.prefix(size))",
                with: "        lastRecord = buffer\n        return Array(buffer.prefix(size))")),
        ]
        for (name, lines, tail) in reverted {
            #expect(try issues(lines, tail) == [Self.recordReadIssue], "\(name)")
        }
        #expect(try Self.processRecordIssues([("Sources/AsteriaScale/HoverDriver.swift", Self.lines("let a = 1"))])
                == ["\(Self.recordReadIssue) nowhere: no \(captureFile)"])
    }

    // MARK: The speaker editor pauses the tracking

    /// How the app pauses the tracking while the speaker editor is open
    /// (DoD 20.3 item 5, T31): the tracking decision takes the editor's
    /// state, opening re-decides right after the editor opens and closing
    /// right after the overlay resumes, and nothing else starts or stops the
    /// tracking. Every close goes that way: the editor is closed in two
    /// places only, that one and the quit (right before the tracking stops
    /// for good), and the overlay resumes in that one alone, so a save or a
    /// cancel cannot close the editor and leave the tracking off. Pinned as
    /// code with white space made one space: the self-test never tracks (no
    /// screen source exists in it) and the Swift suite does not link the
    /// app, so no run would see the pause go.
    static let appDelegateFile = "Sources/AsteriaScale/AppDelegate.swift"
    static let editorTracking = "let track = status.tracks(hoverEnabled: enabled, editorOpen: speakerEditor.isOpen) "
        + "hoverDriver.setTracking(track, pipeline: track ? screenPipeline() : nil)"
    static let editorOpens = "overlay.suspend() speakerEditor.open(editor, target: shown, takeKeyboard: takeKeyboard) "
        + "updateRecognition()"
    static let editorCloses = "speakerEditor.close() overlay.resume() updateRecognition()"
    static let shutdownStops = "speakerEditor.close() hoverDriver.setTracking(false, pipeline: nil)"
    static let pauseIssue = "the tracking pauses for the speaker editor"

    static func editorPauseIssues(_ sources: [Source]) throws -> [String] {
        guard let file = sources.first(where: { $0.path == appDelegateFile }) else {
            return ["\(pauseIssue) nowhere: no \(appDelegateFile)"]
        }
        let read = collapsedCode(file.lines.joined(separator: "\n"))
        guard read.complete else { return ["\(appDelegateFile) \(unreadIssue): a comment or a string literal is left open"] }
        let code = read.code
        let pinned = [editorTracking, editorOpens, editorCloses, shutdownStops].map { code.ranges(of: $0).count }
        let decisions = code.matches(of: try regex(#"\.\s*tracks\s*\("#)).count
        let switches = code.matches(of: try regex(#"\.\s*setTracking\s*\("#)).count
        let opens = code.matches(of: try regex(#"\bspeakerEditor\s*[?!]?\s*\.\s*open\s*\("#)).count
        let closes = code.matches(of: try regex(#"\bspeakerEditor\s*[?!]?\s*\.\s*close\s*\("#)).count
        // Qualified: a continuation's resume() is no overlay's.
        let resumes = code.matches(of: try regex(#"\boverlay\s*[?!]?\s*\.\s*resume\s*\("#)).count
        guard pinned == [1, 1, 1, 1], decisions == 1, switches == 2, opens == 1, closes == 2, resumes == 1 else {
            return ["\(pauseIssue) otherwise than through the editor's state on open and close: pinned \(pinned), "
                    + "decisions \(decisions), tracking switches \(switches), opens \(opens), closes \(closes), "
                    + "overlay resumes \(resumes)"]
        }
        return []
    }

    @Test func theSpeakerEditorPausesTheTracking() throws {
        for issue in try Self.editorPauseIssues(try Self.swiftSources("Sources")) { Issue.record("\(issue)") }

        let appDelegate = ["Sources/AsteriaScale/App", "Delegate.swift"].joined()
        #expect(Self.appDelegateFile == appDelegate)
        let update = ["        statusMenu.update(recognition: status)", "        // Paused while the editor is open.",
                      "        let track = status.tracks(hoverEnabled: enabled,", "                                  editorOpen: speakerEditor.isOpen)",
                      "        hoverDriver.setTracking(track, pipeline: track ? screenPipeline() : nil)"]
        let suspend = "            overlay.suspend()"
        let opening = "            speakerEditor.open(editor, target: shown, takeKeyboard: takeKeyboard)"
        let redecide = "            updateRecognition()", logged = "            log.write(\"speaker editor: open\")"
        let open = [suspend, opening, redecide, logged]
        let close = ["        speakerEditor.close()", "        overlay.resume()", "        updateRecognition()"]
        let quit = ["        speakerEditor.close()", "        hoverDriver.setTracking(false, pipeline: nil)"]
        func issues(_ parts: [[String]]) throws -> [String] {
            try Self.editorPauseIssues([(appDelegate, Self.lines(parts.flatMap { $0 }.joined(separator: "\n")))])
                .map { $0.hasPrefix(Self.pauseIssue) ? Self.pauseIssue : $0 }
        }
        #expect(try issues([update, open, close, quit]).isEmpty)
        func replacing(_ lines: [String], _ old: String, _ new: String) -> [String] {
            lines.map { $0.replacingOccurrences(of: old, with: new) }
        }
        let reverted: [(String, [[String]])] = [
            ("the editor's state ignored", [replacing(update, "speakerEditor.isOpen", "false"), open, close, quit]),
            ("the old decision", [replacing(replacing(update, "status.tracks(hoverEnabled: enabled,", "status.allowsTracking && enabled"),
                                            "                                  editorOpen: speakerEditor.isOpen)", ""), open, close, quit]),
            ("no re-decision on open", [update, open.filter { !$0.contains("updateRecognition") }, close, quit]),
            ("re-decided before opening", [update, [suspend, redecide, opening, logged], close, quit]),
            ("no re-decision on close", [update, open, close.filter { !$0.contains("updateRecognition") }, quit]),
            ("no resume on close", [update, open, close.filter { !$0.contains("resume") }, quit]),
            ("a second open", [update, open, close, quit, ["        speakerEditor.open(editor, target: t, takeKeyboard: true)"]]),
            ("tracking started elsewhere", [update, open, close, quit,
                                            ["        hoverDriver.setTracking(true, pipeline: screenPipeline())"]]),
            ("a second decision", [update, open, close, quit, ["        let again = status.tracks(hoverEnabled: true, editorOpen: false)"]]),
            ("the save closes on its own", [update, open, close, quit,
                                            ["        self.speakerEditor.close()", "        self.overlay.resume()"]]),
            ("the save closes without resuming", [update, open, close, quit, ["        self.speakerEditor.close()"]]),
            ("the cancel closes on its own", [update, open, close, quit,
                                              ["        speakerEditor.onCancel = { [weak self] in self?.speakerEditor.close(); "
                                               + "self?.overlay.resume() }"]]),
            ("the quit's close moved to the save", [update, open, close, Array(quit.dropFirst()),
                                                    ["        self.speakerEditor.close()"]]),
            ("resumed elsewhere", [update, open, close, quit, ["        overlay?.resume()"]]),
        ]
        for (name, parts) in reverted {
            #expect(try issues(parts) == [Self.pauseIssue], "\(name)")
        }
        #expect(try Self.editorPauseIssues([(Self.hoverDriverFile, Self.lines("let a = 1"))])
                == ["\(Self.pauseIssue) nowhere: no \(appDelegate)"])
    }

    // MARK: The speaker editor's panel and card

    /// What the speaker editor's panel decides that no run executes (T31):
    /// the self-test shows the panel without the keyboard and presses keys
    /// through the controller's handler, so whether the name field is being
    /// edited (a digit then is text) and the panel's key path are pinned as
    /// code. «Отмена»'s action and the controller's key handler are pinned
    /// whole, so a cancel by either only calls onCancel (AppDelegate's
    /// closeSpeakerEditor); `close()` is pinned whole, and of the window
    /// calls that close or hide a panel (`close`, `orderOut`,
    /// `performClose`, `setIsVisible`) the file names only that declaration
    /// and its one `orderOut` (AppDelegate's two calls of `close()` are
    /// editorPauseIssues'). A hide by another spelling (the alpha value, a
    /// frame off screen) is beyond these names.
    static let editorPanelFile = "Sources/AsteriaScale/SpeakerEditorPanel.swift"
    static let editingName = "var isEditingName: Bool { firstResponder is NSText }"
    static let panelKeys = "override func sendEvent(_ event: NSEvent) { if event.type == .keyDown, let onKey, "
        + "onKey(event.keyCode, isEditingName) { return } super.sendEvent(event) }"
    static let editorKeys = "func keyPressed(keyCode: UInt16, editingName: Bool) -> Bool { guard isOpen, let command = "
        + "SpeakerEditor.command(keyCode: keyCode, editingName: editingName) else { return false } guard !busy else "
        + "{ return true } switch command { case .choose(let choice): choose(choice) case .cancel: onCancel?() } return true }"
    static let cancelAction = "@objc private func cancelClicked() { guard !busy else { return } onCancel?() }"
    static let editorClose = "func close() { let reactivate = SpeakerEditor.appToReactivate(tookKeyboard: tookKeyboard, "
        + "appIsActive: NSApp.isActive, previousPid: previousApp?.processIdentifier, ownPid: getpid()) let previous = "
        + "previousApp editor = nil target = nil busy = false tookKeyboard = false previousApp = nil panel.orderOut(nil) "
        + "if let reactivate, let previous, previous.processIdentifier == reactivate, !previous.isTerminated { "
        + "NSApp.yieldActivation(to: previous) _ = previous.activate() } }"
    static let panelIssue = "the speaker editor's panel"

    static func editorPanelIssues(_ sources: [Source]) throws -> [String] {
        guard let file = sources.first(where: { $0.path == editorPanelFile }) else {
            return ["\(panelIssue) nowhere: no \(editorPanelFile)"]
        }
        let read = collapsedCode(file.lines.joined(separator: "\n"))
        guard read.complete else { return ["\(editorPanelFile) \(unreadIssue): a comment or a string literal is left open"] }
        let code = read.code
        let pinned = [editingName, panelKeys, editorKeys, cancelAction, editorClose].map { code.ranges(of: $0).count }
        let editing = code.matches(of: try regex(#"\bisEditingName\b"#)).count
        let hides = code.matches(of: try regex(#"\b(?:close|orderOut|performClose|setIsVisible)\s*\("#)).count
        guard pinned == [1, 1, 1, 1, 1], editing == 2, hides == 2 else {
            return ["\(panelIssue) decides otherwise: pinned \(pinned), isEditingName \(editing), closes and hides \(hides)"]
        }
        return []
    }

    /// What HoverDriver hands the speaker editor when a card of a hover over
    /// the screen shows (T31): that card's speaker, the game its hover sent,
    /// its point and its token, right after the card is recorded as shown,
    /// and nowhere else. The self-test's hover.observations checks the game,
    /// the point and that the token is the shown card's on a card without a
    /// speaker («Словарь»); the speaker itself only this pin sees.
    static let shownCardTarget = "app.cardShown(primary) app.speakerCardShown(SpeakerTarget(speaker: primary.speaker, "
        + "game: state.value.game, point: state.value.point), token: token)"
    static let targetIssue = "the speaker editor's card is recorded"
    /// A refused ⌃⌥G goes up as a notice, which supersedes no hover in
    /// flight (OverlayState.notice, T31): the one notice of AppDelegate.swift.
    static let refusalNotice = "overlay.showNotice(refusal.russian, near: point) return refusal"
    static let noticeIssue = "a refused speaker editor shows"

    static func speakerTargetIssues(_ sources: [Source]) throws -> [String] {
        guard let file = sources.first(where: { $0.path == hoverDriverFile }) else {
            return ["\(targetIssue) nowhere: no \(hoverDriverFile)"]
        }
        let read = collapsedCode(file.lines.joined(separator: "\n"))
        guard read.complete else { return ["\(hoverDriverFile) \(unreadIssue): a comment or a string literal is left open"] }
        let pinned = read.code.ranges(of: shownCardTarget).count
        let records = read.code.matches(of: try regex(#"\bspeakerCardShown\s*\("#)).count
        guard pinned == 1, records == 1 else {
            return ["\(targetIssue) otherwise than from the shown card: pinned \(pinned), records \(records)"]
        }
        return []
    }

    static func refusalNoticeIssues(_ sources: [Source]) throws -> [String] {
        guard let file = sources.first(where: { $0.path == appDelegateFile }) else {
            return ["\(noticeIssue) nowhere: no \(appDelegateFile)"]
        }
        let read = collapsedCode(file.lines.joined(separator: "\n"))
        guard read.complete else { return ["\(appDelegateFile) \(unreadIssue): a comment or a string literal is left open"] }
        let pinned = read.code.ranges(of: refusalNotice).count
        let notices = read.code.matches(of: try regex(#"\bshowNotice\s*\("#)).count
        guard pinned == 1, notices == 1 else {
            return ["\(noticeIssue) otherwise than as a notice: pinned \(pinned), notices \(notices)"]
        }
        return []
    }

    @Test func theEditorGetsTheShownCardAndClosesOneWay() throws {
        let sources = try Self.swiftSources("Sources")
        for issue in try Self.editorPanelIssues(sources) + Self.speakerTargetIssues(sources)
            + Self.refusalNoticeIssues(sources) { Issue.record("\(issue)") }

        let panelFile = ["Sources/AsteriaScale/SpeakerEditor", "Panel.swift"].joined()
        let hoverDriver = ["Sources/AsteriaScale/Hover", "Driver.swift"].joined()
        #expect(Self.editorPanelFile == panelFile && Self.hoverDriverFile == hoverDriver)
        let panel = ["    var isEditingName: Bool { firstResponder is NSText }",
                     "    override func sendEvent(_ event: NSEvent) {",
                     "        if event.type == .keyDown, let onKey, onKey(event.keyCode, isEditingName) { return }",
                     "        super.sendEvent(event)", "    }",
                     "    func keyPressed(keyCode: UInt16, editingName: Bool) -> Bool {",
                     "        guard isOpen, let command = SpeakerEditor.command(keyCode: keyCode, editingName: editingName) else {",
                     "            return false", "        }", "        guard !busy else { return true }", "        switch command {",
                     "        case .choose(let choice): choose(choice)", "        case .cancel: onCancel?()", "        }",
                     "        return true", "    }",
                     "    @objc private func cancelClicked() {", "        guard !busy else { return }", "        onCancel?()", "    }",
                     "    func close() {",
                     "        let reactivate = SpeakerEditor.appToReactivate(tookKeyboard: tookKeyboard, appIsActive: NSApp.isActive,",
                     "                                                       previousPid: previousApp?.processIdentifier, ownPid: getpid())",
                     "        let previous = previousApp", "        editor = nil", "        target = nil", "        busy = false",
                     "        tookKeyboard = false", "        previousApp = nil", "        panel.orderOut(nil)",
                     "        if let reactivate, let previous, previous.processIdentifier == reactivate, !previous.isTerminated {",
                     "            NSApp.yieldActivation(to: previous)", "            _ = previous.activate()", "        }", "    }"]
        func panelIssues(_ lines: [String]) throws -> [String] {
            try Self.editorPanelIssues([(panelFile, Self.lines(lines.joined(separator: "\n")))])
                .map { $0.hasPrefix(Self.panelIssue) ? Self.panelIssue : $0 }
        }
        func panelReplacing(_ old: String, _ new: String) -> [String] { panel.map { $0.replacingOccurrences(of: old, with: new) } }
        #expect(try panelIssues(panel).isEmpty)
        let panels: [(String, [String])] = [
            ("never editing the name", panelReplacing("{ firstResponder is NSText }", "{ false }")),
            ("always editing the name", panelReplacing("{ firstResponder is NSText }", "{ true }")),
            ("the key path without the field's state", panelReplacing("onKey(event.keyCode, isEditingName)",
                                                                       "onKey(event.keyCode, false)")),
            ("«Отмена» closes on its own", panelReplacing("        onCancel?()", "        close()")),
            ("«Отмена» hides on its own", panelReplacing("        onCancel?()", "        panel.orderOut(nil)")),
            ("the window closed", panel + ["    func dismiss() { panel.performClose(nil) }"]),
            ("«Отмена» makes the panel invisible", panelReplacing("        onCancel?()", "        panel.setIsVisible(false)")),
            ("Esc makes the panel invisible", panelReplacing("case .cancel: onCancel?()", "case .cancel: panel.setIsVisible(false)")),
            ("the hide moved from close() to «Отмена»", panel.filter { $0 != "        panel.orderOut(nil)" }.map {
                $0 == "        onCancel?()" ? "        panel.orderOut(nil)\n        onCancel?()" : $0 }),
            ("the hide moved out of close()", panel.filter { $0 != "        panel.orderOut(nil)" }
                + ["    func dismiss() { panel.orderOut(nil) }"]),
            ("the handler without the field's state", panelReplacing("editingName: editingName)", "editingName: false)")),
        ]
        for (name, lines) in panels {
            #expect(try panelIssues(lines) == [Self.panelIssue], "\(name)")
        }

        let shown = ["            app.cardShown(primary)", "            // What ⌃⌥G opens the editor on.",
                     "            app.speakerCardShown(SpeakerTarget(speaker: primary.speaker, game: state.value.game,",
                     "                                               point: state.value.point),",
                     "                                 token: token)"]
        func targetIssues(_ lines: [String]) throws -> [String] {
            try Self.speakerTargetIssues([(hoverDriver, Self.lines(lines.joined(separator: "\n")))])
                .map { $0.hasPrefix(Self.targetIssue) ? Self.targetIssue : $0 }
        }
        func shownReplacing(_ old: String, _ new: String) -> [String] { shown.map { $0.replacingOccurrences(of: old, with: new) } }
        #expect(try targetIssues(shown).isEmpty)
        let targets: [(String, [String])] = [
            ("no speaker", shownReplacing("speaker: primary.speaker", "speaker: nil")),
            ("no game", shownReplacing("game: state.value.game", "game: nil")),
            ("another token", shownReplacing("token: token)", "token: app.overlay.hoverToken)")),
            ("before the card is recorded", Array(shown.dropFirst()) + Array(shown.prefix(1))),
            ("a second record", shown + ["            app.speakerCardShown(SpeakerTarget(speaker: nil, game: nil, "
                                         + "point: state.value.point), token: token)"]),
        ]
        for (name, lines) in targets {
            #expect(try targetIssues(lines) == [Self.targetIssue], "\(name)")
        }

        let appDelegate = ["Sources/AsteriaScale/App", "Delegate.swift"].joined()
        let refused = ["            overlay.showNotice(refusal.russian,", "                               near: point)",
                       "            return refusal"]
        func noticeIssues(_ lines: [String]) throws -> [String] {
            try Self.refusalNoticeIssues([(appDelegate, Self.lines(lines.joined(separator: "\n")))])
                .map { $0.hasPrefix(Self.noticeIssue) ? Self.noticeIssue : $0 }
        }
        #expect(try noticeIssues(refused).isEmpty)
        for (name, lines) in [("a message that supersedes the hover", refused.map {
                                  $0.replacingOccurrences(of: "showNotice", with: "showMessage") }),
                              ("a second notice", refused + ["        overlay.showNotice(RU.speakerNone, near: point)"])] {
            #expect(try noticeIssues(lines) == [Self.noticeIssue], "\(name)")
        }
    }

    // MARK: Where the screen source is built

    /// Where the screen source is built and the capture gate decides (DoD 8,
    /// T22: built only where the decision is checked, once at launch). The
    /// initializer takes the gate's permit, which only an `.on` decision
    /// carries, so its one construction is the line of the constructor file
    /// that unwraps the decision; besides it only the initializer's own
    /// declaration in the capture file may spell it. A construction is
    /// matched on raw lines (comments too) in every spelling the scan knows:
    /// the type followed by `(` or `.init`, and the initializer's label
    /// after `init(` on any type (`.init(permit:`).
    static let constructorFile = "Sources/AsteriaScale/AppDelegate.swift"
    static let constructorLine = ["if case .on(let permit) = captureDecision { screenSource = Screen",
                                  "Source(permit: permit) }"].joined()
    static let declarationLine = ["in", "it(permit: CaptureGate.Permit) {"].joined()
    static let constructorPatterns = [[#"Screen"#, #"Source\s*(?:\(|\.\s*init\b)"#], [#"\bin"#, #"it\s*\(\s*permit\s*:"#]]
        .map { $0.joined() }

    /// The decision is read as code (`codeOnly`: no comments, no string
    /// contents) with every run of white space, line ends included, made
    /// one space, so a spelling split over lines or spaced is the same
    /// text. Over every source:
    /// - every reference to `decide` (called, qualified, `CaptureGate.self.`,
    ///   a typealias, unqualified in an extension, a function reference) is
    ///   one of two: its declaration in Kit's Capture.swift, pinned whole
    ///   with its one `Permit()`, and the statement of the constructor file,
    ///   pinned whole to this process's own traits and followed at once by
    ///   the construction line, so nothing can be appended to it;
    /// - `Permit` is named only in Capture.swift's struct declaration (pinned
    ///   whole, its initializer fileprivate), its `case on(Permit)` and
    ///   decide's `Permit()`, and in the capture file's initializer: no
    ///   conformance, typealias or extension of it elsewhere, and
    ///   Capture.swift spells no `.init` and no `Self(`, so no other permit;
    /// - `getppid` is named only as a call and never declared with `func`,
    ///   and `CommandLine`, `ProcessInfo` and `Bundle` only before a `.`, so
    ///   no declaration, parameter, closure parameter, pattern or generic
    ///   parameter takes their names (a type annotation such as `: Bundle`
    ///   trips the rule too: the scan cannot tell it from a shadow);
    /// - no source sets the environment (`setenv`, `putenv`, `unsetenv`,
    ///   `environ`, `_NSGetEnviron`) or binds a function to a symbol name
    ///   (`@_cdecl`, `@_silgen_name`, `@_dynamicReplacement`, `@_extern`);
    /// - a declaration of `captureDecision` is read as `let`/`var
    ///   captureDecision`, `captureDecision:` (an annotation, a parameter,
    ///   also a label or a ternary's `x : y`) or `captureDecision in` (a
    ///   closure parameter, a loop variable); there is exactly one, the
    ///   stored property, with no accessor block, observer, initial value
    ///   or attribute (a property wrapper). A binding the scan does not read
    ///   (a parenthesized closure parameter, a tuple or case pattern) could
    ///   hold no permit either. The pinned statement is the property's only write
    ///   of the forms counted: `=` other than `==` (also after `?` or `!`),
    ///   a tuple pattern before `=`, `&` (inout) and a key path. A write
    ///   could not turn capture on anyway: that takes a permit.
    /// So a literal parent pid, environment, bundle id or argument list
    /// reaches the decision in no spelling the scan reads; code that defeats
    /// the language (an unsafe cast or pointer write, a symbol looked up at
    /// run time) is beyond it.
    static let kitCaptureFile = "Sources/AsteriaScaleKit/Capture.swift"
    static let decideStatement = ["captureDecision = CaptureGate.de", "cide(arguments: CommandLine.arguments, ",
                                  "parentPid: getppid(), environment: ProcessInfo.processInfo.environment, ",
                                  "bundleId: Bundle.main.bundleIdentifier ?? \"\")"].joined()
    static let decideDeclaration = ["public static func de", "cide(arguments: [String], parentPid: Int32, ",
                                    "environment: [String: String], bundleId: String) -> Decision { if let reason = ",
                                    "offReason(arguments: arguments, parentPid: parentPid, environment: environment, ",
                                    "bundleId: bundleId) { return .off(reason) } return .on(Per", "mit()) }"].joined()
    static let permitDeclaration = ["public struct Per", "mit: Sendable, Hashable { fileprivate init() {} }"].joined()
    static let permitCase = ["case on(Per", "mit)"].joined()
    /// Where `Permit` may be named, and how often: the declaration, the case
    /// and decide's construction; the screen source's initializer.
    static let permitMentions = [kitCaptureFile: 3, captureFile: 1]
    /// The decision's one declaration, a stored property: no accessor block
    /// or observer and no initial value may follow it.
    static let decisionDeclaration = #"private\(set\) var captureDecision: CaptureGate\.Decision\?(?! ?[{=])"#

    /// `text` as `codeOnly` reads it, every run of white space made one
    /// space, and whether it was read to its end.
    static func collapsedCode(_ text: String) -> (code: String, complete: Bool) {
        let read = codeOnly(text)
        return (read.code.split(whereSeparator: \.isWhitespace).joined(separator: " "), read.complete)
    }

    static func regex(_ pattern: String) throws -> Regex<AnyRegexOutput> {
        try Regex(pattern).wordBoundaryKind(.simple)
    }

    /// The issues' first words, which the plants match.
    static let constructorIssue = "the screen source is built on"
    static let decideIssue = "the capture gate decides on"
    static let writeIssue = "captureDecision is written in"
    static let declarationIssue = "captureDecision is declared"
    static let permitIssue = "a permit is made in"
    static let shadowIssue = "what the decision reads is redeclared or set in"
    static let unreadIssue = "cannot be read as code"

    /// Every construction of `sources` beyond the one line and the
    /// declaration, and every decision, decision write, permit, shadowed
    /// trait and environment write beyond the pinned ones.
    static func gateIssues(_ sources: [Source]) throws -> [String] {
        let patterns = try constructorPatterns.map { try regex($0) }
        var constructors: [(path: String, line: Int, text: String)] = []
        for source in sources {
            for (number, line) in source.lines.enumerated() where patterns.contains(where: { line.contains($0) }) {
                constructors.append((source.path, number + 1, line.trimmingCharacters(in: .whitespaces)))
            }
        }
        var issues: [String] = []
        let expected = ["\(captureFile) \(declarationLine)", "\(constructorFile) \(constructorLine)"].sorted()
        if constructors.map({ "\($0.path) \($0.text)" }).sorted() != expected {
            issues.append("\(constructorIssue) \(constructors.map { "\($0.path):\($0.line)" }), "
                          + "not only on one line of \(constructorFile) that unwraps the gate's permit")
        }
        return issues + (try decisionIssues(sources))
    }

    static func decisionIssues(_ sources: [Source]) throws -> [String] {
        let decide = try regex(#"\bde"# + #"cide\b"#)
        // `!=` is a comparison: a `?` or `!` counts only right after the name.
        let writes = try [#"\bcaptureDecision(?:[?!]\s*|\s*)=(?!=)"#, #"\bcaptureDecision\b(?=[^(=]*\)\s*=(?!=))"#,
                          #"(?:^|[^&])&\s*(?:self\s*\.\s*)?captureDecision\b"#,
                          #"\\\s*(?:[A-Za-z_][A-Za-z0-9_]*\s*)?\.\s*captureDecision\b"#].map { try regex($0) }
        let declares = try regex(#"\b(?:let|var)\s+captureDecision\b|\bcaptureDecision\s*:|\bcaptureDecision\s+in\b"#)
        let declaration = try regex(decisionDeclaration)
        let wrapped = try regex(#"@[A-Za-z_][A-Za-z0-9_.]*(?:\([^)]*\))?\s+(?:[a-z]+(?:\(set\))?\s+)*var\s+captureDecision\b"#)
        let permit = try regex(#"\bPer"# + #"mit\b"#)
        let kitMints = try [#"\.\s*init\b"#, #"\bSelf\s*\("#].map { try regex($0) }
        let shadows = try [#"\bfunc\s+getppid\b"#, #"\bgetppid\b(?!\s*\()"#,
                           #"\b(?:CommandLine|ProcessInfo|Bundle)\b(?!\s*\.)"#,
                           #"\b(?:setenv|putenv|unsetenv|environ|_NSGetEnviron)\b"#,
                           #"@\s*_(?:cdecl|silgen_name|dynamicReplacement|extern)\b"#].map { try regex($0) }
        var issues: [String] = []
        var decides: [String: Int] = [:], written: [String: Int] = [:], named: [String: Int] = [:]
        var statements = 0, declarations = 0, stored = 0, declared = 0, permitPins = 0
        for source in sources {
            let read = collapsedCode(source.lines.joined(separator: "\n"))
            guard read.complete else {
                issues.append("\(source.path) \(unreadIssue): a comment or a string literal is left open")
                continue
            }
            let code = read.code
            let counts = (code.matches(of: decide).count, writes.reduce(0) { $0 + code.matches(of: $1).count },
                          code.matches(of: permit).count)
            if counts.0 > 0 { decides[source.path] = counts.0 }
            if counts.1 > 0 { written[source.path] = counts.1 }
            if counts.2 > 0 { named[source.path] = counts.2 }
            declared += code.matches(of: declares).count + code.matches(of: wrapped).count
            if source.path == constructorFile {
                statements = code.ranges(of: decideStatement + " " + constructorLine).count
                stored = code.matches(of: declaration).count
            }
            if source.path == kitCaptureFile {
                declarations = code.ranges(of: decideDeclaration).count
                permitPins = code.ranges(of: permitDeclaration).count * code.ranges(of: permitCase).count
                if kitMints.contains(where: { code.contains($0) }) { permitPins = 0 }
            }
            if shadows.contains(where: { code.contains($0) }) { issues.append("\(shadowIssue) \(source.path)") }
        }
        if decides != [kitCaptureFile: 1, constructorFile: 1] || statements != 1 || declarations != 1 {
            issues.append("\(decideIssue) \(decides.sorted { $0.key < $1.key }), not once in \(constructorFile) "
                          + "from this process's own traits besides its declaration")
        }
        if written != [constructorFile: 1] {
            issues.append("\(writeIssue) \(written.sorted { $0.key < $1.key }), not only by the decision")
        }
        if stored != 1 || declared != 1 {
            issues.append("\(declarationIssue) \(declared) times, \(stored) of them the stored property in \(constructorFile)")
        }
        if named != permitMentions || permitPins != 1 || declarations != 1 {
            issues.append("\(permitIssue) \(named.sorted { $0.key < $1.key }), not only by decide")
        }
        return issues
    }

    /// The package's sources; the tests call the pure decision freely and
    /// cannot build the screen source (they do not link the app).
    @Test func theScreenSourceIsBuiltOnlyWhereTheGateDecides() throws {
        let sources = try Self.swiftSources("Sources")
        #expect(sources.contains { $0.path == Self.constructorFile } && sources.contains { $0.path == Self.captureFile }
                && sources.contains { $0.path == Self.kitCaptureFile })
        for issue in try Self.gateIssues(sources) { Issue.record("\(issue)") }
    }

    // MARK: The capture file's shape

    /// What the capture file may declare, as code (T22: a process whose
    /// capture is off has no instance, so it has nothing to call the
    /// preflight, the request or the capture on). Besides its imports it
    /// declares the one class, with one initializer (the permit's), no
    /// nested or second type, nothing at file scope and no static or class
    /// member but `ownWindowRecord`, the window-record read 20.2's self-test
    /// check makes without an instance, pinned whole (white space made one
    /// space), so its body reads that one window record and nothing else;
    /// and no source extends the class: no extension names it before its
    /// `{` (module-qualified or in a `where` clause too) and no typealias
    /// stands for it (`= ScreenSource`, module-qualified too).
    static let captureClass = ["final class Screen", "Source: FrameSource, CapturePermission, GameResolver, ",
                               "@unchecked Sendable {"].joined()
    static let captureStatic = ["static func ownWindow", "Record(_ windowNumber: Int) -> ",
                                "(layer: Int?, sharingState: Int?, onscreen: Bool?) {"].joined()
    static let ownWindowRecord = [captureStatic, " guard let number = CGWindowID(exactly: windowNumber) else { return (nil, nil, nil) }",
                                  " let list = ", "CGWin", "dowList", "CopyWindowInfo([.optionIncludingWindow], number) as? [[String: Any]]",
                                  " let window = list?.first return (window?[kCGWindowLayer as String] as? Int, ",
                                  "window?[kCGWindowSharingState as String] as? Int, window?[kCGWindowIsOnscreen as String] as? Bool) }"]
        .joined()
    static let shapeIssue = "the capture file"
    static let extensionIssue = "the screen source is extended in"

    static func captureShapeIssues(_ sources: [Source]) throws -> [String] {
        var issues: [String] = []
        let extends = try regex(#"\bextension\b[^{]*\bScreen"# + #"Source\b"#)
        let aliases = try regex(#"\btypealias\s+[A-Za-z_][A-Za-z0-9_]*\s*(?:<[^>]*>)?\s*=\s*(?:[A-Za-z_][A-Za-z0-9_]*\s*\.\s*)*"#
                                + #"Screen"# + #"Source\b"#)
        for source in sources {
            let read = collapsedCode(source.lines.joined(separator: "\n"))
            if read.code.contains(extends) || read.code.contains(aliases) { issues.append("\(extensionIssue) \(source.path)") }
        }
        guard let file = sources.first(where: { $0.path == captureFile }) else {
            return issues + ["\(shapeIssue) \(captureFile) is missing"]
        }
        let read = codeOnly(file.lines.joined(separator: "\n"))
        guard read.complete else { return issues + ["\(captureFile) \(unreadIssue): a comment or a string literal is left open"] }
        let lines = read.code.split(separator: "\n").map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
        let statics = try lines.filter { $0.contains(try regex(#"\bstatic\b"#)) }
        let records = read.code.split(whereSeparator: \.isWhitespace).joined(separator: " ").ranges(of: ownWindowRecord).count
        if statics != [captureStatic] || records != 1 {
            issues.append("\(shapeIssue) has the static members \(statics), the pinned window-record read \(records) times")
        }
        let types = try lines.filter {
            $0.contains(try regex(#"\b(?:class|struct|enum|actor|protocol|extension|typealias)\b"#))
        }
        if types != [captureClass] { issues.append("\(shapeIssue) declares the types \(types)") }
        let inits = try lines.filter { $0.contains(try regex(#"\binit\b"#)) }
        if inits != [declarationLine] { issues.append("\(shapeIssue) has the initializers \(inits)") }
        // File scope: what lies outside every brace, line by line.
        var depth = 0
        var top = ""
        for character in read.code {
            switch character {
            case "{":
                if depth == 0 { top.append(character) }
                depth += 1
            case "}":
                depth -= 1
                if depth == 0 { top.append(character) }
            default:
                if depth == 0 || character == "\n" { top.append(character) }
            }
            if depth < 0 { break }
        }
        let importLine = try Regex(["^im", #"port [A-Za-z_][A-Za-z0-9_]*$"#].joined())
        let scope = top.split(separator: "\n").map { $0.trimmingCharacters(in: .whitespaces) }
            .filter { !$0.isEmpty && $0.wholeMatch(of: importLine) == nil }
        if depth != 0 || scope != [captureClass, "}"] { issues.append("\(shapeIssue) holds at file scope \(scope)") }
        return issues
    }

    @Test func theCaptureFileReachesTheCaptureOnlyThroughAnInstance() throws {
        for issue in try Self.captureShapeIssues(try Self.swiftSources("Sources")) { Issue.record("\(issue)") }
    }

    // MARK: The end of a hover over the screen

    /// How HoverDriver ends a hover over the screen (DoD 20.3 item 7, T37):
    /// through `HoverReport.afterScreenHover` with the fresh cursor re-check,
    /// asked before the app hears of a refused capture (which may stop the
    /// tracking, after which no anchor is current), and the overlay told
    /// last, so stopping the tracking cannot hide the end message. Pinned as
    /// code with white space made one space: the Swift suite does not link
    /// the app, so the call itself is what a reverted re-check would change.
    static let hoverDriverFile = "Sources/AsteriaScale/HoverDriver.swift"
    static let endReport = "let report = HoverReport.afterScreenHover(result.outcome, card: shown.card, cursorStillThere: mayShow())"
    static let endRefusal = "app.screenHoverEnded(result.outcome)"
    static let endFinish = "app.overlay.finish(report.overlay, token: token, near: near)"
    static let endIssue = "the hover over the screen ends"

    static func screenHoverEndIssues(_ sources: [Source]) throws -> [String] {
        guard let file = sources.first(where: { $0.path == hoverDriverFile }) else {
            return ["\(endIssue) nowhere: no \(hoverDriverFile)"]
        }
        let read = collapsedCode(file.lines.joined(separator: "\n"))
        guard read.complete else { return ["\(hoverDriverFile) \(unreadIssue): a comment or a string literal is left open"] }
        let code = read.code
        let steps = [endReport, endRefusal, endFinish].map { code.ranges(of: $0) }
        let reports = code.matches(of: try regex(#"\bHoverReport\s*\.\s*after"#)).count
        let rechecks = code.matches(of: try regex(#"\bcursorStillThere\b"#)).count
        let finishes = code.matches(of: try regex(#"\.\s*finish\s*\("#)).count
        guard steps.allSatisfy({ $0.count == 1 }), reports == 1, rechecks == 1, finishes == 1,
              let report = steps[0].first, let refusal = steps[1].first, let finish = steps[2].first,
              report.upperBound <= refusal.lowerBound, refusal.upperBound <= finish.lowerBound else {
            return ["\(endIssue) otherwise than through one re-checked report, then the refusal, then the overlay: "
                    + "steps \(steps.map(\.count)), reports \(reports), re-checks \(rechecks), finishes \(finishes)"]
        }
        return []
    }

    /// What the app carries out of OverlayState that no Kit test can see:
    /// the tracker's hideCard goes through `trackerHide` (which leaves a ⌥V
    /// or «Проверка перевода» card alone), never a plain hide, and a card
    /// clears an earlier card's phrase line (T37).
    static let overlayFile = "Sources/AsteriaScale/Overlay.swift"
    static let trackerHideCase = "case .hideCard: app.overlay.trackerHide() case"
    static let primaryClearsPhrase = "model.badge = primary.badge model.phrase = \"\""
    static let wiringIssue = "the overlay's decisions are carried out"

    static func overlayWiringIssues(_ sources: [Source]) throws -> [String] {
        var issues: [String] = []
        for (path, pinned) in [(hoverDriverFile, trackerHideCase), (overlayFile, primaryClearsPhrase)] {
            guard let file = sources.first(where: { $0.path == path }) else {
                issues.append("\(wiringIssue) nowhere: no \(path)")
                continue
            }
            let read = collapsedCode(file.lines.joined(separator: "\n"))
            let plainHides = path == hoverDriverFile ? read.code.matches(of: try regex(#"\.\s*hide\s*\("#)).count : 0
            if !read.complete || read.code.ranges(of: pinned).count != 1 || plainHides != 0 {
                issues.append("\(wiringIssue) otherwise in \(path): «\(pinned)» not once, or \(plainHides) plain hides")
            }
        }
        return issues
    }

    @Test func aScreenHoverEndsThroughOneReCheckedReport() throws {
        let sources = try Self.swiftSources("Sources")
        for issue in try Self.screenHoverEndIssues(sources) + Self.overlayWiringIssues(sources) { Issue.record("\(issue)") }
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
        #expect(Set(Self.captureAllowList.keys) == [allowed] && Self.captureAllowList[allowed] == [captureFile: 2])
        #expect(Self.captureIssues([(captureFile, Self.lines("let list = \(allowed)([], 1)\nlet game = \(allowed)([], 0)"))],
                                   exactAllowance: true).isEmpty)
        #expect(Self.captureIssues([(captureFile, Self.lines("let list = \(allowed)([], 1)"))], exactAllowance: true)
                == ["\(allowed) on 1 lines of \(captureFile), the allow-list says 2"], "one of the two lines went away")
        #expect(Self.captureIssues([(kitFile, Self.lines("let list = \(allowed)([], 1)"))], exactAllowance: false)
                == ["\(windowList) in \(kitFile):1"], "outside its file")
        #expect(Self.captureIssues([(selfTest, Self.lines("let list = \(allowed)([], 1)"))], exactAllowance: false)
                == ["\(windowList) in \(selfTest):1"], "the self-test reads it through the capture file now")
        #expect(Self.captureIssues([(captureFile, Self.lines("let a = \(allowed)([], 1)\nlet b = \(allowed)([], 2)\n"
                                                                + "let c = \(allowed)([], 3)"))],
                                   exactAllowance: true) == ["\(allowed) on 3 lines of \(captureFile), the allow-list says 2"])
        #expect(Self.captureIssues([(captureFile, Self.lines("let a = 1"))], exactAllowance: true)
                == ["\(allowed) on 0 lines of \(captureFile), the allow-list says 2"],
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
        // this process's own traits, on one line or several, right before it.
        let appDelegate = ["Sources/AsteriaScale/App", "Delegate.swift"].joined()
        let hoverDriver = ["Sources/AsteriaScale/Hover", "Driver.swift"].joined()
        let kitCapture = ["Sources/AsteriaScaleKit/Cap", "ture.swift"].joined()
        let source = ["Screen", "Source"].joined()
        let decideWord = ["de", "cide"].joined()
        let permitWord = ["Per", "mit"].joined()
        let declared: Source = (captureFile, Self.lines(["    in", "it(permit: CaptureGate.\(permitWord)) {"].joined()))
        let decideLines = [
            "    public static func \(decideWord)(arguments: [String], parentPid: Int32, environment: [String: String],",
            "                              bundleId: String) -> Decision {",
            "        if let reason = offReason(arguments: arguments, parentPid: parentPid, environment: environment,",
            "                                  bundleId: bundleId) {",
            "            return .off(reason)",
            "        }",
            "        return .on(\(permitWord)())",
            "    }",
        ]
        let kitDeclared: Source = (kitCapture, Self.lines(([
            "public enum CaptureGate {",
            "    public struct \(permitWord): Sendable, Hashable {",
            "        fileprivate init() {}",
            "    }",
            "    public enum Decision: Sendable, Hashable {",
            "        case on(\(permitWord))",
            "        case off(Reason)",
            "    }",
        ] + decideLines + ["}"]).joined(separator: "\n")))
        let arguments = "CommandLine.arguments", parent = "getppid()", environment = "ProcessInfo.processInfo.environment"
        let bundle = "Bundle.main.bundleIdentifier ?? \"\""
        func decided(_ arguments: String, _ parent: String, _ environment: String, _ bundle: String) -> String {
            ["        captureDecision = CaptureGate.\(decideWord)(arguments: \(arguments), parentPid: \(parent),\n",
             "                                             environment: \(environment),\n",
             "                                             bundleId: \(bundle))"].joined()
        }
        let decision = decided(arguments, parent, environment, bundle)
        let built = "        if case .on(let permit) = captureDecision { screenSource = \(source)(permit: permit) }"
        let stored = "    private(set) var captureDecision: CaptureGate.Decision?"
        /// The constructor file: the stored declaration, then `body`.
        func app(_ body: String) -> Source { (appDelegate, Self.lines("\(stored)\n\(body)")) }
        #expect(Self.constructorFile == appDelegate && Self.kitCaptureFile == kitCapture)
        #expect(Self.collapsedCode(decision).code == Self.decideStatement)
        #expect(Self.collapsedCode(decideLines.joined(separator: "\n")).code == Self.decideDeclaration)
        #expect(Self.permitMentions == [kitCapture: 3, captureFile: 1])
        let base: [Source] = [app("\(decision)\n\(built)"), declared, kitDeclared]
        #expect(try Self.gateIssues(base).isEmpty)
        #expect(try Self.gateIssues([app("        \(Self.decideStatement)\n\(built)"), declared, kitDeclared])
                .isEmpty, "the decision on one line")
        let issuePrefixes = [Self.constructorIssue, Self.decideIssue, Self.writeIssue, Self.declarationIssue, Self.permitIssue,
                             Self.shadowIssue]
        func gate(_ sources: [Source]) throws -> [String] {
            try Self.gateIssues(sources).map { issue in
                issuePrefixes.first(where: { issue.hasPrefix($0) }) ?? (issue.contains(Self.unreadIssue) ? Self.unreadIssue : issue)
            }
        }
        /// `base` with `line` added to `path` (a new file when it is none of them).
        func with(_ path: String, _ line: String) -> [Source] {
            var sources = base
            if let index = sources.firstIndex(where: { $0.path == path }) {
                sources[index].lines += Self.lines(line)
            } else {
                sources.append((path, Self.lines(line)))
            }
            return sources
        }
        #expect(try gate([(appDelegate, Self.lines("let a = 1")), kitDeclared])
                == [Self.constructorIssue, Self.decideIssue, Self.writeIssue, Self.declarationIssue, Self.permitIssue],
                "neither is spelled, nor the declaration and the initializer")
        #expect(try gate([app("\(decision)\n\(built)"), kitDeclared]) == [Self.constructorIssue, Self.permitIssue],
                "no initializer")
        let constructions: [(path: String, line: String, also: [String])] = [
            (selfTest, "let s = \(source)(permit: p)", []),
            (hoverDriver, "_ = \(source).init(permit: p)", []),
            (hoverDriver, "let s: \(source) = .init(permit: p)", []),
            (hoverDriver, "let s = \(source) (permit: p)", []),
            (hoverDriver, "let make = \(source).init", []),
            (hoverDriver, "let s = \(source)()", []),
            (captureFile, ["    convenience in", "it(permit: CaptureGate.\(permitWord), n: Int) { self.init(permit: permit) }"].joined(),
             [Self.permitIssue]),
            (appDelegate, "// \(source)(permit:) once", []),
        ]
        for item in constructions {
            #expect(try gate(with(item.path, item.line)) == [Self.constructorIssue] + item.also, "\(item.line) in \(item.path)")
        }
        #expect(try gate([app("\(decision)\nscreenSource = \(source)(permit: permit)"), declared, kitDeclared])
                == [Self.constructorIssue, Self.decideIssue],
                "a construction that does not unwrap the decision's permit, and so does not follow the decision")
        #expect(try gate(with(selfTest, decision)) == [Self.decideIssue, Self.writeIssue], "a second decision")
        #expect(try gate([(selfTest, Self.lines(decision)), app(built), declared, kitDeclared])
                == [Self.decideIssue, Self.writeIssue], "the decision in another file")
        for (name, literal) in [
            ("arguments", decided("[\"AsteriaScale\"]", parent, environment, bundle)),
            ("parent", decided(arguments, "1", environment, bundle)),
            ("environment", decided(arguments, parent, "[\"__CFBundleIdentifier\": AppIdentity.bundleIdentifier]", bundle)),
            ("bundle", decided(arguments, parent, environment, "AppIdentity.bundleIdentifier")),
        ] {
            #expect(try gate([app("\(literal)\n\(built)"), declared, kitDeclared]) == [Self.decideIssue],
                    "a literal \(name) in the decision")
        }
        #expect(try gate([app("\(decision) ?? .off(.selfTest)\n\(built)"), declared, kitDeclared])
                == [Self.decideIssue], "something appended to the decision")
        // The decision in every other spelling: each is one more reference
        // to the function, wherever and however it is written.
        let traits = "arguments: [], parentPid: 1, environment: [:], bundleId: \"\""
        let spellings: [(path: String, line: String)] = [
            (appDelegate, "_ = CaptureGate.self.\(decideWord)(\(traits))"),
            (appDelegate, "_ = CaptureGate .\(decideWord)(\(traits))"),
            (appDelegate, "_ = CaptureGate.\(decideWord) (\(traits))"),
            (appDelegate, "_ = CaptureGate\n    .\(decideWord)(\(traits))"),
            (appDelegate, "let gate = CaptureGate.\(decideWord)"),
            (hoverDriver, "typealias Gate = CaptureGate\nlet late = Gate.\(decideWord)(\(traits))"),
            (kitCapture, "extension CaptureGate { static func again() -> Decision { \(decideWord)(\(traits)) } }"),
            ("Sources/AsteriaScaleKit/Planted.swift", "let early = CaptureGate.\(decideWord)(\(traits))"),
        ]
        for item in spellings {
            #expect(try gate(with(item.path, item.line)) == [Self.decideIssue], "\(item.line) in \(item.path)")
        }
        // Every other write to the decision, in each form the scan counts.
        for line in ["captureDecision = .off(.selfTest)", "self.captureDecision = nil", "swap(&captureDecision, &other)",
                     "swap(&self.captureDecision, &other)", "self[keyPath: \\.captureDecision] = nil",
                     "let path = \\AppDelegate.captureDecision", "captureDecision? = .off(.selfTest)",
                     "self.captureDecision! = .off(.selfTest)", "(captureDecision, x) = (y, 0)",
                     "(x, self.captureDecision) = (0, y)", "((captureDecision, a), b) = ((y, 0), 1)"] {
            #expect(try gate(with(appDelegate, line)) == [Self.writeIssue], "\(line)")
        }
        // The declaration: one, stored, with no accessors or observers.
        for (name, sources) in [
            ("a computed decision", [(appDelegate, Self.lines("\(stored) { get { nil } set { } }\n\(decision)\n\(built)")), declared,
                                     kitDeclared]),
            ("an observed decision", [(appDelegate, Self.lines("\(stored) { didSet { } }\n\(decision)\n\(built)")), declared,
                                      kitDeclared]),
            ("no declaration", [(appDelegate, Self.lines("\(decision)\n\(built)")), declared, kitDeclared]),
            ("a second declaration", with(hoverDriver, "    var captureDecision: CaptureGate.Decision? = nil")),
            ("a local one", with(appDelegate, "    func probe() { var captureDecision: CaptureGate.Decision? }")),
            ("a parameter", with(appDelegate, "    func probe(captureDecision: CaptureGate.Decision?) { }")),
            ("a closure parameter", with(appDelegate, "    let probe = { (captureDecision: Int) in captureDecision }")),
            ("an unnamed closure parameter", with(appDelegate, "    let probe = [1].map { captureDecision in captureDecision }")),
            ("a loop variable", with(appDelegate, "    func probe() { for captureDecision in [1] { _ = captureDecision } }")),
            ("an optional binding", with(appDelegate, "    func probe() { if let captureDecision { _ = captureDecision } }")),
            ("a wrapped decision", [(appDelegate, Self.lines("    @Wrapper\n\(stored)\n\(decision)\n\(built)")), declared,
                                    kitDeclared]),
            ("an attribute with arguments", [(appDelegate, Self.lines("    @Wrapper(1) \(stored.trimmingCharacters(in: .whitespaces))"
                                                                  + "\n\(decision)\n\(built)")), declared, kitDeclared]),
            ("a lazy decision", [(appDelegate, Self.lines("    private(set) lazy var captureDecision: CaptureGate.Decision? = nil"
                                                      + "\n\(decision)\n\(built)")), declared, kitDeclared]),
        ] as [(String, [Source])] {
            #expect(try gate(sources) == [Self.declarationIssue], "\(name)")
        }
        // A permit made, or its type named, anywhere but its three places.
        for item in [(kitCapture, "    static let spare = \(permitWord)()"), (kitCapture, "    static let spare: Decision = .on(.init())"),
                     (kitCapture, "extension CaptureGate.\(permitWord) { static let spare = Self() }"),
                     (kitCapture, "    static let spare = \(permitWord).self.init()"),
                     ("Sources/AsteriaScaleKit/Planted.swift", "let spare = CaptureGate.\(permitWord) ()"),
                     (kitCapture, "extension CaptureGate { typealias Proof = \(permitWord) }"),
                     (kitCapture, "fileprivate protocol Mintable { init() }\nextension CaptureGate.\(permitWord): Mintable {}"),
                     ("Sources/AsteriaScaleKit/Planted.swift", "extension CaptureGate.\(permitWord) { init(spare: Int) {} }"),
                     (hoverDriver, "func keep(_ proof: CaptureGate.\(permitWord)) { }")] {
            #expect(try gate(with(item.0, item.1)) == [Self.permitIssue], "\(item.1) in \(item.0)")
        }
        for (name, sources) in [
            ("an open initializer", [app("\(decision)\n\(built)"), declared,
                                     (kitCapture, Self.lines(kitDeclared.lines.joined(separator: "\n")
                                         .replacingOccurrences(of: "fileprivate init() {}", with: "public init() {}")))]),
            ("a mintable permit", [app("\(decision)\n\(built)"), declared,
                                   (kitCapture, Self.lines(kitDeclared.lines.joined(separator: "\n")
                                       .replacingOccurrences(of: "\(permitWord): Sendable, Hashable {",
                                                             with: "\(permitWord): Sendable, Hashable, Mintable {")))]),
        ] as [(String, [Source])] {
            #expect(try gate(sources) == [Self.permitIssue], "\(name)")
        }
        // What the decision reads, redeclared or shadowed in any binding, the
        // environment set, and a function bound to a symbol name.
        for line in ["func getppid() -> Int32 { 1 }", "private let ProcessInfo = Fake()", "struct CommandLine { }",
                     "extension Bundle { }", "typealias Bundle = Fake", "var getppid: () -> Int32 = { 1 }",
                     "private func launch(_ getppid: () -> Int32 = { 1 }) { }", "let parents = [1].map { getppid in getppid }",
                     "let (getppid, other) = ({ Int32(1) }, 0)", "for getppid in [{ Int32(1) }] { _ = getppid() }",
                     "func launch<ProcessInfo: FakeInfo>(_ info: ProcessInfo.Type) { }", "extension Foundation.Bundle { }",
                     "protocol Launched { associatedtype CommandLine }", "func launch(_ info: (CommandLine: Int, b: Int)) { }",
                     "setenv(\"__CFBundleIdentifier\", \"x\", 1)", "_ = putenv(entry)", "unsetenv(\"X\")",
                     "_NSGetEnviron().pointee = nil", "environ[0] = nil",
                     "@_cdecl(\"getppid\") func parent() -> Int32 { 1 }", "@_silgen_name(\"getppid\") func parent() -> Int32",
                     "@_dynamicReplacement(for: launch) func again() { }", "@_extern(c, \"getppid\") func parent() -> Int32"] {
            #expect(try gate(with(appDelegate, line)) == [Self.shadowIssue], "\(line)")
        }
        #expect(try gate(with("Sources/AsteriaScaleKit/Planted.swift", "let s = \"abc")) == [Self.unreadIssue])
        // Reads stay free.
        for line in ["_ = \(source).ownWindowRecord(1)", "if captureDecision == nil { return }",
                     "let on = captureDecision?.isOn == true && captureDecision != nil",
                     "let flag = ready && captureDecision != nil", "if (captureDecision) != nil { }",
                     "let some = captureDecision ?? .off(.selfTest)", "log.write(\"\\(captureDecision.map(String.init(describing:)) ?? \"undecided\")\")",
                     "let decided = app.captureDecision", "let reason = CaptureGate.offReason(\(traits))",
                     "// CaptureGate.\(decideWord)(\(traits)) in a comment",
                     "let note = \"CaptureGate.\(decideWord)(arguments:)\"", "let decided = 1; let decides = 2",
                     "let parent = getppid()", "let id = Bundle.main.bundleIdentifier", "let args = CommandLine.arguments",
                     "let env = ProcessInfo.processInfo.environment", "let url = Foundation.Bundle.main.bundleURL",
                     "let note = \"\(permitWord) environ getppid Bundle\"", "let environment = 1; let permitted = true"] {
            #expect(try gate(with(appDelegate, line)).isEmpty, "\(line)")
        }

        // The capture file reaches the capture only through an instance:
        // no static but the window-record read, pinned whole, no type but
        // the class, one initializer, nothing at file scope, no extension
        // anywhere.
        let preflightCall = ["CGPreflightScreen", "CaptureAccess()"].joined()
        let shareableCall = ["SCShareable", "Content.current"].joined()
        let recordLines = [
            "    " + Self.captureStatic,
            "        guard let number = CGWindowID(exactly: windowNumber) else { return (nil, nil, nil) }",
            "        let list = \(windowList)CopyWindowInfo([.optionIncludingWindow], number) as? [[String: Any]]",
            "        let window = list?.first",
            "        return (window?[kCGWindowLayer as String] as? Int, window?[kCGWindowSharingState as String] as? Int,",
            "                window?[kCGWindowIsOnscreen as String] as? Bool)",
            "    }",
        ]
        #expect(Self.collapsedCode(recordLines.joined(separator: "\n")).code == Self.ownWindowRecord)
        /// The capture file with `inside` added to the class, `after` after
        /// it and `inRecord` added to the window-record read's body.
        func shapeFile(_ inside: [String], _ after: [String], inRecord: [String] = []) -> Source {
            var lines: [String] = [
                "im" + "port AppKit", "im" + "port " + ["ScreenCap", "tureKit"].joined(),
                "/// The capture.", Self.captureClass,
                "    " + Self.declarationLine, "    }",
                "    func isGranted() -> Bool { \(preflightCall) }",
                "    deinit { }",
                "    private func failure(_ error: any Error) -> CaptureFailure { .ownAppMissing }",
            ]
            lines += recordLines.prefix(4)
            lines += inRecord
            lines += recordLines.dropFirst(4)
            lines += inside
            lines.append("}")
            lines += after
            return (captureFile, Self.lines(lines.joined(separator: "\n")))
        }
        #expect(try Self.captureShapeIssues([shapeFile([], [])]).isEmpty)
        #expect(try Self.captureShapeIssues([shapeFile(["    func again() -> Bool { isGranted() && \(preflightCall) }"], [])])
                .isEmpty, "an instance member")
        func shape(_ inside: [String], _ after: [String] = [], inRecord: [String] = [], also: [Source] = []) throws -> [String] {
            try Self.captureShapeIssues([shapeFile(inside, after, inRecord: inRecord)] + also).map { issue in
                issue.hasPrefix(Self.extensionIssue) ? Self.extensionIssue
                    : issue.hasPrefix(Self.shapeIssue + " has the static") ? "static"
                    : issue.hasPrefix(Self.shapeIssue + " declares") ? "type"
                    : issue.hasPrefix(Self.shapeIssue + " has the initializers") ? "init"
                    : issue.hasPrefix(Self.shapeIssue + " holds at file scope") ? "scope" : issue
            }
        }
        #expect(try shape(["    static func preflight() -> Bool { \(preflightCall) }"]) == ["static"])
        #expect(try shape(["    static func probe() async throws -> Int { try await \(shareableCall).displays.count }"])
                == ["static"])
        #expect(try shape(["    nonisolated static func preflight() -> Bool { \(preflightCall) }"]) == ["static"])
        #expect(try shape(["    static", "    func preflight() -> Bool { \(preflightCall) }"]) == ["static"])
        // Inside the one static: anything but its window-record read.
        for inRecord in [["        _ = \(preflightCall)"], ["        _ = " + ["CGRequestScreen", "CaptureAccess()"].joined()],
                         ["        Task { _ = try? await \(shareable).excludingDesktopWindows(false, onScreenWindowsOnly: false) }"],
                         ["        func probe() -> Bool { \(preflightCall) }", "        _ = probe()"],
                         ["        _ = { \(preflightCall) }()"], ["        let other = 1"]] {
            #expect(try shape([], inRecord: inRecord) == ["static"], "\(inRecord)")
        }
        #expect(try Self.captureShapeIssues([(captureFile, Self.lines(shapeFile([], []).lines.joined(separator: "\n")
            .replacingOccurrences(of: ".optionIncludingWindow", with: ".optionAll")))]).count == 1, "another window-list query")
        #expect(try shape(["    class var granted: Bool { \(preflightCall) }"]) == ["type"])
        #expect(try shape(["    struct Probe { func go() -> Bool { \(preflightCall) } }"]) == ["type"])
        #expect(try shape(["    convenience init(other: Int) { fatalError() }"]) == ["init"])
        #expect(try shape([], ["func preflight() -> Bool { \(preflightCall) }"]) == ["scope"])
        #expect(try shape([], ["    func preflight() -> Bool { \(preflightCall) }"]) == ["scope"], "indented at file scope")
        #expect(try shape([], ["let granted = \(preflightCall)"]) == ["scope"])
        #expect(try shape([], ["struct Probe { func go() -> Bool { \(preflightCall) } }"]) == ["type", "scope"])
        #expect(try shape([], ["extension \(source) { static func preflight() -> Bool { \(preflightCall) } }"])
                == [Self.extensionIssue, "static", "type", "scope"])
        for extending in ["extension \(source) {\n    static func make() { }\n}", "extension AsteriaScale.\(source) { }",
                          "extension AsteriaScale . \(source): Equatable { }",
                          "extension FrameSource where Self: \(source) { static func make() { } }",
                          "typealias Capturer = \(source)\nextension Capturer { }", "typealias Capturer = AsteriaScale.\(source)"] {
            #expect(try shape([], also: [(hoverDriver, Self.lines(extending))]) == [Self.extensionIssue], "\(extending)")
        }
        #expect(try shape([], also: [(hoverDriver, Self.lines("// extension \(source) in a comment"))]).isEmpty)
        #expect(try shape([], also: [(hoverDriver, Self.lines(["private(set) var screenSource: \(source)?",
                                                               "typealias Frames = FrameSource", "extension FrameSource { }",
                                                               "extension HoverDriver { func source() -> \(source)? { nil } }"]
            .joined(separator: "\n")))]).isEmpty, "naming the class is free outside an extension's header and a typealias")
        #expect(try Self.captureShapeIssues([(hoverDriver, Self.lines("let a = 1"))]) == ["\(Self.shapeIssue) \(captureFile) is missing"])

        // The end of a hover over the screen: one report with the fresh
        // re-check, then the refusal, then the overlay.
        let end = [Self.endReport, Self.endRefusal, Self.endFinish]
        func ending(_ steps: [String]) throws -> [String] {
            try Self.screenHoverEndIssues([(hoverDriver, Self.lines(steps.map { "            " + $0 }.joined(separator: "\n")))])
                .map { $0.hasPrefix(Self.endIssue) ? Self.endIssue : $0 }
        }
        #expect(try ending(end).isEmpty)
        #expect(try ending(["let report = HoverReport.afterScreenHover(result.outcome, card: shown.card,",
                            "                                         cursorStillThere: mayShow())", Self.endRefusal, Self.endFinish])
                .isEmpty, "the report over two lines")
        let reverted: [(String, [String])] = [
            ("no re-check", [Self.endReport.replacingOccurrences(of: "mayShow()", with: "true"), Self.endRefusal, Self.endFinish]),
            ("the plain report", ["let report = HoverReport.after(result.outcome, shownPrimary: shown.primary != nil)",
                                  Self.endRefusal, Self.endFinish]),
            ("a second report", end + ["let again = HoverReport.after(result.outcome, shownPrimary: false)"]),
            ("re-checked again", [Self.endReport, Self.endRefusal,
                                  "app.overlay.finish(report.overlay(cursorStillThere: true), token: token, near: near)"]),
            ("the overlay first", [Self.endReport, Self.endFinish, Self.endRefusal]),
            ("the refusal first", [Self.endRefusal, Self.endReport, Self.endFinish]),
            ("no refusal", [Self.endReport, Self.endFinish]),
            ("a second finish", end + ["app.overlay.finish(.hide, token: token, near: near)"]),
        ]
        for (name, steps) in reverted {
            #expect(try ending(steps) == [Self.endIssue], "\(name)")
        }

        // The tracker's hide through OverlayState, and a card's own phrase line.
        let overlay = ["Sources/AsteriaScale/Over", "lay.swift"].joined()
        #expect(Self.overlayFile == overlay && Self.hoverDriverFile == hoverDriver)
        func wired(_ driver: String, _ card: String) throws -> [String] {
            try Self.overlayWiringIssues([(hoverDriver, Self.lines(driver)), (overlay, Self.lines(card))])
                .map { $0.hasPrefix(Self.wiringIssue) ? Self.wiringIssue : $0 }
        }
        let hideCase = "        case .hideCard:\n            // Only a screen hover's.\n            app.overlay.trackerHide()\n        case .cancelHover(let id):"
        let clears = "            model.badge = primary.badge\n            model.phrase = \"\""
        #expect(try wired(hideCase, clears).isEmpty)
        let unwired: [(String, String, String)] = [
            ("a plain hide", hideCase.replacingOccurrences(of: "app.overlay.trackerHide()", with: "app.overlay.hide()"), clears),
            ("the old check", hideCase.replacingOccurrences(of: "app.overlay.trackerHide()",
                                                            with: "if app.overlay.isVisible { app.overlay.trackerHide() }"), clears),
            ("a plain hide elsewhere", hideCase + "\n        _ = 1\n        app.overlay . hide ()", clears),
            ("the phrase line kept", hideCase, "            model.badge = primary.badge"),
        ]
        for (name, driver, card) in unwired {
            #expect(try wired(driver, card) == [Self.wiringIssue], "\(name)")
        }

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
