import SwiftUI

struct CleanupView: View {
    @Environment(AppState.self) private var app
    @Local private var permanent = false
    @Local private var pickedHogs: Set<String> = []
    @Local private var hogsInitialised = false
    @Local private var hogResult: String?
    @Local private var collapsed: Set<CleanupCategory> = [.appCache, .logs]

    var body: some View {
        let c = app.cleanup
        VStack(alignment: .leading, spacing: 16) {
            PageHeader(title: L("清理", "Clean up"), subtitle: c.lastScan == nil ? L("找出可以安全删掉的缓存、构建产物和旧文件", "Find caches, build output and old files that are safe to delete")
                                                                 : L("找到 \(Fmt.bytes(c.allItems.reduce(0) { $0 + $1.size }))，推荐清理 \(Fmt.bytes(c.recommendedSize))", "Found \(Fmt.bytes(c.allItems.reduce(0) { $0 + $1.size })), \(Fmt.bytes(c.recommendedSize)) recommended")) {
                HStack {
                    if c.scanning || app.projects.scanning { ProgressView().controlSize(.small) }
                    Button { rescan() } label: { Label(c.lastScan == nil ? L("开始扫描", "Start scan") : L("重新扫描", "Rescan"), systemImage: "arrow.clockwise") }
                        .disabled(c.scanning)
                }
            }

            HStack(alignment: .top, spacing: 14) {
                actionCard(c).frame(maxWidth: .infinity)
                trashCard(c).frame(width: 230)
                historyCard(c).frame(width: 200)
            }

            hogsCard()

            if let r = c.lastResult {
                Label(r, systemImage: "checkmark.circle.fill").foregroundStyle(Palette.disk).font(.callout)
            }

            if c.lastScan == nil && !c.scanning {
                Card(L("准备好了", "Ready"), symbol: "sparkles") {
                    EmptyHint(symbol: "sparkles", title: L("点「开始扫描」", "Click “Start scan”"),
                              detail: L("会检查开发工具缓存、项目依赖和构建产物、应用缓存、日志、下载文件夹和大文件。\n扫描只读取，不会删除任何东西。", "Checks developer caches, project dependencies and build output, app caches, logs, Downloads and large files.\nScanning only reads, it never deletes anything."))
                }
                Spacer()
            } else {
                ScrollView {
                    VStack(spacing: 12) {
                        ForEach(CleanupCategory.allCases) { cat in categorySection(cat, c) }
                    }
                }
            }
        }
        .padding(24)
        .onAppear {
            if c.lastScan == nil && !c.scanning { rescan() }
            app.monitor.wantProcesses(true)
        }
        .onDisappear { app.monitor.wantProcesses(false) }
        .onChange(of: app.projects.lastScan) { _, _ in resync() }
        .onChange(of: app.disk.lastScan) { _, _ in resync() }
    }

    private func rescan() { app.cleanup.scan(projects: app.projects, disk: app.disk) }

    private func resync() {
        let before = Set(app.cleanup.allItems.map(\.url))
        app.cleanup.syncFromOthers(projects: app.projects, disk: app.disk)
        // Preselect newly found recommended items only.
        for i in app.cleanup.allItems where i.recommended && !before.contains(i.url) { app.cleanup.selected.insert(i.url) }
    }

    private func actionCard(_ c: CleanupModel) -> some View {
        Card(L("已选择", "Selected"), symbol: "checklist") {
            HStack(alignment: .firstTextBaseline, spacing: 8) {
                Text(Fmt.bytes(c.selectedSize)).font(.system(size: 34, weight: .bold, design: .rounded)).monospacedDigit()
                Text(L("\(c.selectedItems.count) 项", "\(c.selectedItems.count) items")).foregroundStyle(.secondary)
                Spacer()
                Button { run(c) } label: {
                    if c.working { ProgressView().controlSize(.small) } else { Label(L("清理所选", "Clean selected"), systemImage: "trash").fixedSize() }
                }
                .buttonStyle(.borderedProminent).controlSize(.large)
                .tint(permanent ? Palette.danger : Palette.theme)
                .disabled(c.selected.isEmpty || c.working)
            }
            HStack {
                Picker("", selection: $permanent) {
                    Text(L("移到废纸篓", "Move to Trash")).tag(false)
                    Text(L("直接删除", "Delete immediately")).tag(true)
                }
                .pickerStyle(.segmented).labelsHidden().fixedSize()
                Spacer(minLength: 8)
                Button(L("全选推荐", "Select recommended")) { c.selected = Set(c.allItems.filter(\.recommended).map(\.url)) }.fixedSize()
                Button(L("全不选", "Select none")) { c.selected = [] }.fixedSize()
            }
            Text(permanent ? L("直接删除不能恢复，空间马上释放。", "Deleted items can't be restored. Space is freed right away.") : L("移到废纸篓可以恢复，但要清空废纸篓后空间才真正释放。", "Items in the Trash can be restored, but space is only freed once you empty the Trash."))
                .font(.caption).foregroundStyle(permanent ? Palette.danger : .secondary)
        }
    }

