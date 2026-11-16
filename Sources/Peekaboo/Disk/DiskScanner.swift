import Foundation
import Observation

/// One node of the scanned tree. Small files are folded into a single
/// "其他文件" child so the tree stays small enough to keep in memory.
final class DiskNode: Identifiable, Hashable {
    let id = UUID()
    let url: URL
    let name: String
    let isDirectory: Bool
    var size: Int64
    var children: [DiskNode]
    weak var parent: DiskNode?
    let isAggregate: Bool
    var fileCount: Int

    init(url: URL, name: String? = nil, isDirectory: Bool, size: Int64 = 0, children: [DiskNode] = [], isAggregate: Bool = false, fileCount: Int = 0) {
        self.url = url
        self.name = name ?? url.lastPathComponent
        self.isDirectory = isDirectory
        self.size = size
        self.children = children
        self.isAggregate = isAggregate
        self.fileCount = fileCount
    }

    static func == (a: DiskNode, b: DiskNode) -> Bool { a.id == b.id }
    func hash(into h: inout Hasher) { h.combine(id) }

    var path: [DiskNode] {
        var out: [DiskNode] = [self]
        var p = parent
        while let n = p { out.insert(n, at: 0); p = n.parent }
        return out
    }

    func remove(child: DiskNode) {
        children.removeAll { $0 == child }
        var n: DiskNode? = self
        while let cur = n { cur.size -= child.size; n = cur.parent }
    }
}

struct LargeFile: Identifiable, Hashable {
    let url: URL
    let size: Int64
    let modified: Date?
    var id: URL { url }
}

/// Recursive allocated-size scan. Runs off the main thread, reports progress.
final class DiskScanEngine: @unchecked Sendable {
    private let keys: [URLResourceKey] = [.isDirectoryKey, .isSymbolicLinkKey, .totalFileAllocatedSizeKey, .fileAllocatedSizeKey, .volumeIdentifierKey, .contentModificationDateKey, .isPackageKey]
    private let lock = NSLock()
    private var _files = 0
    private var _bytes: Int64 = 0
    private var _cancelled = false
    private var _large: [LargeFile] = []
    private let largeThreshold: Int64
    private let smallThreshold: Int64 = 8 * 1024 * 1024
    private var rootVolume: NSObject?

    var progress: (files: Int, bytes: Int64) { lock.lock(); defer { lock.unlock() }; return (_files, _bytes) }
    var largeFiles: [LargeFile] { lock.lock(); defer { lock.unlock() }; return _large.sorted { $0.size > $1.size } }
    func cancel() { lock.lock(); _cancelled = true; lock.unlock() }
    private var cancelled: Bool { lock.lock(); defer { lock.unlock() }; return _cancelled }

    init(largeThreshold: Int64) { self.largeThreshold = largeThreshold }

    func scan(_ root: URL) -> DiskNode {
        rootVolume = (try? root.resourceValues(forKeys: [.volumeIdentifierKey]))?.volumeIdentifier as? NSObject
        let node = DiskNode(url: root, isDirectory: true)
        let entries = (try? FileManager.default.contentsOfDirectory(at: root, includingPropertiesForKeys: keys, options: [])) ?? []
        // Parallelise across the top-level folders.
        let results = UnsafeMutableBufferPointer<DiskNode?>.allocate(capacity: entries.count)
        results.initialize(repeating: nil)
        defer { results.deallocate() }
        DispatchQueue.concurrentPerform(iterations: entries.count) { i in
            results[i] = self.visit(entries[i])
        }
        attach(results.compactMap { $0 }, to: node)
        return node
    }

