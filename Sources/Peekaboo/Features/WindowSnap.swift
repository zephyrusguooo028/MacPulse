import AppKit
import ApplicationServices
import Carbon
import Observation

/// One window layout. `unit` is the target region as a fraction of the screen's usable
/// area, with the origin at the top left (the Accessibility API's coordinate system).
enum SnapAction: String, CaseIterable, Identifiable, Codable {
    case leftHalf, rightHalf, topHalf, bottomHalf
    case topLeft, topRight, bottomLeft, bottomRight
    case leftThird, centerThird, rightThird, leftTwoThirds, rightTwoThirds
    case maximize, center, restore, previousDisplay, nextDisplay

    var id: String { rawValue }

    var title: String {
        switch self {
        case .leftHalf: return L("左半屏", "Left half")
        case .rightHalf: return L("右半屏", "Right half")
        case .topHalf: return L("上半屏", "Top half")
        case .bottomHalf: return L("下半屏", "Bottom half")
        case .topLeft: return L("左上角", "Top left")
        case .topRight: return L("右上角", "Top right")
        case .bottomLeft: return L("左下角", "Bottom left")
        case .bottomRight: return L("右下角", "Bottom right")
        case .leftThird: return L("左 1/3", "Left third")
        case .centerThird: return L("中 1/3", "Center third")
        case .rightThird: return L("右 1/3", "Right third")
        case .leftTwoThirds: return L("左 2/3", "Left two thirds")
        case .rightTwoThirds: return L("右 2/3", "Right two thirds")
        case .maximize: return L("铺满", "Maximize")
        case .center: return L("居中", "Center")
        case .restore: return L("还原", "Restore")
        case .previousDisplay: return L("移到上一块屏幕", "Previous display")
        case .nextDisplay: return L("移到下一块屏幕", "Next display")
        }
    }

    var unit: CGRect? {
        let t: CGFloat = 1.0 / 3
        switch self {
        case .leftHalf: return CGRect(x: 0, y: 0, width: 0.5, height: 1)
        case .rightHalf: return CGRect(x: 0.5, y: 0, width: 0.5, height: 1)
        case .topHalf: return CGRect(x: 0, y: 0, width: 1, height: 0.5)
        case .bottomHalf: return CGRect(x: 0, y: 0.5, width: 1, height: 0.5)
        case .topLeft: return CGRect(x: 0, y: 0, width: 0.5, height: 0.5)
        case .topRight: return CGRect(x: 0.5, y: 0, width: 0.5, height: 0.5)
        case .bottomLeft: return CGRect(x: 0, y: 0.5, width: 0.5, height: 0.5)
        case .bottomRight: return CGRect(x: 0.5, y: 0.5, width: 0.5, height: 0.5)
        case .leftThird: return CGRect(x: 0, y: 0, width: t, height: 1)
        case .centerThird: return CGRect(x: t, y: 0, width: t, height: 1)
        case .rightThird: return CGRect(x: 2 * t, y: 0, width: t, height: 1)
        case .leftTwoThirds: return CGRect(x: 0, y: 0, width: 2 * t, height: 1)
        case .rightTwoThirds: return CGRect(x: t, y: 0, width: 2 * t, height: 1)
        case .maximize: return CGRect(x: 0, y: 0, width: 1, height: 1)
        case .center, .restore, .previousDisplay, .nextDisplay: return nil
        }
    }

    /// Drawn instead of a layout preview for actions without a fixed region.
    var symbol: String? {
        switch self {
        case .restore: return "arrow.uturn.backward"
        case .previousDisplay: return "rectangle.lefthalf.inset.filled.arrow.left"
        case .nextDisplay: return "rectangle.righthalf.inset.filled.arrow.right"
        default: return nil
        }
    }