    private func trashCard(_ c: CleanupModel) -> some View {
        Card(L("废纸篓", "Trash"), symbol: "trash") {
            if c.trashReadable {
                Text(Fmt.bytes(c.trashSize)).font(.system(size: 26, weight: .semibold, design: .rounded)).monospacedDigit()
                Button(L("清空废纸篓…", "Empty Trash…")) {
                    if FileActions.confirm(L("清空废纸篓？", "Empty the Trash?"), L("将永久删除废纸篓里的 \(Fmt.bytes(c.trashSize))，不能恢复。", "This permanently deletes \(Fmt.bytes(c.trashSize)) in the Trash. It can't be undone."), ok: L("清空", "Empty")) {
                        c.emptyTrash { _ in }
                    }
                }
                .disabled(c.trashSize == 0 || c.working)
            } else {
                Text(L("需要「完全磁盘访问」权限才能读取废纸篓。", "Full Disk Access is required to read the Trash.")).font(.callout).foregroundStyle(.secondary)
                Button(L("打开系统设置", "Open System Settings")) { FileActions.openFullDiskAccessSettings() }
            }
        }
    }

    private func historyCard(_ c: CleanupModel) -> some View {
        Card(L("累计已清理", "Total cleaned"), symbol: "chart.line.uptrend.xyaxis") {
            Text(Fmt.bytes(c.totalFreed)).font(.system(size: 26, weight: .semibold, design: .rounded)).monospacedDigit()
            if let last = c.records.last {
                Text(L("上次 \(Fmt.dateTime(last.date)) · \(Fmt.bytes(last.bytes))", "Last \(Fmt.dateTime(last.date)) · \(Fmt.bytes(last.bytes))")).font(.caption).foregroundStyle(.secondary)
            } else {
                Text(L("还没清理过", "Nothing cleaned yet")).font(.caption).foregroundStyle(.secondary)
            }
        }
    }

    @ViewBuilder
    private func categorySection(_ cat: CleanupCategory, _ c: CleanupModel) -> some View {
        let items = c.items[cat] ?? []
        let selectedInCat = items.filter { c.selected.contains($0.url) }
        let open = !collapsed.contains(cat)
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 10) {
                Toggle("", isOn: Binding(
                    get: { !items.isEmpty && selectedInCat.count == items.count },
                    set: { on in for i in items { if on { c.selected.insert(i.url) } else { c.selected.remove(i.url) } } }))
                    .toggleStyle(.checkbox).labelsHidden().disabled(items.isEmpty)
                Image(systemName: cat.symbol).foregroundStyle(Palette.cpu).frame(width: 18)
                VStack(alignment: .leading, spacing: 2) {
                    Text(cat.title).font(.headline)
                    Text(cat.note).font(.caption).foregroundStyle(.secondary)
                }
                Spacer()
                VStack(alignment: .trailing, spacing: 2) {
                    Text(Fmt.bytes(c.total(cat))).font(.headline).monospacedDigit()
                    Text(items.isEmpty ? emptyText(cat) : L("已选 \(Fmt.bytes(selectedInCat.reduce(0) { $0 + $1.size })) · \(items.count) 项", "\(Fmt.bytes(selectedInCat.reduce(0) { $0 + $1.size })) selected · \(items.count) items"))
                        .font(.caption).foregroundStyle(.secondary)
                }
                Button { if open { collapsed.insert(cat) } else { collapsed.remove(cat) } } label: {
                    Image(systemName: "chevron.down").rotationEffect(.degrees(open ? 0 : -90))
                }
                .buttonStyle(.borderless).disabled(items.isEmpty)
            }
            .padding(14)

