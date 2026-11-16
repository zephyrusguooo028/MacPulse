using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using static Peekaboo.UIX;

namespace Peekaboo;

public sealed class OverviewPage : PageView
{
    string _historyMetric = "CPU";

    public override void Shown() => Tick();

    public override void Tick()
    {
        var m = SystemMonitor.Shared;
        var tiles = new UniformGrid4();
        tiles.Add(Tile("CPU", "", H(14, Ring(m.Cpu.Total, Theme.Load(m.Cpu.Total), Fmt.Pct(m.Cpu.Total), 52, 5),
            Lines(("用户", Fmt.Pct(m.Cpu.User, 1)), ("系统", Fmt.Pct(m.Cpu.System, 1)), ("核心", $"{m.Cpu.PerCore.Length} 个")))));
        var memC = m.Memory.PressureLevel >= 4 ? Theme.DangerC : m.Memory.PressureLevel >= 2 ? Theme.WarnC : Theme.MemC;
        tiles.Add(Tile("内存", "", H(14, Ring(m.Memory.UsedFraction, memC, Fmt.Pct(m.Memory.UsedFraction), 52, 5),
            Lines(("已用", Fmt.Bytes(m.Memory.Used)), ("压力", m.Memory.PressureText), ("提交", Fmt.Bytes(m.Memory.CommitUsed))))));
        var diskLink = T("分析磁盘 →", 12.5, color: Theme.AccentBrush); diskLink.Cursor = System.Windows.Input.Cursors.Hand;
        diskLink.MouseLeftButtonUp += (_, _) => MainWindow.Instance?.Navigate(Page.Disk);
        tiles.Add(Tile("磁盘 " + m.Disk.Name, "", H(14, Ring(m.Disk.UsedFraction, m.Disk.UsedFraction > 0.9 ? Theme.DangerC : Theme.DiskC, Fmt.Pct(m.Disk.UsedFraction), 52, 5, caption: "已用"),
            V(6, Lines(("可用", Fmt.Bytes(m.Disk.Free)), ("已用", Fmt.Bytes(m.Disk.Used))), diskLink))));
        var recent = m.Samples.TakeLast(60).ToList();
        double netMax = Math.Max(1, recent.Select(s => Math.Max(s.NetIn, s.NetOut)).DefaultIfEmpty(1).Max());
        var netChart = new Grid { Height = 44 };
        netChart.Children.Add(Spark(recent.Select(s => s.NetIn).ToList(), Theme.NetInC, 44, netMax));
        netChart.Children.Add(Spark(recent.Select(s => s.NetOut).ToList(), Theme.NetOutC, 44, netMax));
        tiles.Add(Tile("网络", "", V(8, Row(T("↓ " + Fmt.Rate(m.NetIn), 13, FontWeights.Medium, Theme.B(Theme.NetInC)), T("↑ " + Fmt.Rate(m.NetOut), 13, FontWeights.Medium, Theme.B(Theme.NetOutC))), netChart)));

        var live = Card("实时走势", "", Chart(new List<(IList<double>, Color, bool)>
            {
                (m.Samples.Select(s => s.Cpu * 100).ToList(), Theme.CpuC, true),
                (m.Samples.Select(s => s.Mem * 100).ToList(), Theme.MemC, false),
            }, 190, 100, v => $"{v:0}%", m.Samples.Select(s => s.T).ToList(), t => t.ToString("HH:mm:ss")),
            Legend(("CPU", Theme.CpuC, Fmt.Pct(m.Cpu.Total)), ("内存", Theme.MemC, Fmt.Pct(m.Memory.UsedFraction))));

        var cores = new UniformGrid { Rows = 1, Height = 190 };
        foreach (var v in m.Cpu.PerCore)
        {
            var col = new Grid { Margin = new Thickness(2, 0, 2, 0) };
            col.Children.Add(new Border { Background = Theme.Line, CornerRadius = new CornerRadius(3) });
            col.Children.Add(new Border { Background = Theme.B(Theme.Load(v)), CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Bottom, Height = Math.Max(2, 190 * v) });
            cores.Children.Add(col);
        }
        var coreCard = Card("CPU 核心", "", cores, T($"{m.Cpu.PerCore.Length} 个逻辑核心", 12, color: Theme.Secondary));
        coreCard.Width = 330;
        var mid = new DockPanel();
        DockPanel.SetDock(coreCard, Dock.Right); coreCard.Margin = new Thickness(14, 0, 0, 0);
        mid.Children.Add(coreCard); mid.Children.Add(live);

        var picker = H(4);
        foreach (var k in new[] { "CPU", "内存", "网络" })
        {
            var key = k;
            var b = Btn(k, () => { _historyMetric = key; Tick(); }, primary: _historyMetric == k);
            b.Padding = new Thickness(12, 3, 12, 3);
            picker.Children.Add(b);
        }
        var hist = m.History;
        FrameworkElement histBody = hist.Count < 2
            ? Empty("", "正在记录", "Peekaboo 每分钟记一个点，保留 24 小时。运行一会儿就能看到曲线。")
            : _historyMetric switch
            {
                "内存" => Chart(new List<(IList<double>, Color, bool)> { (hist.Select(p => p.Mem * 100).ToList(), Theme.MemC, true) }, 200, 100, v => $"{v:0}%", hist.Select(p => p.T).ToList(), t => t.ToString("HH:mm")),
                "网络" => NetHistory(hist),
                _ => Chart(new List<(IList<double>, Color, bool)> { (hist.Select(p => p.CpuMax * 100).ToList(), Color.FromArgb(90, Theme.CpuC.R, Theme.CpuC.G, Theme.CpuC.B), false), (hist.Select(p => p.Cpu * 100).ToList(), Theme.CpuC, true) }, 200, 100, v => $"{v:0}%", hist.Select(p => p.T).ToList(), t => t.ToString("HH:mm")),
            };
        var history = Card("过去 24 小时", "", histBody, picker);

        var mem = m.Memory;
        var memCard = Card("内存构成", "", V(10,
            Stacked(new List<(string, double, Color)>
            {
                ("已用", mem.Used, Theme.MemC), ("系统缓存", mem.Cached, Color.FromArgb(150, Theme.NetInC.R, Theme.NetInC.G, Theme.NetInC.B)),
                ("可用", Math.Max(0, (double)mem.Available - mem.Cached), Color.FromArgb(40, 128, 128, 128)),
            }, mem.Total, 16),
            WrapT("“系统缓存”是 Windows 为加快速度留着的文件缓存，需要时会自动让出。")));
        var dev = Card("设备", "", Lines(
            ("GPU", m.Gpu is { } g ? Fmt.Pct(g) : "—"),
            ("电池", m.Battery is { } bat ? $"{Fmt.Pct(bat.Percent)} · {(bat.Charging ? "充电中" : bat.OnAC ? "已接电源" : bat.MinutesLeft is { } ml ? $"剩约 {ml / 60} 小时 {ml % 60} 分" : "使用电池")}" : "外接电源"),
            ("已运行", Fmt.Uptime()),
            ("系统", Environment.OSVersion.VersionString),
            ("处理器", $"{Environment.ProcessorCount} 个逻辑核心")));
        dev.Width = 330;
        var bottom = new DockPanel();
        DockPanel.SetDock(dev, Dock.Right); dev.Margin = new Thickness(14, 0, 0, 0);
        bottom.Children.Add(dev); bottom.Children.Add(memCard);

        Content = V(16,
            PageHeader("概览", $"{Environment.MachineName} · {Fmt.Bytes(mem.Total)} 内存 · 已运行 {Fmt.Uptime()}"),
            tiles, mid, history, bottom);
    }

