import SwiftUI
import AppKit

struct Card<Content: View, Trailing: View>: View {
    let title: String
    var symbol: String? = nil
    @ViewBuilder var trailing: () -> Trailing
    @ViewBuilder var content: () -> Content

    init(_ title: String, symbol: String? = nil, @ViewBuilder trailing: @escaping () -> Trailing = { EmptyView() }, @ViewBuilder content: @escaping () -> Content) {
        self.title = title
        self.symbol = symbol
        self.trailing = trailing
        self.content = content
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(spacing: 6) {
                if let symbol { Image(systemName: symbol).foregroundStyle(.secondary) }
                Text(title).font(.headline)
                Spacer()
                trailing()
            }
            content()
        }
        .padding(16)
        .background(RoundedRectangle(cornerRadius: 14, style: .continuous).fill(Color(nsColor: .controlBackgroundColor)))
        .overlay(RoundedRectangle(cornerRadius: 14, style: .continuous).strokeBorder(Color.primary.opacity(0.06)))
    }
}

struct Ring: View {
    let value: Double
    let color: Color
    var lineWidth: CGFloat = 8
    var label: String
    var caption: String? = nil

    var body: some View {
        ZStack {
            Circle().stroke(color.opacity(0.15), lineWidth: lineWidth)
            Circle()
                .trim(from: 0, to: max(0.001, min(value, 1)))
                .stroke(color, style: StrokeStyle(lineWidth: lineWidth, lineCap: .round))
                .rotationEffect(.degrees(-90))
                .animation(.easeOut(duration: 0.4), value: value)
            VStack(spacing: 0) {
                Text(label).font(.system(.title3, design: .rounded).weight(.semibold)).monospacedDigit()
                if let caption { Text(caption).font(.caption2).foregroundStyle(.secondary) }
            }
        }
    }
}

struct Sparkline: View {
    let values: [Double]
    let color: Color
    var maxValue: Double? = 1

    var body: some View {
        GeometryReader { g in
            let top = maxValue ?? max(values.max() ?? 1, 1)
            let pts = values.enumerated().map { i, v in
                CGPoint(x: values.count > 1 ? g.size.width * CGFloat(i) / CGFloat(values.count - 1) : 0,
                        y: g.size.height * (1 - CGFloat(min(v / top, 1))))
            }
            ZStack {
                Path { p in
                    guard let f = pts.first else { return }
                    p.move(to: CGPoint(x: f.x, y: g.size.height))
                    pts.forEach { p.addLine(to: $0) }
                    p.addLine(to: CGPoint(x: pts.last!.x, y: g.size.height))
                    p.closeSubpath()
                }
                .fill(LinearGradient(colors: [color.opacity(0.35), color.opacity(0.02)], startPoint: .top, endPoint: .bottom))
                Path { p in
                    guard let f = pts.first else { return }
                    p.move(to: f)
                    pts.dropFirst().forEach { p.addLine(to: $0) }
                }
                .stroke(color, style: StrokeStyle(lineWidth: 1.5, lineJoin: .round))
            }
        }
    }
}

struct Segment: Identifiable {
    let label: String
    let value: Double
    let color: Color
    var id: String { label }
}

/// Horizontal stacked bar with an optional legend.
struct StackedBar: View {
    let segments: [Segment]
    var total: Double? = nil
    var height: CGFloat = 12
    var legend = true
    var format: (Double) -> String = { Fmt.bytes($0) }

    var body: some View {
        let sum = total ?? segments.reduce(0) { $0 + $1.value }
        VStack(alignment: .leading, spacing: 8) {
            GeometryReader { g in
                HStack(spacing: 1.5) {
                    ForEach(segments) { s in
                        Rectangle().fill(s.color)
                            .frame(width: sum > 0 ? max(0, g.size.width * CGFloat(s.value / sum) - 1.5) : 0)
                    }
                    Spacer(minLength: 0)
                }
                .background(Color.primary.opacity(0.07))
                .clipShape(RoundedRectangle(cornerRadius: height / 2.5, style: .continuous))
            }
            .frame(height: height)
            if legend {
                FlowLegend(items: segments.map { ($0.color, $0.label, format($0.value)) })
            }
        }
    }
}

struct FlowLegend: View {
    let items: [(Color, String, String)]
    var body: some View {
        HStack(spacing: 14) {
            ForEach(Array(items.enumerated()), id: \.offset) { _, it in
                HStack(spacing: 5) {
                    RoundedRectangle(cornerRadius: 2).fill(it.0).frame(width: 8, height: 8)
                    Text(it.1).foregroundStyle(.secondary)
                    Text(it.2).monospacedDigit()
                }
                .font(.caption)
            }
        }
    }
}

struct StatLine: View {
    let label: String
    let value: String
    var color: Color? = nil
    var body: some View {
        HStack {
            if let color { Circle().fill(color).frame(width: 7, height: 7) }
            Text(label).foregroundStyle(.secondary)
            Spacer()
            Text(value).monospacedDigit()
        }
        .font(.callout)
    }
}

