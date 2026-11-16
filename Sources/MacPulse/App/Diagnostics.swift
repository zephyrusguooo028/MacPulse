import Foundation
import AppKit
import SwiftUI

/// `MacPulse --selftest`: prints every reading and scan result without any UI.
enum SelfTest {
    static func run() {
        let cpu = CPUReader()
        let net = NetworkReader()
        _ = cpu.read(); _ = net.read()
        let procs = ProcessReader()
        _ = procs.read()
        Thread.sleep(forTimeInterval: 1.5)

        let c = cpu.read()
        print("CPU total \(Fmt.pct(c.total, digits: 1)) user \(Fmt.pct(c.user, digits: 1)) sys \(Fmt.pct(c.system, digits: 1)) cores \(c.perCore.count) (\(cpu.summary)) letters \(cpu.coreLetters.joined())")
        let m = MemoryReader.read()
        print("MEM used \(Fmt.bytes(m.used)) / \(Fmt.bytes(m.total)) app \(Fmt.bytes(m.app)) wired \(Fmt.bytes(m.wired)) compressed \(Fmt.bytes(m.compressed)) cached \(Fmt.bytes(m.cached)) swap \(Fmt.bytes(m.swapUsed)) pressure \(m.pressureLevel)")
        let d = DiskReader.read()
        print("DISK total \(Fmt.bytes(d.total)) available \(Fmt.bytes(d.available)) free \(Fmt.bytes(d.free)) purgeable \(Fmt.bytes(d.purgeable))")
        let n = net.read()
        print("NET in \(Fmt.rate(n.inRate)) out \(Fmt.rate(n.outRate)) totals \(Fmt.bytes(n.totalIn)) / \(Fmt.bytes(n.totalOut))")
        print("GPU \(GPUReader.read().map { Fmt.pct($0) } ?? "nil")  TEMP \(TemperatureReader().read().map { String(format: "%.1f°C", $0) } ?? "nil")")
        if let b = BatteryReader.read() { print("BATTERY \(Fmt.pct(b.percent)) charging \(b.charging) ac \(b.onAC)") } else { print("BATTERY none") }
        print("UPTIME \(SystemInfo.uptimeText()) LOAD \(SystemInfo.loadAverage)")

        let p = procs.read()
        let groups = procs.group(p).sorted { $0.memory > $1.memory }
        print("PROCS \(p.count), groups \(groups.count). Top by memory:")
        for g in groups.prefix(6) { print("   \(g.name)  \(Fmt.bytes(g.memory))  cpu \(String(format: "%.1f%%", g.cpu * 100))  ×\(g.pids.count)") }

        print("DEV CACHES:")
        for i in CleanupScanner.scanDevCaches() { print("   \(i.title)  \(Fmt.bytes(i.size))  \(i.recommended ? "推荐" : "")  \(i.subtitle)") }
        let running = CleanupScanner.runningNames()
        print("APP CACHES:")
        for i in CleanupScanner.scanAppCaches(running: running).sorted(by: { $0.size > $1.size }).prefix(8) { print("   \(i.title)  \(Fmt.bytes(i.size))  \(i.recommended ? "推荐" : "")  \(i.subtitle)") }
        print("LOGS: \(Fmt.bytes(CleanupScanner.scanLogs().reduce(0) { $0 + $1.size }))")
        print("DOWNLOADS (old):")
        for i in CleanupScanner.scanDownloads(olderThanDays: 90).sorted(by: { $0.size > $1.size }).prefix(8) { print("   \(i.title)  \(Fmt.bytes(i.size))  \(i.subtitle)") }
        let t = CleanupModel.measureTrash()
        print("TRASH \(t.readable ? Fmt.bytes(t.size) : "not readable")")

        let engine = ProjectScanEngine()
        let found = engine.findProjects(roots: AppSettings.shared.projectRootURLs)
        print("PROJECTS \(found.count):")
        for (u, tags, root) in found {
            let pr = engine.measure(u, tags: tags, root: root)
            print("   \(pr.displayName) [\(tags.joined(separator: ","))] total \(Fmt.bytes(pr.totalSize)) src \(Fmt.bytes(pr.sourceSize)) git \(Fmt.bytes(pr.gitSize)) artifacts \(Fmt.bytes(pr.artifactSize)) modified \(Fmt.relative(pr.lastModified))")
            for a in pr.artifacts.prefix(4) { print("      - \(a.url.lastPathComponent) \(Fmt.bytes(a.size)) \(a.safe ? "safe" : "UNSURE")") }
        }

        print("SAFETY home=\(CleanupModel.isSafeTarget(Paths.home)) docs=\(CleanupModel.isSafeTarget(Paths.h("Documents"))) caches=\(CleanupModel.isSafeTarget(Paths.h("Library/Caches"))) etc=\(CleanupModel.isSafeTarget(URL(fileURLWithPath: "/etc"))) npm=\(CleanupModel.isSafeTarget(Paths.h(".npm/_cacache")))")
        let rects = Treemap.layout([6, 6, 4, 3, 2, 2, 1], in: CGRect(x: 0, y: 0, width: 600, height: 400))
        let area = rects.reduce(0) { $0 + $1.width * $1.height }
        print("TREEMAP area \(Int(area)) of 240000, all inside: \(rects.allSatisfy { CGRect(x: -0.01, y: -0.01, width: 600.02, height: 400.02).contains($0) })")
    }
}

