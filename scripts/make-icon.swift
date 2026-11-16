import AppKit
// Draws the 1024px app icon: a rounded tile with a pulse line through a gauge arc.
let size: CGFloat = 1024
let out = CommandLine.arguments[1]
let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: Int(size), pixelsHigh: Int(size), bitsPerSample: 8,
                           samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
NSGraphicsContext.saveGraphicsState()
NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
let inset: CGFloat = 100
let tile = NSRect(x: inset, y: inset, width: size - inset * 2, height: size - inset * 2)
let shape = NSBezierPath(roundedRect: tile, xRadius: 185, yRadius: 185)
NSGraphicsContext.current?.saveGraphicsState()
let shadow = NSShadow(); shadow.shadowBlurRadius = 30; shadow.shadowOffset = NSSize(width: 0, height: -12)
shadow.shadowColor = NSColor.black.withAlphaComponent(0.35); shadow.set()
NSColor.black.setFill(); shape.fill()
NSGraphicsContext.current?.restoreGraphicsState()
shape.addClip()
NSGradient(colors: [NSColor(red: 0.10, green: 0.14, blue: 0.32, alpha: 1), NSColor(red: 0.22, green: 0.30, blue: 0.78, alpha: 1), NSColor(red: 0.50, green: 0.32, blue: 0.92, alpha: 1)])!
    .draw(in: tile, angle: 60)
let c = NSPoint(x: size / 2, y: size / 2 - 10)
let track = NSBezierPath(); track.appendArc(withCenter: c, radius: 270, startAngle: 210, endAngle: -30, clockwise: true)
track.lineWidth = 46; track.lineCapStyle = .round
NSColor.white.withAlphaComponent(0.18).setStroke(); track.stroke()
let arc = NSBezierPath(); arc.appendArc(withCenter: c, radius: 270, startAngle: 210, endAngle: 40, clockwise: true)
arc.lineWidth = 46; arc.lineCapStyle = .round
NSColor(red: 0.45, green: 0.92, blue: 0.78, alpha: 1).setStroke(); arc.stroke()
let pulse = NSBezierPath()
let pts: [(CGFloat, CGFloat)] = [(-250, 0), (-110, 0), (-60, 120), (0, -150), (55, 70), (95, 0), (250, 0)]
pulse.move(to: NSPoint(x: c.x + pts[0].0, y: c.y + pts[0].1))
for p in pts.dropFirst() { pulse.line(to: NSPoint(x: c.x + p.0, y: c.y + p.1)) }
pulse.lineWidth = 40; pulse.lineJoinStyle = .round; pulse.lineCapStyle = .round
NSColor.white.setStroke(); pulse.stroke()
NSGraphicsContext.restoreGraphicsState()
try! rep.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: out))
