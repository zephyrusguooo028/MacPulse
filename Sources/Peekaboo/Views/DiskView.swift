import SwiftUI
import AppKit

enum Treemap {
    /// Squarified treemap layout. `values` must be sorted descending.
    static func layout(_ values: [Double], in rect: CGRect) -> [CGRect] {
        var result = [CGRect](repeating: .zero, count: values.count)
        let total = values.reduce(0, +)
        guard total > 0, rect.width > 0, rect.height > 0 else { return result }
        let scale = Double(rect.width * rect.height) / total
        let areas = values.map { max($0 * scale, 0.0001) }
        var r = rect
        var i = 0
        while i < areas.count {
            let short = Double(min(r.width, r.height))
            var row = [areas[i]]
            var j = i + 1
            while j < areas.count {
                let candidate = row + [areas[j]]
                if worst(candidate, short) <= worst(row, short) { row = candidate; j += 1 } else { break }
            }
            let sum = row.reduce(0, +)
            if r.width >= r.height {
                let w = CGFloat(sum) / r.height
                var y = r.minY
                for (k, a) in row.enumerated() {
                    let h = CGFloat(a) / w
                    result[i + k] = CGRect(x: r.minX, y: y, width: w, height: h)
                    y += h
                }
                r = CGRect(x: r.minX + w, y: r.minY, width: max(r.width - w, 0), height: r.height)
            } else {
                let h = CGFloat(sum) / r.width
                var x = r.minX
                for (k, a) in row.enumerated() {
                    let w = CGFloat(a) / h
                    result[i + k] = CGRect(x: x, y: r.minY, width: w, height: h)
                    x += w
                }
                r = CGRect(x: r.minX, y: r.minY + h, width: r.width, height: max(r.height - h, 0))
            }
            i = j
        }
        return result
    }

    private static func worst(_ row: [Double], _ w: Double) -> Double {
        let s = row.reduce(0, +)
        guard let mx = row.max(), let mn = row.min(), s > 0, mn > 0 else { return .infinity }
        return max(w * w * mx / (s * s), (s * s) / (w * w * mn))
    }
}

struct DiskView: View {
    @Environment(AppState.self) private var app
    @Local private var hovered: DiskNode?

    var body: some View {
        let d = app.monitor.disk
        let scan = app.disk
        VStack(alignment: .leading, spacing: 16) {
            PageHeader(title: L("磁盘", "Disk"), subtitle: L("共 \(Fmt.bytes(d.total))，可用 \(Fmt.bytes(d.available))", "\(Fmt.bytes(d.available)) free of \(Fmt.bytes(d.total))")) {
                HStack {
                    if scan.scanning {
                        Button(L("停止", "Stop")) { scan.cancel() }
                    } else {
                        Button { chooseFolder() } label: { Label(L("选择文件夹…", "Choose folder…"), systemImage: "folder") }
                        Button { scan.scan(Paths.home) } label: {
                            Label(scan.root == nil ? L("扫描用户目录", "Scan home folder") : L("重新扫描", "Rescan"), systemImage: "arrow.clockwise")
                        }
                        .buttonStyle(.borderedProminent)
                    }
                }
            }

            Card("Macintosh HD", symbol: "internaldrive") {
                StackedBar(segments: [
                    Segment(label: L("已用", "Used"), value: Double(d.used), color: Palette.cpu),
                    Segment(label: L("可清除（系统按需释放）", "Purgeable (freed by the system as needed)"), value: Double(d.purgeable), color: Palette.cpu.opacity(0.35)),
                    Segment(label: L("空闲", "Free"), value: Double(d.free), color: Palette.disk.opacity(0.5)),
                ], total: Double(d.total), height: 16)
                if let r = scan.root, r.url == Paths.home {
                    Text(L("其中你的用户目录占 \(Fmt.bytes(r.size))，其余约 \(Fmt.bytes(max(d.used - r.size, 0))) 是 macOS 系统、/Applications 里的应用和受保护的数据（需要「完全磁盘访问」才能统计）。", "Your home folder uses \(Fmt.bytes(r.size)). The remaining ~\(Fmt.bytes(max(d.used - r.size, 0))) is macOS, apps in /Applications and protected data (counting it requires Full Disk Access)."))
                        .font(.caption).foregroundStyle(.secondary)
                }
            }

            if scan.scanning {
                Card(L("正在扫描 \(scan.scanRoot.tildePath)", "Scanning \(scan.scanRoot.tildePath)"), symbol: "magnifyingglass") {
                    HStack(spacing: 12) {
                        ProgressView().controlSize(.small)
                        Text(L("已统计 \(scan.scannedFiles) 个文件，\(Fmt.bytes(scan.scannedBytes))", "\(scan.scannedFiles) files counted, \(Fmt.bytes(scan.scannedBytes))")).monospacedDigit()
                        Spacer()
                    }
                }
            }

            if let current = scan.current {
                breadcrumb(current)
                HStack(alignment: .top, spacing: 14) {
                    VStack(alignment: .leading, spacing: 6) {
                        TreemapView(node: current, hovered: $hovered) { n in
                            if n.isDirectory { scan.open(n) }
                        } onTrash: { n in scan.removed(n) }
                        .frame(maxWidth: .infinity, maxHeight: .infinity)
                        .clipShape(RoundedRectangle(cornerRadius: 12, style: .continuous))
                        hoverLine
                    }
                    childList(current).frame(width: 330)
                }
                .frame(maxHeight: .infinity)
            } else if !scan.scanning {
                Card(L("看看空间都去哪了", "See where your space went"), symbol: "square.grid.3x3.square") {
                    EmptyHint(symbol: "internaldrive", title: L("还没扫描", "Not scanned yet"),
                              detail: L("扫描你的用户目录（\(Paths.home.tildePath)），用方块图显示每个文件夹占了多少空间。\n第一次扫描大概需要几十秒。", "Scans your home folder (\(Paths.home.tildePath)) and shows how much space each folder takes in a treemap.\nThe first scan takes about a minute or less."))
                    HStack {
                        Spacer()
                        Button { scan.scan(Paths.home) } label: { Label(L("开始扫描", "Start scan"), systemImage: "play.fill") }
                            .buttonStyle(.borderedProminent).controlSize(.large)
                        Spacer()
                    }
                }
                Spacer()
            } else {
                Spacer()
            }
        }
        .padding(24)
    }