    /// Magnet's defaults: ⌃⌥ plus a key, ⌃⌥⌘ ←/→ for displays.
    var defaultHotkey: HotkeySpec {
        let co = UInt32(controlKey | optionKey)
        func k(_ code: Int, _ label: String, _ m: UInt32 = co) -> HotkeySpec { HotkeySpec(keyCode: UInt32(code), modifiers: m, label: label) }
        switch self {
        case .leftHalf: return k(kVK_LeftArrow, "←")
        case .rightHalf: return k(kVK_RightArrow, "→")
        case .topHalf: return k(kVK_UpArrow, "↑")
        case .bottomHalf: return k(kVK_DownArrow, "↓")
        case .topLeft: return k(kVK_ANSI_U, "U")
        case .topRight: return k(kVK_ANSI_I, "I")
        case .bottomLeft: return k(kVK_ANSI_J, "J")
        case .bottomRight: return k(kVK_ANSI_K, "K")
        case .leftThird: return k(kVK_ANSI_D, "D")
        case .centerThird: return k(kVK_ANSI_F, "F")
        case .rightThird: return k(kVK_ANSI_G, "G")
        case .leftTwoThirds: return k(kVK_ANSI_E, "E")
        case .rightTwoThirds: return k(kVK_ANSI_T, "T")
        case .maximize: return k(kVK_Return, "↩")
        case .center: return k(kVK_ANSI_C, "C")
        case .restore: return k(kVK_Delete, "⌫")
        case .previousDisplay: return k(kVK_LeftArrow, "←", co | UInt32(cmdKey))
        case .nextDisplay: return k(kVK_RightArrow, "→", co | UInt32(cmdKey))
        }
    }

    /// Target frame for a window currently at `frame`, inside `area` (both top-left origin).
    func target(for frame: CGRect, in area: CGRect) -> CGRect? {
        if let u = unit {
            return CGRect(x: area.minX + area.width * u.minX, y: area.minY + area.height * u.minY,
                          width: area.width * u.width, height: area.height * u.height).integral
        }
        if self == .center {
            let w = min(frame.width, area.width), h = min(frame.height, area.height)
            return CGRect(x: area.midX - w / 2, y: area.midY - h / 2, width: w, height: h).integral
        }
        return nil
    }
}

/// Moves and resizes other apps' windows through the Accessibility API, from global
/// shortcuts, from the Window layout page, and by dragging a window to a screen edge.
@MainActor
@Observable
final class WindowSnapper {
    static let shared = WindowSnapper()
    private static let baseID: UInt32 = 100

    var hotkeysEnabled: Bool { didSet { UserDefaults.standard.set(hotkeysEnabled, forKey: "snap.hotkeys"); applyHotkeys() } }
    var dragEnabled: Bool { didSet { UserDefaults.standard.set(dragEnabled, forKey: "snap.drag"); applyDrag() } }
    /// Layout buttons in the menu bar popover, like clicking Magnet's menu bar icon.
    var showInPopover: Bool { didSet { UserDefaults.standard.set(showInPopover, forKey: "snap.popover") } }
    /// User overrides; a stored nil turns that shortcut off.
    private(set) var custom: [String: HotkeySpec?] = [:]
    /// Shortcuts another app already owns.
    private(set) var failed: Set<SnapAction> = []
    private(set) var trusted = AXIsProcessTrusted()

    @ObservationIgnored private var lastExternalPID: pid_t?
    @ObservationIgnored private var history: [(window: AXUIElement, original: CGRect, snapped: CGRect)] = []
    @ObservationIgnored private var trustTimer: Timer?

    private init() {
        let d = UserDefaults.standard
        hotkeysEnabled = d.object(forKey: "snap.hotkeys") as? Bool ?? true
        dragEnabled = d.object(forKey: "snap.drag") as? Bool ?? true
        showInPopover = d.object(forKey: "snap.popover") as? Bool ?? true
        if let data = d.data(forKey: "snap.custom"), let c = try? JSONDecoder().decode([String: HotkeySpec?].self, from: data) { custom = c }
        NSWorkspace.shared.notificationCenter.addObserver(forName: NSWorkspace.didActivateApplicationNotification, object: nil, queue: .main) { n in
            let pid = (n.userInfo?[NSWorkspace.applicationUserInfoKey] as? NSRunningApplication)?.processIdentifier
            MainActor.assumeIsolated { if let pid, pid != getpid() { WindowSnapper.shared.lastExternalPID = pid } }
        }
        if let f = NSWorkspace.shared.frontmostApplication, f.processIdentifier != getpid() { lastExternalPID = f.processIdentifier }
    }

    func start() {
        applyHotkeys()
        applyDrag()
        // Accessibility takes effect without a restart; notice when it is switched on.
        trustTimer = Timer.scheduledTimer(withTimeInterval: 2, repeats: true) { _ in
            MainActor.assumeIsolated {
                let s = WindowSnapper.shared
                let now = AXIsProcessTrusted()
                if now != s.trusted { s.trusted = now; s.applyDrag() }
            }
        }
    }

