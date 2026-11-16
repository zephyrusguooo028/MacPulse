using System.Diagnostics;

namespace Peekaboo;

public sealed class ProcInfo
{
    public int Pid;
    public string Name = "";
    public string? Path;
    public double Cpu;     // 1.0 = one core
    public long Memory;    // private working set
}

public sealed class AppGroup
{
    public string Id = "";
    public string Name = "";
    public string? Path;
    public List<int> Pids = new();
    public double Cpu;
    public long Memory;
}

/// Samples all processes. Grouped by executable so Chrome's many processes are one row.
internal sealed class ProcessReader
{
    readonly Dictionary<int, TimeSpan> _prev = new();
    readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<int, string?> _paths = new();
    long _prevT;

    public List<ProcInfo> Read()
    {
        long now = Stopwatch.GetTimestamp();
        double dt = _prevT == 0 ? 0 : (now - _prevT) / (double)Stopwatch.Frequency;
        var list = new List<ProcInfo>();
        var seen = new Dictionary<int, TimeSpan>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (p.Id == 0) continue;
                var info = new ProcInfo { Pid = p.Id, Name = p.ProcessName };
                try { info.Memory = p.PrivateMemorySize64; } catch { }
                try
                {
                    var t = p.TotalProcessorTime;
                    seen[p.Id] = t;
                    if (dt > 0 && _prev.TryGetValue(p.Id, out var pt) && t >= pt)
                        info.Cpu = (t - pt).TotalSeconds / dt;
                }
                catch { }
                if (!_paths.TryGetValue(p.Id, out var path))
                {
                    try { path = p.MainModule?.FileName; } catch { path = null; }
                    _paths[p.Id] = path;
                }
                info.Path = path;
                list.Add(info);
            }
        }
        _prev.Clear();
        foreach (var kv in seen) _prev[kv.Key] = kv.Value;
        var alive = new HashSet<int>(list.Select(x => x.Pid));
        foreach (var dead in _paths.Keys.Where(k => !alive.Contains(k)).ToList()) _paths.Remove(dead);
        _prevT = now;
        return list;
    }

    public List<AppGroup> Group(List<ProcInfo> procs)
    {
        var groups = new Dictionary<string, AppGroup>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in procs)
        {
            string key = p.Path ?? p.Name;
            if (!groups.TryGetValue(key, out var g))
            {
                g = new AppGroup { Id = key, Name = DisplayName(p), Path = p.Path };
                groups[key] = g;
            }
            g.Pids.Add(p.Pid);
            g.Cpu += p.Cpu;
            g.Memory += p.Memory;
        }
        return groups.Values.ToList();
    }

    string DisplayName(ProcInfo p)
    {
        if (p.Path is null) return p.Name;
        if (_names.TryGetValue(p.Path, out var n)) return n;
        n = p.Name;
        try
        {
            var desc = FileVersionInfo.GetVersionInfo(p.Path).FileDescription;
            if (!string.IsNullOrWhiteSpace(desc) && desc.Length < 40) n = desc.Trim();
        }
        catch { }
        _names[p.Path] = n;
        return n;
    }

    /// Asks the app to close (like clicking X), or kills it when forced.
    public static bool Quit(AppGroup g, bool force)
    {
        bool ok = false;
        foreach (var pid in g.Pids)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (!force && p.MainWindowHandle != IntPtr.Zero) ok |= p.CloseMainWindow();
                else if (force) { p.Kill(entireProcessTree: false); ok = true; }
            }
            catch { }
        }
        return ok;
    }
}
