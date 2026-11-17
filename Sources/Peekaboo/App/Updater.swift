import AppKit
import CryptoKit
import Observation

/// Checks GitHub Releases for a newer version and installs it in place.
/// This is the only network access in Peekaboo; it can be turned off in Settings.
@MainActor
@Observable
final class Updater {
    static let shared = Updater()
    static let repo = "zephyrusguooo028/Peekaboo"

    enum State: Equatable {
        case idle, checking, upToDate, available(version: String, notes: String), downloading(Double), ready(version: String), installing, failed(String)
    }

    var state: State = .idle
    var lastCheck: Date? { didSet { UserDefaults.standard.set(lastCheck, forKey: "update.lastCheck") } }
    var autoCheck: Bool { didSet { UserDefaults.standard.set(autoCheck, forKey: "update.auto"); schedule() } }
    /// Download in the background and switch over when the user is away or quits.
    var autoInstall: Bool {
        didSet {
            UserDefaults.standard.set(autoInstall, forKey: "update.autoInstall")
            // Turned off while an update waits: go back to asking.
            if !autoInstall, case .ready(let v) = state { idleTimer?.invalidate(); state = .available(version: v, notes: "") }
        }
    }

    @ObservationIgnored private var asset: (url: URL, sha256: String?)?
    @ObservationIgnored private var timer: Timer?
    @ObservationIgnored private var idleTimer: Timer?
    @ObservationIgnored private var staged: URL?
    @ObservationIgnored private var installStarted = false
    /// Version of the release in `asset`.
    @ObservationIgnored private var pendingVersion: String?

    var current: String { Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "0" }
    var hasUpdate: Bool {
        switch state { case .available, .ready: return true; default: return false }
    }

    private init() {
        autoCheck = UserDefaults.standard.object(forKey: "update.auto") as? Bool ?? true
        lastCheck = UserDefaults.standard.object(forKey: "update.lastCheck") as? Date
        autoInstall = UserDefaults.standard.bool(forKey: "update.autoInstall")
    }

    func start() {
        guard !Demo.active else { return }
        // Just came back from an update: say so once.
        let d = UserDefaults.standard
        if let v = d.string(forKey: "update.installing") {
            d.removeObject(forKey: "update.installing")
            if !Self.newer(v, than: current) {
                DispatchQueue.main.asyncAfter(deadline: .now() + 3) {
                    AlertManager.shared.notify(id: "updated", title: L("已更新到 Peekaboo \(v)", "Updated to Peekaboo \(v)"),
                                               body: L("新功能见 设置 › 更新。", "See Settings › Updates for what's new."), tab: .settings)
                }
            }
        }
        schedule()
        if autoCheck, lastCheck.map({ Date().timeIntervalSince($0) > 86_400 }) ?? true {
            DispatchQueue.main.asyncAfter(deadline: .now() + 10) { Task { await self.check(silent: true) } }
        }
    }

    private func schedule() {
        timer?.invalidate()
        guard autoCheck else { return }
        timer = Timer.scheduledTimer(withTimeInterval: 86_400, repeats: true) { _ in
            Task { @MainActor in await Updater.shared.check(silent: true) }
        }
    }

    static func newer(_ a: String, than b: String) -> Bool {
        func parts(_ s: String) -> [Int] { s.trimmingCharacters(in: CharacterSet(charactersIn: "vV")).split(separator: ".").map { Int($0) ?? 0 } }
        let x = parts(a), y = parts(b)
        for i in 0..<max(x.count, y.count) {
            let p = i < x.count ? x[i] : 0, q = i < y.count ? y[i] : 0
            if p != q { return p > q }
        }
        return false
    }

    func check(silent: Bool = false) async {
        switch state { case .downloading, .installing, .ready: return; default: break }
        state = .checking
        var req = URLRequest(url: URL(string: "https://api.github.com/repos/\(Self.repo)/releases/latest")!)
        req.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        req.setValue("Peekaboo-Updater", forHTTPHeaderField: "User-Agent")
        req.timeoutInterval = 20
        do {
            let (data, resp) = try await URLSession.shared.data(for: req)
            guard (resp as? HTTPURLResponse)?.statusCode == 200,
                  let json = try JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let tag = json["tag_name"] as? String else { throw URLError(.badServerResponse) }
            lastCheck = Date()
            let assets = json["assets"] as? [[String: Any]] ?? []
            guard Self.newer(tag, than: current),
                  let dmg = assets.first(where: { ($0["name"] as? String)?.hasSuffix(".dmg") == true }),
                  let urlString = dmg["browser_download_url"] as? String, let url = URL(string: urlString) else {
                state = .upToDate
                return
            }
            let digest = (dmg["digest"] as? String).flatMap { $0.hasPrefix("sha256:") ? String($0.dropFirst(7)) : nil }
            if asset?.url != url { staged = nil }
            asset = (url, digest)
            let notes = (json["body"] as? String ?? "").components(separatedBy: "\n").prefix(12).joined(separator: "\n")
            let version = tag.trimmingCharacters(in: CharacterSet(charactersIn: "vV"))
            pendingVersion = version
            state = .available(version: version, notes: notes)
            if silent { await announce(version) }
        } catch {
            state = silent ? .idle : .failed(L("检查失败：\(error.localizedDescription)", "Check failed: \(error.localizedDescription)"))
        }
    }

