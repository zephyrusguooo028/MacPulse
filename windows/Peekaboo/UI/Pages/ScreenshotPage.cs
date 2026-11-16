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
        var start = Btn("开始截图", () => { MainWindow.Instance?.Hide(); Screenshot.Start(300); }, primary: true, glyph: "");

        var status = T(Screenshot.HotkeyOK ? "" : "这个快捷键被别的程序占用了，换一个试试。", 12, color: Theme.B(Theme.DangerC));
        var recorder = new Button { Padding = new Thickness(12, 5, 12, 5), MinWidth = 170, Cursor = Cursors.Hand, ToolTip = "点一下，然后按下想要的组合键。Esc 取消，Backspace 清除。" };
        recorder.Content = T(S.ShotKey?.Title ?? "未设置", 13, FontWeights.SemiBold);
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
            recorder.Content = T("请按下快捷键…", 13, FontWeights.SemiBold, Theme.AccentBrush);
            recorder.BorderBrush = Theme.AccentBrush;
            handler = (_, e) =>
            {
                var key = e.Key == Key.System ? e.SystemKey : e.Key;
                e.Handled = true;
                if (key == Key.Escape) { Stop(); return; }
                if (key == Key.Back && Keyboard.Modifiers == ModifierKeys.None) { S.ShotKey = null; S.Save(); Stop(); return; }
                if (HotkeyData.From(key, Keyboard.Modifiers) is { } spec) { S.ShotKey = spec; S.Save(); Stop(); }
                else if (key is not (Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin))
                    recorder.Content = T("要加上 Ctrl / Alt / Win", 13, FontWeights.SemiBold, Theme.B(Theme.WarnC));
            };
            if (Window.GetWindow(this) is { } win) win.PreviewKeyDown += handler;
        };
        var reset = Btn("恢复默认", () => { S.ShotKey = HotkeyData.Default; S.Save(); Screenshot.ApplyHotkey(); Build(); });
        reset.IsEnabled = !HotkeyData.Default.Same(S.ShotKey);
        var folder = Btn("更改…", () =>
        {
            var d = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = Settings.Expand(S.ShotFolder) };
            if (d.ShowDialog() == true) { S.ShotFolder = Fmt.Tilde(d.FolderName); S.Save(); Build(); }
        });

        TextBlock Tip(string t) { var x = T(t, 12.5, color: Theme.Secondary); x.TextWrapping = TextWrapping.Wrap; x.TextTrimming = TextTrimming.None; return x; }
        var closeAll = Btn("全部关闭", () => { foreach (var p in Screenshot.Pins.ToList()) p.Close(); Build(); });
        closeAll.IsEnabled = Screenshot.Pins.Count > 0;

        Content = V(16,
            PageHeader("截图", "框选截图、标注，然后复制、保存或贴在屏幕上", start),
            Card("快捷键与保存", "", V(10,
                H(8, T("截图快捷键", 13), recorder, reset, status),
                H(8, T("保存到", 13), T(S.ShotFolder, 13, color: Theme.Secondary), folder))),
            Card("怎么用", "", V(8,
                Tip("• 鼠标移到窗口上会自动高亮，单击直接截这个窗口；拖动可以自由框选。"),
                Tip("• 框选后可以拖动选区；选工具后在选区里画矩形、箭头、画笔、文字或马赛克。"),
                Tip("• 回车或双击：复制到剪贴板。Ctrl+Z 撤销，Esc 取消，右键重新框选。"),
                Tip("• 「贴到屏幕」会把截图钉在最上层：拖动移动、滚轮缩放、双击关闭、右键调透明度。"))),
            Card("贴图", "", Row(T($"当前屏幕上有 {Screenshot.Pins.Count} 张贴图", 13), closeAll)));
    }
}