struct SizeBar: View {
    let fraction: Double
    var color: Color = .accentColor
    var body: some View {
        GeometryReader { g in
            ZStack(alignment: .leading) {
                Capsule().fill(Color.primary.opacity(0.07))
                Capsule().fill(color).frame(width: max(2, g.size.width * CGFloat(min(max(fraction, 0), 1))))
            }
        }
        .frame(height: 5)
    }
}

struct PageHeader<Trailing: View>: View {
    let title: String
    let subtitle: String
    @ViewBuilder var trailing: () -> Trailing
    var body: some View {
        HStack(alignment: .firstTextBaseline) {
            VStack(alignment: .leading, spacing: 3) {
                Text(title).font(.system(size: 26, weight: .bold, design: .rounded))
                Text(subtitle).foregroundStyle(.secondary)
            }
            Spacer()
            trailing()
        }
    }
}

struct EmptyHint: View {
    let symbol: String
    let title: String
    let detail: String
    var body: some View {
        VStack(spacing: 10) {
            Image(systemName: symbol).font(.system(size: 36)).foregroundStyle(.tertiary)
            Text(title).font(.headline)
            Text(detail).foregroundStyle(.secondary).multilineTextAlignment(.center)
        }
        .frame(maxWidth: .infinity, minHeight: 220)
    }
}

enum AppRelaunch {
    /// Quits and reopens Peekaboo; some privacy permissions only apply after a restart.
    static func now() {
        let path = Bundle.main.bundlePath
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/bin/sh")
        p.arguments = ["-c", "sleep 0.8; /usr/bin/open \"\(path)\""]
        try? p.run()
        NSApp.terminate(nil)
    }
}

enum FileActions {
    static func reveal(_ url: URL) { NSWorkspace.shared.activateFileViewerSelecting([url]) }

    static func openFullDiskAccessSettings() {
        if let u = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_AllFiles") {
            NSWorkspace.shared.open(u)
        }
    }

    static func openInTerminal(_ url: URL) {
        let terminal = URL(fileURLWithPath: "/System/Applications/Utilities/Terminal.app")
        NSWorkspace.shared.open([url], withApplicationAt: terminal, configuration: NSWorkspace.OpenConfiguration())
    }

    @MainActor
    static func confirm(_ title: String, _ message: String, ok: String, destructive: Bool = true) -> Bool {
        let a = NSAlert()
        a.messageText = title
        a.informativeText = message
        a.alertStyle = destructive ? .warning : .informational
        let b = a.addButton(withTitle: ok)
        if destructive { b.hasDestructiveAction = true }
        a.addButton(withTitle: "取消")
        return a.runModal() == .alertFirstButtonReturn
    }

    /// Moves one path to the Trash after confirmation. Returns true on success.
    @MainActor
    static func trash(_ url: URL, size: Int64) -> Bool {
        guard CleanupModel.isSafeTarget(url) else {
            _ = confirm("不能删除这个位置", "\(url.tildePath) 是受保护的系统或个人目录。", ok: "好", destructive: false)
            return false
        }
        if AppSettings.shared.isWhitelisted(url) {
            _ = confirm("这个位置在白名单里", "\(url.tildePath) 已被加入白名单，Peekaboo 不会删除它。", ok: "好", destructive: false)
            return false
        }
        guard confirm("移到废纸篓？", "\(url.tildePath)\n\(Fmt.bytes(size))\n\n可以在废纸篓里恢复。", ok: "移到废纸篓") else { return false }
        do {
            try FileManager.default.trashItem(at: url, resultingItemURL: nil)
            return true
        } catch {
            _ = confirm("没能移到废纸篓", error.localizedDescription, ok: "好", destructive: false)
            return false
        }
    }
}

extension View {
    func fileContextMenu(_ url: URL, size: Int64, onTrash: @escaping () -> Void) -> some View {
        contextMenu {
            Button("在 Finder 中显示") { FileActions.reveal(url) }
            Button("拷贝路径") {
                NSPasteboard.general.clearContents()
                NSPasteboard.general.setString(url.path, forType: .string)
            }
            Button("加入白名单（永不清理）") { AppSettings.shared.whitelist.append(url.tildePath) }
            Divider()
            Button("移到废纸篓…", role: .destructive) {
                if FileActions.trash(url, size: size) { onTrash() }
            }
        }
    }
}

/// Drop-in for `@State`. On this SDK `@State` is a macro whose plugin ships only
/// with full Xcode, so building with the Command Line Tools alone needs this.
@propertyWrapper
struct Local<Value>: DynamicProperty {
    private var storage: State<Value>
    init(wrappedValue: Value) { storage = State(initialValue: wrappedValue) }
    var wrappedValue: Value {
        get { storage.wrappedValue }
        nonmutating set { storage.wrappedValue = newValue }
    }
    var projectedValue: Binding<Value> { storage.projectedValue }
}
