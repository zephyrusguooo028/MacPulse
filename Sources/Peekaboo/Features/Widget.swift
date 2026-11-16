import SwiftUI
import AppKit

// MARK: - Settings

enum WidgetSize: String, CaseIterable, Identifiable {
    case small, medium
    var id: String { rawValue }
    var title: String { self == .small ? L("小", "Small") : L("中", "Medium") }
    var width: CGFloat { self == .small ? 168 : 252 }
}

/// Desktop widget options, stored under `widget.*` in UserDefaults.
@MainActor
@Observable
final class WidgetPrefs {
    static let shared = WidgetPrefs()
    @ObservationIgnored private let d = UserDefaults.standard

    var enabled: Bool { didSet { d.set(enabled, forKey: "widget.enabled"); DesktopWidget.apply() } }
    var size: WidgetSize { didSet { d.set(size.rawValue, forKey: "widget.size") } }
    var showCPU: Bool { didSet { d.set(showCPU, forKey: "widget.cpu") } }
    var showMemory: Bool { didSet { d.set(showMemory, forKey: "widget.memory") } }
    var showDisk: Bool { didSet { d.set(showDisk, forKey: "widget.disk") } }
    var showNetwork: Bool { didSet { d.set(showNetwork, forKey: "widget.network") } }
    var showTyping: Bool { didSet { d.set(showTyping, forKey: "widget.typing") } }
    var showBattery: Bool { didSet { d.set(showBattery, forKey: "widget.battery") } }
    var opacity: Double { didSet { d.set(opacity, forKey: "widget.opacity"); DesktopWidget.apply() } }
    var onTop: Bool { didSet { d.set(onTop, forKey: "widget.onTop"); DesktopWidget.apply() } }

    private init() {
        enabled = d.object(forKey: "widget.enabled") as? Bool ?? false
        size = WidgetSize(rawValue: d.string(forKey: "widget.size") ?? "") ?? .medium
        showCPU = d.object(forKey: "widget.cpu") as? Bool ?? true
        showMemory = d.object(forKey: "widget.memory") as? Bool ?? true
        showDisk = d.object(forKey: "widget.disk") as? Bool ?? true
        showNetwork = d.object(forKey: "widget.network") as? Bool ?? true
        showTyping = d.object(forKey: "widget.typing") as? Bool ?? true
        showBattery = d.object(forKey: "widget.battery") as? Bool ?? true
        opacity = d.object(forKey: "widget.opacity") as? Double ?? 1
        onTop = d.object(forKey: "widget.onTop") as? Bool ?? false
    }

    var origin: NSPoint? {
        get {
            guard d.object(forKey: "widget.x") != nil else { return nil }
            return NSPoint(x: d.double(forKey: "widget.x"), y: d.double(forKey: "widget.y"))
        }
        set {
            if let p = newValue { d.set(p.x, forKey: "widget.x"); d.set(p.y, forKey: "widget.y") }
        }
    }
}

// MARK: - Panel

/// A floating mini window on the desktop. A real WidgetKit widget needs an Xcode-built
/// extension, so this is a borderless, non-activating panel instead.
@MainActor
enum DesktopWidget {
    private static var panel: WidgetPanel?

    /// Called once at launch. Also starts the battery history recorder, which needs to
    /// run in the background whether or not the battery page is open.
    static func startIfEnabled() {
        BatteryHistory.shared.start()
        apply()
    }

    static func apply() {
        let p = WidgetPrefs.shared
        guard p.enabled else {
            if let panel { panel.orderOut(nil); IconAnimator.shared.viewer(false) }
            panel = nil
            return
        }
        if panel == nil {
            let w = WidgetPanel()
            panel = w
            IconAnimator.shared.viewer(true)
            w.place(at: p.origin)
            w.orderFront(nil)
        }
        panel?.alphaValue = CGFloat(min(max(p.opacity, 0.3), 1))
        panel?.level = p.onTop ? .floating : NSWindow.Level(rawValue: Int(CGWindowLevelForKey(.desktopIconWindow)) + 1)
    }

    static func hide() { WidgetPrefs.shared.enabled = false }
}

final class WidgetPanel: NSPanel {
    private var host: WidgetHostView!

