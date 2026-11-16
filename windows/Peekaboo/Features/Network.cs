using System.Net;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using static Peekaboo.UIX;

namespace Peekaboo;

/// One socket from the IP helper tables.
internal readonly record struct SocketEntry(int Pid, bool Tcp, bool Listening, string? Remote);

/// Active TCP / UDP endpoints per process from GetExtendedTcpTable / GetExtendedUdpTable.
/// These need no administrator rights (unlike per-process byte counts, which need ETW).
internal static class SocketTables
{
    const int AF_INET = 2, AF_INET6 = 23, ERROR_INSUFFICIENT_BUFFER = 122;
    const int TCP_TABLE_OWNER_PID_ALL = 5, UDP_TABLE_OWNER_PID = 1;
    const uint MIB_TCP_STATE_LISTEN = 2;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    static extern uint GetExtendedUdpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);

    public static List<SocketEntry> Read()
    {
        var list = new List<SocketEntry>();
        try { ReadTable(true, AF_INET, list); } catch { }
        try { ReadTable(true, AF_INET6, list); } catch { }
        try { ReadTable(false, AF_INET, list); } catch { }
        try { ReadTable(false, AF_INET6, list); } catch { }
        return list;
    }

    /// Layouts (all fields DWORD unless noted; each table starts with a DWORD row count, rows follow at offset 4):
    ///   MIB_TCPROW_OWNER_PID   state, localAddr, localPort, remoteAddr, remotePort, pid                       = 24 bytes
    ///   MIB_TCP6ROW_OWNER_PID  localAddr[16], localScope, localPort, remoteAddr[16], remoteScope, remotePort,
    ///                          state, pid                                                                     = 56 bytes
    ///   MIB_UDPROW_OWNER_PID   localAddr, localPort, pid                                                      = 12 bytes
    ///   MIB_UDP6ROW_OWNER_PID  localAddr[16], localScope, localPort, pid                                      = 28 bytes
    static void ReadTable(bool tcp, int af, List<SocketEntry> into)
    {
        int size = 0, allocated = 0;
        IntPtr buf = IntPtr.Zero;
        try
        {
            uint Call() => tcp ? GetExtendedTcpTable(buf, ref size, false, af, TCP_TABLE_OWNER_PID_ALL, 0)
                               : GetExtendedUdpTable(buf, ref size, false, af, UDP_TABLE_OWNER_PID, 0);
            uint rc = Call();   // first call with no buffer returns the size needed
            for (int attempt = 0; attempt < 4 && rc == ERROR_INSUFFICIENT_BUFFER; attempt++)
            {
                if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
                allocated = size + 2048;   // the table can grow between calls
                size = allocated;
                buf = Marshal.AllocHGlobal(allocated);
                rc = Call();
            }
            if (rc != 0 || buf == IntPtr.Zero) return;
            int n = Marshal.ReadInt32(buf);
            int rowSize = (tcp, af) switch { (true, AF_INET) => 24, (true, _) => 56, (false, AF_INET) => 12, _ => 28 };
            if (n < 0 || 4 + (long)n * rowSize > allocated) return;
            for (int i = 0; i < n; i++)
            {
                IntPtr row = buf + 4 + i * rowSize;
                if (tcp && af == AF_INET)
                {
                    uint state = (uint)Marshal.ReadInt32(row, 0);
                    uint raddr = (uint)Marshal.ReadInt32(row, 12);
                    int rport = Port(Marshal.ReadInt32(row, 16));
                    int pid = Marshal.ReadInt32(row, 20);
                    bool listen = state == MIB_TCP_STATE_LISTEN;
                    into.Add(new SocketEntry(pid, true, listen, listen || raddr == 0 ? null : Endpoint(new IPAddress(raddr), rport)));
                }
                else if (tcp)
                {
                    var raw = new byte[16];
                    Marshal.Copy(row + 24, raw, 0, 16);
                    int rport = Port(Marshal.ReadInt32(row, 44));
                    uint state = (uint)Marshal.ReadInt32(row, 48);
                    int pid = Marshal.ReadInt32(row, 52);
                    bool listen = state == MIB_TCP_STATE_LISTEN;
                    var ip = new IPAddress(raw);
                    into.Add(new SocketEntry(pid, true, listen, listen || ip.Equals(IPAddress.IPv6Any) ? null : Endpoint(ip, rport)));
                }
                else
                {
                    int pid = Marshal.ReadInt32(row, af == AF_INET ? 8 : 24);
                    into.Add(new SocketEntry(pid, false, false, null));
                }
            }
        }
        finally
        {
            if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
        }
    }

    /// Ports are stored in network byte order in the low 16 bits.
    static int Port(int raw) => ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);

    static string Endpoint(IPAddress ip, int port)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        return ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{ip}]:{port}" : $"{ip}:{port}";
    }
}

