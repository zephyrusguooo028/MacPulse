import SwiftUI
import AppKit
import Charts

// MARK: - Per-app traffic from nettop

struct AppNet: Identifiable {
    let id: String
    let name: String
    let appPath: String?
    var rateIn: Double = 0
    var rateOut: Double = 0
    var totalIn: Double = 0
    var totalOut: Double = 0
    var group: AppGroup { AppGroup(id: id, name: name, appPath: appPath, pids: [], cpu: 0, memory: 0) }
}

/// One row of `nettop -P -L 1 -n -x -J bytes_in,bytes_out`: cumulative bytes per process.
struct NettopRow: Equatable {
    let name: String
    let pid: Int32
    let bytesIn: Int64
    let bytesOut: Int64
}

enum Nettop {
    /// Output looks like:
    ///     ,bytes_in,bytes_out,
    ///     mDNSResponder.217,66781981,4109502,
    ///     ZCode Helper.3531,226781,23727,
    /// Names may contain spaces (and in theory commas), so fields are taken from the right.
    static func parse(_ text: String) -> [NettopRow] {
        var out: [NettopRow] = []
        for line in text.split(whereSeparator: \.isNewline) {
            var f = line.split(separator: ",", omittingEmptySubsequences: false).map(String.init)
            if f.last == "" { f.removeLast() }
            guard f.count >= 3, let bo = Int64(f[f.count - 1]), let bi = Int64(f[f.count - 2]) else { continue }
            let label = f[0..<(f.count - 2)].joined(separator: ",")
            guard let dot = label.lastIndex(of: "."), let pid = Int32(label[label.index(after: dot)...]) else { continue }
            out.append(NettopRow(name: String(label[..<dot]), pid: pid, bytesIn: bi, bytesOut: bo))
        }
        return out
    }

    /// Runs nettop once; nil if it is missing or fails. Call off the main thread.
    static func run() -> [NettopRow]? {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/bin/nettop")
        p.arguments = ["-P", "-L", "1", "-n", "-x", "-J", "bytes_in,bytes_out"]
        let pipe = Pipe()
        p.standardOutput = pipe
        p.standardError = FileHandle.nullDevice
        do { try p.run() } catch { return nil }
        let data = pipe.fileHandleForReading.readDataToEndOfFile()
        p.waitUntilExit()
        guard p.terminationStatus == 0 else { return nil }
        return parse(String(decoding: data, as: UTF8.self))
    }
}

/// Samples nettop every 3 s while the network page is visible.
@MainActor
@Observable
final class NetSampler {
    static let shared = NetSampler()

    private(set) var apps: [AppNet] = []
    private(set) var since = Date()
    private(set) var failed = false

    @ObservationIgnored private var prev: [String: (Int64, Int64)] = [:]   // "name.pid" → cumulative bytes
    @ObservationIgnored private var prevAt: Date?
    @ObservationIgnored private var byApp: [String: AppNet] = [:]
    @ObservationIgnored private var timer: Timer?
    @ObservationIgnored private var busy = false
    @ObservationIgnored private var viewers = 0
    @ObservationIgnored private var generation = 0

    func start() {
        viewers += 1
        guard viewers == 1 else { return }
        generation += 1
        prev = [:]; prevAt = nil; byApp = [:]; apps = []; since = Date(); failed = false
        SystemMonitor.shared.wantProcesses(true)
        sample()
        let t = Timer(timeInterval: 3, repeats: true) { _ in
            MainActor.assumeIsolated { NetSampler.shared.sample() }
        }
        t.tolerance = 0.3
        RunLoop.main.add(t, forMode: .common)
        timer = t
    }

    func stop() {
        viewers = max(0, viewers - 1)
        guard viewers == 0 else { return }
        timer?.invalidate(); timer = nil
        generation += 1
        SystemMonitor.shared.wantProcesses(false)
    }

    private func sample() {
        if Demo.active { demoTick(); return }
        guard !busy else { return }
        busy = true
        let gen = generation
        DispatchQueue.global(qos: .utility).async {
            let rows = Nettop.run()
            DispatchQueue.main.async {
                MainActor.assumeIsolated {
                    let s = NetSampler.shared
                    s.busy = false
                    guard gen == s.generation else { return }
                    if let rows { s.apply(rows, at: Date()) } else { s.failed = true }
                }
            }
        }
    }

