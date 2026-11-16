using System.Management;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using static Peekaboo.UIX;

namespace Peekaboo;

public sealed record BatteryPoint(DateTime T, double P, bool C);

/// Battery % every 5 minutes, kept for 7 days in battery-history.json.
public static class BatteryHistory
{
    static readonly TimeSpan Step = TimeSpan.FromMinutes(5);
    static readonly TimeSpan Keep = TimeSpan.FromDays(7);
    static string FilePath => Path.Combine(Paths.Support, "battery-history.json");
    static List<BatteryPoint>? _points;
    static DispatcherTimer? _timer;

    public static List<BatteryPoint> Points
    {
        get
        {
            if (_points is null)
            {
                _points = new();
                try
                {
                    if (File.Exists(FilePath))
                        _points.AddRange((JsonSerializer.Deserialize<List<BatteryPoint>>(File.ReadAllText(FilePath)) ?? new())
                            .Where(p => p.T > DateTime.Now - Keep).OrderBy(p => p.T));
                }
                catch { }
            }
            return _points;
        }
    }

    /// Runs in the background from launch; one GetSystemPowerStatus call every 5 minutes.
    public static void Start()
    {
        if (_timer is not null) return;
        Record();
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = Step };
        _timer.Tick += (_, _) => Record();
        _timer.Start();
    }

    static void Record()
    {
        var b = BatteryReader.Read();
        if (b is null) return;
        var now = DateTime.Now;
        var pts = Points;
        if (pts.Count > 0 && now - pts[^1].T < Step - TimeSpan.FromSeconds(30)) return;
        pts.Add(new BatteryPoint(now, b.Percent, b.Charging || b.OnAC));
        pts.RemoveAll(p => p.T < now - Keep);
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(pts)); } catch { }
    }
}

/// Battery details from WMI (root\wmi). Every class is optional: many drivers omit some.
internal sealed class BatteryWmi
{
    public long? DesignCapacity, FullChargeCapacity, RemainingCapacity;   // mWh
    public int? CycleCount;
    public double? ChargeRate, DischargeRate;                            // mW
    public double? Voltage;                                              // mV
    public string? Name, Maker;

    public double? Health => DesignCapacity is > 0 && FullChargeCapacity is > 0 ? (double)FullChargeCapacity / DesignCapacity : null;

    /// Slow (tens to hundreds of ms); call off the UI thread.
    public static BatteryWmi Read()
    {
        var r = new BatteryWmi();
        Query("BatteryStaticData", o =>
        {
            r.DesignCapacity = Long(o, "DesignedCapacity");
            r.Name = Str(o, "DeviceName");
            r.Maker = Str(o, "ManufactureName");
        });
        Query("BatteryFullChargedCapacity", o => r.FullChargeCapacity = Long(o, "FullChargedCapacity"));
        Query("BatteryCycleCount", o => r.CycleCount = (int?)Long(o, "CycleCount"));
        Query("BatteryStatus", o =>
        {
            r.ChargeRate = Long(o, "ChargeRate");
            r.DischargeRate = Long(o, "DischargeRate");
            r.Voltage = Long(o, "Voltage");
            r.RemainingCapacity = Long(o, "RemainingCapacity");
        });
        return r;
    }

    static void Query(string cls, Action<ManagementBaseObject> read)
    {
        try
        {
            using var s = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM " + cls);
            using var results = s.Get();
            foreach (ManagementBaseObject o in results)
            {
                using (o) { try { read(o); } catch { } }
                break; // first battery only
            }
        }
        catch { }
    }

    static long? Long(ManagementBaseObject o, string name)
    {
        try
        {
            var v = o[name];
            if (v is null) return null;
            long x = Convert.ToInt64(v);
            return x > 0 && x < int.MaxValue ? x : null;   // drivers report 0 or 0xFFFFFFFF for "unknown"
        }
        catch { return null; }
    }

    static string? Str(ManagementBaseObject o, string name)
    {
        try { return (o[name] as string)?.Trim() is { Length: > 0 } s ? s : null; } catch { return null; }
    }
}

public sealed class BatteryPage : PageView
{
    public static string? Badge => SystemMonitor.Shared.Battery is { } b ? Fmt.Pct(b.Percent) : null;

    BatteryWmi? _wmi;
    DateTime _wmiAt = DateTime.MinValue;
    bool _visible, _loading, _week;

    public override void Shown()
    {
        _visible = true;
        SystemMonitor.Shared.WantProcesses(true);
        Fill();
    }

    public override void Hidden()
    {
        _visible = false;
        SystemMonitor.Shared.WantProcesses(false);
    }

    public override void Tick() => Fill();

