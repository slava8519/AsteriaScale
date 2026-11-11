import Foundation

// The game a hover belongs to, as the app names it to CoreHost (roadmap 20.3
// DoD 5; docs/MACOS.md T29, T32): the executable of the window's owner under
// the cursor, and for a Wine process the Unix path of the game's .exe inside
// its bottle. Everything here is pure over values the app reads (the window
// list's records, the owner's executable path, its KERN_PROCARGS2 bytes) and
// a link reader, so every rule is tested on synthetic bytes and synthetic
// folders; the app's screen source makes the one real read, and only while
// capture is on (T22).

/// A process's KERN_PROCARGS2 record: the executable path it was started
/// with, its arguments and its environment.
///
/// The layout `parse` reads (and `encode` writes): argc as four bytes,
/// little-endian as every Mac stores it; the executable path and its NUL;
/// NUL padding; argc NUL-terminated arguments; then, after any run of NULs,
/// NUL-terminated environment entries, up to an empty one or the end. The
/// run of NULs is there because Wine rewrites its own argv in place (ntdll's
/// set_process_name moves argv[1...] over argv[0] and zeroes the freed
/// tail), while argc stays the count the process was started with: its
/// last arguments then read as empty strings and the rest of that tail as
/// NULs before the environment. Whatever the kernel appends after the
/// environment reads as more entries, which no lookup here names. Empty
/// arguments at the start cannot be told from the padding before them:
/// `parse` skips them with it (argv[0], and the next ones while they stay
/// empty), so the arguments start that many strings later and end that
/// many strings into the environment, which loses those entries (`encode`
/// writes such a record all the same; it does not read back).
public struct ProcessArguments: Sendable, Equatable {
    public let executablePath: String
    public let arguments: [String]
    /// "NAME=value", in the order the process received them.
    public let environment: [String]

    public init(executablePath: String, arguments: [String], environment: [String]) {
        self.executablePath = executablePath
        self.arguments = arguments
        self.environment = environment
    }

    /// The value of the first `name=` entry of the environment (the
    /// arguments are never read for it), compared byte for byte.
    public func environmentValue(_ name: String) -> String? {
        let key = Array(name.utf8) + [UInt8(ascii: "=")]
        for entry in environment {
            let bytes = Array(entry.utf8)
            if bytes.count >= key.count, Array(bytes[0..<key.count]) == key {
                return String(decoding: bytes[key.count...], as: UTF8.self)
            }
        }
        return nil
    }

    /// The record in `bytes`, or nil when it is not one: fewer than four
    /// bytes, a negative argc, an executable path or one of argc arguments
    /// without its NUL. Every read is bounds-checked and nothing is
    /// allocated from argc, so a truncated buffer, one without a NUL or an
    /// argc of two billion ends in nil, never in a trap (a trap leaves a
    /// crash report). An empty argument after a non-empty one is an
    /// argument; a run of NULs is skipped twice: the padding right after the
    /// executable path, and whatever Wine's argv rewrite left after the
    /// arguments. The first skip also takes every empty argument at the
    /// start (argv[0], and the next ones while they stay empty), each a NUL
    /// like the padding: the arguments then start at the first non-empty
    /// one and end that many strings into the environment, which loses
    /// those entries. A process Wine
    /// starts begins with its loader's path; where its rewrite moves an
    /// empty string to the front, the NULs it leaves at the end of the argv
    /// area take that string's place, so the environment is still whole.
    public static func parse(_ bytes: [UInt8]) -> ProcessArguments? {
        guard bytes.count >= 4 else { return nil }
        let raw = UInt32(bytes[0]) | UInt32(bytes[1]) << 8 | UInt32(bytes[2]) << 16 | UInt32(bytes[3]) << 24
        let argc = Int(Int32(bitPattern: raw))
        guard argc >= 0 else { return nil }
        var index = 4
        guard let path = string(in: bytes, at: &index) else { return nil }
        while index < bytes.count, bytes[index] == 0 { index += 1 }
        var arguments: [String] = []
        while arguments.count < argc {
            guard let argument = string(in: bytes, at: &index) else { return nil }
            arguments.append(argument)
        }
        // Wine's rewritten argv ends in more NULs than argc empty strings
        // take up; without this skip the first of them would end the
        // environment before WINEPREFIX.
        while index < bytes.count, bytes[index] == 0 { index += 1 }
        var environment: [String] = []
        while let entry = string(in: bytes, at: &index), !entry.isEmpty {
            environment.append(entry)
        }
        return ProcessArguments(executablePath: path, arguments: arguments, environment: environment)
    }

