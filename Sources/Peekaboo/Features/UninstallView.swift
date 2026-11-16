import SwiftUI
import AppKit

struct UninstallView: View {
    @Local private var query = ""
    @Local private var sort = "size"
    @Local private var dropTargeted = false
    private var m: UninstallModel { UninstallModel.shared }

    var body: some View {
        let list = filtered()
        VStack(alignment: .leading, spacing: 16) {
            PageHeader(title: L("卸载应用", "Uninstall"), subtitle: subtitle) {
                HStack {
                    if m.loading || m.sizing {
                        ProgressView().controlSize(.small)
                        Text(m.loading ? L("读取应用…", "Reading apps…") : L("计算大小…", "Measuring sizes…")).foregroundStyle(.secondary).font(.callout)
                    }
                    TextField(L("搜索", "Search"), text: $query).textFieldStyle(.roundedBorder).frame(width: 160)
                    Picker(L("排序", "Sort"), selection: $sort) {
                        Text(L("按大小", "By size")).tag("size")
                        Text(L("按名称", "By name")).tag("name")
                        Text(L("按最近使用", "By last used")).tag("used")
                        Text(L("按最久没用", "Least recently used")).tag("unused")
                    }
                    .labelsHidden().frame(width: 130)
                    Button { m.load() } label: { Image(systemName: "arrow.clockwise") }.help(L("重新读取", "Reload")).disabled(m.loading)
                }
            }
            if let r = m.lastResult {
                Label(r, systemImage: "checkmark.circle.fill").foregroundStyle(Palette.disk).font(.callout)
            }
            HStack(alignment: .top, spacing: 14) {
                appList(list)
                detail.frame(width: 400)
            }
            .frame(maxHeight: .infinity)
        }
        .padding(24)
        .overlay {
            if dropTargeted {
                RoundedRectangle(cornerRadius: 16).strokeBorder(Palette.theme, style: StrokeStyle(lineWidth: 3, dash: [8]))
                    .background(RoundedRectangle(cornerRadius: 16).fill(Palette.theme.opacity(0.06)))
                    .overlay(Label(L("松开以卸载这个应用", "Drop to uninstall this app"), systemImage: "arrow.down.app").font(.title3))
                    .padding(8)
            }
        }
        .dropDestination(for: URL.self) { urls, _ in
            guard let u = urls.first(where: { $0.pathExtension == "app" }) else { return false }
            return m.dropped(u)
        } isTargeted: { dropTargeted = $0 }
        .onAppear { if m.apps.isEmpty { m.load() } }
    }

    private var subtitle: String {
        if m.apps.isEmpty { return L("卸载应用并清理它留在资源库里的文件。也可以把应用拖到这里。", "Uninstall apps and the files they leave in your Library. You can also drop an app here.") }
        let total = m.apps.reduce(Int64(0)) { $0 + ($1.size ?? 0) }
        return L("\(m.apps.count) 个应用，共 \(Fmt.bytes(total)) · 不含 Apple 自带应用 · 可以把应用拖到这里", "\(m.apps.count) apps, \(Fmt.bytes(total)) in total · Apple's apps aren't listed · You can drop an app here")
    }

    private func filtered() -> [InstalledApp] {
        var l = m.apps
        let q = query.trimmingCharacters(in: .whitespaces)
        if !q.isEmpty { l = l.filter { $0.name.localizedCaseInsensitiveContains(q) || $0.bundleID.localizedCaseInsensitiveContains(q) } }
        switch sort {
        case "name": return l.sorted { $0.name.localizedStandardCompare($1.name) == .orderedAscending }
        case "used": return l.sorted { ($0.lastUsed ?? .distantPast) > ($1.lastUsed ?? .distantPast) }
        case "unused": return l.sorted { ($0.lastUsed ?? .distantPast) < ($1.lastUsed ?? .distantPast) }
        default: return l.sorted { ($0.size ?? 0) > ($1.size ?? 0) }
        }
    }