    /// `--snap-test <file>`: snaps the front TextEdit window through a few layouts and writes the frames.
    static func selfTest(to path: String) {
        var out = ["trusted=\(AXIsProcessTrusted())", "screens=\(screenAreas())"]
        if let te = NSRunningApplication.runningApplications(withBundleIdentifier: "com.apple.TextEdit").first,
           let w = shared.focusedWindow(te.processIdentifier) {
            out.append("start=\(frame(of: w).map { "\($0)" } ?? "nil")")
            for a in [SnapAction.leftHalf, .topRight, .rightThird, .center, .maximize, .restore] {
                shared.apply(a, to: w)
                Thread.sleep(forTimeInterval: 0.3)
                out.append("\(a.rawValue)=\(frame(of: w).map { "\($0)" } ?? "nil")")
            }
        } else { out.append("no TextEdit window") }
        try? out.joined(separator: "\n").write(toFile: path, atomically: true, encoding: .utf8)
    }

    /// The last app other than Peekaboo that was in front.
    var lastExternalApp: NSRunningApplication? { lastExternalPID.flatMap { NSRunningApplication(processIdentifier: $0) } }

    /// The app a click on a layout button will act on.
    var targetAppName: String? {
        let front = NSWorkspace.shared.frontmostApplication
        if let front, front.processIdentifier != getpid() { return front.localizedName }
        return lastExternalPID.flatMap { NSRunningApplication(processIdentifier: $0)?.localizedName }
    }

    // MARK: Shortcuts

    func hotkey(_ a: SnapAction) -> HotkeySpec? {
        if let c = custom[a.rawValue] { return c }
        return a.defaultHotkey
    }

    func setHotkey(_ a: SnapAction, _ spec: HotkeySpec?) {
        custom[a.rawValue] = .some(spec)
        // The same keys can only do one thing: take them away from any other layout.
        if let spec {
            for other in SnapAction.allCases where other != a && hotkey(other).map({ $0.keyCode == spec.keyCode && $0.modifiers == spec.modifiers }) == true { custom[other.rawValue] = .some(nil) }
        }
        saveCustom()
        applyHotkeys()
    }

    func resetHotkeys() {
        custom = [:]
        saveCustom()
        applyHotkeys()
    }

    private func saveCustom() {
        if let data = try? JSONEncoder().encode(custom) { UserDefaults.standard.set(data, forKey: "snap.custom") }
    }

    func applyHotkeys() {
        var bad: Set<SnapAction> = []
        for (i, a) in SnapAction.allCases.enumerated() {
            let id = Self.baseID + UInt32(i)
            if hotkeysEnabled, let k = hotkey(a) {
                if !Hotkeys.shared.register(id: id, keyCode: k.keyCode, modifiers: k.modifiers, action: { WindowSnapper.shared.perform(a) }) {
                    bad.insert(a)
                }
            } else {
                Hotkeys.shared.unregister(id: id)
            }
        }
        failed = bad
    }

    // MARK: Moving windows

    /// From a shortcut: the focused window of the frontmost app. From the page
    /// (`fromPage`), the app that was in front before Peekaboo.
    func perform(_ a: SnapAction, fromPage: Bool = false) {
        guard AXIsProcessTrusted() else {
            trusted = false
            PermissionKind.accessibility.request()
            return
        }
        let front = NSWorkspace.shared.frontmostApplication?.processIdentifier
        if !fromPage, front == getpid() {
            if let w = NSApp.keyWindow ?? NSApp.mainWindow { performOwn(a, w) }
            return
        }
        guard let pid = fromPage || front == nil ? lastExternalPID : front, let w = focusedWindow(pid) else { NSSound.beep(); return }
        apply(a, to: w)
        if fromPage { NSRunningApplication(processIdentifier: pid)?.activate() }
    }

    func focusedWindow(_ pid: pid_t) -> AXUIElement? {
        let app = AXUIElementCreateApplication(pid)
        AXUIElementSetMessagingTimeout(app, 0.5)
        for attr in [kAXFocusedWindowAttribute, kAXMainWindowAttribute] {
            var v: CFTypeRef?
            if AXUIElementCopyAttributeValue(app, attr as CFString, &v) == .success, let v, CFGetTypeID(v) == AXUIElementGetTypeID() {
                return (v as! AXUIElement)
            }
        }
        return nil
    }

