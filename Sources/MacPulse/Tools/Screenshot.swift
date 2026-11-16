import AppKit
import SwiftUI
import ScreenCaptureKit
import CoreImage
import CoreImage.CIFilterBuiltins

// MARK: - Annotations

enum ShotTool: String, CaseIterable, Identifiable {
    case rect, arrow, pen, text, mosaic
    var id: String { rawValue }
    var symbol: String {
        switch self {
        case .rect: return "rectangle"
        case .arrow: return "arrow.up.right"
        case .pen: return "scribble"
        case .text: return "textformat"
        case .mosaic: return "square.grid.3x3.fill"
        }
    }
    var title: String {
        switch self {
        case .rect: return "矩形"
        case .arrow: return "箭头"
        case .pen: return "画笔"
        case .text: return "文字"
        case .mosaic: return "马赛克"
        }
    }
}

struct ShotMark: Identifiable {
    enum Kind { case rect(CGRect), arrow(CGPoint, CGPoint), pen([CGPoint]), text(CGPoint, String), mosaic(CGRect) }
    let id = UUID()
    var kind: Kind
    var color: Color
    var width: CGFloat
}

enum ShotPalette {
    static let colors: [Color] = [
        Color(red: 0.95, green: 0.25, blue: 0.25), Color(red: 1.0, green: 0.78, blue: 0.1),
        Color(red: 0.2, green: 0.78, blue: 0.4), Color(red: 0.2, green: 0.55, blue: 1.0), .white, .black,
    ]
}

/// Draws marks; `origin` is subtracted so the same code renders the overlay and the export.
@MainActor
func drawMarks(_ marks: [ShotMark], in ctx: GraphicsContext, origin: CGPoint, pixelated: Image?, fullSize: CGSize) {
    func o(_ p: CGPoint) -> CGPoint { CGPoint(x: p.x - origin.x, y: p.y - origin.y) }
    func o(_ r: CGRect) -> CGRect { r.offsetBy(dx: -origin.x, dy: -origin.y) }
    for m in marks {
        let style = StrokeStyle(lineWidth: m.width, lineCap: .round, lineJoin: .round)
        switch m.kind {
        case .rect(let r):
            ctx.stroke(Path(roundedRect: o(r), cornerRadius: 2), with: .color(m.color), style: style)
        case .arrow(let a, let b):
            let p1 = o(a), p2 = o(b)
            let angle = atan2(p2.y - p1.y, p2.x - p1.x)
            let head = max(10, m.width * 4)
            var shaft = Path(); shaft.move(to: p1)
            shaft.addLine(to: CGPoint(x: p2.x - cos(angle) * head * 0.6, y: p2.y - sin(angle) * head * 0.6))
            ctx.stroke(shaft, with: .color(m.color), style: style)
            var tip = Path()
            tip.move(to: p2)
            tip.addLine(to: CGPoint(x: p2.x - cos(angle - 0.45) * head, y: p2.y - sin(angle - 0.45) * head))
            tip.addLine(to: CGPoint(x: p2.x - cos(angle + 0.45) * head, y: p2.y - sin(angle + 0.45) * head))
            tip.closeSubpath()
            ctx.fill(tip, with: .color(m.color))
        case .pen(let pts):
            guard let f = pts.first else { continue }
            var p = Path(); p.move(to: o(f))
            for q in pts.dropFirst() { p.addLine(to: o(q)) }
            ctx.stroke(p, with: .color(m.color), style: style)
        case .text(let at, let s):
            ctx.draw(Text(s).font(.system(size: 12 + m.width * 3, weight: .semibold)).foregroundColor(m.color), at: o(at), anchor: .topLeading)
        case .mosaic(let r):
            guard let pixelated else { continue }
            var c = ctx
            c.clip(to: Path(o(r)))
            c.draw(pixelated, in: CGRect(origin: CGPoint(x: -origin.x, y: -origin.y), size: fullSize))
        }
    }
}