    private var hoverLine: some View {
        HStack(spacing: 6) {
            if let h = hovered {
                Image(systemName: h.isAggregate ? "doc.on.doc" : (h.isDirectory ? "folder.fill" : "doc.fill")).foregroundStyle(.secondary)
                Text(h.isAggregate ? h.name : h.url.tildePath).lineLimit(1).truncationMode(.middle)
                Spacer()
                Text(Fmt.bytes(h.size)).monospacedDigit().fontWeight(.medium)
                if h.isDirectory { Text(L("· 点击进入", "· Click to open")).foregroundStyle(.secondary) }
            } else {
                Text(L("鼠标移到方块上查看详情，点击文件夹进入，右键可以在 Finder 中显示或移到废纸篓", "Hover a block for details, click a folder to open it, right-click to show in Finder or move to Trash")).foregroundStyle(.secondary)
                Spacer()
            }
        }
        .font(.callout)
        .frame(height: 20)
    }

    private func breadcrumb(_ node: DiskNode) -> some View {
        HStack(spacing: 4) {
            Button { app.disk.up() } label: { Image(systemName: "chevron.left") }
                .disabled(node.parent == nil)
            ForEach(node.path) { n in
                if n != node.path.first { Image(systemName: "chevron.right").font(.caption2).foregroundStyle(.tertiary) }
                Button(n.parent == nil ? n.url.tildePath : n.name) { app.disk.current = n }
                    .buttonStyle(.plain)
                    .foregroundStyle(n == node ? .primary : .secondary)
                    .fontWeight(n == node ? .semibold : .regular)
            }
            Spacer()
            Text(L("\(Fmt.bytes(node.size)) · \(node.fileCount) 个文件", "\(Fmt.bytes(node.size)) · \(node.fileCount) files")).foregroundStyle(.secondary).monospacedDigit()
            if let t = app.disk.lastScan { Text(L("· 扫描于 \(Fmt.time(t))", "· Scanned at \(Fmt.time(t))")).foregroundStyle(.tertiary) }
        }
        .font(.callout)
    }

