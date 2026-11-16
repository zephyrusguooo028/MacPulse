import SwiftUI
import AppKit
import Charts
import IOKit
import IOKit.ps

// MARK: - Reading the smart battery

/// Everything AppleSmartBattery exposes that is worth showing. Every field is optional:
/// the registry entry differs between Intel and Apple silicon, and desktops have a stub.
struct BatteryDetail {
    var present = false
    var percent: Double = 0
    var cycleCount: Int?
    var designCapacity: Int?      // mAh
    var maxCapacity: Int?         // mAh, what a full charge holds today
    var currentCapacity: Int?     // mAh
    var temperature: Double?      // °C
    var voltage: Double?          // V
    var amperage: Double?         // A, negative while discharging
    var isCharging = false
    var externalConnected = false
    var fullyCharged = false
    var minutesToEmpty: Int?
    var minutesToFull: Int?
    var adapterWatts: Int?
    var adapterName: String?

    var health: Double? {
        guard let d = designCapacity, d > 0, let m = maxCapacity, m > 0 else { return nil }
        return Double(m) / Double(d)
    }
    /// Watts flowing in or out of the battery.
    var power: Double? {
        guard let v = voltage, let a = amperage, v > 0 else { return nil }
        return v * a
    }
}

@MainActor
enum BatteryDetailReader {
    static func read() -> BatteryDetail {
        if Demo.active { return demo() }
        var b = BatteryDetail()
        let svc = IOServiceGetMatchingService(kIOMainPortDefault, IOServiceMatching("AppleSmartBattery"))
        var d: [String: Any] = [:]
        if svc != 0 {
            var props: Unmanaged<CFMutableDictionary>?
            if IORegistryEntryCreateCFProperties(svc, &props, kCFAllocatorDefault, 0) == KERN_SUCCESS,
               let dict = props?.takeRetainedValue() as? [String: Any] { d = dict }
            IOObjectRelease(svc)
        }
        func num(_ k: String) -> Int64? { (d[k] as? NSNumber)?.int64Value }
        func pos(_ k: String) -> Int? { num(k).flatMap { $0 > 0 ? Int($0) : nil } }

        b.designCapacity = pos("DesignCapacity")
        b.cycleCount = num("CycleCount").map { Int($0) }
        // On Apple silicon MaxCapacity is a percentage, so only use it when it looks like mAh.
        b.maxCapacity = pos("AppleRawMaxCapacity") ?? pos("NominalChargeCapacity") ?? pos("MaxCapacity").flatMap { $0 > 100 ? $0 : nil }
        b.currentCapacity = pos("AppleRawCurrentCapacity") ?? pos("CurrentCapacity").flatMap { $0 > 100 ? $0 : nil }
        if let t = num("Temperature"), t > 0 { b.temperature = Double(t) / 100 }
        if let v = num("Voltage"), v > 0 { b.voltage = Double(v) / 1000 }
        if let a = num("Amperage"), b.voltage != nil { b.amperage = Double(Int32(truncatingIfNeeded: a)) / 1000 }
        b.isCharging = d["IsCharging"] as? Bool ?? false
        b.externalConnected = d["ExternalConnected"] as? Bool ?? false
        b.fullyCharged = d["FullyCharged"] as? Bool ?? false
        if let t = pos("AvgTimeToEmpty") ?? pos("TimeRemaining"), t < 65535 { b.minutesToEmpty = t }
        if let t = pos("AvgTimeToFull"), t < 65535 { b.minutesToFull = t }

        var adapter = d["AdapterDetails"] as? [String: Any]
        if (adapter?["Watts"] as? NSNumber) == nil, let raw = d["AppleRawAdapterDetails"] as? [[String: Any]] {
            adapter = raw.first { ($0["Watts"] as? NSNumber) != nil } ?? adapter
        }
        if (adapter?["Watts"] as? NSNumber) == nil, let ext = IOPSCopyExternalPowerAdapterDetails()?.takeRetainedValue() as? [String: Any] {
            adapter = ext
        }
        if let w = (adapter?["Watts"] as? NSNumber)?.intValue, w > 0 { b.adapterWatts = w }
        b.adapterName = (adapter?["Name"] as? String) ?? (adapter?["Description"] as? String)

        let ps = BatteryReader.read()
        // IOPS only lists an internal battery when one is installed; desktops keep a zeroed stub.
        b.present = ps != nil
        if let ps {
            b.percent = ps.percent
            b.isCharging = b.isCharging || ps.charging
            b.externalConnected = b.externalConnected || ps.onAC
            if b.minutesToEmpty == nil, !ps.onAC { b.minutesToEmpty = ps.minutesLeft }
        } else if let c = b.currentCapacity, let m = b.maxCapacity, m > 0 {
            b.percent = Double(c) / Double(m)
        }
        if b.externalConnected { b.minutesToEmpty = nil }
        return b
    }