// MARK: - Capture session (one per screen)

@MainActor
@Observable
final class ShotSession {
    enum Phase { case selecting, editing }

    let screen: NSScreen
    let image: CGImage
    let pixelated: CGImage?
    let windowRects: [CGRect]       // local, top-left origin, front to back
    var phase: Phase = .selecting
    var selection: CGRect?
    var hover: CGRect?
    var dragStart: CGPoint?
    var moveOffset: CGPoint?
    var marks: [ShotMark] = []
    var draft: ShotMark?
    var tool: ShotTool?
    var color: Color = ShotPalette.colors[0]
    var width: CGFloat = 3
    var textAt: CGPoint?
    var textValue = ""
    var mouse: CGPoint = .zero

    var size: CGSize { screen.frame.size }

    init(screen: NSScreen, image: CGImage, windowRects: [CGRect]) {
        self.screen = screen
        self.image = image
        self.windowRects = windowRects
        let ci = CIImage(cgImage: image)
        let f = CIFilter.pixellate()
        f.inputImage = ci
        f.scale = Float(max(12, CGFloat(image.width) / screen.frame.width * 10))
        f.center = .zero
        self.pixelated = f.outputImage.flatMap { CIContext().createCGImage($0.cropped(to: ci.extent), from: ci.extent) }
    }

    func windowAt(_ p: CGPoint) -> CGRect? { windowRects.first { $0.contains(p) } }

    /// The final image: the selection with marks burned in, at full pixel resolution.
    func export() -> NSImage? {
        guard let sel = selection?.integral, sel.width > 1, sel.height > 1 else { return nil }
        let full = Image(decorative: image, scale: 1).resizable()
        let pix = pixelated.map { Image(decorative: $0, scale: 1).resizable() }
        let marks = self.marks
        let size = self.size
        let view = ZStack(alignment: .topLeading) {
            full.frame(width: size.width, height: size.height).offset(x: -sel.minX, y: -sel.minY)
            Canvas { ctx, _ in drawMarks(marks, in: ctx, origin: sel.origin, pixelated: pix, fullSize: size) }
                .frame(width: sel.width, height: sel.height)
        }
        .frame(width: sel.width, height: sel.height, alignment: .topLeading)
        .clipped()
        let r = ImageRenderer(content: view)
        r.scale = CGFloat(image.width) / size.width
        guard let cg = r.cgImage else { return nil }
        return NSImage(cgImage: cg, size: sel.size)
    }
}

// MARK: - Controller

@MainActor
final class ScreenshotController {
    static let shared = ScreenshotController()
    private var windows: [NSWindow] = []
    private var sessions: [ShotSession] = []
    private var monitors: [Any] = []
    private(set) var pins: [PinWindow] = []

    var hasPermission: Bool { CGPreflightScreenCaptureAccess() }

    func applyHotkey() {
        if let k = AppSettings.shared.shotHotkey.key {
            Hotkeys.shared.register(id: 1, keyCode: k.code, modifiers: k.mods) { ScreenshotController.shared.start() }
        } else {
            Hotkeys.shared.unregister(id: 1)
        }
    }

