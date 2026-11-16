import AppKit
import SwiftUI
import Observation

/// Animated menu bar icons. Pixel sprites stay crisp at 16 px; the geometric ones are
/// drawn as vectors. Every icon animates faster as the CPU gets busier.
enum MenuIcon: String, CaseIterable, Identifiable {
    case none, slime, ghost, chick, whale, rocket, heart, pulse, fan, bars
    var id: String { rawValue }

    var title: String {
        switch self {
        case .none: return "不显示"
        case .slime: return "史莱姆"
        case .ghost: return "小幽灵"
        case .chick: return "小鸡"
        case .whale: return "小鲸鱼"
        case .rocket: return "火箭"
        case .heart: return "心跳"
        case .pulse: return "脉搏线"
        case .fan: return "风扇"
        case .bars: return "均衡器"
        }
    }

    var frameCount: Int {
        switch self {
        case .none: return 1
        case .slime: return 6
        case .ghost, .chick, .rocket: return 4
        case .whale: return 8
        case .heart: return 6
        case .pulse: return 12
        case .fan: return 8
        case .bars: return 8
        }
    }

    /// Width in points; height is always 18.
    var width: CGFloat {
        switch self {
        case .none: return 0
        case .slime, .whale, .fan, .bars: return 18
        case .ghost, .chick: return 16
        case .rocket: return 14
        case .heart: return 15
        case .pulse: return 24
        }
    }
}

/// Metrics the user can tick to show next to the icon.
enum MenuMetric: String, CaseIterable, Identifiable {
    case cpu, cpuGraph, memory, disk, network, temperature, gpu, battery
    var id: String { rawValue }

    var title: String {
        switch self {
        case .cpu: return "CPU 占用"
        case .cpuGraph: return "CPU 走势图"
        case .memory: return "内存占用"
        case .disk: return "磁盘剩余"
        case .network: return "网速"
        case .temperature: return "温度"
        case .gpu: return "GPU 占用"
        case .battery: return "电池"
        }
    }

    @MainActor
    func text(_ m: SystemMonitor) -> String? {
        switch self {
        case .cpu: return "CPU \(Int((m.cpu.total * 100).rounded()))%"
        case .cpuGraph: return nil
        case .memory: return "内存 \(Int((m.memory.usedFraction * 100).rounded()))%"
        case .disk: return "剩 \(MenuBarRenderer.diskText(m.disk.available))"
        case .network: return "↓\(Self.short(m.netIn)) ↑\(Self.short(m.netOut))"
        case .temperature: return m.temperature.map { String(format: "%.0f°", $0) }
        case .gpu: return m.gpu.map { "GPU \(Int(($0 * 100).rounded()))%" }
        case .battery: return m.battery.map { "\($0.charging ? "⚡" : "电")\(Int(($0.percent * 100).rounded()))%" }
        }
    }

    /// Compact rate for the menu bar: "820K", "1.2M".
    static func short(_ bps: Double) -> String {
        let k = bps / 1024
        if k < 1 { return "0K" }
        if k < 1000 { return String(format: "%.0fK", k) }
        let mb = k / 1024
        return mb < 10 ? String(format: "%.1fM", mb) : String(format: "%.0fM", mb)
    }
}

/// One-line status for the popover header.
@MainActor
func statusSentence(_ m: SystemMonitor, _ s: AppSettings) -> String {
    let cpu = Fmt.pct(m.cpu.total)
    if m.memory.pressureLevel >= 4 { return "内存压力很大，可以关掉一些应用" }
    if m.disk.total > 0, Double(m.disk.available) < Double(s.alertDiskGB) * 1_073_741_824 {
        return "磁盘只剩 \(Fmt.bytes(m.disk.available))，去清理一下吧"
    }
    if m.cpu.total > 0.9 { return "CPU 满载中 \(cpu)" }
    if m.cpu.total > 0.6 { return "有点忙，CPU \(cpu)" }
    if m.cpu.total < 0.08 { return "很清闲，CPU \(cpu)" }
    return "运行正常，CPU \(cpu)"
}

