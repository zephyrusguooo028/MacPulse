import SwiftUI
import Charts

enum PopoverStyle: String, CaseIterable, Identifiable {
    case compact, cards, chart
    var id: String { rawValue }
    var title: String {
        switch self {
        case .compact: return "紧凑列表"
        case .cards: return "大数字卡片"
        case .chart: return "大图表"
        }
    }
}

// MARK: - Shared pieces

struct PopoverHeader: View {
    @Environment(AppState.self) private var app
    var trailing: String? = nil

    var body: some View {
        let icon = app.settings.menuIcon == .none ? MenuIcon.ghost : app.settings.menuIcon
        HStack(spacing: 10) {
            AnimatedIconView(icon: icon, height: 22)
                .frame(width: 36, height: 36)
                .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(Color.accentColor.opacity(0.12)))
            VStack(alignment: .leading, spacing: 1) {
                Text("MacPulse").font(.system(size: 13, weight: .semibold, design: .rounded))
                Text(statusSentence(app.monitor, app.settings)).font(.system(size: 11)).foregroundStyle(.secondary).lineLimit(1)
            }
            Spacer()
            if let trailing {
                Text(trailing).font(.system(size: 11)).foregroundStyle(.tertiary).monospacedDigit()
            }
        }
    }
}

struct PopoverFooter: View {
    var body: some View {
        HStack(spacing: 6) {
            Button { WindowManager.shared.show(.overview) } label: {
                Text("打开主窗口").frame(maxWidth: .infinity)
            }
            .buttonStyle(.borderedProminent)
            Button { WindowManager.shared.show(.cleanup) } label: {
                Label("清理", systemImage: "sparkles").frame(maxWidth: .infinity)
            }
            .buttonStyle(.bordered)
            Button { WindowManager.shared.show(.settings) } label: { Image(systemName: "gearshape") }
                .buttonStyle(.borderless).help("设置").padding(.leading, 4)
            Button { NSApp.terminate(nil) } label: { Image(systemName: "power") }
                .buttonStyle(.borderless).help("退出 MacPulse")
        }
        .controlSize(.regular)
    }
}

struct TopApps: View {
    @Environment(AppState.self) private var app
    var count = 3

    var body: some View {
        VStack(spacing: 6) {
            if app.monitor.groups.isEmpty {
                Text("读取中…").font(.system(size: 12)).foregroundStyle(.tertiary).frame(maxWidth: .infinity, alignment: .leading)
            }
            ForEach(app.monitor.topByCPU.prefix(count)) { g in
                HStack(spacing: 8) {
                    Image(nsImage: ProcessControl.icon(for: g)).resizable().frame(width: 16, height: 16)
                    Text(g.name).lineLimit(1)
                    Spacer()
                    Text(Fmt.bytes(g.memory)).foregroundStyle(.secondary)
                    Text(String(format: "%.0f%%", g.cpu * 100))
                        .foregroundStyle(g.cpu > 0.8 ? Palette.danger : .primary)
                        .frame(width: 40, alignment: .trailing)
                }
                .font(.system(size: 12)).monospacedDigit()
            }
        }
    }
}

private func sectionTitle(_ t: String) -> some View {
    Text(t).font(.system(size: 11, weight: .medium)).foregroundStyle(.secondary)
        .frame(maxWidth: .infinity, alignment: .leading)
}

// MARK: - 1. Compact list

struct CompactPopover: View {
    @Environment(AppState.self) private var app

    var body: some View {
        let m = app.monitor
        VStack(spacing: 12) {
            PopoverHeader(trailing: "已运行 \(SystemInfo.uptimeText())")
            VStack(spacing: 10) {
                row("cpu", "CPU", m.cpu.total, Fmt.pct(m.cpu.total), Palette.load(m.cpu.total))
                row("memorychip", "内存", m.memory.usedFraction, Fmt.pct(m.memory.usedFraction),
                    m.memory.pressureLevel >= 4 ? Palette.danger : Palette.mem, note: "压力\(m.memory.pressureText)")
                row("internaldrive", "磁盘", m.disk.usedFraction, "剩 \(MenuBarRenderer.diskText(m.disk.available))",
                    m.disk.usedFraction > 0.9 ? Palette.danger : Palette.disk)
                HStack(spacing: 10) {
                    icon("network")
                    Text("网络").frame(width: 34, alignment: .leading)
                    Label(Fmt.rate(m.netIn), systemImage: "arrow.down").foregroundStyle(Palette.netIn)
                    Spacer()
                    Label(Fmt.rate(m.netOut), systemImage: "arrow.up").foregroundStyle(Palette.netOut)
                }
                .font(.system(size: 12)).monospacedDigit()
                HStack(spacing: 10) {
                    icon("thermometer.medium")
                    Text("其他").frame(width: 34, alignment: .leading)
                    Text(m.temperature.map { String(format: "%.0f°C", $0) } ?? "—")
                    Spacer()
                    Text("GPU \(m.gpu.map { Fmt.pct($0) } ?? "—")")
                    if let b = m.battery { Text("电池 \(Fmt.pct(b.percent))") }
                }
                .font(.system(size: 12)).monospacedDigit()
            }
            .padding(12)
            .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(Color.primary.opacity(0.045)))

            VStack(spacing: 8) {
                sectionTitle("占用最高")
                TopApps(count: 3)
            }
            .padding(12)
            .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(Color.primary.opacity(0.045)))

