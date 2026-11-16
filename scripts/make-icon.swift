import AppKit
// Draws the 1024px app icon. Usage: swift make-icon.swift out.png [peek|pixel|hide]
let size: CGFloat = 1024
let out = CommandLine.arguments[1]
let variant = CommandLine.arguments.count > 2 ? CommandLine.arguments[2] : "peek"

let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: Int(size), pixelsHigh: Int(size), bitsPerSample: 8,
                           samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
NSGraphicsContext.saveGraphicsState()
NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
func C(_ r: CGFloat, _ g: CGFloat, _ b: CGFloat, _ a: CGFloat = 1) -> NSColor { NSColor(red: r / 255, green: g / 255, blue: b / 255, alpha: a) }

let inset: CGFloat = 100
let tile = NSRect(x: inset, y: inset, width: size - inset * 2, height: size - inset * 2)
let shape = NSBezierPath(roundedRect: tile, xRadius: 190, yRadius: 190)
NSGraphicsContext.current?.saveGraphicsState()
let shadow = NSShadow(); shadow.shadowBlurRadius = 30; shadow.shadowOffset = NSSize(width: 0, height: -12)
shadow.shadowColor = NSColor.black.withAlphaComponent(0.3); shadow.set()
NSColor.white.setFill(); shape.fill()
NSGraphicsContext.current?.restoreGraphicsState()
shape.addClip()

let ink = C(58, 44, 72)
let blush = C(255, 140, 170, 0.75)

func eye(_ x: CGFloat, _ y: CGFloat, _ r: CGFloat) {
    ink.setFill(); NSBezierPath(ovalIn: NSRect(x: x - r, y: y - r * 1.15, width: r * 2, height: r * 2.3)).fill()
    NSColor.white.setFill(); NSBezierPath(ovalIn: NSRect(x: x - r * 0.15, y: y + r * 0.25, width: r * 0.7, height: r * 0.7)).fill()
}
func cheek(_ x: CGFloat, _ y: CGFloat) { blush.setFill(); NSBezierPath(ovalIn: NSRect(x: x - 42, y: y - 22, width: 84, height: 44)).fill() }
func smile(_ x: CGFloat, _ y: CGFloat, _ w: CGFloat) {
    let p = NSBezierPath(); p.move(to: NSPoint(x: x - w, y: y))
    p.curve(to: NSPoint(x: x + w, y: y), controlPoint1: NSPoint(x: x - w * 0.5, y: y - w * 0.9), controlPoint2: NSPoint(x: x + w * 0.5, y: y - w * 0.9))
    p.lineWidth = 16; p.lineCapStyle = .round; ink.setStroke(); p.stroke()
}