    /// The NUL-terminated string that starts at `index`, which then moves
    /// past its NUL; nil when no NUL ends it before the buffer does.
    private static func string(in bytes: [UInt8], at index: inout Int) -> String? {
        guard index >= 0, index < bytes.count, let end = bytes[index...].firstIndex(of: 0) else { return nil }
        let text = String(decoding: bytes[index..<end], as: UTF8.self)
        index = end + 1
        return text
    }

    /// A record laid out as `parse` reads it, with `padding` NULs after the
    /// executable path and an empty entry after the environment. The one
    /// place that spells the layout for the tests and for the self-test's
    /// synthetic game; nothing here reads a real process. Empty arguments
    /// at the start are written as the kernel would write them, and `parse`
    /// reads them back as padding (see `parse`): such a record does not
    /// read back.
    public static func encode(executablePath: String, arguments: [String], environment: [String],
                              padding: Int = 3) -> [UInt8] {
        let argc = UInt32(truncatingIfNeeded: arguments.count)
        var bytes: [UInt8] = [UInt8(argc & 0xFF), UInt8((argc >> 8) & 0xFF), UInt8((argc >> 16) & 0xFF),
                              UInt8(argc >> 24)]
        bytes += Array(executablePath.utf8) + [0]
        bytes += [UInt8](repeating: 0, count: max(padding, 0))
        for text in arguments + environment { bytes += Array(text.utf8) + [0] }
        bytes.append(0)
        return bytes
    }
}

/// From a window under the cursor to hover.resolve's `game` (T29).
public enum GameIdentity {
    /// CoreHost's limit on game.key and game.displayName, in UTF-16 units: a
    /// longer one makes it refuse the hover (bad_request), so none is sent.
    public static let maxLength = 256
    static let keyPrefix = "path:"

    /// One entry of the window list, read only by its owner's pid, its layer
    /// and its bounds (T29: never by its name, which needs Screen Recording).
    /// The bounds are Quartz global points: origin at the top-left corner of
    /// the primary display, y down.
    public struct WindowRecord: Sendable, Hashable {
        public let ownerPid: Int32
        public let layer: Int
        public let bounds: PointRect

        public init(ownerPid: Int32, layer: Int, bounds: PointRect) {
            self.ownerPid = ownerPid
            self.layer = layer
            self.bounds = bounds
        }
    }

    /// The owner of the frontmost window under `point` that is not this
    /// app's and is either an ordinary window (layer 0) or one above that
    /// whose owner `isWine` says is Wine's, from a list in front-to-back
    /// order, as the window list gives it. A full-screen bottle's window may
    /// sit above layer 0 (unmeasured, T29), and the ordinary window behind
    /// it would name another program; any other window above layer 0 (the
    /// menu bar, a floating panel) is passed over, and below 0 (the desktop)
    /// is never taken. `isWine` is asked only about such a window under the
    /// point. `point` is Cocoa global (y up), so it is flipped with the
    /// primary display's height first; a window holds the points with
    /// minX <= x < maxX and minY <= y < maxY. Nil when no such window is
    /// there or a coordinate is not finite.
    public static func windowOwner(at point: GlobalPoint, windows: [WindowRecord], ownPid: Int32,
                                   primaryHeight: Double, isWine: (Int32) -> Bool) -> Int32? {
        let x = point.x, y = primaryHeight - point.y
        guard x.isFinite, y.isFinite else { return nil }
        return windows.first { window in
            let bounds = window.bounds
            guard window.layer >= 0, window.ownerPid > 0, window.ownerPid != ownPid,
                  bounds.width > 0, bounds.height > 0,
                  x >= bounds.minX, x < bounds.maxX, y >= bounds.minY, y < bounds.maxY else { return false }
            return window.layer == 0 || isWine(window.ownerPid)
        }?.ownerPid
    }

