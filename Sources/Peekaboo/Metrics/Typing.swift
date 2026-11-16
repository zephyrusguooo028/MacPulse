import AppKit
import CoreGraphics
import Observation

/// One day of typing statistics. Only counts are stored: which key and which app,
/// never the order of keys, so nothing that was typed can be reconstructed.
struct TypingDay: Codable {
    var total = 0
    var hours = Array(repeating: 0, count: 24)
    var keys: [String: Int] = [:]      // key code → presses
    var apps: [String: Int] = [:]      // app name → presses
    var activeSeconds: Double = 0      // time spent typing (gaps under 5 s)
    var longestStreak: Double = 0      // longest continuous typing, seconds
}

@MainActor
@Observable
final class TypingMonitor {
    static let shared = TypingMonitor()

    private(set) var days: [String: TypingDay] = [:]
    private(set) var running = false
    private(set) var permissionGranted = CGPreflightListenEventAccess()

    @ObservationIgnored private var tap: CFMachPort?
    @ObservationIgnored private var source: CFRunLoopSource?
    @ObservationIgnored private var frontApp = "未知"
    @ObservationIgnored private var lastKey: Date?
    @ObservationIgnored private var streakStart: Date?
    @ObservationIgnored private var dirty = false
    @ObservationIgnored private var saveTimer: Timer?
    @ObservationIgnored private let url = Paths.support.appendingPathComponent("typing.json")

    private init() {
        if let d = try? Data(contentsOf: url), let decoded = try? JSONDecoder().decode([String: TypingDay].self, from: d) {
            days = decoded
        }
        NSWorkspace.shared.notificationCenter.addObserver(forName: NSWorkspace.didActivateApplicationNotification, object: nil, queue: .main) { n in
            let name = (n.userInfo?[NSWorkspace.applicationUserInfoKey] as? NSRunningApplication)?.localizedName
            MainActor.assumeIsolated { TypingMonitor.shared.frontApp = name ?? "未知" }
        }
        frontApp = NSWorkspace.shared.frontmostApplication?.localizedName ?? "未知"
    }

    static func dayKey(_ d: Date = Date()) -> String {
        let f = DateFormatter()
        f.dateFormat = "yyyy-MM-dd"
        return f.string(from: d)
    }

    var today: TypingDay { days[Self.dayKey()] ?? TypingDay() }

    func day(offset: Int) -> TypingDay {
        days[Self.dayKey(Calendar.current.date(byAdding: .day, value: -offset, to: Date())!)] ?? TypingDay()
    }

    /// Starts or stops the listener according to the setting.
    @ObservationIgnored private var retry: Timer?

    func apply() {
        permissionGranted = CGPreflightListenEventAccess()
        if AppSettings.shared.typingEnabled { start() } else { stop() }
        // Waiting for permission: keep retrying in the background, not only on the Typing page.
        retry?.invalidate(); retry = nil
        if AppSettings.shared.typingEnabled && !running {
            retry = Timer.scheduledTimer(withTimeInterval: 3, repeats: true) { _ in
                MainActor.assumeIsolated {
                    let t = TypingMonitor.shared
                    t.permissionGranted = CGPreflightListenEventAccess()
                    if t.permissionGranted { t.retry?.invalidate(); t.retry = nil; t.start() }
                }
            }
        }
    }

    func requestPermission() {
        _ = CGRequestListenEventAccess()
        permissionGranted = CGPreflightListenEventAccess()
        if !permissionGranted, let u = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_ListenEvent") {
            NSWorkspace.shared.open(u)
        }
    }

    private func start() {
        guard tap == nil else { NSLog("Peekaboo typing: already running"); return }
        permissionGranted = CGPreflightListenEventAccess()
        NSLog("Peekaboo typing: start, listen permission = \(permissionGranted)")
        guard permissionGranted else { running = false; return }
        let mask = (1 << CGEventType.keyDown.rawValue) | (1 << CGEventType.flagsChanged.rawValue)
        // Listen-only: events pass through untouched.
        guard let t = CGEvent.tapCreate(tap: .cgSessionEventTap, place: .tailAppendEventTap, options: .listenOnly,
                                        eventsOfInterest: CGEventMask(mask), callback: typingTapCallback, userInfo: nil) else {
            NSLog("Peekaboo typing: tapCreate returned nil")
            running = false
            return
        }
        tap = t
        source = CFMachPortCreateRunLoopSource(kCFAllocatorDefault, t, 0)
        CFRunLoopAddSource(CFRunLoopGetMain(), source, .commonModes)
        CGEvent.tapEnable(tap: t, enable: true)
        running = true
        NSLog("Peekaboo typing: tap running")
        saveTimer = Timer.scheduledTimer(withTimeInterval: 60, repeats: true) { _ in
            MainActor.assumeIsolated { TypingMonitor.shared.save() }
        }
    }

    private func stop() {
        if let t = tap { CGEvent.tapEnable(tap: t, enable: false) }
        if let s = source { CFRunLoopRemoveSource(CFRunLoopGetMain(), s, .commonModes) }
        tap = nil; source = nil
        saveTimer?.invalidate(); saveTimer = nil
        running = false
        save()
    }

    fileprivate func reenable() { if let t = tap { CGEvent.tapEnable(tap: t, enable: true) } }

    fileprivate func record(keyCode: Int) {
        let now = Date()
        let key = Self.dayKey(now)
        var d = days[key] ?? TypingDay()
        d.total += 1
        d.hours[Calendar.current.component(.hour, from: now)] += 1
        d.keys[String(keyCode), default: 0] += 1
        d.apps[frontApp, default: 0] += 1
        if let last = lastKey, now.timeIntervalSince(last) < 5 {
            d.activeSeconds += now.timeIntervalSince(last)
            if let s = streakStart { d.longestStreak = max(d.longestStreak, now.timeIntervalSince(s)) }
        } else {
            streakStart = now
        }
        lastKey = now
        days[key] = d
        dirty = true
    }

    func save() {
        guard dirty else { return }
        dirty = false
        // Keep 120 days.
        let cutoff = Self.dayKey(Calendar.current.date(byAdding: .day, value: -120, to: Date())!)
        days = days.filter { $0.key >= cutoff }
        if let data = try? JSONEncoder().encode(days) { try? data.write(to: url, options: .atomic) }
    }

    /// Screenshot runs only: fills a week with made-up counts in memory (never saved).
    func loadDemo() {
        var rng = SystemRandomNumberGenerator()
        for off in 0..<30 {
            var d = TypingDay()
            for h in 9..<23 { d.hours[h] = Int.random(in: 100...1400, using: &rng) }
            d.total = d.hours.reduce(0, +)
            for row in KeyboardLayout.rows { for k in row { d.keys[String(k.code)] = Int.random(in: 0...(k.code == 49 ? 1800 : 600), using: &rng) } }
            d.apps = ["Cursor": d.total / 2, "微信": d.total / 4, "飞书": d.total / 6, "Safari": d.total / 12]
            d.activeSeconds = Double(d.total) / 3.2
            d.longestStreak = 1500
            days[Self.dayKey(Calendar.current.date(byAdding: .day, value: -off, to: Date())!)] = d
        }
    }

    func clearAll() {
        days = [:]
        dirty = true
        save()
        try? FileManager.default.removeItem(at: url)
    }
}