/// Advances the shared frame counter. The frame rate follows CPU load and the timer
/// idles when no icon is visible.
@MainActor
@Observable
final class IconAnimator {
    static let shared = IconAnimator()
    var frame = 0
    @ObservationIgnored private var viewers = 0

    func start() { schedule(after: 0.2) }
    func viewer(_ on: Bool) { viewers = max(0, viewers + (on ? 1 : -1)) }

    private func schedule(after interval: TimeInterval) {
        let t = Timer(timeInterval: interval, repeats: false) { [weak self] _ in
            MainActor.assumeIsolated { self?.step() }
        }
        t.tolerance = interval * 0.1
        RunLoop.main.add(t, forMode: .common)
    }

    private func step() {
        guard AppSettings.shared.menuIcon != .none || viewers > 0 else { schedule(after: 1); return }
        frame &+= 1
        StatusItemController.shared.refresh()
        // 0% CPU → 5 fps, 100% → ~14 fps.
        schedule(after: max(0.07, 0.22 - 0.15 * min(SystemMonitor.shared.cpu.total, 1)))
    }
}

// MARK: - Pixel sprites

private struct Sprite {
    var rows: [String]
    var dx = 0
    var dy = 0
}

private enum Sprites {
    static func slime(_ f: Int) -> Sprite {
        // (width, height, lift) per frame: rest, squash, rest, rise, top, fall.
        let shapes: [(Int, Int, Int)] = [(14, 9, 0), (16, 7, 0), (14, 9, 0), (12, 11, 2), (12, 11, 4), (12, 11, 2)]
        let (w, h, lift) = shapes[f % shapes.count]
        let W = 18
        var grid = Array(repeating: Array(repeating: Character("."), count: W), count: 16)
        let cx = Double(W) / 2
        for r in 0..<h {
            let t = Double(r) / Double(h)
            let half = Double(w) / 2 * (1 - pow(t, 2.4)).squareRoot()
            let y = 15 - lift - r
            for x in 0..<W where abs(Double(x) + 0.5 - cx) <= half { grid[y][x] = "#" }
        }
        let eyeRow = 15 - lift - Int(Double(h) * 0.45)
        let eyeH = f % shapes.count == 1 ? 1 : 2
        for x in [Int(cx) - 3, Int(cx) + 2] {
            for k in 0..<eyeH where eyeRow - k >= 0 { grid[eyeRow - k][x] = "." }
        }
        // Shine
        let shineY = 15 - lift - h + 2
        let shineX = Int(cx) - w / 2 + 3
        if shineY >= 0, shineX >= 0, shineX < W { grid[shineY][shineX] = "." }
        return Sprite(rows: grid.map { String($0) })
    }

    static let ghostBody = [
        ".....######.....",
        "...##########...",
        "..############..",
        ".##############.",
        ".####.####.####.",
        ".####.####.####.",
        ".##############.",
        "#######..#######",
        "################",
        ".##############.",
        ".##############.",
    ]
    static func ghost(_ f: Int) -> Sprite {
        var body = ghostBody
        if f >= 2 { // glance right
            body[4] = ".#####.####.###."
            body[5] = ".#####.####.###."
        }
        let tail = f % 2 == 0
            ? ["..############..", "...#####..####..", "....##.....##..."]
            : ["..############..", "..####..#####...", "...##.....##...."]
        return Sprite(rows: body + tail, dy: [1, 0, 0, 1][f % 4] + 1)
    }

    static let chickBody = [
        "......####......",
        ".....######.....",
        ".....###.##.....",
        ".....#########..",
        ".....######.....",
        "...##########...",
        ".#############..",
        ".#############..",
        ".#####..######..",
        ".#############..",
        "..###########...",
        "....#######.....",
    ]
    static func chick(_ f: Int) -> Sprite {
        let legs: [[String]] = [
            ["......#..#......", ".....##.##......"],
            [".....#....#.....", "....##...##....."],
            ["......#..#......", ".....##.##......"],
            [".......##.......", "......####......"],
        ]
        return Sprite(rows: chickBody + legs[f % 4], dy: [1, 0, 1, 0][f % 4] + 1)
    }

