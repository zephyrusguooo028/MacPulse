import Foundation

/// Made-up duplicates and apps for `--demo` screenshots. Nothing here reads the disk.
extension Demo {
    private static let KB: Int64 = 1024
    private static func u(_ rel: String) -> URL { Paths.home.appendingPathComponent(rel) }
    private static func ago(_ days: Double) -> Date { Date().addingTimeInterval(-days * 86_400) }

    private static func group(_ kind: DupKind, _ files: [(String, Int64, Double)]) -> DupGroup {
        let fs = files.map { DupFile(url: u($0.0), size: $0.1, modified: ago($0.2)) }
        return DupGroup(id: kind.rawValue + fs.map(\.url.path).joined(), kind: kind, files: fs)
    }

    static func duplicateGroups() -> [DupGroup] { [
        group(.exact, [("Downloads/Installer-4.2.dmg", 612 * 1024 * KB, 3), ("Downloads/Installer-4.2 (1).dmg", 612 * 1024 * KB, 40), ("Desktop/Installer-4.2.dmg", 612 * 1024 * KB, 41)]),
        group(.exact, [("Documents/Video/Interview-raw.mov", 840 * 1024 * KB, 12), ("Downloads/Interview-raw.mov", 840 * 1024 * KB, 60)]),
        group(.exact, [("Documents/Reports/Annual-report-final.pdf", 18 * 1024 * KB, 9), ("Desktop/Annual-report-final.pdf", 18 * 1024 * KB, 9.5), ("Downloads/Annual-report-final (2).pdf", 18 * 1024 * KB, 30)]),
        group(.exact, [("Pictures/Trip/Lake 0412.jpg", 4_300 * KB, 120), ("Downloads/Lake 0412.jpg", 4_300 * KB, 118)]),
        group(.exact, [("Documents/Slides/Kickoff.key", 36 * 1024 * KB, 22), ("Documents/Slides/Kickoff copy.key", 36 * 1024 * KB, 22.2)]),
        group(.exact, [("Downloads/fonts-pack.zip", 9_800 * KB, 200), ("Documents/Design/fonts-pack.zip", 9_800 * KB, 190)]),
    ] }

    static func similarGroups() -> [DupGroup] { [
        group(.similar, [("Pictures/Trip/Sunset 2031.jpg", 5_200 * KB, 30), ("Pictures/Trip/Sunset 2031 (edited).jpg", 3_900 * KB, 29), ("Desktop/Sunset small.png", 1_100 * KB, 10)]),
        group(.similar, [("Pictures/Family/Garden 1180.heic", 2_600 * KB, 90), ("Pictures/Family/Garden 1181.heic", 2_550 * KB, 90)]),
        group(.similar, [("Pictures/Screenshots/Mockup v3.png", 880 * KB, 5), ("Downloads/Mockup v3 export.png", 610 * KB, 4)]),
    ] }

    static func uninstallApps() -> [InstalledApp] {
        func app(_ name: String, _ id: String, _ v: String, _ mb: Int64, _ used: Double?, _ sym: String) -> InstalledApp {
            InstalledApp(url: URL(fileURLWithPath: "/Applications/\(name).app"), name: name, bundleID: id, version: v, size: mb * 1024 * KB,
                         lastUsed: used.map(ago), demoSymbol: sym)
        }
        return [
            app("Pixel Studio", "com.example.pixelstudio", "5.1.2", 2_480, 2, "paintbrush.pointed.fill"),
            app("Code Forge", "dev.example.codeforge", "1.94.0", 1_120, 0.1, "chevron.left.forwardslash.chevron.right"),
            app("Meet Room", "com.example.meetroom", "6.2.0", 640, 21, "video.fill"),
            app("Note Garden", "com.example.notegarden", "3.8", 410, 1, "leaf.fill"),
            app("Tidy Mail", "com.example.tidymail", "2.4.1", 286, 180, "envelope.fill"),
            app("Beat Box", "com.example.beatbox", "1.12", 352, 95, "music.note"),
            app("Map Sketch", "com.example.mapsketch", "0.9.7", 198, 400, "map.fill"),
            app("Pocket Vault", "com.example.pocketvault", "8.10", 164, 7, "lock.fill"),
            app("Clip Cut", "com.example.clipcut", "4.0", 930, 260, "film.fill"),
            app("Shelf Reader", "com.example.shelfreader", "2.2", 74, nil, "book.fill"),
        ]
    }

    static func leftovers(for app: InstalledApp) -> [Leftover] {
        let id = app.bundleID
        return [
            Leftover(url: u("Library/Application Support/\(app.name)"), kind: L("应用数据", "App data"), size: 386 * 1024 * KB),
            Leftover(url: u("Library/Caches/\(id)"), kind: L("缓存", "Cache"), size: 212 * 1024 * KB),
            Leftover(url: u("Library/Containers/\(id)"), kind: L("沙盒容器", "Sandbox container"), size: 48 * 1024 * KB),
            Leftover(url: u("Library/Logs/\(app.name)"), kind: L("日志", "Logs"), size: 3_400 * KB),
            Leftover(url: u("Library/HTTPStorages/\(id)"), kind: L("网络存储", "HTTP storage"), size: 820 * KB),
            Leftover(url: u("Library/Saved Application State/\(id).savedState"), kind: L("窗口状态", "Saved window state"), size: 96 * KB),
            Leftover(url: u("Library/Preferences/\(id).plist"), kind: L("偏好设置", "Preferences"), size: 12 * KB),
            Leftover(url: u("Library/LaunchAgents/\(id).helper.plist"), kind: L("登录启动项", "Launch agent"), size: 4 * KB),
        ]
    }
}