    func start(after delay: TimeInterval = 0) {
        guard windows.isEmpty else { return }
        guard hasPermission else {
            _ = CGRequestScreenCaptureAccess()
            if FileActions.confirm("需要「屏幕录制」权限", "截图需要在 系统设置 › 隐私与安全性 › 屏幕录制 里允许 MacPulse。授权后可能需要重新打开 MacPulse。", ok: "打开系统设置", destructive: false),
               let u = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture") {
                NSWorkspace.shared.open(u)
            }
            return
        }
        Task { @MainActor in
            if delay > 0 { try? await Task.sleep(for: .seconds(delay)) }
            await capture()
        }
    }

    private func capture() async {
        guard let content = try? await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true) else { return }
        let me = content.applications.filter { $0.processID == ProcessInfo.processInfo.processIdentifier }
        let infos = (CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID) as? [[String: Any]]) ?? []
        let myPID = Int(ProcessInfo.processInfo.processIdentifier)
        let cgWindows: [CGRect] = infos.compactMap { w in
            guard (w[kCGWindowLayer as String] as? Int) == 0, (w[kCGWindowOwnerPID as String] as? Int) != myPID,
                  let b = w[kCGWindowBounds as String] as? [String: CGFloat] else { return nil }
            let r = CGRect(x: b["X"] ?? 0, y: b["Y"] ?? 0, width: b["Width"] ?? 0, height: b["Height"] ?? 0)
            return r.width > 40 && r.height > 30 ? r : nil
        }

        for screen in NSScreen.screens {
            guard let id = screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? CGDirectDisplayID,
                  let display = content.displays.first(where: { $0.displayID == id }) else { continue }
            let filter = SCContentFilter(display: display, excludingApplications: me, exceptingWindows: [])
            let cfg = SCStreamConfiguration()
            let scale = screen.backingScaleFactor
            cfg.width = Int(screen.frame.width * scale)
            cfg.height = Int(screen.frame.height * scale)
            cfg.showsCursor = false
            guard let img = try? await SCScreenshotManager.captureImage(contentFilter: filter, configuration: cfg) else { continue }
            let bounds = CGDisplayBounds(id)
            let local = cgWindows.compactMap { r -> CGRect? in
                let l = r.offsetBy(dx: -bounds.minX, dy: -bounds.minY).intersection(CGRect(origin: .zero, size: bounds.size))
                return l.isNull || l.width < 20 ? nil : l
            }
            let session = ShotSession(screen: screen, image: img, windowRects: local)
            sessions.append(session)
            let w = OverlayWindow(contentRect: screen.frame, styleMask: [.borderless], backing: .buffered, defer: false)
            w.level = .screenSaver
            w.isOpaque = true
            w.backgroundColor = .black
            w.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
            w.contentView = NSHostingView(rootView: CaptureView(session: session).environment(AppState.shared))
            w.setFrame(screen.frame, display: true)
            w.makeKeyAndOrderFront(nil)
            windows.append(w)
        }
        NSApp.activate(ignoringOtherApps: true)
        NSCursor.crosshair.push()
        monitors.append(NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self] e in
            guard let self else { return e }
            if e.keyCode == 53 { // esc
                if let s = self.sessions.first(where: { $0.textAt != nil }) { s.textAt = nil; return nil }
                self.finish(); return nil
            }
            if e.keyCode == 36 || e.keyCode == 76, self.sessions.allSatisfy({ $0.textAt == nil }) { self.copyAndClose(); return nil }
            if e.modifierFlags.contains(.command), e.charactersIgnoringModifiers == "z" { self.undo(); return nil }
            if e.modifierFlags.contains(.command), e.charactersIgnoringModifiers == "c" { self.copyAndClose(); return nil }
            return e
        } as Any)
        monitors.append(NSEvent.addLocalMonitorForEvents(matching: .rightMouseDown) { [weak self] e in
            guard let self else { return e }
            if let s = self.sessions.first(where: { $0.phase == .editing }) {
                s.phase = .selecting; s.selection = nil; s.marks = []; s.tool = nil
            } else { self.finish() }
            return nil
        } as Any)
    }

    private var active: ShotSession? { sessions.first { $0.selection != nil } }

    func undo() { if let s = active, !s.marks.isEmpty { s.marks.removeLast() } }

    func copyAndClose() {
        guard let img = active?.export() else { finish(); return }
        NSPasteboard.general.clearContents()
        NSPasteboard.general.writeObjects([img])
        finish()
    }

    func save() {
        guard let img = active?.export() else { return }
        finish()
        let f = DateFormatter(); f.dateFormat = "yyyy-MM-dd HH.mm.ss"
        let folder = URL(fileURLWithPath: (AppSettings.shared.shotFolder as NSString).expandingTildeInPath)
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let url = folder.appendingPathComponent("截图 \(f.string(from: Date())).png")
        if let tiff = img.tiffRepresentation, let rep = NSBitmapImageRep(data: tiff), let png = rep.representation(using: .png, properties: [:]) {
            try? png.write(to: url)
            NSPasteboard.general.clearContents()
            NSPasteboard.general.writeObjects([img])
            NSWorkspace.shared.activateFileViewerSelecting([url])
        }
    }

    func pin() {
        guard let s = active, let img = s.export(), let sel = s.selection else { return }
        let origin = NSPoint(x: s.screen.frame.minX + sel.minX, y: s.screen.frame.maxY - sel.maxY)
        finish()
        let p = PinWindow(image: img, at: origin)
        pins.append(p)
        p.onClose = { [weak self, weak p] in self?.pins.removeAll { $0 === p } }
        p.orderFrontRegardless()
    }

    func closeAllPins() { for p in pins { p.close() } ; pins.removeAll() }

    func finish() {
        for m in monitors { NSEvent.removeMonitor(m) }
        monitors.removeAll()
        for w in windows { w.orderOut(nil) }
        windows.removeAll()
        sessions.removeAll()
        NSCursor.pop()
    }
}

