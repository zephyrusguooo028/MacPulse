import SwiftUI
import Charts

struct OverviewView: View {
    @Environment(AppState.self) private var app
    @Local private var historyMetric = "CPU"

    var body: some View {
        let m = app.monitor
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                PageHeader(title: "概览",
                           subtitle: "\(SystemInfo.chip) · \(Fmt.bytes(m.memory.total)) 内存 · \(SystemInfo.osVersion) · 已运行 \(SystemInfo.uptimeText())") {
                    EmptyView()
                }

                HStack(alignment: .top, spacing: 14) {
                    cpuTile(m)
                    memTile(m)
                    diskTile(m)
                    netTile(m)
                }

                HStack(alignment: .top, spacing: 14) {
                    Card("实时走势", symbol: "waveform.path.ecg", trailing: {
                        Text("最近 5 分钟").font(.caption).foregroundStyle(.secondary)
                    }) {
                        liveChart(m).frame(height: 190)
                    }
                    .frame(maxWidth: .infinity)

                    Card("CPU 核心", symbol: "cpu", trailing: {
                        Text(m.coreSummary).font(.caption).foregroundStyle(.secondary)
                    }) {
                        coreBars(m).frame(height: 190)
                    }
                    .frame(width: 330)
                }

                Card("过去 24 小时", symbol: "clock.arrow.circlepath", trailing: {
                    Picker("", selection: $historyMetric) {
                        ForEach(["CPU", "内存", "网络", "温度"], id: \.self) { Text($0) }
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
                    StatLine(label: "用户", value: Fmt.pct(m.cpu.user, digits: 1), color: Palette.cpu)
                    StatLine(label: "系统", value: Fmt.pct(m.cpu.system, digits: 1), color: Palette.warn)
                    StatLine(label: "负载", value: SystemInfo.loadAverage.first.map { String(format: "%.2f", $0) } ?? "—")
                }
            }
        }
    }

    private func memTile(_ m: SystemMonitor) -> some View {
        let color: Color = m.memory.pressureLevel >= 4 ? Palette.danger : (m.memory.pressureLevel >= 2 ? Palette.warn : Palette.mem)
        return tile("内存", "memorychip") {
            HStack(spacing: 14) {
                Ring(value: m.memory.usedFraction, color: color, label: Fmt.pct(m.memory.usedFraction)).frame(width: 72, height: 72)
                VStack(spacing: 6) {
                    StatLine(label: "已用", value: Fmt.bytes(m.memory.used))
                    StatLine(label: "压力", value: m.memory.pressureText, color: color)
                    StatLine(label: "交换", value: Fmt.bytes(m.memory.swapUsed))
                }
            }
        }
    }

    private func diskTile(_ m: SystemMonitor) -> some View {
        tile("磁盘", "internaldrive") {
            HStack(spacing: 14) {
                Ring(value: m.disk.usedFraction, color: m.disk.usedFraction > 0.9 ? Palette.danger : Palette.disk,
                     label: Fmt.pct(m.disk.usedFraction), caption: "已用").frame(width: 72, height: 72)
                VStack(spacing: 6) {
                    StatLine(label: "可用", value: Fmt.bytes(m.disk.available), color: Palette.disk)
                    StatLine(label: "已用", value: Fmt.bytes(m.disk.used))
                    Button("分析磁盘 →") { app.tab = .disk }.buttonStyle(.link).font(.callout)
                        .frame(maxWidth: .infinity, alignment: .trailing)
                }
            }
        }
    }

