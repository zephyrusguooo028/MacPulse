import Foundation
import AppKit
import Observation

enum CleanupCategory: String, CaseIterable, Identifiable {
    case devCache, projectArtifacts, appCache, logs, downloads, largeFiles
    var id: String { rawValue }
    var title: String {
        switch self {
        case .devCache: return L("开发工具缓存", "Developer caches")
        case .projectArtifacts: return L("项目依赖与构建产物", "Project dependencies & build output")
        case .appCache: return L("应用缓存", "App caches")
        case .logs: return L("日志", "Logs")
        case .downloads: return L("下载文件夹里的旧文件", "Old files in Downloads")
        case .largeFiles: return L("大文件", "Large files")
        }
    }
    var symbol: String {
        switch self {
        case .devCache: return "hammer"
        case .projectArtifacts: return "shippingbox"
        case .appCache: return "app.badge"
        case .logs: return "doc.text"
        case .downloads: return "arrow.down.circle"
        case .largeFiles: return "doc.zipper"
        }
    }
    var note: String {
        switch self {
        case .devCache: return L("npm / pip / Go 等工具的下载和编译缓存，删掉后下次用到会重新下载。", "Download and build caches from npm, pip, Go and other tools. They are re-downloaded the next time they're needed.")
        case .projectArtifacts: return L("node_modules、dist 等可重新生成的目录。默认只勾选长期没改动的项目。", "Regenerable folders like node_modules and dist. Only projects untouched for a long time are selected by default.")
        case .appCache: return L("各应用的缓存。默认只勾选当前没在运行的应用。", "Caches of individual apps. Only apps that aren't running are selected by default.")
        case .logs: return L("应用和系统写下的日志，通常可以放心删除。", "Logs written by apps and the system. Usually safe to delete.")
        case .downloads: return L("下载文件夹里很久没动过的文件和安装包，请自己确认后勾选。", "Files and installers in Downloads that haven't been touched in a long time. Review before selecting.")
        case .largeFiles: return L("整个用户目录里的大文件，来自磁盘页的扫描结果，请自己确认后勾选。", "Large files across your home folder, from the Disk scan. Review before selecting.")
        }
    }
}

struct CleanupItem: Identifiable, Hashable {
    let url: URL
    let title: String
    let subtitle: String
    let size: Int64
    let category: CleanupCategory
    let recommended: Bool
    var id: URL { url }
}

struct CleanupRecord: Codable, Identifiable {
    let date: Date
    let bytes: Int64
    let count: Int
    let permanent: Bool
    var id: Date { date }
}

enum CleanupCatalog {
    struct DevCache { let path: String; let title: String; let recommended: Bool }

