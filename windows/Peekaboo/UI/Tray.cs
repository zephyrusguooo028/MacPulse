using System.Windows.Threading;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace Peekaboo;

/// Tray icons: the animated icon (click to open the popup) plus one small icon per
/// ticked metric showing its value. Windows trays cannot show text, so numbers are drawn.
public sealed class TrayController : IDisposable
{
    public static TrayController Shared { get; } = new();

    WF.NotifyIcon? _main;
    readonly Dictionary<TrayMetric, WF.NotifyIcon> _metricIcons = new();
    readonly Dictionary<string, SD.Icon> _frameCache = new();
    readonly Dictionary<TrayMetric, (string key, SD.Icon icon)> _metricCache = new();
    DispatcherTimer? _anim;
    public int Frame { get; private set; }
    public event Action? FrameChanged;
    int _viewers;

    public void Install()
    {
        _main = new WF.NotifyIcon { Visible = true, Text = "Peekaboo" };
        _main.MouseClick += (_, e) => { if (e.Button == WF.MouseButtons.Left) PopupWindow.Toggle(); };
        _main.ContextMenuStrip = BuildMenu();
        _main.BalloonTipClicked += (_, _) => MainWindow.Show(_pendingBalloonTab);
        Settings.Changed += () => { foreach (var i in _frameCache.Values) i.Dispose(); _frameCache.Clear(); Refresh(); };
        SystemMonitor.Shared.Ticked += Refresh;
        _anim = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
        _anim.Tick += (_, _) => Step();
        _anim.Start();
        Refresh();
    }

    WF.ContextMenuStrip BuildMenu()
    {
        var m = new WF.ContextMenuStrip();
        m.Items.Add("打开 Peekaboo", null, (_, _) => MainWindow.Show(Page.Overview));
        m.Items.Add("清理…", null, (_, _) => MainWindow.Show(Page.Cleanup));
        m.Items.Add("设置", null, (_, _) => MainWindow.Show(Page.Settings));
        m.Items.Add(new WF.ToolStripSeparator());
        m.Items.Add("退出", null, (_, _) => System.Windows.Application.Current.Shutdown());
        return m;
    }

    public void Viewer(bool on) => _viewers = Math.Max(0, _viewers + (on ? 1 : -1));

    void Step()
    {
        var s = Settings.Shared;
        double cpu = SystemMonitor.Shared.Cpu.Total;
        // 0% CPU → 5 fps, 100% → ~14 fps. Idle slowly when nothing animates.
        _anim!.Interval = TimeSpan.FromMilliseconds(s.Icon == TrayIcon.None && _viewers == 0 ? 1000 : Math.Max(70, 220 - 150 * Math.Min(cpu, 1)));
        if (s.Icon == TrayIcon.None && _viewers == 0) return;
        Frame++;
        UpdateMainIcon();
        FrameChanged?.Invoke();
    }

    SD.Color Ink => Settings.TaskbarIsLight ? SD.Color.FromArgb(20, 20, 20) : SD.Color.White;

    void UpdateMainIcon()
    {
        if (_main is null) return;
        var s = Settings.Shared;
        var m = SystemMonitor.Shared;
        var icon = s.Icon == TrayIcon.None ? TrayIcon.Pulse : s.Icon;
        int frame = s.Icon == TrayIcon.None ? 0 : Frame % IconArt.Frames(icon);
        int bucket = s.IconTint ? (m.Cpu.Total > 0.85 ? 2 : m.Cpu.Total > 0.6 ? 1 : 0) : -1;
        int bars = icon == TrayIcon.Bars ? (int)(m.Cpu.Total * 4) : 0;
        string key = $"{icon}-{frame}-{bucket}-{bars}-{Settings.TaskbarIsLight}";
        if (!_frameCache.TryGetValue(key, out var ico))
        {
            using var bmp = IconArt.Render(icon, frame, m.Cpu.Total, s.IconTint ? IconArt.Tint(m.Cpu.Total) : Ink);
            ico = ToIcon(bmp);
            _frameCache[key] = ico;
        }
        _main.Icon = ico;
    }

    static SD.Icon ToIcon(SD.Bitmap bmp)
    {
        IntPtr h = bmp.GetHicon();
        using var tmp = SD.Icon.FromHandle(h);
        var owned = (SD.Icon)tmp.Clone();
        Native.DestroyIcon(h);
        return owned;
    }

