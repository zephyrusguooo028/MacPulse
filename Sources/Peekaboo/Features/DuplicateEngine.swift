import Foundation
import CryptoKit
import ImageIO
import CoreGraphics

/// One file that takes part in a duplicate or similar-image group.
struct DupFile: Identifiable, Hashable, Sendable {
    let url: URL
    let size: Int64
    let modified: Date?
    var id: URL { url }
    var isImage: Bool { DuplicateEngine.imageExtensions.contains(url.pathExtension.lowercased()) }
}

enum DupKind: String, Sendable { case exact, similar }

struct DupGroup: Identifiable, Hashable, Sendable {
    let id: String
    let kind: DupKind
    let files: [DupFile]
    var totalSize: Int64 { files.reduce(0) { $0 + $1.size } }
    /// Space freed by keeping one file: for exact copies every copy is the same size;
    /// for similar images we assume the largest one is kept.
    var reclaimable: Int64 {
        guard files.count > 1 else { return 0 }
        return kind == .exact ? files[0].size * Int64(files.count - 1) : totalSize - (files.map(\.size).max() ?? 0)
    }
}

/// Thread-safe progress and cancellation shared between the scan and the UI.
final class DupProgress: @unchecked Sendable {
    private let lock = NSLock()
    private var _cancelled = false
    private var _phase = ""
    private var _done = 0
    private var _total = 0

    var cancelled: Bool { lock.lock(); defer { lock.unlock() }; return _cancelled }
    func cancel() { lock.lock(); _cancelled = true; lock.unlock() }
    func set(_ phase: String, total: Int) { lock.lock(); _phase = phase; _done = 0; _total = total; lock.unlock() }
    func tick(_ n: Int = 1) { lock.lock(); _done += n; lock.unlock() }
    var snapshot: (phase: String, done: Int, total: Int) { lock.lock(); defer { lock.unlock() }; return (_phase, _done, _total) }
}

/// Finds exact duplicates (size → first 64 KB → full SHA-256) and visually similar
/// images (dHash, Hamming distance ≤ 5). Never modifies anything; read-only.
struct DuplicateEngine: Sendable {
    static let imageExtensions: Set<String> = ["jpg", "jpeg", "png", "heic", "webp", "gif", "bmp", "tiff", "tif"]
    static let skipFolderNames: Set<String> = ["node_modules", ".git", ".svn", ".hg", "Pods", ".Trash"]
    static let skipPackageExtensions: Set<String> = ["app", "photoslibrary", "photolibrary", "musiclibrary", "tvlibrary", "fcpbundle", "imovielibrary", "aplibrary", "framework", "bundle", "xcodeproj", "xcworkspace"]
    static let partialBytes = 64 * 1024
    static let similarThreshold = 5

    let roots: [URL]
    let minSize: Int64
    let progress: DupProgress

    struct Result: Sendable {
        var exact: [DupGroup] = []
        var similar: [DupGroup] = []
        var scannedFiles = 0
    }

    func run() -> Result {
        var r = Result()
        let files = collect()
        r.scannedFiles = files.count
        if progress.cancelled { return r }
        r.exact = exactGroups(files)
        if progress.cancelled { return r }
        // Exact copies collapse to one representative so they don't show up twice.
        var inExact = Set<URL>()
        for g in r.exact { for f in g.files.dropFirst() { inExact.insert(f.url) } }
        let images = files.filter { $0.isImage && !inExact.contains($0.url) }
        r.similar = similarGroups(images)
        return r
    }

    // MARK: Listing

