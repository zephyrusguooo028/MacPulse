using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using static Peekaboo.UIX;

namespace Peekaboo;

/// Base for pages: built when shown, refreshed on each monitor tick while visible.
public abstract class PageView : ContentControl
{
    public virtual void Shown() { }
    public virtual void Hidden() { }
    public virtual void Tick() { }
}

public sealed class MainWindow : Window
{
    static MainWindow? _instance;
    readonly ContentControl _content = new();
    readonly StackPanel _nav = new();
    readonly Dictionary<Page, Border> _navItems = new();
    PageView? _page;
    Page _current;
    readonly ContentControl _footer = new();
    readonly Dictionary<Page, Border> _badges = new();
    readonly TextBlock _status = T("", 10.5, color: Theme.Secondary);
    readonly Image _brandIcon = new() { Width = 20, Height = 20 };
    readonly Dictionary<string, System.Windows.Media.Imaging.BitmapSource> _brandFrames = new();

    public static void Show(Page page)
    {
        _instance ??= new MainWindow();
        _instance.Navigate(page);
        if (!_instance.IsVisible) _instance.Show();
        if (_instance.WindowState == WindowState.Minimized) _instance.WindowState = WindowState.Normal;
        _instance.Activate();
    }

    public static MainWindow? Instance => _instance;

    MainWindow()
    {
        Title = "Peekaboo";
        Width = 1120; Height = 760; MinWidth = 900; MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Theme.Window; FontFamily = Theme.Font; Foreground = Theme.Text;
        try { Icon = IconArt.ToWpf(IconArt.Render(TrayIcon.Ghost, 0, 0, System.Drawing.Color.FromArgb(158, 97, 242), 64)); } catch { }

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
        root.ColumnDefinitions.Add(new ColumnDefinition());

        var side = new DockPanel { Margin = new Thickness(10, 14, 10, 12) };
        RenderOptions.SetBitmapScalingMode(_brandIcon, BitmapScalingMode.NearestNeighbor);
        var brand = H(10,
            new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(9), Background = Theme.AccentSoftBrush, Child = _brandIcon },
            V(1, T("Peekaboo", 15, FontWeights.SemiBold), _status));
        brand.Margin = new Thickness(6, 0, 0, 14);
        DockPanel.SetDock(brand, Dock.Top);
        side.Children.Add(brand);
        DockPanel.SetDock(_footer, Dock.Bottom);
        side.Children.Add(_footer);
        side.Children.Add(_nav);
        void Group(string title, params Page[] pages)
        {
            if (title.Length > 0) { var h = T(title, 11, FontWeights.SemiBold, Theme.Tertiary); h.Margin = new Thickness(10, 10, 0, 4); _nav.Children.Add(h); }
            foreach (var p in pages) _nav.Children.Add(NavItem(p));
        }
        Group("监控", Page.Overview, Page.Processes);
        Group("空间", Page.Disk, Page.Projects, Page.Cleanup);
        Group("工具", Page.Screenshot, Page.Typing);
        var gap = new Border { Height = 10 }; _nav.Children.Add(gap);
        _nav.Children.Add(NavItem(Page.Settings));
        root.Children.Add(side);
        TrayController.Shared.FrameChanged += AnimateBrand;
        TrayController.Shared.Viewer(true);

        var scroll = new ScrollViewer { Content = _content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetColumn(scroll, 1);
        root.Children.Add(scroll);
        Content = root;

        SourceInitialized += (_, _) => Native.RoundCorners(this);
        SystemMonitor.Shared.Ticked += OnTick;
        Closing += (_, e) => { e.Cancel = true; Hide(); _page?.Hidden(); _page = null; };
        IsVisibleChanged += (_, _) => { if (IsVisible && _page is null) Navigate(_current); };
        OnTick();
    }

    static (string title, string glyph, Color tile) Meta(Page p) => p switch
    {
        Page.Overview => ("概览", "\uE9D9", Theme.CpuC),
        Page.Processes => ("进程", "\uE9F5", Color.FromRgb(92, 107, 140)),
        Page.Disk => ("磁盘", "\uEDA2", Theme.DiskC),
        Page.Projects => ("项目", "\uE8B7", Theme.NetOutC),
        Page.Cleanup => ("清理", "\uE74D", Color.FromRgb(237, 92, 140)),
        Page.Typing => ("打字", "\uE765", Theme.MemC),
        Page.Screenshot => ("截图", "\uE722", Color.FromRgb(64, 178, 217)),
        _ => ("设置", "\uE713", Color.FromRgb(140, 143, 153)),
    };