/// Modifier keys arrive as flagsChanged; count only the press, not the release.
private func modifierIsDown(_ code: Int64, _ flags: CGEventFlags) -> Bool {
    switch code {
    case 56, 60: return flags.contains(.maskShift)
    case 59, 62: return flags.contains(.maskControl)
    case 58, 61: return flags.contains(.maskAlternate)
    case 55, 54: return flags.contains(.maskCommand)
    case 57: return flags.contains(.maskAlphaShift)
    case 63: return flags.contains(.maskSecondaryFn)
    default: return false
    }
}

private let typingTapCallback: CGEventTapCallBack = { _, type, event, _ in
    if type == .tapDisabledByTimeout || type == .tapDisabledByUserInput {
        DispatchQueue.main.async { MainActor.assumeIsolated { TypingMonitor.shared.reenable() } }
        return Unmanaged.passUnretained(event)
    }
    let code = event.getIntegerValueField(.keyboardEventKeycode)
    if type == .keyDown {
        // Holding a key down repeats; count it once.
        if event.getIntegerValueField(.keyboardEventAutorepeat) == 0 {
            MainActor.assumeIsolated { TypingMonitor.shared.record(keyCode: Int(code)) }
        }
    } else if type == .flagsChanged, modifierIsDown(code, event.flags) {
        MainActor.assumeIsolated { TypingMonitor.shared.record(keyCode: Int(code)) }
    }
    return Unmanaged.passUnretained(event)
}

/// ANSI keyboard layout with macOS key codes, for the heatmap.
enum KeyboardLayout {
    struct Key {
        let code: Int; let raw: String; let width: Double
        var label: String { code == 49 ? L("空格", "Space") : raw }
    }

    static let rows: [[Key]] = [
        [k(50, "`"), k(18, "1"), k(19, "2"), k(20, "3"), k(21, "4"), k(23, "5"), k(22, "6"), k(26, "7"), k(28, "8"), k(25, "9"), k(29, "0"), k(27, "-"), k(24, "="), k(51, "⌫", 1.5)],
        [k(48, "⇥", 1.5), k(12, "Q"), k(13, "W"), k(14, "E"), k(15, "R"), k(17, "T"), k(16, "Y"), k(32, "U"), k(34, "I"), k(31, "O"), k(35, "P"), k(33, "["), k(30, "]"), k(42, "\\")],
        [k(57, "⇪", 1.75), k(0, "A"), k(1, "S"), k(2, "D"), k(3, "F"), k(5, "G"), k(4, "H"), k(38, "J"), k(40, "K"), k(37, "L"), k(41, ";"), k(39, "'"), k(36, "↩", 1.75)],
        [k(56, "⇧", 2.25), k(6, "Z"), k(7, "X"), k(8, "C"), k(9, "V"), k(11, "B"), k(45, "N"), k(46, "M"), k(43, ","), k(47, "."), k(44, "/"), k(60, "⇧", 2.25)],
        [k(63, "fn"), k(59, "⌃"), k(58, "⌥"), k(55, "⌘", 1.25), k(49, "Space", 5), k(54, "⌘", 1.25), k(61, "⌥"), k(123, "←"), k(125, "↓"), k(126, "↑"), k(124, "→")],
    ]

    private static func k(_ c: Int, _ l: String, _ w: Double = 1) -> Key { Key(code: c, raw: l, width: w) }

    static var labels: [Int: String] {
        var m: [Int: String] = [:]
        for row in rows { for key in row { m[key.code] = key.label } }
        m[53] = "esc"; m[117] = "⌦"; m[76] = "⌤"
        return m
    }
}
