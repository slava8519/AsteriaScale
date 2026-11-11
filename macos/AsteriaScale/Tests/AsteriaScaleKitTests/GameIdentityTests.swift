import Foundation
import Testing
@testable import AsteriaScaleKit

/// KERN_PROCARGS2 records on synthetic bytes only (roadmap 20.3 DoD 5): no
/// test reads a real process.
@Suite struct ProcessArgumentsTests {
    static let loader = "/Applications/CrossOver.app/Contents/SharedSupport/CrossOver/lib/wine/x86_64-unix/wine64-preloader"

    /// The layout byte for byte: argc little-endian, the path, its padding,
    /// argc arguments (an empty one among them), the environment up to its
    /// empty entry, and nothing after that.
    @Test func theLayoutIsReadAsTheKernelWritesIt() throws {
        var bytes: [UInt8] = [3, 0, 0, 0]
        bytes += Array("/x/wine".utf8) + [0, 0, 0, 0]
        bytes += Array("wine".utf8) + [0] + [0] + Array(#"C:\Games\Novel.exe"#.utf8) + [0]
        bytes += Array("HOME=/Users/x".utf8) + [0] + Array("WINEPREFIX=/B/One".utf8) + [0] + [0]
        bytes += Array("executable_path=/x/wine".utf8) + [0]
        let parsed = try #require(ProcessArguments.parse(bytes))
        #expect(parsed == ProcessArguments(executablePath: "/x/wine", arguments: ["wine", "", #"C:\Games\Novel.exe"#],
                                           environment: ["HOME=/Users/x", "WINEPREFIX=/B/One"]))
        #expect(parsed.environmentValue("WINEPREFIX") == "/B/One" && parsed.environmentValue("HOME") == "/Users/x")
        #expect(parsed.environmentValue("executable_path") == nil && parsed.environmentValue("WINE") == nil)
    }

    @Test func encodeWritesWhatParseReads() {
        let arguments = [Self.loader, "", #"C:\Games\Novel.exe"#, "-windowed"]
        let environment = ["WINEPREFIX=/B/One", "LANG=ru_RU.UTF-8", "EMPTY="]
        for padding in [0, 1, 3, 8] {
            let bytes = ProcessArguments.encode(executablePath: Self.loader, arguments: arguments, environment: environment,
                                                padding: padding)
            #expect(ProcessArguments.parse(bytes) == ProcessArguments(executablePath: Self.loader, arguments: arguments,
                                                                      environment: environment), "padding \(padding)")
        }
        #expect(ProcessArguments.parse(ProcessArguments.encode(executablePath: "/x", arguments: [], environment: []))
                == ProcessArguments(executablePath: "/x", arguments: [], environment: []))
    }

    /// The first entry wins, the name must match up to its `=`, and the
    /// arguments are never taken for the environment.
    @Test func theEnvironmentIsLookedUpByItsWholeName() throws {
        let bytes = ProcessArguments.encode(executablePath: "/x/wine", arguments: ["WINEPREFIX=/evil"],
                                            environment: ["WINEPREFIXX=/a", "WINEPREFIX=/first", "WINEPREFIX=/second"])
        let parsed = try #require(ProcessArguments.parse(bytes))
        #expect(parsed.environmentValue("WINEPREFIX") == "/first")
        let none = try #require(ProcessArguments.parse(ProcessArguments.encode(
            executablePath: "/x/wine", arguments: ["WINEPREFIX=/evil"], environment: [])))
        #expect(none.environmentValue("WINEPREFIX") == nil)
    }

    /// A record after Wine rewrote its argv in place, as ntdll's
    /// set_process_name does where there is no setproctitle (macOS): the
    /// strings after argv[0] moved over it and the freed tail zeroed, while
    /// the kernel keeps the argc the process was started with. Each shift
    /// drops the first string of the argv area.
    static func shifted(executablePath: String, arguments: [String], environment: [String], shifts: Int) -> [UInt8] {
        var area: [UInt8] = []
        for argument in arguments { area += Array(argument.utf8) + [0] }
        for _ in 0..<max(shifts, 0) {
            guard let end = area.firstIndex(of: 0) else { break }
            let dropped = end + 1
            area = Array(area[dropped...]) + [UInt8](repeating: 0, count: dropped)
        }
        let argc = UInt32(arguments.count)
        var bytes: [UInt8] = [UInt8(argc & 0xFF), UInt8((argc >> 8) & 0xFF), UInt8((argc >> 16) & 0xFF), UInt8(argc >> 24)]
        bytes += Array(executablePath.utf8) + [0, 0, 0, 0]
        bytes += area
        for entry in environment { bytes += Array(entry.utf8) + [0] }
        return bytes + [0]
    }

    /// The environment after such a rewrite is still read: the NULs the
    /// shift left past argc empty strings end neither the arguments nor
    /// the environment.
    @Test func wineRewrittenArgumentsKeepTheEnvironment() throws {
        let wineloader = "/Applications/CrossOver.app/Contents/SharedSupport/CrossOver/bin/wineloader"
        let game = #"C:\Games\Novel\Novel.exe"#
        let environment = ["HOME=/Users/x", "WINEPREFIX=/B/One"]
        let cases: [(String, [String], Int, [String])] = [
            ("the preloader dropped", [Self.loader, wineloader, game], 1, [wineloader, game, ""]),
            ("the loader dropped", [wineloader, game], 1, [game, ""]),
            ("both dropped", [Self.loader, wineloader, game], 2, [game, "", ""]),
            ("not rewritten", [wineloader, game], 0, [wineloader, game]),
        ]
        for (name, arguments, shifts, expected) in cases {
            let bytes = Self.shifted(executablePath: wineloader, arguments: arguments, environment: environment, shifts: shifts)
            let parsed = try #require(ProcessArguments.parse(bytes), "\(name)")
            #expect(parsed.arguments == expected && parsed.environment == environment, "\(name): \(parsed)")
            #expect(parsed.environmentValue("WINEPREFIX") == "/B/One", "\(name)")
        }
    }

    /// Whatever the bytes, parse returns: nil for what is not a record,
    /// never a trap (a trap leaves a crash report).
    @Test func anyBytesEndInARecordOrNil() {
        let malformed: [(String, [UInt8])] = [
            ("empty", []), ("short", [1, 0, 0]), ("no path", [0, 0, 0, 0]), ("no NUL", [0, 0, 0, 0, 0x2F, 0x78]),
            ("negative argc", [0xFF, 0xFF, 0xFF, 0xFF, 0x2F, 0, 0x61, 0]),
            ("huge argc", [0xFF, 0xFF, 0xFF, 0x7F, 0x2F, 0, 0x61, 0, 0x62, 0]),
            ("argc over the arguments", [3, 0, 0, 0, 0x2F, 0, 0x61, 0, 0x62, 0]),
            ("an argument without its NUL", [2, 0, 0, 0, 0x2F, 0, 0x61, 0, 0x62]),
            ("only padding", [1, 0, 0, 0, 0x2F, 0, 0, 0, 0]),
        ]
        for (name, bytes) in malformed {
            #expect(ProcessArguments.parse(bytes) == nil, "\(name)")
        }
        // An environment that ends without its NUL keeps what came before.
        #expect(ProcessArguments.parse([1, 0, 0, 0, 0x2F, 0, 0x61, 0, 0x41, 0x3D, 0x31, 0, 0x42])?.environment == ["A=1"])

        // Every prefix of a record, and pseudo-random buffers (a fixed seed,
        // so a failure repeats): parse never traps, and whatever it reads
        // fits in the bytes it was given.
        let record = ProcessArguments.encode(executablePath: Self.loader, arguments: [Self.loader, #"C:\Games\Novel.exe"#],
                                             environment: ["WINEPREFIX=/B/One"])
        for length in 0...record.count {
            let parsed = ProcessArguments.parse(Array(record.prefix(length)))
            if let parsed { #expect(parsed.arguments.count == 2, "prefix \(length)") }
        }
        var seed: UInt64 = 0x2026_1111
        for round in 0..<3000 {
            var bytes: [UInt8] = []
            seed = seed &* 6364136223846793005 &+ 1442695040888963407
            let count = Int(seed >> 56)
            for _ in 0..<count {
                seed = seed &* 6364136223846793005 &+ 1442695040888963407
                // Mostly NULs and a few letters, so records half form.
                let byte = UInt8(truncatingIfNeeded: seed >> 59)
                bytes.append(byte < 12 ? 0 : byte)
            }
            if round % 3 == 0, bytes.count >= 4 { bytes[0] = UInt8(truncatingIfNeeded: round); bytes[1] = 0; bytes[2] = 0; bytes[3] = 0 }
            if let parsed = ProcessArguments.parse(bytes) {
                #expect(parsed.arguments.count + parsed.environment.count <= bytes.count, "round \(round)")
            }
        }
    }
}

/// The game a hover sends (T29, T32), from synthetic process records,
/// synthetic window lists and synthetic folders under the test's scratch.
@Suite struct GameIdentityTests {
    static let loader = ProcessArgumentsTests.loader

    static func wine(_ arguments: [String], prefix: String? = "/B/One", executable: String = loader,
                     resolve: (String) -> String? = { _ in nil }) -> HoverGame? {
        let environment = ["HOME=/Users/x"] + (prefix.map { ["WINEPREFIX=\($0)"] } ?? [])
        let record = ProcessArguments.encode(executablePath: executable, arguments: arguments, environment: environment)
        return GameIdentity.game(executablePath: executable, processArguments: record, resolveLink: resolve)
    }

    /// Core's key normalization (SpeakerGenderPreferences.NormalizeGameKey),
    /// spelled again here from the C# source: the key trimmed and NFC, a
    /// `path:` prefix in any case, the path trimmed, its quotes trimmed, NFC,
    /// absolute, and .NET's GetFullPath on Unix (empty and `.` components
    /// dropped, `..` applied, never above the root). nil where Core drops
    /// the key.
    static func coreKey(_ key: String) -> String? {
        let whitespace = CharacterSet.whitespacesAndNewlines.union(CharacterSet(charactersIn: "\u{85}"))
        let trimmed = key.trimmingCharacters(in: whitespace).precomposedStringWithCanonicalMapping
        guard trimmed.lowercased().hasPrefix("path:") else { return nil }
        let path = String(trimmed.dropFirst(5)).trimmingCharacters(in: whitespace)
            .trimmingCharacters(in: CharacterSet(charactersIn: "\"")).precomposedStringWithCanonicalMapping
        guard path.hasPrefix("/"), !path.contains("\u{0}") else { return nil }
        var parts: [Substring] = []
        for part in path.split(separator: "/", omittingEmptySubsequences: true) {
            if part == "." { continue }
            if part == ".." {
                if !parts.isEmpty { parts.removeLast() }
                continue
            }
            parts.append(part)
        }
        return "path:/" + parts.joined(separator: "/")
    }

    /// What CoreHost accepts as game.key (ValidateGame): at most 256
    /// characters, and Core's normalization leaves it as it is.
    static func coreHostAccepts(_ game: HoverGame?) -> Bool {
        guard let game else { return false }
        return game.key.utf16.count <= 256 && game.displayName.utf16.count <= 256 && coreKey(game.key) == game.key
            && coreKey(coreKey(game.key) ?? "") == game.key
    }

    @Test func wineIsKnownByItsLoaderOrCrossOver() {
        for path in ["/usr/local/bin/wine", "/opt/wine/bin/wine64", "/x/lib/wine/x86_64-unix/wineloader", Self.loader,
                     "/x/WINE64-PRELOADER", "/Applications/CrossOver.app/Contents/SharedSupport/CrossOver/bin/cxstart"] {
            #expect(GameIdentity.isWine(path), "\(path)")
        }
        for path in ["/Applications/Vampire Crawlers.app/Contents/MacOS/VampireCrawlers", "/usr/bin/swine-dir/game",
                     "/Applications/CrossOverlay.app/Contents/MacOS/CrossOverlay"] {
            #expect(!GameIdentity.isWine(path), "\(path)")
        }
    }

    /// A native program: `path:` its executable, named after its bundle.
    @Test func aNativeGameIsItsExecutable() throws {
        let bundle = "/Applications/Vampire Crawlers.app/Contents/MacOS/VampireCrawlers"
        let game = try #require(GameIdentity.game(executablePath: bundle, processArguments: nil, resolveLink: { _ in nil }))
        #expect(game == HoverGame(key: "path:" + bundle, displayName: "Vampire Crawlers") && Self.coreHostAccepts(game))
        #expect(GameIdentity.game(executablePath: "/usr/local/games/novel", processArguments: nil, resolveLink: { _ in nil })
                == HoverGame(key: "path:/usr/local/games/novel", displayName: "novel"))
        // Core's form: NFC, no empty or dot components.
        let decomposed = "/Applications/Cafe\u{301}.app//Contents/./MacOS/x/../Cafe\u{301}"
        let canonical = try #require(GameIdentity.game(executablePath: decomposed, processArguments: nil,
                                                       resolveLink: { _ in nil }))
        #expect(canonical == HoverGame(key: "path:/Applications/Caf\u{E9}.app/Contents/MacOS/Caf\u{E9}", displayName: "Caf\u{E9}"))
        #expect(Self.coreHostAccepts(canonical) && canonical.key.unicodeScalars.count == canonical.key.count)
    }

    /// Nothing Core would store under another key, or CoreHost would refuse
    /// (T32, which would refuse every hover in that game too).
    @Test func noKeyCoreWouldChangeOrRefuse() {
        let refused = ["relative/game", "", "/Games/Novel ", "/Games/Novel\u{A0}", "/Games/Novel\"", "/Games/No\u{0}vel",
                       "/" + String(repeating: "a", count: 251)]
        for path in refused {
            #expect(GameIdentity.game(executablePath: path, processArguments: nil, resolveLink: { _ in nil }) == nil, "\(path)")
        }
        let longest = "/" + String(repeating: "a", count: 250)
        let game = GameIdentity.game(executablePath: longest, processArguments: nil, resolveLink: { _ in nil })
        #expect(game?.key.utf16.count == 256 && Self.coreHostAccepts(game))
        // The reference agrees that each refused one would not stay itself.
        for path in ["/Games/Novel ", "/Games/Novel\"", "/Games//Novel", "/Games/./Novel"] {
            #expect(Self.coreKey("path:" + path) != "path:" + path, "\(path)")
        }
    }

    /// Wine: the .exe the arguments name, wherever Wine leaves it, mapped
    /// into the bottle; Wine's own programs and paths are never a game.
    @Test func aWineGameIsTheExeInItsBottle() {
        let novel = HoverGame(key: "path:/B/One/drive_c/Games/Novel/Novel.exe", displayName: "Novel.exe")
        let layouts: [(String, [String], HoverGame?)] = [
            ("loader, then the game", [Self.loader, #"C:\Games\Novel\Novel.exe"#], novel),
            ("the game first, with arguments", [#"C:\Games\Novel\Novel.exe"#, "-windowed", "D:\\other.exe"], novel),
            ("forward slashes, lower case", ["wine64", "c:/Games/Novel/Novel.exe"], novel),
            ("start.exe before the game", [#"C:\windows\command\start.exe"#, "/exec", #"C:\Games\Novel\Novel.exe"#], novel),
            ("dots in the path", [#"C:\Games\.\Old\..\Novel\Novel.exe"#], novel),
            ("upper-case extension", [#"C:\Games\Novel\NOVEL.EXE"#],
             HoverGame(key: "path:/B/One/drive_c/Games/Novel/NOVEL.EXE", displayName: "NOVEL.EXE")),
            ("start.exe /unix", ["wine64", "start.exe", "/unix", "/Users/x/Games/Novel/Novel.exe"],
             HoverGame(key: "path:/Users/x/Games/Novel/Novel.exe", displayName: "Novel.exe")),
            ("Wine's desktop", [#"C:\windows\system32\explorer.exe"#, "/desktop"], nil),
            ("a bottle's Wine program", ["/B/One/drive_c/windows/system32/winedevice.exe"], nil),
            ("Wine's own builtin", ["/Applications/CrossOver.app/Contents/lib/wine/x86_64-windows/explorer.exe"], nil),
            ("no .exe at all", [Self.loader, "--version"], nil),
            ("a bare name", ["Novel.exe"], nil),
            ("a relative path", [#"Games\Novel.exe"#], nil),
            ("only an extension", [#"C:\Games\.exe"#], nil),
            ("a UNC path", [#"\\server\share\Novel.exe"#], nil),
        ]
        for (name, arguments, expected) in layouts {
            let game = Self.wine(arguments)
            #expect(game == expected, "\(name): \(String(describing: game))")
            if expected != nil { #expect(Self.coreHostAccepts(game), "\(name)") }
        }
        // Never the loader's path, never a process: key.
        #expect(Self.wine([Self.loader]) == nil)
        // Two bottles, two keys (T29: a loader path would be one for both).
        let other = Self.wine([#"C:\Games\Novel\Novel.exe"#], prefix: "/B/Two/")
        #expect(other?.key == "path:/B/Two/drive_c/Games/Novel/Novel.exe" && other != novel)
    }

    /// The game of a Wine process whose argv Wine rewrote in place (T29):
    /// the shift leaves the .exe in the arguments and NULs before the
    /// environment, and WINEPREFIX still places it in the bottle.
    @Test func aRewrittenWineProcessKeepsItsGame() {
        let wineloader = "/Applications/CrossOver.app/Contents/SharedSupport/CrossOver/bin/wineloader"
        let novel = HoverGame(key: "path:/B/One/drive_c/Games/Novel/Novel.exe", displayName: "Novel.exe")
        for (arguments, shifts) in [([Self.loader, wineloader, #"C:\Games\Novel\Novel.exe"#], 1),
                                    ([wineloader, #"C:\Games\Novel\Novel.exe"#], 1),
                                    ([Self.loader, wineloader, #"C:\Games\Novel\Novel.exe"#], 2)] {
            let record = ProcessArgumentsTests.shifted(executablePath: wineloader, arguments: arguments,
                                                       environment: ["HOME=/Users/x", "WINEPREFIX=/B/One"], shifts: shifts)
            let game = GameIdentity.game(executablePath: wineloader, processArguments: record, resolveLink: { _ in nil })
            #expect(game == novel && Self.coreHostAccepts(game), "\(arguments.count) arguments, \(shifts) shifts")
        }
    }

    /// Without WINEPREFIX a Windows path cannot be placed (a Unix path still
    /// can), and a prefix that is not absolute places nothing; a record that
    /// does not parse is no game.
    @Test func withoutItsBottleAWindowsPathIsNoGame() {
        #expect(Self.wine([#"C:\Games\Novel\Novel.exe"#], prefix: nil) == nil)
        #expect(Self.wine([#"C:\Games\Novel\Novel.exe"#], prefix: "bottles/one") == nil)
        #expect(Self.wine(["/Users/x/Games/Novel.exe"], prefix: nil)?.key == "path:/Users/x/Games/Novel.exe")
        #expect(GameIdentity.game(executablePath: Self.loader, processArguments: nil, resolveLink: { _ in "/" }) == nil)
        #expect(GameIdentity.game(executablePath: Self.loader, processArguments: [1, 2, 3], resolveLink: { _ in "/" }) == nil)
    }

    /// Other letters go through the bottle's dosdevices links, resolved by
    /// realpath on a folder tree made under the test's scratch folder (the
    /// links point inside it); a letter without a link is no game, and the
    /// resolver is asked for exactly `<prefix>/dosdevices/<letter>:`.
    @Test func otherDrivesFollowTheBottlesLinks() throws {
        let scratch = try Scratch("game")
        defer { scratch.remove() }
        let bottle = try scratch.folder("b")
        let games = try scratch.folder("d/Games")
        let root = try scratch.folder("z")
        let devices = try scratch.folder("b/dosdevices")
        try FileManager.default.createSymbolicLink(atPath: devices.appendingPathComponent("d:").path,
                                                   withDestinationPath: "../../d")
        try FileManager.default.createSymbolicLink(at: devices.appendingPathComponent("z:"), withDestinationURL: root)
        let realD = try #require(GameIdentity.realPath(games.deletingLastPathComponent().path))
        let realZ = try #require(GameIdentity.realPath(root.path))
        func expected(_ path: String) -> HoverGame? {
            let key = "path:" + path
            return key.utf16.count <= 256 ? HoverGame(key: key, displayName: "Novel.exe") : nil
        }
        let asked = Box<[String]>([])
        let resolve: (String) -> String? = { link in
            asked.mutate { $0.append(link) }
            return GameIdentity.realPath(link)
        }
        let onD = Self.wine([#"D:\Games\Novel.exe"#], prefix: bottle.path, resolve: resolve)
        #expect(onD == expected(realD + "/Games/Novel.exe") && (onD == nil || Self.coreHostAccepts(onD)))
        let onZ = Self.wine(["Z:/Novel.exe"], prefix: bottle.path, resolve: resolve)
        #expect(onZ == expected(realZ + "/Novel.exe"))
        #expect(Self.wine([#"E:\Novel.exe"#], prefix: bottle.path, resolve: resolve) == nil, "no link for E:")
        #expect(asked.get == ["d:", "z:", "e:"].map { bottle.path + "/dosdevices/" + $0 })
        // C: never asks: it is the bottle's drive_c.
        asked.set([])
        #expect(Self.wine([#"C:\Novel.exe"#], prefix: bottle.path, resolve: resolve)
                == expected(bottle.path + "/drive_c/Novel.exe"))
        #expect(asked.get.isEmpty)
        // A drive mapped to the root.
        #expect(Self.wine([#"Z:\Users\x\Novel.exe"#], resolve: { _ in "/" })?.key == "path:/Users/x/Novel.exe")
        #expect(GameIdentity.realPath(scratch.root.appendingPathComponent("missing").path) == nil)
    }

    /// The window under the cursor: front to back, the first window of
    /// another process whose bounds hold the point and that is ordinary
    /// (layer 0) or Wine's above that, with Cocoa's y flipped into Quartz's
    /// by the primary display's height.
    @Test func theWindowUnderTheCursorIsTheFrontmostOrdinaryForeignOne() {
        typealias Record = GameIdentity.WindowRecord
        let own: Int32 = 7
        func rect(_ x: Double, _ y: Double, _ width: Double, _ height: Double) -> PointRect {
            PointRect(x: x, y: y, width: width, height: height)
        }
        let windows = [
            Record(ownerPid: own, layer: 0, bounds: rect(0, 0, 1440, 900)),
            Record(ownerPid: 30, layer: 25, bounds: rect(0, 0, 1440, 900)),
            Record(ownerPid: 0, layer: 0, bounds: rect(0, 0, 1440, 900)),
            Record(ownerPid: 31, layer: 0, bounds: rect(0, 0, 0, 900)),
            Record(ownerPid: 42, layer: 0, bounds: rect(100, 100, 400, 300)),
            Record(ownerPid: 43, layer: 0, bounds: rect(0, 0, 1440, 900)),
        ]
        // No owner above is Wine's here: those windows are passed over.
        func owner(_ x: Double, _ y: Double, _ list: [Record] = windows) -> Int32? {
            GameIdentity.windowOwner(at: GlobalPoint(x: x, y: y), windows: list, ownPid: own, primaryHeight: 900,
                                     isWine: { _ in false })
        }
        // Cocoa (200, 600) is Quartz (200, 300): inside 42.
        #expect(owner(200, 600) == 42)
        // Edges: minX and minY inside, maxX and maxY outside (Quartz).
        #expect(owner(100, 800) == 42 && owner(500, 600) == 43 && owner(499.5, 500.5) == 42 && owner(200, 400) == 43)
        #expect(owner(50, 50) == 43)
        // The top row of the primary display (Cocoa y == 900) is Quartz 0.
        #expect(owner(10, 900, [Record(ownerPid: 50, layer: 0, bounds: rect(0, 0, 100, 1))]) == 50)
        // A display to the right and above: Quartz y is negative there.
        #expect(owner(1500, 1000, [Record(ownerPid: 51, layer: 0, bounds: rect(1440, -200, 800, 600))]) == 51)
        #expect(owner(200, 600, windows.filter { $0.ownerPid != 42 && $0.ownerPid != 43 }) == nil)
        #expect(owner(.nan, 600) == nil && owner(200, .infinity) == nil)
        #expect(GameIdentity.windowOwner(at: GlobalPoint(x: 200, y: 600), windows: windows, ownPid: own,
                                         primaryHeight: .nan, isWine: { _ in true }) == nil)
    }

    /// A full-screen bottle's window above layer 0 (T29: its level is not
    /// measured) names the bottle, not the ordinary window behind it; a
    /// window above 0 that is not Wine's is still passed over, one below 0
    /// (the desktop) is never taken, and Wine is asked only about a window
    /// above 0 under the point.
    @Test func aWineWindowAboveTheOrdinaryLayerIsTheGames() {
        typealias Record = GameIdentity.WindowRecord
        let own: Int32 = 7, bottle: Int32 = 60, menuBar: Int32 = 61, behind: Int32 = 62, desktop: Int32 = 63
        let screen = PointRect(x: 0, y: 0, width: 1440, height: 900)
        let asked = Box<[Int32]>([])
        func owner(_ windows: [Record], _ x: Double = 700, _ y: Double = 450) -> Int32? {
            GameIdentity.windowOwner(at: GlobalPoint(x: x, y: y), windows: windows, ownPid: own, primaryHeight: 900,
                                     isWine: { pid in
                                         asked.mutate { $0.append(pid) }
                                         return pid == bottle || pid == desktop
                                     })
        }
        let menu = Record(ownerPid: menuBar, layer: 24, bounds: PointRect(x: 0, y: 0, width: 1440, height: 24))
        let fullScreen = Record(ownerPid: bottle, layer: 26, bounds: screen)
        let ordinary = Record(ownerPid: behind, layer: 0, bounds: screen)
        #expect(owner([menu, Record(ownerPid: own, layer: 101, bounds: screen), fullScreen, ordinary]) == bottle)
        #expect(asked.get == [bottle], "asked about \(asked.get)")
        asked.set([])
        // Under the menu bar the menu bar's owner is asked and passed over.
        #expect(owner([menu, fullScreen, ordinary], 700, 890) == bottle && asked.get == [menuBar, bottle])
        asked.set([])
        #expect(owner([Record(ownerPid: menuBar, layer: 3, bounds: screen), ordinary]) == behind && asked.get == [menuBar])
        #expect(owner([Record(ownerPid: desktop, layer: -2147483624, bounds: screen)]) == nil)
        #expect(owner([Record(ownerPid: bottle, layer: 0, bounds: screen)]) == bottle)
        asked.set([])
        #expect(owner([Record(ownerPid: bottle, layer: 26, bounds: PointRect(x: 0, y: 0, width: 100, height: 100)), ordinary])
                == behind && asked.get.isEmpty, "a window not under the point is never asked about")
    }
}