    func apply(_ a: SnapAction, to w: AXUIElement, original: CGRect? = nil) {
        guard let frame = Self.frame(of: w) else { return }
        let screens = Self.screenAreas()
        guard !screens.isEmpty else { return }
        let i = Self.screenIndex(for: frame, in: screens)
        let area = screens[i]
        var target: CGRect?
        switch a {
        case .restore:
            guard let h = history.firstIndex(where: { CFEqual($0.window, w) }) else { NSSound.beep(); return }
            target = history[h].original
            history.remove(at: h)
            Self.setFrame(w, target!)
            return
        case .previousDisplay, .nextDisplay:
            guard screens.count > 1 else { NSSound.beep(); return }
            let j = (i + (a == .nextDisplay ? 1 : screens.count - 1)) % screens.count
            let to = screens[j]
            let w2 = min(frame.width, to.width), h2 = min(frame.height, to.height)
            let rx = area.width > frame.width ? (frame.minX - area.minX) / (area.width - frame.width) : 0
            let ry = area.height > frame.height ? (frame.minY - area.minY) / (area.height - frame.height) : 0
            target = CGRect(x: to.minX + (to.width - w2) * min(max(rx, 0), 1), y: to.minY + (to.height - h2) * min(max(ry, 0), 1),
                            width: w2, height: h2).integral
        default:
            target = a.target(for: frame, in: area)
        }
        guard let target else { return }
        remember(w, original: original ?? frame, snapped: target)
        Self.setFrame(w, target)
    }

    private func remember(_ w: AXUIElement, original: CGRect, snapped: CGRect) {
        if let i = history.firstIndex(where: { CFEqual($0.window, w) }) {
            // Snapping a snapped window again keeps the size it had before the first snap.
            let keep = history[i].original
            history[i] = (w, keep, snapped)
        } else {
            history.append((w, original, snapped))
            if history.count > 60 { history.removeFirst() }
        }
    }

    /// Peekaboo's own windows can't be driven through Accessibility from inside the app.
    private func performOwn(_ a: SnapAction, _ win: NSWindow) {
        guard let screen = win.screen ?? NSScreen.main else { return }
        let v = screen.visibleFrame
        let primary = NSScreen.screens.first?.frame.height ?? v.maxY
        let area = CGRect(x: v.minX, y: primary - v.maxY, width: v.width, height: v.height)
        let f = win.frame
        let cur = CGRect(x: f.minX, y: primary - f.maxY, width: f.width, height: f.height)
        guard let t = a.target(for: cur, in: area) else { return }
        win.setFrame(NSRect(x: t.minX, y: primary - t.maxY, width: t.width, height: t.height), display: true, animate: true)
    }

    // MARK: Drag to a screen edge

    @ObservationIgnored private var monitor: Any?
    @ObservationIgnored private var drag: (window: AXUIElement, start: CGRect, moved: Bool)?
    @ObservationIgnored private var zone: SnapAction?
    @ObservationIgnored private var lastPoll = Date.distantPast
    @ObservationIgnored private let preview = SnapPreview()

    private func applyDrag() {
        let want = dragEnabled && AXIsProcessTrusted()
        if want, monitor == nil {
            monitor = NSEvent.addGlobalMonitorForEvents(matching: [.leftMouseDown, .leftMouseDragged, .leftMouseUp]) { e in
                let type = e.type
                MainActor.assumeIsolated { WindowSnapper.shared.handle(type) }
            }
        } else if !want, let m = monitor {
            NSEvent.removeMonitor(m)
            monitor = nil
            endDrag()
        }
    }

