import SwiftUI
import AppKit
import ImageIO
import UniformTypeIdentifiers

struct DuplicatesView: View {
    @Local private var tab: DupKind = .exact
    private var m: DuplicatesModel { DuplicatesModel.shared }

    /// Sidebar badge: what exact duplicates would free, once a scan has run.
    @MainActor static var badge: String? {
        let m = DuplicatesModel.shared
        guard m.lastScan != nil, m.reclaimableExact > 0 else { return nil }
        return Fmt.bytes(m.reclaimableExact)
    }

    var body: some View {
        let groups = tab == .exact ? m.exact : m.similar
        VStack(alignment: .leading, spacing: 14) {
            PageHeader(title: L("重复文件", "Duplicates"), subtitle: subtitle) {
                HStack {
                    if m.scanning {
                        if let f = m.fraction { ProgressView(value: f).frame(width: 120) } else { ProgressView().controlSize(.small) }
                        Text(m.status).foregroundStyle(.secondary).font(.callout).monospacedDigit().lineLimit(1)
                        Button(L("取消", "Cancel")) { m.cancel() }
                    } else {
                        Button { m.scan() } label: { Label(m.lastScan == nil ? L("开始扫描", "Scan") : L("重新扫描", "Rescan"), systemImage: "magnifyingglass") }
                            .buttonStyle(.borderedProminent).disabled(m.folders.isEmpty || m.working)
                    }
                }
            }
            settingsCard
            if m.lastScan == nil {
                Card(L("重复文件", "Duplicates"), symbol: "doc.on.doc") {
                    EmptyHint(symbol: "doc.on.doc", title: m.scanning ? L("正在扫描…", "Scanning…") : L("还没扫描", "Not scanned yet"),
                              detail: L("找出内容完全相同的文件和看起来很像的图片。只会读取文件，不会改动任何东西；删除前会让你确认。", "Finds files with identical contents and pictures that look alike. Files are only read, never changed; you confirm before anything is removed."))
                }
                Spacer(minLength: 0)
            } else {
                summary
                toolbar
                if groups.isEmpty {
                    Card(tab == .exact ? L("完全相同", "Identical") : L("相似图片", "Similar images"), symbol: "checkmark.seal") {
                        EmptyHint(symbol: "checkmark.seal", title: L("没有找到", "Nothing found"),
                                  detail: tab == .exact ? L("这些文件夹里没有内容完全相同的文件。", "No files with identical contents in these folders.") : L("没有看起来相似的图片。", "No similar-looking images."))
                    }
                    Spacer(minLength: 0)
                } else {
                    ScrollView {
                        LazyVStack(spacing: 10) {
                            ForEach(groups.prefix(300)) { g in groupCard(g) }
                            if groups.count > 300 {
                                Text(L("还有 \(groups.count - 300) 组未显示，处理完这些后重新扫描即可看到。", "\(groups.count - 300) more groups not shown. Rescan after handling these."))
                                    .font(.caption).foregroundStyle(.secondary).padding(8)
                            }
                        }
                    }
                }
            }
        }
        .padding(24)
    }

    private var subtitle: String {
        guard m.lastScan != nil else { return L("找出重复的文件和相似的图片，腾出空间", "Find duplicate files and similar images to free up space") }
        return L("扫描了 \(m.scannedFiles) 个文件 · \(Fmt.relative(m.lastScan))", "Scanned \(m.scannedFiles) files · \(Fmt.relative(m.lastScan))")
    }

    // MARK: Settings