    private func apply(_ rows: [NettopRow], at now: Date) {
        failed = false
        // pid → app, so helpers are counted under their app ("Safari" rather than "com.apple.WebKit.Networking").
        var owner: [Int32: AppGroup] = [:]
        for g in SystemMonitor.shared.groups { for p in g.pids { owner[p] = g } }

        let first = prevAt == nil
        let dt = max(now.timeIntervalSince(prevAt ?? now), 0.5)
        var rates: [String: (Double, Double)] = [:]
        var next: [String: (Int64, Int64)] = [:]
        for r in rows {
            let key = "\(r.name).\(r.pid)"
            next[key] = (r.bytesIn, r.bytesOut)
            guard !first else { continue }
            var dIn = r.bytesIn, dOut = r.bytesOut
            if let p = prev[key] {
                dIn = r.bytesIn >= p.0 ? r.bytesIn - p.0 : r.bytesIn
                dOut = r.bytesOut >= p.1 ? r.bytesOut - p.1 : r.bytesOut
            }
            guard dIn > 0 || dOut > 0 else { continue }
            let g = owner[r.pid]
            let id = g?.id ?? "n:\(r.name)"
            if byApp[id] == nil { byApp[id] = AppNet(id: id, name: g?.name ?? r.name, appPath: g?.appPath) }
            byApp[id]!.totalIn += Double(dIn)
            byApp[id]!.totalOut += Double(dOut)
            let cur = rates[id] ?? (0, 0)
            rates[id] = (cur.0 + Double(dIn) / dt, cur.1 + Double(dOut) / dt)
        }
        for id in byApp.keys {
            byApp[id]!.rateIn = rates[id]?.0 ?? 0
            byApp[id]!.rateOut = rates[id]?.1 ?? 0
        }
        prev = next
        prevAt = now
        publish()
    }

    private func publish() {
        apps = byApp.values.sorted {
            let a = $0.rateIn + $0.rateOut, b = $1.rateIn + $1.rateOut
            return a != b ? a > b : ($0.totalIn + $0.totalOut) > ($1.totalIn + $1.totalOut)
        }
    }

    private func demoTick() {
        let t = Date().timeIntervalSince1970
        let sample: [(String, String?, Double, Double)] = [
            ("Safari", "/Applications/Safari.app", 820_000 + 600_000 * abs(sin(t / 5)), 42_000),
            ("Music", "/System/Applications/Music.app", 310_000 + 40_000 * sin(t / 3), 6_000),
            ("Photos", "/System/Applications/Photos.app", 18_000, 1_400_000 + 300_000 * abs(cos(t / 4))),
            ("Mail", "/System/Applications/Mail.app", 12_000 * abs(sin(t / 7)), 2_000),
            ("node", nil, 64_000, 9_000 + 5_000 * abs(sin(t))),
            ("Terminal", "/System/Applications/Utilities/Terminal.app", 900, 400),
        ]
        let dt = prevAt.map { now in Date().timeIntervalSince(now) } ?? 0
        for (name, path, i, o) in sample {
            let id = path ?? "n:\(name)"
            if byApp[id] == nil {
                // Pretend the page has been open for a few minutes so totals look lived-in.
                byApp[id] = AppNet(id: id, name: name, appPath: path, totalIn: i * 240, totalOut: o * 240)
            }
            byApp[id]!.rateIn = i; byApp[id]!.rateOut = o
            byApp[id]!.totalIn += i * dt; byApp[id]!.totalOut += o * dt
        }
        if prevAt == nil { since = Date().addingTimeInterval(-240) }
        prevAt = Date()
        publish()
    }

    /// "1.2M/s" style, for the sidebar badge.
    static func compact(_ v: Double) -> String {
        if v >= 1_073_741_824 { return String(format: "%.1fG/s", v / 1_073_741_824) }
        if v >= 1_048_576 { return String(format: v >= 10_485_760 ? "%.0fM/s" : "%.1fM/s", v / 1_048_576) }
        return String(format: "%.0fK/s", v / 1024)
    }
}

// MARK: - Page

struct NetworkView: View {
    @MainActor static var badge: String? {
        let v = SystemMonitor.shared.netIn
        return v >= 10 * 1024 ? NetSampler.compact(v) : nil
    }

    @Environment(AppState.self) private var app

