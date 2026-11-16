import SwiftUI
import AppKit

@main
enum Entry {
    static func main() {
        let args = CommandLine.arguments
        if let i = args.firstIndex(of: "--icon-sheet"), i + 1 < args.count {
            MainActor.assumeIsolated { IconSheet.run(to: URL(fileURLWithPath: args[i + 1])) }
            exit(0)
        }
        if args.contains("--selftest") {
            SelfTest.run()
            exit(0)
        }
        MacPulseApp.main()
    }
}

enum MainTab: String, CaseIterable, Identifiable {
    case overview, disk, projects, processes, cleanup, typing, settings
    var id: String { rawValue }
    var title: String {
        switch self {
        case .overview: return "概览"
        case .disk: return "磁盘"
        case .projects: return "项目"
        case .processes: return "进程"
        case .cleanup: return "清理"
        case .typing: return "打字"
        case .settings: return "设置"
        }
    }
    var symbol: String {
        switch self {
        case .overview: return "gauge.with.dots.needle.67percent"
        case .disk: return "internaldrive"
        case .projects: return "folder.badge.gearshape"
        case .processes: return "list.bullet.rectangle"
        case .cleanup: return "sparkles"
        case .typing: return "keyboard"
        case .settings: return "gearshape"
        }
    }
}

/// Shared state for every view.
@MainActor
@Observable
final class AppState {
    static let shared = AppState()
    let monitor = SystemMonitor.shared
    let settings = AppSettings.shared
    let disk = DiskScanModel()
    let projects = ProjectModel()
    let cleanup = CleanupModel()
    var tab: MainTab = .overview
}

struct MacPulseApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) var delegate

    var body: some Scene {
        // The menu bar item and windows are managed through AppKit (StatusItemController,
        // WindowManager); SwiftUI only needs a scene to exist.
        Settings { EmptyView() }
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationDidFinishLaunching(_ n: Notification) {
        MainActor.assumeIsolated {
            NSApp.setActivationPolicy(.accessory)
            let m = SystemMonitor.shared
            m.onTick = {
                AlertManager.shared.check($0)
                StatusItemController.shared.refresh()
            }
            m.start()
            IconAnimator.shared.start()
            StatusItemController.shared.install()
            TypingMonitor.shared.apply()
            AlertManager.shared.setup()
            if let i = CommandLine.arguments.firstIndex(of: "--snapshot"), i + 1 < CommandLine.arguments.count {
                Snapshot.run(to: URL(fileURLWithPath: CommandLine.arguments[i + 1]))
            } else if CommandLine.arguments.contains("--open") {
                WindowManager.shared.show(.overview)
            }
        }
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows: Bool) -> Bool {
        MainActor.assumeIsolated { WindowManager.shared.show(nil) }
        return true
    }

    func applicationWillTerminate(_ n: Notification) {
        MainActor.assumeIsolated { SystemMonitor.shared.shutdown(); TypingMonitor.shared.save() }
    }
}

/// The main window is managed through AppKit so it can be opened from anywhere
/// (popover, notifications) and so the Dock icon only shows while it is open.
@MainActor
final class WindowManager: NSObject, NSWindowDelegate {
    static let shared = WindowManager()
    private(set) var window: NSWindow?

    func show(_ tab: MainTab?) {
        StatusItemController.shared.closePopover()
        if let tab { AppState.shared.tab = tab }
        if window == nil {
            let host = NSHostingController(rootView: MainView().environment(AppState.shared))
            let w = NSWindow(contentViewController: host)
            w.title = "MacPulse"
            w.styleMask = [.titled, .closable, .miniaturizable, .resizable, .fullSizeContentView]
            w.titlebarAppearsTransparent = true
            w.toolbarStyle = .unified
            w.setContentSize(NSSize(width: 1120, height: 760))
            w.minSize = NSSize(width: 900, height: 600)
            w.isReleasedWhenClosed = false
            w.delegate = self
            w.center()
            w.setFrameAutosaveName("MacPulseMain")
            window = w
        }
        NSApp.setActivationPolicy(.regular)
        window?.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    func windowWillClose(_ notification: Notification) {
        NSApp.setActivationPolicy(.accessory)
    }
}
