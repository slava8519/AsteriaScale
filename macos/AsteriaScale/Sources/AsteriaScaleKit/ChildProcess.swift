import Darwin
import Foundation

/// A helper process leading its own process group, with three pipes.
public struct SpawnedChild: Sendable {
    public let pid: pid_t
    public let stdinWrite: Int32
    public let stdoutRead: Int32
    public let stderrRead: Int32

    /// SETPGROUP with 0 makes the child the leader of a group named after it.
    public var pgid: pid_t { pid }

    public func closeParentEnds() {
        for fd in [stdinWrite, stdoutRead, stderrRead] { Darwin.close(fd) }
    }
}

public struct SpawnError: Error, Sendable, Equatable, CustomStringConvertible {
    public let call: String
    public let code: Int32
    public var description: String { "\(call) failed: \(String(cString: strerror(code)))" }
}

/// How a helper ended: an exit code or the signal that killed it.
public enum ExitStatus: Sendable, Equatable, CustomStringConvertible {
    case exited(Int32)
    case signaled(Int32)
    /// waitpid failed (someone else reaped the child); never expected.
    case unknown

    init(waitStatus status: Int32) {
        // <sys/wait.h> macros are not imported into Swift.
        if status & 0x7F == 0 {
            self = .exited((status >> 8) & 0xFF)
        } else {
            self = .signaled(status & 0x7F)
        }
    }

    public var description: String {
        switch self {
        case .exited(let code): return "exit \(code)"
        case .signaled(let signal): return "signal \(signal)"
        case .unknown: return "unknown"
        }
    }
}

public enum ChildProcess {
    /// Serialises pipe creation: a pipe end must be close-on-exec before any
    /// other thread of this process can spawn, and macOS has no pipe2.
    private static let pipeGate = NSLock()

    /// Starts `executable` with posix_spawn, because these guarantees are
    /// explicit there rather than observed behaviour of Foundation.Process:
    /// - SETPGROUP(0): the child leads a new process group, so one killpg
    ///   reaches CoreHost, the ranker worker and llama-server together.
    /// - CLOEXEC_DEFAULT: the child inherits exactly fds 0, 1 and 2.
    /// - SETSIGMASK with an empty mask: a thread of a dispatch queue or of the
    ///   Swift concurrency pool blocks SIGTERM and SIGCHLD, posix_spawn
    ///   passes the calling thread's mask on, and .NET keeps it, so without
    ///   this CoreHost would ignore SIGTERM and Process.WaitForExit would hang
    ///   (CoreHost reports the mask it got in core.hello).
    /// - SETSIGDEF(SIGPIPE): the app ignores SIGPIPE, the child must not.
    /// - addchdir_np: a fixed working directory (the non-_np form is macOS 26+).
    /// - F_SETNOSIGPIPE on our write end: writing to a dead helper returns
    ///   EPIPE instead of killing the app, whatever its SIGPIPE disposition.
    public static func spawn(executable: String, arguments: [String], environment: [String: String],
                             workingDirectory: String) throws(SpawnError) -> SpawnedChild {
        var input: [Int32] = [-1, -1], output: [Int32] = [-1, -1], errors: [Int32] = [-1, -1]
        pipeGate.lock()
        let pipeFailure: SpawnError? = {
            for index in 0..<3 {
                var ends: [Int32] = [-1, -1]
                guard pipe(&ends) == 0 else { return SpawnError(call: "pipe", code: errno) }
                for fd in ends { _ = fcntl(fd, F_SETFD, FD_CLOEXEC) }
                switch index {
                case 0: input = ends
                case 1: output = ends
                default: errors = ends
                }
            }
            return nil
        }()
        pipeGate.unlock()
        let parentEnds = [input[1], output[0], errors[0]]
        let childEnds = [input[0], output[1], errors[1]]
        if let pipeFailure {
            closeAll(parentEnds + childEnds)
            throw pipeFailure
        }

        var actions: posix_spawn_file_actions_t?
        posix_spawn_file_actions_init(&actions)
        defer { posix_spawn_file_actions_destroy(&actions) }
        posix_spawn_file_actions_adddup2(&actions, input[0], 0)
        posix_spawn_file_actions_adddup2(&actions, output[1], 1)
        posix_spawn_file_actions_adddup2(&actions, errors[1], 2)
        posix_spawn_file_actions_addchdir_np(&actions, workingDirectory)

        var attributes: posix_spawnattr_t?
        posix_spawnattr_init(&attributes)
        defer { posix_spawnattr_destroy(&attributes) }
        posix_spawnattr_setflags(&attributes, Int16(POSIX_SPAWN_SETPGROUP | POSIX_SPAWN_CLOEXEC_DEFAULT
                                                     | POSIX_SPAWN_SETSIGDEF | POSIX_SPAWN_SETSIGMASK))
        posix_spawnattr_setpgroup(&attributes, 0)
        var defaults = sigset_t()
        sigemptyset(&defaults)
        sigaddset(&defaults, SIGPIPE)
        posix_spawnattr_setsigdefault(&attributes, &defaults)
        var mask = sigset_t()
        sigemptyset(&mask)
        posix_spawnattr_setsigmask(&attributes, &mask)

        let argv: [UnsafeMutablePointer<CChar>?] = ([executable] + arguments).map { strdup($0) } + [nil]
        let envp: [UnsafeMutablePointer<CChar>?] = environment.sorted { $0.key < $1.key }
            .map { strdup("\($0.key)=\($0.value)") } + [nil]
        defer {
            for pointer in argv { free(pointer) }
            for pointer in envp { free(pointer) }
        }

        var pid: pid_t = 0
        let result = posix_spawn(&pid, executable, &actions, &attributes, argv, envp)
        closeAll(childEnds)
        guard result == 0 else {
            closeAll(parentEnds)
            throw SpawnError(call: "posix_spawn", code: result)
        }
        _ = fcntl(input[1], F_SETNOSIGPIPE, 1)
        for fd in [output[0], errors[0]] {
            let flags = fcntl(fd, F_GETFL)
            _ = fcntl(fd, F_SETFL, flags | O_NONBLOCK)
        }
        return SpawnedChild(pid: pid, stdinWrite: input[1], stdoutRead: output[0], stderrRead: errors[0])
    }