    /// Whether `executablePath` is Wine's: a file whose name starts with
    /// "wine" (wine, wine64, wineloader, wine64-preloader, …) or anything
    /// inside CrossOver.app. Its own path names Wine, never the game, so it
    /// is never a key; a native program whose name starts with "wine" is
    /// taken for Wine too and gets a game only if its arguments name an .exe.
    public static func isWine(_ executablePath: String) -> Bool {
        let name = String(decoding: lastComponent(Array(executablePath.utf8)), as: UTF8.self).lowercased()
        return name.hasPrefix("wine") || executablePath.lowercased().contains("/crossover.app/")
    }

    /// The game of the process with this executable (proc_pidpath's path).
    /// A native program is `path:` its executable, named after its .app
    /// bundle or its file. A Wine process is `path:` the Unix path of the
    /// game's .exe in its bottle, from its KERN_PROCARGS2 bytes: the first
    /// argument that is a fully qualified .exe outside Wine's own programs,
    /// a Windows path mapped through WINEPREFIX (C: is `drive_c`, another
    /// letter the text of the link `dosdevices/<letter>:` as `readLink`
    /// gives it, see `bottlePath`) or an absolute Unix path as it is; named
    /// after the .exe. Nil when any of it cannot be had, and nil rather than
    /// a key Core would store under another spelling or CoreHost would
    /// refuse (T32): no game then, never Wine's own path and never a
    /// `process:` key for Wine.
    public static func game(executablePath: String, processArguments: [UInt8]?,
                            readLink: (String) -> String?) -> HoverGame? {
        guard isWine(executablePath) else {
            guard let path = canonicalPath(executablePath), let key = key(forPath: path),
                  let name = displayName(nativeName(path)) else { return nil }
            return HoverGame(key: key, displayName: name)
        }
        guard let bytes = processArguments, let process = ProcessArguments.parse(bytes),
              let executable = gameExecutable(in: process.arguments) else { return nil }
        let unixPath: String?
        switch executable {
        case .unix(let path):
            unixPath = path
        case .windows(let drive, let components):
            guard let prefix = process.environmentValue("WINEPREFIX") else { return nil }
            unixPath = bottlePath(drive: drive, components: components, prefix: prefix, readLink: readLink)
        }
        guard let unixPath, let path = canonicalPath(unixPath), let key = key(forPath: path),
              let name = displayName(String(decoding: lastComponent(Array(path.utf8)), as: UTF8.self)) else { return nil }
        return HoverGame(key: key, displayName: name)
    }

    /// An .exe an argument names.
    enum Executable: Sendable, Equatable {
        /// A drive letter (lowercased) and the path's components, `.` and
        /// `..` already taken out.
        case windows(drive: UInt8, components: [String])
        case unix(String)
    }

    /// The first argument, in order, that names the game's .exe: wherever
    /// Wine leaves it (the loader may come first, or `start.exe /unix`
    /// before a Unix path), so the rule names no index.
    static func gameExecutable(in arguments: [String]) -> Executable? {
        for argument in arguments {
            if let executable = executable(argument) { return executable }
        }
        return nil
    }