            PopoverFooter()
        }
        .padding(14)
        .frame(width: 300)
    }

    private func icon(_ s: String) -> some View {
        Image(systemName: s).font(.system(size: 12)).foregroundStyle(.secondary).frame(width: 16)
    }

    private func row(_ symbol: String, _ title: String, _ v: Double, _ value: String, _ color: Color, note: String? = nil) -> some View {
        HStack(spacing: 10) {
            icon(symbol)
            Text(title).frame(width: 34, alignment: .leading)
            GeometryReader { g in
                ZStack(alignment: .leading) {
                    Capsule().fill(Color.primary.opacity(0.08))
                    Capsule().fill(color).frame(width: max(4, g.size.width * CGFloat(min(v, 1))))
                        .animation(.easeOut(duration: 0.4), value: v)
                }
            }
            .frame(height: 6)
            Text(value).frame(width: 64, alignment: .trailing).fontWeight(.medium)
        }
        .font(.system(size: 12)).monospacedDigit()
        .help(note ?? "")
    }
}

// MARK: - 2. Big-number cards

struct CardsPopover: View {
    @Environment(AppState.self) private var app

    var body: some View {
        let m = app.monitor
        VStack(spacing: 12) {
            PopoverHeader(trailing: Fmt.time(Date()))
            LazyVGrid(columns: [GridItem(.flexible(), spacing: 10), GridItem(.flexible())], spacing: 10) {
                card("CPU", "cpu", Fmt.pct(m.cpu.total), sub: "\(m.coreLetters.count) 核", color: Palette.cpu,
                     spark: m.samples.suffix(60).map(\.cpu), maxV: 1)
                card("内存", "memorychip", Fmt.pct(m.memory.usedFraction), sub: "压力\(m.memory.pressureText)",
                     color: m.memory.pressureLevel >= 4 ? Palette.danger : Palette.mem, spark: m.samples.suffix(60).map(\.mem), maxV: 1)
                card("磁盘", "internaldrive", MenuBarRenderer.diskText(m.disk.available), sub: "可用 · 共 \(Fmt.bytes(m.disk.total))",
                     color: Palette.disk, ring: m.disk.usedFraction)
                card("网络", "network", MenuMetric.short(m.netIn) + "/s", sub: "下载 · 上传 \(Fmt.rate(m.netOut))", color: Palette.netIn,
                     spark: m.samples.suffix(60).map(\.netIn), maxV: nil)
            }
            HStack(spacing: 8) {
                pill("thermometer.medium", m.temperature.map { String(format: "%.0f°C", $0) } ?? "—")
                pill("cube", "GPU \(m.gpu.map { Fmt.pct($0) } ?? "—")")
                if let b = m.battery { pill(b.charging ? "bolt.fill" : "battery.75", Fmt.pct(b.percent)) }
                pill("clock", SystemInfo.uptimeText())
            }
            VStack(spacing: 8) {
                sectionTitle("占用最高")
                TopApps(count: 3)
            }
            PopoverFooter()
        }
        .padding(14)
        .frame(width: 320)
    }