    static var devCaches: [DevCache] { [
        .init(path: "~/.npm/_cacache", title: L("npm 缓存", "npm cache"), recommended: true),
        .init(path: "~/Library/Caches/Yarn", title: L("Yarn 缓存", "Yarn cache"), recommended: true),
        .init(path: "~/Library/Caches/yarn", title: L("Yarn 缓存", "Yarn cache"), recommended: true),
        .init(path: "~/Library/pnpm/store", title: L("pnpm 存储", "pnpm store"), recommended: false),
        .init(path: "~/.bun/install/cache", title: L("Bun 缓存", "Bun cache"), recommended: true),
        .init(path: "~/Library/Caches/pip", title: L("pip 缓存", "pip cache"), recommended: true),
        .init(path: "~/.cache/pip", title: L("pip 缓存", "pip cache"), recommended: true),
        .init(path: "~/Library/Caches/go-build", title: L("Go 编译缓存", "Go build cache"), recommended: true),
        .init(path: "~/Library/Caches/Homebrew", title: L("Homebrew 下载缓存", "Homebrew download cache"), recommended: true),
        .init(path: "~/Library/Caches/node-gyp", title: L("node-gyp 缓存", "node-gyp cache"), recommended: true),
        .init(path: "~/Library/Caches/CocoaPods", title: L("CocoaPods 缓存", "CocoaPods cache"), recommended: true),
        .init(path: "~/Library/Caches/ms-playwright", title: L("Playwright 浏览器", "Playwright browsers"), recommended: false),
        .init(path: "~/Library/Caches/com.apple.dt.Xcode", title: L("Xcode 缓存", "Xcode cache"), recommended: true),
        .init(path: "~/Library/Developer/Xcode/DerivedData", title: "Xcode DerivedData", recommended: true),
        .init(path: "~/Library/Developer/Xcode/iOS DeviceSupport", title: L("iOS 设备支持文件", "iOS device support files"), recommended: false),
        .init(path: "~/Library/Developer/Xcode/Archives", title: L("Xcode 归档", "Xcode archives"), recommended: false),
        .init(path: "~/Library/Developer/CoreSimulator/Caches", title: L("模拟器缓存", "Simulator caches"), recommended: true),
        .init(path: "~/.gradle/caches", title: L("Gradle 缓存", "Gradle cache"), recommended: true),
        .init(path: "~/.cargo/registry/cache", title: L("Cargo 下载缓存", "Cargo download cache"), recommended: true),
        .init(path: "~/.cache/huggingface", title: L("Hugging Face 模型缓存", "Hugging Face model cache"), recommended: false),
    ] }

    static func expand(_ p: String) -> URL { URL(fileURLWithPath: (p as NSString).expandingTildeInPath) }
}

/// Collects candidates off the main thread.
enum CleanupScanner {
    static func scanDevCaches() -> [CleanupItem] {
        var seen = Set<String>()
        return CleanupCatalog.devCaches.compactMap { c in
            let u = CleanupCatalog.expand(c.path)
            // Case-insensitive volumes make Yarn/yarn the same folder.
            guard FileSizer.exists(u), seen.insert(u.path.lowercased()).inserted else { return nil }
            let s = FileSizer.size(of: u)
            guard s > 1024 * 1024 else { return nil }
            return CleanupItem(url: u, title: c.title, subtitle: u.tildePath, size: s, category: .devCache, recommended: c.recommended)
        }
    }

    static func scanAppCaches(running: Set<String>) -> [CleanupItem] {
        let root = Paths.h("Library/Caches")
        let devPaths = Set(CleanupCatalog.devCaches.map { CleanupCatalog.expand($0.path).path.lowercased() })
        let kids = (try? FileManager.default.contentsOfDirectory(at: root, includingPropertiesForKeys: nil)) ?? []
        return kids.compactMap { u in
            if devPaths.contains(u.path.lowercased()) { return nil }
            let s = FileSizer.size(of: u)
            guard s > 10 * 1024 * 1024 else { return nil }
            let id = u.lastPathComponent
            let (name, isRunning) = appName(forCacheFolder: id, running: running)
            let sub = isRunning ? L("\(u.tildePath) · 正在运行", "\(u.tildePath) · Running") : u.tildePath
            return CleanupItem(url: u, title: name, subtitle: sub, size: s, category: .appCache,
                               recommended: !isRunning && !id.hasPrefix("com.apple."))
        }
    }

    private static func appName(forCacheFolder id: String, running: Set<String>) -> (String, Bool) {
        if let appURL = NSWorkspace.shared.urlForApplication(withBundleIdentifier: id) {
            var n = FileManager.default.displayName(atPath: appURL.path)
            if n.hasSuffix(".app") { n = String(n.dropLast(4)) }
            return (n, running.contains(id) || matchesRunning(n, running))
        }
        // Folders named after the app ("Google", "LarkShell") rather than a bundle id:
        // match any distinctive word of the folder name against running apps.
        return (id, matchesRunning(id, running))
    }

