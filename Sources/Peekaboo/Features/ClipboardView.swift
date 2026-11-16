import AppKit
import SwiftUI

/// Sidebar page: the full clipboard history with search, filters, pin and delete.
struct ClipboardView: View {
    @MainActor static var badge: String? {
        let s = ClipboardStore.shared
        return s.enabled && !s.items.isEmpty ? "\(s.items.count)" : nil
    }

    @Local private var query = ""
    @Local private var filter: ClipKind?
    @Local private var copied: UUID?

    var body: some View {
        let store = ClipboardStore.shared
        let q = query.trimmingCharacters(in: .whitespaces)
        let shown = store.items.filter { (filter == nil || $0.kind == filter) && $0.matches(q) }
            .sorted { $0.pinned != $1.pinned ? $0.pinned : $0.date > $1.date }
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                PageHeader(title: L("剪贴板", "Clipboard"),
                           subtitle: L("复制过的文字、图片、链接和文件都在这里，按 \(store.hotkey?.title ?? "—") 随时调出", "Everything you've copied: text, images, links and files. Press \(store.hotkey?.title ?? "—") to bring it up anywhere")) {
                    HStack {
                        Button { ClipboardPicker.shared.show() } label: { Label(L("快速面板", "Quick picker"), systemImage: "rectangle.and.text.magnifyingglass") }
                            .disabled(!store.enabled)
                        Button(role: .destructive) { store.confirmClear() } label: { Label(L("清空", "Clear"), systemImage: "trash") }
                            .disabled(store.items.allSatisfy(\.pinned))
                    }
                    .controlSize(.large)
                }

                if !store.enabled {
                    Card(L("剪贴板历史", "Clipboard history"), symbol: "list.clipboard") {
                        EmptyHint(symbol: "list.clipboard", title: L("剪贴板历史已关闭", "Clipboard history is off"),
                                  detail: L("打开后，Peekaboo 会在本机记住你复制过的内容。", "When it's on, Peekaboo remembers what you copy, on this Mac only."))
                        HStack { Spacer(); Button(L("开启剪贴板历史", "Turn on clipboard history")) { store.enabled = true }.buttonStyle(.borderedProminent); Spacer() }
                    }
                } else {
                    HStack(spacing: 10) {
                        HStack(spacing: 6) {
                            Image(systemName: "magnifyingglass").foregroundStyle(.secondary)
                            TextField(L("搜索", "Search"), text: $query).textFieldStyle(.plain)
                            if !query.isEmpty {
                                Button { query = "" } label: { Image(systemName: "xmark.circle.fill").foregroundStyle(.tertiary) }.buttonStyle(.plain)
                            }
                        }
                        .padding(.horizontal, 10).padding(.vertical, 6)
                        .background(RoundedRectangle(cornerRadius: 8, style: .continuous).fill(Color(nsColor: .controlBackgroundColor)))
                        .overlay(RoundedRectangle(cornerRadius: 8, style: .continuous).strokeBorder(Color.primary.opacity(0.08)))
                        .frame(maxWidth: 280)
                        chip(nil, L("全部", "All"), count: store.items.count)
                        ForEach(ClipKind.allCases, id: \.self) { k in chip(k, k.title, count: store.items.filter { $0.kind == k }.count) }
                        Spacer()
                    }

                    Card(L("历史记录", "History"), symbol: "clock.arrow.circlepath", trailing: {
                        Text(L("\(store.items.count) / \(store.maxItems) 条 · 已固定 \(store.items.filter(\.pinned).count)", "\(store.items.count) / \(store.maxItems) · \(store.items.filter(\.pinned).count) pinned"))
                            .font(.caption).foregroundStyle(.secondary)
                    }) {
                        if shown.isEmpty {
                            EmptyHint(symbol: "doc.on.clipboard",
                                      title: store.items.isEmpty ? L("还没有记录", "Nothing yet") : L("没有匹配的条目", "No matching items"),
                                      detail: store.items.isEmpty ? L("复制一段文字、一张图片或一个文件，它就会出现在这里。", "Copy some text, an image or a file and it shows up here.") : L("换个关键词或筛选条件试试。", "Try another search or filter."))
                        } else {
                            LazyVStack(spacing: 0) {
                                ForEach(shown) { item in
                                    row(item)
                                    if item.id != shown.last?.id { Divider().padding(.leading, 54) }
                                }
                            }
                        }
                    }
                }

                Card(L("隐私", "Privacy"), symbol: "lock.shield") {
                    Text(L("剪贴板历史只保存在本机（\(store.folder.tildePath)），从不联网或上传。密码管理器标记为「隐藏」或「临时」的内容（如 1Password、钥匙串里复制的密码）会自动跳过。",
                           "Clipboard history is stored only on this Mac (\(store.folder.tildePath)) and is never uploaded. Content that password managers mark as concealed or transient (such as passwords copied from 1Password or Keychain) is skipped automatically."))
                        .font(.callout).foregroundStyle(.secondary)
                }
            }
            .padding(24)
        }
    }

    private func chip(_ kind: ClipKind?, _ title: String, count: Int) -> some View {
        let on = filter == kind
        return Button { filter = kind } label: {
            HStack(spacing: 4) {
                if let kind { Image(systemName: kind.symbol).font(.system(size: 10)) }
                Text(title)
                Text("\(count)").foregroundStyle(on ? Color.white.opacity(0.8) : Color.secondary).monospacedDigit()
            }
            .font(.system(size: 12, weight: .medium))
            .padding(.horizontal, 10).padding(.vertical, 5)
            .foregroundStyle(on ? Color.white : Color.primary)
            .background(Capsule().fill(on ? Palette.theme : Color.primary.opacity(0.06)))
        }
        .buttonStyle(.plain)
    }

    private func row(_ item: ClipItem) -> some View {
        let store = ClipboardStore.shared
        return HStack(alignment: .center, spacing: 12) {
            ClipThumb(item: item, size: 42)
            VStack(alignment: .leading, spacing: 3) {
                Text(item.preview)
                    .lineLimit(2)
                    .font(item.kind == .text && (item.text ?? "").contains("{") ? .system(size: 12, design: .monospaced) : .callout)
                    .foregroundStyle(item.kind == .link ? Color.blue : Color.primary)
                    .textSelection(.enabled)
                HStack(spacing: 4) {
                    Image(systemName: item.kind.symbol).font(.system(size: 9))
                    Text([item.source, Fmt.relative(item.date)].compactMap { $0 }.joined(separator: " · "))
                }
                .font(.caption).foregroundStyle(.secondary)
            }
            Spacer(minLength: 8)
            HStack(spacing: 2) {
                iconButton(copied == item.id ? "checkmark" : "doc.on.doc", copied == item.id ? L("已复制", "Copied") : L("复制", "Copy"), tint: copied == item.id ? Palette.disk : nil) {
                    if store.copy(item) {
                        copied = item.id
                        DispatchQueue.main.asyncAfter(deadline: .now() + 1.5) { if copied == item.id { copied = nil } }
                    }
                }
                iconButton(item.pinned ? "pin.fill" : "pin", item.pinned ? L("取消固定", "Unpin") : L("固定（不会被自动清除）", "Pin (never removed automatically)"), tint: item.pinned ? Palette.theme : nil) {
                    store.togglePin(item)
                }
                iconButton("trash", L("删除", "Delete"), tint: nil) { store.delete(item) }
            }
        }
        .padding(.vertical, 8)
        .contextMenu {
            Button(L("复制", "Copy")) { store.copy(item) }
            Button(item.pinned ? L("取消固定", "Unpin") : L("固定", "Pin")) { store.togglePin(item) }
            if item.kind == .file, let p = item.paths?.first { Button(L("在 Finder 中显示", "Show in Finder")) { FileActions.reveal(URL(fileURLWithPath: p)) } }
            if item.kind == .link, let t = item.text, let u = URL(string: t.trimmingCharacters(in: .whitespacesAndNewlines)) { Button(L("在浏览器中打开", "Open in browser")) { NSWorkspace.shared.open(u) } }
            Divider()
            Button(L("删除", "Delete"), role: .destructive) { store.delete(item) }
        }
    }

    private func iconButton(_ symbol: String, _ help: String, tint: Color?, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Image(systemName: symbol)
                .font(.system(size: 13))
                .foregroundStyle(tint ?? Color.secondary)
                .frame(width: 28, height: 28)
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .help(help)
    }
}