    func collect() -> [DupFile] {
        progress.set(L("正在列出文件…", "Listing files…"), total: 0)
        let fm = FileManager.default
        let keys: [URLResourceKey] = [.isRegularFileKey, .isDirectoryKey, .isSymbolicLinkKey, .isPackageKey, .isHiddenKey,
                                      .fileSizeKey, .contentModificationDateKey, .fileResourceIdentifierKey,
                                      .isUbiquitousItemKey, .ubiquitousItemDownloadingStatusKey]
        var seenPaths = Set<String>()
        var seenInodes = Set<NSObject>()
        var out: [DupFile] = []
        for root in roots {
            guard let e = fm.enumerator(at: root, includingPropertiesForKeys: keys,
                                        options: [.skipsHiddenFiles, .skipsPackageDescendants],
                                        errorHandler: { _, _ in true }) else { continue }
            for case let u as URL in e {
                if progress.cancelled { return out }
                guard let v = try? u.resourceValues(forKeys: Set(keys)) else { continue }
                if v.isSymbolicLink == true { continue }
                if v.isHidden == true { if v.isDirectory == true { e.skipDescendants() }; continue }
                if v.isDirectory == true {
                    let name = u.lastPathComponent
                    if Self.skipFolderNames.contains(name) || v.isPackage == true || Self.skipPackageExtensions.contains(u.pathExtension.lowercased()) {
                        e.skipDescendants()
                    }
                    continue
                }
                guard v.isRegularFile == true, let size = v.fileSize, Int64(size) >= minSize else { continue }
                // iCloud files that are only in the cloud would be downloaded by reading them.
                if v.isUbiquitousItem == true, v.ubiquitousItemDownloadingStatus != .current { continue }
                let path = u.standardizedFileURL.path
                guard seenPaths.insert(path).inserted else { continue }
                // Hard links are the same file under two names: deleting one frees nothing.
                if let id = v.fileResourceIdentifier as? NSObject, !seenInodes.insert(id).inserted { continue }
                out.append(DupFile(url: u.standardizedFileURL, size: Int64(size), modified: v.contentModificationDate))
                progress.tick()
            }
        }
        return out
    }

    // MARK: Exact duplicates

    func exactGroups(_ files: [DupFile]) -> [DupGroup] {
        let bySize = Dictionary(grouping: files, by: \.size).values.filter { $0.count > 1 }
        let candidates = bySize.flatMap { $0 }
        progress.set(L("正在比较文件开头…", "Comparing file headers…"), total: candidates.count)
        let partial = hashAll(candidates) { Self.sha256(of: $0.url, limit: Self.partialBytes) }
        if progress.cancelled { return [] }

        // Files that agree on size and partial hash need a full hash, unless the
        // partial hash already covered the whole file.
        var needFull: [DupFile] = []
        var groups: [[DupFile]] = []
        for sameSize in bySize {
            let byPartial = Dictionary(grouping: sameSize.filter { partial[$0.url] != nil }) { partial[$0.url]! }.values.filter { $0.count > 1 }
            for g in byPartial {
                if g[0].size <= Int64(Self.partialBytes) { groups.append(g) } else { needFull.append(contentsOf: g) }
            }
        }
        progress.set(L("正在计算完整指纹…", "Hashing full files…"), total: needFull.count)
        let full = hashAll(needFull) { Self.sha256(of: $0.url, limit: nil, cancel: progress) }
        if progress.cancelled { return [] }
        let byFull = Dictionary(grouping: needFull.filter { full[$0.url] != nil }) { "\($0.size)-\(full[$0.url]!)" }.values.filter { $0.count > 1 }
        groups.append(contentsOf: byFull)

        return groups.map { g in
            let files = g.sorted { ($0.modified ?? .distantPast) > ($1.modified ?? .distantPast) }
            return DupGroup(id: "x-" + files.map(\.url.path).sorted().joined(separator: "|"), kind: .exact, files: files)
        }
        .sorted { $0.reclaimable > $1.reclaimable }
    }

    /// Runs `hash` over the files with bounded concurrency (4 at a time: disk-bound work
    /// gains little beyond that and would starve the rest of the system).
    private func hashAll(_ files: [DupFile], _ hash: @escaping @Sendable (DupFile) -> String?) -> [URL: String] {
        var out: [URL: String] = [:]
        let lock = NSLock()
        let q = OperationQueue()
        q.maxConcurrentOperationCount = 4
        q.qualityOfService = .utility
        for f in files {
            q.addOperation {
                if self.progress.cancelled { return }
                let h = hash(f)
                lock.lock(); if let h { out[f.url] = h }; lock.unlock()
                self.progress.tick()
            }
        }
        q.waitUntilAllOperationsAreFinished()
        return out
    }

