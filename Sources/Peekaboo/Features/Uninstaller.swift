import Foundation
import AppKit
import CoreServices
import Observation

struct InstalledApp: Identifiable, Hashable, Sendable {
    let url: URL
    let name: String
    let bundleID: String
    let version: String
    var size: Int64?
    var lastUsed: Date?
    /// Symbol used for the made-up icon in demo mode.
    var demoSymbol: String? = nil
    var id: URL { url }
}

struct Leftover: Identifiable, Hashable, Sendable {
    let url: URL
    let kind: String
    let size: Int64
    var id: URL { url }
}

/// Lists third-party apps and finds the files they leave in ~/Library. Read-only.
enum AppCatalog {
    static let selfBundleID = Bundle.main.bundleIdentifier ?? "local.peekaboo.app"

    static var appFolders: [URL] { [URL(fileURLWithPath: "/Applications"), Paths.h("Applications")] }

    /// Apps directly in /Applications and ~/Applications, plus one folder deep
    /// (vendors like Adobe put apps in a subfolder).
    static func list() -> [InstalledApp] {
        var out: [InstalledApp] = []
        var seen = Set<String>()
        let fm = FileManager.default
        for root in appFolders {
            let kids = (try? fm.contentsOfDirectory(at: root, includingPropertiesForKeys: [.isDirectoryKey, .isSymbolicLinkKey], options: [.skipsHiddenFiles])) ?? []
            for u in kids {
                if u.pathExtension == "app" {
                    if let a = info(u), seen.insert(a.url.path).inserted { out.append(a) }
                } else if (try? u.resourceValues(forKeys: [.isDirectoryKey, .isSymbolicLinkKey]))?.isDirectory == true,
                          (try? u.resourceValues(forKeys: [.isSymbolicLinkKey]))?.isSymbolicLink != true {
                    let inner = (try? fm.contentsOfDirectory(at: u, includingPropertiesForKeys: nil, options: [.skipsHiddenFiles])) ?? []
                    for v in inner where v.pathExtension == "app" {
                        if let a = info(v), seen.insert(a.url.path).inserted { out.append(a) }
                    }
                }
            }
        }
        return out.sorted { $0.name.localizedStandardCompare($1.name) == .orderedAscending }
    }

    /// Reads one app bundle; nil for Apple's apps, Peekaboo itself and broken bundles.
    static func info(_ url: URL) -> InstalledApp? {
        guard let b = Bundle(url: url), let id = b.bundleIdentifier, !isProtectedBundleID(id) else { return nil }
        if url.standardizedFileURL.path == Bundle.main.bundleURL.standardizedFileURL.path { return nil }
        var name = FileManager.default.displayName(atPath: url.path)
        if name.hasSuffix(".app") { name = String(name.dropLast(4)) }
        let version = (b.infoDictionary?["CFBundleShortVersionString"] as? String) ?? (b.infoDictionary?["CFBundleVersion"] as? String) ?? ""
        return InstalledApp(url: url.standardizedFileURL, name: name, bundleID: id, version: version, size: nil, lastUsed: lastUsed(url))
    }

    static func isProtectedBundleID(_ id: String) -> Bool {
        id.lowercased().hasPrefix("com.apple.") || id == selfBundleID || id == "local.peekaboo.app"
    }

    static func lastUsed(_ url: URL) -> Date? {
        guard let item = MDItemCreateWithURL(kCFAllocatorDefault, url as CFURL) else { return nil }
        return MDItemCopyAttribute(item, kMDItemLastUsedDate) as? Date
    }