final class OverlayWindow: NSWindow {
    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { true }
}

// MARK: - Overlay view

struct CaptureView: View {
    @Bindable var session: ShotSession

    var body: some View {
        let s = session
        let full = Image(decorative: s.image, scale: 1).resizable()
        let pix = s.pixelated.map { Image(decorative: $0, scale: 1).resizable() }
        ZStack(alignment: .topLeading) {
            full.frame(width: s.size.width, height: s.size.height)

            // Dim everything outside the selection (or the hovered window).
            let hole = s.selection ?? (s.dragStart == nil ? s.hover : nil)
            Canvas { ctx, size in
                var p = Path(CGRect(origin: .zero, size: size))
                if let h = hole { p.addRect(h) }
                ctx.fill(p, with: .color(.black.opacity(0.45)), style: FillStyle(eoFill: true))
            }
            .frame(width: s.size.width, height: s.size.height)
            .allowsHitTesting(false)

            Canvas { ctx, _ in
                drawMarks(s.marks + (s.draft.map { [$0] } ?? []), in: ctx, origin: .zero, pixelated: pix, fullSize: s.size)
            }
            .allowsHitTesting(false)

            if let h = hole {
                Rectangle().strokeBorder(Palette.cpu, lineWidth: 1.5)
                    .frame(width: h.width, height: h.height).offset(x: h.minX, y: h.minY)
                    .allowsHitTesting(false)
                Text("\(Int(h.width)) × \(Int(h.height))")
                    .font(.system(size: 11, weight: .medium)).monospacedDigit().foregroundStyle(.white)
                    .padding(.horizontal, 6).padding(.vertical, 2)
                    .background(Capsule().fill(.black.opacity(0.7)))
                    .offset(x: h.minX, y: max(h.minY - 24, 4))
                    .allowsHitTesting(false)
            }

            if s.phase == .selecting && s.dragStart == nil {
                Loupe(image: s.image, point: s.mouse, size: s.size)
                    .offset(x: min(s.mouse.x + 20, s.size.width - 130), y: min(s.mouse.y + 20, s.size.height - 150))
                    .allowsHitTesting(false)
            }

            if let at = s.textAt {
                TextField("输入文字，回车完成", text: $session.textValue)
                    .textFieldStyle(.plain)
                    .font(.system(size: 12 + s.width * 3, weight: .semibold))
                    .foregroundStyle(s.color)
                    .frame(width: 260)
                    .padding(4)
                    .background(RoundedRectangle(cornerRadius: 4).strokeBorder(s.color.opacity(0.6), style: StrokeStyle(lineWidth: 1, dash: [4, 3])))
                    .offset(x: at.x - 4, y: at.y - 4)
                    .onSubmit { commitText() }
            }

            if s.phase == .editing, let sel = s.selection {
                ShotToolbar(session: session)
                    .fixedSize()
                    .offset(toolbarOffset(sel))
            }
        }
        .frame(width: s.size.width, height: s.size.height, alignment: .topLeading)
        .contentShape(Rectangle())
        .onContinuousHover { phase in
            if case .active(let p) = phase {
                s.mouse = p
                if s.phase == .selecting && s.dragStart == nil { s.hover = s.windowAt(p) }
            }
        }
        .gesture(DragGesture(minimumDistance: 0).onChanged(dragChanged).onEnded(dragEnded))
        .simultaneousGesture(TapGesture(count: 2).onEnded {
            if s.phase == .editing, s.tool == nil { ScreenshotController.shared.copyAndClose() }
        })
    }