    static let whaleBody = [
        "..........#####...",
        "........#########.",
        "##.....##########.",
        ".##..######.#####.",
        "..###############.",
        "..###############.",
        ".##..###########..",
        "##.....#######....",
    ]
    static func whale(_ f: Int) -> Sprite {
        let e = ".................."
        func row(_ cols: [Int]) -> String {
            var c = Array(e)
            for x in cols where x < c.count { c[x] = "#" }
            return String(c)
        }
        let spouts: [[String]] = [
            [e, e, e, e, e],
            [e, e, e, e, row([12])],
            [e, e, e, row([12]), row([12])],
            [e, row([11, 13]), row([12]), row([12]), row([12])],
            [row([10, 14]), row([11, 13]), row([12]), e, e],
            [row([9, 15]), e, e, e, e],
            [e, e, e, e, e],
            [e, e, e, e, e],
        ]
        let bob = [0, 0, 0, 0, 1, 1, 1, 0][f % 8]
        return Sprite(rows: spouts[f % 8] + whaleBody, dy: 1 + bob)
    }

    static func rocket(_ f: Int) -> Sprite {
        var rows = [
            "......##......",
            ".....####.....",
            "....######....",
            "....##..##....",
            "....##..##....",
            "....######....",
            "....######....",
            "...########...",
            "..##.####.##..",
            "..#..####..#..",
            ".....#..#.....",
        ]
        let flames: [[String]] = [
            [".....####.....", "......##......", ".............."],
            [".....####.....", ".....####.....", "......##......"],
            ["......##......", "......##......", ".............."],
            [".....####.....", "......##......", "......#......."],
        ]
        rows += flames[f % 4]
        // Stars streaming past.
        var chars = rows.map { Array($0) }
        let y = (f * 3) % 14
        if y < chars.count { chars[y][0] = "#" }
        let y2 = (f * 3 + 7) % 14
        if y2 < chars.count { chars[y2][13] = "#" }
        return Sprite(rows: chars.map { String($0) }, dy: 1)
    }

    static let heartBig = [
        "..###...###..",
        ".#..##.#####.",
        "#.##########.",
        "#############",
        "#############",
        ".###########.",
        "..#########..",
        "...#######...",
        "....#####....",
        ".....###.....",
        "......#......",
    ]
    static let heartSmall = [
        ".##...##.",
        "#..#.####",
        "#########",
        "#########",
        ".#######.",
        "..#####..",
        "...###...",
        "....#....",
    ]
    static func heart(_ f: Int) -> Sprite {
        let big = [true, false, true, false, false, false][f % 6]
        let pad = big ? "." : "..."
        let rows = (big ? heartBig : heartSmall).map { pad + $0 + pad }
        return Sprite(rows: rows, dy: big ? 3 : 5)
    }
}

// MARK: - Rendering

enum IconRenderer {
    /// Draws one frame. `tint` nil = template black; otherwise a solid colour.
    static func draw(_ icon: MenuIcon, frame f: Int, cpu: Double, tint: NSColor?, in rect: NSRect) {
        guard icon != .none else { return }
        let scale = rect.height / 18
        let color = tint ?? .black
        let ctx = NSGraphicsContext.current
        ctx?.saveGraphicsState()
        let xf = NSAffineTransform()
        xf.translateX(by: rect.minX, yBy: rect.minY)
        xf.scaleX(by: scale, yBy: scale)
        xf.concat()
        color.setFill(); color.setStroke()

        func pixels(_ s: Sprite) {
            let top: CGFloat = 18 - CGFloat(s.dy)
            for (r, line) in s.rows.enumerated() {
                for (c, ch) in line.enumerated() where ch == "#" {
                    NSRect(x: CGFloat(c + s.dx), y: top - CGFloat(r + 1), width: 1, height: 1).fill()
                }
            }
        }

        let frame = f % icon.frameCount
        switch icon {
        case .none: break
        case .slime: pixels(Sprites.slime(frame))
        case .ghost: pixels(Sprites.ghost(frame))
        case .chick: pixels(Sprites.chick(frame))
        case .whale: pixels(Sprites.whale(frame))
        case .rocket: pixels(Sprites.rocket(frame))
        case .heart: pixels(Sprites.heart(frame))
        case .pulse: drawPulse(frame, color: color)
        case .fan: drawFan(frame)
        case .bars: drawBars(frame, cpu: cpu)
        }
        ctx?.restoreGraphicsState()
    }