    static func demo() -> BatteryDetail {
        var b = BatteryDetail()
        b.present = true
        b.percent = BatteryHistory.shared.points.last?.p ?? 0.76
        b.cycleCount = 214
        b.designCapacity = 6075
        b.maxCapacity = 5528
        b.currentCapacity = Int(Double(5528) * b.percent)
        b.temperature = 31.4
        b.voltage = 12.62
        b.amperage = -0.84
        b.minutesToEmpty = Int(b.percent * 520)
        b.adapterWatts = 96
        b.adapterName = "96W USB-C Power Adapter"
        return b
    }

    /// Battery % for the sidebar badge and the widget; nil on desktops.
    @MainActor
    static var percentNow: Double? {
        if Demo.active { return BatteryHistory.shared.points.last?.p ?? 0.76 }
        return SystemMonitor.shared.battery?.percent
    }
}

// MARK: - History (one point every 5 minutes, 7 days)

struct BatteryPoint: Codable, Identifiable {
    let t: Date
    let p: Double     // 0…1
    let c: Bool       // charging or on power
    var id: Date { t }
}

@MainActor
@Observable
final class BatteryHistory {
    static let shared = BatteryHistory()
    private(set) var points: [BatteryPoint] = []
    @ObservationIgnored private var timer: Timer?
    @ObservationIgnored private let url = Paths.support.appendingPathComponent("battery-history.json")
    private static let keep: TimeInterval = 7 * 86400
    private static let step: TimeInterval = 300

    private init() {
        if Demo.active { points = Self.demoPoints(); return }
        if let data = try? Data(contentsOf: url), let p = try? JSONDecoder().decode([BatteryPoint].self, from: data) {
            points = p.filter { $0.t > Date().addingTimeInterval(-Self.keep) }
        }
    }

    /// Starts recording in the background. Cheap: one IOPS read every 5 minutes.
    func start() {
        guard timer == nil, !Demo.active else { return }
        record()
        let t = Timer(timeInterval: Self.step, repeats: true) { _ in
            MainActor.assumeIsolated { BatteryHistory.shared.record() }
        }
        t.tolerance = 20
        RunLoop.main.add(t, forMode: .common)
        timer = t
    }

    private func record() {
        guard let b = BatteryReader.read() else { return }
        let now = Date()
        if let last = points.last, now.timeIntervalSince(last.t) < Self.step - 30 { return }
        points.append(BatteryPoint(t: now, p: b.percent, c: b.charging || b.onAC))
        points.removeAll { $0.t < now.addingTimeInterval(-Self.keep) }
        if let data = try? JSONEncoder().encode(points) { try? data.write(to: url, options: .atomic) }
    }

