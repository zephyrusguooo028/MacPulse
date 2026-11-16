import Foundation
import Observation
import ServiceManagement

/// The highlight colour used for the animated icon, selections and buttons.
enum ThemeColor: String, CaseIterable, Identifiable {
    case system, mono, load, pink, mint, purple, orange, blue
    var id: String { rawValue }
    var title: String {
        switch self {
        case .system: return "跟随系统"
        case .mono: return "黑白"
        case .load: return "跟随负载"
        case .pink: return "樱花粉"
        case .mint: return "薄荷绿"
        case .purple: return "葡萄紫"
        case .orange: return "橘子橙"
        case .blue: return "天空蓝"
        }
    }
}

@Observable
final class AppSettings {
    static let shared = AppSettings()
    private let d = UserDefaults.standard

    var menuIcon: MenuIcon { didSet { d.set(menuIcon.rawValue, forKey: "menuIcon") } }
    /// Ticked metrics, in display order.
    var menuMetrics: [MenuMetric] { didSet { d.set(menuMetrics.map(\.rawValue), forKey: "menuMetrics") } }
    var iconTint: Bool { didSet { d.set(iconTint, forKey: "iconTint") } }
    var themeColor: ThemeColor { didSet { d.set(themeColor.rawValue, forKey: "themeColor") } }
    var popoverStyle: PopoverStyle { didSet { d.set(popoverStyle.rawValue, forKey: "popoverStyle") } }
    var sampleInterval: Double { didSet { d.set(sampleInterval, forKey: "sampleInterval") } }
    var showTemperature: Bool { didSet { d.set(showTemperature, forKey: "showTemperature") } }

    var projectRoots: [String] { didSet { d.set(projectRoots, forKey: "projectRoots") } }
    var whitelist: [String] { didSet { d.set(whitelist, forKey: "whitelist") } }
    var downloadsOldDays: Int { didSet { d.set(downloadsOldDays, forKey: "downloadsOldDays") } }
    var largeFileMB: Int { didSet { d.set(largeFileMB, forKey: "largeFileMB") } }
    var staleProjectDays: Int { didSet { d.set(staleProjectDays, forKey: "staleProjectDays") } }

    var alertDisk: Bool { didSet { d.set(alertDisk, forKey: "alertDisk") } }
    var alertDiskGB: Int { didSet { d.set(alertDiskGB, forKey: "alertDiskGB") } }
    var alertMemory: Bool { didSet { d.set(alertMemory, forKey: "alertMemory") } }
    var alertCPU: Bool { didSet { d.set(alertCPU, forKey: "alertCPU") } }
    var alertCPUPercent: Int { didSet { d.set(alertCPUPercent, forKey: "alertCPUPercent") } }
    var cleanupTips: Bool { didSet { d.set(cleanupTips, forKey: "cleanupTips") } }
    var cleanupTipGB: Int { didSet { d.set(cleanupTipGB, forKey: "cleanupTipGB") } }

    var lastCleanupTip: Date? {
        get { d.object(forKey: "lastCleanupTip") as? Date }
        set { d.set(newValue, forKey: "lastCleanupTip") }
    }

    private init() {
        menuIcon = MenuIcon(rawValue: d.string(forKey: "menuIcon") ?? "") ?? .ghost
        menuMetrics = (d.stringArray(forKey: "menuMetrics") ?? ["cpu", "memory"]).compactMap(MenuMetric.init(rawValue:))
        iconTint = d.object(forKey: "iconTint") as? Bool ?? false
        themeColor = ThemeColor(rawValue: d.string(forKey: "themeColor") ?? "") ?? .system
        popoverStyle = PopoverStyle(rawValue: d.string(forKey: "popoverStyle") ?? "") ?? .compact
        sampleInterval = d.object(forKey: "sampleInterval") as? Double ?? 2
        showTemperature = d.object(forKey: "showTemperature") as? Bool ?? true
        projectRoots = d.stringArray(forKey: "projectRoots") ?? ["~/Documents", "~/Desktop", "~/Developer", "~/Projects", "~/code", "~/work"]
        whitelist = d.stringArray(forKey: "whitelist") ?? []
        downloadsOldDays = d.object(forKey: "downloadsOldDays") as? Int ?? 90
        largeFileMB = d.object(forKey: "largeFileMB") as? Int ?? 500
        staleProjectDays = d.object(forKey: "staleProjectDays") as? Int ?? 60
        alertDisk = d.object(forKey: "alertDisk") as? Bool ?? true
        alertDiskGB = d.object(forKey: "alertDiskGB") as? Int ?? 20
        alertMemory = d.object(forKey: "alertMemory") as? Bool ?? true
        alertCPU = d.object(forKey: "alertCPU") as? Bool ?? false
        alertCPUPercent = d.object(forKey: "alertCPUPercent") as? Int ?? 90
        cleanupTips = d.object(forKey: "cleanupTips") as? Bool ?? true
        cleanupTipGB = d.object(forKey: "cleanupTipGB") as? Int ?? 5
    }

    var projectRootURLs: [URL] {
        projectRoots.map { URL(fileURLWithPath: ($0 as NSString).expandingTildeInPath) }
            .filter { FileManager.default.fileExists(atPath: $0.path) }
    }

    func isWhitelisted(_ url: URL) -> Bool {
        let p = url.standardizedFileURL.path
        return whitelist.contains { w in
            let wp = (w as NSString).expandingTildeInPath
            return p == wp || p.hasPrefix(wp.hasSuffix("/") ? wp : wp + "/")
        }
    }

    // MARK: Launch at login

    var launchAtLogin: Bool {
        get { SMAppService.mainApp.status == .enabled }
        set {
            do {
                if newValue { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }
            } catch {
                NSLog("MacPulse: launch-at-login change failed: \(error)")
            }
        }
    }

    var launchAtLoginNeedsApproval: Bool { SMAppService.mainApp.status == .requiresApproval }
}

extension AppSettings {
    func isShown(_ m: MenuMetric) -> Bool { menuMetrics.contains(m) }

    /// Toggle a metric while keeping the canonical order.
    func setShown(_ m: MenuMetric, _ on: Bool) {
        var set = Set(menuMetrics)
        if on { set.insert(m) } else { set.remove(m) }
        menuMetrics = MenuMetric.allCases.filter { set.contains($0) }
    }
}
