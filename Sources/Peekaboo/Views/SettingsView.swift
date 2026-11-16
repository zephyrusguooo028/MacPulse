import SwiftUI
import AppKit

struct SettingsView: View {
    @Environment(AppState.self) private var app
    @Local private var launchAtLogin = AppSettings.shared.launchAtLogin
    @Local private var loginError: String?

    var body: some View {
        @Bindable var s = app.settings
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                PageHeader(title: L("设置", "Settings"), subtitle: "Peekaboo 1.0") { EmptyView() }

                Card(L("语言", "Language"), symbol: "globe") {
                    Picker(L("界面语言", "Interface language"), selection: $s.language) {
                        ForEach(AppLanguage.allCases) { Text($0.title).tag($0) }
                    }
                    .frame(maxWidth: 360)
                    .onChange(of: s.language) { _, _ in StatusItemController.shared.refresh() }
                }

                Card(L("菜单栏", "Menu bar"), symbol: "menubar.rectangle") {
                    Picker(L("点开后的面板", "Popover style"), selection: $s.popoverStyle) {
                        ForEach(PopoverStyle.allCases) { Text($0.title).tag($0) }
                    }
                    .pickerStyle(.segmented)
                    HStack(spacing: 10) {
                        Text(L("主题色", "Theme color"))
                        ForEach(ThemeColor.allCases) { t in
                            Button { s.themeColor = t } label: {
                                VStack(spacing: 4) {
                                    ZStack {
                                        if t == .system {
                                            Circle().fill(AngularGradient(colors: [.red, .orange, .yellow, .green, .blue, .purple, .red], center: .center))
                                        } else if t == .load {
                                            Circle().fill(LinearGradient(colors: [Palette.disk, Palette.warn, Palette.danger], startPoint: .leading, endPoint: .trailing))
                                        } else {
                                            Circle().fill(Palette.color(t, cpu: 0))
                                        }
                                        if s.themeColor == t { Image(systemName: "checkmark").font(.system(size: 10, weight: .bold)).foregroundStyle(t == .mono ? Color(nsColor: .windowBackgroundColor) : .white) }
                                    }
                                    .frame(width: 22, height: 22)
                                    .overlay(Circle().strokeBorder(Color.primary.opacity(s.themeColor == t ? 0.5 : 0.1), lineWidth: s.themeColor == t ? 2 : 1))
                                    Text(t.title).font(.caption2).foregroundStyle(.secondary)
                                }
                            }
                            .buttonStyle(.plain)
                        }
                    }
                    Divider()
                    Text(L("动画图标", "Animated icon")).font(.subheadline.weight(.semibold))
                    Text(L("CPU 越忙，动画越快。动画本身大约占 1–2% CPU，选「不显示」最省电。", "The busier the CPU, the faster the animation. The animation itself uses about 1–2% CPU; choose “None” to save the most power.")).font(.caption).foregroundStyle(.secondary)
                    LazyVGrid(columns: Array(repeating: GridItem(.flexible(), spacing: 8), count: 5), spacing: 8) {
                        ForEach(MenuIcon.allCases) { icon in
                            Button { s.menuIcon = icon; StatusItemController.shared.refresh() } label: {
                                VStack(spacing: 6) {
                                    if icon == .none {
                                        Image(systemName: "nosign").font(.system(size: 22)).foregroundStyle(.tertiary).frame(height: 36)
                                    } else {
                                        AnimatedIconView(icon: icon, height: 36, tinted: s.menuIcon == icon)
                                    }
                                    Text(icon.title).font(.caption)
                                }
                                .frame(maxWidth: .infinity)
                                .padding(.vertical, 10)
                                .background(RoundedRectangle(cornerRadius: 10).fill(s.menuIcon == icon ? Palette.theme.opacity(0.14) : Color.primary.opacity(0.04)))
                                .overlay(RoundedRectangle(cornerRadius: 10).strokeBorder(s.menuIcon == icon ? Palette.theme : .clear, lineWidth: 1.5))
                                .contentShape(Rectangle())
                            }
                            .buttonStyle(.plain)
                        }
                    }
                    .onAppear { IconAnimator.shared.viewer(true) }
                    .onDisappear { IconAnimator.shared.viewer(false) }
                    Toggle(L("图标按 CPU 负载变色（绿 / 橙 / 红）", "Tint icon by CPU load (green / orange / red)"), isOn: $s.iconTint)
                        .onChange(of: s.iconTint) { _, _ in StatusItemController.shared.refresh() }

                    Divider()
                    Text(L("图标旁边显示", "Show next to icon")).font(.subheadline.weight(.semibold))
                    LazyVGrid(columns: Array(repeating: GridItem(.flexible(), alignment: .leading), count: 4), alignment: .leading, spacing: 8) {
                        ForEach(MenuMetric.allCases) { metric in
                            Toggle(metric.title, isOn: Binding(get: { s.isShown(metric) },
                                                               set: { s.setShown(metric, $0); StatusItemController.shared.refresh() }))
                                .toggleStyle(.checkbox)
                        }
                    }
                    HStack(spacing: 6) {
                        Text(L("预览", "Preview")).font(.caption).foregroundStyle(.secondary)
                        HStack(spacing: 6) {
                            if s.menuIcon != .none { AnimatedIconView(icon: s.menuIcon, height: 18, tinted: false) }
                            if s.isShown(.cpuGraph) {
                                Image(nsImage: IconRenderer.cpuGraph(app.monitor.samples.suffix(14).map(\.cpu))).renderingMode(.template)
                            }
                            Text(s.menuMetrics.compactMap { $0.text(app.monitor) }.joined(separator: "  "))
                                .font(.system(size: 12, weight: .medium)).monospacedDigit()
                        }
                        .padding(.horizontal, 10).padding(.vertical, 4)
                        .background(Capsule().fill(Color.primary.opacity(0.06)))
                    }
                    Divider()
                    HStack {
                        Text(L("刷新间隔", "Refresh interval"))
                        Picker("", selection: $s.sampleInterval) {
                            Text(L("1 秒", "1 s")).tag(1.0)
                            Text(L("2 秒", "2 s")).tag(2.0)
                            Text(L("3 秒", "3 s")).tag(3.0)
                            Text(L("5 秒", "5 s")).tag(5.0)
                        }
                        .labelsHidden().frame(width: 100)
                        .onChange(of: s.sampleInterval) { _, _ in app.monitor.reschedule() }
                        Text(L("间隔越长越省电", "Longer intervals save more power")).font(.caption).foregroundStyle(.secondary)
                    }
                    Toggle(L("显示 CPU 温度（估算值，读取的是芯片自带的传感器）", "Show CPU temperature (estimated from the chip's built-in sensors)"), isOn: $s.showTemperature)
                }

                Card(L("通用", "General"), symbol: "gearshape") {
                    Toggle(L("开机时自动启动", "Launch at login"), isOn: $launchAtLogin)
                        .onChange(of: launchAtLogin) { _, v in
                            AppSettings.shared.launchAtLogin = v
                            let actual = AppSettings.shared.launchAtLogin
                            if actual != v {
                                loginError = AppSettings.shared.launchAtLoginNeedsApproval
                                    ? L("需要在 系统设置 › 通用 › 登录项 里允许 Peekaboo。", "Allow Peekaboo in System Settings › General › Login Items.")
                                    : L("设置失败。请把 Peekaboo.app 放到「应用程序」文件夹后再试。", "Couldn't change the setting. Move Peekaboo.app to the Applications folder and try again.")
                            } else { loginError = nil }
                        }
                    if let e = loginError { Text(e).font(.caption).foregroundStyle(Palette.warn) }
                    HStack {
                        VStack(alignment: .leading, spacing: 2) {
                            Text(L("完全磁盘访问权限", "Full Disk Access"))
                            Text(L("可选。打开后能统计邮件、信息、废纸篓等受保护的目录。", "Optional. Lets Peekaboo measure protected folders such as Mail, Messages and the Trash.")).font(.caption).foregroundStyle(.secondary)
                        }
                        Spacer()
                        Button(L("打开系统设置", "Open System Settings")) { FileActions.openFullDiskAccessSettings() }
                    }
                }

                Card(L("提醒", "Alerts"), symbol: "bell") {
                    HStack {
                        Toggle(L("磁盘可用空间低于", "Alert when free disk space is below"), isOn: $s.alertDisk)
                        Stepper("\(s.alertDiskGB) GB", value: $s.alertDiskGB, in: 5...200, step: 5).disabled(!s.alertDisk)
                        Text(L("时提醒", ""))
                    }
                    Toggle(L("内存压力持续紧张时提醒", "Alert when memory pressure stays critical"), isOn: $s.alertMemory)
                    HStack {
                        Toggle(L("CPU 连续 5 分钟高于", "Alert when CPU stays for 5 minutes above"), isOn: $s.alertCPU)
                        Stepper("\(s.alertCPUPercent)%", value: $s.alertCPUPercent, in: 50...100, step: 5).disabled(!s.alertCPU)
                        Text(L("时提醒", ""))
                    }
                    HStack {
                        Toggle(L("每周检查一次，可清理空间超过", "Check weekly and notify when cleanable space exceeds"), isOn: $s.cleanupTips)
                        Stepper("\(s.cleanupTipGB) GB", value: $s.cleanupTipGB, in: 1...100).disabled(!s.cleanupTips)
                        Text(L("时提示", ""))
                    }
                    Text(L("同一种提醒有冷却时间，不会反复打扰。", "Each alert has a cooldown, so you won't be bothered repeatedly.")).font(.caption).foregroundStyle(.secondary)
                }

                Card(L("清理规则", "Cleanup rules"), symbol: "sparkles") {
                    Stepper(L("下载文件夹里超过 \(s.downloadsOldDays) 天没动的文件算「旧文件」", "Files in Downloads untouched for over \(s.downloadsOldDays) days count as “old”"), value: $s.downloadsOldDays, in: 7...720, step: 15)
                    Stepper(L("超过 \(s.largeFileMB) MB 的文件算「大文件」", "Files over \(s.largeFileMB) MB count as “large”"), value: $s.largeFileMB, in: 50...10_000, step: 50)
                    Stepper(L("超过 \(s.staleProjectDays) 天没改动的项目，默认勾选它的依赖和构建产物", "Preselect dependencies and build output of projects unchanged for over \(s.staleProjectDays) days"), value: $s.staleProjectDays, in: 7...365, step: 7)
                }

                pathList(title: L("项目目录", "Project folders"), symbol: "folder", note: L("在这些目录里自动找开发项目（最多往下找 4 层）。", "Development projects are found automatically in these folders (up to 4 levels deep)."), paths: $s.projectRoots)
                pathList(title: L("白名单", "Whitelist"), symbol: "lock.shield", note: L("这些位置永远不会被清理。也可以在任意列表里右键「加入白名单」。", "These locations are never cleaned. You can also right-click any list item and choose “Add to whitelist”."), paths: $s.whitelist)

                ClipboardSettingsCard()
                WidgetSettingsCard()

                Card(L("关于", "About"), symbol: "info.circle") {
                    Text(L("Peekaboo 只在本机读取系统信息，不联网、不上传任何数据。清理默认移到废纸篓，并且永远不碰系统目录和用户目录之外的文件。", "Peekaboo reads system information locally only. It never goes online or uploads any data. Cleanup moves items to the Trash by default and never touches system folders or files outside your home folder."))
                        .font(.callout).foregroundStyle(.secondary)
                    Text(L("数据保存在 \(Paths.support.tildePath)", "Data is stored in \(Paths.support.tildePath)")).font(.caption).foregroundStyle(.tertiary).textSelection(.enabled)
                }
            }
            .padding(24)
            .frame(maxWidth: 760, alignment: .leading)
        }
    }

    private func pathList(title: String, symbol: String, note: String, paths: Binding<[String]>) -> some View {
        Card(title, symbol: symbol, trailing: {
            Button { add(to: paths) } label: { Label(L("添加…", "Add…"), systemImage: "plus") }.controlSize(.small)
        }) {
            Text(note).font(.caption).foregroundStyle(.secondary)
            if paths.wrappedValue.isEmpty {
                Text(L("（空）", "(empty)")).foregroundStyle(.tertiary)
            }
            ForEach(Array(paths.wrappedValue.enumerated()), id: \.offset) { i, p in
                HStack {
                    Image(systemName: "folder").foregroundStyle(.secondary)
                    Text(p)
                    if !FileManager.default.fileExists(atPath: (p as NSString).expandingTildeInPath) {
                        Text(L("不存在", "Missing")).font(.caption).foregroundStyle(.tertiary)
                    }
                    Spacer()
                    Button { paths.wrappedValue.remove(at: i) } label: { Image(systemName: "minus.circle") }.buttonStyle(.borderless)
                }
            }
        }
    }

    private func add(to paths: Binding<[String]>) {
        let p = NSOpenPanel()
        p.canChooseDirectories = true
        p.canChooseFiles = true
        p.allowsMultipleSelection = true
        p.directoryURL = Paths.home
        if p.runModal() == .OK {
            for u in p.urls where !paths.wrappedValue.contains(u.tildePath) { paths.wrappedValue.append(u.tildePath) }
        }
    }
}
