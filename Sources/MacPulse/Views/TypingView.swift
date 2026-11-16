import SwiftUI
import Charts

struct TypingView: View {
    @Environment(AppState.self) private var app
    @Local private var range = 7

    var body: some View {
        let t = TypingMonitor.shared
        let s = app.settings
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                PageHeader(title: "打字", subtitle: "只统计按了多少次、按的哪个键，从不记录你打了什么内容") {
                    Toggle("开启打字统计", isOn: Binding(get: { s.typingEnabled }, set: { s.typingEnabled = $0; t.apply() }))
                        .toggleStyle(.switch)
                }

                if s.typingEnabled && !t.permissionGranted {
                    Card("需要「输入监控」权限", symbol: "lock.shield") {
                        Text("macOS 要求统计按键的应用获得「输入监控」权限。MacPulse 只数按键次数，不记录内容，也不联网。授权后回到这里关掉再打开一次开关。")
                            .foregroundStyle(.secondary)
                        HStack {
                            Button("去授权") { t.requestPermission() }.buttonStyle(.borderedProminent)
                            Button("我已授权，重新检查") { t.apply() }
                        }
                    }
                } else if !s.typingEnabled && t.days.isEmpty {
                    Card("打字统计", symbol: "keyboard") {
                        EmptyHint(symbol: "keyboard", title: "还没开启",
                                  detail: "打开右上角的开关，就能看到每天敲了多少键、哪个时段最勤快、最常按的键和在哪些应用里打字最多。\n只计数，不记录内容。")
                    }
                }

                if !t.days.isEmpty || t.running {
                    summary(t, goal: s.typingGoal)
                    HStack(alignment: .top, spacing: 14) {
                        hourly(t).frame(maxWidth: .infinity)
                        daily(t).frame(maxWidth: .infinity)
                    }
                    Card("键盘热力图（今天）", symbol: "keyboard") { heatmap(t.today) }
                    HStack(alignment: .top, spacing: 14) {
                        topKeys(t.today).frame(maxWidth: .infinity)
                        topApps(t.today).frame(maxWidth: .infinity)
                    }
                    HStack {
                        Stepper("每日目标 \(s.typingGoal) 次", value: Binding(get: { s.typingGoal }, set: { s.typingGoal = $0 }), in: 1000...100_000, step: 1000)
                        Spacer()
                        Button("清空全部打字数据…", role: .destructive) {
                            if FileActions.confirm("清空打字数据？", "会删除所有已记录的打字统计，不能恢复。", ok: "清空") { t.clearAll() }
                        }
                    }
                    .font(.callout)
                }
            }
            .padding(24)
        }
    }

    private func summary(_ t: TypingMonitor, goal: Int) -> some View {
        let d = t.today
        let y = t.day(offset: 1)
        let progress = Double(d.total) / Double(max(goal, 1))
        let kpm = d.activeSeconds > 30 ? Double(d.total) / (d.activeSeconds / 60) : 0
        return HStack(spacing: 14) {
            Card("今天", symbol: "flame") {
                HStack(spacing: 16) {
                    Ring(value: progress, color: Palette.theme, lineWidth: 9, label: Fmt.pct(min(progress, 9.99)), caption: "目标")
                        .frame(width: 84, height: 84)
                    VStack(alignment: .leading, spacing: 4) {
                        Text("\(d.total)").font(.system(size: 34, weight: .bold, design: .rounded)).monospacedDigit()
                            .contentTransition(.numericText()).animation(.easeOut, value: d.total)
                        Text("次按键 · 目标 \(goal)").font(.caption).foregroundStyle(.secondary)
                        Text(y.total > 0 ? "昨天 \(y.total)（\(d.total >= y.total ? "已超过" : "还差 \(y.total - d.total)")）" : "昨天没有记录")
                            .font(.caption).foregroundStyle(.secondary)
                    }
                }
            }
            .frame(maxWidth: .infinity)
            Card("节奏", symbol: "metronome") {
                VStack(spacing: 8) {
                    StatLine(label: "打字时长", value: minutes(d.activeSeconds))
                    StatLine(label: "最长连续", value: minutes(d.longestStreak))
                    StatLine(label: "平均速度", value: kpm > 0 ? String(format: "%.0f 键/分钟", kpm) : "—")
                    StatLine(label: "状态", value: t.running ? "统计中" : "已暂停", color: t.running ? Palette.disk : .gray)
                }
            }
            .frame(width: 300)
        }
    }

    private func minutes(_ s: Double) -> String {
        let m = Int(s / 60)
        return m >= 60 ? "\(m / 60) 小时 \(m % 60) 分" : "\(m) 分钟"
    }

    private func hourly(_ t: TypingMonitor) -> some View {
        let d = t.today
        return Card("今天每小时", symbol: "clock") {
            Chart {
                ForEach(0..<24, id: \.self) { h in
                    BarMark(x: .value("小时", h), y: .value("按键", d.hours[h]))
                        .foregroundStyle(Palette.theme.gradient)
                        .cornerRadius(3)
                }
            }
            .chartXScale(domain: -0.5...23.5)
            .chartXAxis { AxisMarks(values: [0, 6, 12, 18, 23]) { v in AxisValueLabel { Text("\(v.as(Int.self) ?? 0)点") } } }
            .frame(height: 160)
        }
    }

    private func daily(_ t: TypingMonitor) -> some View {
        let pts: [(Date, Int)] = (0..<range).reversed().map { off in
            (Calendar.current.date(byAdding: .day, value: -off, to: Date())!, t.day(offset: off).total)
        }
        return Card("每天", symbol: "calendar", trailing: {
            Picker("", selection: $range) { Text("7 天").tag(7); Text("30 天").tag(30) }
                .pickerStyle(.segmented).labelsHidden().frame(width: 120)
        }) {
            Chart {
                ForEach(pts, id: \.0) { p in
                    BarMark(x: .value("日期", p.0, unit: .day), y: .value("按键", p.1))
                        .foregroundStyle(Palette.mem.gradient).cornerRadius(3)
                }
                RuleMark(y: .value("目标", app.settings.typingGoal))
                    .foregroundStyle(.secondary).lineStyle(StrokeStyle(lineWidth: 1, dash: [4, 3]))
            }
            .chartXAxis { AxisMarks(values: .stride(by: .day, count: range == 7 ? 1 : 5)) { _ in AxisValueLabel(format: .dateTime.month(.defaultDigits).day()) } }
            .frame(height: 160)
        }
    }

    private func heatmap(_ d: TypingDay) -> some View {
        let maxCount = max(d.keys.values.max() ?? 1, 1)
        return GeometryReader { g in
            let unit = (g.size.width - 14 * 4) / 15
            VStack(alignment: .leading, spacing: 4) {
                ForEach(Array(KeyboardLayout.rows.enumerated()), id: \.offset) { _, row in
                    HStack(spacing: 4) {
                        ForEach(Array(row.enumerated()), id: \.offset) { _, key in
                            let n = d.keys[String(key.code)] ?? 0
                            let heat = n == 0 ? 0 : 0.15 + 0.85 * sqrt(Double(n) / Double(maxCount))
                            VStack(spacing: 1) {
                                Text(key.label).font(.system(size: 12, weight: .semibold))
                                if n > 0 { Text("\(n)").font(.system(size: 9)).monospacedDigit().opacity(0.8) }
                            }
                            .foregroundStyle(heat > 0.55 ? Color.white : Color.primary)
                            .frame(width: unit * key.width + 4 * (key.width - 1), height: unit * 0.82)
                            .background(RoundedRectangle(cornerRadius: 6, style: .continuous)
                                .fill(heat > 0 ? Palette.theme.opacity(heat) : Color.primary.opacity(0.05)))
                            .help("\(key.label)：\(n) 次")
                        }
                    }
                }
            }
        }
        .frame(height: 5 * 52)
    }

    private func topKeys(_ d: TypingDay) -> some View {
        let top = d.keys.sorted { $0.value > $1.value }.prefix(8)
        let maxV = Double(top.first?.value ?? 1)
        return Card("最常按的键", symbol: "star") {
            if top.isEmpty { Text("今天还没有数据").foregroundStyle(.secondary) }
            ForEach(Array(top), id: \.key) { k, v in
                HStack {
                    Text(KeyboardLayout.labels[Int(k) ?? -1] ?? "键 \(k)").frame(width: 50, alignment: .leading)
                    SizeBar(fraction: Double(v) / maxV, color: Palette.theme)
                    Text("\(v)").monospacedDigit().frame(width: 60, alignment: .trailing)
                }
                .font(.callout)
            }
        }
    }

    private func topApps(_ d: TypingDay) -> some View {
        let top = d.apps.sorted { $0.value > $1.value }.prefix(8)
        let total = Double(max(d.total, 1))
        return Card("在哪里打字最多", symbol: "app.badge") {
            if top.isEmpty { Text("今天还没有数据").foregroundStyle(.secondary) }
            ForEach(Array(top), id: \.key) { name, v in
                HStack {
                    Text(name).lineLimit(1).frame(width: 120, alignment: .leading)
                    SizeBar(fraction: Double(v) / total, color: Palette.mem)
                    Text(Fmt.pct(Double(v) / total)).monospacedDigit().frame(width: 50, alignment: .trailing)
                }
                .font(.callout)
            }
        }
    }
}