    init() {
        super.init(contentRect: NSRect(x: 0, y: 0, width: 240, height: 160), styleMask: [.borderless, .nonactivatingPanel],
                   backing: .buffered, defer: false)
        isFloatingPanel = false
        hidesOnDeactivate = false
        isOpaque = false
        backgroundColor = .clear
        hasShadow = true
        isMovableByWindowBackground = true
        isReleasedWhenClosed = false
        collectionBehavior = [.canJoinAllSpaces, .stationary, .ignoresCycle]

        let fx = NSVisualEffectView()
        fx.material = .hudWindow
        fx.blendingMode = .behindWindow
        fx.state = .active
        fx.wantsLayer = true
        fx.layer?.cornerRadius = 16
        fx.layer?.cornerCurve = .continuous
        fx.layer?.masksToBounds = true
        contentView = fx

        host = WidgetHostView(rootView: AnyView(EmptyView()))
        host.rootView = AnyView(WidgetContent(onSize: { [weak self] s in
            MainActor.assumeIsolated { self?.fit(s) }
        }).environment(AppState.shared))
        host.frame = fx.bounds
        host.autoresizingMask = [.width, .height]
        fx.addSubview(host)
        fit(host.fittingSize)

        NotificationCenter.default.addObserver(forName: NSWindow.didMoveNotification, object: self, queue: .main) { n in
            guard let w = n.object as? NSWindow else { return }
            let o = w.frame.origin
            MainActor.assumeIsolated { WidgetPrefs.shared.origin = o }
        }
    }

    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }

    /// Keeps the top-left corner in place while the content grows or shrinks.
    func fit(_ size: CGSize) {
        guard size.width > 10, size.height > 10 else { return }
        let top = frame.maxY
        let r = NSRect(x: frame.minX, y: top - size.height, width: size.width, height: size.height)
        if r.size != frame.size { setFrame(r, display: true) }
    }

    func place(at origin: NSPoint?) {
        if let origin, NSScreen.screens.contains(where: { $0.visibleFrame.insetBy(dx: -40, dy: -40).contains(NSPoint(x: origin.x + 20, y: origin.y + 20)) }) {
            setFrameOrigin(origin)
        } else if let s = NSScreen.main?.visibleFrame {
            setFrameOrigin(NSPoint(x: s.maxX - frame.width - 24, y: s.maxY - frame.height - 24))
        }
    }
}

/// Lets the whole widget drag the window and shows the right-click menu.
final class WidgetHostView: NSHostingView<AnyView> {
    override var mouseDownCanMoveWindow: Bool { true }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    override func mouseDown(with event: NSEvent) {
        window?.performDrag(with: event)
    }

    override func rightMouseDown(with event: NSEvent) {
        NSMenu.popUpContextMenu(makeMenu(), with: event, for: self)
    }

    override func menu(for event: NSEvent) -> NSMenu? { makeMenu() }

    func makeMenu() -> NSMenu {
        let m = NSMenu()
        m.addItem(WidgetMenuItem(L("打开 Peekaboo", "Open Peekaboo")) { WindowManager.shared.show(nil) })
        m.addItem(WidgetMenuItem(L("小组件设置…", "Widget settings…")) { WindowManager.shared.show(.settings) })
        m.addItem(.separator())
        let top = WidgetMenuItem(L("总在最前", "Always on top")) { WidgetPrefs.shared.onTop.toggle() }
        top.state = MainActor.assumeIsolated { WidgetPrefs.shared.onTop } ? .on : .off
        m.addItem(top)
        m.addItem(WidgetMenuItem(L("隐藏小组件", "Hide widget")) { DesktopWidget.hide() })
        return m
    }
}

private final class WidgetMenuItem: NSMenuItem {
    private let handler: @MainActor () -> Void
    init(_ title: String, _ handler: @escaping @MainActor () -> Void) {
        self.handler = handler
        super.init(title: title, action: #selector(fire), keyEquivalent: "")
        target = self
    }
    required init(coder: NSCoder) { fatalError() }
    @objc private func fire() { MainActor.assumeIsolated { handler() } }
}

// MARK: - Content

private struct WidgetSizeKey: PreferenceKey {
    static var defaultValue: CGSize = .zero
    static func reduce(value: inout CGSize, nextValue: () -> CGSize) { value = nextValue() }
}

struct WidgetContent: View {
    let onSize: (CGSize) -> Void
    @Environment(AppState.self) private var app

