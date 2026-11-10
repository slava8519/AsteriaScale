import Darwin
import Foundation

/// What the supervisor records about the CoreHost it runs (Runtime/corehost.pid).
public struct HostRecord: Codable, Sendable, Equatable {
    public let pid: Int32
    public let pgid: Int32
    public let start: ProcessStartTime
    public let executable: String

    public init(pid: Int32, pgid: Int32, start: ProcessStartTime, executable: String) {
        self.pid = pid
        self.pgid = pgid
        self.start = start
        self.executable = executable
    }
}

public struct SweepReport: Sendable, Equatable {
    public var killed: [pid_t] = []
    public var spared: [pid_t] = []
    public var note: String

    public init(killed: [pid_t] = [], spared: [pid_t] = [], note: String) {
        self.killed = killed
        self.spared = spared
        self.note = note
    }
}

/// Cleans up after an app that died without stopping CoreHost (SIGKILL, a
/// crash): CoreHost then sees stdin EOF and kills its group itself, but a
/// helper that ignores its parent (llama-server from 20.5) or a CoreHost that
/// is hung would survive. At the next start the app reads the record and kills
/// the members of the recorded group that are provably ours:
/// - the leader only if its start time equals the recorded one (otherwise the
///   pid was reused and the whole group is somebody else's);
/// - another member only if it started no earlier than the recorded leader:
///   a process group id cannot be reused while the group exists, so a member
///   that started later descends from our CoreHost;
/// - and in both cases only if its executable lies under an allowed prefix
///   (the bundle's helpers, the TranslateGemma runtime).
public enum OrphanSweep {
    public struct Failure: Error, Sendable, Equatable {
        public let reason: String
    }

    public static func record(pid: pid_t, executable: String, to url: URL) throws(Failure) {
        guard let start = ProcessTable.startTime(pid) else {
            throw Failure(reason: "pid \(pid) has no start time (already gone)")
        }
        let record = HostRecord(pid: pid, pgid: pid, start: start, executable: executable)
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys]
        do {
            try encoder.encode(record).write(to: url, options: .atomic)
        } catch {
            throw Failure(reason: "cannot write \(url.path): \(error)")
        }
    }

    public static func read(_ url: URL) -> HostRecord? {
        guard let data = try? Data(contentsOf: url) else { return nil }
        return try? JSONDecoder().decode(HostRecord.self, from: data)
    }

    /// Kills what the record proves to be ours, waits up to 1 s for it to be
    /// gone (so a new CoreHost finds corehost.lock free), and removes the
    /// record in every case.
    public static func sweep(recordAt url: URL, allowedPrefixes: [String]) -> SweepReport {
        guard FileManager.default.fileExists(atPath: url.path) else { return SweepReport(note: "no record") }
        defer { try? FileManager.default.removeItem(at: url) }
        guard let record = read(url) else { return SweepReport(note: "unreadable record removed") }
        guard record.pid > 1, record.pgid == record.pid else {
            return SweepReport(note: "record names no group leader (pid \(record.pid), pgid \(record.pgid))")
        }
        let prefixes = allowedPrefixes.map(normalizedPrefix)
        let members = ProcessTable.liveMembers(record.pgid)
        if members.isEmpty { return SweepReport(note: "group \(record.pgid) is gone") }
        if members.contains(record.pid), ProcessTable.startTime(record.pid) != record.start {
            return SweepReport(spared: members, note: "pid \(record.pid) was reused; group \(record.pgid) is not ours")
        }
        var report = SweepReport(note: "group \(record.pgid)")
        for member in members {
            guard let start = ProcessTable.startTime(member), let path = ProcessTable.executablePath(member) else {
                report.spared.append(member)
                continue
            }
            let startMatches = member == record.pid ? start == record.start : start >= record.start
            let pathAllowed = prefixes.contains { path.hasPrefix($0) }
            if startMatches, pathAllowed, Darwin.kill(member, SIGKILL) == 0 {
                report.killed.append(member)
            } else {
                report.spared.append(member)
            }
        }
        let deadline = ContinuousClock.now + .seconds(1)
        while report.killed.contains(where: ProcessTable.isAlive), ContinuousClock.now < deadline {
            usleep(1_000)
        }
        return report
    }

    /// proc_pidpath reports the resolved path, so a prefix is resolved too and
    /// ends in "/" ("/bin" must not admit "/binx/…").
    static func normalizedPrefix(_ prefix: String) -> String {
        let resolved = URL(fileURLWithPath: prefix).resolvingSymlinksInPath().path
        return resolved.hasSuffix("/") ? resolved : resolved + "/"
    }
}
