import SwiftUI

struct ScreenshotView: View {
    @Environment(AppState.self) private var app
    @Local private var permission = ScreenshotController.shared.hasPermission

    var body: some View {
        @Bindable var s = app.settings
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                PageHeader(title: L("截图", "Screenshot"), subtitle: L("框选截图、标注，然后复制、保存或贴在屏幕上", "Select an area, annotate, then copy, save or pin it to the screen")) {
                    Button {
                        WindowManager.shared.window?.orderOut(nil)
                        ScreenshotController.shared.start(after: 0.3)
                    } label: { Label(L("开始截图", "Take screenshot"), systemImage: "camera.viewfinder") }
                    .buttonStyle(.borderedProminent).controlSize(.large)
                }

                if !permission {
                    Card(L("需要「屏幕录制」权限", "Screen Recording permission required"), symbol: "lock.shield") {
                        Text(L("macOS 要求截图的应用获得「屏幕录制」权限。截图只保存在你的电脑上，不会上传。授权后可能需要重新打开 Peekaboo。", "macOS requires Screen Recording permission for apps that take screenshots. Screenshots stay on your Mac and are never uploaded. You may need to reopen Peekaboo after granting access."))
                            .foregroundStyle(.secondary)
                        HStack {
                            Button(L("去授权", "Grant access")) { ScreenshotController.shared.start() }.buttonStyle(.borderedProminent)
                            Button(L("重启 Peekaboo", "Restart Peekaboo")) { AppRelaunch.now() }
                        }
                        .task {
                            while !Task.isCancelled && !permission {
                                try? await Task.sleep(for: .seconds(1.5))
                                permission = ScreenshotController.shared.hasPermission
                            }
                        }
                    }
                }

                Card(L("快捷键与保存", "Shortcut & saving"), symbol: "command") {
                    HStack(spacing: 10) {
                        Text(L("截图快捷键", "Screenshot shortcut"))
                        HotkeyRecorder(spec: Binding(get: { s.shotHotkey }, set: { s.shotHotkey = $0; ScreenshotController.shared.applyHotkey() }))
                        Button(L("恢复默认", "Restore default")) { s.shotHotkey = .defaultShot; ScreenshotController.shared.applyHotkey() }
                            .disabled(s.shotHotkey == .defaultShot)
                    }
                    if !ScreenshotController.shared.hotkeyOK {
                        Text(L("这个快捷键已被占用，换一个试试。", "This shortcut is already in use. Try another one.")).font(.caption).foregroundStyle(Palette.danger)
                    }
                    HStack {
                        Text(L("保存到", "Save to"))
                        Text(s.shotFolder).foregroundStyle(.secondary)
                        Button(L("更改…", "Change…")) {
                            let p = NSOpenPanel(); p.canChooseDirectories = true; p.canChooseFiles = false
                            if p.runModal() == .OK, let u = p.url { s.shotFolder = u.tildePath }
                        }
                    }
                }

                Card(L("怎么用", "How to use"), symbol: "questionmark.circle") {
                    VStack(alignment: .leading, spacing: 8) {
                        tip("cursorarrow.click", L("鼠标移到窗口上会自动高亮，单击直接截这个窗口；拖动可以自由框选。", "Hovering a window highlights it; click to capture that window, or drag to select any area."))
                        tip("hand.draw", L("框选后可以拖动选区；选工具后在选区里画矩形、箭头、画笔、文字或马赛克。", "Drag the selection to move it. Pick a tool to draw rectangles, arrows, pen strokes, text or mosaic inside it."))
                        tip("return", L("回车或双击：复制到剪贴板。⌘Z 撤销，Esc 取消，右键重新框选。", "Return or double-click: copy to clipboard. ⌘Z to undo, Esc to cancel, right-click to reselect."))
                        tip("pin", L("「贴到屏幕」会把截图钉在最上层：拖动移动、滚轮缩放、双击关闭、右键调透明度。", "“Pin to screen” keeps the screenshot on top: drag to move, scroll to zoom, double-click to close, right-click to change opacity."))
                    }
                }

                Card(L("贴图", "Pinned screenshots"), symbol: "pin") {
                    HStack {
                        Text(L("当前屏幕上有 \(ScreenshotController.shared.pins.count) 张贴图", "\(ScreenshotController.shared.pins.count) pinned on screen"))
                        Spacer()
                        Button(L("全部关闭", "Close all")) { ScreenshotController.shared.closeAllPins() }
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
            Text(recording ? (hint ?? L("请按下快捷键…", "Press a shortcut…")) : (spec?.title ?? L("未设置", "Not set")))
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
        .help(L("点一下，然后按下想要的组合键。Esc 取消，⌫ 清除。", "Click, then press the key combination you want. Esc to cancel, ⌫ to clear."))
        .onDisappear { stop() }
    }

    private func start() {
        recording = true
        hint = nil
        Hotkeys.shared.pause() // so existing shortcuts do not swallow the keys while recording
        monitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { e in
            if e.keyCode == 53 { stop(); return nil }                       // esc
            if e.keyCode == 51 && e.modifierFlags.intersection([.command, .control, .option, .shift]).isEmpty {
                spec = nil; stop(); return nil                                // delete clears
            }
            if let s = HotkeySpec.from(e) { spec = s; stop() }
            else { hint = L("要加上 ⌃ / ⌥ / ⌘", "Add ⌃ / ⌥ / ⌘") }
            return nil
        }
    }

    private func stop() {
        if let m = monitor { NSEvent.removeMonitor(m) }
        monitor = nil
        if recording { recording = false; Hotkeys.shared.resume() }
    }
}