    private static func drawPulse(_ f: Int, color: NSColor) {
        // An ECG trace scrolling left; brighter towards the head.
        func y(_ x: Double) -> Double {
            let p = x.truncatingRemainder(dividingBy: 24)
            switch p {
            case 8..<10: return 9 + (p - 8) * 1.5          // small bump
            case 10..<11: return 12 - (p - 10) * 3
            case 13..<14: return 9 - (p - 13) * 5           // dip
            case 14..<15.5: return 4 + (p - 14) * 9.3       // spike up
            case 15.5..<17: return 18 - (p - 15.5) * 9.3    // spike down
            case 17..<18: return 4 + (p - 17) * 5
            default: return 9
            }
        }
        let shift = Double(f) * 2
        let p = NSBezierPath()
        let steps = 96
        for i in 0...steps {
            let x = Double(i) / Double(steps) * 22 + 1
            let pt = NSPoint(x: x, y: 18 - y(x + shift) - 0.5)
            if i == 0 { p.move(to: pt) } else { p.line(to: pt) }
        }
        p.lineWidth = 1.5
        p.lineJoinStyle = .round
        p.lineCapStyle = .round
        color.setStroke()
        p.stroke()
        let head = NSPoint(x: 23, y: 18 - y(23 + shift) - 0.5)
        color.setFill()
        NSBezierPath(ovalIn: NSRect(x: head.x - 1.4, y: head.y - 1.4, width: 2.8, height: 2.8)).fill()
    }

    private static func drawFan(_ f: Int) {
        let c = NSPoint(x: 9, y: 9)
        let ring = NSBezierPath(ovalIn: NSRect(x: 1, y: 1, width: 16, height: 16))
        ring.lineWidth = 1.4
        ring.stroke()
        let base = Double(f) * 15 * .pi / 180
        for k in 0..<3 {
            let a = base + Double(k) * 2 * .pi / 3
            let blade = NSBezierPath()
            let tip = NSPoint(x: c.x + CGFloat(cos(a)) * 6.2, y: c.y + CGFloat(sin(a)) * 6.2)
            let side = a + .pi / 2
            blade.move(to: c)
            blade.curve(to: tip,
                        controlPoint1: NSPoint(x: c.x + CGFloat(cos(a)) * 2 + CGFloat(cos(side)) * 3.6, y: c.y + CGFloat(sin(a)) * 2 + CGFloat(sin(side)) * 3.6),
                        controlPoint2: NSPoint(x: tip.x + CGFloat(cos(side)) * 2.4, y: tip.y + CGFloat(sin(side)) * 2.4))
            blade.curve(to: c,
                        controlPoint1: NSPoint(x: tip.x - CGFloat(cos(side)) * 0.6, y: tip.y - CGFloat(sin(side)) * 0.6),
                        controlPoint2: NSPoint(x: c.x + CGFloat(cos(a)) * 2, y: c.y + CGFloat(sin(a)) * 2))
            blade.fill()
        }
        NSBezierPath(ovalIn: NSRect(x: c.x - 1.6, y: c.y - 1.6, width: 3.2, height: 3.2)).fill()
    }