    void RefreshWmi()
    {
        if (_loading || (DateTime.Now - _wmiAt).TotalSeconds < 30) return;
        _loading = true;
        var d = Dispatcher;
        Task.Run(BatteryWmi.Read).ContinueWith(t =>
        {
            d.BeginInvoke(() =>
            {
                _loading = false;
                _wmiAt = DateTime.Now;
                if (t.Status == TaskStatus.RanToCompletion) _wmi = t.Result;
                if (_visible) Fill();
            });
        });
    }

    void Fill()
    {
        var m = SystemMonitor.Shared;
        var b = m.Battery ?? BatteryReader.Read();
        if (b is null)
        {
            Native.GetSystemPowerStatus(out var ps);
            string power = ps.ACLineStatus == 1 ? L("外接电源已连接", "Mains power connected") : ps.ACLineStatus == 0 ? L("未接电源", "Not on mains power") : L("未知", "Unknown");
            Content = V(16,
                PageHeader(L("电池", "Battery"), L("台式机 · 外接电源", "Desktop · mains power")),
                Card(L("没有电池", "No battery"), "", V(10,
                    Empty("", L("这台电脑一直插着电", "This PC runs on mains power"),
                          L("台式机没有电池，所以没有电量、健康度和充电记录。", "Desktop PCs have no battery, so there is no charge, health or charging history to show.")),
                    Line(L("电源", "Power"), power))));
            return;
        }
        RefreshWmi();
        var w = _wmi;

        string status = b.Charging ? L("充电中", "Charging") : b.OnAC ? (b.Percent >= 0.99 ? L("已充满", "Fully charged") : L("已接电源，未充电", "Plugged in, not charging")) : L("使用电池", "On battery");
        var chargeC = b.Percent < 0.2 && !b.OnAC ? Theme.DangerC : b.Percent < 0.35 ? Theme.WarnC : Theme.DiskC;
        var charge = Card(L("电量", "Charge"), "", RingAnd(Ring(b.Percent, chargeC, Fmt.Pct(b.Percent)), V(6,
            Line(L("状态", "Status"), status),
            Line(L("剩余", "Time left"), !b.OnAC && b.MinutesLeft is { } ml ? Duration(ml) : "—"),
            Line(L("当前容量", "Current charge"), w?.RemainingCapacity is { } rc ? $"{rc:N0} mWh" : "—"))));

        var h = w?.Health;
        var healthC = (h ?? 1) < 0.8 ? Theme.WarnC : Theme.DiskC;
        var health = Card(L("健康度", "Health"), "", RingAnd(Ring(Math.Min(h ?? 0, 1), healthC, h is { } hv ? Fmt.Pct(Math.Min(hv, 1)) : "—"), V(6,
            Line(L("循环次数", "Cycle count"), w?.CycleCount?.ToString() ?? "—"),
            Line(L("设计容量", "Design capacity"), w?.DesignCapacity is { } dc ? $"{dc:N0} mWh" : "—"),
            Line(L("满电容量", "Full charge capacity"), w?.FullChargeCapacity is { } fc ? $"{fc:N0} mWh" : "—"))));

        string rate = w?.ChargeRate is { } cr && b.Charging ? $"+{cr / 1000:0.0} W" : w?.DischargeRate is { } dr && !b.OnAC ? $"{dr / 1000:0.0} W" : "—";
        var powerLines = V(6,
            Line(L("电压", "Voltage"), w?.Voltage is { } v ? $"{v / 1000:0.00} V" : "—"),
            Line(b.Charging ? L("充入功率", "Charging power") : L("耗电功率", "Draw"), rate),
            Line(L("电源适配器", "Power adapter"), b.OnAC ? L("已连接", "Connected") : L("未连接", "Not connected")),
            Line(L("电池型号", "Battery"), string.Join(" · ", new[] { w?.Maker, w?.Name }.Where(s => s is not null)) is { Length: > 0 } nm ? nm : "—"));
        if (_loading && w is null) powerLines.Children.Add(T(L("正在读取电池详情…", "Reading battery details…"), 12, color: Theme.Tertiary));
        var power2 = Card(L("电源", "Power"), "", powerLines);

        var top = new Grid();
        for (int i = 0; i < 3; i++) top.ColumnDefinitions.Add(new ColumnDefinition());
        foreach (var (card, i) in new[] { (charge, 0), (health, 1), (power2, 2) })
        {
            Grid.SetColumn(card, i);
            card.Margin = new Thickness(i == 0 ? 0 : 7, 0, i == 2 ? 0 : 7, 0);
            top.Children.Add(card);
        }

        Content = V(16,
            PageHeader(L("电池", "Battery"), $"{Fmt.Pct(b.Percent)} · {status}"),
            top,
            HistoryCard(),
            HogsCard(b.OnAC));
    }

    static string Duration(int min) => L($"{min / 60} 小时 {min % 60} 分", $"{min / 60}h {min % 60}m");

