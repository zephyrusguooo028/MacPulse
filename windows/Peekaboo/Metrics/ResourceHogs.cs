using System.Diagnostics;

namespace Peekaboo;

/// An app worth quitting to free CPU or memory.
public sealed record ResourceHog(AppGroup Group, bool Cpu, double AvgCpu, int? IdleMinutes, bool Recommended)
{
    public string Reason => Cpu
        ? L($"持续占用 CPU，近几分钟平均 {Fmt.Pct(AvgCpu)}", $"Busy for a while, averaging {Fmt.Pct(AvgCpu)} CPU")
        : L($"占用 {Fmt.Bytes(Group.Memory)}，{IdleText}没用过", $"Using {Fmt.Bytes(Group.Memory)}, unused for {IdleText}");

    string IdleText
    {
        get
        {
            int m = IdleMinutes ?? 0;
            return m >= 60 ? L($"{m / 60} 小时", $"{m / 60} h") : L($"{m} 分钟", $"{m} min");
        }
    }
}

public static class ResourceHogs
{
    /// Never offered: Windows itself, the shell, security software and Peekaboo.
    static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "svchost", "dwm", "explorer",
        "fontdrvhost", "sihost", "taskhostw", "ctfmon", "RuntimeBroker", "SearchHost", "StartMenuExperienceHost", "ShellExperienceHost",
        "TextInputHost", "SecurityHealthService", "SecurityHealthSystray", "MsMpEng", "NisSrv", "audiodg", "spoolsv", "WmiPrvSE",
        "conhost", "dllhost", "LockApp", "Widgets", "WidgetService", "Peekaboo", "MemCompression", "SgrmBroker",
    };

    static bool IsProtected(AppGroup g)
    {
        var exe = Path.GetFileNameWithoutExtension(g.Path ?? g.Name);
        if (Protected.Contains(exe) || Protected.Contains(g.Name)) return true;
        if (g.Path is null) return true;   // cannot tell what it is
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return g.Path.StartsWith(win, StringComparison.OrdinalIgnoreCase);
    }

    static bool HasWindow(AppGroup g)
    {
        foreach (var pid in g.Pids)
        {
            try { using var p = Process.GetProcessById(pid); if (p.MainWindowHandle != IntPtr.Zero) return true; } catch { }
        }
        return false;
    }

    static DateTime? StartTime(AppGroup g)
    {
        DateTime? best = null;
        foreach (var pid in g.Pids.Take(5))
        {
            try { using var p = Process.GetProcessById(pid); var t = p.StartTime; if (best is null || t < best) best = t; } catch { }
        }
        return best;
    }

    public static List<ResourceHog> Find()
    {
        var m = SystemMonitor.Shared;
        var list = new List<ResourceHog>();
        foreach (var g in m.Groups)
        {
            if (IsProtected(g)) continue;
            double avg = m.CpuAverage.TryGetValue(g.Id, out var a) ? a : g.Cpu;
            if (avg > 0.5 && g.Cpu > 0.25 && m.ProcessSamples >= 3) { list.Add(new ResourceHog(g, true, avg, null, false)); continue; }
            if (g.Memory < 300L << 20 || !HasWindow(g)) continue;
            var since = new[] { m.LastActive.TryGetValue(g.Path!, out var la) ? la : (DateTime?)null, StartTime(g), m.StartedAt }
                .Where(d => d is not null).Max()!.Value;
            int idle = (int)(DateTime.Now - since).TotalMinutes;
            if (idle < 30) continue;
            list.Add(new ResourceHog(g, false, avg, idle, idle >= 60 && g.Memory > 500L << 20));
        }
        return list.OrderByDescending(h => h.Cpu).ThenByDescending(h => h.Cpu ? h.AvgCpu : h.Group.Memory).ToList();
    }

    public static (string title, string detail, int level) MemorySummary()
    {
        var mem = SystemMonitor.Shared.Memory;
        return mem.PressureLevel switch
        {
            4 => (L("内存很紧张", "Memory is tight"), L("Windows 已经在大量使用页面文件，会明显变卡。退出几个占内存大的应用最有效。", "Windows is leaning on the page file, which slows things down. Quitting a few large apps helps most."), 4),
            2 => (L("内存有点紧", "Memory is getting tight"), L("还能正常用，但再多开几个大应用就会变慢。可以退出下面这些很久没用的应用。", "Still fine, but a few more big apps will slow things down. Consider quitting the unused apps below."), 2),
            _ => (L("内存充足", "Memory is fine"), L($"其中 {Fmt.Bytes(mem.Cached)} 是系统缓存，需要时会自动让出，不用手动清理。", $"{Fmt.Bytes(mem.Cached)} is system cache that Windows frees on demand. Nothing to clean."), 1),
        };
    }

    /// Asks each app to close its windows (like clicking X). Never forces.
    public static (int count, long memory) Quit(IEnumerable<ResourceHog> hogs)
    {
        int n = 0; long mem = 0;
        foreach (var h in hogs) if (ProcessReader.Quit(h.Group, force: false)) { n++; mem += h.Group.Memory; }
        return (n, mem);
    }
}