    private func toolbarOffset(_ sel: CGRect) -> CGSize {
        let w: CGFloat = 560, h: CGFloat = 40
        var x = sel.maxX - w
        x = min(max(x, 8), session.size.width - w - 8)
        var y = sel.maxY + 8
        if y + h > session.size.height - 8 { y = sel.minY - h - 8 }
        if y < 8 { y = sel.maxY - h - 8 }
        return CGSize(width: x, height: y)
    }

    private func commitText() {
        let s = session
        if let at = s.textAt, !s.textValue.trimmingCharacters(in: .whitespaces).isEmpty {
            s.marks.append(ShotMark(kind: .text(at, s.textValue), color: s.color, width: s.width))
        }
        s.textAt = nil
        s.textValue = ""
    }

    private func dragChanged(_ g: DragGesture.Value) {
        let s = session
        let p = g.location, start = g.startLocation
        switch s.phase {
        case .selecting:
            if g.translation.width.magnitude + g.translation.height.magnitude > 4 {
                s.dragStart = start
                s.selection = CGRect(x: min(start.x, p.x), y: min(start.y, p.y), width: abs(p.x - start.x), height: abs(p.y - start.y))
            }
        case .editing:
            guard let sel = s.selection else { return }
            let q = CGPoint(x: min(max(p.x, sel.minX), sel.maxX), y: min(max(p.y, sel.minY), sel.maxY))
            guard let tool = s.tool else {
                // No tool: drag inside moves the selection.
                if sel.contains(start) {
                    if s.moveOffset == nil { s.moveOffset = CGPoint(x: start.x - sel.minX, y: start.y - sel.minY) }
                    let o = s.moveOffset!
                    var r = sel
                    r.origin = CGPoint(x: min(max(p.x - o.x, 0), s.size.width - r.width), y: min(max(p.y - o.y, 0), s.size.height - r.height))
                    s.selection = r
                }
                return
            }
            guard sel.contains(start) else { return }
            switch tool {
            case .rect: s.draft = ShotMark(kind: .rect(CGRect(x: min(start.x, q.x), y: min(start.y, q.y), width: abs(q.x - start.x), height: abs(q.y - start.y))), color: s.color, width: s.width)
            case .mosaic: s.draft = ShotMark(kind: .mosaic(CGRect(x: min(start.x, q.x), y: min(start.y, q.y), width: abs(q.x - start.x), height: abs(q.y - start.y))), color: s.color, width: s.width)
            case .arrow: s.draft = ShotMark(kind: .arrow(start, q), color: s.color, width: s.width)
            case .pen:
                if case .pen(var pts)? = s.draft?.kind { pts.append(q); s.draft?.kind = .pen(pts) }
                else { s.draft = ShotMark(kind: .pen([start, q]), color: s.color, width: s.width) }
            case .text: break
            }
        }
    }

