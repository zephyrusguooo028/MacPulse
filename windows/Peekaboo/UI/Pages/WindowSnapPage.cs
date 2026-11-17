using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using static Peekaboo.UIX;

namespace Peekaboo;

/// Sidebar page: keyboard window layouts (halves, quarters, thirds…) and their shortcuts.
public sealed class WindowSnapPage : PageView
{
    readonly TextBlock _target = T("", 12.5, color: Theme.Secondary);
    readonly TextBlock _result = T("", 12.5, color: Theme.Secondary);
    bool _recording;
    KeyEventHandler? _keyHandler;

    public override void Shown()
    {
        WindowSnap.Changed -= OnChanged;
        WindowSnap.Changed += OnChanged;
        Build();
    }

    public override void Hidden()
    {
        WindowSnap.Changed -= OnChanged;
        if (_keyHandler is not null && Window.GetWindow(this) is { } w) w.PreviewKeyDown -= _keyHandler;
        _keyHandler = null;
        if (_recording) { _recording = false; WindowSnap.ApplyHotkeys(); }
    }

    void OnChanged()
    {
        if (_recording) return;
        UpdateTarget();
    }

    void UpdateTarget()
    {
        var t = WindowSnap.LastTargetTitle;
        _target.Text = t is null ? L("目标窗口：还没有。先点一下想要排列的窗口，再回到这里。", "Target window: none yet. Click the window you want to arrange, then come back here.")
                                 : L($"目标窗口：{t}", $"Target window: {t}");
    }

    internal static string ShortcutText(SnapAction a)
    {
        if (!WindowSnap.S.Enabled) return L("快捷键已关闭", "Shortcuts off");
        if (WindowSnap.Hotkey(a) is not { } k) return L("未设置", "Not set");
        return WindowSnap.InUse(a) ? L("被占用", "In use") : k.Title.Replace(" + ", "+");
    }

    void Build()
    {
        UpdateTarget();
        TextBlock Tip(string t) { var x = T(t, 12.5, color: Theme.Secondary); x.TextWrapping = TextWrapping.Wrap; x.TextTrimming = TextTrimming.None; return x; }
        _target.TextWrapping = TextWrapping.NoWrap;
        _result.TextWrapping = TextWrapping.Wrap; _result.TextTrimming = TextTrimming.None;

        var enabled = Check(L("启用分屏快捷键（Ctrl + Alt + 方向键等）", "Enable layout shortcuts (Ctrl + Alt + arrows and more)"), WindowSnap.S.Enabled, on => { WindowSnap.SetEnabled(on); Build(); });
        int inUse = WindowSnap.All.Count(WindowSnap.InUse);
        var warn = T(inUse > 0 && WindowSnap.S.Enabled ? L($"有 {inUse} 个快捷键被别的程序占用了，可以在下面换一个。", $"{inUse} shortcuts are used by another program. You can change them below.") : "", 12, color: Theme.B(Theme.DangerC));
        warn.TextWrapping = TextWrapping.Wrap;
        var inPopup = Check(L("在托盘弹窗里显示布局按钮", "Show layout buttons in the tray popup"), WindowSnap.S.SnapInPopup, WindowSnap.SetSnapInPopup);

        var groups = new (string title, SnapAction[] actions)[]
        {
            (L("半屏", "Halves"), new[] { SnapAction.LeftHalf, SnapAction.RightHalf, SnapAction.TopHalf, SnapAction.BottomHalf }),
            (L("四分之一", "Quarters"), new[] { SnapAction.TopLeft, SnapAction.TopRight, SnapAction.BottomLeft, SnapAction.BottomRight }),
            (L("三分之一", "Thirds"), new[] { SnapAction.LeftThird, SnapAction.CenterThird, SnapAction.RightThird, SnapAction.LeftTwoThirds, SnapAction.RightTwoThirds }),
            (L("其他", "Other"), new[] { SnapAction.Maximize, SnapAction.Center, SnapAction.Restore, SnapAction.PrevDisplay, SnapAction.NextDisplay }),
        };
        var layouts = V(14);
        foreach (var (title, actions) in groups)
        {
            var wrap = new WrapPanel();
            foreach (var a in actions) wrap.Children.Add(TileFor(a));
            layouts.Children.Add(V(8, T(title, 12, FontWeights.SemiBold, Theme.Tertiary), wrap));
        }

        var reset = Btn(L("恢复默认", "Restore defaults"), () => { WindowSnap.RestoreDefaults(); Build(); });
        reset.IsEnabled = WindowSnap.S.Overrides.Count > 0;
        var keys = new StackPanel();
        foreach (var a in WindowSnap.All)
        {
            if (keys.Children.Count > 0) keys.Children.Add(new Border { Height = 1, Background = Theme.Line });
            keys.Children.Add(ShortcutRow(a));
        }

        Content = V(16,
            PageHeader(L("分屏", "Window layout"), L("用快捷键或点一下，把窗口排到半屏、四分之一或三分之一", "Snap windows to halves, quarters or thirds with a shortcut or a click")),
            Card(L("分屏快捷键", "Layout shortcuts"), "", V(8,
                enabled,
                Tip(L("拖动窗口到屏幕边缘的分屏由 Windows 自带的「贴靠」完成；Peekaboo 额外提供三分之一布局和全套快捷键。",
                      "Dragging a window to a screen edge is handled by Windows’ own Snap; Peekaboo adds thirds and keyboard shortcuts for every layout.")),
                warn)),
            Card(L("布局", "Layouts"), "", V(12, 
                V(4, inPopup, Tip(L("点托盘图标，再点一个布局，就像 Magnet 的菜单一样。", "Click the tray icon, then a layout, like Magnet’s menu."))),
                V(2, _target, _result), layouts)),
            Card(L("自定义快捷键", "Custom shortcuts"), "", keys, reset));
    }