    private func visit(_ url: URL) -> DiskNode? {
        if cancelled { return nil }
        guard let v = try? url.resourceValues(forKeys: Set(keys)) else { return nil }
        if v.isSymbolicLink == true { return nil }
        if v.isDirectory == true {
            if let vol = v.volumeIdentifier as? NSObject, let rv = rootVolume, !vol.isEqual(rv) { return nil }
            let node = DiskNode(url: url, isDirectory: true)
            let entries = (try? FileManager.default.contentsOfDirectory(at: url, includingPropertiesForKeys: keys, options: [])) ?? []
            attach(entries.compactMap { visit($0) }, to: node)
            return node
        }
        let size = Int64(v.totalFileAllocatedSize ?? v.fileAllocatedSize ?? 0)
        lock.lock()
        _files += 1
        _bytes += size
        if size >= largeThreshold { _large.append(LargeFile(url: url, size: size, modified: v.contentModificationDate)) }
        lock.unlock()
        return DiskNode(url: url, isDirectory: false, size: size, fileCount: 1)
    }

    private func attach(_ kids: [DiskNode], to node: DiskNode) {
        var keep: [DiskNode] = []
        var smallSize: Int64 = 0
        var smallCount = 0
        var total: Int64 = 0
        var files = 0
        for k in kids {
            total += k.size
            files += k.fileCount
            if k.size < (k.isDirectory ? 1024 * 1024 : smallThreshold) {
                // Tiny files and tiny folders are folded together to keep the tree light.
                if k.size > 0 { smallSize += k.size; smallCount += 1 }
            } else {
                keep.append(k)
            }
        }
        if smallCount > 0 {
            keep.append(DiskNode(url: node.url, name: L("其他 \(smallCount) 个小项目", "\(smallCount) other small items"), isDirectory: false, size: smallSize, isAggregate: true, fileCount: smallCount))
        }
        keep.sort { $0.size > $1.size }
        for k in keep { k.parent = node }
        node.children = keep
        node.size = total
        node.fileCount = files
    }
}

@MainActor
@Observable
final class DiskScanModel {
    var root: DiskNode?
    var current: DiskNode?
    var scanning = false
    var scannedFiles = 0
    var scannedBytes: Int64 = 0
    var largeFiles: [LargeFile] = []
    var scanRoot: URL = Paths.home
    var lastScan: Date?

    @ObservationIgnored private var engine: DiskScanEngine?

    func scan(_ url: URL? = nil) {
        if Demo.active {
            root = Demo.diskTree(); current = root; largeFiles = Demo.largeFiles(); lastScan = Date(); scanning = false
            return
        }
        if let url { scanRoot = url }
        engine?.cancel()
        let engine = DiskScanEngine(largeThreshold: Int64(AppSettings.shared.largeFileMB) * 1024 * 1024)
        self.engine = engine
        scanning = true
        scannedFiles = 0
        scannedBytes = 0
        let target = scanRoot
        Task.detached(priority: .utility) {
            let node = engine.scan(target)
            await MainActor.run {
                guard self.engine === engine else { return }
                self.root = node
                self.current = node
                self.largeFiles = engine.largeFiles
                self.scanning = false
                self.lastScan = Date()
            }
        }
        Task { @MainActor in
            while self.scanning && self.engine === engine {
                let p = engine.progress
                self.scannedFiles = p.files
                self.scannedBytes = p.bytes
                try? await Task.sleep(for: .milliseconds(250))
            }
        }
    }

    func cancel() {
        engine?.cancel()
        scanning = false
    }

    func open(_ n: DiskNode) {
        guard n.isDirectory, !n.children.isEmpty else { return }
        current = n
    }

    func up() { if let p = current?.parent { current = p } }

    /// Remove a node from the tree after it was moved to the Trash.
    func removed(_ n: DiskNode) {
        n.parent?.remove(child: n)
        largeFiles.removeAll { $0.url == n.url || $0.url.path.hasPrefix(n.url.path + "/") }
        // Nudge observers: the tree is a reference type.
        let c = current
        current = nil
        current = c
    }

    func removedFile(_ url: URL) {
        largeFiles.removeAll { $0.url == url }
    }
}