    private func handle(_ type: NSEvent.EventType) {
        switch type {
        case .leftMouseDown:
            endDrag()
            guard let w = Self.window(at: Self.axPoint(NSEvent.mouseLocation)), let f = Self.frame(of: w) else { return }
            drag = (w, f, false)
        case .leftMouseDragged:
            guard var d = drag else { return }
            let now = Date()
            guard now.timeIntervalSince(lastPoll) > 0.03 else { return }
            lastPoll = now
            if !d.moved {
                guard let f = Self.frame(of: d.window) else { drag = nil; return }
                // Moving, not resizing: position changed but the size didn't.
                guard f.origin != d.start.origin else { return }
                guard abs(f.width - d.start.width) < 1, abs(f.height - d.start.height) < 1 else { drag = nil; return }
                d.moved = true
                // Dragging a snapped window away gives it back its earlier size, like Magnet.
                if let i = history.firstIndex(where: { CFEqual($0.window, d.window) }), history[i].snapped.size == f.size {
                    let orig = history[i].original
                    history.remove(at: i)
                    Self.setSize(d.window, orig.size)
                    d.start = CGRect(origin: f.origin, size: orig.size)
                }
                drag = d
            }
            zone = Self.zone(at: NSEvent.mouseLocation)
            if let z = zone, let area = Self.areaUnderCursor(), let t = z.target(for: d.start, in: area) {
                preview.show(axRect: t)
            } else {
                preview.hide()
            }
        case .leftMouseUp:
            if let d = drag, d.moved, let z = Self.zone(at: NSEvent.mouseLocation) {
                // Let the window server finish the move before resizing.
                let w = d.window, start = d.start
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.05) {
                    WindowSnapper.shared.apply(z, to: w, original: start)
                }
            }
            endDrag()
        default: break
        }
    }

    private func endDrag() {
        drag = nil
        zone = nil
        preview.hide()
    }

    /// Edges snap to halves, corners to quarters, the top to fill the screen,
    /// the bottom edge to thirds.
    static func zone(at p: NSPoint) -> SnapAction? {
        guard let s = NSScreen.screens.first(where: { NSMouseInRect(p, $0.frame, false) }) else { return nil }
        let f = s.frame
        let edge: CGFloat = 4, corner: CGFloat = 90
        let left = p.x <= f.minX + edge, right = p.x >= f.maxX - edge - 1
        let top = p.y >= f.maxY - edge - 1, bottom = p.y <= f.minY + edge
        let nearTop = p.y >= f.maxY - corner, nearBottom = p.y <= f.minY + corner
        let nearLeft = p.x <= f.minX + corner, nearRight = p.x >= f.maxX - corner
        if left { return nearTop ? .topLeft : nearBottom ? .bottomLeft : .leftHalf }
        if right { return nearTop ? .topRight : nearBottom ? .bottomRight : .rightHalf }
        if top { return nearLeft ? .topLeft : nearRight ? .topRight : .maximize }
        if bottom {
            if nearLeft { return .bottomLeft }
            if nearRight { return .bottomRight }
            let x = (p.x - f.minX) / f.width
            return x < 1.0 / 3 ? .leftThird : x < 2.0 / 3 ? .centerThird : .rightThird
        }
        return nil
    }

    // MARK: Accessibility helpers (top-left origin, global)

    static var primaryHeight: CGFloat { NSScreen.screens.first?.frame.height ?? 0 }

    static func axPoint(_ p: NSPoint) -> CGPoint { CGPoint(x: p.x, y: primaryHeight - p.y) }

    static func axRect(_ r: NSRect) -> CGRect { CGRect(x: r.minX, y: primaryHeight - r.maxY, width: r.width, height: r.height) }

    static func appKitRect(_ r: CGRect) -> NSRect { NSRect(x: r.minX, y: primaryHeight - r.maxY, width: r.width, height: r.height) }

    /// Usable area (without menu bar and Dock) of each screen.
    static func screenAreas() -> [CGRect] { NSScreen.screens.map { axRect($0.visibleFrame) } }

    static func areaUnderCursor() -> CGRect? {
        let p = NSEvent.mouseLocation
        return NSScreen.screens.first(where: { NSMouseInRect(p, $0.frame, false) }).map { axRect($0.visibleFrame) }
    }

    static func screenIndex(for frame: CGRect, in screens: [CGRect]) -> Int {
        var best = 0, bestArea: CGFloat = -1
        for (i, s) in screens.enumerated() {
            let r = s.intersection(frame)
            let a = r.isNull ? 0 : r.width * r.height
            if a > bestArea { best = i; bestArea = a }
        }
        return best
    }

    static func window(at p: CGPoint) -> AXUIElement? {
        let sys = AXUIElementCreateSystemWide()
        AXUIElementSetMessagingTimeout(sys, 0.2)
        var el: AXUIElement?
        guard AXUIElementCopyElementAtPosition(sys, Float(p.x), Float(p.y), &el) == .success, let el else { return nil }
        var pid: pid_t = 0
        AXUIElementGetPid(el, &pid)
        guard pid != getpid() else { return nil }
        var role: CFTypeRef?
        AXUIElementCopyAttributeValue(el, kAXRoleAttribute as CFString, &role)
        if (role as? String) == kAXWindowRole { return el }
        var w: CFTypeRef?
        guard AXUIElementCopyAttributeValue(el, kAXWindowAttribute as CFString, &w) == .success, let w, CFGetTypeID(w) == AXUIElementGetTypeID() else { return nil }
        return (w as! AXUIElement)
    }

    static func frame(of w: AXUIElement) -> CGRect? {
        var p: CFTypeRef?, s: CFTypeRef?
        guard AXUIElementCopyAttributeValue(w, kAXPositionAttribute as CFString, &p) == .success,
              AXUIElementCopyAttributeValue(w, kAXSizeAttribute as CFString, &s) == .success,
              let p, let s, CFGetTypeID(p) == AXValueGetTypeID(), CFGetTypeID(s) == AXValueGetTypeID() else { return nil }
        var pt = CGPoint.zero, sz = CGSize.zero
        AXValueGetValue(p as! AXValue, .cgPoint, &pt)
        AXValueGetValue(s as! AXValue, .cgSize, &sz)
        return CGRect(origin: pt, size: sz)
    }

    static func setSize(_ w: AXUIElement, _ size: CGSize) {
        var sz = size
        if let v = AXValueCreate(.cgSize, &sz) { AXUIElementSetAttributeValue(w, kAXSizeAttribute as CFString, v) }
    }

    static func setFrame(_ w: AXUIElement, _ r: CGRect) {
        // Apps with "enhanced user interface" on animate every change, which makes
        // the two-step move below visibly slow; switch it off for the move.
        var pid: pid_t = 0
        AXUIElementGetPid(w, &pid)
        let app = AXUIElementCreateApplication(pid)
        var enhanced: CFTypeRef?
        let wasEnhanced = AXUIElementCopyAttributeValue(app, "AXEnhancedUserInterface" as CFString, &enhanced) == .success && (enhanced as? Bool) == true
        if wasEnhanced { AXUIElementSetAttributeValue(app, "AXEnhancedUserInterface" as CFString, kCFBooleanFalse) }
        var pt = r.origin
        // Size, then position, then size again: moving between screens of different
        // sizes can clip the first resize.
        setSize(w, r.size)
        if let v = AXValueCreate(.cgPoint, &pt) { AXUIElementSetAttributeValue(w, kAXPositionAttribute as CFString, v) }
        setSize(w, r.size)
        if wasEnhanced { AXUIElementSetAttributeValue(app, "AXEnhancedUserInterface" as CFString, kCFBooleanTrue) }
    }
}

