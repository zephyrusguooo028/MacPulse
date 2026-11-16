import Foundation
import SwiftUI

enum Fmt {
    static func bytes(_ v: Int64) -> String {
        let b = Double(max(v, 0))
        if b < 1024 { return "\(Int(b)) B" }
        let units = ["KB", "MB", "GB", "TB"]
        var x = b / 1024
        var i = 0
        while x >= 1024 && i < units.count - 1 { x /= 1024; i += 1 }
        if x >= 100 { return String(format: "%.0f %@", x, units[i]) }
        if x >= 10 { return String(format: "%.1f %@", x, units[i]) }
        return String(format: "%.2f %@", x, units[i])
    }

    static func bytes(_ v: UInt64) -> String { bytes(Int64(clamping: v)) }
    static func bytes(_ v: Double) -> String { bytes(Int64(v)) }

    static func rate(_ bytesPerSec: Double) -> String { bytes(bytesPerSec) + "/s" }

    static func pct(_ v: Double, digits: Int = 0) -> String {
        String(format: "%.\(digits)f%%", v * 100)
    }

    static func relative(_ d: Date?) -> String {
        guard let d else { return "—" }
        let f = RelativeDateTimeFormatter()
        f.locale = Locale(identifier: "zh_CN")
        f.unitsStyle = .short
        return f.localizedString(for: d, relativeTo: Date())
    }

    static func daysAgo(_ d: Date?) -> Int {
        guard let d else { return 0 }
        return Int(Date().timeIntervalSince(d) / 86400)
    }

    static func time(_ d: Date) -> String {
        let f = DateFormatter()
        f.dateFormat = "HH:mm"
        return f.string(from: d)
    }

    static func clock(_ d: Date) -> String {
        let f = DateFormatter()
        f.dateFormat = "HH:mm:ss"
        return f.string(from: d)
    }

    static func dateTime(_ d: Date) -> String {
        let f = DateFormatter()
        f.dateFormat = "MM-dd HH:mm"
        return f.string(from: d)
    }
}

extension URL {
    /// Path with the home directory replaced by "~".
    var tildePath: String {
        let home = FileManager.default.homeDirectoryForCurrentUser.path
        return path.hasPrefix(home) ? "~" + path.dropFirst(home.count) : path
    }
}

enum Palette {
    static let cpu = Color(red: 0.25, green: 0.52, blue: 0.96)
    static let mem = Color(red: 0.62, green: 0.38, blue: 0.95)
    static let disk = Color(red: 0.13, green: 0.70, blue: 0.55)
    static let netIn = Color(red: 0.10, green: 0.65, blue: 0.85)
    static let netOut = Color(red: 0.96, green: 0.55, blue: 0.20)
    static let warn = Color(red: 0.96, green: 0.62, blue: 0.10)
    static let danger = Color(red: 0.92, green: 0.28, blue: 0.30)

    /// Green → amber → red as a 0…1 load rises.
    static func load(_ v: Double) -> Color {
        if v < 0.6 { return disk }
        if v < 0.85 { return warn }
        return danger
    }

    static let treemap: [Color] = [
        Color(red: 0.25, green: 0.52, blue: 0.96),
        Color(red: 0.62, green: 0.38, blue: 0.95),
        Color(red: 0.13, green: 0.70, blue: 0.55),
        Color(red: 0.96, green: 0.55, blue: 0.20),
        Color(red: 0.90, green: 0.33, blue: 0.55),
        Color(red: 0.10, green: 0.65, blue: 0.85),
        Color(red: 0.75, green: 0.65, blue: 0.15),
        Color(red: 0.45, green: 0.55, blue: 0.65),
    ]
}

enum FileSizer {
    /// Allocated size of a file or directory tree. Does not follow symlinks.
    static func size(of url: URL, cancel: (() -> Bool)? = nil) -> Int64 {
        let keys: [URLResourceKey] = [.totalFileAllocatedSizeKey, .fileAllocatedSizeKey, .isDirectoryKey, .isSymbolicLinkKey]
        guard let vals = try? url.resourceValues(forKeys: Set(keys)) else { return 0 }
        if vals.isSymbolicLink == true { return 0 }
        if vals.isDirectory != true {
            return Int64(vals.totalFileAllocatedSize ?? vals.fileAllocatedSize ?? 0)
        }
        var total: Int64 = 0
        guard let e = FileManager.default.enumerator(at: url, includingPropertiesForKeys: keys, options: [], errorHandler: { _, _ in true }) else { return 0 }
        var n = 0
        for case let f as URL in e {
            n += 1
            if n & 0xFFF == 0, cancel?() == true { break }
            guard let v = try? f.resourceValues(forKeys: [.totalFileAllocatedSizeKey, .fileAllocatedSizeKey, .isDirectoryKey]) else { continue }
            if v.isDirectory == true { continue }
            total += Int64(v.totalFileAllocatedSize ?? v.fileAllocatedSize ?? 0)
        }
        return total
    }

    static func exists(_ url: URL) -> Bool { FileManager.default.fileExists(atPath: url.path) }

    static func modified(_ url: URL) -> Date? {
        (try? url.resourceValues(forKeys: [.contentModificationDateKey]))?.contentModificationDate
    }
}

enum Paths {
    static let home = FileManager.default.homeDirectoryForCurrentUser
    static func h(_ rel: String) -> URL { home.appendingPathComponent(rel) }

    static var support: URL {
        let u = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("MacPulse", isDirectory: true)
        try? FileManager.default.createDirectory(at: u, withIntermediateDirectories: true)
        return u
    }
}