switch variant {
case "pixel":
    // The tray's pixel ghost, big, on a candy gradient.
    NSGradient(colors: [C(255, 196, 214), C(196, 178, 255)])!.draw(in: tile, angle: -60)
    let rows = [
        ".....######.....", "...##########...", "..############..", ".##############.",
        ".####.####.####.", ".####.####.####.", ".##############.", "#######..#######",
        "################", ".##############.", ".##############.", "..############..",
        "...#####..####..", "....##.....##...",
    ]
    let px: CGFloat = 40, ox = (size - 16 * px) / 2, oy = (size + CGFloat(rows.count) * px) / 2 - 10
    for (r, line) in rows.enumerated() {
        for (c, ch) in line.enumerated() {
            let rect = NSRect(x: ox + CGFloat(c) * px, y: oy - CGFloat(r + 1) * px, width: px, height: px)
            if ch == "#" { NSColor.white.setFill(); rect.fill() }
            else if (r == 4 || r == 5) && (c == 5 || c == 10) { ink.setFill(); rect.fill() }
        }
    }
    // pixel blush
    blush.setFill()
    NSRect(x: ox + 2 * px, y: oy - 7 * px, width: px * 2, height: px * 0.7).fill()
    NSRect(x: ox + 12 * px, y: oy - 7 * px, width: px * 2, height: px * 0.7).fill()

case "hide":
    // Ghost covering one eye with a little hand: peek-a-boo!
    NSGradient(colors: [C(178, 240, 220), C(160, 208, 255)])!.draw(in: tile, angle: -70)
    let body = NSBezierPath()
    body.move(to: NSPoint(x: 250, y: 230))
    body.line(to: NSPoint(x: 250, y: 560))
    body.curve(to: NSPoint(x: 774, y: 560), controlPoint1: NSPoint(x: 250, y: 900), controlPoint2: NSPoint(x: 774, y: 900))
    body.line(to: NSPoint(x: 774, y: 230))
    for i in 0..<4 {
        let x0 = 774 - CGFloat(i) * 131
        body.curve(to: NSPoint(x: x0 - 131, y: 230), controlPoint1: NSPoint(x: x0 - 30, y: 170), controlPoint2: NSPoint(x: x0 - 100, y: 170))
    }
    body.close()
    NSGraphicsContext.current?.saveGraphicsState()
    let s2 = NSShadow(); s2.shadowBlurRadius = 24; s2.shadowOffset = NSSize(width: 0, height: -10); s2.shadowColor = C(60, 90, 120, 0.25); s2.set()
    NSColor.white.setFill(); body.fill()
    NSGraphicsContext.current?.restoreGraphicsState()
    eye(430, 570, 34)
    // closed, happy eye
    let e = NSBezierPath(); e.move(to: NSPoint(x: 560, y: 572))
    e.curve(to: NSPoint(x: 640, y: 572), controlPoint1: NSPoint(x: 580, y: 610), controlPoint2: NSPoint(x: 620, y: 610))
    e.lineWidth = 16; e.lineCapStyle = .round; ink.setStroke(); e.stroke()
    cheek(380, 490); cheek(660, 490)
    smile(512, 480, 34)
    // little hand peeking over the right eye
    NSGraphicsContext.current?.saveGraphicsState()
    let s3 = NSShadow(); s3.shadowBlurRadius = 10; s3.shadowOffset = NSSize(width: 0, height: -4); s3.shadowColor = C(60, 90, 120, 0.25); s3.set()
    NSColor.white.setFill()
    NSBezierPath(ovalIn: NSRect(x: 560, y: 520, width: 150, height: 120)).fill()
    NSGraphicsContext.current?.restoreGraphicsState()

default: // "peek": ghost peeking over the bottom edge, holding on with both hands
    NSGradient(colors: [C(255, 214, 196), C(214, 190, 255)])!.draw(in: tile, angle: -75)
    let ledgeY: CGFloat = 330
    let body = NSBezierPath()
    body.move(to: NSPoint(x: 240, y: ledgeY - 10))
    body.line(to: NSPoint(x: 240, y: 560))
    body.curve(to: NSPoint(x: 784, y: 560), controlPoint1: NSPoint(x: 240, y: 920), controlPoint2: NSPoint(x: 784, y: 920))
    body.line(to: NSPoint(x: 784, y: ledgeY - 10))
    body.close()
    NSGraphicsContext.current?.saveGraphicsState()
    let s2 = NSShadow(); s2.shadowBlurRadius = 24; s2.shadowOffset = NSSize(width: 0, height: -10); s2.shadowColor = C(120, 80, 140, 0.25); s2.set()
    NSColor.white.setFill(); body.fill()
    NSGraphicsContext.current?.restoreGraphicsState()
    // the "screen" it peeks over
    let ledge = NSRect(x: 100, y: 100, width: 824, height: ledgeY - 100)
    C(92, 74, 140).setFill(); ledge.fill()
    C(255, 255, 255, 0.12).setFill(); NSRect(x: 100, y: ledgeY - 18, width: 824, height: 18).fill()
    // tiny pulse line on the screen
    let pl = NSBezierPath()
    let pts: [(CGFloat, CGFloat)] = [(200, 215), (380, 215), (430, 275), (490, 160), (545, 245), (585, 215), (824, 215)]
    pl.move(to: NSPoint(x: pts[0].0, y: pts[0].1)); for p in pts.dropFirst() { pl.line(to: NSPoint(x: p.0, y: p.1)) }
    pl.lineWidth = 22; pl.lineJoinStyle = .round; pl.lineCapStyle = .round; C(130, 240, 200).setStroke(); pl.stroke()
    eye(420, 560, 36); eye(604, 560, 36)
    cheek(345, 470); cheek(679, 470)
    smile(512, 470, 30)
    // hands gripping the edge
    NSColor.white.setFill()
    for x in [300, 664] as [CGFloat] { NSBezierPath(roundedRect: NSRect(x: x, y: ledgeY - 40, width: 60, height: 80), xRadius: 30, yRadius: 30).fill() }
}
NSGraphicsContext.restoreGraphicsState()
try! rep.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: out))
