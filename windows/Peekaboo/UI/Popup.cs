using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using static Peekaboo.UIX;

namespace Peekaboo;

/// The "compact list" panel shown when the tray icon is clicked.
public sealed class PopupWindow : Window
{
    static PopupWindow? _current;
    readonly ContentControl _host = new();
    readonly Image _icon = new() { Width = 22, Height = 22, Stretch = Stretch.Uniform };
    readonly Dictionary<string, System.Windows.Media.Imaging.BitmapSource> _frames = new();
    /// The layout section is built once and stays in the tree, so hover state survives the per-tick
    /// refresh; only the parts above and below it are rebuilt.
    readonly FrameworkElement _snap;
    readonly ContentControl _top = new(), _bottom = new();
    readonly TextBlock _snapHint = T("", 11, color: Theme.Tertiary);
    SnapAction? _snapHover;

    public static void Toggle()
    {
        if (_current is { IsVisible: true }) { _current.Close(); _current = null; return; }
        _current = new PopupWindow();
        _current.Show();
        _current.Activate();
    }

    PopupWindow()
    {
        Width = 312; SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; Topmost = true;
        Background = Theme.Window; FontFamily = Theme.Font;
        BorderBrush = Theme.Line; BorderThickness = new Thickness(1);
        Content = _host;
        _snap = BuildSnap();
        var root = V(12, _top, _snap, _bottom);
        root.Margin = new Thickness(14);
        _host.Content = root;
        bool closing = false;
        Closing += (_, _) => closing = true;
        Deactivated += (_, _) => { if (!closing) Close(); };
        Loaded += (_, _) => { Place(); Native.RoundCorners(this); };
        SystemMonitor.Shared.WantProcesses(true);
        SystemMonitor.Shared.Ticked += Rebuild;
        TrayController.Shared.Viewer(true);
        TrayController.Shared.FrameChanged += AnimateIcon;
        WindowSnap.Changed += Rebuild;
        Closed += (_, _) =>
        {
            WindowSnap.Changed -= Rebuild;
            SystemMonitor.Shared.Ticked -= Rebuild;
            SystemMonitor.Shared.WantProcesses(false);
            TrayController.Shared.Viewer(false);
            TrayController.Shared.FrameChanged -= AnimateIcon;
        };
        Rebuild();
        AnimateIcon();
    }

    /// Bottom-right, just above the taskbar (or below it when the taskbar is on top).
    void Place()
    {
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - ActualWidth - 12;
        Top = wa.Bottom - ActualHeight - 12;
        var cursor = System.Windows.Forms.Cursor.Position;
        var src = PresentationSource.FromVisual(this);
        double scale = src?.CompositionTarget?.TransformToDevice.M11 ?? 1;
        if (cursor.Y / scale < wa.Top + 60) Top = wa.Top + 12;
    }

    void AnimateIcon()
    {
        var icon = Settings.Shared.Icon == TrayIcon.None ? TrayIcon.Ghost : Settings.Shared.Icon;
        int f = TrayController.Shared.Frame % IconArt.Frames(icon);
        var c = Theme.AccentColor;
        string key = $"{icon}{f}{c}";
        if (!_frames.TryGetValue(key, out var img))
        {
            using var bmp = IconArt.Render(icon, f, SystemMonitor.Shared.Cpu.Total, System.Drawing.Color.FromArgb(c.R, c.G, c.B), 64);
            img = IconArt.ToWpf(bmp);
            _frames[key] = img;
        }
        _icon.Source = img;
    }

    void Rebuild()
    {
        var m = SystemMonitor.Shared;
        if (_icon.Parent is Border previous) previous.Child = null;
        var header = Row(
            H(10,
                new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(10), Background = Theme.AccentSoftBrush, Child = _icon },
                V(1, T("Peekaboo", 14, FontWeights.SemiBold), T(m.StatusSentence(), 11.5, color: Theme.Secondary))),
            T(L("已运行 ", "Up ") + Fmt.Uptime(), 11, color: Theme.Tertiary));

        FrameworkElement MetricRow(string glyph, string name, double v, string value, Color c) =>
            Row(H(10, Glyph(glyph, 13), Fixed(T(name, 12.5), English ? 58 : 34)), Fixed(T(value, 12.5, FontWeights.SemiBold), 70, right: true)) is Grid g
                ? WithBar(g, v, c) : new Grid();