    /// The app bundle may be moved to the Trash only when it sits in an Applications
    /// folder (or in the user's home, for a dragged-in app) and isn't Apple's.
    static func isRemovableBundle(_ app: InstalledApp) -> Bool {
        let p = app.url.standardizedFileURL.resolvingSymlinksInPath().path
        guard app.url.pathExtension == "app", !isProtectedBundleID(app.bundleID) else { return false }
        if p.hasPrefix("/System/") || p.hasPrefix("/Library/") || p.hasPrefix("/usr/") { return false }
        let inAppFolder = appFolders.contains { p.hasPrefix($0.resolvingSymlinksInPath().path + "/") }
        return inAppFolder || CleanupModel.isSafeTarget(app.url)
    }

    // MARK: Leftovers

    static let genericNames: Set<String> = ["app", "apps", "data", "cache", "caches", "logs", "electron", "helper", "update", "updater",
                                            "support", "settings", "preferences", "default", "shared", "temp", "tmp", "google", "microsoft", "adobe"]

    static let genericBundleIDs: Set<String> = ["com.github.electron", "com.electron.app", "org.chromium.chromium", "com.example.app"]

    /// Finds per-app data under ~/Library that matches the bundle id or the exact app name.
    static func leftovers(for app: InstalledApp, cancel: () -> Bool = { false }) -> [Leftover] {
        let fm = FileManager.default
        let lib = Paths.h("Library")
        let id = app.bundleID
        let idL = id.lowercased()
        // Some Electron apps keep the template's bundle id; matching it would hit other apps' data.
        let genericID = genericBundleIDs.contains(idL)
        var names = Set<String>()
        for n in [app.name, Bundle(url: app.url)?.infoDictionary?["CFBundleName"] as? String].compactMap({ $0 }) {
            let l = n.lowercased()
            if l.count >= 3, !genericNames.contains(l) { names.insert(l) }
        }
        var found: [(URL, String)] = []
        func kids(_ rel: String) -> [URL] {
            (try? fm.contentsOfDirectory(at: lib.appendingPathComponent(rel), includingPropertiesForKeys: nil, options: [])) ?? []
        }
        func byIDOrName(_ rel: String, _ kind: String) {
            for u in kids(rel) {
                // Exact names only: "com.google.Chrome.canary" belongs to another app.
                let n = u.lastPathComponent.lowercased()
                if (n == idL && !genericID) || names.contains(n) { found.append((u, kind)) }
            }
        }
        byIDOrName("Application Support", L("应用数据", "App data"))
        byIDOrName("Caches", L("缓存", "Cache"))
        byIDOrName("Logs", L("日志", "Logs"))
        for u in kids("Preferences") {
            let n = u.lastPathComponent.lowercased()
            if n == idL + ".plist" && !genericID { found.append((u, L("偏好设置", "Preferences"))) }
        }
        for u in kids("Preferences/ByHost") {
            let n = u.lastPathComponent.lowercased()
            // "<bundle id>.<host UUID>.plist"; a longer id is another app's.
            let host = n.hasPrefix(idL + ".") && n.hasSuffix(".plist") ? String(n.dropFirst(idL.count + 1).dropLast(6)) : ""
            if !genericID, host.count >= 12, host.allSatisfy({ $0.isHexDigit || $0 == "-" }) { found.append((u, L("偏好设置", "Preferences"))) }
        }
        for u in kids("Containers") where u.lastPathComponent.lowercased() == idL { found.append((u, L("沙盒容器", "Sandbox container"))) }
        // Group containers are named "<TEAMID>.<group>"; only take ones whose group is
        // exactly this bundle id. Vendor-wide groups may be shared by other apps.
        for u in kids("Group Containers") {
            let parts = u.lastPathComponent.split(separator: ".", maxSplits: 1).map(String.init)
            guard parts.count == 2, parts[0].count == 10, parts[0].allSatisfy({ $0.isUppercase || $0.isNumber }) else { continue }
            let g = parts[1].lowercased()
            if !genericID && g == idL { found.append((u, L("共享容器", "Group container"))) }
        }
        for u in kids("Saved Application State") where u.lastPathComponent.lowercased() == idL + ".savedstate" && !genericID { found.append((u, L("窗口状态", "Saved window state"))) }
        for u in kids("HTTPStorages") {
            let n = u.lastPathComponent.lowercased()
            if (n == idL || n == idL + ".binarycookies") && !genericID { found.append((u, L("网络存储", "HTTP storage"))) }
        }
        for u in kids("WebKit") where u.lastPathComponent.lowercased() == idL && !genericID { found.append((u, L("网页数据", "WebKit data"))) }
        for u in kids("LaunchAgents") where u.pathExtension == "plist" && !genericID {
            if let d = try? Data(contentsOf: u), let s = String(data: d, encoding: .utf8) ?? String(data: d, encoding: .isoLatin1),
               s.range(of: id, options: .caseInsensitive) != nil {
                found.append((u, L("登录启动项", "Launch agent")))
            }
        }
        var seen = Set<String>()
        return found.compactMap { u, kind in
            if cancel() { return nil }
            let u = u.standardizedFileURL
            // Symlinks are left alone; Apple's own data never matches but is excluded anyway.
            if (try? u.resourceValues(forKeys: [.isSymbolicLinkKey]))?.isSymbolicLink == true { return nil }
            if u.lastPathComponent.lowercased().hasPrefix("com.apple.") { return nil }
            guard CleanupModel.isSafeTarget(u), seen.insert(u.path).inserted else { return nil }
            return Leftover(url: u, kind: kind, size: FileSizer.size(of: u))
        }
        .sorted { $0.size > $1.size }
    }
}

