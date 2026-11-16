import AppKit

/// An app or process worth quitting to free CPU or memory.
struct ResourceHog: Identifiable, Hashable {
    enum Kind { case cpu, idleMemory }
    let group: AppGroup
    let kind: Kind
    let avgCPU: Double
    let idleMinutes: Int?
    let recommended: Bool
    var id: String { group.id }

    var reason: String {
        switch kind {
        case .cpu:
            return L("持续占用 CPU，近几分钟平均 \(Fmt.pct(avgCPU))", "Busy for a while, averaging \(Fmt.pct(avgCPU)) CPU")
        case .idleMemory:
            let m = idleMinutes ?? 0
            let t = m >= 60 ? L("\(m / 60) 小时", "\(m / 60) h") : L("\(m) 分钟", "\(m) min")
            return L("占用 \(Fmt.bytes(group.memory))，\(t)没用过", "Using \(Fmt.bytes(group.memory)), unused for \(t)")
        }
    }
}

enum ResourceHogs {
    /// Never offered: the system, the desktop, and Peekaboo itself.
    static let protectedNames: Set<String> = [
        "WindowServer", "Finder", "Dock", "SystemUIServer", "loginwindow", "ControlCenter", "Spotlight", "NotificationCenter",
        "kernel_task", "launchd", "coreaudiod", "mds", "mds_stores", "mdworker", "mdworker_shared", "backupd", "Peekaboo",
        "WallpaperAgent", "TextInputMenuAgent", "universalaccessd", "sharingd", "bluetoothd", "airportd", "cfprefsd",
    ]

    @MainActor
    static func isProtected(_ g: AppGroup) -> Bool {
        if protectedNames.contains(g.name) { return true }
        if let app = g.appPath {
            return app.hasPrefix("/System/") || app.contains("/Peekaboo.app")
        }
        // Loose processes: only offer ones that run from user / Homebrew locations (scripts, dev servers).
        guard let path = SystemMonitor.shared.processes.first(where: { $0.pid == g.pids.first })?.path else { return true }
        return !(path.hasPrefix("/Users/") || path.hasPrefix("/opt/homebrew/") || path.hasPrefix("/usr/local/") || path.hasPrefix("/Applications/"))
    }

    @MainActor
    static func find() -> [ResourceHog] {
        let m = SystemMonitor.shared
        let front = NSWorkspace.shared.frontmostApplication?.bundleURL?.path
        let running = NSWorkspace.shared.runningApplications
        var out: [ResourceHog] = []
        for g in m.groups where !isProtected(g) {
            let avg = m.cpuAverage[g.id] ?? g.cpu
            // Sustained CPU: the smoothed value and the current one are both high.
            if avg > 0.5 && g.cpu > 0.25 && m.processSamples >= 3 {
                out.append(ResourceHog(group: g, kind: .cpu, avgCPU: avg, idleMinutes: nil, recommended: false))
                continue
            }
            // Idle memory: a regular app in the background that is big and has not been used for a while.
            guard let path = g.appPath, path != front, g.memory > 300 * 1_048_576,
                  let app = running.first(where: { $0.bundleURL?.path == path }), app.activationPolicy == .regular else { continue }
            let since = [m.lastActive[path], app.launchDate, m.startedAt].compactMap { $0 }.max() ?? m.startedAt
            let idle = Int(Date().timeIntervalSince(since) / 60)
            guard idle >= 30 else { continue }
            out.append(ResourceHog(group: g, kind: .idleMemory, avgCPU: avg, idleMinutes: idle,
                                   recommended: idle >= 60 && g.memory > 500 * 1_048_576))
        }
        return out.sorted { a, b in
            if a.kind != b.kind { return a.kind == .cpu }
            return a.kind == .cpu ? a.avgCPU > b.avgCPU : a.group.memory > b.group.memory
        }
    }

    /// Plain-language memory state.
    @MainActor
    static func memorySummary() -> (title: String, detail: String, level: Int) {
        let mem = SystemMonitor.shared.memory
        let cached = Fmt.bytes(mem.cached)
        switch mem.pressureLevel {
        case 4:
            return (L("内存很紧张", "Memory is tight"),
                    L("系统已经在大量压缩内存、用硬盘当内存，会明显变卡。退出几个占内存大的应用最有效。",
                      "macOS is compressing heavily and swapping to disk, which slows things down. Quitting a few large apps helps most."), 4)
        case 2:
            return (L("内存有点紧", "Memory is getting tight"),
                    L("还能正常用，但再多开几个大应用就会变慢。可以退出下面这些很久没用的应用。",
                      "Still fine, but a few more big apps will slow things down. Consider quitting the unused apps below."), 2)
        default:
            return (L("内存充足", "Memory is fine"),
                    L("「已用」里有 \(cached) 是系统缓存，需要时会自动让出，不用手动清理。",
                      "\(cached) of what looks used is file cache that macOS frees on demand. Nothing to clean."), 1)
        }
    }

    /// Asks each app to quit (like ⌘Q) or sends SIGTERM to loose processes. Never forces.
    @MainActor
    static func quit(_ hogs: [ResourceHog]) -> (count: Int, memory: Int64) {
        var n = 0
        var mem: Int64 = 0
        for h in hogs where ProcessControl.quit(h.group, force: false) {
            n += 1
            mem += h.group.memory
        }
        return (n, mem)
    }
}
