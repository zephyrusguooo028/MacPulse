import Foundation
import AppKit
import Observation

enum KeepRule: String, CaseIterable, Identifiable {
    case newest, oldest, largest
    var id: String { rawValue }
    var title: String {
        switch self {
        case .newest: return L("保留最新的", "Keep the newest")
        case .oldest: return L("保留最早的", "Keep the oldest")
        case .largest: return L("保留最大的", "Keep the largest")
        }
    }
}

@MainActor
@Observable
final class DuplicatesModel {
    static let shared = DuplicatesModel()

    var folders: [URL] { didSet { UserDefaults.standard.set(folders.map(\.tildePath), forKey: "dupFolders") } }
    var minKB: Int { didSet { UserDefaults.standard.set(minKB, forKey: "dupMinKB") } }
    var keepRule: KeepRule = .newest
    var preferredFolder: URL?

    var exact: [DupGroup] = []
    var similar: [DupGroup] = []
    var selected: Set<URL> = []
    var scanning = false
    var working = false
    var status = ""
    var fraction: Double?
    var lastScan: Date?
    var scannedFiles = 0
    var lastResult: String?
    @ObservationIgnored private var progress: DupProgress?

    static var defaultFolders: [URL] { ["Downloads", "Documents", "Desktop", "Pictures"].map { Paths.h($0) } }

    init() {
        let saved = UserDefaults.standard.stringArray(forKey: "dupFolders")
        folders = (saved?.map { URL(fileURLWithPath: ($0 as NSString).expandingTildeInPath) }) ?? Self.defaultFolders
        let kb = UserDefaults.standard.integer(forKey: "dupMinKB")
        minKB = kb > 0 ? kb : 100
    }

    var reclaimableExact: Int64 { exact.reduce(0) { $0 + $1.reclaimable } }
    var reclaimableSimilar: Int64 { similar.reduce(0) { $0 + $1.reclaimable } }
    var allFiles: [DupFile] {
        var seen = Set<URL>()
        return (exact + similar).flatMap(\.files).filter { seen.insert($0.url).inserted }
    }
    var selectedFiles: [DupFile] { allFiles.filter { selected.contains($0.url) } }
    var selectedSize: Int64 { selectedFiles.reduce(0) { $0 + $1.size } }

    // MARK: Scan

    func scan() {
        progress?.cancel()
        lastResult = nil
        if Demo.active {
            exact = Demo.duplicateGroups(); similar = Demo.similarGroups()
            scannedFiles = 3_412; lastScan = Date(); scanning = false; status = ""
            smartSelect()
            return
        }
        let p = DupProgress()
        progress = p
        scanning = true
        status = L("准备中…", "Preparing…")
        fraction = nil
        let engine = DuplicateEngine(roots: folders.filter { FileSizer.exists($0) }, minSize: Int64(minKB) * 1024, progress: p)
        Task.detached(priority: .utility) {
            let r = engine.run()
            await MainActor.run {
                guard self.progress === p else { return }
                self.scanning = false
                self.status = ""
                self.fraction = nil
                if p.cancelled { return }
                self.exact = r.exact
                self.similar = r.similar
                self.scannedFiles = r.scannedFiles
                self.lastScan = Date()
                self.smartSelect()
            }
        }
        Task { @MainActor in
            while self.progress === p && self.scanning {
                let s = p.snapshot
                self.status = s.total > 0 ? "\(s.phase) \(s.done)/\(s.total)" : (s.done > 0 ? "\(s.phase) \(s.done)" : s.phase)
                self.fraction = s.total > 0 ? Double(s.done) / Double(s.total) : nil
                try? await Task.sleep(for: .milliseconds(250))
            }
        }
    }

    func cancel() {
        progress?.cancel()
        progress = nil
        scanning = false
        status = ""
        fraction = nil
    }

    // MARK: Selection

    /// True when the file may be selected: it is deletable and at least one other
    /// file of each group it belongs to would remain.
    func canSelect(_ f: DupFile) -> Bool {
        guard deletable(f.url) else { return false }
        for g in groups(containing: f.url) where g.files.allSatisfy({ $0.url == f.url || selected.contains($0.url) }) { return false }
        return true
    }

    func deletable(_ url: URL) -> Bool { CleanupModel.isSafeTarget(url) && !AppSettings.shared.isWhitelisted(url) }

    @discardableResult
    func toggle(_ f: DupFile, on: Bool) -> Bool {
        if !on { selected.remove(f.url); return true }
        guard canSelect(f) else { NSSound.beep(); return false }
        selected.insert(f.url)
        return true
    }

    func groups(containing url: URL) -> [DupGroup] { (exact + similar).filter { g in g.files.contains { $0.url == url } } }

    func keeper(of g: DupGroup) -> DupFile {
        var pool = g.files
        if let pref = preferredFolder {
            let inPref = pool.filter { $0.url.path.hasPrefix(pref.standardizedFileURL.path + "/") }
            if !inPref.isEmpty { pool = inPref }
        }
        // Never pick a file we couldn't delete anyway as the one to delete: prefer keeping it.
        let locked = pool.filter { !deletable($0.url) }
        if !locked.isEmpty { pool = locked }
        switch keepRule {
        case .newest: return pool.max { ($0.modified ?? .distantPast) < ($1.modified ?? .distantPast) }!
        case .oldest: return pool.min { ($0.modified ?? .distantPast) < ($1.modified ?? .distantPast) }!
        case .largest: return pool.max { $0.size < $1.size }!
        }
    }