    private static func drawBars(_ f: Int, cpu: Double) {
        let amp = 0.3 + 0.7 * min(max(cpu, 0), 1)
        for b in 0..<5 {
            // Deterministic pseudo-random height per bar and frame.
            var h = UInt32(truncatingIfNeeded: (f % 8) * 31 + b * 17) &* 2654435761
            h ^= h >> 13
            let r = Double(h % 1000) / 1000
            let height = 3 + (0.35 + 0.65 * r) * amp * 12
            let x = CGFloat(1 + b * 3) + 0.5
            NSBezierPath(roundedRect: NSRect(x: x, y: 2, width: 2.2, height: CGFloat(height)), xRadius: 1, yRadius: 1).fill()
        }
    }

    /// Load colour for "按负载变色".
    static func tint(for cpu: Double) -> NSColor {
        if cpu > 0.85 { return NSColor(red: 0.92, green: 0.28, blue: 0.30, alpha: 1) }
        if cpu > 0.6 { return NSColor(red: 0.96, green: 0.62, blue: 0.10, alpha: 1) }
        return NSColor(red: 0.13, green: 0.70, blue: 0.55, alpha: 1)
    }

    static func cpuBucket(_ cpu: Double) -> Int { cpu > 0.85 ? 2 : (cpu > 0.6 ? 1 : 0) }

    /// Bitmap of one frame at 2× for the status bar.
    static func bitmap(_ icon: MenuIcon, frame: Int, cpu: Double, tint: NSColor?, scale: CGFloat = 2, height: CGFloat = 18) -> NSImage {
        let size = NSSize(width: max(icon.width, 1) * height / 18, height: height)
        let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: Int(size.width * scale), pixelsHigh: Int(size.height * scale),
                                   bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                                   colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
        rep.size = size
        NSGraphicsContext.saveGraphicsState()
        let ctx = NSGraphicsContext(bitmapImageRep: rep)
        ctx?.imageInterpolation = .none
        ctx?.shouldAntialias = !(icon == .slime || icon == .ghost || icon == .chick || icon == .whale || icon == .rocket || icon == .heart)
        NSGraphicsContext.current = ctx
        draw(icon, frame: frame, cpu: cpu, tint: tint, in: NSRect(origin: .zero, size: size))
        NSGraphicsContext.restoreGraphicsState()
        let img = NSImage(size: size)
        img.addRepresentation(rep)
        img.isTemplate = tint == nil
        return img
    }

    /// Small CPU history graph used by the "CPU 走势图" metric.
    static func cpuGraph(_ history: [Double]) -> NSImage {
        let bars = 12
        let vals = Array((Array(repeating: 0.0, count: bars) + history).suffix(bars))
        let img = NSImage(size: NSSize(width: 30, height: 18), flipped: false) { _ in
            NSColor.black.withAlphaComponent(0.25).setFill()
            NSBezierPath(roundedRect: NSRect(x: 0, y: 2, width: 30, height: 14), xRadius: 2.5, yRadius: 2.5).fill()
            NSColor.black.setFill()
            for (i, v) in vals.enumerated() {
                let bh = max(1, 14 * CGFloat(min(max(v, 0), 1)))
                NSRect(x: 1.5 + CGFloat(i) * 2.25, y: 2, width: 1.75, height: bh).fill()
            }
            return true
        }
        img.isTemplate = true
        return img
    }
}

/// SwiftUI view of an animated icon, for the popover and settings.
struct AnimatedIconView: View {
    let icon: MenuIcon
    var height: CGFloat = 36
    var tinted = true
    @Environment(AppState.self) private var app

    var body: some View {
        let frame = IconAnimator.shared.frame
        let img = IconRenderer.bitmap(icon, frame: frame, cpu: app.monitor.cpu.total, tint: nil, scale: 2, height: height)
        Image(nsImage: img)
            .renderingMode(.template)
            .interpolation(.none)
            .foregroundStyle(tinted ? AnyShapeStyle(Palette.theme) : AnyShapeStyle(.primary))
            .frame(width: max(icon.width, 18) * height / 18, height: height)
    }
}
