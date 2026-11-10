import Darwin
import Foundation

/// The app's single-instance lock: an advisory flock on Runtime/app.lock.
/// The kernel drops it when the holder dies, so a crashed app never blocks the
/// next start, and it follows the file, not the path, so a second app that
/// reaches the folder through a link still sees it. CoreHost holds its own
/// lock (Runtime/corehost.lock) against a second CoreHost on the same data.
public final class InstanceLock: @unchecked Sendable {
    public let url: URL
    private let lock = NSLock()
    private var fd: Int32

    private init(url: URL, fd: Int32) {
        self.url = url
        self.fd = fd
    }

    public struct Failure: Error, Sendable, Equatable, CustomStringConvertible {
        public let call: String
        public let code: Int32
        public var description: String { "\(call) failed: \(String(cString: strerror(code)))" }
    }

    /// Takes the lock, or returns nil when another holder has it. Creates the
    /// file (never its folder) if needed. The descriptor is close-on-exec, so
    /// no helper inherits the lock.
    public static func acquire(at url: URL) throws(Failure) -> InstanceLock? {
        let fd = Darwin.open(url.path, O_RDWR | O_CREAT | O_CLOEXEC, 0o644)
        guard fd >= 0 else { throw Failure(call: "open", code: errno) }
        while flock(fd, LOCK_EX | LOCK_NB) != 0 {
            let code = errno
            if code == EINTR { continue }
            Darwin.close(fd)
            if code == EWOULDBLOCK { return nil }
            throw Failure(call: "flock", code: code)
        }
        return InstanceLock(url: url, fd: fd)
    }

    public var isHeld: Bool {
        lock.lock()
        defer { lock.unlock() }
        return fd >= 0
    }

    /// Releases the lock; the file stays, so the next holder needs no create.
    public func release() {
        lock.lock()
        defer { lock.unlock() }
        guard fd >= 0 else { return }
        Darwin.close(fd)
        fd = -1
    }

    deinit { release() }
}
