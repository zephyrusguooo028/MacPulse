import Foundation

/// `--demo`: made-up projects, processes, disk contents and cleanup items, so README
/// screenshots never show anyone's real files or apps. Live CPU / memory stay real.
enum Demo {
    static let active = CommandLine.arguments.contains("--demo")

    private static let GB: Int64 = 1_073_741_824
    private static let MB: Int64 = 1_048_576
    private static func h(_ rel: String) -> URL { Paths.home.appendingPathComponent(rel) }

    // MARK: Disk

    static func diskTree() -> DiskNode {
        func dir(_ rel: String, _ size: Int64, _ kids: [DiskNode] = [], files: Int = 1200) -> DiskNode {
            DiskNode(url: h(rel), isDirectory: true, size: size, children: kids, fileCount: files)
        }
        let library = dir("Library", 18 * GB, [
            dir("Library/Application Support", 9 * GB + 300 * MB, files: 52_000),
            dir("Library/Caches", 4 * GB + 600 * MB, files: 38_000),
            dir("Library/Containers", 2 * GB + 100 * MB, files: 21_000),
            dir("Library/Developer", 1 * GB + 400 * MB, files: 9_000),
            dir("Library/Mail", 420 * MB, files: 6_000),
            DiskNode(url: h("Library"), name: L("其他 34 个小项目", "34 other small items"), isDirectory: false, size: 180 * MB, isAggregate: true, fileCount: 2_100),
        ], files: 128_000)
        let root = dir("", 64 * GB, [
            library,
            dir("Movies", 14 * GB, files: 80),
            dir("Downloads", 9 * GB + 200 * MB, files: 640),
            dir("Code", 8 * GB + 500 * MB, files: 210_000),
            dir("Pictures", 6 * GB + 300 * MB, files: 12_000),
            dir("Documents", 3 * GB + 100 * MB, files: 4_800),
            dir(".npm", 1 * GB + 200 * MB, files: 60_000),
            dir(".cargo", 820 * MB, files: 14_000),
            dir("Music", 640 * MB, files: 900),
            dir("Desktop", 210 * MB, files: 40),
            DiskNode(url: Paths.home, name: L("其他 18 个小项目", "18 other small items"), isDirectory: false, size: 96 * MB, isAggregate: true, fileCount: 300),
        ], files: 510_000)
        func link(_ n: DiskNode) { for c in n.children { c.parent = n; link(c) } }
        link(root)
        return root
    }

    static func largeFiles() -> [LargeFile] {
        [
            LargeFile(url: h("Movies/Trip to Kyoto 4K.mov"), size: 6 * GB + 400 * MB, modified: Date().addingTimeInterval(-86400 * 210)),
            LargeFile(url: h("Downloads/Xcode_16.xip"), size: 3 * GB + 100 * MB, modified: Date().addingTimeInterval(-86400 * 120)),
            LargeFile(url: h("Movies/Screen Recording 2026-03-02.mov"), size: 1 * GB + 800 * MB, modified: Date().addingTimeInterval(-86400 * 260)),
            LargeFile(url: h("Downloads/ubuntu-24.04-desktop-arm64.iso"), size: 2 * GB + 600 * MB, modified: Date().addingTimeInterval(-86400 * 95)),
        ]
    }

    // MARK: Projects

    static func projects() -> [Project] {
        func art(_ rel: String, _ kind: ArtifactKind, _ size: Int64, safe: Bool = true) -> Artifact {
            Artifact(url: h(rel), kind: kind, size: size, safe: safe)
        }
        func days(_ d: Double) -> Date { Date().addingTimeInterval(-86400 * d) }
        return [
            Project(url: h("Code/photo-gallery"), displayName: "photo-gallery", tags: ["Node", "Git"], sourceSize: 48 * MB, gitSize: 120 * MB,
                    artifacts: [art("Code/photo-gallery/node_modules", .dependency, 612 * MB), art("Code/photo-gallery/.next", .build, 180 * MB)], lastModified: days(0.2)),
            Project(url: h("Code/weather-app"), displayName: "weather-app", tags: ["Swift", "Git"], sourceSize: 36 * MB, gitSize: 64 * MB,
                    artifacts: [art("Code/weather-app/.build", .build, 1 * GB + 100 * MB)], lastModified: days(74)),
            Project(url: h("Code/blog"), displayName: "blog", tags: ["Node", "Git"], sourceSize: 12 * MB, gitSize: 22 * MB,
                    artifacts: [art("Code/blog/node_modules", .dependency, 340 * MB), art("Code/blog/dist", .build, 18 * MB)], lastModified: days(130)),
            Project(url: h("Code/ml-notebooks"), displayName: "ml-notebooks", tags: ["Python", "Git"], sourceSize: 210 * MB, gitSize: 40 * MB,
                    artifacts: [art("Code/ml-notebooks/.venv", .dependency, 1 * GB + 400 * MB), art("Code/ml-notebooks/__pycache__", .cache, 6 * MB)], lastModified: days(9)),
            Project(url: h("Code/api-server"), displayName: "api-server", tags: ["Go", "Git"], sourceSize: 18 * MB, gitSize: 30 * MB,
                    artifacts: [art("Code/api-server/build", .build, 96 * MB, safe: false)], lastModified: days(2)),
            Project(url: h("Code/rust-cli"), displayName: "rust-cli", tags: ["Rust", "Git"], sourceSize: 4 * MB, gitSize: 9 * MB,
                    artifacts: [art("Code/rust-cli/target", .build, 2 * GB + 200 * MB)], lastModified: days(61)),
            Project(url: h("Code/landing-page"), displayName: "landing-page", tags: ["Node"], sourceSize: 3 * MB, gitSize: 0,
                    artifacts: [art("Code/landing-page/node_modules", .dependency, 210 * MB)], lastModified: days(200)),
        ]
    }

