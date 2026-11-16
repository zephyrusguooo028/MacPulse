import AppKit
import SwiftUI

/// The quick picker (⌥⇧V by default): a floating panel with a search field and the
/// recent items. ↑/↓ + Return or a click copies the item back; Esc closes.
@MainActor
final class ClipboardPicker: NSObject, NSWindowDelegate {
    static let shared = ClipboardPicker()
    private var panel: PickerPanel?
    private var monitor: Any?
    let model = PickerModel()

    func toggle() { panel == nil ? show() : close() }

    func show() {
        guard panel == nil else { return }
        model.query = ""
        model.selection = 0
        let size = NSSize(width: 580, height: 460)
        let screen = NSScreen.screens.first { NSMouseInRect(NSEvent.mouseLocation, $0.frame, false) } ?? NSScreen.main
        let vf = screen?.visibleFrame ?? NSRect(x: 0, y: 0, width: 1440, height: 900)
        let origin = NSPoint(x: vf.midX - size.width / 2, y: vf.midY - size.height / 2 + vf.height * 0.08)
        let p = PickerPanel(contentRect: NSRect(origin: origin, size: size),
                            styleMask: [.titled, .fullSizeContentView, .nonactivatingPanel], backing: .buffered, defer: false)
        p.titleVisibility = .hidden
        p.titlebarAppearsTransparent = true
        p.isMovableByWindowBackground = true
        p.standardWindowButton(.closeButton)?.isHidden = true
        p.standardWindowButton(.miniaturizeButton)?.isHidden = true
        p.standardWindowButton(.zoomButton)?.isHidden = true
        p.level = .floating
        p.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .transient]
        p.isReleasedWhenClosed = false
        p.hidesOnDeactivate = false
        p.becomesKeyOnlyIfNeeded = false
        p.delegate = self
        let host = NSHostingView(rootView: PickerView(model: model).environment(AppState.shared))
        host.frame = NSRect(origin: .zero, size: size)
        p.contentView = host
        panel = p
        p.makeKeyAndOrderFront(nil)
        monitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self] e in
            guard let self, e.window === self.panel else { return e }
            return self.handle(e) ? nil : e
        }
        DispatchQueue.main.async { [weak self] in self?.focusSearch() }
    }

    func close() {
        if let m = monitor { NSEvent.removeMonitor(m) }
        monitor = nil
        let p = panel
        panel = nil
        p?.orderOut(nil)
        p?.close()
    }

    /// Copies the item and closes; optionally pastes into the app that was in front.
    func choose(_ item: ClipItem) {
        let store = ClipboardStore.shared
        store.copy(item)
        close()
        if store.autoPaste && store.canAutoPaste {
            // The panel never activated Peekaboo, so the previous app is still frontmost.
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.12) { store.pasteIntoFrontApp() }
        }
    }

    func windowDidResignKey(_ notification: Notification) { close() }

    private func handle(_ e: NSEvent) -> Bool {
        let list = model.results
        switch e.keyCode {
        case 53: close(); return true                                      // esc
        case 125: model.selection = min(model.selection + 1, max(list.count - 1, 0)); return true  // down
        case 126: model.selection = max(model.selection - 1, 0); return true                      // up
        case 36, 76:                                                       // return / enter
            if list.indices.contains(model.selection) { choose(list[model.selection]) }
            return true
        default: return false
        }
    }

    private func focusSearch() {
        func find(_ v: NSView) -> NSTextField? {
            if let t = v as? NSTextField, t.isEditable { return t }
            for s in v.subviews { if let t = find(s) { return t } }
            return nil
        }
        guard let p = panel, let content = p.contentView, let field = find(content) else { return }
        p.makeFirstResponder(field)
    }
}

final class PickerPanel: NSPanel {
    override var canBecomeKey: Bool { true }
}

@MainActor
@Observable
final class PickerModel {
    var query = "" { didSet { selection = 0 } }
    var selection = 0
    var results: [ClipItem] {
        let q = query.trimmingCharacters(in: .whitespaces)
        return Array(ClipboardStore.shared.items.filter { $0.matches(q) }.prefix(60))
    }
}

private struct PickerView: View {
    let model: PickerModel