            if open && !items.isEmpty {
                Divider()
                VStack(spacing: 0) {
                    ForEach(items.prefix(60)) { item in
                        HStack(spacing: 10) {
                            Toggle("", isOn: Binding(get: { c.selected.contains(item.url) },
                                                     set: { if $0 { c.selected.insert(item.url) } else { c.selected.remove(item.url) } }))
                                .toggleStyle(.checkbox).labelsHidden()
                            VStack(alignment: .leading, spacing: 1) {
                                HStack(spacing: 6) {
                                    Text(item.title).lineLimit(1).truncationMode(.middle)
                                    if item.recommended {
                                        Text(L("推荐", "Recommended")).font(.caption2).foregroundStyle(Palette.disk)
                                            .padding(.horizontal, 5).padding(.vertical, 1)
                                            .background(Capsule().fill(Palette.disk.opacity(0.12)))
                                    }
                                    if app.settings.isWhitelisted(item.url) {
                                        Text(L("白名单", "Whitelisted")).font(.caption2).foregroundStyle(.secondary)
                                    }
                                }
                                Text(item.subtitle).font(.caption).foregroundStyle(.secondary).lineLimit(1).truncationMode(.middle)
                            }
                            Spacer()
                            Text(Fmt.bytes(item.size)).monospacedDigit()
                            Button { FileActions.reveal(item.url) } label: { Image(systemName: "magnifyingglass") }
                                .buttonStyle(.borderless).help(L("在 Finder 中显示", "Show in Finder"))
                        }
                        .padding(.horizontal, 14).padding(.vertical, 6)
                        .contextMenu {
                            Button(L("在 Finder 中显示", "Show in Finder")) { FileActions.reveal(item.url) }
                            Button(L("加入白名单（永不清理）", "Add to whitelist (never clean)")) {
                                app.settings.whitelist.append(item.url.tildePath)
                                c.selected.remove(item.url)
                            }
                        }
                    }
                    if items.count > 60 {
                        Text(L("还有 \(items.count - 60) 项未显示", "\(items.count - 60) more items not shown")).font(.caption).foregroundStyle(.secondary).padding(8)
                    }
                }
                .padding(.vertical, 4)
            }
        }
        .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(Color(nsColor: .controlBackgroundColor)))
    }

    private func emptyText(_ cat: CleanupCategory) -> String {
        switch cat {
        case .largeFiles: return app.disk.root == nil ? L("先在「磁盘」页扫描", "Scan on the Disk page first") : L("没有超过 \(app.settings.largeFileMB) MB 的文件", "No files over \(app.settings.largeFileMB) MB")
        case .projectArtifacts: return app.projects.scanning ? L("正在扫描项目…", "Scanning projects…") : L("没有找到", "Nothing found")
        default: return app.cleanup.scanning ? L("扫描中…", "Scanning…") : L("没有找到", "Nothing found")
        }
    }

    private func run(_ c: CleanupModel) {
        let list = c.selectedItems
        let size = list.reduce(Int64(0)) { $0 + $1.size }
        let preview = list.prefix(10).map { "• \($0.title)  \(Fmt.bytes($0.size))" }.joined(separator: "\n")
            + (list.count > 10 ? L("\n…等 \(list.count) 项", "\n…\(list.count) items in total") : "")
        let tail = permanent ? L("\n\n直接删除不能恢复。", "\n\nDeleted items can't be restored.") : L("\n\n会移到废纸篓，可以恢复。", "\n\nItems will be moved to the Trash and can be restored.")
        guard FileActions.confirm(permanent ? L("直接删除 \(Fmt.bytes(size))？", "Delete \(Fmt.bytes(size)) immediately?") : L("把 \(Fmt.bytes(size)) 移到废纸篓？", "Move \(Fmt.bytes(size)) to the Trash?"),
                                  preview + tail, ok: permanent ? L("删除", "Delete") : L("移到废纸篓", "Move to Trash"), destructive: true) else { return }
        c.perform(list, permanent: permanent) { o in
            let done = Set(list.map(\.url)).subtracting(o.failures.map(\.0)).subtracting(o.skipped)
            app.projects.removed(done)
            for u in done { app.disk.removedFile(u) }
            if !o.failures.isEmpty || !o.skipped.isEmpty {
                var lines = o.failures.prefix(8).map { L("\($0.0.tildePath)：\($0.1)", "\($0.0.tildePath): \($0.1)") }
                if !o.skipped.isEmpty { lines.append(L("跳过 \(o.skipped.count) 项（白名单或受保护位置）", "Skipped \(o.skipped.count) items (whitelisted or protected)")) }
                _ = FileActions.confirm(L("部分项目没有清理", "Some items weren't cleaned"), lines.joined(separator: "\n"), ok: L("好", "OK"), destructive: false)
            }
        }
    }

    // MARK: Free up memory and CPU

    @ViewBuilder
    private func hogsCard() -> some View {
        let hogs = ResourceHogs.find()
        let summary = ResourceHogs.memorySummary()
        let picked = hogs.filter { pickedHogs.contains($0.id) }
        let color: Color = summary.level >= 4 ? Palette.danger : (summary.level >= 2 ? Palette.warn : Palette.disk)
        Card(L("释放内存和 CPU", "Free up memory and CPU"), symbol: "memorychip", trailing: {
            Button {
                let list = picked
                let names = list.prefix(8).map { "• \($0.group.name)" }.joined(separator: "\n")
                guard FileActions.confirm(L("退出 \(list.count) 个应用？", "Quit \(list.count) apps?"),
                                          names + L("\n\n会像 ⌘Q 一样让它们自己保存后退出，有未保存内容的应用会先询问你。",
                                                    "\n\nThey quit like ⌘Q, so apps with unsaved work will ask you first."),
                                          ok: L("退出", "Quit")) else { return }
                let r = ResourceHogs.quit(list)
                pickedHogs.subtract(list.map(\.id))
                hogResult = L("已请求退出 \(r.count) 个应用，约可释放 \(Fmt.bytes(r.memory)) 内存",
                              "Asked \(r.count) apps to quit, freeing about \(Fmt.bytes(r.memory))")
                DispatchQueue.main.asyncAfter(deadline: .now() + 2) { app.monitor.refreshProcesses() }
            } label: {
                Label(L("退出所选", "Quit selected"), systemImage: "xmark.circle")
            }
            .buttonStyle(.borderedProminent)
            .disabled(picked.isEmpty)
        }) {
            HStack(alignment: .top, spacing: 10) {
                Circle().fill(color).frame(width: 10, height: 10).padding(.top, 4)
                VStack(alignment: .leading, spacing: 2) {
                    Text(summary.title).font(.headline)
                    Text(summary.detail).font(.callout).foregroundStyle(.secondary)
                }
                Spacer()
                VStack(alignment: .trailing, spacing: 2) {
                    Text(Fmt.pct(app.monitor.memory.usedFraction)).font(.system(.title3, design: .rounded).weight(.semibold)).monospacedDigit()
                    Text(L("内存 · CPU \(Fmt.pct(app.monitor.cpu.total))", "memory · CPU \(Fmt.pct(app.monitor.cpu.total))")).font(.caption).foregroundStyle(.secondary)
                }
            }
            if let r = hogResult {
                Label(r, systemImage: "checkmark.circle.fill").foregroundStyle(Palette.disk).font(.callout)
            }
            Divider()
            if hogs.isEmpty {
                Text(app.monitor.processSamples < 3
                     ? L("正在观察各个应用的占用情况，几秒后显示…", "Watching app usage, results in a few seconds…")
                     : L("没有发现持续占用 CPU 或长时间闲置的大内存应用，一切正常。", "No busy or long-idle memory-heavy apps found. All good."))
                    .foregroundStyle(.secondary).font(.callout)
            }
            ForEach(hogs) { h in
                HStack(spacing: 10) {
                    Toggle("", isOn: Binding(get: { pickedHogs.contains(h.id) },
                                             set: { if $0 { pickedHogs.insert(h.id) } else { pickedHogs.remove(h.id) } }))
                        .toggleStyle(.checkbox).labelsHidden()
                    Image(nsImage: ProcessControl.icon(for: h.group)).resizable().frame(width: 20, height: 20)
                    VStack(alignment: .leading, spacing: 1) {
                        HStack(spacing: 6) {
                            Text(h.group.name)
                            Text(h.kind == .cpu ? L("CPU", "CPU") : L("闲置", "Idle"))
                                .font(.caption2).padding(.horizontal, 5).padding(.vertical, 1)
                                .foregroundStyle(h.kind == .cpu ? Palette.danger : Palette.warn)
                                .background(Capsule().fill((h.kind == .cpu ? Palette.danger : Palette.warn).opacity(0.12)))
                        }
                        Text(h.reason).font(.caption).foregroundStyle(.secondary)
                    }
                    Spacer()
                    Text(Fmt.bytes(h.group.memory)).monospacedDigit()
                    Text(String(format: "%.0f%%", h.group.cpu * 100)).monospacedDigit().foregroundStyle(.secondary).frame(width: 48, alignment: .trailing)
                }
                .font(.callout)
            }
            .onAppear {
                if !hogsInitialised { pickedHogs = Set(hogs.filter(\.recommended).map(\.id)); hogsInitialised = true }
            }
            Text(L("只会正常退出应用（像 ⌘Q），不会强制结束；系统进程、Finder、程序坞和 Peekaboo 自己不会出现在这里。",
                   "Apps are quit normally (like ⌘Q), never force-killed. System processes, Finder, the Dock and Peekaboo itself are never listed."))
                .font(.caption).foregroundStyle(.tertiary)
        }
    }
}