    static FrameworkElement NetHistory(List<MinutePoint> hist)
    {
        double max = Math.Max(1024, hist.Max(p => Math.Max(p.NetIn, p.NetOut)));
        return Chart(new List<(IList<double>, Color, bool)> { (hist.Select(p => p.NetIn).ToList(), Theme.NetInC, false), (hist.Select(p => p.NetOut).ToList(), Theme.NetOutC, false) },
                     200, max, v => Fmt.Short(v), hist.Select(p => p.T).ToList(), t => t.ToString("HH:mm"));
    }

    static Border Tile(string title, string glyph, UIElement body) => Card(title, glyph, body);

    static FrameworkElement Lines(params (string label, string value)[] rows)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int i = 0; i < rows.Length; i++)
        {
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = T(rows[i].label, 11.5, color: Theme.Secondary); l.Margin = new Thickness(0, i == 0 ? 0 : 5, 6, 0);
            var v = T(rows[i].value, 11.5, FontWeights.Medium); v.Margin = new Thickness(0, i == 0 ? 0 : 5, 0, 0); v.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetRow(l, i); Grid.SetRow(v, i); Grid.SetColumn(v, 1);
            g.Children.Add(l); g.Children.Add(v);
        }
        return g;
    }

    /// Ring on the left, details filling the rest of the tile.
    static FrameworkElement RingAnd(FrameworkElement ring, FrameworkElement details)
    {
        var d = new DockPanel();
        DockPanel.SetDock(ring, Dock.Left);
        ring.Margin = new Thickness(0, 0, 8, 0);
        details.VerticalAlignment = VerticalAlignment.Center;
        d.Children.Add(ring); d.Children.Add(details);
        return d;
    }

    static TextBlock WrapT(string text) { var t = T(text, 12, color: Theme.Secondary); t.TextWrapping = TextWrapping.Wrap; t.TextTrimming = TextTrimming.None; return t; }

    static FrameworkElement Legend(params (string label, Color c, string value)[] items)
    {
        var s = H(12);
        foreach (var (l, c, v) in items)
            s.Children.Add(H(5, new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = Theme.B(c) }, T(l, 12, color: Theme.Secondary), T(v, 12)));
        return s;
    }
}

/// Four equal columns with spacing.
public sealed class UniformGrid4 : Grid
{
    public UniformGrid4() { for (int i = 0; i < 4; i++) ColumnDefinitions.Add(new ColumnDefinition()); }
    public void Add(FrameworkElement e)
    {
        int i = Children.Count;
        Grid.SetColumn(e, i);
        e.Margin = new Thickness(i == 0 ? 0 : 7, 0, i == 3 ? 0 : 7, 0);
        Children.Add(e);
    }
}
