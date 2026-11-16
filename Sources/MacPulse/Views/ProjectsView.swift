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
            PageHeader(title: "项目",
                       subtitle: pm.projects.isEmpty ? "自动找出开发项目，看源码、依赖和构建产物各占多少"
                                                     : "\(pm.projects.count) 个项目，共 \(Fmt.bytes(pm.projects.reduce(0) { $0 + $1.totalSize }))，其中依赖和构建产物 \(Fmt.bytes(pm.totalArtifacts))") {
                HStack {
                    if pm.scanning {
                        ProgressView().controlSize(.small)
                        Text(pm.status).foregroundStyle(.secondary).font(.callout)
                    }
                    Picker("排序", selection: $sort) {
                        Text("按总大小").tag("total")
                        Text("按可清理").tag("artifacts")
                        Text("按最近修改").tag("recent")
                        Text("按最久没动").tag("stale")
                    }
                    .frame(width: 150)
                    Button { pm.scan() } label: { Label(pm.projects.isEmpty ? "扫描项目" : "重新扫描", systemImage: "arrow.clockwise") }
                        .buttonStyle(.borderedProminent).disabled(pm.scanning)
                }
            }

            if pm.projects.isEmpty {
                Card("项目目录", symbol: "folder") {
                    EmptyHint(symbol: "folder.badge.gearshape", title: pm.scanning ? "正在扫描…" : "还没扫描",
                              detail: "会在这些目录里找项目：\(app.settings.projectRootURLs.map(\.tildePath).joined(separator: "、"))\n可以在设置里修改。")
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
        return Card("空间构成（前 \(top.count) 个）", symbol: "chart.bar.xaxis") {
            Chart {
                ForEach(top) { p in
                    BarMark(x: .value("大小", Double(p.sourceSize) / 1_048_576), y: .value("项目", p.displayName))
                        .foregroundStyle(by: .value("类型", "源码"))
                    BarMark(x: .value("大小", Double(p.gitSize) / 1_048_576), y: .value("项目", p.displayName))
                        .foregroundStyle(by: .value("类型", "Git 历史"))
                    BarMark(x: .value("大小", Double(p.artifactSize) / 1_048_576), y: .value("项目", p.displayName))
                        .foregroundStyle(by: .value("类型", "依赖/构建产物"))
                }
            }
            .chartForegroundStyleScale(["源码": Palette.cpu, "Git 历史": Palette.mem, "依赖/构建产物": Palette.netOut])
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
                                    Text("\(Fmt.daysAgo(p.lastModified)) 天没动").font(.caption2).foregroundStyle(Palette.warn)
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
                                Text("源码 \(Fmt.bytes(p.sourceSize))")
                                if p.gitSize > 0 { Text("· Git \(Fmt.bytes(p.gitSize))") }
                                if p.artifactSize > 0 { Text("· 可清理 \(Fmt.bytes(p.artifactSize))").foregroundStyle(Palette.netOut) }
                                Spacer()
                                Text("\(Fmt.relative(p.lastModified))修改")
                            }
                            .font(.caption).foregroundStyle(.secondary)
                        }
                        .padding(10)
                        .background(RoundedRectangle(cornerRadius: 10).fill(selection == p.url ? Palette.theme.opacity(0.12) : Color(nsColor: .controlBackgroundColor)))
                        .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                    .contextMenu {
                        Button("在 Finder 中显示") { FileActions.reveal(p.url) }
                        Button("在终端中打开") { FileActions.openInTerminal(p.url) }
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
                        Segment(label: "源码", value: Double(p.sourceSize), color: Palette.cpu),
                        Segment(label: "Git", value: Double(p.gitSize), color: Palette.mem),
                        Segment(label: "可清理", value: Double(p.artifactSize), color: Palette.netOut),
                    ], height: 12)
                    HStack {
                        Button("在 Finder 中显示") { FileActions.reveal(p.url) }
                        Button("在终端中打开") { FileActions.openInTerminal(p.url) }
                    }
                    .controlSize(.small)
                    Divider()
                    if p.artifacts.isEmpty {
                        Text("没有可清理的依赖或构建产物。").foregroundStyle(.secondary)
                    } else {
                        Text("依赖与构建产物").font(.subheadline.weight(.semibold))
                        ScrollView {
                            VStack(spacing: 6) {
                                ForEach(p.artifacts) { a in
                                    HStack(alignment: .top) {
                                        Toggle("", isOn: Binding(get: { picked.contains(a.url) },
                                                                 set: { if $0 { picked.insert(a.url) } else { picked.remove(a.url) } }))
                                            .toggleStyle(.checkbox).labelsHidden()
                                        VStack(alignment: .leading, spacing: 2) {
                                            Text(a.url.path.replacingOccurrences(of: p.url.path + "/", with: "")).lineLimit(1).truncationMode(.middle)
                                            Text(a.safe ? "\(a.kind.rawValue) · \(a.kind.hint)" : "\(a.kind.rawValue) · 不在 .gitignore 里，可能是源码，请确认")
                                                .font(.caption).foregroundStyle(a.safe ? Color.secondary : Palette.warn)
                                        }
                                        Spacer()
                                        Text(Fmt.bytes(a.size)).monospacedDigit()
                                    }
                                    .contextMenu { Button("在 Finder 中显示") { FileActions.reveal(a.url) } }
                                }
                            }
                        }
                        .frame(maxHeight: 260)
                        let size = p.artifacts.filter { picked.contains($0.url) }.reduce(Int64(0)) { $0 + $1.size }
                        HStack {
                            Text("已选 \(Fmt.bytes(size))").monospacedDigit()
                            Spacer()
                            Button("移到废纸篓…") { clean(p, permanent: false) }
                                .disabled(picked.isEmpty || app.cleanup.working)
                            Button("直接删除…") { clean(p, permanent: true) }
                                .disabled(picked.isEmpty || app.cleanup.working)
                        }
                    }
                }
            }
        } else {
            Card("详情", symbol: "info.circle") {
                EmptyHint(symbol: "hand.point.left", title: "选一个项目", detail: "左边点一个项目，查看它的依赖和构建产物，并可以单独清理。")
            }
        }
    }

    private func clean(_ p: Project, permanent: Bool) {
        let items = p.artifacts.filter { picked.contains($0.url) }.map {
            CleanupItem(url: $0.url, title: $0.url.lastPathComponent, subtitle: "", size: $0.size, category: .projectArtifacts, recommended: false)
        }
        let size = items.reduce(Int64(0)) { $0 + $1.size }
        let msg = items.map { "• " + $0.url.tildePath }.prefix(8).joined(separator: "\n")
            + (items.count > 8 ? "\n…等 \(items.count) 项" : "")
            + (permanent ? "\n\n直接删除不能恢复。这些目录可以通过重新安装依赖或重新构建生成。" : "\n\n可以在废纸篓里恢复；清空废纸篓后才会释放空间。")
        guard FileActions.confirm(permanent ? "直接删除 \(Fmt.bytes(size))？" : "把 \(Fmt.bytes(size)) 移到废纸篓？", msg,
                                  ok: permanent ? "删除" : "移到废纸篓") else { return }
        app.cleanup.perform(items, permanent: permanent) { o in
            let done = Set(items.map(\.url)).subtracting(o.failures.map(\.0)).subtracting(o.skipped)
            app.projects.removed(done)
            picked.subtract(done)
            if !o.failures.isEmpty {
                _ = FileActions.confirm("有 \(o.failures.count) 项没清理成功", o.failures.map { "\($0.0.tildePath)：\($0.1)" }.joined(separator: "\n"), ok: "好", destructive: false)
            }
        }
    }
}
