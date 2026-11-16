import SwiftUI
import Charts

struct OverviewView: View {
    @Environment(AppState.self) private var app
    @Local private var historyMetric = "cpu"

    private static func metricName(_ key: String) -> String {
        switch key {
        case "mem": return L("内存", "Memory")
        case "net": return L("网络", "Network")
        case "temp": return L("温度", "Temp")
        default: return "CPU"
        }
    }

    var body: some View {
        let m = app.monitor
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                PageHeader(title: L("概览", "Overview"),
                           subtitle: L("\(SystemInfo.chip) · \(Fmt.bytes(m.memory.total)) 内存 · \(SystemInfo.osVersion) · 已运行 \(SystemInfo.uptimeText())", "\(SystemInfo.chip) · \(Fmt.bytes(m.memory.total)) memory · \(SystemInfo.osVersion) · up \(SystemInfo.uptimeText())")) {
                    EmptyView()
                }

                HStack(alignment: .top, spacing: 14) {
                    cpuTile(m)
                    memTile(m)
                    diskTile(m)
                    netTile(m)
                }

                HStack(alignment: .top, spacing: 14) {
                    Card(L("实时走势", "Live activity"), symbol: "waveform.path.ecg", trailing: {
                        Text(L("最近 5 分钟", "Last 5 minutes")).font(.caption).foregroundStyle(.secondary)
                    }) {
                        liveChart(m).frame(height: 190)
                    }
                    .frame(maxWidth: .infinity)

                    Card(L("CPU 核心", "CPU cores"), symbol: "cpu", trailing: {
                        Text(m.coreSummary).font(.caption).foregroundStyle(.secondary)
                    }) {
                        coreBars(m).frame(height: 190)
                    }
                    .frame(width: 330)
                }

                Card(L("过去 24 小时", "Past 24 hours"), symbol: "clock.arrow.circlepath", trailing: {
                    Picker("", selection: $historyMetric) {
                        ForEach(["cpu", "mem", "net", "temp"], id: \.self) { Text(Self.metricName($0)).tag($0) }
                    }
                    .pickerStyle(.segmented).labelsHidden().frame(width: 260)
                }) {
                    historyChart(m).frame(height: 200)
                }

                HStack(alignment: .top, spacing: 14) {
                    memoryBreakdown(m).frame(maxWidth: .infinity)
                    deviceCard(m).frame(width: 330)
                }
            }
            .padding(24)
        }
    }

    // MARK: Tiles

    private func tile<C: View>(_ title: String, _ symbol: String, @ViewBuilder _ c: @escaping () -> C) -> some View {
        Card(title, symbol: symbol) { c() }.frame(maxWidth: .infinity)
    }

    private func cpuTile(_ m: SystemMonitor) -> some View {
        tile("CPU", "cpu") {
            HStack(spacing: 14) {
                Ring(value: m.cpu.total, color: Palette.load(m.cpu.total), label: Fmt.pct(m.cpu.total)).frame(width: 72, height: 72)
                VStack(spacing: 6) {
                    StatLine(label: L("用户", "User"), value: Fmt.pct(m.cpu.user, digits: 1), color: Palette.cpu)
                    StatLine(label: L("系统", "System"), value: Fmt.pct(m.cpu.system, digits: 1), color: Palette.warn)
                    StatLine(label: L("负载", "Load"), value: SystemInfo.loadAverage.first.map { String(format: "%.2f", $0) } ?? "—")
                }
            }
        }
    }

    private func memTile(_ m: SystemMonitor) -> some View {
        let color: Color = m.memory.pressureLevel >= 4 ? Palette.danger : (m.memory.pressureLevel >= 2 ? Palette.warn : Palette.mem)
        return tile(L("内存", "Memory"), "memorychip") {
            HStack(spacing: 14) {
                Ring(value: m.memory.usedFraction, color: color, label: Fmt.pct(m.memory.usedFraction)).frame(width: 72, height: 72)
                VStack(spacing: 6) {
                    StatLine(label: L("已用", "Used"), value: Fmt.bytes(m.memory.used))
                    StatLine(label: L("压力", "Pressure"), value: m.memory.pressureText, color: color)
                    StatLine(label: L("交换", "Swap"), value: Fmt.bytes(m.memory.swapUsed))
                }
            }
        }
    }

    private func diskTile(_ m: SystemMonitor) -> some View {
        tile(L("磁盘", "Disk"), "internaldrive") {
            HStack(spacing: 14) {
                Ring(value: m.disk.usedFraction, color: m.disk.usedFraction > 0.9 ? Palette.danger : Palette.disk,
                     label: Fmt.pct(m.disk.usedFraction), caption: L("已用", "used")).frame(width: 72, height: 72)
                VStack(spacing: 6) {
                    StatLine(label: L("可用", "Free"), value: Fmt.bytes(m.disk.available), color: Palette.disk)
                    StatLine(label: L("已用", "Used"), value: Fmt.bytes(m.disk.used))
                    Button(L("分析磁盘 →", "Analyze disk →")) { app.tab = .disk }.buttonStyle(.link).font(.callout)
                        .frame(maxWidth: .infinity, alignment: .trailing)
                }
            }
        }
    }

    private func netTile(_ m: SystemMonitor) -> some View {
        tile(L("网络", "Network"), "network") {
            VStack(alignment: .leading, spacing: 6) {
                HStack {
                    Label(Fmt.rate(m.netIn), systemImage: "arrow.down").foregroundStyle(Palette.netIn)
                    Spacer()
                    Label(Fmt.rate(m.netOut), systemImage: "arrow.up").foregroundStyle(Palette.netOut)
                }
                .font(.callout.weight(.medium)).monospacedDigit()
                ZStack {
                    Sparkline(values: m.samples.suffix(60).map(\.netIn), color: Palette.netIn, maxValue: nil)
                    Sparkline(values: m.samples.suffix(60).map(\.netOut), color: Palette.netOut,
                              maxValue: max(m.samples.suffix(60).map(\.netIn).max() ?? 1, m.samples.suffix(60).map(\.netOut).max() ?? 1, 1))
                }
                .frame(height: 44)
            }
        }
    }

    // MARK: Charts

    private func liveChart(_ m: SystemMonitor) -> some View {
        Chart {
            ForEach(m.samples) { s in
                AreaMark(x: .value(L("时间", "Time"), s.t), y: .value("CPU", s.cpu * 100), series: .value(L("指标", "Metric"), "CPU"))
                    .foregroundStyle(Palette.cpu.opacity(0.18))
                LineMark(x: .value(L("时间", "Time"), s.t), y: .value("CPU", s.cpu * 100), series: .value(L("指标", "Metric"), "CPU"))
                    .foregroundStyle(Palette.cpu)
                LineMark(x: .value(L("时间", "Time"), s.t), y: .value(L("内存", "Memory"), s.mem * 100), series: .value(L("指标", "Metric"), L("内存", "Memory")))
                    .foregroundStyle(Palette.mem)
            }
        }
        .chartYScale(domain: 0...100)
        .chartYAxis { AxisMarks(values: [0, 50, 100]) { v in AxisGridLine(); AxisValueLabel { Text("\(v.as(Int.self) ?? 0)%") } } }
        .chartXAxis { AxisMarks(values: .automatic(desiredCount: 5)) { v in AxisGridLine(); AxisValueLabel { if let d = v.as(Date.self) { Text(Fmt.clock(d)) } } } }
        .chartLegend(.hidden)
        .overlay(alignment: .topTrailing) {
            FlowLegend(items: [(Palette.cpu, "CPU", Fmt.pct(m.cpu.total)), (Palette.mem, L("内存", "Memory"), Fmt.pct(m.memory.usedFraction))])
                .padding(6).background(.regularMaterial, in: RoundedRectangle(cornerRadius: 6))
        }
    }

    private func coreBars(_ m: SystemMonitor) -> some View {
        let cores = m.cpu.perCore
        let letters = m.coreLetters
        func color(_ i: Int) -> Color {
            switch i < letters.count ? letters[i] : "P" {
            case "E": return Palette.disk
            case "S": return Palette.mem
            default: return Palette.cpu
            }
        }
        return GeometryReader { g in
            HStack(alignment: .bottom, spacing: 4) {
                ForEach(Array(cores.enumerated()), id: \.offset) { i, v in
                    VStack(spacing: 4) {
                        ZStack(alignment: .bottom) {
                            RoundedRectangle(cornerRadius: 3).fill(Color.primary.opacity(0.06))
                            RoundedRectangle(cornerRadius: 3)
                                .fill(color(i))
                                .frame(height: max(2, (g.size.height - 20) * CGFloat(v)))
                                .animation(.easeOut(duration: 0.3), value: v)
                        }
                        Text(i < letters.count ? letters[i] : "").font(.system(size: 9, weight: .semibold)).foregroundStyle(.secondary)
                    }
                }
            }
        }
    }

    @ViewBuilder
    private func historyChart(_ m: SystemMonitor) -> some View {
        let pts = m.history
        if pts.count < 2 {
            EmptyHint(symbol: "clock", title: L("正在记录", "Recording"), detail: L("Peekaboo 每分钟记一个点，保留 24 小时。运行一会儿就能看到曲线。", "Peekaboo records one point per minute and keeps 24 hours. Leave it running for a while to see the chart."))
        } else {
            Chart {
                ForEach(pts) { p in
                    switch historyMetric {
                    case "mem":
                        AreaMark(x: .value(L("时间", "Time"), p.t), y: .value(L("内存", "Memory"), p.mem * 100)).foregroundStyle(Palette.mem.opacity(0.25))
                        LineMark(x: .value(L("时间", "Time"), p.t), y: .value(L("内存", "Memory"), p.mem * 100)).foregroundStyle(Palette.mem)
                    case "net":
                        LineMark(x: .value(L("时间", "Time"), p.t), y: .value(L("速率", "Rate"), p.netIn / 1024), series: .value(L("方向", "Direction"), L("下载", "Download"))).foregroundStyle(Palette.netIn)
                        LineMark(x: .value(L("时间", "Time"), p.t), y: .value(L("速率", "Rate"), p.netOut / 1024), series: .value(L("方向", "Direction"), L("上传", "Upload"))).foregroundStyle(Palette.netOut)
                    case "temp":
                        if let t = p.temp {
                            LineMark(x: .value(L("时间", "Time"), p.t), y: .value(L("温度", "Temperature"), t)).foregroundStyle(Palette.danger)
                        }
                    default:
                        AreaMark(x: .value(L("时间", "Time"), p.t), yStart: .value(L("平均", "Average"), p.cpu * 100), yEnd: .value(L("峰值", "Peak"), p.cpuMax * 100))
                            .foregroundStyle(Palette.cpu.opacity(0.18))
                        LineMark(x: .value(L("时间", "Time"), p.t), y: .value("CPU", p.cpu * 100)).foregroundStyle(Palette.cpu)
                    }
                }
            }
            .chartXAxis { AxisMarks(values: .automatic(desiredCount: 8)) { v in AxisGridLine(); AxisValueLabel { if let d = v.as(Date.self) { Text(Fmt.time(d)) } } } }
            .chartYAxis {
                AxisMarks { v in
                    AxisGridLine()
                    AxisValueLabel {
                        let d = v.as(Double.self) ?? 0
                        switch historyMetric {
                        case "net": Text(Fmt.rate(d * 1024))
                        case "temp": Text(String(format: "%.0f°", d))
                        default: Text(String(format: "%.0f%%", d))
                        }
                    }
                }
            }
            .chartLegend(.hidden)
        }
    }

    // MARK: Lower cards

    private func memoryBreakdown(_ m: SystemMonitor) -> some View {
        let mem = m.memory
        let free = mem.total > mem.used + mem.cached ? mem.total - mem.used - mem.cached : 0
        return Card(L("内存构成", "Memory breakdown"), symbol: "square.stack.3d.up") {
            StackedBar(segments: [
                Segment(label: L("应用", "App"), value: Double(mem.app), color: Palette.mem),
                Segment(label: L("联动", "Wired"), value: Double(mem.wired), color: Palette.warn),
                Segment(label: L("压缩", "Compressed"), value: Double(mem.compressed), color: Palette.danger),
                Segment(label: L("缓存文件", "Cached files"), value: Double(mem.cached), color: Palette.netIn.opacity(0.6)),
                Segment(label: L("空闲", "Free"), value: Double(free), color: Color.primary.opacity(0.12)),
            ], total: Double(mem.total), height: 16)
            Text(L("“缓存文件”是系统为加快速度留着的文件缓存，需要时会自动让出，不算真正占用。", "“Cached files” is file cache the system keeps to speed things up. It's released automatically when needed and isn't really in use."))
                .font(.caption).foregroundStyle(.secondary)
        }
    }

    private func deviceCard(_ m: SystemMonitor) -> some View {
        Card(L("设备", "Device"), symbol: "desktopcomputer") {
            VStack(spacing: 8) {
                StatLine(label: "GPU", value: m.gpu.map { Fmt.pct($0) } ?? "—")
                StatLine(label: L("CPU 温度", "CPU temperature"), value: m.temperature.map { String(format: L("%.0f °C（估算）", "%.0f °C (estimated)"), $0) } ?? (app.settings.showTemperature ? L("读不到", "Unavailable") : L("已关闭", "Off")))
                if let b = m.battery {
                    StatLine(label: L("电池", "Battery"), value: "\(Fmt.pct(b.percent)) · \(b.charging ? L("充电中", "Charging") : (b.onAC ? L("已接电源", "On power adapter") : (b.minutesLeft.map { L("剩约 \($0 / 60) 小时 \($0 % 60) 分", "About \($0 / 60)h \($0 % 60)m left") } ?? L("使用电池", "On battery"))))")
                } else {
                    StatLine(label: L("电源", "Power"), value: L("外接电源", "Power adapter"))
                }
                StatLine(label: L("负载 1/5/15 分", "Load 1/5/15 min"), value: SystemInfo.loadAverage.map { String(format: "%.2f", $0) }.joined(separator: " / "))
                StatLine(label: L("型号", "Model"), value: SystemInfo.model)
            }
        }
    }
}