    /// Bundle ids, display names and executable names of running apps.
    static func runningNames() -> Set<String> {
        var s = Set<String>()
        for a in NSWorkspace.shared.runningApplications {
            if let b = a.bundleIdentifier { s.insert(b) }
            if let n = a.localizedName { s.insert(n) }
            if let e = a.executableURL?.lastPathComponent { s.insert(e) }
        }
        return s
    }

    static let genericWords: Set<String> = ["shell", "helper", "cache", "caches", "services", "service", "labs", "data", "apple", "app", "com", "inc", "agent", "daemon", "framework"]

    /// "LarkShell" → ["lark"]; "com.google.Chrome" → ["google", "chrome"].
    static func words(_ s: String) -> [String] {
        var out: [String] = []
        var cur = ""
        for ch in s {
            if ch.isLetter || ch.isNumber {
                if ch.isUppercase, let last = cur.last, last.isLowercase { out.append(cur); cur = "" }
                cur.append(ch)
            } else if !cur.isEmpty { out.append(cur); cur = "" }
        }
        if !cur.isEmpty { out.append(cur) }
        return out.map { $0.lowercased() }.filter { $0.count >= 4 && !genericWords.contains($0) }
    }

    static func matchesRunning(_ folder: String, _ running: Set<String>) -> Bool {
        if running.contains(folder) { return true }
        let ws = words(folder)
        guard !ws.isEmpty else { return false }
        let runningWords = Set(running.flatMap { words($0) })
        return ws.contains { runningWords.contains($0) }
    }

    static func scanLogs() -> [CleanupItem] {
        let root = Paths.h("Library/Logs")
        let kids = (try? FileManager.default.contentsOfDirectory(at: root, includingPropertiesForKeys: nil)) ?? []
        return kids.compactMap { u in
            let s = FileSizer.size(of: u)
            guard s > 1024 * 1024 else { return nil }
            return CleanupItem(url: u, title: u.lastPathComponent, subtitle: u.tildePath, size: s, category: .logs, recommended: true)
        }
    }

    static let installerExts: Set<String> = ["dmg", "pkg", "zip", "iso", "xip", "rar", "7z", "tar", "gz", "tgz"]

    static func scanDownloads(olderThanDays days: Int) -> [CleanupItem] {
        let root = Paths.h("Downloads")
        let keys: [URLResourceKey] = [.contentModificationDateKey, .contentAccessDateKey, .addedToDirectoryDateKey]
        let kids = (try? FileManager.default.contentsOfDirectory(at: root, includingPropertiesForKeys: keys, options: [.skipsHiddenFiles])) ?? []
        let cutoff = Date().addingTimeInterval(-Double(days) * 86400)
        let installerCutoff = Date().addingTimeInterval(-14 * 86400)
        return kids.compactMap { u in
            let v = try? u.resourceValues(forKeys: Set(keys))
            let last = [v?.contentModificationDate, v?.contentAccessDate, v?.addedToDirectoryDate].compactMap { $0 }.max() ?? .distantPast
            let isInstaller = installerExts.contains(u.pathExtension.lowercased())
            guard last < cutoff || (isInstaller && last < installerCutoff) else { return nil }
            let s = FileSizer.size(of: u)
            guard s > 5 * 1024 * 1024 else { return nil }
            let kind = isInstaller ? L("安装包/压缩包", "Installer/archive") : L("文件", "File")
            return CleanupItem(url: u, title: u.lastPathComponent, subtitle: L("\(kind) · \(Fmt.daysAgo(last)) 天没动过", "\(kind) · untouched for \(Fmt.daysAgo(last)) days"),
                               size: s, category: .downloads, recommended: false)
        }
    }
}

@MainActor
@Observable
final class CleanupModel {
    var items: [CleanupCategory: [CleanupItem]] = [:]
    var selected: Set<URL> = []
    var scanning = false
    var working = false
    var lastResult: String?
    var trashSize: Int64 = 0
    var trashReadable = true
    var records: [CleanupRecord] = []
    var lastScan: Date?

    @ObservationIgnored private let recordsURL = Paths.support.appendingPathComponent("cleanup-log.json")