    // MARK: Cleanup

    static func cleanupItems() -> [CleanupCategory: [CleanupItem]] {
        func item(_ rel: String, _ title: String, _ size: Int64, _ c: CleanupCategory, _ rec: Bool, _ sub: String? = nil) -> CleanupItem {
            CleanupItem(url: h(rel), title: title, subtitle: sub ?? h(rel).tildePath, size: size, category: c, recommended: rec)
        }
        return [
            .devCache: [
                item(".npm/_cacache", L("npm 缓存", "npm cache"), 1 * GB + 100 * MB, .devCache, true),
                item("Library/Caches/Homebrew", L("Homebrew 下载缓存", "Homebrew downloads"), 860 * MB, .devCache, true),
                item("Library/Developer/Xcode/DerivedData", "Xcode DerivedData", 2 * GB + 300 * MB, .devCache, true),
                item("Library/Caches/pip", L("pip 缓存", "pip cache"), 420 * MB, .devCache, true),
            ],
            .appCache: [
                item("Library/Caches/Google", "Google Chrome", 980 * MB, .appCache, false, L("~/Library/Caches/Google · 正在运行", "~/Library/Caches/Google · running")),
                item("Library/Caches/com.spotify.client", "Spotify", 1 * GB + 600 * MB, .appCache, true),
                item("Library/Caches/com.tinyspeck.slackmacgap", "Slack", 540 * MB, .appCache, true),
            ],
            .logs: [item("Library/Logs/DiagnosticReports", "DiagnosticReports", 64 * MB, .logs, true)],
            .downloads: [
                item("Downloads/Xcode_16.xip", "Xcode_16.xip", 3 * GB + 100 * MB, .downloads, false, L("安装包/压缩包 · 120 天没动过", "Installer / archive · untouched for 120 days")),
                item("Downloads/Figma-installer.dmg", "Figma-installer.dmg", 280 * MB, .downloads, false, L("安装包/压缩包 · 45 天没动过", "Installer / archive · untouched for 45 days")),
            ],
        ]
    }

    // MARK: Processes

    static let apps: [(path: String, name: String, cpu: Double, mem: Int64, count: Int)] = [
        ("/Applications/Safari.app", "Safari", 0.18, 2 * GB + 300 * MB, 14),
        ("/System/Applications/Music.app", "Music", 0.04, 520 * MB, 3),
        ("/System/Applications/Mail.app", "Mail", 0.02, 410 * MB, 4),
        ("/System/Applications/Photos.app", "Photos", 1.62, 1 * GB + 900 * MB, 5),
        ("/System/Applications/Notes.app", "Notes", 0.01, 260 * MB, 2),
        ("/System/Applications/Utilities/Terminal.app", "Terminal", 0.06, 180 * MB, 2),
        ("/System/Applications/Calendar.app", "Calendar", 0.01, 150 * MB, 1),
    ]

    static func groups() -> [AppGroup] {
        var pid: Int32 = 900
        var out = apps.map { a -> AppGroup in
            let pids = (0..<a.count).map { _ in pid += 1; return pid }
            return AppGroup(id: a.path, name: a.name, appPath: a.path, pids: pids, cpu: a.cpu, memory: a.mem)
        }
        out.append(AppGroup(id: "pid:4242", name: "node", appPath: nil, pids: [4242], cpu: 0.92, memory: 640 * MB))
        return out
    }

    static func processes() -> [ProcInfo] {
        groups().flatMap { g in
            g.pids.enumerated().map { i, p in
                ProcInfo(pid: p, name: i == 0 ? g.name : "\(g.name) Helper", path: g.appPath ?? "/opt/homebrew/bin/node", appPath: g.appPath,
                         cpu: i == 0 ? g.cpu : 0, memory: i == 0 ? g.memory : 0)
            }
        }
    }

    static func hogs() -> [ResourceHog] {
        let g = groups()
        return [
            ResourceHog(group: g.first { $0.name == "Photos" }!, kind: .cpu, avgCPU: 1.55, idleMinutes: nil, recommended: false),
            ResourceHog(group: g.first { $0.name == "node" }!, kind: .cpu, avgCPU: 0.88, idleMinutes: nil, recommended: false),
            ResourceHog(group: g.first { $0.name == "Music" }!, kind: .idleMemory, avgCPU: 0.04, idleMinutes: 140, recommended: true),
            ResourceHog(group: g.first { $0.name == "Mail" }!, kind: .idleMemory, avgCPU: 0.02, idleMinutes: 52, recommended: false),
        ]
    }

    // MARK: History

    static func history() -> [MinutePoint] {
        let now = Date()
        return (0..<1440).map { i in
            let t = now.addingTimeInterval(Double(i - 1440) * 60)
            let x = Double(i)
            let work = (sin(x / 90) + 1) / 2
            let cpu = min(0.95, 0.08 + 0.35 * work + 0.1 * abs(sin(x / 7)) + (i % 211 < 12 ? 0.35 : 0))
            return MinutePoint(t: t, cpu: cpu, cpuMax: min(1, cpu + 0.15 + 0.1 * abs(sin(x / 3))), mem: 0.55 + 0.2 * work,
                               netIn: 40_000 + 900_000 * abs(sin(x / 33)), netOut: 12_000 + 160_000 * abs(sin(x / 41)), temp: 42 + 18 * work)
        }
    }
}