    var body: some View {
        let m = app.monitor
        let s = NetSampler.shared
        let apps = s.apps
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                PageHeader(title: L("网络", "Network"), subtitle: L("每个应用的上传和下载，每 3 秒刷新", "Upload and download per app, refreshed every 3 seconds")) { EmptyView() }

                HStack(alignment: .top, spacing: 14) {
                    Card(L("当前速率", "Current rate"), symbol: "arrow.up.arrow.down") {
                        VStack(alignment: .leading, spacing: 10) {
                            rateLine("arrow.down", L("下载", "Download"), m.netIn, Palette.netIn)
                            rateLine("arrow.up", L("上传", "Upload"), m.netOut, Palette.netOut)
                            Divider()
                            StatLine(label: L("打开本页后共下载", "Downloaded since opened"), value: Fmt.bytes(apps.reduce(0) { $0 + $1.totalIn }))
                            StatLine(label: L("打开本页后共上传", "Uploaded since opened"), value: Fmt.bytes(apps.reduce(0) { $0 + $1.totalOut }))
                        }
                    }
                    .frame(width: 300)

                    Card(L("最近 5 分钟", "Last 5 minutes"), symbol: "waveform.path.ecg", trailing: {
                        FlowLegend(items: [(Palette.netIn, L("下载", "Download"), ""), (Palette.netOut, L("上传", "Upload"), "")])
                    }) {
                        liveChart(m).frame(height: 150)
                    }
                    .frame(maxWidth: .infinity)
                }

                Card(L("按应用", "By app"), symbol: "app.connected.to.app.below.fill", trailing: {
                    Text(L("自 \(Fmt.clock(s.since)) 起", "Since \(Fmt.clock(s.since))")).font(.caption).foregroundStyle(.secondary)
                }) {
                    if s.failed {
                        EmptyHint(symbol: "exclamationmark.triangle", title: L("读不到每个应用的流量", "Per-app traffic unavailable"),
                                  detail: L("系统自带的 nettop 没有返回数据。整体速率仍然准确。", "The built-in nettop tool returned nothing. The overall rates above are still accurate."))
                    } else if apps.isEmpty {
                        EmptyHint(symbol: "hourglass", title: L("正在统计", "Measuring"),
                                  detail: L("几秒后就会出现有网络活动的应用。", "Apps with network activity will appear in a few seconds."))
                    } else {
                        appTable(Array(apps.prefix(25)))
                    }
                }

                Text(L("数据来自系统自带的 nettop，按进程统计后归到所属应用。速率是两次采样之间的平均值；离开本页就停止采样。",
                       "Data comes from the built-in nettop tool, counted per process and grouped by app. Rates are averaged between samples; sampling stops when you leave this page."))
                    .font(.caption).foregroundStyle(.secondary)
            }
            .padding(24)
        }
        .onAppear { NetSampler.shared.start() }
        .onDisappear { NetSampler.shared.stop() }
    }

    private func rateLine(_ symbol: String, _ title: String, _ v: Double, _ c: Color) -> some View {
        HStack {
            Label(title, systemImage: symbol).foregroundStyle(c)
            Spacer()
            Text(Fmt.rate(v)).font(.system(.title3, design: .rounded).weight(.semibold)).monospacedDigit()
        }
    }

    private func liveChart(_ m: SystemMonitor) -> some View {
        let pts = m.samples.suffix(150)
        return Chart {
            ForEach(pts) { s in
                AreaMark(x: .value(L("时间", "Time"), s.t), y: .value(L("速率", "Rate"), s.netIn), series: .value(L("方向", "Direction"), "in"))
                    .foregroundStyle(Palette.netIn.opacity(0.18))
                LineMark(x: .value(L("时间", "Time"), s.t), y: .value(L("速率", "Rate"), s.netIn), series: .value(L("方向", "Direction"), "in"))
                    .foregroundStyle(Palette.netIn)
                LineMark(x: .value(L("时间", "Time"), s.t), y: .value(L("速率", "Rate"), s.netOut), series: .value(L("方向", "Direction"), "out"))
                    .foregroundStyle(Palette.netOut)
            }
        }
        .chartYAxis { AxisMarks(values: .automatic(desiredCount: 3)) { v in AxisGridLine(); AxisValueLabel { Text(Fmt.rate(v.as(Double.self) ?? 0)) } } }
        .chartXAxis { AxisMarks(values: .automatic(desiredCount: 5)) { v in AxisGridLine(); AxisValueLabel { if let d = v.as(Date.self) { Text(Fmt.clock(d)) } } } }
        .chartLegend(.hidden)
    }

    private func appTable(_ apps: [AppNet]) -> some View {
        let top = max(apps.map { $0.totalIn + $0.totalOut }.max() ?? 1, 1)
        return VStack(spacing: 0) {
            HStack {
                Text(L("应用", "App")).frame(maxWidth: .infinity, alignment: .leading)
                Text(L("↓ 下载", "↓ Down")).frame(width: 100, alignment: .trailing)
                Text(L("↑ 上传", "↑ Up")).frame(width: 100, alignment: .trailing)
                Text(L("合计", "Total")).frame(width: 190, alignment: .trailing)
            }
            .font(.caption.weight(.semibold)).foregroundStyle(.secondary)
            .padding(.bottom, 6)
            ForEach(apps) { a in
                Divider()
                HStack {
                    HStack(spacing: 8) {
                        Image(nsImage: ProcessControl.icon(for: a.group)).resizable().frame(width: 18, height: 18)
                        Text(a.name).lineLimit(1)
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                    Text(a.rateIn > 0 ? Fmt.rate(a.rateIn) : "—").foregroundStyle(a.rateIn > 0 ? Palette.netIn : .secondary).frame(width: 100, alignment: .trailing)
                    Text(a.rateOut > 0 ? Fmt.rate(a.rateOut) : "—").foregroundStyle(a.rateOut > 0 ? Palette.netOut : .secondary).frame(width: 100, alignment: .trailing)
                    HStack(spacing: 8) {
                        StackedBar(segments: [Segment(label: "in", value: a.totalIn, color: Palette.netIn),
                                              Segment(label: "out", value: a.totalOut, color: Palette.netOut)],
                                   total: top, height: 6, legend: false)
                        Text(Fmt.bytes(a.totalIn + a.totalOut)).frame(width: 74, alignment: .trailing)
                    }
                    .frame(width: 190)
                }
                .font(.callout).monospacedDigit()
                .padding(.vertical, 6)
            }
        }
    }
}