/// Card shown in Settings.
struct ClipboardSettingsCard: View {
    @Local private var trusted = AXIsProcessTrusted()

    var body: some View {
        let store = ClipboardStore.shared
        Card(L("剪贴板历史", "Clipboard history"), symbol: "list.clipboard") {
            Toggle(L("记录剪贴板历史", "Keep clipboard history"), isOn: Binding(get: { store.enabled }, set: { store.enabled = $0 }))
            HStack(spacing: 10) {
                Text(L("快速面板快捷键", "Quick picker shortcut"))
                HotkeyRecorder(spec: Binding(get: { store.hotkey }, set: { store.hotkey = $0 }))
                Button(L("恢复默认", "Restore default")) { store.hotkey = ClipboardStore.defaultHotkey }
                    .disabled(store.hotkey == ClipboardStore.defaultHotkey)
            }
            .disabled(!store.enabled)
            if !store.hotkeyOK {
                Text(L("这个快捷键已被占用，换一个试试。", "This shortcut is already in use. Try another one.")).font(.caption).foregroundStyle(Palette.danger)
            }
            HStack {
                Text(L("最多保留", "Keep up to"))
                Picker("", selection: Binding(get: { store.maxItems }, set: { store.maxItems = $0 })) {
                    ForEach([50, 100, 200, 500, 1000], id: \.self) { Text(L("\($0) 条", "\($0) items")).tag($0) }
                }
                .labelsHidden().frame(width: 110)
                Text(L("已固定的条目不会被自动清除", "Pinned items are never removed automatically")).font(.caption).foregroundStyle(.secondary)
            }
            Toggle(L("选中后自动粘贴到当前应用", "Paste into the current app after choosing"), isOn: Binding(get: { store.autoPaste }, set: {
                store.autoPaste = $0
                if $0 && !AXIsProcessTrusted() { store.requestAccessibility() }
            }))
            if store.autoPaste && !trusted {
                VStack(alignment: .leading, spacing: 6) {
                    Text(L("自动粘贴需要模拟按下 ⌘V，macOS 要求为此在 系统设置 › 隐私与安全性 › 辅助功能 里允许 Peekaboo。没有授权时只会复制，需要你自己按 ⌘V。",
                           "Auto-paste works by pressing ⌘V for you, so macOS requires allowing Peekaboo in System Settings › Privacy & Security › Accessibility. Without it, items are only copied and you press ⌘V yourself."))
                        .font(.caption).foregroundStyle(.secondary)
                    HStack {
                        Button(L("去授权", "Grant access")) {
                            store.requestAccessibility()
                            if let u = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility") { NSWorkspace.shared.open(u) }
                        }
                        Button(L("重启 Peekaboo", "Restart Peekaboo")) { AppRelaunch.now() }
                    }
                }
                .task {
                    while !Task.isCancelled && !trusted {
                        try? await Task.sleep(for: .seconds(1.5))
                        trusted = AXIsProcessTrusted()
                    }
                }
            }
            HStack {
                Text(L("当前 \(store.items.count) 条记录，只保存在本机。密码管理器的内容会自动跳过。", "\(store.items.count) items, stored on this Mac only. Password-manager content is skipped."))
                    .font(.caption).foregroundStyle(.secondary)
                Spacer()
                Button(L("清空历史…", "Clear history…")) { store.confirmClear() }
                    .disabled(store.items.allSatisfy(\.pinned))
            }
        }
    }
}