    static func sha256(of url: URL, limit: Int?, cancel: DupProgress? = nil) -> String? {
        guard let h = try? FileHandle(forReadingFrom: url) else { return nil }
        defer { try? h.close() }
        var hasher = SHA256()
        var remaining = limit ?? Int.max
        let chunk = 1 << 20
        while remaining > 0 {
            if cancel?.cancelled == true { return nil }
            guard let d = try? h.read(upToCount: min(chunk, remaining)), !d.isEmpty else { break }
            hasher.update(data: d)
            remaining -= d.count
        }
        return hasher.finalize().map { String(format: "%02x", $0) }.joined()
    }

    // MARK: Similar images

    func similarGroups(_ images: [DupFile]) -> [DupGroup] {
        progress.set(L("正在比较图片…", "Comparing images…"), total: images.count)
        var hashes = [UInt64?](repeating: nil, count: images.count)
        let lock = NSLock()
        let q = OperationQueue()
        q.maxConcurrentOperationCount = 4
        q.qualityOfService = .utility
        for (i, f) in images.enumerated() {
            q.addOperation {
                if self.progress.cancelled { return }
                let h = Self.dHash(f.url)
                lock.lock(); hashes[i] = h; lock.unlock()
                self.progress.tick()
            }
        }
        q.waitUntilAllOperationsAreFinished()
        if progress.cancelled { return [] }

        // Greedy clustering around a seed: everything within the threshold of the seed
        // joins its group. (Union-find would chain loosely related pictures together.)
        let items = images.indices.compactMap { i -> (DupFile, UInt64)? in
            guard let h = hashes[i], h != 0, h != .max else { return nil }   // flat images all hash alike
            return (images[i], h)
        }
        var used = [Bool](repeating: false, count: items.count)
        var groups: [DupGroup] = []
        for i in items.indices where !used[i] {
            var members = [items[i].0]
            for j in (i + 1)..<items.count where !used[j] {
                if (items[i].1 ^ items[j].1).nonzeroBitCount <= Self.similarThreshold {
                    used[j] = true
                    members.append(items[j].0)
                }
            }
            guard members.count > 1 else { continue }
            used[i] = true
            let files = members.sorted { ($0.modified ?? .distantPast) > ($1.modified ?? .distantPast) }
            groups.append(DupGroup(id: "s-" + files.map(\.url.path).sorted().joined(separator: "|"), kind: .similar, files: files))
        }
        return groups.sorted { $0.reclaimable > $1.reclaimable }
    }

    /// Difference hash: 9×8 grayscale, one bit per horizontally adjacent pair.
    static func dHash(_ url: URL) -> UInt64? {
        guard let src = CGImageSourceCreateWithURL(url as CFURL, [kCGImageSourceShouldCache: false] as CFDictionary) else { return nil }
        let opts: [CFString: Any] = [
            kCGImageSourceCreateThumbnailFromImageAlways: true,
            kCGImageSourceCreateThumbnailWithTransform: true,
            kCGImageSourceThumbnailMaxPixelSize: 64,
            kCGImageSourceShouldCacheImmediately: false,
        ]
        guard let thumb = CGImageSourceCreateThumbnailAtIndex(src, 0, opts as CFDictionary) else { return nil }
        let w = 9, h = 8
        var px = [UInt8](repeating: 0, count: w * h)
        let ok = px.withUnsafeMutableBytes { buf -> Bool in
            guard let ctx = CGContext(data: buf.baseAddress, width: w, height: h, bitsPerComponent: 8, bytesPerRow: w,
                                      space: CGColorSpaceCreateDeviceGray(), bitmapInfo: CGImageAlphaInfo.none.rawValue) else { return false }
            ctx.interpolationQuality = .high
            ctx.setFillColor(gray: 1, alpha: 1)   // transparent areas read as white
            ctx.fill(CGRect(x: 0, y: 0, width: w, height: h))
            ctx.draw(thumb, in: CGRect(x: 0, y: 0, width: w, height: h))
            return true
        }
        guard ok else { return nil }
        var hash: UInt64 = 0
        for y in 0..<h {
            for x in 0..<(w - 1) {
                hash <<= 1
                if px[y * w + x] > px[y * w + x + 1] { hash |= 1 }
            }
        }
        return hash
    }
}