    Border NavItem(Page p)
    {
        var (title, glyph, tile) = Meta(p);
        var icon = new Border
        {
            Width = 24, Height = 24, CornerRadius = new CornerRadius(6),
            Background = new LinearGradientBrush(Color.FromArgb(255, (byte)Math.Min(255, tile.R + 25), (byte)Math.Min(255, tile.G + 25), (byte)Math.Min(255, tile.B + 25)), tile, 90),
            Child = new TextBlock { Text = glyph, FontFamily = Theme.IconFont, FontSize = 12, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        var badge = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 1, 6, 1), Visibility = Visibility.Collapsed, Child = T("", 10.5, FontWeights.Medium, Theme.Secondary) };
        _badges[p] = badge;
        var b = new Border { CornerRadius = new CornerRadius(7), Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 1, 0, 1), Cursor = Cursors.Hand, Background = Brushes.Transparent,
                             Child = Row(H(10, icon, T(title, 13.5)), badge) };
        b.MouseLeftButtonUp += (_, _) => Navigate(p);
        b.MouseEnter += (_, _) => { if (_current != p) b.Background = Theme.Hover; };
        b.MouseLeave += (_, _) => { if (_current != p) b.Background = Brushes.Transparent; };
        _navItems[p] = b;
        return b;
    }

    void AnimateBrand()
    {
        if (!IsVisible) return;
        var icon = Settings.Shared.Icon == TrayIcon.None ? TrayIcon.Ghost : Settings.Shared.Icon;
        int f = TrayController.Shared.Frame % IconArt.Frames(icon);
        var c = Theme.AccentColor;
        var key = $"{icon}{f}{c}";
        if (!_brandFrames.TryGetValue(key, out var src))
        {
            using var bmp = IconArt.Render(icon, f, SystemMonitor.Shared.Cpu.Total, System.Drawing.Color.FromArgb(c.R, c.G, c.B), 40);
            src = IconArt.ToWpf(bmp);
            _brandFrames[key] = src;
        }
        _brandIcon.Source = src;
    }

    void SetBadge(Page p, string? text, bool alert = false)
    {
        var b = _badges[p];
        if (text is null) { b.Visibility = Visibility.Collapsed; return; }
        b.Visibility = Visibility.Visible;
        b.Background = alert ? Theme.B(Theme.DangerC) : Theme.Line;
        var t = (TextBlock)b.Child; t.Text = text; t.Foreground = alert ? Brushes.White : Theme.Secondary;
    }

    public void Navigate(Page p)
    {
        _page?.Hidden();
        _current = p;
        foreach (var (k, b) in _navItems) b.Background = k == p ? Theme.AccentSoftBrush : Brushes.Transparent;
        _page = p switch
        {
            Page.Overview => new OverviewPage(),
            Page.Disk => new DiskPage(),
            Page.Projects => new ProjectsPage(),
            Page.Processes => new ProcessesPage(),
            Page.Cleanup => new CleanupPage(),
            Page.Typing => new TypingPage(),
            Page.Screenshot => new ScreenshotPage(),
            _ => new SettingsPage(),
        };
        _page.Margin = new Thickness(24, 20, 24, 24);
        _content.Content = _page;
        _page.Shown();
    }

    void OnTick()
    {
        var m = SystemMonitor.Shared; var st = Settings.Shared;
        _status.Text = m.StatusSentence();
        SetBadge(Page.Overview, Fmt.Pct(m.Cpu.Total), m.Cpu.Total > 0.9);
        SetBadge(Page.Processes, Fmt.Pct(m.Memory.UsedFraction), m.Memory.PressureLevel >= 4);
        SetBadge(Page.Disk, "剩 " + Fmt.DiskShort(m.Disk.Free), m.Disk.Total > 0 && m.Disk.Free < st.AlertDiskGB * 1073741824L);
        SetBadge(Page.Projects, ProjectModel.Shared.Projects.Count > 0 ? ProjectModel.Shared.Projects.Count.ToString() : null);
        long rec = CleanupModel.Shared.All.Where(i => i.Recommended).Sum(i => i.Size);
        SetBadge(Page.Cleanup, rec > 0 ? Fmt.Bytes(rec) : null);
        SetBadge(Page.Typing, TypingMonitor.Shared.Running ? TypingMonitor.Shared.Today.Total.ToString("N0") : null);
        SetBadge(Page.Screenshot, st.ShotHotkey == ShotHotkey.None ? null : GlobalHotkey.Title(st.ShotHotkey).Replace(" ", ""));
        SetBadge(Page.Settings, null);

        FrameworkElement Meter(string glyph, string title, double v, string value, Color c) =>
            V(4, Row(H(5, Glyph(glyph, 11), T(title, 11, FontWeights.Medium)), T(value, 11, color: Theme.Secondary)), Bar(v, c, 5));
        _footer.Content = Panel(V(8,
            Meter("\uEDA2", "磁盘 " + m.Disk.Name, m.Disk.UsedFraction, Fmt.DiskShort(m.Disk.Free) + " 可用", m.Disk.UsedFraction > 0.9 ? Theme.DangerC : Theme.DiskC),
            Meter("\uE964", "内存", m.Memory.UsedFraction, Fmt.Pct(m.Memory.UsedFraction), m.Memory.PressureLevel >= 4 ? Theme.DangerC : Theme.MemC)), 10, Theme.CardAlt);
        if (IsVisible) _page?.Tick();
    }
}