/// `MacPulse --snapshot <dir>`: opens every page with live data and saves PNGs.
/// Used to check the UI without needing screen-recording permission.
@MainActor
enum Snapshot {
    static func run(to dir: URL) {
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        Task { @MainActor in
            let app = AppState.shared
            app.monitor.wantProcesses(true)
            try? await Task.sleep(for: .seconds(5))
            for st in PopoverStyle.allCases {
                savePopover(st, to: dir.appendingPathComponent("0-popover-\(st.rawValue).png"))
            }

            if CommandLine.arguments.contains("--popover-only") { NSApp.terminate(nil); return }
            WindowManager.shared.show(.overview)
            try? await Task.sleep(for: .seconds(2))
            saveWindow(to: dir.appendingPathComponent("1-overview.png"))

            app.tab = .disk
            app.disk.scan(Paths.home)
            try? await Task.sleep(for: .seconds(1))
            saveWindow(to: dir.appendingPathComponent("2-disk-scanning.png"))
            while app.disk.scanning { try? await Task.sleep(for: .milliseconds(500)) }
            try? await Task.sleep(for: .seconds(1))
            saveWindow(to: dir.appendingPathComponent("2-disk.png"))
            if let lib = app.disk.root?.children.first(where: { $0.name == "Library" }) {
                app.disk.open(lib)
                try? await Task.sleep(for: .seconds(1))
                saveWindow(to: dir.appendingPathComponent("2-disk-library.png"))
            }

            app.tab = .projects
            try? await Task.sleep(for: .seconds(1))
            while app.projects.scanning { try? await Task.sleep(for: .milliseconds(500)) }
            try? await Task.sleep(for: .seconds(1))
            saveWindow(to: dir.appendingPathComponent("3-projects.png"))

            app.tab = .processes
            try? await Task.sleep(for: .seconds(4))
            saveWindow(to: dir.appendingPathComponent("4-processes.png"))

            app.tab = .cleanup
            try? await Task.sleep(for: .seconds(1))
            while app.cleanup.scanning || app.projects.scanning { try? await Task.sleep(for: .milliseconds(500)) }
            try? await Task.sleep(for: .seconds(1))
            saveWindow(to: dir.appendingPathComponent("5-cleanup.png"))

            TypingMonitor.shared.loadDemo()
            app.tab = .typing
            try? await Task.sleep(for: .seconds(1))
            saveWindow(to: dir.appendingPathComponent("7-typing.png"))

            app.tab = .settings
            try? await Task.sleep(for: .seconds(1))
            saveWindow(to: dir.appendingPathComponent("6-settings.png"))
            NSApp.terminate(nil)
        }
    }