    private func dragEnded(_ g: DragGesture.Value) {
        let s = session
        switch s.phase {
        case .selecting:
            if s.selection == nil {
                // A click picks the window under the mouse, or the whole screen.
                s.selection = s.windowAt(g.location) ?? CGRect(origin: .zero, size: s.size)
            }
            s.dragStart = nil
            if let sel = s.selection, sel.width > 4, sel.height > 4 { s.phase = .editing } else { s.selection = nil }
        case .editing:
            s.moveOffset = nil
            if s.tool == .text, let sel = s.selection, sel.contains(g.location) {
                if s.textAt != nil { commitText() }
                s.textAt = g.location
                return
            }
            if let d = s.draft { s.marks.append(d); s.draft = nil }
        }
    }
}

/// Magnifier with pixel coordinates, shown while choosing the start point.
private struct Loupe: View {
    let image: CGImage
    let point: CGPoint
    let size: CGSize

    var body: some View {
        let scale = CGFloat(image.width) / size.width
        let src = CGRect(x: point.x * scale - 12, y: point.y * scale - 12, width: 24, height: 24)
        VStack(spacing: 0) {
            if let crop = image.cropping(to: src) {
                Image(decorative: crop, scale: 1).resizable().interpolation(.none)
                    .frame(width: 120, height: 120)
                    .overlay(Rectangle().stroke(Palette.cpu.opacity(0.8), lineWidth: 1).frame(width: 6, height: 6))
            }
            Text("\(Int(point.x)), \(Int(point.y))").font(.system(size: 10)).monospacedDigit().foregroundStyle(.white)
                .frame(width: 120).padding(.vertical, 3).background(.black.opacity(0.75))
        }
        .clipShape(RoundedRectangle(cornerRadius: 6))
        .overlay(RoundedRectangle(cornerRadius: 6).stroke(.white.opacity(0.8), lineWidth: 1))
    }
}

private struct ShotToolbar: View {
    @Bindable var session: ShotSession

    var body: some View {
        HStack(spacing: 2) {
            ForEach(ShotTool.allCases) { t in
                button(t.symbol, t.title, selected: session.tool == t) { session.tool = session.tool == t ? nil : t }
            }
            divider
            ForEach(Array(ShotPalette.colors.enumerated()), id: \.offset) { _, c in
                Circle().fill(c).frame(width: 14, height: 14)
                    .overlay(Circle().stroke(session.color == c ? Color.primary : Color.primary.opacity(0.2), lineWidth: session.color == c ? 2 : 1))
                    .padding(3)
                    .onTapGesture { session.color = c }
            }
            Menu {
                ForEach([2, 3, 5, 8], id: \.self) { w in Button("\(w) 号") { session.width = CGFloat(w) } }
            } label: { Image(systemName: "lineweight") }
                .menuStyle(.borderlessButton).menuIndicator(.hidden).frame(width: 28).help("粗细")
            divider
            button("arrow.uturn.backward", "撤销 ⌘Z", selected: false) { ScreenshotController.shared.undo() }
            button("pin", "贴到屏幕", selected: false) { ScreenshotController.shared.pin() }
            button("square.and.arrow.down", "保存", selected: false) { ScreenshotController.shared.save() }
            button("xmark", "取消 Esc", selected: false) { ScreenshotController.shared.finish() }
            Button { ScreenshotController.shared.copyAndClose() } label: {
                Label("复制", systemImage: "checkmark").font(.system(size: 12, weight: .semibold))
            }
            .buttonStyle(.borderedProminent).controlSize(.small).help("复制到剪贴板（回车 / 双击）")
            .padding(.leading, 4)
        }
        .padding(5)
        .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(.regularMaterial))
        .overlay(RoundedRectangle(cornerRadius: 10, style: .continuous).strokeBorder(Color.primary.opacity(0.1)))
        .shadow(color: .black.opacity(0.25), radius: 10, y: 4)
        .tint(Palette.theme)
    }

    private var divider: some View { Divider().frame(height: 20).padding(.horizontal, 4) }

    private func button(_ symbol: String, _ help: String, selected: Bool, _ action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Image(systemName: symbol).font(.system(size: 13, weight: .medium))
                .frame(width: 28, height: 26)
                .background(RoundedRectangle(cornerRadius: 6).fill(selected ? Palette.theme.opacity(0.25) : .clear))
        }
        .buttonStyle(.plain).help(help)
    }
}

