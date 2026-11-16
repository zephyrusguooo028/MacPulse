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
    readonly TextBlock _diskFooter = T("", 11, color: Theme.Secondary);
    readonly ContentControl _diskBar = new();

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
        var brand = H(8, new Image { Width = 22, Height = 22, Source = Icon }, T("Peekaboo", 16, FontWeights.SemiBold));
        brand.Margin = new Thickness(8, 0, 0, 16);
        DockPanel.SetDock(brand, Dock.Top);
        side.Children.Add(brand);
        var footer = Panel(V(6, H(6, Glyph("", 12), T("系统盘", 11.5, FontWeights.Medium)), _diskBar, _diskFooter), 10, Theme.CardAlt);
        DockPanel.SetDock(footer, Dock.Bottom);
        side.Children.Add(footer);
        side.Children.Add(_nav);
        foreach (var p in Enum.GetValues<Page>()) _nav.Children.Add(NavItem(p));
        root.Children.Add(side);

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

    Border NavItem(Page p)
    {
        var (title, glyph) = p switch
        {
            Page.Overview => ("概览", ""), Page.Disk => ("磁盘", ""), Page.Projects => ("项目", ""),
            Page.Processes => ("进程", ""), Page.Cleanup => ("清理", ""), Page.Typing => ("打字", "\uE765"), _ => ("设置", ""),
        };
        var b = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 8, 10, 8), Cursor = Cursors.Hand, Background = Brushes.Transparent,
                             Child = H(10, Glyph(glyph, 15), T(title, 13.5)) };
        if (p == Page.Settings) b.Margin = new Thickness(0, 14, 0, 0);
        b.MouseLeftButtonUp += (_, _) => Navigate(p);
        b.MouseEnter += (_, _) => { if (_current != p) b.Background = Theme.Hover; };
        b.MouseLeave += (_, _) => { if (_current != p) b.Background = Brushes.Transparent; };
        _navItems[p] = b;
        return b;
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
            _ => new SettingsPage(),
        };
        _page.Margin = new Thickness(24, 20, 24, 24);
        _content.Content = _page;
        _page.Shown();
    }

    void OnTick()
    {
        var d = SystemMonitor.Shared.Disk;
        _diskBar.Content = Bar(d.UsedFraction, d.UsedFraction > 0.9 ? Theme.DangerC : Theme.DiskC, 5);
        _diskFooter.Text = $"{d.Name} 可用 {Fmt.Bytes(d.Free)} / 共 {Fmt.Bytes(d.Total)}";
        if (IsVisible) _page?.Tick();
    }
}