    /// Blocks until `pid` has ended but leaves it a zombie (WNOWAIT): while
    /// the zombie exists, nobody can reuse its pid or its process group id,
    /// so a killpg after this still reaches only our group.
    public static func waitUntilEnded(_ pid: pid_t) {
        var info = siginfo_t()
        while waitid(P_PID, id_t(pid), &info, WEXITED | WNOWAIT) == -1 {
            if errno != EINTR { return }
        }
    }

    /// Reaps `pid`. Call it only after the group was killed (see waitUntilEnded).
    public static func reap(_ pid: pid_t) -> ExitStatus {
        var status: Int32 = 0
        while waitpid(pid, &status, 0) == -1 {
            if errno != EINTR { return .unknown }
        }
        return ExitStatus(waitStatus: status)
    }

    static func closeAll(_ fds: [Int32]) {
        for fd in fds where fd >= 0 { Darwin.close(fd) }
    }
}

/// The kernel's view of processes, through libproc and sysctl only.
public enum ProcessTable {
    /// Every member of a process group, zombies included; empty when the
    /// group does not exist.
    public static func groupMembers(_ pgid: pid_t) -> [pid_t] {
        var capacity = 256
        while true {
            var buffer = [pid_t](repeating: 0, count: capacity)
            let count = Int(proc_listpgrppids(pgid, &buffer, Int32(capacity * MemoryLayout<pid_t>.stride)))
            if count <= 0 { return [] }
            if count < capacity { return Array(buffer.prefix(count)) }
            capacity *= 4
        }
    }

    /// True for a process that exists and has not exited. A zombie is not
    /// alive, although kill(pid, 0) still succeeds on it.
    public static func isAlive(_ pid: pid_t) -> Bool {
        guard let info = kinfo(pid) else { return false }
        return info.kp_proc.p_stat != SZOMB
    }

    /// The group's members that have not exited yet.
    public static func liveMembers(_ pgid: pid_t) -> [pid_t] {
        groupMembers(pgid).filter(isAlive)
    }

    public static func startTime(_ pid: pid_t) -> ProcessStartTime? {
        var info = proc_bsdinfo()
        let size = Int32(MemoryLayout<proc_bsdinfo>.stride)
        guard proc_pidinfo(pid, PROC_PIDTBSDINFO, 0, &info, size) == size else { return nil }
        return ProcessStartTime(seconds: info.pbi_start_tvsec, microseconds: info.pbi_start_tvusec)
    }

    public static func executablePath(_ pid: pid_t) -> String? {
        var buffer = [UInt8](repeating: 0, count: 4 * Int(MAXPATHLEN))
        let length = proc_pidpath(pid, &buffer, UInt32(buffer.count))
        guard length > 0 else { return nil }
        return String(decoding: buffer.prefix(Int(length)), as: UTF8.self)
    }

    /// The process's ignored-signal set as the kernel reports it (kinfo_proc).
    /// The kernel does not report a thread's blocked mask this way: the
    /// p_sigmask field is always zero, so only the process itself can say
    /// which signals it blocks (CoreHost does so in core.hello).
    public static func ignoredSignals(_ pid: pid_t) -> UInt32? {
        kinfo(pid)?.kp_proc.p_sigignore
    }

    static func kinfo(_ pid: pid_t) -> kinfo_proc? {
        var info = kinfo_proc()
        var size = MemoryLayout<kinfo_proc>.stride
        var mib: [Int32] = [CTL_KERN, KERN_PROC, KERN_PROC_PID, pid]
        guard sysctl(&mib, 4, &info, &size, nil, 0) == 0, size == MemoryLayout<kinfo_proc>.stride,
              info.kp_proc.p_pid == pid else { return nil }
        return info
    }

    /// Waits until no member of the group is alive, up to `timeout`.
    @discardableResult
    public static func waitUntilGroupDead(_ pgid: pid_t, timeout: Duration) -> Bool {
        let deadline = ContinuousClock.now + timeout
        while true {
            if liveMembers(pgid).isEmpty { return true }
            if ContinuousClock.now >= deadline { return false }
            usleep(1_000)
        }
    }
}

/// A process start time as the kernel records it, which tells a process from
/// a later one that reused its pid.
public struct ProcessStartTime: Sendable, Hashable, Comparable, Codable, CustomStringConvertible {
    public let seconds: UInt64
    public let microseconds: UInt64

    public init(seconds: UInt64, microseconds: UInt64) {
        self.seconds = seconds
        self.microseconds = microseconds
    }

    public static func < (lhs: ProcessStartTime, rhs: ProcessStartTime) -> Bool {
        (lhs.seconds, lhs.microseconds) < (rhs.seconds, rhs.microseconds)
    }

    public var description: String { "\(seconds).\(String(format: "%06llu", microseconds))" }
}