    private var settingsCard: some View {
        Card(L("扫描范围", "What to scan"), symbol: "folder", trailing: {
            HStack(spacing: 6) {
                Text(L("最小文件", "Minimum size")).foregroundStyle(.secondary).font(.callout)
                Picker("", selection: Binding(get: { m.minKB }, set: { m.minKB = $0 })) {
                    ForEach([10, 100, 1024, 10 * 1024, 100 * 1024], id: \.self) { kb in Text(Fmt.bytes(Int64(kb) * 1024)).tag(kb) }
                }
                .labelsHidden().frame(width: 100)
            }
        }) {
            DupFlowLayout(spacing: 6) {
                ForEach(m.folders, id: \.self) { u in
                    HStack(spacing: 4) {
                        Image(systemName: "folder.fill").foregroundStyle(Palette.cpu).font(.caption)
                        Text(u.tildePath).font(.callout).lineLimit(1)
                        Button { m.removeFolder(u) } label: { Image(systemName: "xmark.circle.fill").foregroundStyle(.tertiary) }
                            .buttonStyle(.plain).disabled(m.scanning).help(L("不扫描这个文件夹", "Don't scan this folder"))
                    }
                    .padding(.horizontal, 8).padding(.vertical, 4)
                    .background(Capsule().fill(Color.primary.opacity(0.06)))
                }
                Button { m.addFolder() } label: { Label(L("添加文件夹…", "Add folder…"), systemImage: "plus") }
                    .buttonStyle(.borderless).disabled(m.scanning)
                if m.folders != DuplicatesModel.defaultFolders {
                    Button(L("恢复默认", "Reset")) { m.folders = DuplicatesModel.defaultFolders }.buttonStyle(.borderless).disabled(m.scanning)
                }
            }
            Text(L("会跳过隐藏文件夹、node_modules、.git、应用和照片图库等包，以及符号链接和只在 iCloud 里的文件。", "Hidden folders, node_modules, .git, packages such as apps and photo libraries, symlinks and iCloud-only files are skipped."))
                .font(.caption).foregroundStyle(.secondary)
        }
    }

    // MARK: Summary and actions

    private var summary: some View {
        HStack(spacing: 12) {
            tile(L("完全相同", "Identical"), "\(m.exact.count)", L("组 · 可释放 \(Fmt.bytes(m.reclaimableExact))", "groups · \(Fmt.bytes(m.reclaimableExact)) reclaimable"), Palette.netOut)
            tile(L("相似图片", "Similar images"), "\(m.similar.count)", L("组 · 约 \(Fmt.bytes(m.reclaimableSimilar))", "groups · about \(Fmt.bytes(m.reclaimableSimilar))"), Palette.mem)
            tile(L("已选择", "Selected"), "\(m.selected.count)", L("个文件 · \(Fmt.bytes(m.selectedSize))", "files · \(Fmt.bytes(m.selectedSize))"), Palette.theme)
        }
    }