        var metrics = V(10,
            MetricRow("", "CPU", m.Cpu.Total, Fmt.Pct(m.Cpu.Total), Theme.Load(m.Cpu.Total)),
            MetricRow("", L("内存", "Mem"), m.Memory.UsedFraction, Fmt.Pct(m.Memory.UsedFraction), m.Memory.PressureLevel >= 4 ? Theme.DangerC : Theme.MemC),
            MetricRow("", L("磁盘", "Disk"), m.Disk.UsedFraction, L("剩 ", "") + Fmt.DiskShort(m.Disk.Free) + L("", " free"), m.Disk.UsedFraction > 0.9 ? Theme.DangerC : Theme.DiskC),
            Row(H(10, Glyph("", 13), Fixed(T(L("网络", "Net"), 12.5), English ? 58 : 34), T("↓ " + Fmt.Rate(m.NetIn), 12.5, color: Theme.B(Theme.NetInC))),
                T("↑ " + Fmt.Rate(m.NetOut), 12.5, color: Theme.B(Theme.NetOutC))),
            Row(H(10, Glyph("", 13), Fixed(T(L("其他", "Other"), 12.5), English ? 58 : 34), T("GPU " + (m.Gpu is { } g ? Fmt.Pct(g) : "—"), 12.5)),
                T(m.Battery is { } b ? L($"电池 {Fmt.Pct(b.Percent)}{(b.Charging ? " ⚡" : "")}", $"Battery {Fmt.Pct(b.Percent)}{(b.Charging ? " ⚡" : "")}") : "", 12.5)));

        var apps = V(8, T(L("占用最高", "Top apps"), 11.5, FontWeights.Medium, Theme.Secondary));
        var top = m.TopByCpu.Take(3).ToList();
        if (top.Count == 0) apps.Children.Add(T(L("读取中…", "Loading…"), 12, color: Theme.Tertiary));
        foreach (var a in top)
            apps.Children.Add(Row(H(8, AppIcon(a.Path), T(a.Name, 12.5)),
                H(10, T(Fmt.Bytes(a.Memory), 12, color: Theme.Secondary),
                      Fixed(T(Fmt.Pct(a.Cpu), 12.5, FontWeights.Medium, a.Cpu > 0.8 ? Theme.B(Theme.DangerC) : Theme.Text), 40, right: true))));

        var openBtn = Btn(L("打开主窗口", "Open main window"), () => { Close(); MainWindow.Show(Page.Overview); }, primary: true);
        var cleanBtn = Btn(L("清理", "Clean"), () => { Close(); MainWindow.Show(Page.Cleanup); }, glyph: "");
        var gear = Btn("", () => { Close(); MainWindow.Show(Page.Settings); }, glyph: "");
        var power = Btn("", () => Application.Current.Shutdown(), glyph: "");
        var shot = Btn("", () => { Close(); Screenshot.Start(250); }, glyph: "\uE722");
        var footer = new Grid();
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        shot.Margin = new Thickness(6, 0, 0, 0); Grid.SetColumn(shot, 4); footer.Children.Add(shot);
        cleanBtn.Margin = new Thickness(6, 0, 0, 0); gear.Margin = new Thickness(6, 0, 0, 0); power.Margin = new Thickness(4, 0, 0, 0);
        Grid.SetColumn(cleanBtn, 1); Grid.SetColumn(gear, 2); Grid.SetColumn(power, 3);
        footer.Children.Add(openBtn); footer.Children.Add(cleanBtn); footer.Children.Add(gear); footer.Children.Add(power);