    /// An automatic check found `version`: tell the user once, or stage it when auto-install is on.
    private func announce(_ version: String) async {
        let d = UserDefaults.standard
        let first = d.string(forKey: "update.notified") != version
        if autoInstall {
            do {
                _ = try await download()
                state = .ready(version: version)
                startIdleWatch()
                if first {
                    AlertManager.shared.notify(id: "update", title: L("Peekaboo \(version) 已下载", "Peekaboo \(version) downloaded"),
                                               body: L("离开电脑一会儿或退出 Peekaboo 时会自动安装。", "It installs by itself when you're away for a while or quit Peekaboo."), tab: .settings)
                }
                d.set(version, forKey: "update.notified")
                return
            } catch {
                state = .available(version: version, notes: "")
            }
        }
        guard first else { return }
        d.set(version, forKey: "update.notified")
        AlertManager.shared.notify(id: "update", title: L("Peekaboo \(version) 可以更新", "Peekaboo \(version) is available"),
                                   body: L("点「安装」直接更新，或点这里查看更新内容。", "Click Install to update now, or click here to see what's new."),
                                   tab: .settings, install: true)
    }

    /// Downloads and verifies the dmg; reuses an earlier download of the same file.
    private func download() async throws -> URL {
        if let staged, FileManager.default.fileExists(atPath: staged.path) { return staged }
        guard let asset else { throw URLError(.badURL) }
        state = .downloading(0)
        let (tmp, _) = try await URLSession.shared.download(from: asset.url, delegate: ProgressDelegate { p in
            Task { @MainActor in if case .downloading = Updater.shared.state { Updater.shared.state = .downloading(p) } }
        })
        let dir = Paths.support.appendingPathComponent("Updates")
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let dmg = dir.appendingPathComponent("Peekaboo-update.dmg")
        try? FileManager.default.removeItem(at: dmg)
        try FileManager.default.moveItem(at: tmp, to: dmg)
        if let want = asset.sha256 {
            let got = SHA256.hash(data: try Data(contentsOf: dmg)).map { String(format: "%02x", $0) }.joined()
            guard got == want.lowercased() else {
                try? FileManager.default.removeItem(at: dmg)
                throw NSError(domain: "Peekaboo", code: 1, userInfo: [NSLocalizedDescriptionKey: L("校验失败，下载的文件不完整", "Checksum mismatch, download corrupted")])
            }
        }
        staged = dmg
        return dmg
    }

    /// Downloads (unless already staged), then swaps the app bundle and relaunches.
    func install() {
        guard asset != nil || staged != nil else { return }
        Task {
            do {
                let dmg = try await download()
                state = .installing
                try runInstaller(dmg: dmg, relaunch: true)
            } catch {
                state = .failed(L("更新失败：\(error.localizedDescription)", "Update failed: \(error.localizedDescription)"))
            }
        }
    }

    /// While an update waits: install once the user has been away for 15 minutes.
    private func startIdleWatch() {
        idleTimer?.invalidate()
        idleTimer = Timer.scheduledTimer(withTimeInterval: 60, repeats: true) { _ in
            MainActor.assumeIsolated { Updater.shared.installIfIdle() }
        }
    }

    /// Hidden setting `update.idleMinutes`, mainly for testing.
    static var idleMinutes: Double { UserDefaults.standard.object(forKey: "update.idleMinutes") as? Double ?? 15 }

    static var idleSeconds: Double {
        CGEventSource.secondsSinceLastEventType(.combinedSessionState, eventType: CGEventType(rawValue: ~0) ?? .null)
    }

    private func installIfIdle() {
        guard case .ready = state, let staged, Self.idleSeconds >= Self.idleMinutes * 60,
              !ScreenshotController.shared.busy else { return }
        state = .installing
        do { try runInstaller(dmg: staged, relaunch: true) } catch { state = .failed(error.localizedDescription) }
    }

    /// Called when Peekaboo quits: put the downloaded version in place without reopening.
    func installOnQuit() {
        guard case .ready = state, let staged, !installStarted else { return }
        try? runInstaller(dmg: staged, relaunch: false)
    }

    private func runInstaller(dmg: URL, relaunch: Bool) throws {
        installStarted = true
        idleTimer?.invalidate()
        if let v = pendingVersion { UserDefaults.standard.set(v, forKey: "update.installing") }
        let app = Bundle.main.bundleURL.path
        let pid = ProcessInfo.processInfo.processIdentifier
        // Runs after Peekaboo quits: mount, replace the bundle, clear quarantine, reopen.
        let script = """
        while kill -0 \(pid) 2>/dev/null; do sleep 0.3; done
        MNT=$(mktemp -d /tmp/peekaboo-mnt.XXXXXX)
        hdiutil attach -nobrowse -quiet -mountpoint "$MNT" "\(dmg.path)" || exit 1
        if [ -d "$MNT/Peekaboo.app" ]; then
          rm -rf "\(app).old"; mv "\(app)" "\(app).old" && ditto "$MNT/Peekaboo.app" "\(app)" && rm -rf "\(app).old" || mv "\(app).old" "\(app)"
          xattr -dr com.apple.quarantine "\(app)" 2>/dev/null
        fi
        hdiutil detach -quiet "$MNT"; rm -f "\(dmg.path)"
        \(relaunch ? "open \"\(app)\"" : "")
        """
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/bin/sh")
        p.arguments = ["-c", script]
        try p.run()
        if relaunch { DispatchQueue.main.asyncAfter(deadline: .now() + 0.3) { NSApp.terminate(nil) } }
    }
}

private final class ProgressDelegate: NSObject, URLSessionDownloadDelegate, @unchecked Sendable {
    let onProgress: (Double) -> Void
    init(_ f: @escaping (Double) -> Void) { onProgress = f }
    func urlSession(_ s: URLSession, downloadTask: URLSessionDownloadTask, didWriteData _: Int64, totalBytesWritten w: Int64, totalBytesExpectedToWrite t: Int64) {
        if t > 0 { onProgress(Double(w) / Double(t)) }
    }
    func urlSession(_ s: URLSession, downloadTask: URLSessionDownloadTask, didFinishDownloadingTo location: URL) {}
}
