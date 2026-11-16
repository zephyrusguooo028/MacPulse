using System.Windows;
using System.Windows.Controls;
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

    public static void Toggle()
    {
        if (_current is { IsVisible: true }) { _current.Close(); return; }
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
        Deactivated += (_, _) => Close();
        Loaded += (_, _) => { Place(); Native.RoundCorners(this); };
        SystemMonitor.Shared.WantProcesses(true);
        SystemMonitor.Shared.Ticked += Rebuild;
        TrayController.Shared.Viewer(true);
        TrayController.Shared.FrameChanged += AnimateIcon;
        Closed += (_, _) =>
        {
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
            T("已运行 " + Fmt.Uptime(), 11, color: Theme.Tertiary));

        FrameworkElement MetricRow(string glyph, string name, double v, string value, Color c) =>
            Row(H(10, Glyph(glyph, 13), Fixed(T(name, 12.5), 34)), Fixed(T(value, 12.5, FontWeights.SemiBold), 70, right: true)) is Grid g
                ? WithBar(g, v, c) : new Grid();

        var metrics = V(10,
            MetricRow("", "CPU", m.Cpu.Total, Fmt.Pct(m.Cpu.Total), Theme.Load(m.Cpu.Total)),
            MetricRow("", "内存", m.Memory.UsedFraction, Fmt.Pct(m.Memory.UsedFraction), m.Memory.PressureLevel >= 4 ? Theme.DangerC : Theme.MemC),
            MetricRow("", "磁盘", m.Disk.UsedFraction, "剩 " + Fmt.DiskShort(m.Disk.Free), m.Disk.UsedFraction > 0.9 ? Theme.DangerC : Theme.DiskC),
            Row(H(10, Glyph("", 13), Fixed(T("网络", 12.5), 34), T("↓ " + Fmt.Rate(m.NetIn), 12.5, color: Theme.B(Theme.NetInC))),
                T("↑ " + Fmt.Rate(m.NetOut), 12.5, color: Theme.B(Theme.NetOutC))),
            Row(H(10, Glyph("", 13), Fixed(T("其他", 12.5), 34), T("GPU " + (m.Gpu is { } g ? Fmt.Pct(g) : "—"), 12.5)),
                T(m.Battery is { } b ? $"电池 {Fmt.Pct(b.Percent)}{(b.Charging ? " ⚡" : "")}" : "", 12.5)));

        var apps = V(8, T("占用最高", 11.5, FontWeights.Medium, Theme.Secondary));
        var top = m.TopByCpu.Take(3).ToList();
        if (top.Count == 0) apps.Children.Add(T("读取中…", 12, color: Theme.Tertiary));
        foreach (var a in top)
            apps.Children.Add(Row(H(8, AppIcon(a.Path), T(a.Name, 12.5)),
                H(10, T(Fmt.Bytes(a.Memory), 12, color: Theme.Secondary),
                      Fixed(T(Fmt.Pct(a.Cpu), 12.5, FontWeights.Medium, a.Cpu > 0.8 ? Theme.B(Theme.DangerC) : Theme.Text), 40, right: true))));

        var openBtn = Btn("打开主窗口", () => { Close(); MainWindow.Show(Page.Overview); }, primary: true);
        var cleanBtn = Btn("清理", () => { Close(); MainWindow.Show(Page.Cleanup); }, glyph: "");
        var gear = Btn("", () => { Close(); MainWindow.Show(Page.Settings); }, glyph: "");
        var power = Btn("", () => Application.Current.Shutdown(), glyph: "");
        var footer = new Grid();
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        cleanBtn.Margin = new Thickness(6, 0, 0, 0); gear.Margin = new Thickness(6, 0, 0, 0); power.Margin = new Thickness(4, 0, 0, 0);
        Grid.SetColumn(cleanBtn, 1); Grid.SetColumn(gear, 2); Grid.SetColumn(power, 3);
        footer.Children.Add(openBtn); footer.Children.Add(cleanBtn); footer.Children.Add(gear); footer.Children.Add(power);

        var root = V(12, header, Panel(metrics, 12, Theme.CardAlt), Panel(apps, 12, Theme.CardAlt), footer);
        root.Margin = new Thickness(14);
        _host.Content = root;
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