@MainActor
@Observable
final class UninstallModel {
    static let shared = UninstallModel()

    var apps: [InstalledApp] = []
    var loading = false
    var sizing = false
    var selection: URL?
    var leftovers: [Leftover] = []
    var picked: Set<URL> = []
    var findingLeftovers = false
    var working = false
    var lastResult: String?
    @ObservationIgnored private var leftoverToken = 0

    var selectedApp: InstalledApp? { apps.first { $0.url == selection } }

    func load() {
        if Demo.active { apps = Demo.uninstallApps(); return }
        guard !loading else { return }
        loading = true
        Task.detached(priority: .userInitiated) {
            let list = AppCatalog.list()
            await MainActor.run {
                self.apps = list
                self.loading = false
                self.measureSizes()
            }
        }
    }

    private func measureSizes() {
        sizing = true
        let urls = apps.map(\.url)
        Task.detached(priority: .utility) {
            let results = UnsafeMutableBufferPointer<Int64>.allocate(capacity: urls.count)
            results.initialize(repeating: 0)
            DispatchQueue.concurrentPerform(iterations: urls.count) { i in results[i] = FileSizer.size(of: urls[i]) }
            let sizes = Dictionary(uniqueKeysWithValues: zip(urls, Array(results)))
            results.deallocate()
            await MainActor.run {
                for i in self.apps.indices { if let s = sizes[self.apps[i].url] { self.apps[i].size = s } }
                self.sizing = false
            }
        }
    }

    func select(_ app: InstalledApp) {
        selection = app.url
        leftovers = []
        picked = []
        lastResult = nil
        leftoverToken += 1
        let token = leftoverToken
        if Demo.active {
            leftovers = Demo.leftovers(for: app)
            picked = Set(leftovers.map(\.url))
            return
        }
        findingLeftovers = true
        Task.detached(priority: .userInitiated) {
            let found = AppCatalog.leftovers(for: app)
            await MainActor.run {
                guard self.leftoverToken == token else { return }
                self.leftovers = found
                self.picked = Set(found.filter { !AppSettings.shared.isWhitelisted($0.url) }.map(\.url))
                self.findingLeftovers = false
            }
        }
    }