    FrameworkElement TileFor(SnapAction a)
    {
        var name = T(WindowSnap.Title(a), 12.5, FontWeights.SemiBold);
        string sc = ShortcutText(a);
        bool bad = WindowSnap.S.Enabled && WindowSnap.InUse(a);
        var key = T(sc, 11, color: bad ? Theme.B(Theme.DangerC) : Theme.Secondary);
        var tile = new Border
        {
            Width = 148, Padding = new Thickness(10), Margin = new Thickness(0, 0, 10, 10), CornerRadius = new CornerRadius(8),
            Background = Theme.CardAlt, BorderBrush = Theme.Line, BorderThickness = new Thickness(1), Cursor = Cursors.Hand,
            Child = V(8, Drawing(a), V(2, name, key)),
            ToolTip = L("点一下，应用到目标窗口", "Click to apply to the target window"),
        };
        tile.MouseEnter += (_, _) => { tile.Background = Theme.Hover; tile.BorderBrush = Theme.AccentBrush; };
        tile.MouseLeave += (_, _) => { tile.Background = Theme.CardAlt; tile.BorderBrush = Theme.Line; };
        tile.MouseLeftButtonUp += (_, _) =>
        {
            var title = WindowSnap.LastTargetTitle;
            bool ok = WindowSnap.ApplyToLast(a);
            _result.Foreground = ok ? Theme.Secondary : Theme.B(Theme.WarnC);
            _result.Text = ok ? L($"已对「{title}」应用「{WindowSnap.Title(a)}」。", $"Applied “{WindowSnap.Title(a)}” to “{title}”.")
                : title is null ? L("没有可排列的窗口：先切到想要排列的窗口，再回来点。", "No window to arrange: switch to the window you want, then come back and click.")
                : a == SnapAction.Restore ? L("这个窗口还没有被 Peekaboo 排列过，没有可还原的位置。", "Peekaboo hasn’t moved this window yet, so there’s nothing to restore.")
                : a is SnapAction.PrevDisplay or SnapAction.NextDisplay ? L("只有一个显示器。", "There’s only one display.")
                : L("这个窗口不能调整大小，或已最小化。", "That window can’t be resized or is minimized.");
        };
        return tile;
    }