    private func netTile(_ m: SystemMonitor) -> some View {
        tile("网络", "network") {
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
                AreaMark(x: .value("时间", s.t), y: .value("CPU", s.cpu * 100), series: .value("指标", "CPU"))
                    .foregroundStyle(Palette.cpu.opacity(0.18))
                LineMark(x: .value("时间", s.t), y: .value("CPU", s.cpu * 100), series: .value("指标", "CPU"))
                    .foregroundStyle(Palette.cpu)
                LineMark(x: .value("时间", s.t), y: .value("内存", s.mem * 100), series: .value("指标", "内存"))
                    .foregroundStyle(Palette.mem)
            }
        }
        .chartYScale(domain: 0...100)
        .chartYAxis { AxisMarks(values: [0, 50, 100]) { v in AxisGridLine(); AxisValueLabel { Text("\(v.as(Int.self) ?? 0)%") } } }
        .chartXAxis { AxisMarks(values: .automatic(desiredCount: 5)) { v in AxisGridLine(); AxisValueLabel { if let d = v.as(Date.self) { Text(Fmt.clock(d)) } } } }
        .chartLegend(.hidden)
        .overlay(alignment: .topTrailing) {
            FlowLegend(items: [(Palette.cpu, "CPU", Fmt.pct(m.cpu.total)), (Palette.mem, "内存", Fmt.pct(m.memory.usedFraction))])
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
            EmptyHint(symbol: "clock", title: "正在记录", detail: "Peekaboo 每分钟记一个点，保留 24 小时。运行一会儿就能看到曲线。")
        } else {
            Chart {
                ForEach(pts) { p in
                    switch historyMetric {
                    case "内存":
                        AreaMark(x: .value("时间", p.t), y: .value("内存", p.mem * 100)).foregroundStyle(Palette.mem.opacity(0.25))
                        LineMark(x: .value("时间", p.t), y: .value("内存", p.mem * 100)).foregroundStyle(Palette.mem)
                    case "网络":
                        LineMark(x: .value("时间", p.t), y: .value("速率", p.netIn / 1024), series: .value("方向", "下载")).foregroundStyle(Palette.netIn)
                        LineMark(x: .value("时间", p.t), y: .value("速率", p.netOut / 1024), series: .value("方向", "上传")).foregroundStyle(Palette.netOut)
                    case "温度":
                        if let t = p.temp {
                            LineMark(x: .value("时间", p.t), y: .value("温度", t)).foregroundStyle(Palette.danger)
                        }
                    default:
                        AreaMark(x: .value("时间", p.t), yStart: .value("平均", p.cpu * 100), yEnd: .value("峰值", p.cpuMax * 100))
                            .foregroundStyle(Palette.cpu.opacity(0.18))
                        LineMark(x: .value("时间", p.t), y: .value("CPU", p.cpu * 100)).foregroundStyle(Palette.cpu)
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
                        case "网络": Text(Fmt.rate(d * 1024))
                        case "温度": Text(String(format: "%.0f°", d))
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
        return Card("内存构成", symbol: "square.stack.3d.up") {
            StackedBar(segments: [
                Segment(label: "应用", value: Double(mem.app), color: Palette.mem),
                Segment(label: "联动", value: Double(mem.wired), color: Palette.warn),
                Segment(label: "压缩", value: Double(mem.compressed), color: Palette.danger),
                Segment(label: "缓存文件", value: Double(mem.cached), color: Palette.netIn.opacity(0.6)),
                Segment(label: "空闲", value: Double(free), color: Color.primary.opacity(0.12)),
            ], total: Double(mem.total), height: 16)
            Text("“缓存文件”是系统为加快速度留着的文件缓存，需要时会自动让出，不算真正占用。")
                .font(.caption).foregroundStyle(.secondary)
        }
    }

    private func deviceCard(_ m: SystemMonitor) -> some View {
        Card("设备", symbol: "desktopcomputer") {
            VStack(spacing: 8) {
                StatLine(label: "GPU", value: m.gpu.map { Fmt.pct($0) } ?? "—")
                StatLine(label: "CPU 温度", value: m.temperature.map { String(format: "%.0f °C（估算）", $0) } ?? (app.settings.showTemperature ? "读不到" : "已关闭"))
                if let b = m.battery {
                    StatLine(label: "电池", value: "\(Fmt.pct(b.percent)) · \(b.charging ? "充电中" : (b.onAC ? "已接电源" : (b.minutesLeft.map { "剩约 \($0 / 60) 小时 \($0 % 60) 分" } ?? "使用电池")))")
                } else {
                    StatLine(label: "电源", value: "外接电源")
                }
                StatLine(label: "负载 1/5/15 分", value: SystemInfo.loadAverage.map { String(format: "%.2f", $0) }.joined(separator: " / "))
                StatLine(label: "型号", value: SystemInfo.model)
            }
        }
    }
}
