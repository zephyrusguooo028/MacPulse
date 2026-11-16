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
                PageHeader(title: "设置", subtitle: "MacPulse 1.0") { EmptyView() }

                Card("菜单栏", symbol: "menubar.rectangle") {
                    Picker("点开后的面板", selection: $s.popoverStyle) {
                        ForEach(PopoverStyle.allCases) { Text($0.title).tag($0) }
                    }
                    .pickerStyle(.segmented)
                    Divider()
                    Text("动画图标").font(.subheadline.weight(.semibold))
                    Text("CPU 越忙，动画越快。动画本身大约占 1–2% CPU，选「不显示」最省电。").font(.caption).foregroundStyle(.secondary)
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
                                .background(RoundedRectangle(cornerRadius: 10).fill(s.menuIcon == icon ? Color.accentColor.opacity(0.14) : Color.primary.opacity(0.04)))
                                .overlay(RoundedRectangle(cornerRadius: 10).strokeBorder(s.menuIcon == icon ? Color.accentColor : .clear, lineWidth: 1.5))
                                .contentShape(Rectangle())
                            }
                            .buttonStyle(.plain)
                        }
                    }
                    .onAppear { IconAnimator.shared.viewer(true) }
                    .onDisappear { IconAnimator.shared.viewer(false) }
                    Toggle("图标按 CPU 负载变色（绿 / 橙 / 红）", isOn: $s.iconTint)
                        .onChange(of: s.iconTint) { _, _ in StatusItemController.shared.refresh() }

                    Divider()
                    Text("图标旁边显示").font(.subheadline.weight(.semibold))
                    LazyVGrid(columns: Array(repeating: GridItem(.flexible(), alignment: .leading), count: 4), alignment: .leading, spacing: 8) {
                        ForEach(MenuMetric.allCases) { metric in
                            Toggle(metric.title, isOn: Binding(get: { s.isShown(metric) },
                                                               set: { s.setShown(metric, $0); StatusItemController.shared.refresh() }))
                                .toggleStyle(.checkbox)
                        }
                    }
                    HStack(spacing: 6) {
                        Text("预览").font(.caption).foregroundStyle(.secondary)
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
                        Text("刷新间隔")
                        Picker("", selection: $s.sampleInterval) {
                            Text("1 秒").tag(1.0)
                            Text("2 秒").tag(2.0)
                            Text("3 秒").tag(3.0)
                            Text("5 秒").tag(5.0)
                        }
                        .labelsHidden().frame(width: 100)
                        .onChange(of: s.sampleInterval) { _, _ in app.monitor.reschedule() }
                        Text("间隔越长越省电").font(.caption).foregroundStyle(.secondary)
                    }
                    Toggle("显示 CPU 温度（估算值，读取的是芯片自带的传感器）", isOn: $s.showTemperature)
                }

                Card("通用", symbol: "gearshape") {
                    Toggle("开机时自动启动", isOn: $launchAtLogin)
                        .onChange(of: launchAtLogin) { _, v in
                            AppSettings.shared.launchAtLogin = v
                            let actual = AppSettings.shared.launchAtLogin
                            if actual != v {
                                loginError = AppSettings.shared.launchAtLoginNeedsApproval
                                    ? "需要在 系统设置 › 通用 › 登录项 里允许 MacPulse。"
                                    : "设置失败。请把 MacPulse.app 放到「应用程序」文件夹后再试。"
                            } else { loginError = nil }
                        }
                    if let e = loginError { Text(e).font(.caption).foregroundStyle(Palette.warn) }
                    HStack {
                        VStack(alignment: .leading, spacing: 2) {
                            Text("完全磁盘访问权限")
                            Text("可选。打开后能统计邮件、信息、废纸篓等受保护的目录。").font(.caption).foregroundStyle(.secondary)
                        }
                        Spacer()
                        Button("打开系统设置") { FileActions.openFullDiskAccessSettings() }
                    }
                }

                Card("提醒", symbol: "bell") {
                    HStack {
                        Toggle("磁盘可用空间低于", isOn: $s.alertDisk)
                        Stepper("\(s.alertDiskGB) GB", value: $s.alertDiskGB, in: 5...200, step: 5).disabled(!s.alertDisk)
                        Text("时提醒")
                    }
                    Toggle("内存压力持续紧张时提醒", isOn: $s.alertMemory)
                    HStack {
                        Toggle("CPU 连续 5 分钟高于", isOn: $s.alertCPU)
                        Stepper("\(s.alertCPUPercent)%", value: $s.alertCPUPercent, in: 50...100, step: 5).disabled(!s.alertCPU)
                        Text("时提醒")
                    }
                    HStack {
                        Toggle("每周检查一次，可清理空间超过", isOn: $s.cleanupTips)
                        Stepper("\(s.cleanupTipGB) GB", value: $s.cleanupTipGB, in: 1...100).disabled(!s.cleanupTips)
                        Text("时提示")
                    }
                    Text("同一种提醒有冷却时间，不会反复打扰。").font(.caption).foregroundStyle(.secondary)
                }

                Card("清理规则", symbol: "sparkles") {
                    Stepper("下载文件夹里超过 \(s.downloadsOldDays) 天没动的文件算「旧文件」", value: $s.downloadsOldDays, in: 7...720, step: 15)
                    Stepper("超过 \(s.largeFileMB) MB 的文件算「大文件」", value: $s.largeFileMB, in: 50...10_000, step: 50)
                    Stepper("超过 \(s.staleProjectDays) 天没改动的项目，默认勾选它的依赖和构建产物", value: $s.staleProjectDays, in: 7...365, step: 7)
                }

                pathList(title: "项目目录", symbol: "folder", note: "在这些目录里自动找开发项目（最多往下找 4 层）。", paths: $s.projectRoots)
                pathList(title: "白名单", symbol: "lock.shield", note: "这些位置永远不会被清理。也可以在任意列表里右键「加入白名单」。", paths: $s.whitelist)

                Card("关于", symbol: "info.circle") {
                    Text("MacPulse 只在本机读取系统信息，不联网、不上传任何数据。清理默认移到废纸篓，并且永远不碰系统目录和用户目录之外的文件。")
                        .font(.callout).foregroundStyle(.secondary)
                    Text("数据保存在 \(Paths.support.tildePath)").font(.caption).foregroundStyle(.tertiary).textSelection(.enabled)
                }
            }
            .padding(24)
            .frame(maxWidth: 760, alignment: .leading)
        }
    }

    private func pathList(title: String, symbol: String, note: String, paths: Binding<[String]>) -> some View {
        Card(title, symbol: symbol, trailing: {
            Button { add(to: paths) } label: { Label("添加…", systemImage: "plus") }.controlSize(.small)
        }) {
            Text(note).font(.caption).foregroundStyle(.secondary)
            if paths.wrappedValue.isEmpty {
                Text("（空）").foregroundStyle(.tertiary)
            }
            ForEach(Array(paths.wrappedValue.enumerated()), id: \.offset) { i, p in
                HStack {
                    Image(systemName: "folder").foregroundStyle(.secondary)
                    Text(p)
                    if !FileManager.default.fileExists(atPath: (p as NSString).expandingTildeInPath) {
                        Text("不存在").font(.caption).foregroundStyle(.tertiary)
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