    var body: some View {
        @Bindable var m = model
        let results = model.results
        VStack(spacing: 0) {
            HStack(spacing: 8) {
                Image(systemName: "magnifyingglass").foregroundStyle(.secondary)
                TextField(L("搜索剪贴板历史", "Search clipboard history"), text: $m.query)
                    .textFieldStyle(.plain)
                    .font(.system(size: 17))
                Text(ClipboardStore.shared.hotkey?.title ?? "").font(.caption).foregroundStyle(.tertiary)
            }
            .padding(.horizontal, 16)
            .padding(.top, 14)
            .padding(.bottom, 10)
            Divider()
            if results.isEmpty {
                VStack(spacing: 8) {
                    Image(systemName: "list.clipboard").font(.system(size: 30)).foregroundStyle(.tertiary)
                    Text(ClipboardStore.shared.items.isEmpty ? L("还没有记录，复制点东西试试", "Nothing yet. Copy something to get started.") : L("没有匹配的条目", "No matching items"))
                        .foregroundStyle(.secondary)
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                ScrollViewReader { proxy in
                    ScrollView {
                        LazyVStack(spacing: 2) {
                            ForEach(Array(results.enumerated()), id: \.element.id) { i, item in
                                PickerRow(item: item, selected: i == model.selection)
                                    .id(item.id)
                                    .contentShape(Rectangle())
                                    .onTapGesture { ClipboardPicker.shared.choose(item) }
                                    .onHover { if $0 { model.selection = i } }
                            }
                        }
                        .padding(6)
                    }
                    .onChange(of: model.selection) { _, s in
                        if results.indices.contains(s) { proxy.scrollTo(results[s].id) }
                    }
                }
            }
            Divider()
            HStack(spacing: 14) {
                hint("↑↓", L("选择", "Select"))
                hint("↩", ClipboardStore.shared.autoPaste && ClipboardStore.shared.canAutoPaste ? L("粘贴", "Paste") : L("复制", "Copy"))
                hint("esc", L("关闭", "Close"))
                Spacer()
                Text(L("\(ClipboardStore.shared.items.count) 条", "\(ClipboardStore.shared.items.count) items")).foregroundStyle(.tertiary)
            }
            .font(.caption)
            .padding(.horizontal, 14)
            .padding(.vertical, 8)
        }
        .frame(width: 580, height: 460)
        .background(.regularMaterial)
    }

    private func hint(_ key: String, _ label: String) -> some View {
        HStack(spacing: 4) {
            Text(key).font(.caption.weight(.semibold))
                .padding(.horizontal, 5).padding(.vertical, 1)
                .background(RoundedRectangle(cornerRadius: 4).fill(Color.primary.opacity(0.08)))
            Text(label).foregroundStyle(.secondary)
        }
    }
}

private struct PickerRow: View {
    let item: ClipItem
    let selected: Bool

    var body: some View {
        HStack(spacing: 10) {
            ClipThumb(item: item, size: 40)
            VStack(alignment: .leading, spacing: 3) {
                Text(item.preview).lineLimit(2).font(.system(size: 13))
                    .foregroundStyle(selected ? Color.white : Color.primary)
                HStack(spacing: 4) {
                    if item.pinned { Image(systemName: "pin.fill").font(.system(size: 9)) }
                    Text([item.source, Fmt.relative(item.date)].compactMap { $0 }.joined(separator: " · "))
                }
                .font(.caption)
                .foregroundStyle(selected ? Color.white.opacity(0.8) : Color.secondary)
            }
            Spacer(minLength: 0)
        }
        .padding(.horizontal, 8)
        .padding(.vertical, 6)
        .background(RoundedRectangle(cornerRadius: 8, style: .continuous).fill(selected ? Color.accentColor : Color.clear))
    }
}

/// Thumbnail for images, app-style icon for files, kind symbol otherwise.
struct ClipThumb: View {
    let item: ClipItem
    var size: CGFloat = 40

    var body: some View {
        Group {
            if item.kind == .image, let img = ClipboardStore.shared.thumbnail(item) {
                Image(nsImage: img).resizable().aspectRatio(contentMode: .fill)
            } else if item.kind == .file, let p = item.paths?.first {
                Image(nsImage: NSWorkspace.shared.icon(forFile: p)).resizable().aspectRatio(contentMode: .fit).padding(3)
            } else {
                Image(systemName: item.kind.symbol)
                    .font(.system(size: size * 0.4))
                    .foregroundStyle(item.kind == .link ? Color.blue : Color.secondary)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
                    .background(Color.primary.opacity(0.06))
            }
        }
        .frame(width: size, height: size)
        .clipShape(RoundedRectangle(cornerRadius: 7, style: .continuous))
    }
}
