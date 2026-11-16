using System.Text.Json;
using System.Windows.Threading;

namespace Peekaboo;

public sealed record LiveSample(DateTime T, double Cpu, double Mem, double NetIn, double NetOut);
public sealed record MinutePoint(DateTime T, double Cpu, double CpuMax, double Mem, double NetIn, double NetOut);

/// One point per minute for the last 24 hours, saved to disk.
internal sealed class HistoryStore
{
    public List<MinutePoint> Points { get; } = new();
    DateTime? _bucket;
    double _cpu, _cpuMax, _mem, _in, _out; int _n;
    DateTime _lastSave = DateTime.Now;
    static string FilePath => Path.Combine(Paths.Support, "history.json");

    public HistoryStore()
    {
        try
        {
            if (File.Exists(FilePath))
                Points.AddRange((JsonSerializer.Deserialize<List<MinutePoint>>(File.ReadAllText(FilePath)) ?? new())
                    .Where(p => p.T > DateTime.Now.AddDays(-1)));
        }
        catch { }
    }

    public bool Add(double cpu, double mem, double netIn, double netOut)
    {
        var now = DateTime.Now;
        var minute = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0);
        bool appended = false;
        if (_bucket is { } b && b != minute && _n > 0)
        {
            Points.Add(new MinutePoint(b, _cpu / _n, _cpuMax, _mem / _n, _in / _n, _out / _n));
            Points.RemoveAll(p => p.T < now.AddDays(-1));
            _cpu = _cpuMax = _mem = _in = _out = 0; _n = 0;
            appended = true;
        }
        _bucket = minute;
        _cpu += cpu; _cpuMax = Math.Max(_cpuMax, cpu); _mem += mem; _in += netIn; _out += netOut; _n++;
        if ((now - _lastSave).TotalMinutes > 5) Save();
        return appended;
    }

    public void Save()
    {
        _lastSave = DateTime.Now;
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(Points)); } catch { }
    }
}

/// Samples everything on the UI thread on a timer and raises Ticked afterwards.
public sealed class SystemMonitor
{
    public static SystemMonitor Shared { get; } = new();

    public CpuReading Cpu { get; private set; } = new();
    public MemoryReading Memory { get; private set; } = new();
    public DiskReading Disk { get; private set; } = new();
    public double NetIn { get; private set; }
    public double NetOut { get; private set; }
    public BatteryReading? Battery { get; private set; }
    public double? Gpu { get; private set; }
    public List<LiveSample> Samples { get; } = new();
    public List<MinutePoint> History => _store.Points;
    public List<ProcInfo> Processes { get; private set; } = new();
    public List<AppGroup> Groups { get; private set; } = new();
    public IEnumerable<AppGroup> TopByCpu => Groups.OrderByDescending(g => g.Cpu).Take(5);

    public event Action? Ticked;

    readonly CpuReader _cpu = new();
    readonly NetworkReader _net = new();
    readonly ProcessReader _procs = new();
    readonly GpuReader _gpu = new();
    readonly HistoryStore _store = new();
    DispatcherTimer? _timer;
    int _tick, _processDemand;

    public void Start()
    {
        _cpu.Read(); _net.Read();
        Sample();
        Reschedule();
    }

    public void Reschedule()
    {
        _timer?.Stop();
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(Math.Max(1, Settings.Shared.SampleInterval)) };
        _timer.Tick += (_, _) => Sample();
        _timer.Start();
    }

    /// Process sampling is the costliest read, so it runs only while something shows processes.
    public void WantProcesses(bool on)
    {
        _processDemand = Math.Max(0, _processDemand + (on ? 1 : -1));
        if (on) RefreshProcesses();
    }

    public void RefreshProcesses()
    {
        Processes = _procs.Read();
        Groups = _procs.Group(Processes);
        // Smoothed CPU per app so one busy second does not look like a runaway process.
        double alpha = _processDemand > 0 ? 0.15 : 0.4;
        var next = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in Groups) next[g.Id] = (CpuAverage.TryGetValue(g.Id, out var prev) ? prev : g.Cpu) * (1 - alpha) + g.Cpu * alpha;
        CpuAverage = next;
        ProcessSamples++;
    }

    public Dictionary<string, double> CpuAverage { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
    public int ProcessSamples { get; private set; }
    /// Last time each executable owned the foreground window.
    public Dictionary<string, DateTime> LastActive { get; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTime StartedAt { get; } = DateTime.Now;

    void TrackForeground()
    {
        try
        {
            var h = GetForegroundWindow();
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == 0) return;
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            var path = p.MainModule?.FileName;
            if (path is not null) LastActive[path] = DateTime.Now;
        }
        catch { }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    public void Sample()
    {
        _tick++;
        Cpu = _cpu.Read();
        Memory = MemoryReader.Read();
        (NetIn, NetOut) = _net.Read();
        if (_tick % 10 == 1) { Disk = DiskReader.Read(); Battery = BatteryReader.Read(); }
        if (_tick % 2 == 1) Gpu = _gpu.Read() ?? Gpu;
        // Background sample every ~30 s keeps CPU averages warm for the cleanup page.
        if (_processDemand > 0 || _tick % 15 == 0) RefreshProcesses();
        TrackForeground();

        Samples.Add(new LiveSample(DateTime.Now, Cpu.Total, Memory.UsedFraction, NetIn, NetOut));
        if (Samples.Count > 150) Samples.RemoveRange(0, Samples.Count - 150);
        _store.Add(Cpu.Total, Memory.UsedFraction, NetIn, NetOut);
        Ticked?.Invoke();
    }

    public void Shutdown() => _store.Save();

    public string StatusSentence()
    {
        var s = Settings.Shared;
        if (Memory.PressureLevel >= 4) return L("内存压力很大，可以关掉一些应用", "Memory pressure is high, try closing some apps");
        if (Disk.Total > 0 && Disk.Free < s.AlertDiskGB * 1073741824L) return L($"{Disk.Name} 只剩 {Fmt.Bytes(Disk.Free)}，去清理一下吧", $"Only {Fmt.Bytes(Disk.Free)} left on {Disk.Name}, time to clean up");
        var cpu = Fmt.Pct(Cpu.Total);
        if (Cpu.Total > 0.9) return L($"CPU 满载中 {cpu}", $"CPU maxed out at {cpu}");
        if (Cpu.Total > 0.6) return L($"有点忙，CPU {cpu}", $"A bit busy, CPU {cpu}");
        if (Cpu.Total < 0.08) return L($"很清闲，CPU {cpu}", $"Nice and idle, CPU {cpu}");
        return L($"运行正常，CPU {cpu}", $"Running normally, CPU {cpu}");
    }
}