    init() {
        if let d = try? Data(contentsOf: recordsURL), let r = try? JSONDecoder().decode([CleanupRecord].self, from: d) {
            records = r
        }
    }

    var allItems: [CleanupItem] { CleanupCategory.allCases.flatMap { items[$0] ?? [] } }
    var selectedItems: [CleanupItem] { allItems.filter { selected.contains($0.url) } }
    var selectedSize: Int64 { selectedItems.reduce(0) { $0 + $1.size } }
    var recommendedSize: Int64 { allItems.filter(\.recommended).reduce(0) { $0 + $1.size } }
    func total(_ c: CleanupCategory) -> Int64 { (items[c] ?? []).reduce(0) { $0 + $1.size } }
    var totalFreed: Int64 { records.reduce(0) { $0 + $1.bytes } }

    /// Scans everything except project artifacts and large files, which come from the other models.
    func scan(projects: ProjectModel, disk: DiskScanModel) {
        guard !scanning else { return }
        scanning = true
        let running = CleanupScanner.runningNames()
        let days = AppSettings.shared.downloadsOldDays
        Task.detached(priority: .utility) {
            async let dev = CleanupScanner.scanDevCaches()
            async let app = CleanupScanner.scanAppCaches(running: running)
            async let logs = CleanupScanner.scanLogs()
            async let dl = CleanupScanner.scanDownloads(olderThanDays: days)
            let trash = Self.measureTrash()
            let (d, a, l, w) = await (dev, app, logs, dl)
            await MainActor.run {
                self.items[.devCache] = d.sorted { $0.size > $1.size }
                self.items[.appCache] = a.sorted { $0.size > $1.size }
                self.items[.logs] = l.sorted { $0.size > $1.size }
                self.items[.downloads] = w.sorted { $0.size > $1.size }
                self.trashSize = trash.size
                self.trashReadable = trash.readable
                self.syncFromOthers(projects: projects, disk: disk)
                self.selected = Set(self.allItems.filter(\.recommended).map(\.url))
                self.scanning = false
                self.lastScan = Date()
            }
        }
        if projects.projects.isEmpty && !projects.scanning { projects.scan() }
    }

    /// Pull project artifacts and large files from the project / disk models.
    func syncFromOthers(projects: ProjectModel, disk: DiskScanModel) {
        let staleDays = AppSettings.shared.staleProjectDays
        var arts: [CleanupItem] = []
        for p in projects.projects {
            let stale = Fmt.daysAgo(p.lastModified) >= staleDays
            for a in p.artifacts where a.size > 1024 * 1024 {
                let note = a.safe ? a.kind.title : L("\(a.kind.title)（未在 .gitignore 中，请确认）", "\(a.kind.title) (not in .gitignore, please check)")
                arts.append(CleanupItem(url: a.url, title: "\(p.displayName) / \(a.url.lastPathComponent)",
                                        subtitle: L("\(note) · 项目 \(Fmt.relative(p.lastModified))修改", "\(note) · project modified \(Fmt.relative(p.lastModified))"),
                                        size: a.size, category: .projectArtifacts, recommended: a.safe && stale))
            }
        }
        items[.projectArtifacts] = arts.sorted { $0.size > $1.size }
        items[.largeFiles] = disk.largeFiles.prefix(100).map {
            CleanupItem(url: $0.url, title: $0.url.lastPathComponent, subtitle: L("\($0.url.deletingLastPathComponent().tildePath) · \(Fmt.relative($0.modified))修改", "\($0.url.deletingLastPathComponent().tildePath) · modified \(Fmt.relative($0.modified))"),
                        size: $0.size, category: .largeFiles, recommended: false)
        }
    }

    nonisolated static func measureTrash() -> (size: Int64, readable: Bool) {
        let t = Paths.h(".Trash")
        guard (try? FileManager.default.contentsOfDirectory(atPath: t.path)) != nil else { return (0, false) }
        return (FileSizer.size(of: t), true)
    }

