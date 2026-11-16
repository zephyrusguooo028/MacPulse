import SwiftUI

struct ScreenshotView: View {
    @Environment(AppState.self) private var app
    @Local private var permission = ScreenshotController.shared.hasPermission

    var body: some View {
        @Bindable var s = app.settings
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                PageHeader(title: "截图", subtitle: "框选截图、标注，然后复制、保存或贴在屏幕上") {
                    Button {
                        WindowManager.shared.window?.orderOut(nil)
                        ScreenshotController.shared.start(after: 0.3)
                    } label: { Label("开始截图", systemImage: "camera.viewfinder") }
                    .buttonStyle(.borderedProminent).controlSize(.large)
                }

                if !permission {
                    Card("需要「屏幕录制」权限", symbol: "lock.shield") {
                        Text("macOS 要求截图的应用获得「屏幕录制」权限。截图只保存在你的电脑上，不会上传。授权后可能需要重新打开 MacPulse。")
                            .foregroundStyle(.secondary)
                        HStack {
                            Button("去授权") { ScreenshotController.shared.start() }.buttonStyle(.borderedProminent)
                            Button("重启 MacPulse") { AppRelaunch.now() }
                        }
                        .task {
                            while !Task.isCancelled && !permission {
                                try? await Task.sleep(for: .seconds(1.5))
                                permission = ScreenshotController.shared.hasPermission
                            }
                        }
                    }
                }

                Card("快捷键与保存", symbol: "command") {
                    HStack(spacing: 10) {
                        Text("截图快捷键")
                        HotkeyRecorder(spec: Binding(get: { s.shotHotkey }, set: { s.shotHotkey = $0; ScreenshotController.shared.applyHotkey() }))
                        Button("恢复默认") { s.shotHotkey = .defaultShot; ScreenshotController.shared.applyHotkey() }
                            .disabled(s.shotHotkey == .defaultShot)
                    }
                    if !ScreenshotController.shared.hotkeyOK {
                        Text("这个快捷键已被占用，换一个试试。").font(.caption).foregroundStyle(Palette.danger)
                    }
                    HStack {
                        Text("保存到")
                        Text(s.shotFolder).foregroundStyle(.secondary)
                        Button("更改…") {
                            let p = NSOpenPanel(); p.canChooseDirectories = true; p.canChooseFiles = false
                            if p.runModal() == .OK, let u = p.url { s.shotFolder = u.tildePath }
                        }
                    }
                }

                Card("怎么用", symbol: "questionmark.circle") {
                    VStack(alignment: .leading, spacing: 8) {
                        tip("cursorarrow.click", "鼠标移到窗口上会自动高亮，单击直接截这个窗口；拖动可以自由框选。")
                        tip("hand.draw", "框选后可以拖动选区；选工具后在选区里画矩形、箭头、画笔、文字或马赛克。")
                        tip("return", "回车或双击：复制到剪贴板。⌘Z 撤销，Esc 取消，右键重新框选。")
                        tip("pin", "「贴到屏幕」会把截图钉在最上层：拖动移动、滚轮缩放、双击关闭、右键调透明度。")
                    }
                }

                Card("贴图", symbol: "pin") {
                    HStack {
                        Text("当前屏幕上有 \(ScreenshotController.shared.pins.count) 张贴图")
                        Spacer()
                        Button("全部关闭") { ScreenshotController.shared.closeAllPins() }
                            .disabled(ScreenshotController.shared.pins.isEmpty)
                    }
                }
            }
            .padding(24)
        }
    }

    private func tip(_ symbol: String, _ text: String) -> some View {
        HStack(alignment: .top, spacing: 10) {
            Image(systemName: symbol).foregroundStyle(Palette.theme).frame(width: 20)
            Text(text).foregroundStyle(.secondary)
        }
    }
}

/// Click, then press the shortcut. Esc cancels, ⌫ clears.
struct HotkeyRecorder: View {
    @Binding var spec: HotkeySpec?
    @Local private var recording = false
    @Local private var hint: String?
    @Local private var monitor: Any?

    var body: some View {
        Button {
            recording ? stop() : start()
        } label: {
            Text(recording ? (hint ?? "请按下快捷键…") : (spec?.title ?? "未设置"))
                .font(.system(size: 13, weight: .semibold, design: .rounded))
                .monospacedDigit()
                .frame(minWidth: 130)
                .padding(.vertical, 4)
                .padding(.horizontal, 10)
                .background(RoundedRectangle(cornerRadius: 7, style: .continuous)
                    .fill(recording ? Palette.theme.opacity(0.18) : Color.primary.opacity(0.06)))
                .overlay(RoundedRectangle(cornerRadius: 7, style: .continuous)
                    .strokeBorder(recording ? Palette.theme : Color.primary.opacity(0.12), lineWidth: recording ? 1.5 : 1))
        }
        .buttonStyle(.plain)
        .help("点一下，然后按下想要的组合键。Esc 取消，⌫ 清除。")
        .onDisappear { stop() }
    }

    private func start() {
        recording = true
        hint = nil
        Hotkeys.shared.unregister(id: 1) // so the old shortcut does not fire while recording
        monitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { e in
            if e.keyCode == 53 { stop(); return nil }                       // esc
            if e.keyCode == 51 && e.modifierFlags.intersection([.command, .control, .option, .shift]).isEmpty {
                spec = nil; stop(); return nil                                // delete clears
            }
            if let s = HotkeySpec.from(e) { spec = s; stop() }
            else { hint = "要加上 ⌃ / ⌥ / ⌘" }
            return nil
        }
    }

    private func stop() {
        if let m = monitor { NSEvent.removeMonitor(m) }
        monitor = nil
        if recording { recording = false; ScreenshotController.shared.applyHotkey() }
    }
}
