import SwiftUI
import AppKit

/// Sidebar page: window layouts, their shortcuts and the drag-to-edge switch.
struct WindowSnapView: View {
    private let groups: [(String, [SnapAction])] = [
        (L("半屏", "Halves"), [.leftHalf, .rightHalf, .topHalf, .bottomHalf]),
        (L("四分之一", "Quarters"), [.topLeft, .topRight, .bottomLeft, .bottomRight]),
        (L("三分之一", "Thirds"), [.leftThird, .centerThird, .rightThird, .leftTwoThirds, .rightTwoThirds]),
        (L("其他", "More"), [.maximize, .center, .restore, .previousDisplay, .nextDisplay]),
    ]

    var body: some View {
        let s = WindowSnapper.shared
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                PageHeader(title: L("分屏", "Window layout"),
                           subtitle: L("按快捷键，或把窗口拖到屏幕边缘，一下摆好位置", "Use a shortcut, or drag a window to a screen edge, to put it in place")) {
                    Button { s.resetHotkeys() } label: { Label(L("恢复默认快捷键", "Restore default shortcuts"), systemImage: "arrow.counterclockwise") }
                        .controlSize(.large)
                }

                if !s.trusted && !Demo.active {
                    HStack(spacing: 12) {
                        Image(systemName: "accessibility").font(.system(size: 20)).foregroundStyle(Palette.warn)
                        VStack(alignment: .leading, spacing: 2) {
                            Text(L("需要「辅助功能」权限", "Accessibility permission needed")).font(.headline)
                            Text(L("macOS 要求开启它，Peekaboo 才能移动和缩放其他应用的窗口。开启后立即生效，不用重启。",
                                   "macOS requires it before Peekaboo can move and resize other apps' windows. It works right away, no restart."))
                                .foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                        }
                        Spacer()
                        Button(L("去授权", "Grant")) { PermissionKind.accessibility.request() }.buttonStyle(.borderedProminent)
                    }
                    .padding(14)
                    .background(RoundedRectangle(cornerRadius: 14, style: .continuous).fill(Palette.warn.opacity(0.1)))
                }

                Card(L("使用方式", "How it works"), symbol: "slider.horizontal.3") {
                    HStack(alignment: .center) {
                        VStack(alignment: .leading, spacing: 2) {
                            Text(L("快捷键", "Keyboard shortcuts"))
                            Text(L("默认是 ⌃⌥ 加方向键或字母，和 Magnet 一样。点下面的快捷键可以改，按 ⌫ 清除。",
                                   "⌃⌥ plus an arrow or letter by default, the same as Magnet. Click a shortcut below to change it, ⌫ to clear."))
                                .font(.caption).foregroundStyle(.secondary)
                        }
                        Spacer(minLength: 16)
                        Toggle("", isOn: Binding(get: { s.hotkeysEnabled }, set: { s.hotkeysEnabled = $0 })).labelsHidden()
                    }
                    Divider()
                    HStack(alignment: .center) {
                        VStack(alignment: .leading, spacing: 2) {
                            Text(L("拖到屏幕边缘自动分屏", "Snap when dragged to a screen edge"))
                            Text(L("左右边缘 → 半屏，四个角 → 四分之一，顶部 → 铺满，底部 → 三分之一。拖走已分屏的窗口会恢复原来的大小。",
                                   "Left/right edge → half, corners → quarter, top → maximize, bottom → thirds. Dragging a snapped window away restores its size."))
                                .font(.caption).foregroundStyle(.secondary)
                        }
                        Spacer(minLength: 16)
                        Toggle("", isOn: Binding(get: { s.dragEnabled }, set: { s.dragEnabled = $0 })).labelsHidden()
                    }
                    Text(L("点下面任意一个布局，会把你刚才在用的窗口摆过去。", "Click any layout below to apply it to the window you were just using."))
                        .font(.caption).foregroundStyle(.secondary)
                }
                .toggleStyle(.switch)

                ForEach(groups, id: \.0) { g in
                    Card(g.0, symbol: "rectangle.split.2x2") {
                        LazyVGrid(columns: [GridItem(.adaptive(minimum: 160), spacing: 12)], spacing: 12) {
                            ForEach(g.1) { a in tile(a) }
                        }
                    }
                }
            }
            .padding(24)
        }
    }

    private func tile(_ a: SnapAction) -> some View {
        let s = WindowSnapper.shared
        return VStack(spacing: 8) {
            Button { s.perform(a, fromPage: true) } label: {
                VStack(spacing: 6) {
                    LayoutGlyph(action: a).frame(width: 92, height: 58)
                    Text(a.title).font(.system(size: 12, weight: .medium)).lineLimit(1).minimumScaleFactor(0.8)
                }
                .frame(maxWidth: .infinity)
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .help(L("应用到刚才的窗口", "Apply to the window you were just using"))
            HotkeyRecorder(spec: Binding(get: { s.hotkey(a) }, set: { s.setHotkey(a, $0) }))
                .scaleEffect(0.9)
            if s.failed.contains(a) {
                Text(L("被其他应用占用", "Used by another app")).font(.caption2).foregroundStyle(Palette.danger)
            }
        }
        .padding(10)
        .frame(maxWidth: .infinity)
        .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(Color.primary.opacity(0.035)))
        .opacity(s.hotkeysEnabled || a.unit != nil ? 1 : 0.6)
    }
}

/// A tiny screen with the target region filled in.
struct LayoutGlyph: View {
    let action: SnapAction

    var body: some View {
        GeometryReader { g in
            let r = CGRect(origin: .zero, size: g.size)
            ZStack(alignment: .topLeading) {
                RoundedRectangle(cornerRadius: 6, style: .continuous).fill(Color.primary.opacity(0.06))
                RoundedRectangle(cornerRadius: 6, style: .continuous).strokeBorder(Color.primary.opacity(0.18), lineWidth: 1)
                if let u = action.unit {
                    let inner = r.insetBy(dx: 4, dy: 4)
                    RoundedRectangle(cornerRadius: 3, style: .continuous)
                        .fill(Palette.theme.gradient)
                        .frame(width: max(inner.width * u.width - 2, 2), height: max(inner.height * u.height - 2, 2))
                        .offset(x: inner.minX + inner.width * u.minX + 1, y: inner.minY + inner.height * u.minY + 1)
                } else if action == .center {
                    RoundedRectangle(cornerRadius: 3, style: .continuous)
                        .fill(Palette.theme.gradient)
                        .frame(width: r.width * 0.5, height: r.height * 0.56)
                        .offset(x: r.width * 0.25, y: r.height * 0.22)
                } else if let sym = action.symbol {
                    Image(systemName: sym)
                        .font(.system(size: 22, weight: .medium))
                        .foregroundStyle(Palette.theme)
                        .frame(width: r.width, height: r.height)
                }
            }
        }
    }
}
