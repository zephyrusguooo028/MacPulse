import Foundation
import UserNotifications

/// Threshold alerts (disk, memory pressure, sustained CPU) and the weekly
/// "you could free X GB" tip. Each alert has a cooldown so it never nags.
@MainActor
final class AlertManager: NSObject, UNUserNotificationCenterDelegate {
    static let shared = AlertManager()

    private var lastFired: [String: Date] = [:]
    private var pressureSince: Date?
    private var cpuSince: Date?
    private var lastTipCheck = Date.distantPast
    private var available: Bool { Bundle.main.bundleIdentifier != nil }

    func setup() {
        guard available else { return }
        let c = UNUserNotificationCenter.current()
        c.delegate = self
        c.requestAuthorization(options: [.alert, .sound]) { _, _ in }
    }

    func check(_ m: SystemMonitor) {
        let s = AppSettings.shared
        let now = Date()

        if s.alertDisk, m.disk.total > 0 {
            let gb = Double(m.disk.available) / 1_073_741_824
            if gb < Double(s.alertDiskGB) {
                fire("disk", cooldown: 6 * 3600, title: "磁盘空间不足",
                     body: "只剩 \(Fmt.bytes(m.disk.available))，打开 Peekaboo 看看哪些可以清理。", tab: .cleanup)
            }
        }

        if s.alertMemory {
            if m.memory.pressureLevel >= 4 {
                if pressureSince == nil { pressureSince = now }
                if now.timeIntervalSince(pressureSince!) > 30 {
                    fire("mem", cooldown: 3600, title: "内存压力很高",
                         body: "已用 \(Fmt.bytes(m.memory.used)) / \(Fmt.bytes(m.memory.total))，可以关掉一些占内存的应用。", tab: .processes)
                }
            } else { pressureSince = nil }
        }

        if s.alertCPU {
            if m.cpu.total * 100 >= Double(s.alertCPUPercent) {
                if cpuSince == nil { cpuSince = now }
                if now.timeIntervalSince(cpuSince!) > 300 {
                    fire("cpu", cooldown: 3600, title: "CPU 持续高负载",
                         body: "过去 5 分钟 CPU 一直在 \(s.alertCPUPercent)% 以上。", tab: .processes)
                }
            } else { cpuSince = nil }
        }

        if s.cleanupTips, now.timeIntervalSince(lastTipCheck) > 6 * 3600 {
            lastTipCheck = now
            let last = s.lastCleanupTip ?? .distantPast
            if now.timeIntervalSince(last) > 7 * 86400 { checkCleanupTip() }
        }
    }

    /// Quick background estimate of the always-safe stuff (dev caches + logs).
    private func checkCleanupTip() {
        let threshold = Int64(AppSettings.shared.cleanupTipGB) * 1_073_741_824
        Task.detached(priority: .background) {
            let total = (CleanupScanner.scanDevCaches() + CleanupScanner.scanLogs())
                .filter(\.recommended).reduce(Int64(0)) { $0 + $1.size }
            await MainActor.run {
                AppSettings.shared.lastCleanupTip = Date()
                if total >= threshold {
                    self.fire("tip", cooldown: 0, title: "可以清理 \(Fmt.bytes(total))",
                              body: "开发缓存和日志占了 \(Fmt.bytes(total))，点这里查看并清理。", tab: .cleanup)
                }
            }
        }
    }

    private func fire(_ key: String, cooldown: TimeInterval, title: String, body: String, tab: MainTab) {
        if let l = lastFired[key], Date().timeIntervalSince(l) < cooldown { return }
        lastFired[key] = Date()
        guard available else { NSLog("Peekaboo alert: \(title) — \(body)"); return }
        let c = UNMutableNotificationContent()
        c.title = title
        c.body = body
        c.userInfo = ["tab": tab.rawValue]
        UNUserNotificationCenter.current().add(UNNotificationRequest(identifier: key + UUID().uuidString, content: c, trigger: nil))
    }

    nonisolated func userNotificationCenter(_ center: UNUserNotificationCenter, didReceive response: UNNotificationResponse, withCompletionHandler done: @escaping () -> Void) {
        let raw = response.notification.request.content.userInfo["tab"] as? String
        Task { @MainActor in
            WindowManager.shared.show(MainTab(rawValue: raw ?? "") ?? .overview)
        }
        done()
    }

    nonisolated func userNotificationCenter(_ center: UNUserNotificationCenter, willPresent notification: UNNotification, withCompletionHandler done: @escaping (UNNotificationPresentationOptions) -> Void) {
        done([.banner, .sound])
    }
}