    public void Refresh()
    {
        if (_main is null) return;
        var m = SystemMonitor.Shared;
        var s = Settings.Shared;
        UpdateMainIcon();
        var tip = $"Peekaboo · CPU {Fmt.Pct(m.Cpu.Total)} · 内存 {Fmt.Pct(m.Memory.UsedFraction)}\n{m.Disk.Name} 剩 {Fmt.Bytes(m.Disk.Free)} · ↓{Fmt.Short(m.NetIn)} ↑{Fmt.Short(m.NetOut)}";
        _main.Text = tip.Length > 127 ? tip[..127] : tip;

        foreach (var metric in Enum.GetValues<TrayMetric>())
        {
            bool want = s.Shows(metric);
            if (!want)
            {
                if (_metricIcons.Remove(metric, out var old)) { old.Visible = false; old.Dispose(); }
                continue;
            }
            if (!_metricIcons.TryGetValue(metric, out var ni))
            {
                ni = new WF.NotifyIcon { Visible = true };
                ni.MouseClick += (_, e) => { if (e.Button == WF.MouseButtons.Left) PopupWindow.Toggle(); };
                ni.ContextMenuStrip = BuildMenu();
                _metricIcons[metric] = ni;
            }
            var (text, accent, label) = MetricFace(metric, m);
            string key = metric == TrayMetric.CpuGraph ? string.Join(",", m.Samples.TakeLast(8).Select(x => (int)(x.Cpu * 20))) : text;
            key += Settings.TaskbarIsLight;
            if (!_metricCache.TryGetValue(metric, out var cached) || cached.key != key)
            {
                using var bmp = metric == TrayMetric.CpuGraph
                    ? IconArt.Graph(m.Samples.TakeLast(8).Select(x => x.Cpu).ToList(), Ink)
                    : IconArt.Number(text, Ink, accent);
                var ico = ToIcon(bmp);
                ni.Icon = ico;
                cached.icon?.Dispose();
                _metricCache[metric] = (key, ico);
            }
            ni.Text = label;
        }
    }

    static (string text, SD.Color accent, string label) MetricFace(TrayMetric metric, SystemMonitor m)
    {
        SD.Color C(System.Windows.Media.Color c) => SD.Color.FromArgb(c.R, c.G, c.B);
        return metric switch
        {
            TrayMetric.Cpu => (Math.Min(99, (int)Math.Round(m.Cpu.Total * 100)).ToString(), C(Theme.CpuC), $"CPU {Fmt.Pct(m.Cpu.Total)}"),
            TrayMetric.Memory => (Math.Min(99, (int)Math.Round(m.Memory.UsedFraction * 100)).ToString(), C(Theme.MemC), $"内存 {Fmt.Pct(m.Memory.UsedFraction)}（{Fmt.Bytes(m.Memory.Used)} / {Fmt.Bytes(m.Memory.Total)}）"),
            TrayMetric.Disk => (((int)(m.Disk.Free / 1073741824.0)).ToString(), C(Theme.DiskC), $"{m.Disk.Name} 剩余 {Fmt.Bytes(m.Disk.Free)}"),
            TrayMetric.Network => (Fmt.Short(m.NetIn).Replace("K", "k"), C(Theme.NetInC), $"下载 {Fmt.Rate(m.NetIn)} · 上传 {Fmt.Rate(m.NetOut)}"),
            TrayMetric.Gpu => (m.Gpu is { } g ? Math.Min(99, (int)Math.Round(g * 100)).ToString() : "–", C(Theme.WarnC), $"GPU {(m.Gpu is { } g2 ? Fmt.Pct(g2) : "读不到")}"),
            TrayMetric.Battery => (m.Battery is { } b ? Math.Min(99, (int)Math.Round(b.Percent * 100)).ToString() : "–", SD.Color.FromArgb(120, 200, 80), m.Battery is { } b2 ? $"电池 {Fmt.Pct(b2.Percent)}{(b2.Charging ? " 充电中" : "")}" : "没有电池"),
            _ => ("", C(Theme.CpuC), "CPU 走势"),
        };
    }

    Page _pendingBalloonTab = Page.Overview;

    public void Notify(string title, string body, Page tab)
    {
        if (_main is null) return;
        _pendingBalloonTab = tab;
        _main.ShowBalloonTip(8000, title, body, WF.ToolTipIcon.Info);
    }

    public void Dispose()
    {
        if (_main is not null) { _main.Visible = false; _main.Dispose(); }
        foreach (var n in _metricIcons.Values) { n.Visible = false; n.Dispose(); }
    }
}