    private func appList(_ list: [InstalledApp]) -> some View {
        let maxSize = Double(max(list.compactMap(\.size).max() ?? 1, 1))
        return ScrollView {
            LazyVStack(spacing: 4) {
                if list.isEmpty && !m.loading {
                    EmptyHint(symbol: "app.dashed", title: L("没有应用", "No apps"), detail: query.isEmpty ? L("应用程序文件夹里没有可以卸载的第三方应用。", "No third-party apps in your Applications folders.") : L("没有匹配「\(query)」的应用。", "No apps match “\(query)”."))
                }
                ForEach(list) { a in
                    Button { m.select(a) } label: {
                        HStack(spacing: 10) {
                            AppIconView(app: a).frame(width: 34, height: 34)
                            VStack(alignment: .leading, spacing: 4) {
                                HStack {
                                    Text(a.name).fontWeight(.medium).lineLimit(1)
                                    if !a.version.isEmpty { Text(a.version).font(.caption).foregroundStyle(.tertiary).lineLimit(1) }
                                    Spacer()
                                    Text(a.size.map { Fmt.bytes($0) } ?? "…").monospacedDigit().fontWeight(.medium)
                                }
                                SizeBar(fraction: Double(a.size ?? 0) / maxSize, color: Color(red: 0.80, green: 0.30, blue: 0.35))
                                Text(a.lastUsed.map { L("上次使用 \(Fmt.relative($0))", "Last used \(Fmt.relative($0))") } ?? L("没有使用记录", "No usage record"))
                                    .font(.caption).foregroundStyle(.secondary)
                            }
                        }
                        .padding(10)
                        .background(RoundedRectangle(cornerRadius: 10).fill(m.selection == a.url ? Palette.theme.opacity(0.12) : Color(nsColor: .controlBackgroundColor)))
                        .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                    .contextMenu {
                        Button(L("在 Finder 中显示", "Show in Finder")) { FileActions.reveal(a.url) }
                    }
                }
            }
        }
    }

    @ViewBuilder
    private var detail: some View {
        if let a = m.selectedApp {
            Card(a.name, trailing: { EmptyView() }) {
                VStack(alignment: .leading, spacing: 12) {
                    HStack(spacing: 12) {
                        AppIconView(app: a).frame(width: 56, height: 56)
                        VStack(alignment: .leading, spacing: 3) {
                            Text(a.bundleID).font(.callout).foregroundStyle(.secondary).textSelection(.enabled).lineLimit(1)
                            Text(a.version.isEmpty ? a.url.deletingLastPathComponent().tildePath : L("版本 \(a.version) · \(a.url.deletingLastPathComponent().tildePath)", "Version \(a.version) · \(a.url.deletingLastPathComponent().tildePath)"))
                                .font(.caption).foregroundStyle(.secondary).lineLimit(1)
                            Text(a.lastUsed.map { L("上次使用 \(Fmt.relative($0))", "Last used \(Fmt.relative($0))") } ?? L("没有使用记录", "No usage record")).font(.caption).foregroundStyle(.secondary)
                        }
                    }
                    Divider()
                    HStack {
                        Text(L("应用本身", "The app")).fontWeight(.medium)
                        Spacer()
                        Text(a.size.map { Fmt.bytes($0) } ?? "…").monospacedDigit()
                    }
                    HStack {
                        Text(L("残留文件", "Leftover files")).fontWeight(.medium)
                        if m.findingLeftovers { ProgressView().controlSize(.small) }
                        Spacer()
                        Text(Fmt.bytes(m.leftovers.filter { m.picked.contains($0.url) }.reduce(0) { $0 + $1.size })).monospacedDigit()
                    }
                    if m.leftovers.isEmpty && !m.findingLeftovers {
                        Text(L("资源库里没有找到它的数据。", "No data found for it in your Library.")).font(.callout).foregroundStyle(.secondary)
                    }
                    ScrollView {
                        VStack(spacing: 2) {
                            ForEach(m.leftovers) { l in leftoverRow(l) }
                        }
                    }
                    .frame(maxHeight: .infinity)
                    Text(L("只列出名称与应用标识 \(a.bundleID) 或应用名完全一致的项目。登录启动项按内容匹配。", "Only items named exactly after the bundle id \(a.bundleID) or the app name are listed. Launch agents are matched by content."))
                        .font(.caption).foregroundStyle(.tertiary)
                    Button(role: .destructive) { m.uninstall() } label: {
                        Label(L("卸载", "Uninstall"), systemImage: "trash").frame(maxWidth: .infinity)
                    }
                    .buttonStyle(.borderedProminent).tint(Palette.danger).controlSize(.large)
                    .disabled(m.working || m.findingLeftovers)
                }
            }
            .frame(maxHeight: .infinity, alignment: .top)
        } else {
            Card(L("详情", "Details"), symbol: "app.badge") {
                EmptyHint(symbol: "arrow.down.app", title: L("选一个应用", "Pick an app"),
                          detail: L("在左边选择一个应用，或者把应用拖到这里。会列出它留下的缓存、偏好设置和数据，确认后一起移到废纸篓。", "Choose an app on the left, or drop one here. Its caches, preferences and data are listed, and after you confirm everything goes to the Trash together."))
            }
        }
    }

    private func leftoverRow(_ l: Leftover) -> some View {
        let whitelisted = AppSettings.shared.isWhitelisted(l.url)
        return HStack(spacing: 8) {
            Toggle("", isOn: Binding(get: { m.picked.contains(l.url) }, set: { if $0 { m.picked.insert(l.url) } else { m.picked.remove(l.url) } }))
                .toggleStyle(.checkbox).labelsHidden().disabled(whitelisted)
            VStack(alignment: .leading, spacing: 1) {
                HStack(spacing: 5) {
                    Text(l.kind).font(.callout)
                    if whitelisted { Text(L("白名单", "Whitelisted")).font(.caption2).foregroundStyle(.secondary) }
                }
                Text(l.url.tildePath).font(.caption).foregroundStyle(.secondary).lineLimit(1).truncationMode(.middle)
            }
            Spacer(minLength: 6)
            Text(Fmt.bytes(l.size)).font(.callout).monospacedDigit()
            Button { FileActions.reveal(l.url) } label: { Image(systemName: "magnifyingglass") }
                .buttonStyle(.borderless).help(L("在 Finder 中显示", "Show in Finder"))
        }
        .padding(.vertical, 3)
        .contextMenu {
            Button(L("在 Finder 中显示", "Show in Finder")) { FileActions.reveal(l.url) }
            Button(L("拷贝路径", "Copy path")) {
                NSPasteboard.general.clearContents()
                NSPasteboard.general.setString(l.url.path, forType: .string)
            }
            Button(L("加入白名单（永不清理）", "Add to whitelist (never clean)")) {
                AppSettings.shared.whitelist.append(l.url.tildePath)
                m.picked.remove(l.url)
            }
        }
    }
}

/// The app's Finder icon; demo apps get a drawn one so screenshots show no real apps.
struct AppIconView: View {
    let app: InstalledApp
    private static let cache = NSCache<NSString, NSImage>()

    var body: some View {
        if let sym = app.demoSymbol {
            let hue = Double(app.name.unicodeScalars.reduce(3) { ($0 &* 31 &+ Int($1.value)) % 9973 } % 360) / 360
            GeometryReader { g in
                RoundedRectangle(cornerRadius: g.size.width * 0.22, style: .continuous)
                    .fill(LinearGradient(colors: [Color(hue: hue, saturation: 0.55, brightness: 0.95), Color(hue: hue, saturation: 0.75, brightness: 0.7)], startPoint: .top, endPoint: .bottom))
                    .overlay(Image(systemName: sym).font(.system(size: g.size.width * 0.5, weight: .semibold)).foregroundStyle(.white))
            }
            .padding(2)
        } else {
            Image(nsImage: Self.icon(app.url.path)).resizable().aspectRatio(contentMode: .fit)
        }
    }

    static func icon(_ path: String) -> NSImage {
        if let i = cache.object(forKey: path as NSString) { return i }
        let i = NSWorkspace.shared.icon(forFile: path)
        cache.setObject(i, forKey: path as NSString)
        return i
    }
}