    static FrameworkElement Line(string label, string value)
        => Row(T(label, 12.5, color: Theme.Secondary), T(value, 12.5));

    static FrameworkElement RingAnd(FrameworkElement ring, FrameworkElement lines)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        lines.Margin = new Thickness(14, 0, 0, 0);
        if (lines is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(lines, 1);
        g.Children.Add(ring); g.Children.Add(lines);
        return g;
    }

    Border HistoryCard()
    {
        var picker = H(4);
        foreach (var (label, week) in new[] { (L("24 小时", "24 hours"), false), (L("7 天", "7 days"), true) })
        {
            var wk = week;
            var btn = Btn(label, () => { _week = wk; Fill(); }, primary: _week == week);
            btn.Padding = new Thickness(12, 3, 12, 3);
            picker.Children.Add(btn);
        }
        var cutoff = DateTime.Now - (_week ? TimeSpan.FromDays(7) : TimeSpan.FromHours(24));
        var pts = BatteryHistory.Points.Where(p => p.T >= cutoff).ToList();
        FrameworkElement body;
        if (pts.Count < 2)
        {
            body = Empty("", L("正在记录", "Recording"), L("Peekaboo 每 5 分钟记一次电量，保留 7 天。运行一会儿就能看到曲线。", "Peekaboo records the charge every 5 minutes and keeps 7 days. Leave it running for a while to see the chart."));
        }
        else
        {
            var accent = Theme.AccentColor;
            // Charging stretches drawn as a faint band at the bottom so they read as "plugged in".
            var plugged = pts.Select(p => p.C ? 100.0 : 0).ToList();
            var chart = Chart(new List<(IList<double>, Color, bool)>
            {
                (plugged, Color.FromArgb(0, Theme.DiskC.R, Theme.DiskC.G, Theme.DiskC.B), true),
                (pts.Select(p => p.P * 100).ToList(), accent, true),
            }, 200, 100, v => $"{v:0}%", pts.Select(p => p.T).ToList(), t => _week ? t.ToString("ddd d") : t.ToString("HH:mm"));
            var legend = H(12,
                H(5, new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = Theme.B(accent) }, T(L("电量", "Charge"), 12, color: Theme.Secondary), T(Fmt.Pct(pts[^1].P), 12)),
                H(5, new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(2), Background = Theme.B(Theme.DiskC, 0.3) }, T(L("接着电源", "On power"), 12, color: Theme.Secondary)));
            body = V(8, chart, legend);
        }
        return Card(L("电量记录", "Charge history"), "", body, picker);
    }

    static Border HogsCard(bool onAC)
    {
        var m = SystemMonitor.Shared;
        var list = m.Groups
            .Select(g => (g, cpu: m.CpuAverage.TryGetValue(g.Id, out var a) ? a : g.Cpu))
            .Where(x => x.cpu > 0.005)
            .OrderByDescending(x => x.cpu)
            .Take(6).ToList();
        var s = V(8);
        if (list.Count == 0) s.Children.Add(T(L("现在没有明显耗电的应用。", "No app is using much energy right now."), 13, color: Theme.Secondary));
        double top = Math.Max(list.Count > 0 ? list[0].cpu : 1, 0.01);
        foreach (var (g, cpu) in list)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            var name = H(8, PopupWindow.AppIcon(g.Path, 18), T(g.Name, 13));
            var bar = Bar(cpu / top, cpu > 0.8 ? Theme.DangerC : cpu > 0.3 ? Theme.WarnC : Theme.AccentColor, 5);
            bar.VerticalAlignment = VerticalAlignment.Center;
            var pct = T((cpu * 100).ToString("0.0") + "%", 12.5); pct.HorizontalAlignment = HorizontalAlignment.Right; pct.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(bar, 1); Grid.SetColumn(pct, 2);
            row.Children.Add(name); row.Children.Add(bar); row.Children.Add(pct);
            s.Children.Add(row);
        }
        var note = T(onAC
            ? L("拔掉电源后，这些应用最可能耗电。按最近一段时间的平均 CPU 估算。", "Once unplugged, these apps are the most likely to drain the battery. Estimated from recent average CPU.")
            : L("按最近一段时间的平均 CPU 排序，CPU 越高通常越耗电。这是估算，不含屏幕、GPU 和网络的耗电。", "Sorted by recent average CPU; higher CPU usually means more battery drain. This is an estimate and leaves out display, GPU and network power."),
            12, color: Theme.Secondary);
        note.TextWrapping = TextWrapping.Wrap; note.TextTrimming = TextTrimming.None;
        s.Children.Add(note);
        return Card(L("耗电大户（估算）", "Energy hogs (estimate)"), "", s,
            T(onAC ? L("当前接着电源", "Plugged in") : L("正在使用电池", "On battery"), 12, color: Theme.Secondary));
    }
}
