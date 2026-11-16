import AppKit
import SwiftUI

/// The menu bar item, driven directly through AppKit. Setting `button.image` each
/// animation frame is far cheaper than letting SwiftUI rebuild a MenuBarExtra label.
@MainActor
final class StatusItemController: NSObject, NSPopoverDelegate {
    static let shared = StatusItemController()

    private var item: NSStatusItem?
    private let popover = NSPopover()
    private var lastKey = ""
    private var lastTitle: String?
    private var graphItem: NSStatusItem?
    private var lastGraphSample: Date?
    /// Pre-rendered icon frames, keyed by icon, frame and tint.
    private var frames: [String: NSImage] = [:]

    func install() {
        let it = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        it.button?.target = self
        it.button?.action = #selector(toggle)
        it.button?.imagePosition = .imageOnly
        item = it

        popover.behavior = .transient
        popover.animates = true
        popover.delegate = self
        refresh()
    }

    /// Redraws the icon and text. Skips work when nothing visible changed.
    func refresh() {
        guard let button = item?.button else { return }
        let m = SystemMonitor.shared
        let s = AppSettings.shared

        // Text: the ticked metrics, joined.
        let text = s.menuMetrics.compactMap { $0.text(m) }.joined(separator: "  ")
        setTitle(text.isEmpty ? nil : text, on: button)

        // Icon: cached bitmap per (icon, frame, tint).
        let icon = s.menuIcon
        let frame = IconAnimator.shared.frame % icon.frameCount
        let bucket = s.iconTint ? IconRenderer.cpuBucket(m.cpu.total) : -1
        let barsLevel = icon == .bars ? Int(m.cpu.total * 4) : 0
        let key = "\(icon.rawValue)-\(frame)-\(bucket)-\(barsLevel)"
        if key != lastKey {
            lastKey = key
            if icon == .none {
                button.image = text.isEmpty && !s.isShown(.cpuGraph) ? IconRenderer.bitmap(.pulse, frame: 0, cpu: 0, tint: nil) : nil
            } else {
                if let img = frames[key] {
                    button.image = img
                } else {
                    let img = IconRenderer.bitmap(icon, frame: frame, cpu: m.cpu.total,
                                                  tint: s.iconTint ? IconRenderer.tint(for: m.cpu.total) : nil)
                    frames[key] = img
                    button.image = img
                }
            }
        }

        // CPU graph lives in its own item so the animation never has to redraw it.
        if s.isShown(.cpuGraph) {
            if graphItem == nil {
                let g = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
                g.button?.target = self
                g.button?.action = #selector(toggle)
                graphItem = g
            }
            let t = m.samples.last?.t
            if t != lastGraphSample {
                lastGraphSample = t
                graphItem?.button?.image = IconRenderer.cpuGraph(m.samples.suffix(14).map(\.cpu))
            }
        } else if let g = graphItem {
            NSStatusBar.system.removeStatusItem(g)
            graphItem = nil
        }
    }

    private func setTitle(_ t: String?, on button: NSStatusBarButton) {
        guard t != lastTitle else { return }
        lastTitle = t
        if let t {
            button.attributedTitle = NSAttributedString(string: t, attributes: [
                .font: NSFont.monospacedDigitSystemFont(ofSize: 12, weight: .medium),
                .baselineOffset: 0.5,
            ])
            button.imagePosition = .imageLeft
        } else {
            button.title = ""
            button.imagePosition = .imageOnly
        }
    }

    @objc private func toggle(_ sender: NSStatusBarButton? = nil) {
        guard let button = sender ?? item?.button else { return }
        if popover.isShown {
            popover.performClose(nil)
        } else {
            // Content is built on open and released on close, so nothing re-renders while hidden.
            popover.contentViewController = NSHostingController(rootView: PopoverView().environment(AppState.shared))
            popover.show(relativeTo: button.bounds, of: button, preferredEdge: .minY)
            popover.contentViewController?.view.window?.makeKey()
        }
    }

    func closePopover() { if popover.isShown { popover.performClose(nil) } }

    func popoverWillShow(_ notification: Notification) {
        SystemMonitor.shared.wantProcesses(true)
        IconAnimator.shared.viewer(true)
    }

    func popoverDidClose(_ notification: Notification) {
        popover.contentViewController = nil
        SystemMonitor.shared.wantProcesses(false)
        IconAnimator.shared.viewer(false)
    }
}