/// The translucent rectangle that shows where a dragged window will land.
@MainActor
private final class SnapPreview {
    private var panel: NSPanel?
    private var shown = false

    func show(axRect: CGRect) {
        let frame = WindowSnapper.appKitRect(axRect).insetBy(dx: 4, dy: 4)
        let p = panel ?? make()
        if !shown {
            p.setFrame(frame, display: true)
            p.alphaValue = 0
            p.orderFrontRegardless()
            shown = true
            NSAnimationContext.runAnimationGroup { $0.duration = 0.12; p.animator().alphaValue = 1 }
        } else if p.frame != frame {
            NSAnimationContext.runAnimationGroup { ctx in
                ctx.duration = 0.12
                p.animator().setFrame(frame, display: true)
            }
        }
    }

    func hide() {
        guard shown, let p = panel else { return }
        shown = false
        p.orderOut(nil)
    }

    private func make() -> NSPanel {
        let p = NSPanel(contentRect: .zero, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        p.isOpaque = false
        p.backgroundColor = .clear
        p.hasShadow = false
        p.ignoresMouseEvents = true
        p.level = .floating
        p.collectionBehavior = [.canJoinAllSpaces, .transient, .ignoresCycle]
        let v = NSView()
        v.wantsLayer = true
        let c = NSColor(Palette.theme)
        v.layer?.backgroundColor = c.withAlphaComponent(0.22).cgColor
        v.layer?.borderColor = c.withAlphaComponent(0.85).cgColor
        v.layer?.borderWidth = 2
        v.layer?.cornerRadius = 12
        p.contentView = v
        panel = p
        return p
    }
}
