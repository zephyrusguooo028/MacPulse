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
                PageHeader(title: L("打字", "Typing"), subtitle: L("只统计按了多少次、按的哪个键，从不记录你打了什么内容", "Counts only how many keys you press and which ones, never what you type")) {
                    Toggle(L("开启打字统计", "Enable typing stats"), isOn: Binding(get: { s.typingEnabled }, set: {
                        s.typingEnabled = $0
                        // Turning it on also shows today's count in the menu bar (can be unticked in Settings).
                        if $0 && !s.menuMetrics.contains(.typing) { s.menuMetrics.append(.typing) }
                        t.apply()
                    }))
                        .toggleStyle(.switch)
                }

                if s.typingEnabled && !t.permissionGranted {
                    Card(L("需要「输入监控」权限", "Input Monitoring permission required"), symbol: "lock.shield") {
                        VStack(alignment: .leading, spacing: 8) {
                            Text(L("macOS 要求统计按键的应用获得「输入监控」权限。Peekaboo 只数按键次数，不记录内容，也不联网。", "macOS requires Input Monitoring permission for apps that count keystrokes. Peekaboo only counts key presses, never records content, and never goes online."))
                                .foregroundStyle(.secondary)
                            Text(L("1. 点「去授权」，在 系统设置 › 隐私与安全性 › 输入监控 里打开 Peekaboo 的开关。\n2. 如果列表里已经有 Peekaboo 但开关打开了还是没反应，先选中它点「−」删掉，再回来点一次「去授权」。\n3. 授权后这里会自动检测；如果一直没变化，点「重启 Peekaboo」。", "1. Click “Grant access” and turn on Peekaboo in System Settings › Privacy & Security › Input Monitoring.\n2. If Peekaboo is already listed and enabled but nothing happens, select it, click “−” to remove it, then click “Grant access” again.\n3. This page detects access automatically. If nothing changes, click “Restart Peekaboo”."))
                                .font(.callout).foregroundStyle(.secondary)
                            HStack {
                                Button(L("去授权", "Grant access")) { t.requestPermission() }.buttonStyle(.borderedProminent)
                                Button(L("重启 Peekaboo", "Restart Peekaboo")) { AppRelaunch.now() }
                                ProgressView().controlSize(.small)
                                Text(L("正在等待授权…", "Waiting for permission…")).font(.caption).foregroundStyle(.secondary)
                            }
                        }
                    }
                    .task {
                        // Poll so the page reacts as soon as permission is granted.
                        while !Task.isCancelled && !t.permissionGranted {
                            try? await Task.sleep(for: .seconds(1.5))
                            t.apply()
                        }
                    }
                } else if !s.typingEnabled && t.days.isEmpty {
                    Card(L("打字统计", "Typing stats"), symbol: "keyboard") {
                        EmptyHint(symbol: "keyboard", title: L("还没开启", "Not enabled"),
                                  detail: L("打开右上角的开关，就能看到每天敲了多少键、哪个时段最勤快、最常按的键和在哪些应用里打字最多。\n只计数，不记录内容。", "Turn on the switch at the top right to see how many keys you press each day, your busiest hours, your most-used keys and the apps you type in most.\nOnly counts, never records content."))
                    }
                }

                if !t.days.isEmpty || t.running {
                    summary(t, goal: s.typingGoal)
                    HStack(alignment: .top, spacing: 14) {
                        hourly(t).frame(maxWidth: .infinity)
                        daily(t).frame(maxWidth: .infinity)
                    }
                    Card(L("键盘热力图（今天）", "Keyboard heatmap (today)"), symbol: "keyboard") { heatmap(t.today) }
                    HStack(alignment: .top, spacing: 14) {
                        topKeys(t.today).frame(maxWidth: .infinity)
                        topApps(t.today).frame(maxWidth: .infinity)
                    }
                    HStack {
                        Stepper(L("每日目标 \(s.typingGoal) 次", "Daily goal: \(s.typingGoal) keys"), value: Binding(get: { s.typingGoal }, set: { s.typingGoal = $0 }), in: 1000...100_000, step: 1000)
                        Spacer()
                        Button(L("清空全部打字数据…", "Clear all typing data…"), role: .destructive) {
                            if FileActions.confirm(L("清空打字数据？", "Clear typing data?"), L("会删除所有已记录的打字统计，不能恢复。", "This deletes all recorded typing stats. It can't be undone."), ok: L("清空", "Clear")) { t.clearAll() }
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
            Card(L("今天", "Today"), symbol: "flame") {
                HStack(spacing: 16) {
                    Ring(value: progress, color: Palette.theme, lineWidth: 9, label: Fmt.pct(min(progress, 9.99)), caption: L("目标", "goal"))
                        .frame(width: 84, height: 84)
                    VStack(alignment: .leading, spacing: 4) {
                        Text("\(d.total)").font(.system(size: 34, weight: .bold, design: .rounded)).monospacedDigit()
                            .contentTransition(.numericText()).animation(.easeOut, value: d.total)
                        Text(L("次按键 · 目标 \(goal)", "keystrokes · goal \(goal)")).font(.caption).foregroundStyle(.secondary)
                        Text(y.total > 0 ? L("昨天 \(y.total)（\(d.total >= y.total ? "已超过" : "还差 \(y.total - d.total)")）", "Yesterday \(y.total) (\(d.total >= y.total ? "passed" : "\(y.total - d.total) to go"))") : L("昨天没有记录", "No record yesterday"))
                            .font(.caption).foregroundStyle(.secondary)
                    }
                }
            }
            .frame(maxWidth: .infinity)
            Card(L("节奏", "Rhythm"), symbol: "metronome") {
                VStack(spacing: 8) {
                    StatLine(label: L("打字时长", "Typing time"), value: minutes(d.activeSeconds))
                    StatLine(label: L("最长连续", "Longest streak"), value: minutes(d.longestStreak))
                    StatLine(label: L("平均速度", "Average speed"), value: kpm > 0 ? String(format: L("%.0f 键/分钟", "%.0f keys/min"), kpm) : "—")
                    StatLine(label: L("状态", "Status"), value: t.running ? L("统计中", "Counting") : L("已暂停", "Paused"), color: t.running ? Palette.disk : .gray)
                }
            }
            .frame(width: 300)
        }
    }

    private func minutes(_ s: Double) -> String {
        let m = Int(s / 60)
        return m >= 60 ? L("\(m / 60) 小时 \(m % 60) 分", "\(m / 60)h \(m % 60)m") : L("\(m) 分钟", "\(m) min")
    }

    private func hourly(_ t: TypingMonitor) -> some View {
        let d = t.today
        return Card(L("今天每小时", "Today by hour"), symbol: "clock") {
            Chart {
                ForEach(0..<24, id: \.self) { h in
                    BarMark(x: .value(L("小时", "Hour"), h), y: .value(L("按键", "Keys"), d.hours[h]))
                        .foregroundStyle(Palette.theme.gradient)
                        .cornerRadius(3)
                }
            }
            .chartXScale(domain: -0.5...23.5)
            .chartXAxis { AxisMarks(values: [0, 6, 12, 18]) { v in AxisValueLabel { Text(L("\(v.as(Int.self) ?? 0)点", "\(v.as(Int.self) ?? 0):00")) } } }
            .frame(height: 160)
        }
    }

    private func daily(_ t: TypingMonitor) -> some View {
        let pts: [(Date, Int)] = (0..<range).reversed().map { off in
            (Calendar.current.date(byAdding: .day, value: -off, to: Date())!, t.day(offset: off).total)
        }
        return Card(L("每天", "Daily"), symbol: "calendar", trailing: {
            Picker("", selection: $range) { Text(L("7 天", "7 days")).tag(7); Text(L("30 天", "30 days")).tag(30) }
                .pickerStyle(.segmented).labelsHidden().frame(width: 120)
        }) {
            Chart {
                ForEach(pts, id: \.0) { p in
                    BarMark(x: .value(L("日期", "Date"), p.0, unit: .day), y: .value(L("按键", "Keys"), p.1))
                        .foregroundStyle(Palette.mem.gradient).cornerRadius(3)
                }
                RuleMark(y: .value(L("目标", "Goal"), app.settings.typingGoal))
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
                            .help(L("\(key.label)：\(n) 次", "\(key.label): \(n) presses"))
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
        return Card(L("最常按的键", "Most-used keys"), symbol: "star") {
            if top.isEmpty { Text(L("今天还没有数据", "No data yet today")).foregroundStyle(.secondary) }
            ForEach(Array(top), id: \.key) { k, v in
                HStack {
                    Text(KeyboardLayout.labels[Int(k) ?? -1] ?? L("键 \(k)", "Key \(k)")).frame(width: 50, alignment: .leading)
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
        return Card(L("在哪里打字最多", "Where you type most"), symbol: "app.badge") {
            if top.isEmpty { Text(L("今天还没有数据", "No data yet today")).foregroundStyle(.secondary) }
            ForEach(Array(top), id: \.key) { name, v in
                HStack {
                    Text(name == "未知" ? L("未知", "Unknown") : name).lineLimit(1).frame(width: 120, alignment: .leading)
                    SizeBar(fraction: Double(v) / total, color: Palette.mem)
                    Text(Fmt.pct(Double(v) / total)).monospacedDigit().frame(width: 50, alignment: .trailing)
                }
                .font(.callout)
            }
        }
    }
}