        _top.Content = V(12, header, Panel(metrics, 12, Theme.CardAlt), Panel(apps, 12, Theme.CardAlt));
        _bottom.Content = footer;
        // Collapsed elements take no space, margin included, so the gap goes with it.
        _snap.Visibility = WindowSnap.S.SnapInPopup ? Visibility.Visible : Visibility.Collapsed;
        UpdateSnapHint();
    }

    // ---- window layout buttons (Magnet's menu-bar dropdown)

    static readonly SnapAction[] SnapOrder =
    {
        SnapAction.LeftHalf, SnapAction.RightHalf, SnapAction.TopHalf, SnapAction.BottomHalf, SnapAction.Maximize, SnapAction.Center,
        SnapAction.TopLeft, SnapAction.TopRight, SnapAction.BottomLeft, SnapAction.BottomRight, SnapAction.LeftTwoThirds, SnapAction.RightTwoThirds,
        SnapAction.LeftThird, SnapAction.CenterThird, SnapAction.RightThird, SnapAction.Restore, SnapAction.PrevDisplay, SnapAction.NextDisplay,
    };

    FrameworkElement BuildSnap()
    {
        _snapHint.MaxWidth = 180;
        _snapHint.HorizontalAlignment = HorizontalAlignment.Right;
        var grid = new UniformGrid { Columns = 6, Rows = 3, Margin = new Thickness(-2) };
        foreach (var a in SnapOrder) grid.Children.Add(SnapButton(a));
        return Panel(V(8, Row(T(L("分屏", "Window layout"), 11.5, FontWeights.Medium, Theme.Secondary), _snapHint), grid), 12, Theme.CardAlt);
    }

    FrameworkElement SnapButton(SnapAction a)
    {
        var drawing = WindowSnapPage.Drawing(a, 31, 18);
        drawing.HorizontalAlignment = HorizontalAlignment.Center;
        var b = new Border
        {
            Margin = new Thickness(2), Padding = new Thickness(3, 2, 3, 2), CornerRadius = new CornerRadius(5),
            Background = Brushes.Transparent, BorderBrush = Brushes.Transparent, BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand, ToolTip = WindowSnap.Title(a), Child = drawing,
        };
        b.MouseEnter += (_, _) => { b.Background = Theme.AccentSoftBrush; b.BorderBrush = Theme.AccentBrush; _snapHover = a; UpdateSnapHint(); };
        b.MouseLeave += (_, _) => { b.Background = Brushes.Transparent; b.BorderBrush = Brushes.Transparent; if (_snapHover == a) _snapHover = null; UpdateSnapHint(); };
        b.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (WindowSnap.LastTarget == IntPtr.Zero)
            {
                _snapHint.Text = L("先点一下要排列的窗口", "Click a window to arrange first");
                _snapHint.Foreground = Theme.B(Theme.WarnC);
                return;
            }
            Close();
            WindowSnap.ApplyToLastAndFocus(a);
        };
        return b;
    }

    void UpdateSnapHint()
    {
        _snapHint.Foreground = Theme.Tertiary;
        if (_snapHover is { } a)
        {
            string name = WindowSnap.Title(a);
            if (WindowSnap.S.Enabled && WindowSnap.Hotkey(a) is { } k && !WindowSnap.InUse(a)) name += "  " + k.Title.Replace(" + ", "+");
            _snapHint.Text = name;
            return;
        }
        var app = WindowSnap.LastTargetApp;
        _snapHint.Text = app is null ? L("先点一下要排列的窗口", "Click a window to arrange first") : L($"应用到 {app}", $"Applies to {app}");
    }

    static FrameworkElement Fixed(FrameworkElement e, double width, bool right = false)
    {
        e.Width = width;
        if (e is TextBlock t && right) t.TextAlignment = TextAlignment.Right;
        return e;
    }

    static FrameworkElement WithBar(Grid row, double v, Color c)
    {
        // Insert a bar between the label and the value.
        row.ColumnDefinitions.Insert(1, new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions[0].Width = GridLength.Auto;
        foreach (UIElement child in row.Children) if (Grid.GetColumn(child) == 1) Grid.SetColumn(child, 2);
        var bar = Bar(v, c, 6);
        bar.VerticalAlignment = VerticalAlignment.Center;
        bar.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(bar, 1);
        row.Children.Add(bar);
        return row;
    }

    static readonly Dictionary<string, ImageSource?> IconCache = new(StringComparer.OrdinalIgnoreCase);

    public static FrameworkElement AppIcon(string? path, double size = 16)
    {
        ImageSource? src = null;
        if (path is not null && !IconCache.TryGetValue(path, out src))
        {
            try
            {
                using var ico = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (ico is not null)
                {
                    src = Imaging.CreateBitmapSourceFromHIcon(ico.Handle, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                    src.Freeze();
                }
            }
            catch { }
            IconCache[path] = src;
        }
        if (src is null) return new Border { Width = size, Height = size, CornerRadius = new CornerRadius(4), Background = Theme.Line };
        return new Image { Source = src, Width = size, Height = size };
    }
}