    /// Keeps one file per group by the current rule and selects the rest.
    func smartSelect() {
        var sel = Set<URL>()
        var keep = Set<URL>()
        for g in exact + similar {
            let k = keeper(of: g)
            keep.insert(k.url)
            for f in g.files where f.url != k.url && deletable(f.url) { sel.insert(f.url) }
        }
        // A file kept in one group is never deleted through another.
        selected = sel.subtracting(keep)
    }

    func selectNone() { selected.removeAll() }

    // MARK: Delete

    @MainActor
    func trashSelected() {
        // Re-check each group on disk: a group must keep at least one existing,
        // unselected file, otherwise none of its files are touched.
        var blocked = Set<URL>()
        for g in exact + similar {
            let survivors = g.files.filter { !selected.contains($0.url) && FileManager.default.fileExists(atPath: $0.url.path) }
            if survivors.isEmpty { g.files.forEach { blocked.insert($0.url) } }
        }
        let list = selectedFiles.filter { !blocked.contains($0.url) && deletable($0.url) }
        let skipped = selected.count - list.count
        guard !list.isEmpty else {
            _ = FileActions.confirm(L("没有可以移走的文件", "Nothing to move"), L("每组至少要保留一个文件，白名单和受保护位置里的文件也不会被移走。", "Every group keeps at least one file, and whitelisted or protected files are never moved."), ok: L("好", "OK"), destructive: false)
            return
        }
        let size = list.reduce(Int64(0)) { $0 + $1.size }
        let preview = list.prefix(12).map { "• \($0.url.tildePath)  \(Fmt.bytes($0.size))" }.joined(separator: "\n")
            + (list.count > 12 ? L("\n…等 \(list.count) 个文件", "\n…\(list.count) files in total") : "")
        let skipNote = skipped > 0 ? L("\n\n另有 \(skipped) 个已选文件会被跳过（白名单、受保护位置或该组没有可保留的文件）。", "\n\n\(skipped) other selected files will be skipped (whitelisted, protected, or no file would remain in their group).") : ""
        guard FileActions.confirm(L("把 \(list.count) 个文件（\(Fmt.bytes(size))）移到废纸篓？", "Move \(list.count) files (\(Fmt.bytes(size))) to the Trash?"),
                                  preview + skipNote + L("\n\n每组都会保留至少一个文件。可以在废纸篓里恢复。", "\n\nAt least one file in every group is kept. You can restore them from the Trash."),
                                  ok: L("移到废纸篓", "Move to Trash")) else { return }
        if Demo.active {
            finishTrash(done: Set(list.map(\.url)), failures: [])
            return
        }
        working = true
        let urls = list.map(\.url)
        Task.detached(priority: .userInitiated) {
            var done = Set<URL>()
            var failures: [(URL, String)] = []
            for u in urls {
                do { try FileManager.default.trashItem(at: u, resultingItemURL: nil); done.insert(u) }
                catch { failures.append((u, error.localizedDescription)) }
            }
            let (d, f) = (done, failures)
            await MainActor.run { self.finishTrash(done: d, failures: f) }
        }
    }

    private func finishTrash(done: Set<URL>, failures: [(URL, String)]) {
        working = false
        let freed = allFiles.filter { done.contains($0.url) }.reduce(Int64(0)) { $0 + $1.size }
        selected.subtract(done)
        func prune(_ gs: [DupGroup]) -> [DupGroup] {
            gs.compactMap { g in
                let left = g.files.filter { !done.contains($0.url) }
                return left.count > 1 ? DupGroup(id: g.id, kind: g.kind, files: left) : nil
            }
        }
        exact = prune(exact)
        similar = prune(similar)
        lastResult = L("已把 \(done.count) 个文件（\(Fmt.bytes(freed))）移到废纸篓，清倒废纸篓后才会真正释放空间", "Moved \(done.count) files (\(Fmt.bytes(freed))) to the Trash. Empty it to actually free the space")
        if !failures.isEmpty {
            let lines = failures.prefix(8).map { L("\($0.0.tildePath)：\($0.1)", "\($0.0.tildePath): \($0.1)") }
            _ = FileActions.confirm(L("有 \(failures.count) 个文件没能移走", "\(failures.count) files couldn't be moved"), lines.joined(separator: "\n"), ok: L("好", "OK"), destructive: false)
        }
    }

    // MARK: Folders

    func addFolder() {
        let p = NSOpenPanel()
        p.canChooseDirectories = true
        p.canChooseFiles = false
        p.allowsMultipleSelection = true
        p.directoryURL = Paths.home
        p.prompt = L("添加", "Add")
        guard p.runModal() == .OK else { return }
        for u in p.urls where !folders.contains(u) { folders.append(u) }
    }

    func removeFolder(_ u: URL) { folders.removeAll { $0 == u }; if preferredFolder == u { preferredFolder = nil } }
}
