import SwiftUI
import Charts

struct ProjectsView: View {
    @Environment(AppState.self) private var app
    @Local private var sort = "total"
    @Local private var selection: URL?
    @Local private var picked: Set<URL> = []

    var body: some View {
        let pm = app.projects
        let list = sorted(pm.projects)
        VStack(alignment: .leading, spacing: 16) {
            PageHeader(title: L("项目", "Projects"),
                       subtitle: pm.projects.isEmpty ? L("自动找出开发项目，看源码、依赖和构建产物各占多少", "Finds your development projects and shows how much space source, dependencies and build output take")
                                                     : L("\(pm.projects.count) 个项目，共 \(Fmt.bytes(pm.projects.reduce(0) { $0 + $1.totalSize }))，其中依赖和构建产物 \(Fmt.bytes(pm.totalArtifacts))", "\(pm.projects.count) projects, \(Fmt.bytes(pm.projects.reduce(0) { $0 + $1.totalSize })) in total, \(Fmt.bytes(pm.totalArtifacts)) of it dependencies and build output")) {
                HStack {
                    if pm.scanning {
                        ProgressView().controlSize(.small)
                        Text(pm.status).foregroundStyle(.secondary).font(.callout)
                    }
                    Picker(L("排序", "Sort"), selection: $sort) {
                        Text(L("按总大小", "By total size")).tag("total")
                        Text(L("按可清理", "By cleanable")).tag("artifacts")
                        Text(L("按最近修改", "By recently modified")).tag("recent")
                        Text(L("按最久没动", "By least recently touched")).tag("stale")
                    }
                    .frame(width: 150)
                    Button { pm.scan() } label: { Label(pm.projects.isEmpty ? L("扫描项目", "Scan projects") : L("重新扫描", "Rescan"), systemImage: "arrow.clockwise") }
                        .buttonStyle(.borderedProminent).disabled(pm.scanning)
                }
            }

            if pm.projects.isEmpty {
                Card(L("项目目录", "Project folders"), symbol: "folder") {
                    EmptyHint(symbol: "folder.badge.gearshape", title: pm.scanning ? L("正在扫描…", "Scanning…") : L("还没扫描", "Not scanned yet"),
                              detail: L("会在这些目录里找项目：\(app.settings.projectRootURLs.map(\.tildePath).joined(separator: "、"))\n可以在设置里修改。", "Looks for projects in: \(app.settings.projectRootURLs.map(\.tildePath).joined(separator: ", "))\nYou can change this in Settings."))
                }
                Spacer()
            } else {
                summaryChart(list)
                HStack(alignment: .top, spacing: 14) {
                    projectList(list)
                    detail.frame(width: 380)
                }
                .frame(maxHeight: .infinity)
            }
        }
        .padding(24)
        .onAppear { if pm.projects.isEmpty && !pm.scanning { pm.scan() } }
    }

    private func sorted(_ p: [Project]) -> [Project] {
        switch sort {
        case "artifacts": return p.sorted { $0.artifactSize > $1.artifactSize }
        case "recent": return p.sorted { ($0.lastModified ?? .distantPast) > ($1.lastModified ?? .distantPast) }
        case "stale": return p.sorted { ($0.lastModified ?? .distantPast) < ($1.lastModified ?? .distantPast) }
        default: return p.sorted { $0.totalSize > $1.totalSize }
        }
    }

    private func summaryChart(_ list: [Project]) -> some View {
        let top = Array(list.sorted { $0.totalSize > $1.totalSize }.prefix(8))
        return Card(L("空间构成（前 \(top.count) 个）", "Space breakdown (top \(top.count))"), symbol: "chart.bar.xaxis") {
            Chart {
                ForEach(top) { p in
                    BarMark(x: .value(L("大小", "Size"), Double(p.sourceSize) / 1_048_576), y: .value(L("项目", "Project"), p.displayName))
                        .foregroundStyle(by: .value(L("类型", "Type"), L("源码", "Source")))
                    BarMark(x: .value(L("大小", "Size"), Double(p.gitSize) / 1_048_576), y: .value(L("项目", "Project"), p.displayName))
                        .foregroundStyle(by: .value(L("类型", "Type"), L("Git 历史", "Git history")))
                    BarMark(x: .value(L("大小", "Size"), Double(p.artifactSize) / 1_048_576), y: .value(L("项目", "Project"), p.displayName))
                        .foregroundStyle(by: .value(L("类型", "Type"), L("依赖/构建产物", "Dependencies/build output")))
                }
            }
            .chartForegroundStyleScale([L("源码", "Source"): Palette.cpu, L("Git 历史", "Git history"): Palette.mem, L("依赖/构建产物", "Dependencies/build output"): Palette.netOut])
            .chartXAxis { AxisMarks { v in AxisGridLine(); AxisValueLabel { Text(Fmt.bytes((v.as(Double.self) ?? 0) * 1_048_576)) } } }
            .chartLegend(position: .top, alignment: .leading)
            .frame(height: CGFloat(max(top.count, 1)) * 24 + 40)
        }
    }