    /// A believable laptop week: charged overnight, two stretches on battery each day.
    private static func demoPoints() -> [BatteryPoint] {
        let now = Date()
        let n = Int(keep / step)
        var level = 0.62
        var out: [BatteryPoint] = []
        // The daily pattern is shifted so "now" always lands mid-afternoon, on battery.
        let c0 = Calendar.current.dateComponents([.hour, .minute], from: now)
        let shift = 15.5 - (Double(c0.hour ?? 0) + Double(c0.minute ?? 0) / 60)
        for i in 0..<n {
            let t = now.addingTimeInterval(Double(i - n + 1) * step)
            let c = Calendar.current.dateComponents([.hour, .minute], from: t)
            let h = (Double(c.hour ?? 0) + Double(c.minute ?? 0) / 60 + shift + 48).truncatingRemainder(dividingBy: 24)
            let charging = h < 7.5 || (h >= 13 && h < 14.5) || h >= 22.5
            if charging {
                level = min(1, level + (level > 0.8 ? 0.012 : 0.03))
            } else {
                level = max(0.07, level - (0.006 + 0.004 * abs(sin(Double(i) / 9))))
            }
            out.append(BatteryPoint(t: t, p: level, c: charging))
        }
        return out
    }
}

// MARK: - Page

struct BatteryView: View {
    @MainActor static var badge: String? { BatteryDetailReader.percentNow.map { Fmt.pct($0) } }

