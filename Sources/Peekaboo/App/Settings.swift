import Foundation
import Observation
import ServiceManagement

/// Interface language.
enum AppLanguage: String, CaseIterable, Identifiable {
    case system, zh, en
    var id: String { rawValue }
    var title: String {
        switch self {
        case .system: return "跟随系统 / System"
        case .zh: return "简体中文"
        case .en: return "English"
        }
    }
}

/// Picks the Chinese or English text for the current language setting.
/// Reading the setting inside a SwiftUI body makes the view refresh when it changes.
func L(_ zh: String, _ en: String) -> String { Loc.isEnglish ? en : zh }

enum Loc {
    static var isEnglish: Bool {
        switch AppSettings.shared.language {
        case .zh: return false
        case .en: return true
        case .system: return !(Locale.preferredLanguages.first ?? "en").hasPrefix("zh")
        }
    }
    static var locale: Locale { Locale(identifier: isEnglish ? "en_US" : "zh_CN") }
}

/// The highlight colour used for the animated icon, selections and buttons.
enum ThemeColor: String, CaseIterable, Identifiable {
    case system, mono, load, pink, mint, purple, orange, blue
    var id: String { rawValue }
    var title: String {
        switch self {
        case .system: return L("跟随系统", "System")
        case .mono: return L("黑白", "Mono")
        case .load: return L("跟随负载", "By load")
        case .pink: return L("樱花粉", "Pink")
        case .mint: return L("薄荷绿", "Mint")
        case .purple: return L("葡萄紫", "Purple")
        case .orange: return L("橘子橙", "Orange")
        case .blue: return L("天空蓝", "Blue")
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
    var language: AppLanguage { didSet { d.set(language.rawValue, forKey: "language") } }
    var typingEnabled: Bool { didSet { d.set(typingEnabled, forKey: "typingEnabled") } }
    var typingGoal: Int { didSet { d.set(typingGoal, forKey: "typingGoal") } }
    /// nil = no shortcut.
    var shotHotkey: HotkeySpec? { didSet { d.set(try? JSONEncoder().encode(shotHotkey), forKey: "shotHotkeySpec") } }
    var shotFolder: String { didSet { d.set(shotFolder, forKey: "shotFolder") } }
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
        language = AppLanguage(rawValue: d.string(forKey: "language") ?? "") ?? .system
        typingEnabled = d.object(forKey: "typingEnabled") as? Bool ?? false
        typingGoal = d.object(forKey: "typingGoal") as? Int ?? 10000
        if let data = d.data(forKey: "shotHotkeySpec") {
            shotHotkey = try? JSONDecoder().decode(HotkeySpec?.self, from: data) ?? nil
        } else {
            shotHotkey = .defaultShot
        }
        shotFolder = d.string(forKey: "shotFolder") ?? "~/Desktop"
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
                NSLog("Peekaboo: launch-at-login change failed: \(error)")
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