    /// `argument` as a game's .exe: a fully qualified Windows path (`X:\` or
    /// `X:/`) or an absolute Unix path whose last component ends in .exe,
    /// outside Wine's own programs (a drive's `windows` folder, a bottle's
    /// `drive_c/windows`, Wine's `lib/wine`). A bare name or a relative path
    /// cannot be placed in the bottle.
    static func executable(_ argument: String) -> Executable? {
        let bytes = Array(argument.utf8)
        let slash = UInt8(ascii: "/"), backslash = UInt8(ascii: "\\"), colon = UInt8(ascii: ":")
        if bytes.first == slash {
            let parts = components(bytes, separators: [slash])
            guard let last = parts.last, isExe(last) else { return nil }
            let lowered = parts.map { $0.lowercased() }
            for index in lowered.indices.dropLast() {
                if lowered[index] == "drive_c", lowered[index + 1] == "windows" { return nil }
                if lowered[index] == "lib", lowered[index + 1] == "wine" { return nil }
            }
            return .unix(argument)
        }
        guard bytes.count > 3, isLetter(bytes[0]), bytes[1] == colon, bytes[2] == backslash || bytes[2] == slash else {
            return nil
        }
        let parts = components(Array(bytes[3...]), separators: [slash, backslash])
        guard let last = parts.last, isExe(last), parts.first?.lowercased() != "windows" else { return nil }
        return .windows(drive: bytes[0] | 0x20, components: parts)
    }

    /// The Unix path of a Windows path in the bottle at `prefix` (Wine's
    /// layout), lexical only: C: is `<prefix>/drive_c`; any other letter is
    /// what `readLink` gives for `<prefix>/dosdevices/<letter>:` (the
    /// link's own text, injected): an absolute one as it is, a relative one
    /// (Wine's `../drive_c` style) taken from the `dosdevices` folder, each
    /// with `.` and `..` applied by name. No link inside the target is
    /// followed, so its folders are never looked up (a drive mapped into
    /// Documents or onto a removable volume would otherwise be touched on
    /// every hover). As `..` goes by name, a relative link that climbs
    /// above the bottle names another folder than the kernel would when the
    /// bottle's folder is reached through a link, and so does any relative
    /// link when `dosdevices` is itself one (Wine's own, `../drive_c`, stays
    /// in the bottle, and Wine makes `dosdevices` a folder); the key is
    /// still one per letter. The end-of-path rule of `canonicalPath` is the final
    /// key's alone: a bottle or drive folder whose name ends in a space or a
    /// quote still gives a key Core keeps as it is.
    static func bottlePath(drive: UInt8, components: [String], prefix: String,
                           readLink: (String) -> String?) -> String? {
        guard let root = lexicalPath(prefix) else { return nil }
        let base: String
        if drive == UInt8(ascii: "c") {
            base = root == "/" ? "/drive_c" : root + "/drive_c"
        } else {
            let devices = (root == "/" ? "" : root) + "/dosdevices/"
            guard let text = readLink(devices + String(decoding: [drive], as: UTF8.self) + ":"), !text.isEmpty,
                  let target = lexicalPath(text.utf8.first == UInt8(ascii: "/") ? text : devices + text) else { return nil }
            base = target
        }
        return (base == "/" ? "" : base) + "/" + components.joined(separator: "/")
    }

    /// What a `dosdevices` entry names, read with readlink(2) alone (T29):
    /// the link's own text, relative or absolute as Wine wrote it, and
    /// nothing of its target, which may lie in a folder macOS guards
    /// (Documents, Downloads, a removable or network volume) and would then
    /// raise a privacy prompt in the middle of a hover; `path` itself when
    /// the entry is not a link (EINVAL: a folder, which a bottle may hold in
    /// place of a link; realpath kept it too); nil when there is no entry or
    /// it cannot be read (ENOENT, EACCES, …) and when the text is empty or
    /// fills the buffer (it may be cut). The text is decoded from exactly
    /// the bytes readlink wrote: it adds no NUL.
    public static func readLink(_ path: String) -> String? {
        var buffer = [CChar](repeating: 0, count: Int(PATH_MAX) + 1)
        let count = Darwin.readlink(path, &buffer, buffer.count)
        let error = errno
        guard count >= 0 else { return error == EINVAL ? path : nil }
        guard count > 0, count < buffer.count else { return nil }
        return String(decoding: buffer[0..<count].map { UInt8(bitPattern: $0) }, as: UTF8.self)
    }