// MARK: - Pinned image

final class PinWindow: NSPanel {
    var onClose: (() -> Void)?
    private let image: NSImage
    private var zoom: CGFloat = 1

    init(image: NSImage, at origin: NSPoint) {
        self.image = image
        super.init(contentRect: NSRect(origin: origin, size: image.size), styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        level = .floating
        hasShadow = true
        isMovableByWindowBackground = true
        collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        backgroundColor = .clear
        let v = PinImageView(frame: NSRect(origin: .zero, size: image.size))
        v.image = image
        v.imageScaling = .scaleProportionallyUpOrDown
        v.wantsLayer = true
        v.layer?.borderColor = NSColor.controlAccentColor.withAlphaComponent(0.6).cgColor
        v.layer?.borderWidth = 1
        v.owner = self
        contentView = v
    }

    override var canBecomeKey: Bool { true }

    override func close() {
        super.close()
        onClose?()
    }

    func zoomBy(_ factor: CGFloat) {
        zoom = min(max(zoom * factor, 0.2), 5)
        let s = NSSize(width: image.size.width * zoom, height: image.size.height * zoom)
        let c = NSPoint(x: frame.midX, y: frame.midY)
        setFrame(NSRect(x: c.x - s.width / 2, y: c.y - s.height / 2, width: s.width, height: s.height), display: true, animate: false)
    }

    func menu() -> NSMenu {
        let m = NSMenu()
        m.addItem(withTitle: "复制", action: #selector(copyImage), keyEquivalent: "").target = self
        m.addItem(withTitle: "另存为…", action: #selector(saveImage), keyEquivalent: "").target = self
        m.addItem(.separator())
        for (t, a) in [("不透明", 1.0), ("透明 25%", 0.75), ("透明 50%", 0.5)] {
            let i = m.addItem(withTitle: t, action: #selector(setAlpha(_:)), keyEquivalent: "")
            i.target = self; i.representedObject = a
            i.state = abs(alphaValue - a) < 0.01 ? .on : .off
        }
        m.addItem(withTitle: "原始大小", action: #selector(resetZoom), keyEquivalent: "").target = self
        m.addItem(.separator())
        m.addItem(withTitle: "关闭", action: #selector(closeMe), keyEquivalent: "").target = self
        return m
    }

    @objc private func copyImage() { NSPasteboard.general.clearContents(); NSPasteboard.general.writeObjects([image]) }
    @objc private func setAlpha(_ i: NSMenuItem) { alphaValue = i.representedObject as? Double ?? 1 }
    @objc private func resetZoom() { zoom = 1; zoomBy(1) }
    @objc private func closeMe() { close() }
    @objc private func saveImage() {
        let p = NSSavePanel()
        p.nameFieldStringValue = "截图.png"
        p.allowedContentTypes = [.png]
        if p.runModal() == .OK, let url = p.url, let tiff = image.tiffRepresentation,
           let png = NSBitmapImageRep(data: tiff)?.representation(using: .png, properties: [:]) {
            try? png.write(to: url)
        }
    }
}

private final class PinImageView: NSImageView {
    weak var owner: PinWindow?
    override func scrollWheel(with e: NSEvent) { owner?.zoomBy(e.scrollingDeltaY > 0 ? 1.06 : 1 / 1.06) }
    override func mouseDown(with e: NSEvent) {
        if e.clickCount == 2 { owner?.close(); return }
        window?.performDrag(with: e)
    }
    override func rightMouseDown(with e: NSEvent) {
        guard let m = owner?.menu() else { return }
        NSMenu.popUpContextMenu(m, with: e, for: self)
    }
    override func keyDown(with e: NSEvent) { if e.keyCode == 53 { owner?.close() } else { super.keyDown(with: e) } }
}
