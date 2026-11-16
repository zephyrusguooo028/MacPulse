using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using static Peekaboo.UIX;

namespace Peekaboo;

public sealed class ScreenshotPage : PageView
{
    Settings S => Settings.Shared;

    public override void Shown() => Build();

    void Build()
    {
        var start = Btn(L("开始截图", "Take screenshot"), () => { MainWindow.Instance?.Hide(); Screenshot.Start(300); }, primary: true, glyph: "");

        var status = T(Screenshot.HotkeyOK ? "" : L("这个快捷键被别的程序占用了，换一个试试。", "This shortcut is used by another program. Try a different one."), 12, color: Theme.B(Theme.DangerC));
        var recorder = new Button { Padding = new Thickness(12, 5, 12, 5), MinWidth = 170, Cursor = Cursors.Hand, ToolTip = L("点一下，然后按下想要的组合键。Esc 取消，Backspace 清除。", "Click, then press the key combination you want. Esc cancels, Backspace clears.") };
        recorder.Content = T(S.ShotKey?.Title ?? L("未设置", "Not set"), 13, FontWeights.SemiBold);
        bool recording = false;
        KeyEventHandler? handler = null;
        void Stop()
        {
            recording = false;
            if (handler is not null && Window.GetWindow(this) is { } w) w.PreviewKeyDown -= handler;
            Screenshot.ApplyHotkey();
            Build();
        }
        recorder.Click += (_, _) =>
        {
            if (recording) { Stop(); return; }
            recording = true;
            Screenshot.SuspendHotkey();
            recorder.Content = T(L("请按下快捷键…", "Press a shortcut…"), 13, FontWeights.SemiBold, Theme.AccentBrush);
            recorder.BorderBrush = Theme.AccentBrush;
            handler = (_, e) =>
            {
                var key = e.Key == Key.System ? e.SystemKey : e.Key;
                e.Handled = true;
                if (key == Key.Escape) { Stop(); return; }
                if (key == Key.Back && Keyboard.Modifiers == ModifierKeys.None) { S.ShotKey = null; S.Save(); Stop(); return; }
                if (HotkeyData.From(key, Keyboard.Modifiers) is { } spec) { S.ShotKey = spec; S.Save(); Stop(); }
                else if (key is not (Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin))
                    recorder.Content = T(L("要加上 Ctrl / Alt / Win", "Include Ctrl / Alt / Win"), 13, FontWeights.SemiBold, Theme.B(Theme.WarnC));
            };
            if (Window.GetWindow(this) is { } win) win.PreviewKeyDown += handler;
        };
        var reset = Btn(L("恢复默认", "Restore default"), () => { S.ShotKey = HotkeyData.Default; S.Save(); Screenshot.ApplyHotkey(); Build(); });
        reset.IsEnabled = !HotkeyData.Default.Same(S.ShotKey);
        var folder = Btn(L("更改…", "Change…"), () =>
        {
            var d = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = Settings.Expand(S.ShotFolder) };
            if (d.ShowDialog() == true) { S.ShotFolder = Fmt.Tilde(d.FolderName); S.Save(); Build(); }
        });

        TextBlock Tip(string t) { var x = T(t, 12.5, color: Theme.Secondary); x.TextWrapping = TextWrapping.Wrap; x.TextTrimming = TextTrimming.None; return x; }
        var closeAll = Btn(L("全部关闭", "Close all"), () => { foreach (var p in Screenshot.Pins.ToList()) p.Close(); Build(); });
        closeAll.IsEnabled = Screenshot.Pins.Count > 0;

        Content = V(16,
            PageHeader(L("截图", "Screenshot"), L("框选截图、标注，然后复制、保存或贴在屏幕上", "Select an area, annotate it, then copy, save or pin it to the screen"), start),
            Card(L("快捷键与保存", "Shortcut and saving"), "", V(10,
                H(8, T(L("截图快捷键", "Screenshot shortcut"), 13), recorder, reset, status),
                H(8, T(L("保存到", "Save to"), 13), T(S.ShotFolder, 13, color: Theme.Secondary), folder))),
            Card(L("怎么用", "How to use"), "", V(8,
                Tip(L("• 鼠标移到窗口上会自动高亮，单击直接截这个窗口；拖动可以自由框选。", "• Hover over a window to highlight it and click to capture it; drag to select any area.")),
                Tip(L("• 框选后可以拖动选区；选工具后在选区里画矩形、箭头、画笔、文字或马赛克。", "• After selecting, drag to move the area; pick a tool to draw rectangles, arrows, pen strokes, text or mosaic.")),
                Tip(L("• 回车或双击：复制到剪贴板。Ctrl+Z 撤销，Esc 取消，右键重新框选。", "• Enter or double-click copies to the clipboard. Ctrl+Z undoes, Esc cancels, right-click reselects.")),
                Tip(L("• 「贴到屏幕」会把截图钉在最上层：拖动移动、滚轮缩放、双击关闭、右键调透明度。", "• “Pin to screen” keeps the shot on top: drag to move, scroll to zoom, double-click to close, right-click for opacity.")))),
            Card(L("贴图", "Pinned shots"), "", Row(T(L($"当前屏幕上有 {Screenshot.Pins.Count} 张贴图", $"{Screenshot.Pins.Count} pinned on screen"), 13), closeAll)));
    }
}