    var body: some View {
        let p = WidgetPrefs.shared
        let m = app.monitor
        let size = p.size
        let medium = size == .medium
        let icon = app.settings.menuIcon == .none ? MenuIcon.ghost : app.settings.menuIcon
        VStack(alignment: .leading, spacing: medium ? 10 : 7) {
            HStack(spacing: 6) {
                AnimatedIconView(icon: icon, height: medium ? 20 : 15)
                Text("Peekaboo").font(.system(size: medium ? 13 : 11, weight: .semibold, design: .rounded))
                Spacer(minLength: 0)
                if p.showBattery, let b = BatteryDetailReader.percentNow {
                    Label(Fmt.pct(b), systemImage: m.battery?.charging == true ? "battery.100percent.bolt" : "battery.75percent")
                        .font(.system(size: medium ? 11 : 10)).foregroundStyle(.secondary).monospacedDigit()
                }
            }
            if medium && (p.showCPU || p.showMemory) {
                HStack(spacing: 14) {
                    if p.showCPU { ring("CPU", m.cpu.total, Palette.load(m.cpu.total)) }
                    if p.showMemory { ring(L("内存", "Memory"), m.memory.usedFraction, m.memory.pressureLevel >= 4 ? Palette.danger : Palette.mem) }
                }
                .frame(maxWidth: .infinity)
            } else {
                if p.showCPU { bar("CPU", m.cpu.total, Palette.load(m.cpu.total)) }
                if p.showMemory { bar(L("内存", "Mem"), m.memory.usedFraction, m.memory.pressureLevel >= 4 ? Palette.danger : Palette.mem) }
            }
            if p.showDisk {
                line("internaldrive", L("磁盘可用", "Disk free"), Fmt.bytes(m.disk.available), m.disk.usedFraction > 0.9 ? Palette.danger : Palette.disk, medium)
            }
            if p.showNetwork {
                HStack(spacing: 8) {
                    // The small widget has no room for "13.5 KB/s", so it uses "13K/s".
                    Label(medium ? Fmt.rate(m.netIn) : NetSampler.compact(m.netIn), systemImage: "arrow.down").foregroundStyle(Palette.netIn)
                    Spacer(minLength: 0)
                    Label(medium ? Fmt.rate(m.netOut) : NetSampler.compact(m.netOut), systemImage: "arrow.up").foregroundStyle(Palette.netOut)
                }
                .font(.system(size: medium ? 11.5 : 10, weight: .medium)).monospacedDigit().lineLimit(1)
            }
            if p.showTyping, TypingMonitor.shared.running {
                line("keyboard", L("今日按键", "Keys today"), "\(TypingMonitor.shared.today.total)", Palette.mem, medium)
            }
        }
        .padding(medium ? 14 : 10)
        .frame(width: size.width, alignment: .leading)
        .fixedSize(horizontal: false, vertical: true)
        .background(GeometryReader { g in Color.clear.preference(key: WidgetSizeKey.self, value: g.size) })
        .onPreferenceChange(WidgetSizeKey.self) { onSize($0) }
    }

    private func ring(_ title: String, _ v: Double, _ c: Color) -> some View {
        VStack(spacing: 4) {
            Ring(value: v, color: c, lineWidth: 6, label: Fmt.pct(v)).frame(width: 66, height: 66)
            Text(title).font(.caption2).foregroundStyle(.secondary)
        }
    }

    private func bar(_ title: String, _ v: Double, _ c: Color) -> some View {
        VStack(spacing: 3) {
            HStack {
                Text(title).foregroundStyle(.secondary)
                Spacer()
                Text(Fmt.pct(v)).monospacedDigit()
            }
            .font(.system(size: 10.5, weight: .medium))
            SizeBar(fraction: v, color: c)
        }
    }

    private func line(_ symbol: String, _ title: String, _ value: String, _ c: Color, _ medium: Bool) -> some View {
        HStack(spacing: 6) {
            Image(systemName: symbol).foregroundStyle(c).frame(width: 14)
            Text(title).foregroundStyle(.secondary).lineLimit(1)
            Spacer(minLength: 4)
            Text(value).monospacedDigit().lineLimit(1)
        }
        .font(.system(size: medium ? 11.5 : 10, weight: .medium))
    }
}

// MARK: - Settings card

struct WidgetSettingsCard: View {
    var body: some View {
        @Bindable var p = WidgetPrefs.shared
        Card(L("桌面小组件", "Desktop widget"), symbol: "rectangle.on.rectangle") {
            Toggle(L("在桌面显示一个悬浮小窗", "Show a small floating window on the desktop"), isOn: $p.enabled)
            Text(L("显示动画图标和实时数据，可以拖到任意位置；右键可以打开 Peekaboo 或隐藏。默认待在桌面图标上方、其他窗口下方。",
                   "Shows the animated icon and live numbers. Drag it anywhere; right-click to open Peekaboo or hide it. By default it sits above desktop icons and below other windows."))
                .font(.caption).foregroundStyle(.secondary)
            if p.enabled {
                Picker(L("大小", "Size"), selection: $p.size) {
                    ForEach(WidgetSize.allCases) { Text($0.title).tag($0) }
                }
                .pickerStyle(.segmented).frame(maxWidth: 260)
                HStack(spacing: 14) {
                    Toggle("CPU", isOn: $p.showCPU)
                    Toggle(L("内存", "Memory"), isOn: $p.showMemory)
                    Toggle(L("磁盘可用", "Free disk"), isOn: $p.showDisk)
                    Toggle(L("网速", "Network"), isOn: $p.showNetwork)
                    Toggle(L("电池", "Battery"), isOn: $p.showBattery)
                    Toggle(L("今日按键", "Keys today"), isOn: $p.showTyping)
                }
                .toggleStyle(.checkbox)
                HStack {
                    Text(L("不透明度", "Opacity"))
                    Slider(value: $p.opacity, in: 0.3...1).frame(maxWidth: 220)
                    Text(Fmt.pct(p.opacity)).monospacedDigit().foregroundStyle(.secondary)
                }
                Toggle(L("总在最前（盖在其他窗口上面）", "Always on top (above other windows)"), isOn: $p.onTop)
            }
        }
    }
}
