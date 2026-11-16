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
        case idle, checking, upToDate, available(version: String, notes: String), downloading(Double), installing, failed(String)
    }

    var state: State = .idle
    var lastCheck: Date? { didSet { UserDefaults.standard.set(lastCheck, forKey: "update.lastCheck") } }
    var autoCheck: Bool { didSet { UserDefaults.standard.set(autoCheck, forKey: "update.auto"); schedule() } }

    @ObservationIgnored private var asset: (url: URL, sha256: String?)?
    @ObservationIgnored private var timer: Timer?

    var current: String { Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "0" }
    var hasUpdate: Bool { if case .available = state { return true } else { return false } }

    private init() {
        autoCheck = UserDefaults.standard.object(forKey: "update.auto") as? Bool ?? true
        lastCheck = UserDefaults.standard.object(forKey: "update.lastCheck") as? Date
    }

    func start() {
        guard !Demo.active else { return }
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
        if case .downloading = state { return }
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
            asset = (url, digest)
            let notes = (json["body"] as? String ?? "").components(separatedBy: "\n").prefix(12).joined(separator: "\n")
            state = .available(version: tag.trimmingCharacters(in: CharacterSet(charactersIn: "vV")), notes: notes)
        } catch {
            state = silent ? .idle : .failed(L("检查失败：\(error.localizedDescription)", "Check failed: \(error.localizedDescription)"))
        }
    }

    /// Downloads the dmg, verifies it, then swaps the app bundle and relaunches.
    func install() {
        guard let asset else { return }
        state = .downloading(0)
        Task {
            do {
                let (tmp, _) = try await URLSession.shared.download(from: asset.url, delegate: ProgressDelegate { p in
                    Task { @MainActor in Updater.shared.state = .downloading(p) }
                })
                let dmg = FileManager.default.temporaryDirectory.appendingPathComponent("Peekaboo-update.dmg")
                try? FileManager.default.removeItem(at: dmg)
                try FileManager.default.moveItem(at: tmp, to: dmg)
                if let want = asset.sha256 {
                    let got = SHA256.hash(data: try Data(contentsOf: dmg)).map { String(format: "%02x", $0) }.joined()
                    guard got == want.lowercased() else { throw NSError(domain: "Peekaboo", code: 1, userInfo: [NSLocalizedDescriptionKey: L("校验失败，下载的文件不完整", "Checksum mismatch, download corrupted")]) }
                }
                state = .installing
                try runInstaller(dmg: dmg)
            } catch {
                state = .failed(L("更新失败：\(error.localizedDescription)", "Update failed: \(error.localizedDescription)"))
            }
        }
    }

    private func runInstaller(dmg: URL) throws {
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
        open "\(app)"
        """
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/bin/sh")
        p.arguments = ["-c", script]
        try p.run()
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.3) { NSApp.terminate(nil) }
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