    private func tile(_ title: String, _ value: String, _ caption: String, _ color: Color) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(title).font(.caption).foregroundStyle(.secondary)
            HStack(alignment: .firstTextBaseline, spacing: 5) {
                Text(value).font(.system(.title2, design: .rounded).weight(.semibold)).foregroundStyle(color).monospacedDigit()
                Text(caption).font(.callout).foregroundStyle(.secondary).lineLimit(1)
            }
        }
        .padding(12)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(Color(nsColor: .controlBackgroundColor)))
    }

    private var toolbar: some View {
        HStack(spacing: 10) {
            Picker("", selection: $tab) {
                Text(L("完全相同 (\(m.exact.count))", "Identical (\(m.exact.count))")).tag(DupKind.exact)
                Text(L("相似图片 (\(m.similar.count))", "Similar images (\(m.similar.count))")).tag(DupKind.similar)
            }
            .pickerStyle(.segmented).labelsHidden().frame(width: 280)
            Spacer()
            Menu {
                Section(L("每组保留一个", "Keep one per group")) {
                    ForEach(KeepRule.allCases) { r in
                        Button { m.keepRule = r; m.smartSelect() } label: {
                            if m.keepRule == r { Label(r.title, systemImage: "checkmark") } else { Text(r.title) }
                        }
                    }
                }
                Section(L("优先保留位于此处的文件", "Prefer keeping files in")) {
                    Button { m.preferredFolder = nil; m.smartSelect() } label: {
                        if m.preferredFolder == nil { Label(L("不指定", "No preference"), systemImage: "checkmark") } else { Text(L("不指定", "No preference")) }
                    }
                    ForEach(m.folders, id: \.self) { u in
                        Button { m.preferredFolder = u; m.smartSelect() } label: {
                            if m.preferredFolder == u { Label(u.tildePath, systemImage: "checkmark") } else { Text(u.tildePath) }
                        }
                    }
                }
                Divider()
                Button(L("全部取消选择", "Select none")) { m.selectNone() }
            } label: { Label(L("智能选择", "Smart select"), systemImage: "wand.and.stars") }
                .fixedSize()
            Button(role: .destructive) { m.trashSelected() } label: {
                Label(L("移到废纸篓", "Move to Trash"), systemImage: "trash")
            }
            .buttonStyle(.borderedProminent).tint(Palette.danger)
            .disabled(m.selected.isEmpty || m.working || m.scanning)
        }
        .overlay(alignment: .bottomLeading) {
            if let r = m.lastResult { Text(r).font(.caption).foregroundStyle(Palette.disk).offset(y: 18) }
        }
    }

    // MARK: Groups

    private func groupCard(_ g: DupGroup) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 8) {
                Image(systemName: g.kind == .exact ? "doc.on.doc" : "photo.on.rectangle").foregroundStyle(.secondary)
                Text(g.kind == .exact ? L("\(g.files.count) 个相同文件 · 每个 \(Fmt.bytes(g.files[0].size))", "\(g.files.count) identical files · \(Fmt.bytes(g.files[0].size)) each")
                                      : L("\(g.files.count) 张相似图片", "\(g.files.count) similar images"))
                    .fontWeight(.medium)
                Spacer()
                Text(L("可释放 \(Fmt.bytes(g.reclaimable))", "\(Fmt.bytes(g.reclaimable)) reclaimable")).font(.callout).foregroundStyle(.secondary).monospacedDigit()
            }
            .padding(.horizontal, 14).padding(.vertical, 10)
            Divider()
            ForEach(g.files) { f in fileRow(f) }
        }
        .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(Color(nsColor: .controlBackgroundColor)))
        .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous).strokeBorder(Color.primary.opacity(0.06)))
    }

    private func fileRow(_ f: DupFile) -> some View {
        let isOn = m.selected.contains(f.url)
        let whitelisted = AppSettings.shared.isWhitelisted(f.url)
        return HStack(spacing: 10) {
            Toggle("", isOn: Binding(get: { isOn }, set: { m.toggle(f, on: $0) }))
                .toggleStyle(.checkbox).labelsHidden()
                .disabled(!isOn && !m.canSelect(f))
                .help(!isOn && !m.canSelect(f) ? (m.deletable(f.url) ? L("每组至少保留一个文件", "At least one file per group is kept") : L("白名单或受保护位置，不会删除", "Whitelisted or protected; never deleted")) : "")
            DupThumb(url: f.url, isImage: f.isImage).frame(width: 40, height: 40)
            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 6) {
                    Text(f.url.lastPathComponent).lineLimit(1).truncationMode(.middle)
                    if !isOn {
                        Text(L("保留", "Keep")).font(.caption2).foregroundStyle(Palette.disk)
                            .padding(.horizontal, 5).padding(.vertical, 1).background(Capsule().fill(Palette.disk.opacity(0.12)))
                    }
                    if whitelisted { Text(L("白名单", "Whitelisted")).font(.caption2).foregroundStyle(.secondary) }
                }
                Text(f.url.deletingLastPathComponent().tildePath).font(.caption).foregroundStyle(.secondary).lineLimit(1).truncationMode(.middle)
            }
            Spacer(minLength: 8)
            VStack(alignment: .trailing, spacing: 2) {
                Text(Fmt.bytes(f.size)).monospacedDigit()
                Text(f.modified.map { Self.dateFormat.string(from: $0) } ?? "—").font(.caption).foregroundStyle(.secondary).monospacedDigit()
            }
            Button { FileActions.reveal(f.url) } label: { Image(systemName: "magnifyingglass") }
                .buttonStyle(.borderless).help(L("在 Finder 中显示", "Show in Finder"))
        }
        .padding(.horizontal, 14).padding(.vertical, 6)
        .background(isOn ? Palette.danger.opacity(0.05) : Color.clear)
        .contextMenu {
            Button(L("在 Finder 中显示", "Show in Finder")) { FileActions.reveal(f.url) }
            Button(L("拷贝路径", "Copy path")) {
                NSPasteboard.general.clearContents()
                NSPasteboard.general.setString(f.url.path, forType: .string)
            }
            Button(L("加入白名单（永不清理）", "Add to whitelist (never clean)")) {
                AppSettings.shared.whitelist.append(f.url.tildePath)
                m.selected.remove(f.url)
            }
        }
    }

    static let dateFormat: DateFormatter = {
        let f = DateFormatter()
        f.dateFormat = "yyyy-MM-dd HH:mm"
        return f
    }()
}