    /// hover.resolve's game.key for an absolute path, nil when it is longer
    /// than CoreHost allows.
    static func key(forPath path: String) -> String? {
        let key = keyPrefix + path
        return key.utf16.count <= maxLength ? key : nil
    }

    /// `path` as Core keeps a game key's path (SpeakerGenderPreferences:
    /// NFC, then .NET's GetFullPath, which drops empty and `.` components
    /// and applies `..`, never above the root), so the key CoreHost stores
    /// is the one the app sent and a second normalization changes nothing.
    /// Nil for what Core would change in another way or refuse: a path that
    /// is not absolute, holds a NUL, or ends in white space or a quote,
    /// which Core trims off the key (T32). Only for a key's whole path: a
    /// folder on the way to it goes through `lexicalPath`.
    static func canonicalPath(_ path: String) -> String? {
        guard let result = lexicalPath(path), let last = result.unicodeScalars.last,
              !last.properties.isWhitespace, last != "\"" else { return nil }
        return result
    }

    /// `path` in NFC with empty and `.` components dropped and `..` applied
    /// by name, never above the root, as GetFullPath does on Unix; nil when
    /// it is not absolute or holds a NUL. No file is read.
    static func lexicalPath(_ path: String) -> String? {
        let bytes = Array(path.precomposedStringWithCanonicalMapping.utf8)
        guard bytes.first == UInt8(ascii: "/"), !bytes.contains(0) else { return nil }
        return "/" + components(bytes, separators: [UInt8(ascii: "/")]).joined(separator: "/")
    }

    /// A game's display name: NFC, trimmed, 1...256 UTF-16 units.
    static func displayName(_ text: String) -> String? {
        let name = text.precomposedStringWithCanonicalMapping.trimmingCharacters(in: .whitespacesAndNewlines)
        return (1...maxLength).contains(name.utf16.count) ? name : nil
    }

    /// A native program's name: its bundle's (`Name.app/Contents/MacOS/…`
    /// gives "Name"), otherwise its file's. Read from the path only.
    static func nativeName(_ path: String) -> String {
        let parts = components(Array(path.utf8), separators: [UInt8(ascii: "/")], resolvingDots: false)
        if let index = parts.lastIndex(where: { $0.lowercased().hasSuffix(".app") }), index + 2 < parts.count,
           parts[index + 1] == "Contents", parts[index + 2] == "MacOS" {
            let bundle = String(parts[index].dropLast(4))
            if !bundle.isEmpty { return bundle }
        }
        return parts.last ?? ""
    }

    /// The non-empty components of `bytes` split at any of `separators`
    /// (ASCII, so a split never cuts a UTF-8 sequence), with `.` dropped
    /// and `..` applied when `resolvingDots`.
    static func components(_ bytes: [UInt8], separators: Set<UInt8>, resolvingDots: Bool = true) -> [String] {
        var parts: [String] = []
        for piece in bytes.split(whereSeparator: { separators.contains($0) }) {
            let part = String(decoding: piece, as: UTF8.self)
            if resolvingDots, part == "." { continue }
            if resolvingDots, part == ".." {
                if !parts.isEmpty { parts.removeLast() }
                continue
            }
            parts.append(part)
        }
        return parts
    }

    static func lastComponent(_ bytes: [UInt8]) -> [UInt8] {
        guard let slash = bytes.lastIndex(of: UInt8(ascii: "/")) else { return bytes }
        return Array(bytes[(slash + 1)...])
    }

    static func isExe(_ name: String) -> Bool {
        let lowered = name.lowercased()
        return lowered.hasSuffix(".exe") && lowered.count > 4
    }

    static func isLetter(_ byte: UInt8) -> Bool {
        (UInt8(ascii: "a")...UInt8(ascii: "z")).contains(byte | 0x20)
    }
}