    /// Handles a .app dropped on the page: selects it, adding it to the list if needed.
    func dropped(_ url: URL) -> Bool {
        guard url.pathExtension == "app" else { return false }
        guard let app = AppCatalog.info(url) else {
            _ = FileActions.confirm(L("不能卸载这个应用", "This app can't be uninstalled"), L("Peekaboo 不卸载 Apple 自带的应用和它自己。", "Peekaboo doesn't uninstall Apple's apps or itself."), ok: L("好", "OK"), destructive: false)
            return false
        }
        if !apps.contains(where: { $0.url == app.url }) {
            var a = app
            a.size = FileSizer.size(of: url)
            apps.append(a)
        }
        select(app)
        return true
    }

    // MARK: Uninstall

    func uninstall() {
        guard let app = selectedApp else { return }
        let chosen = leftovers.filter { picked.contains($0.url) && !AppSettings.shared.isWhitelisted($0.url) && CleanupModel.isSafeTarget($0.url) }
        let total = (app.size ?? 0) + chosen.reduce(0) { $0 + $1.size }
        let lines = ["• \(app.url.path)  \(app.size.map { Fmt.bytes($0) } ?? "…")"] + chosen.prefix(12).map { "• \($0.url.tildePath)  \(Fmt.bytes($0.size))" }
            + (chosen.count > 12 ? [L("…等 \(chosen.count) 项残留", "…\(chosen.count) leftovers in total")] : [])
        let running = !NSRunningApplication.runningApplications(withBundleIdentifier: app.bundleID).isEmpty
        let runNote = running ? L("\n\n\(app.name) 正在运行，会先让它退出（像 ⌘Q 一样，有未保存内容时它会先询问你）。", "\n\n\(app.name) is running and will be asked to quit first (like ⌘Q; it will ask about unsaved work).") : ""
        guard FileActions.confirm(L("卸载 \(app.name)？", "Uninstall \(app.name)?"),
                                  L("会把下面这些移到废纸篓，共 \(Fmt.bytes(total))：\n", "These will be moved to the Trash, \(Fmt.bytes(total)) in total:\n") + lines.joined(separator: "\n") + runNote
                                  + L("\n\n可以在废纸篓里恢复。", "\n\nYou can restore them from the Trash."),
                                  ok: L("卸载", "Uninstall")) else { return }
        if Demo.active {
            apps.removeAll { $0.url == app.url }
            selection = nil; leftovers = []; picked = []
            lastResult = L("已卸载 \(app.name)，\(Fmt.bytes(total)) 已移到废纸篓", "Uninstalled \(app.name); \(Fmt.bytes(total)) moved to the Trash")
            return
        }
        working = true
        Task { @MainActor in
            if running {
                let quit = await Self.quit(app)
                if !quit {
                    let force = FileActions.confirm(L("\(app.name) 还没有退出", "\(app.name) hasn't quit"),
                                                    L("它可能在等你保存文件。可以切换过去处理后再试，或者强制退出（未保存的内容会丢失）。", "It may be waiting for you to save something. Switch to it and try again, or force quit (unsaved work will be lost)."),
                                                    ok: L("强制退出", "Force Quit"))
                    if force {
                        NSRunningApplication.runningApplications(withBundleIdentifier: app.bundleID).forEach { $0.forceTerminate() }
                        try? await Task.sleep(for: .seconds(1))
                    } else { working = false; return }
                }
            }
            let canTrashApp = AppCatalog.isRemovableBundle(app)
            let urls = chosen.map(\.url)
            let outcome = await Task.detached(priority: .userInitiated) { () -> (appError: Error?, done: Set<URL>, failures: [(URL, String)]) in
                var appError: Error?
                if canTrashApp {
                    do { try FileManager.default.trashItem(at: app.url, resultingItemURL: nil) } catch { appError = error }
                } else {
                    appError = NSError(domain: "Peekaboo", code: 1, userInfo: [NSLocalizedDescriptionKey: L("Peekaboo 只会移走「应用程序」文件夹或个人文件夹里的应用。", "Peekaboo only moves apps that are in an Applications folder or your home folder.")])
                }
                var done = Set<URL>()
                var failures: [(URL, String)] = []
                for u in urls {
                    do { try FileManager.default.trashItem(at: u, resultingItemURL: nil); done.insert(u) }
                    catch { failures.append((u, error.localizedDescription)) }
                }
                return (appError, done, failures)
            }.value
            finish(app: app, chosen: chosen, outcome.appError, outcome.done, outcome.failures)
        }
    }