/// Small thumbnail for an image, or the file type's icon. Loads off the main thread.
struct DupThumb: View {
    let url: URL
    let isImage: Bool
    @Local private var image: NSImage?

    private static let cache = NSCache<NSURL, NSImage>()

    var body: some View {
        ZStack {
            if let image {
                Image(nsImage: image).resizable().aspectRatio(contentMode: isImage ? .fill : .fit)
            } else if isImage && Demo.active {
                DupDemoPicture(seed: url.lastPathComponent)
            } else {
                Image(nsImage: NSWorkspace.shared.icon(for: UTType(filenameExtension: url.pathExtension) ?? .data)).resizable().aspectRatio(contentMode: .fit)
            }
        }
        .clipShape(RoundedRectangle(cornerRadius: 6, style: .continuous))
        .task(id: url) {
            guard isImage, !Demo.active else { return }
            if let c = Self.cache.object(forKey: url as NSURL) { image = c; return }
            let u = url
            let img = await Task.detached(priority: .utility) { () -> NSImage? in
                guard let src = CGImageSourceCreateWithURL(u as CFURL, nil),
                      let cg = CGImageSourceCreateThumbnailAtIndex(src, 0, [kCGImageSourceCreateThumbnailFromImageAlways: true,
                                                                            kCGImageSourceCreateThumbnailWithTransform: true,
                                                                            kCGImageSourceThumbnailMaxPixelSize: 96] as CFDictionary) else { return nil }
                return NSImage(cgImage: cg, size: NSSize(width: cg.width, height: cg.height))
            }.value
            if let img { Self.cache.setObject(img, forKey: u as NSURL); image = img }
        }
    }
}

/// Made-up "photo" for demo screenshots: a soft landscape whose colours come from the name.
struct DupDemoPicture: View {
    let seed: String
    var body: some View {
        // Pictures whose names start alike (the similar ones) share colours.
        let key = String(seed.prefix(6))
        let hue = Double(key.unicodeScalars.reduce(7) { ($0 &* 31 &+ Int($1.value)) % 9973 } % 360) / 360
        ZStack(alignment: .bottom) {
            LinearGradient(colors: [Color(hue: hue, saturation: 0.45, brightness: 0.95), Color(hue: (hue + 0.08).truncatingRemainder(dividingBy: 1), saturation: 0.55, brightness: 0.8)], startPoint: .top, endPoint: .bottom)
            Circle().fill(Color.white.opacity(0.8)).frame(width: 10, height: 10).offset(x: 8, y: -24)
            Ellipse().fill(Color(hue: (hue + 0.3).truncatingRemainder(dividingBy: 1), saturation: 0.5, brightness: 0.55)).frame(width: 60, height: 26).offset(x: -8, y: 10)
        }
    }
}

/// Wraps children onto multiple lines.
struct DupFlowLayout: Layout {
    var spacing: CGFloat = 6
    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        let rows = arrange(width: proposal.width ?? .infinity, subviews)
        return CGSize(width: proposal.width ?? rows.map(\.width).max() ?? 0, height: rows.last.map { $0.y + $0.height } ?? 0)
    }
    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        var i = 0
        for row in arrange(width: bounds.width, subviews) {
            var x = bounds.minX
            for s in row.sizes {
                subviews[i].place(at: CGPoint(x: x, y: bounds.minY + row.y + (row.height - s.height) / 2), proposal: ProposedViewSize(s))
                x += s.width + spacing
                i += 1
            }
        }
    }
    private struct Row { var sizes: [CGSize] = []; var width: CGFloat = 0; var height: CGFloat = 0; var y: CGFloat = 0 }
    private func arrange(width: CGFloat, _ subviews: Subviews) -> [Row] {
        var rows: [Row] = [Row()]
        for s in subviews.map({ $0.sizeThatFits(.unspecified) }) {
            if rows[rows.count - 1].width + s.width > width && !rows[rows.count - 1].sizes.isEmpty {
                let y = rows[rows.count - 1].y + rows[rows.count - 1].height + spacing
                rows.append(Row(y: y))
            }
            rows[rows.count - 1].sizes.append(s)
            rows[rows.count - 1].width += s.width + (rows[rows.count - 1].sizes.count > 1 ? spacing : 0)
            rows[rows.count - 1].height = max(rows[rows.count - 1].height, s.height)
        }
        return rows
    }
}
