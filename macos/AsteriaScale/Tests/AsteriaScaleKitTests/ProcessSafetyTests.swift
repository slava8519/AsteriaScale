import Darwin
import Foundation
import Testing
@testable import AsteriaScaleKit

extension ProcessSuites {
    @Suite struct InstanceLockTests {
        static let tryLock = "import fcntl,sys\nf=open(sys.argv[1],'a')\ntry:\n fcntl.flock(f,fcntl.LOCK_EX|fcntl.LOCK_NB)\n "
            + "sys.stdout.write('got')\nexcept OSError:\n sys.stdout.write('busy')"

        static func otherProcessTries(_ url: URL, _ scratch: Scratch) throws -> String {
            let child = try ChildProcess.spawn(executable: Fake.python, arguments: ["-I", "-B", "-c", tryLock, url.path],
                                               environment: Fake.environment(scratch), workingDirectory: scratch.cwd.path)
            defer { _ = ChildProcess.reap(child.pid); child.closeParentEnds() }
            return readAll(child.stdoutRead)
        }

        @Test func refusesASecondHolder() throws {
            let scratch = try Scratch("lock")
            defer { scratch.remove() }
            let url = scratch.root.appendingPathComponent("app.lock")
            let first = try #require(try InstanceLock.acquire(at: url))
            #expect(first.isHeld)
            #expect(try InstanceLock.acquire(at: url) == nil, "a second holder in this process")
            #expect(try Self.otherProcessTries(url, scratch) == "busy", "a second holder in another process")
            first.release()
            #expect(!first.isHeld)
            #expect(try Self.otherProcessTries(url, scratch) == "got")
            let second = try #require(try InstanceLock.acquire(at: url))
            second.release()
            #expect(throws: InstanceLock.Failure.self) {
                _ = try InstanceLock.acquire(at: scratch.root.appendingPathComponent("missing/app.lock"))
            }
        }
    }

    @Suite struct OrphanSweepTests {
        static func now() -> ProcessStartTime {
            var time = timeval()
            gettimeofday(&time, nil)
            return ProcessStartTime(seconds: UInt64(time.tv_sec), microseconds: UInt64(time.tv_usec))
        }

        static func write(_ record: HostRecord, to url: URL) throws {
            try JSONEncoder().encode(record).write(to: url)
        }

        @Test func killsALeaderWhosePathAndStartTimeMatch() throws {
            let scratch = try Scratch("sweep-leader")
            defer { scratch.remove() }
            let record = scratch.root.appendingPathComponent("corehost.pid")
            let child = try ChildProcess.spawn(executable: "/bin/sleep", arguments: [sleepMarker], environment: [:],
                                               workingDirectory: scratch.cwd.path)
            defer { child.closeParentEnds() }
            try OrphanSweep.record(pid: child.pid, executable: "/bin/sleep", to: record)
            #expect(OrphanSweep.read(record)?.start == ProcessTable.startTime(child.pid))
            let report = OrphanSweep.sweep(recordAt: record, allowedPrefixes: ["/bin"])
            #expect(report.killed == [child.pid] && report.spared.isEmpty, "\(report)")
            #expect(ChildProcess.reap(child.pid) == .signaled(SIGKILL))
            #expect(!FileManager.default.fileExists(atPath: record.path))
        }

        @Test func sparesAMismatchingStartTimeOrPath() throws {
            let scratch = try Scratch("sweep-spare")
            defer { scratch.remove() }
            let record = scratch.root.appendingPathComponent("corehost.pid")
            let child = try ChildProcess.spawn(executable: "/bin/sleep", arguments: [sleepMarker], environment: [:],
                                               workingDirectory: scratch.cwd.path)
            defer { killAndReap(child) }
            let start = try #require(ProcessTable.startTime(child.pid))
            // A reused pid: the recorded start is not this process's.
            try Self.write(HostRecord(pid: child.pid, pgid: child.pid,
                                      start: ProcessStartTime(seconds: start.seconds - 1, microseconds: start.microseconds),
                                      executable: "/bin/sleep"), to: record)
            var report = OrphanSweep.sweep(recordAt: record, allowedPrefixes: ["/bin/"])
            #expect(report.killed.isEmpty && report.spared == [child.pid], "\(report)")
            #expect(ProcessTable.isAlive(child.pid))
            // The right process, but its executable lies outside the allowed prefixes.
            try OrphanSweep.record(pid: child.pid, executable: "/bin/sleep", to: record)
            report = OrphanSweep.sweep(recordAt: record, allowedPrefixes: ["/usr/bin/", "/bi"])
            #expect(report.killed.isEmpty && report.spared == [child.pid], "\(report)")
            #expect(ProcessTable.isAlive(child.pid))
            #expect(!FileManager.default.fileExists(atPath: record.path))
        }

        /// The leader is gone (as after an app SIGKILL), one member is left in
        /// the group, reparented to launchd: it started after the recorded
        /// leader, so it is ours and dies.
        @Test func killsAnOrphanedMemberWhoseLeaderExited() throws {
            let scratch = try Scratch("sweep-orphan")
            defer { scratch.remove() }
            let record = scratch.root.appendingPathComponent("corehost.pid")
            let before = Self.now()
            let leader = try ChildProcess.spawn(executable: "/bin/sh", arguments: ["-c", "/bin/sleep \(sleepMarker) & exit 0"],
                                                environment: ["PATH": "/usr/bin:/bin"], workingDirectory: scratch.cwd.path)
            leader.closeParentEnds()
            #expect(ChildProcess.reap(leader.pid) == .exited(0))
            // The background job may still be sh between its fork and exec.
            let deadline = ContinuousClock.now + .seconds(2)
            var members = ProcessTable.liveMembers(leader.pid)
            while ContinuousClock.now < deadline, members.count != 1 || ProcessTable.executablePath(members[0]) != "/bin/sleep" {
                usleep(2_000)
                members = ProcessTable.liveMembers(leader.pid)
            }
            #expect(members.count == 1, "the background sleep stays in the group: \(members)")
            let orphan = try #require(members.first)
            #expect(ProcessTable.executablePath(orphan) == "/bin/sleep")
            try Self.write(HostRecord(pid: leader.pid, pgid: leader.pid, start: before, executable: "/bin/sh"), to: record)
            let report = OrphanSweep.sweep(recordAt: record, allowedPrefixes: ["/bin/"])
            #expect(report.killed == [orphan], "\(report)")
            #expect(!ProcessTable.isAlive(orphan))
            #expect(ProcessTable.liveMembers(leader.pid).isEmpty)
        }

        @Test func ignoresAMissingBrokenOrStaleRecord() throws {
            let scratch = try Scratch("sweep-missing")
            defer { scratch.remove() }
            let record = scratch.root.appendingPathComponent("corehost.pid")
            #expect(OrphanSweep.sweep(recordAt: record, allowedPrefixes: ["/"]) == SweepReport(note: "no record"))
            try Data("not json".utf8).write(to: record)
            #expect(OrphanSweep.sweep(recordAt: record, allowedPrefixes: ["/"]).note == "unreadable record removed")
            #expect(!FileManager.default.fileExists(atPath: record.path))
            // Prefixes that match nothing: a broken guard must not be able to
            // kill anything here.
            let nowhere = ["/nonexistent-prefix/"]
            try Self.write(HostRecord(pid: 1, pgid: 1, start: Self.now(), executable: "/sbin/launchd"), to: record)
            let launchd = OrphanSweep.sweep(recordAt: record, allowedPrefixes: nowhere)
            #expect(launchd.killed.isEmpty && launchd.note.hasPrefix("record names no group leader"), "\(launchd)")
            try Self.write(HostRecord(pid: 4242, pgid: 4243, start: Self.now(), executable: "/x"), to: record)
            #expect(OrphanSweep.sweep(recordAt: record, allowedPrefixes: nowhere).note.hasPrefix("record names no group leader"))
            // A group that no longer exists: the pid of an exited child.
            let gone = try ChildProcess.spawn(executable: "/usr/bin/true", arguments: [], environment: [:],
                                              workingDirectory: scratch.cwd.path)
            gone.closeParentEnds()
            _ = ChildProcess.reap(gone.pid)
            try Self.write(HostRecord(pid: gone.pid, pgid: gone.pid, start: Self.now(), executable: "/usr/bin/true"), to: record)
            #expect(OrphanSweep.sweep(recordAt: record, allowedPrefixes: nowhere) == SweepReport(note: "group \(gone.pid) is gone"))
        }
    }
}