internal sealed class AppConnections
{
    public string Name = "";
    public string? Path;
    public int Tcp, Udp, Listening;
    public List<string> Remotes = new();
}

public sealed class NetworkPage : PageView
{
    public static string? Badge => SystemMonitor.Shared.NetIn >= 10 * 1024 ? Fmt.Short(SystemMonitor.Shared.NetIn) + "/s" : null;

    readonly ContentControl _top = new(), _table = new();
    readonly TextBlock _count = T("", 12, color: Theme.Secondary);
    DispatcherTimer? _timer;
    List<AppConnections>? _apps;
    bool _busy, _visible;

    public NetworkPage()
    {
        var note = T(L("Windows 上按应用统计上传 / 下载流量需要管理员权限，所以这里不显示每个应用用了多少流量，只显示它们正在使用的网络连接。上面的整体速率是准确的。",
                       "On Windows, measuring upload / download volume per app requires administrator rights, so per-app traffic is not shown here. Instead you see the network connections each app has open. The overall rates above are accurate."),
                     12, color: Theme.Secondary);
        note.TextWrapping = TextWrapping.Wrap; note.TextTrimming = TextTrimming.None;
        Content = V(16,
            PageHeader(L("网络", "Network"), L("整体速率和每个应用的网络连接，每 3 秒刷新", "Overall rates and each app’s connections, refreshed every 3 seconds")),
            _top,
            Card(L("按应用的连接", "Connections by app"), "", _table, _count),
            note);
    }

    public override void Shown()
    {
        _visible = true;
        SystemMonitor.Shared.WantProcesses(true);
        FillTop();
        _table.Content = Empty("", L("正在读取", "Loading"), L("马上就好。", "Just a moment."));
        Refresh();
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
    }

    public override void Hidden()
    {
        _visible = false;
        _timer?.Stop(); _timer = null;
        SystemMonitor.Shared.WantProcesses(false);
    }

    public override void Tick() => FillTop();

    void FillTop()
    {
        var m = SystemMonitor.Shared;
        FrameworkElement Rate(string label, double v, Color c) =>
            Row(T(label, 13, FontWeights.Medium, Theme.B(c)), T(Fmt.Rate(v), 18, FontWeights.SemiBold));
        var rates = Card(L("当前速率", "Current rate"), "", V(10,
            Rate(L("↓ 下载", "↓ Download"), m.NetIn, Theme.NetInC),
            Rate(L("↑ 上传", "↑ Upload"), m.NetOut, Theme.NetOutC)));
        rates.Width = 300;
        double max = Math.Max(1024, m.Samples.Select(s => Math.Max(s.NetIn, s.NetOut)).DefaultIfEmpty(0).Max());
        var chart = Card(L("最近 5 分钟", "Last 5 minutes"), "", Chart(new List<(IList<double>, Color, bool)>
            {
                (m.Samples.Select(s => s.NetIn).ToList(), Theme.NetInC, true),
                (m.Samples.Select(s => s.NetOut).ToList(), Theme.NetOutC, false),
            }, 130, max, v => Fmt.Short(v) + "/s", m.Samples.Select(s => s.T).ToList(), t => t.ToString("HH:mm:ss")),
            H(12, Dot(L("下载", "Download"), Theme.NetInC), Dot(L("上传", "Upload"), Theme.NetOutC)));
        var row = new DockPanel();
        DockPanel.SetDock(rates, Dock.Left); rates.Margin = new Thickness(0, 0, 14, 0);
        row.Children.Add(rates); row.Children.Add(chart);
        _top.Content = row;
    }