    struct Outcome { var freed: Int64 = 0; var count = 0; var failures: [(URL, String)] = []; var skipped: [URL] = [] }

    /// Moves the given paths to the Trash (default) or deletes them permanently.
    func perform(_ list: [CleanupItem], permanent: Bool, completion: @escaping (Outcome) -> Void) {
        working = true
        let settings = AppSettings.shared
        let whitelisted = Set(list.filter { settings.isWhitelisted($0.url) }.map(\.url))
        Task.detached(priority: .userInitiated) {
            var acc = Outcome()
            let fm = FileManager.default
            for item in list {
                if whitelisted.contains(item.url) { acc.skipped.append(item.url); continue }
                guard Self.isSafeTarget(item.url) else { acc.skipped.append(item.url); continue }
                do {
                    if permanent { try fm.removeItem(at: item.url) } else { try fm.trashItem(at: item.url, resultingItemURL: nil) }
                    acc.freed += item.size
                    acc.count += 1
                } catch {
                    acc.failures.append((item.url, error.localizedDescription))
                }
            }
            let trash = Self.measureTrash()
            let o = acc
            await MainActor.run {
                let done = Set(list.map(\.url)).subtracting(o.failures.map(\.0)).subtracting(o.skipped)
                for c in CleanupCategory.allCases { self.items[c]?.removeAll { done.contains($0.url) } }
                self.selected.subtract(done)
                self.trashSize = trash.size
                if o.count > 0 { self.log(CleanupRecord(date: Date(), bytes: o.freed, count: o.count, permanent: permanent)) }
                self.working = false
                self.lastResult = permanent ? L("已删除 \(o.count) 项，释放 \(Fmt.bytes(o.freed))", "Deleted \(o.count) items, freed \(Fmt.bytes(o.freed))")
                                            : L("已把 \(o.count) 项（\(Fmt.bytes(o.freed))）移到废纸篓，清空废纸篓后才会真正释放空间", "Moved \(o.count) items (\(Fmt.bytes(o.freed))) to the Trash. Space is freed once you empty the Trash.")
                completion(o)
            }
        }
    }

    func emptyTrash(completion: @escaping (Int64) -> Void) {
        working = true
        Task.detached(priority: .userInitiated) {
            let t = Paths.h(".Trash")
            let before = Self.measureTrash().size
            let kids = (try? FileManager.default.contentsOfDirectory(at: t, includingPropertiesForKeys: nil)) ?? []
            for k in kids { try? FileManager.default.removeItem(at: k) }
            let after = Self.measureTrash().size
            await MainActor.run {
                self.trashSize = after
                let freed = max(before - after, 0)
                if freed > 0 { self.log(CleanupRecord(date: Date(), bytes: freed, count: kids.count, permanent: true)) }
                self.lastResult = L("废纸篓已清空，释放 \(Fmt.bytes(freed))", "Trash emptied, freed \(Fmt.bytes(freed))")
                self.working = false
                completion(freed)
            }
        }
    }

    private func log(_ r: CleanupRecord) {
        records.append(r)
        if let d = try? JSONEncoder().encode(records) { try? d.write(to: recordsURL, options: .atomic) }
    }

    /// Last line of defence: only ever touch things inside the home folder, never the
    /// home folder itself or one of its top-level standard folders.
    nonisolated static func isSafeTarget(_ url: URL) -> Bool {
        let p = url.standardizedFileURL.resolvingSymlinksInPath().path
        let home = Paths.home.resolvingSymlinksInPath().path
        guard p.hasPrefix(home + "/") else { return false }
        let protected = ["Desktop", "Documents", "Downloads", "Library", "Movies", "Music", "Pictures", "Public", "Applications",
                         "Library/Caches", "Library/Logs", "Library/Application Support", "Library/Developer", ".Trash"]
        return !protected.contains { p == home + "/" + $0 }
    }
}