    /// A small screen with the target region highlighted. Below ~40 px tall (the tray popup's buttons)
    /// Restore and the display moves become a single screen with an icon glyph.
    internal static FrameworkElement Drawing(SnapAction a, double W = 126, double H = 72)
    {
        bool compact = H < 40;
        var canvas = new Canvas { Width = W, Height = H };
        void Screen(double x, double y, double w, double h)
        {
            var s = new Border { Width = w, Height = h, CornerRadius = new CornerRadius(compact ? 3 : 4), Background = Theme.Card, BorderBrush = Theme.Line, BorderThickness = new Thickness(compact ? 1 : 1.5) };
            Canvas.SetLeft(s, x); Canvas.SetTop(s, y); canvas.Children.Add(s);
        }
        void Region(double x, double y, double w, double h, bool solid = true)
        {
            var r = new Border { Width = Math.Max(1, w), Height = Math.Max(1, h), CornerRadius = new CornerRadius(compact ? 1.5 : 2.5),
                                 Background = solid ? Theme.B(Theme.AccentColor, 0.85) : Theme.B(Theme.AccentColor, 0.18),
                                 BorderBrush = Theme.AccentBrush, BorderThickness = new Thickness(solid ? 0 : 1.2) };
            Canvas.SetLeft(r, x); Canvas.SetTop(r, y); canvas.Children.Add(r);
        }
        double pad = compact ? 2.5 : 4;
        if (compact && a is SnapAction.Restore or SnapAction.PrevDisplay or SnapAction.NextDisplay)
        {
            var icon = Glyph(a switch { SnapAction.Restore => "\uE7A7", SnapAction.PrevDisplay => "\uE72A", _ => "\uE72B" }, H * 0.5, Theme.AccentBrush);
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            return new Border { Width = W, Height = H, CornerRadius = new CornerRadius(3), Background = Theme.Card, BorderBrush = Theme.Line, BorderThickness = new Thickness(1), Child = icon };
        }
        if (a is SnapAction.PrevDisplay or SnapAction.NextDisplay)
        {
            double sw = 56, sh = 40, y = (H - sh) / 2;
            Screen(0, y, sw, sh); Screen(W - sw, y, sw, sh);
            bool next = a == SnapAction.NextDisplay;
            Region((next ? W - sw : 0) + pad, y + pad, sw - 2 * pad, sh - 2 * pad);
            Region((next ? 0 : W - sw) + pad, y + pad, sw - 2 * pad, sh - 2 * pad, solid: false);
            var arrow = Glyph(next ? "" : "", 11, Theme.AccentBrush);
            arrow.Width = 14; arrow.TextAlignment = TextAlignment.Center;
            Canvas.SetLeft(arrow, W / 2 - 7); Canvas.SetTop(arrow, H / 2 - 8); canvas.Children.Add(arrow);
            return canvas;
        }
        Screen(0, 0, W, H);
        // Inner work area in "pixels" so the drawing uses the same math as the real snap.
        var work = new SnapRect(0, 0, 1200, 680);
        double sx = (W - 2 * pad) / work.Width, sy = (H - 2 * pad) / work.Height;
        if (a == SnapAction.Restore)
        {
            Region(pad + 150 * sx, pad + 130 * sy, 620 * sx, 420 * sy, solid: false);
            Region(pad + 560 * sx, pad + 60 * sy, 560 * sx, 400 * sy);
            var g = Glyph("", 12, Theme.OnAccent);
            Canvas.SetLeft(g, pad + 790 * sx); Canvas.SetTop(g, pad + 200 * sy); canvas.Children.Add(g);
            return canvas;
        }
        var current = new SnapRect(300, 150, 900, 530);
        if (WindowSnap.Compute(a, work, current) is { } r)
        {
            const double gap = 1;   // so adjacent regions read as separate even when flush
            Region(pad + r.Left * sx + gap, pad + r.Top * sy + gap, r.Width * sx - 2 * gap, r.Height * sy - 2 * gap);
        }
        return canvas;
    }

    FrameworkElement ShortcutRow(SnapAction a)
    {
        var hk = WindowSnap.Hotkey(a);
        bool bad = WindowSnap.S.Enabled && hk is not null && WindowSnap.InUse(a);
        var status = T(bad ? L("被占用", "In use") : WindowSnap.IsCustom(a) ? L("已自定义", "Custom") : "", 12, color: bad ? Theme.B(Theme.DangerC) : Theme.Tertiary);
        var recorder = new Button { Padding = new Thickness(12, 4, 12, 4), MinWidth = 190, Cursor = Cursors.Hand, IsEnabled = WindowSnap.S.Enabled,
            ToolTip = L("点一下，然后按下想要的组合键。Esc 取消，Backspace 清除。", "Click, then press the key combination you want. Esc cancels, Backspace clears.") };
        recorder.Content = T(hk?.Title ?? L("未设置", "Not set"), 12.5, FontWeights.SemiBold);
        void Stop()
        {
            _recording = false;
            if (_keyHandler is not null && Window.GetWindow(this) is { } w) w.PreviewKeyDown -= _keyHandler;
            _keyHandler = null;
            WindowSnap.ApplyHotkeys();
            Build();
        }
        recorder.Click += (_, _) =>
        {
            if (_recording) { Stop(); return; }
            _recording = true;
            WindowSnap.SuspendHotkeys();
            recorder.Content = T(L("请按下快捷键…", "Press a shortcut…"), 12.5, FontWeights.SemiBold, Theme.AccentBrush);
            recorder.BorderBrush = Theme.AccentBrush;
            _keyHandler = (_, e) =>
            {
                var key = e.Key == Key.System ? e.SystemKey : e.Key;
                e.Handled = true;
                if (key == Key.Escape) { Stop(); return; }
                if (key == Key.Back && Keyboard.Modifiers == ModifierKeys.None) { WindowSnap.SetHotkey(a, null); Stop(); return; }
                if (HotkeyData.From(key, Keyboard.Modifiers) is { } spec)
                {
                    spec.Label = key switch { Key.Left => "←", Key.Right => "→", Key.Up => "↑", Key.Down => "↓", Key.Back => "Backspace", _ => spec.Label };
                    WindowSnap.SetHotkey(a, spec); Stop();
                }
                else if (key is not (Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin))
                    recorder.Content = T(L("要加上 Ctrl / Alt / Win", "Include Ctrl / Alt / Win"), 12.5, FontWeights.SemiBold, Theme.B(Theme.WarnC));
            };
            if (Window.GetWindow(this) is { } win) win.PreviewKeyDown += _keyHandler;
        };
        var name = T(WindowSnap.Title(a), 13); name.Width = 150;
        var row = H(10, name, recorder, status);
        row.Margin = new Thickness(0, 5, 0, 5);
        return row;
    }
}