    static FrameworkElement Dot(string label, Color c)
        => H(5, new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = Theme.B(c), VerticalAlignment = VerticalAlignment.Center }, T(label, 12, color: Theme.Secondary));

    void Refresh()
    {
        if (_busy) return;
        _busy = true;
        // pid → app, taken on the UI thread so the background work never touches monitor state.
        var owner = new Dictionary<int, (string name, string? path)>();
        foreach (var g in SystemMonitor.Shared.Groups) foreach (var p in g.Pids) owner[p] = (g.Name, g.Path);
        var d = Dispatcher;
        Task.Run(() => Group(SocketTables.Read(), owner)).ContinueWith(t => d.BeginInvoke(() =>
        {
            _busy = false;
            if (!_visible) return;
            if (t.Status == TaskStatus.RanToCompletion) { _apps = t.Result; FillTable(); }
            else _table.Content = Empty("", L("读不到连接列表", "Couldn’t read the connection list"), t.Exception?.InnerException?.Message ?? "");
        }));
    }

    static List<AppConnections> Group(List<SocketEntry> sockets, Dictionary<int, (string name, string? path)> owner)
    {
        var map = new Dictionary<string, AppConnections>(StringComparer.OrdinalIgnoreCase);
        var names = new Dictionary<int, string>();
        foreach (var s in sockets)
        {
            // PID 0 only holds closed connections waiting to time out (TIME_WAIT); not an app.
            if (s.Pid == 0) continue;
            string name; string? path = null;
            if (owner.TryGetValue(s.Pid, out var o)) { name = o.name; path = o.path; }
            else if (s.Pid == 0) name = L("系统空闲进程", "System Idle Process");
            else if (s.Pid == 4) name = L("系统", "System");
            else
            {
                if (!names.TryGetValue(s.Pid, out var n))
                {
                    try { using var p = System.Diagnostics.Process.GetProcessById(s.Pid); n = p.ProcessName; } catch { n = $"PID {s.Pid}"; }
                    names[s.Pid] = n;
                }
                name = n;
            }
            if (!map.TryGetValue(name, out var a)) map[name] = a = new AppConnections { Name = name, Path = path };
            if (!s.Tcp) a.Udp++;
            else if (s.Listening) a.Listening++;
            else a.Tcp++;
            if (s.Remote is { } r && a.Remotes.Count < 3 && !a.Remotes.Contains(r) && !r.StartsWith("127.") && !r.StartsWith("[::1]")) a.Remotes.Add(r);
        }
        return map.Values.OrderByDescending(a => a.Tcp).ThenByDescending(a => a.Udp + a.Listening).ThenBy(a => a.Name).ToList();
    }

    void FillTable()
    {
        var apps = _apps ?? new();
        _count.Text = L($"{apps.Count} 个应用 · {apps.Sum(a => a.Tcp)} 个 TCP 连接", $"{apps.Count} apps · {apps.Sum(a => a.Tcp)} TCP connections");
        var grid = new Grid();
        foreach (var w in new[] { new GridLength(1, GridUnitType.Star), new GridLength(80), new GridLength(80), new GridLength(80), new GridLength(2, GridUnitType.Star) })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        int r = 0;
        void Cell(UIElement e, int col, bool right = false)
        {
            if (e is FrameworkElement fe) { fe.Margin = new Thickness(8, 5, 8, 5); fe.VerticalAlignment = VerticalAlignment.Center; if (right) fe.HorizontalAlignment = HorizontalAlignment.Right; }
            Grid.SetRow(e, r); Grid.SetColumn(e, col); grid.Children.Add(e);
        }
        grid.RowDefinitions.Add(new RowDefinition());
        Cell(T(L("应用", "App"), 12, FontWeights.SemiBold, Theme.Secondary), 0);
        Cell(T(L("TCP 连接", "TCP"), 12, FontWeights.SemiBold, Theme.Secondary), 1, true);
        Cell(T(L("监听", "Listening"), 12, FontWeights.SemiBold, Theme.Secondary), 2, true);
        Cell(T("UDP", 12, FontWeights.SemiBold, Theme.Secondary), 3, true);
        Cell(T(L("远程地址", "Remote addresses"), 12, FontWeights.SemiBold, Theme.Secondary), 4);
        foreach (var a in apps.Take(60))
        {
            r++;
            grid.RowDefinitions.Add(new RowDefinition());
            var line = new Border { BorderBrush = Theme.Line, BorderThickness = new Thickness(0, 1, 0, 0) };
            Grid.SetRow(line, r); Grid.SetColumnSpan(line, 5); grid.Children.Add(line);
            Cell(H(8, PopupWindow.AppIcon(a.Path, 18), T(a.Name, 13)), 0);
            Cell(T(a.Tcp > 0 ? a.Tcp.ToString() : "—", 12.5, color: a.Tcp > 0 ? Theme.B(Theme.NetInC) : Theme.Tertiary), 1, true);
            Cell(T(a.Listening > 0 ? a.Listening.ToString() : "—", 12.5, color: Theme.Secondary), 2, true);
            Cell(T(a.Udp > 0 ? a.Udp.ToString() : "—", 12.5, color: Theme.Secondary), 3, true);
            Cell(T(a.Remotes.Count > 0 ? string.Join("  ", a.Remotes) : "—", 12, color: Theme.Secondary), 4);
        }
        _table.Content = apps.Count == 0 ? Empty("", L("没有网络连接", "No network connections"), L("现在没有应用在使用网络。", "No app is using the network right now.")) : grid;
    }
}