    private func card(_ title: String, _ symbol: String, _ big: String, sub: String, color: Color,
                      spark: [Double]? = nil, maxV: Double? = 1, ring: Double? = nil) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack(spacing: 4) {
                Image(systemName: symbol).font(.system(size: 10, weight: .semibold))
                Text(title).font(.system(size: 11, weight: .semibold))
                Spacer()
                if let ring {
                    Circle().trim(from: 0, to: ring).stroke(color, style: StrokeStyle(lineWidth: 3, lineCap: .round))
                        .rotationEffect(.degrees(-90))
                        .background(Circle().stroke(color.opacity(0.2), lineWidth: 3))
                        .frame(width: 16, height: 16)
                }
            }
            .foregroundStyle(color)
            Text(big).font(.system(size: 26, weight: .bold, design: .rounded)).monospacedDigit()
                .lineLimit(1).minimumScaleFactor(0.6)
                .contentTransition(.numericText()).animation(.easeOut(duration: 0.3), value: big)
            Text(sub).font(.system(size: 10)).foregroundStyle(.secondary).lineLimit(1)
            if let spark {
                Sparkline(values: spark, color: color, maxValue: maxV).frame(height: 22)
            } else {
                Spacer().frame(height: 22)
            }
        }
        .padding(10)
        .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(color.opacity(0.10)))
    }

    private func pill(_ symbol: String, _ text: String) -> some View {
        HStack(spacing: 3) {
            Image(systemName: symbol)
            Text(text).lineLimit(1)
        }
        .font(.system(size: 11)).monospacedDigit()
        .padding(.horizontal, 8).padding(.vertical, 4)
        .background(Capsule().fill(Color.primary.opacity(0.06)))
    }
}

// MARK: - 3. One big chart

struct ChartPopover: View {
    @Environment(AppState.self) private var app

    var body: some View {
        let m = app.monitor
        VStack(spacing: 12) {
            PopoverHeader()
            VStack(alignment: .leading, spacing: 6) {
                HStack(alignment: .firstTextBaseline, spacing: 12) {
                    big("CPU", Fmt.pct(m.cpu.total), Palette.cpu)
                    big("内存", Fmt.pct(m.memory.usedFraction), Palette.mem)
                    Spacer()
                    Text("最近 5 分钟").font(.system(size: 10)).foregroundStyle(.tertiary)
                }
                Chart {
                    ForEach(m.samples) { s in
                        AreaMark(x: .value("t", s.t), y: .value("CPU", s.cpu * 100), series: .value("k", "CPU"))
                            .foregroundStyle(LinearGradient(colors: [Palette.cpu.opacity(0.35), Palette.cpu.opacity(0.02)], startPoint: .top, endPoint: .bottom))
                            .interpolationMethod(.catmullRom)
                        LineMark(x: .value("t", s.t), y: .value("CPU", s.cpu * 100), series: .value("k", "CPU"))
                            .foregroundStyle(Palette.cpu).interpolationMethod(.catmullRom)
                        LineMark(x: .value("t", s.t), y: .value("内存", s.mem * 100), series: .value("k", "内存"))
                            .foregroundStyle(Palette.mem).lineStyle(StrokeStyle(lineWidth: 1.5, dash: [3, 2]))
                    }
                }
                .chartYScale(domain: 0...100)
                .chartXAxis(.hidden)
                .chartYAxis { AxisMarks(position: .trailing, values: [0, 50, 100]) { _ in AxisGridLine() } }
                .frame(height: 120)
            }
            .padding(12)
            .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(Color.primary.opacity(0.045)))

            HStack(spacing: 0) {
                stat("磁盘剩余", MenuBarRenderer.diskText(m.disk.available))
                Divider().frame(height: 26)
                stat("下载", MenuMetric.short(m.netIn) + "/s")
                Divider().frame(height: 26)
                stat("上传", MenuMetric.short(m.netOut) + "/s")
                Divider().frame(height: 26)
                stat("温度", m.temperature.map { String(format: "%.0f°", $0) } ?? "—")
            }

            VStack(spacing: 8) {
                sectionTitle("占用最高")
                TopApps(count: 2)
            }
            .padding(12)
            .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(Color.primary.opacity(0.045)))

            PopoverFooter()
        }
        .padding(14)
        .frame(width: 320)
    }

    private func big(_ label: String, _ v: String, _ c: Color) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 4) {
            Circle().fill(c).frame(width: 7, height: 7)
            Text(label).font(.system(size: 11)).foregroundStyle(.secondary)
            Text(v).font(.system(size: 22, weight: .bold, design: .rounded)).monospacedDigit()
                .lineLimit(1).fixedSize()
        }
    }

    private func stat(_ label: String, _ v: String) -> some View {
        VStack(spacing: 2) {
            Text(v).font(.system(size: 14, weight: .semibold, design: .rounded)).monospacedDigit()
            Text(label).font(.system(size: 10)).foregroundStyle(.secondary)
        }
        .frame(maxWidth: .infinity)
    }
}
