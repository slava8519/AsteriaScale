import Foundation

/// The app's own log, ~/Library/Logs/AsteriaScale/app.log (CoreHost writes
/// hover-translation.log beside it). Lines are appended on a private serial
/// queue, so a slow disk never blocks the main thread; at `maxBytes` the file
/// moves to app.log.1, replacing the previous one, so the folder never holds
/// more than two of them. English for the owner and for agents; the
/// Russian texts the app showed are quoted as they were.
public final class AppLog: @unchecked Sendable {
    public let url: URL
    public let maxBytes: Int
    private let queue = DispatchQueue(label: "asteria.applog")

    public init(url: URL, maxBytes: Int = 1 << 20) {
        self.url = url
        self.maxBytes = maxBytes
    }

    public func write(_ line: String) {
        let stamp = ISO8601DateFormatter.string(from: Date(), timeZone: .current,
                                                formatOptions: [.withInternetDateTime, .withFractionalSeconds])
        let data = Data("\(stamp) \(line)\n".utf8)
        queue.async { [url, maxBytes] in AppLog.append(data, to: url, maxBytes: maxBytes) }
    }

    /// Waits until every line written so far is on disk.
    public func flush() {
        queue.sync {}
    }

    private static func append(_ data: Data, to url: URL, maxBytes: Int) {
        let manager = FileManager.default
        if let size = (try? manager.attributesOfItem(atPath: url.path))?[.size] as? Int, size + data.count > maxBytes {
            let previous = url.appendingPathExtension("1")
            try? manager.removeItem(at: previous)
            try? manager.moveItem(at: url, to: previous)
        }
        if let handle = try? FileHandle(forWritingTo: url) {
            defer { try? handle.close() }
            _ = try? handle.seekToEnd()
            try? handle.write(contentsOf: data)
        } else {
            try? data.write(to: url)
        }
    }
}
