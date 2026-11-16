import SwiftUI

struct ProcessesView: View {
    @Environment(AppState.self) private var app
    @Local private var grouped = true
    @Local private var sortByMemory = false
    @Local private var query = ""

    var body: some View {
        let m = app.monitor
        let rows = rowsToShow(m)
        let totalCPU = m.cpu.total
        VStack(alignment: .leading, spacing: 16) {
            PageHeader(title: L("进程", "Processes"), subtitle: L("\(m.processes.count) 个进程 · CPU \(Fmt.pct(totalCPU)) · 内存 \(Fmt.bytes(m.memory.used)) / \(Fmt.bytes(m.memory.total))", "\(m.processes.count) processes · CPU \(Fmt.pct(totalCPU)) · Memory \(Fmt.bytes(m.memory.used)) / \(Fmt.bytes(m.memory.total))")) {
                HStack {
                    TextField(L("搜索", "Search"), text: $query).textFieldStyle(.roundedBorder).frame(width: 180)
                    Picker("", selection: $grouped) {
                        Text(L("按应用", "By app")).tag(true)
                        Text(L("全部进程", "All processes")).tag(false)
                    }
                    .pickerStyle(.segmented).labelsHidden().frame(width: 160)
                    Picker("", selection: $sortByMemory) {
                        Text("CPU").tag(false)
                        Text(L("内存", "Memory")).tag(true)
                    }
                    .pickerStyle(.segmented).labelsHidden().frame(width: 110)
                }
            }

            HStack(spacing: 14) {
                Card("CPU", symbol: "cpu") {
                    Sparkline(values: m.samples.map(\.cpu), color: Palette.cpu).frame(height: 40)
                }
                Card(L("内存", "Memory"), symbol: "memorychip") {
                    StackedBar(segments: [
                        Segment(label: L("应用", "App"), value: Double(m.memory.app), color: Palette.mem),
                        Segment(label: L("联动", "Wired"), value: Double(m.memory.wired), color: Palette.warn),
                        Segment(label: L("压缩", "Compressed"), value: Double(m.memory.compressed), color: Palette.danger),
                    ], total: Double(m.memory.total), height: 12)
                }
            }

            VStack(spacing: 0) {
                HStack {
                    Text(L("名称", "Name")).frame(maxWidth: .infinity, alignment: .leading)
                    Text(grouped ? L("进程数", "Processes") : "PID").frame(width: 70, alignment: .trailing)
                    Text("CPU").frame(width: 80, alignment: .trailing)
                    Text(L("内存", "Memory")).frame(width: 100, alignment: .trailing)
                    Text("").frame(width: 120)
                }
                .font(.caption.weight(.semibold)).foregroundStyle(.secondary)
                .padding(.horizontal, 12).padding(.vertical, 8)
                Divider()
                ScrollView {
                    LazyVStack(spacing: 0) {
                        ForEach(rows) { g in
                            row(g)
                            Divider().opacity(0.5)
                        }
                    }
                }
            }
            .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(Color(nsColor: .controlBackgroundColor)))
            .frame(maxHeight: .infinity)

            Text(L("只显示当前用户能读取的进程。CPU 100% = 占满一个核心。退出会像 ⌘Q 一样让应用自己保存后关闭。", "Only processes readable by the current user are shown. CPU 100% = one full core. Quit works like ⌘Q, letting the app save before closing."))
                .font(.caption).foregroundStyle(.secondary)
        }
        .padding(24)
        .onAppear { app.monitor.wantProcesses(true) }
        .onDisappear { app.monitor.wantProcesses(false) }
    }

    private func rowsToShow(_ m: SystemMonitor) -> [AppGroup] {
        var list: [AppGroup] = grouped ? m.groups : m.processes.map {
            AppGroup(id: "p\($0.pid)", name: $0.name, appPath: $0.appPath, pids: [$0.pid], cpu: $0.cpu, memory: $0.memory)
        }
        if !query.isEmpty { list = list.filter { $0.name.localizedCaseInsensitiveContains(query) } }
        list.sort { sortByMemory ? $0.memory > $1.memory : ($0.cpu == $1.cpu ? $0.memory > $1.memory : $0.cpu > $1.cpu) }
        return Array(list.prefix(150))
    }

    private func row(_ g: AppGroup) -> some View {
        HStack {
            HStack(spacing: 8) {
                Image(nsImage: ProcessControl.icon(for: g)).resizable().frame(width: 18, height: 18)
                Text(g.name).lineLimit(1)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            Text(grouped ? "\(g.pids.count)" : "\(g.pids.first ?? 0)").foregroundStyle(.secondary).frame(width: 70, alignment: .trailing)
            Text(String(format: "%.1f%%", g.cpu * 100))
                .foregroundStyle(g.cpu > 0.8 ? Palette.danger : (g.cpu > 0.3 ? Palette.warn : .primary))
                .frame(width: 80, alignment: .trailing)
            Text(Fmt.bytes(g.memory)).frame(width: 100, alignment: .trailing)
            HStack(spacing: 6) {
                Button(L("退出", "Quit")) { quit(g, force: false) }
                Menu {
                    Button(L("强制退出", "Force quit")) { quit(g, force: true) }
                    if let p = g.appPath { Button(L("在 Finder 中显示", "Show in Finder")) { FileActions.reveal(URL(fileURLWithPath: p)) } }
                } label: { Image(systemName: "ellipsis") }
                    .menuStyle(.borderlessButton).menuIndicator(.hidden).frame(width: 24)
            }
            .controlSize(.small)
            .frame(width: 120, alignment: .trailing)
        }
        .font(.callout).monospacedDigit()
        .padding(.horizontal, 12).padding(.vertical, 6)
    }

    private func quit(_ g: AppGroup, force: Bool) {
        let what = g.isApp ? L("应用「\(g.name)」", "app “\(g.name)”") : L("进程「\(g.name)」", "process “\(g.name)”")
        let msg = force ? L("强制退出不会保存未保存的内容。", "Force quitting discards unsaved changes.") : L("应用会自己处理保存，和 ⌘Q 一样。", "The app handles saving itself, just like ⌘Q.")
        guard FileActions.confirm(force ? L("强制退出\(what)？", "Force quit \(what)?") : L("退出\(what)？", "Quit \(what)?"), msg, ok: force ? L("强制退出", "Force quit") : L("退出", "Quit"), destructive: force) else { return }
        if !ProcessControl.quit(g, force: force) {
            _ = FileActions.confirm(L("没能退出", "Couldn't quit"), L("可能没有权限，或者它是系统进程。", "You may not have permission, or it's a system process."), ok: L("好", "OK"), destructive: false)
        }
        DispatchQueue.main.asyncAfter(deadline: .now() + 1) { app.monitor.refreshProcesses() }
    }
}