    @Environment(AppState.self) private var app
    @Local private var detail = BatteryDetailReader.read()
    @Local private var range = 0   // 0 = 24 h, 1 = 7 days

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                PageHeader(title: L("电池", "Battery"), subtitle: subtitle) { EmptyView() }
                if detail.present {
                    HStack(alignment: .top, spacing: 14) {
                        chargeCard.frame(maxWidth: .infinity)
                        healthCard.frame(maxWidth: .infinity)
                        powerCard.frame(maxWidth: .infinity)
                    }
                    historyCard
                    hogsCard
                } else {
                    Card(L("没有电池", "No battery"), symbol: "powerplug") {
                        EmptyHint(symbol: "powerplug", title: L("这台 Mac 一直插着电", "This Mac runs on mains power"),
                                  detail: L("台式 Mac 没有电池，所以没有电量、健康度和充电记录。", "Desktop Macs have no battery, so there is no charge, health or charging history to show."))
                        if detail.adapterWatts != nil || detail.adapterName != nil {
                            Divider()
                            StatLine(label: L("电源适配器", "Power adapter"), value: adapterText)
                        }
                    }
                }
            }
            .padding(24)
        }
        .onAppear { app.monitor.wantProcesses(true) }
        .onDisappear { app.monitor.wantProcesses(false) }
        .task {
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(10))
                detail = BatteryDetailReader.read()
            }
        }
    }

    private var subtitle: String {
        guard detail.present else { return L("台式机 · 外接电源", "Desktop · mains power") }
        return "\(Fmt.pct(detail.percent)) · \(statusText)"
    }

    private var statusText: String {
        if detail.fullyCharged && detail.externalConnected { return L("已充满", "Fully charged") }
        if detail.isCharging { return L("充电中", "Charging") }
        if detail.externalConnected { return L("已接电源，未充电", "Plugged in, not charging") }
        return L("使用电池", "On battery")
    }

    private var adapterText: String {
        [detail.adapterWatts.map { "\($0) W" }, detail.adapterName].compactMap { $0 }.joined(separator: " · ")
    }

    private static func duration(_ m: Int) -> String {
        L("\(m / 60) 小时 \(m % 60) 分", "\(m / 60)h \(m % 60)m")
    }

    private var chargeColor: Color {
        detail.percent < 0.2 && !detail.externalConnected ? Palette.danger : (detail.percent < 0.35 ? Palette.warn : Palette.disk)
    }

    private var chargeCard: some View {
        Card(L("电量", "Charge"), symbol: detail.isCharging ? "battery.100percent.bolt" : "battery.75percent") {
            HStack(spacing: 14) {
                Ring(value: detail.percent, color: chargeColor, label: Fmt.pct(detail.percent)).frame(width: 72, height: 72)
                VStack(spacing: 6) {
                    StatLine(label: L("状态", "Status"), value: statusText, color: chargeColor)
                    if let m = detail.minutesToEmpty, !detail.externalConnected {
                        StatLine(label: L("剩余", "Time left"), value: Self.duration(m))
                    } else if let m = detail.minutesToFull, detail.isCharging {
                        StatLine(label: L("充满还要", "Until full"), value: Self.duration(m))
                    }
                    if let c = detail.currentCapacity { StatLine(label: L("当前容量", "Current charge"), value: "\(c) mAh") }
                }
            }
        }
    }

    private var healthCard: some View {
        let h = detail.health
        let color = (h ?? 1) < 0.8 ? Palette.warn : Palette.disk
        return Card(L("健康度", "Health"), symbol: "heart.text.square") {
            HStack(spacing: 14) {
                Ring(value: min(h ?? 0, 1), color: color, label: h.map { Fmt.pct(min($0, 1)) } ?? "—").frame(width: 72, height: 72)
                VStack(spacing: 6) {
                    StatLine(label: L("循环次数", "Cycle count"), value: detail.cycleCount.map { "\($0)" } ?? "—")
                    StatLine(label: L("设计容量", "Design capacity"), value: detail.designCapacity.map { "\($0) mAh" } ?? "—")
                    StatLine(label: L("满电容量", "Full charge capacity"), value: detail.maxCapacity.map { "\($0) mAh" } ?? "—", color: color)
                }
            }
        }
    }

    private var powerCard: some View {
        Card(L("电源", "Power"), symbol: "bolt") {
            VStack(spacing: 6) {
                StatLine(label: L("温度", "Temperature"), value: detail.temperature.map { String(format: "%.1f °C", $0) } ?? "—")
                StatLine(label: L("电压", "Voltage"), value: detail.voltage.map { String(format: "%.2f V", $0) } ?? "—")
                StatLine(label: L("电流", "Current"), value: detail.amperage.map { String(format: "%.2f A", $0) } ?? "—")
                StatLine(label: detail.amperage.map { $0 >= 0 } ?? false ? L("充入功率", "Charging power") : L("耗电功率", "Draw"),
                         value: detail.power.map { String(format: "%.1f W", abs($0)) } ?? "—")
                StatLine(label: L("电源适配器", "Power adapter"), value: detail.externalConnected || Demo.active ? (adapterText.isEmpty ? L("已连接", "Connected") : adapterText) : L("未连接", "Not connected"))
            }
        }
    }

    // MARK: History chart

    private var historyCard: some View {
        let all = BatteryHistory.shared.points
        let cutoff = Date().addingTimeInterval(range == 0 ? -86400 : -7 * 86400)
        let pts = all.filter { $0.t >= cutoff }
        return Card(L("电量记录", "Charge history"), symbol: "chart.xyaxis.line", trailing: {
            Picker("", selection: $range) {
                Text(L("24 小时", "24 hours")).tag(0)
                Text(L("7 天", "7 days")).tag(1)
            }
            .pickerStyle(.segmented).labelsHidden().frame(width: 180)
        }) {
            if pts.count < 2 {
                EmptyHint(symbol: "clock", title: L("正在记录", "Recording"),
                          detail: L("Peekaboo 每 5 分钟记一次电量，保留 7 天。运行一会儿就能看到曲线。", "Peekaboo records the charge every 5 minutes and keeps 7 days. Leave it running for a while to see the chart."))
            } else {
                Chart {
                    ForEach(chargingSpans(pts), id: \.0) { s in
                        RectangleMark(xStart: .value(L("开始", "Start"), s.0), xEnd: .value(L("结束", "End"), s.1), yStart: .value("", 0), yEnd: .value("", 100))
                            .foregroundStyle(Palette.disk.opacity(0.10))
                    }
                    ForEach(pts) { p in
                        AreaMark(x: .value(L("时间", "Time"), p.t), y: .value(L("电量", "Charge"), p.p * 100))
                            .foregroundStyle(LinearGradient(colors: [Palette.theme.opacity(0.3), Palette.theme.opacity(0.02)], startPoint: .top, endPoint: .bottom))
                        LineMark(x: .value(L("时间", "Time"), p.t), y: .value(L("电量", "Charge"), p.p * 100))
                            .foregroundStyle(Palette.theme)
                    }
                }
                .chartYScale(domain: 0...100)
                // A little room on the right so the last time label is not clipped.
                .chartXScale(domain: cutoff...Date().addingTimeInterval(range == 0 ? 2400 : 6 * 3600))
                .chartYAxis { AxisMarks(values: [0, 25, 50, 75, 100]) { v in AxisGridLine(); AxisValueLabel { Text("\(v.as(Int.self) ?? 0)%") } } }
                .chartXAxis {
                    AxisMarks(values: range == 0 ? .stride(by: .hour, count: 3) : .stride(by: .day)) { v in
                        AxisGridLine()
                        AxisValueLabel { if let d = v.as(Date.self) { Text(range == 0 ? Fmt.time(d) : Self.day(d)) } }
                    }
                }
                .frame(height: 200)
                FlowLegend(items: [(Palette.theme, L("电量", "Charge"), Fmt.pct(pts.last?.p ?? 0)),
                                   (Palette.disk.opacity(0.4), L("接着电源", "On power"), "")])
            }
        }
    }

    private static func day(_ d: Date) -> String {
        let f = DateFormatter()
        f.locale = Loc.locale
        f.setLocalizedDateFormatFromTemplate("EEE d")
        return f.string(from: d)
    }

    /// Runs of consecutive "on power" samples, drawn as a shaded band.
    private func chargingSpans(_ pts: [BatteryPoint]) -> [(Date, Date)] {
        var out: [(Date, Date)] = []
        var start: Date?
        for (i, p) in pts.enumerated() {
            if p.c, start == nil { start = p.t }
            let end = !p.c || i == pts.count - 1
            if end, let s = start { out.append((s, p.t)); start = nil }
        }
        return out
    }

    // MARK: Energy hogs

    private var hogsCard: some View {
        let m = app.monitor
        let onBattery = !detail.externalConnected
        let list = m.groups
            .map { ($0, m.cpuAverage[$0.id] ?? $0.cpu) }
            .filter { $0.1 > 0.005 }
            .sorted { $0.1 > $1.1 }
            .prefix(6)
        let top = max(list.first?.1 ?? 1, 0.01)
        return Card(L("耗电大户（估算）", "Energy hogs (estimate)"), symbol: "flame", trailing: {
            Text(onBattery ? L("正在使用电池", "On battery") : L("当前接着电源", "Plugged in")).font(.caption).foregroundStyle(.secondary)
        }) {
            if list.isEmpty {
                Text(L("现在没有明显耗电的应用。", "No app is using much energy right now.")).foregroundStyle(.secondary)
            } else {
                VStack(spacing: 8) {
                    ForEach(Array(list), id: \.0.id) { g, cpu in
                        HStack(spacing: 8) {
                            Image(nsImage: ProcessControl.icon(for: g)).resizable().frame(width: 18, height: 18)
                            Text(g.name).lineLimit(1).frame(width: 180, alignment: .leading)
                            SizeBar(fraction: cpu / top, color: cpu > 0.8 ? Palette.danger : (cpu > 0.3 ? Palette.warn : Palette.theme))
                            Text(String(format: "%.1f%%", cpu * 100)).monospacedDigit().frame(width: 64, alignment: .trailing)
                        }
                        .font(.callout)
                    }
                }
            }
            Text(onBattery
                 ? L("按最近一段时间的平均 CPU 排序，CPU 越高通常越耗电。这是估算，不含屏幕、GPU 和网络的耗电。",
                     "Sorted by recent average CPU; higher CPU usually means more battery drain. This is an estimate and leaves out display, GPU and network power.")
                 : L("拔掉电源后，这些应用最可能耗电。按最近一段时间的平均 CPU 估算。",
                     "Once unplugged, these apps are the most likely to drain the battery. Estimated from recent average CPU."))
                .font(.caption).foregroundStyle(.secondary)
        }
    }
}