    private func childList(_ node: DiskNode) -> some View {
        let top = node.children.first?.size ?? 1
        return ScrollView {
            LazyVStack(spacing: 2) {
                ForEach(Array(node.children.prefix(200).enumerated()), id: \.element.id) { i, c in
                    Button {
                        if c.isDirectory { app.disk.open(c) }
                    } label: {
                        VStack(alignment: .leading, spacing: 4) {
                            HStack(spacing: 6) {
                                RoundedRectangle(cornerRadius: 2).fill(c.isAggregate ? Color.gray : Palette.treemap[i % Palette.treemap.count])
                                    .frame(width: 8, height: 8)
                                Image(systemName: c.isAggregate ? "doc.on.doc" : (c.isDirectory ? "folder" : "doc"))
                                    .foregroundStyle(.secondary).frame(width: 14)
                                Text(c.name).lineLimit(1).truncationMode(.middle)
                                Spacer()
                                Text(Fmt.bytes(c.size)).monospacedDigit().foregroundStyle(.secondary)
                            }
                            SizeBar(fraction: Double(c.size) / Double(max(top, 1)),
                                    color: c.isAggregate ? .gray : Palette.treemap[i % Palette.treemap.count])
                        }
                        .padding(.vertical, 5).padding(.horizontal, 8)
                        .background(RoundedRectangle(cornerRadius: 6).fill(hovered == c ? Color.primary.opacity(0.06) : .clear))
                        .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                    .onHover { hovered = $0 ? c : (hovered == c ? nil : hovered) }
                    .modifier(NodeMenu(node: c) { app.disk.removed(c) })
                }
            }
            .padding(6)
        }
        .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(Color(nsColor: .controlBackgroundColor)))
    }

    private func chooseFolder() {
        let p = NSOpenPanel()
        p.canChooseDirectories = true
        p.canChooseFiles = false
        p.allowsMultipleSelection = false
        p.directoryURL = Paths.home
        p.prompt = L("扫描", "Scan")
        if p.runModal() == .OK, let u = p.url { app.disk.scan(u) }
    }
}

struct NodeMenu: ViewModifier {
    let node: DiskNode
    let onTrash: () -> Void
    func body(content: Content) -> some View {
        if node.isAggregate { content } else { content.fileContextMenu(node.url, size: node.size, onTrash: onTrash) }
    }
}

struct TreemapView: View {
    let node: DiskNode
    @Binding var hovered: DiskNode?
    let onOpen: (DiskNode) -> Void
    let onTrash: (DiskNode) -> Void

    var body: some View {
        GeometryReader { g in
            let kids = Array(node.children.filter { $0.size > 0 }.prefix(120))
            let rects = Treemap.layout(kids.map { Double($0.size) }, in: CGRect(origin: .zero, size: g.size))
            ZStack(alignment: .topLeading) {
                ForEach(Array(kids.enumerated()), id: \.element.id) { i, c in
                    let r = rects[i]
                    let base = c.isAggregate ? Color.gray : Palette.treemap[i % Palette.treemap.count]
                    ZStack(alignment: .topLeading) {
                        Rectangle()
                            .fill(LinearGradient(colors: [base.opacity(hovered == c ? 1 : 0.88), base.opacity(hovered == c ? 0.85 : 0.68)],
                                                 startPoint: .topLeading, endPoint: .bottomTrailing))
                        if r.width > 56 && r.height > 30 {
                            VStack(alignment: .leading, spacing: 1) {
                                HStack(spacing: 3) {
                                    if c.isDirectory { Image(systemName: "folder.fill").font(.system(size: 9)) }
                                    Text(c.name).font(.system(size: 11, weight: .semibold)).lineLimit(1)
                                }
                                if r.height > 44 { Text(Fmt.bytes(c.size)).font(.system(size: 10)).monospacedDigit().opacity(0.85) }
                            }
                            .foregroundStyle(.white)
                            .padding(6)
                            .frame(width: r.width, alignment: .leading)
                        }
                    }
                    .frame(width: max(r.width - 2, 0), height: max(r.height - 2, 0))
                    .clipShape(RoundedRectangle(cornerRadius: 4, style: .continuous))
                    .offset(x: r.minX + 1, y: r.minY + 1)
                    .onHover { inside in
                        if inside { hovered = c } else if hovered == c { hovered = nil }
                    }
                    .onTapGesture { onOpen(c) }
                    .modifier(NodeMenu(node: c) { onTrash(c) })
                }
            }
        }
        .background(Color.primary.opacity(0.04))
    }
}