    private func projectList(_ list: [Project]) -> some View {
        let maxSize = Double(list.map(\.totalSize).max() ?? 1)
        return ScrollView {
            LazyVStack(spacing: 4) {
                ForEach(list) { p in
                    let stale = Fmt.daysAgo(p.lastModified) >= app.settings.staleProjectDays
                    Button { selection = p.url; picked = Set(p.artifacts.filter(\.safe).map(\.url)) } label: {
                        VStack(alignment: .leading, spacing: 6) {
                            HStack(spacing: 8) {
                                Image(systemName: "folder.fill").foregroundStyle(Palette.cpu)
                                Text(p.displayName).fontWeight(.medium).lineLimit(1)
                                ForEach(p.tags.prefix(3), id: \.self) { t in
                                    Text(t).font(.caption2).padding(.horizontal, 5).padding(.vertical, 1)
                                        .background(Capsule().fill(Color.primary.opacity(0.07)))
                                }
                                if stale {
                                    Text(L("\(Fmt.daysAgo(p.lastModified)) 天没动", "Untouched \(Fmt.daysAgo(p.lastModified)) days")).font(.caption2).foregroundStyle(Palette.warn)
                                }
                                Spacer()
                                Text(Fmt.bytes(p.totalSize)).monospacedDigit().fontWeight(.medium)
                            }
                            GeometryReader { g in
                                let w = g.size.width * CGFloat(Double(p.totalSize) / maxSize)
                                HStack(spacing: 1) {
                                    Rectangle().fill(Palette.cpu).frame(width: w * CGFloat(frac(p.sourceSize, p)))
                                    Rectangle().fill(Palette.mem).frame(width: w * CGFloat(frac(p.gitSize, p)))
                                    Rectangle().fill(Palette.netOut).frame(width: w * CGFloat(frac(p.artifactSize, p)))
                                    Spacer(minLength: 0)
                                }
                                .clipShape(Capsule())
                            }
                            .frame(height: 6)
                            HStack {
                                Text(L("源码 \(Fmt.bytes(p.sourceSize))", "Source \(Fmt.bytes(p.sourceSize))"))
                                if p.gitSize > 0 { Text("· Git \(Fmt.bytes(p.gitSize))") }
                                if p.artifactSize > 0 { Text(L("· 可清理 \(Fmt.bytes(p.artifactSize))", "· \(Fmt.bytes(p.artifactSize)) cleanable")).foregroundStyle(Palette.netOut) }
                                Spacer()
                                Text(L("\(Fmt.relative(p.lastModified))修改", "Modified \(Fmt.relative(p.lastModified))"))
                            }
                            .font(.caption).foregroundStyle(.secondary)
                        }
                        .padding(10)
                        .background(RoundedRectangle(cornerRadius: 10).fill(selection == p.url ? Palette.theme.opacity(0.12) : Color(nsColor: .controlBackgroundColor)))
                        .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                    .contextMenu {
                        Button(L("在 Finder 中显示", "Show in Finder")) { FileActions.reveal(p.url) }
                        Button(L("在终端中打开", "Open in Terminal")) { FileActions.openInTerminal(p.url) }
                    }
                }
            }
        }
    }

    private func frac(_ v: Int64, _ p: Project) -> Double { p.totalSize > 0 ? Double(v) / Double(p.totalSize) : 0 }

    @ViewBuilder
    private var detail: some View {
        if let url = selection, let p = app.projects.projects.first(where: { $0.url == url }) {
            Card(p.displayName, symbol: "folder") {
                VStack(alignment: .leading, spacing: 10) {
                    Text(p.url.tildePath).font(.caption).foregroundStyle(.secondary).textSelection(.enabled)
                    StackedBar(segments: [
                        Segment(label: L("源码", "Source"), value: Double(p.sourceSize), color: Palette.cpu),
                        Segment(label: "Git", value: Double(p.gitSize), color: Palette.mem),
                        Segment(label: L("可清理", "Cleanable"), value: Double(p.artifactSize), color: Palette.netOut),
                    ], height: 12)
                    HStack {
                        Button(L("在 Finder 中显示", "Show in Finder")) { FileActions.reveal(p.url) }
                        Button(L("在终端中打开", "Open in Terminal")) { FileActions.openInTerminal(p.url) }
                    }
                    .controlSize(.small)
                    Divider()
                    if p.artifacts.isEmpty {
                        Text(L("没有可清理的依赖或构建产物。", "No dependencies or build output to clean.")).foregroundStyle(.secondary)
                    } else {
                        Text(L("依赖与构建产物", "Dependencies & build output")).font(.subheadline.weight(.semibold))
                        ScrollView {
                            VStack(spacing: 6) {
                                ForEach(p.artifacts) { a in
                                    HStack(alignment: .top) {
                                        Toggle("", isOn: Binding(get: { picked.contains(a.url) },
                                                                 set: { if $0 { picked.insert(a.url) } else { picked.remove(a.url) } }))
                                            .toggleStyle(.checkbox).labelsHidden()
                                        VStack(alignment: .leading, spacing: 2) {
                                            Text(a.url.path.replacingOccurrences(of: p.url.path + "/", with: "")).lineLimit(1).truncationMode(.middle)
                                            Text(a.safe ? "\(a.kind.title) · \(a.kind.hint)" : L("\(a.kind.title) · 不在 .gitignore 里，可能是源码，请确认", "\(a.kind.title) · not in .gitignore, may be source code, please check"))
                                                .font(.caption).foregroundStyle(a.safe ? Color.secondary : Palette.warn)
                                        }
                                        Spacer()
                                        Text(Fmt.bytes(a.size)).monospacedDigit()
                                    }
                                    .contextMenu { Button(L("在 Finder 中显示", "Show in Finder")) { FileActions.reveal(a.url) } }
                                }
                            }
                        }
                        .frame(maxHeight: 260)
                        let size = p.artifacts.filter { picked.contains($0.url) }.reduce(Int64(0)) { $0 + $1.size }
                        HStack {
                            Text(L("已选 \(Fmt.bytes(size))", "\(Fmt.bytes(size)) selected")).monospacedDigit()
                            Spacer()
                            Button(L("移到废纸篓…", "Move to Trash…")) { clean(p, permanent: false) }
                                .disabled(picked.isEmpty || app.cleanup.working)
                            Button(L("直接删除…", "Delete immediately…")) { clean(p, permanent: true) }
                                .disabled(picked.isEmpty || app.cleanup.working)
                        }
                    }
                }
            }
        } else {
            Card(L("详情", "Details"), symbol: "info.circle") {
                EmptyHint(symbol: "hand.point.left", title: L("选一个项目", "Select a project"), detail: L("左边点一个项目，查看它的依赖和构建产物，并可以单独清理。", "Click a project on the left to see its dependencies and build output and clean them individually."))
            }
        }
    }

    private func clean(_ p: Project, permanent: Bool) {
        let items = p.artifacts.filter { picked.contains($0.url) }.map {
            CleanupItem(url: $0.url, title: $0.url.lastPathComponent, subtitle: "", size: $0.size, category: .projectArtifacts, recommended: false)
        }
        let size = items.reduce(Int64(0)) { $0 + $1.size }
        let msg = items.map { "• " + $0.url.tildePath }.prefix(8).joined(separator: "\n")
            + (items.count > 8 ? L("\n…等 \(items.count) 项", "\n…\(items.count) items in total") : "")
            + (permanent ? L("\n\n直接删除不能恢复。这些目录可以通过重新安装依赖或重新构建生成。", "\n\nDeleted items can't be restored. These folders can be regenerated by reinstalling dependencies or rebuilding.") : L("\n\n可以在废纸篓里恢复；清空废纸篓后才会释放空间。", "\n\nYou can restore them from the Trash; space is freed once you empty the Trash."))
        guard FileActions.confirm(permanent ? L("直接删除 \(Fmt.bytes(size))？", "Delete \(Fmt.bytes(size)) immediately?") : L("把 \(Fmt.bytes(size)) 移到废纸篓？", "Move \(Fmt.bytes(size)) to the Trash?"), msg,
                                  ok: permanent ? L("删除", "Delete") : L("移到废纸篓", "Move to Trash")) else { return }
        app.cleanup.perform(items, permanent: permanent) { o in
            let done = Set(items.map(\.url)).subtracting(o.failures.map(\.0)).subtracting(o.skipped)
            app.projects.removed(done)
            picked.subtract(done)
            if !o.failures.isEmpty {
                _ = FileActions.confirm(L("有 \(o.failures.count) 项没清理成功", "\(o.failures.count) items couldn't be cleaned"), o.failures.map { L("\($0.0.tildePath)：\($0.1)", "\($0.0.tildePath): \($0.1)") }.joined(separator: "\n"), ok: L("好", "OK"), destructive: false)
            }
        }
    }
}