    private static func saveWindow(to url: URL) {
        guard let v = WindowManager.shared.window?.contentView else { return }
        saveView(v, to: url)
    }

    private static func savePopover(_ style: PopoverStyle, to url: URL) {
        let host = NSHostingView(rootView: PopoverView(style: style).environment(AppState.shared))
        host.frame = NSRect(origin: .zero, size: host.fittingSize)
        let w = NSWindow(contentRect: host.frame, styleMask: [.borderless], backing: .buffered, defer: false)
        w.contentView = host
        w.backgroundColor = .windowBackgroundColor
        host.layoutSubtreeIfNeeded()
        saveView(host, to: url)
    }

    private static func saveView(_ v: NSView, to url: URL) {
        v.layoutSubtreeIfNeeded()
        guard let rep = v.bitmapImageRepForCachingDisplay(in: v.bounds) else { return }
        v.cacheDisplay(in: v.bounds, to: rep)
        try? rep.representation(using: .png, properties: [:])?.write(to: url)
    }

    private static func savePNG(_ img: NSImage, scale: CGFloat, to url: URL) {
        let size = NSSize(width: img.size.width * scale, height: img.size.height * scale)
        let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: Int(size.width), pixelsHigh: Int(size.height), bitsPerSample: 8,
                                   samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
        NSColor.white.setFill()
        NSRect(origin: .zero, size: size).fill()
        img.draw(in: NSRect(origin: .zero, size: size))
        NSGraphicsContext.restoreGraphicsState()
        try? rep.representation(using: .png, properties: [:])?.write(to: url)
    }
}

/// `MacPulse --icon-sheet <file.png>`: every icon × every frame, on light and dark
/// menu-bar backgrounds, plus the load-tinted variant.
enum IconSheet {
    @MainActor
    static func run(to url: URL) {
        let scale: CGFloat = 4
        let icons = MenuIcon.allCases.filter { $0 != .none }
        let maxFrames = icons.map(\.frameCount).max() ?? 1
        let cellW: CGFloat = 26 * scale, cellH: CGFloat = 18 * scale + 12
        let variants: [(bg: NSColor, fg: NSColor?)] = [
            (NSColor(white: 0.94, alpha: 1), .black), (NSColor(white: 0.16, alpha: 1), .white),
            (NSColor(white: 0.16, alpha: 1), IconRenderer.tint(for: 0.7)),
        ]
        let rows = icons.count * variants.count
        let W = Int(CGFloat(maxFrames) * cellW + 110), H = Int(CGFloat(rows) * cellH)
        let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: W, pixelsHigh: H, bitsPerSample: 8, samplesPerPixel: 4,
                                   hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
        var row = 0
        for icon in icons {
            for v in variants {
                let y = CGFloat(H) - CGFloat(row + 1) * cellH
                v.bg.setFill(); NSRect(x: 0, y: y, width: CGFloat(W), height: cellH).fill()
                let label = v.bg.whiteComponent > 0.5 ? NSColor.black : NSColor.white
                (icon.title as NSString).draw(at: NSPoint(x: 8, y: y + cellH / 2 - 9), withAttributes: [.font: NSFont.systemFont(ofSize: 15, weight: .semibold), .foregroundColor: label])
                for f in 0..<icon.frameCount {
                    let img = IconRenderer.bitmap(icon, frame: f, cpu: 0.6, tint: v.fg, scale: scale)
                    img.draw(in: NSRect(x: 110 + CGFloat(f) * cellW, y: y + 6, width: img.size.width * scale, height: img.size.height * scale))
                }
                row += 1
            }
        }
        NSGraphicsContext.restoreGraphicsState()
        try? rep.representation(using: .png, properties: [:])?.write(to: url)
    }
}