    private func finish(app: InstalledApp, chosen: [Leftover], _ appError: Error?, _ done: Set<URL>, _ failures: [(URL, String)]) {
        working = false
        let freed = chosen.filter { done.contains($0.url) }.reduce(0) { $0 + $1.size }
        leftovers.removeAll { done.contains($0.url) }
        picked.subtract(done)
        if appError == nil {
            apps.removeAll { $0.url == app.url }
            selection = nil
            lastResult = L("已卸载 \(app.name)：应用和 \(done.count) 项残留（\(Fmt.bytes((app.size ?? 0) + freed))）已移到废纸篓", "Uninstalled \(app.name): the app and \(done.count) leftovers (\(Fmt.bytes((app.size ?? 0) + freed))) moved to the Trash")
        } else {
            lastResult = L("已清理 \(done.count) 项残留（\(Fmt.bytes(freed))），应用本身没能移走", "Removed \(done.count) leftovers (\(Fmt.bytes(freed))); the app itself couldn't be moved")
            let ns = appError! as NSError
            let permission = ns.code == NSFileWriteNoPermissionError || ns.code == CocoaError.fileWriteNoPermission.rawValue
                || (ns.domain == NSPOSIXErrorDomain && (ns.code == Int(EACCES) || ns.code == Int(EPERM)))
                || (ns.userInfo[NSUnderlyingErrorKey] as? NSError).map { $0.domain == NSPOSIXErrorDomain && ($0.code == Int(EACCES) || $0.code == Int(EPERM)) } == true
            let how = L("这个应用需要管理员权限才能删除（通常是 App Store 应用或由安装器装的应用）。可以这样删除：\n\n• App Store 应用：打开「启动台」，按住应用图标直到它抖动，点左上角的 ⓧ。\n• 其他应用：在 Finder 里把它拖到废纸篓，按提示输入密码。\n\n它的残留文件已经按你的选择处理了。",
                        "This app needs administrator rights to remove (usually an App Store app or one installed by an installer). To remove it:\n\n• App Store apps: open Launchpad, hold the app's icon until it jiggles, then click ⓧ.\n• Other apps: drag it to the Trash in Finder and enter your password when asked.\n\nIts leftover files were handled as you chose.")
            if FileActions.confirm(L("\(app.name) 没能移到废纸篓", "Couldn't move \(app.name) to the Trash"), permission ? how : ns.localizedDescription + "\n\n" + how,
                                   ok: L("在 Finder 中显示", "Show in Finder"), destructive: false) {
                FileActions.reveal(app.url)
            }
        }
        if !failures.isEmpty {
            let lines = failures.prefix(8).map { L("\($0.0.tildePath)：\($0.1)", "\($0.0.tildePath): \($0.1)") }
            _ = FileActions.confirm(L("有 \(failures.count) 项残留没能移走", "\(failures.count) leftovers couldn't be moved"), lines.joined(separator: "\n"), ok: L("好", "OK"), destructive: false)
        }
    }

    /// Asks the app to quit and waits up to 8 seconds.
    static func quit(_ app: InstalledApp) async -> Bool {
        let running = NSRunningApplication.runningApplications(withBundleIdentifier: app.bundleID)
        running.forEach { $0.terminate() }
        for _ in 0..<32 {
            if running.allSatisfy(\.isTerminated) { return true }
            try? await Task.sleep(for: .milliseconds(250))
        }
        return running.allSatisfy(\.isTerminated)
    }
}
