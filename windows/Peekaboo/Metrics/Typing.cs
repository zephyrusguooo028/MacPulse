using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Threading;

namespace Peekaboo;

/// One day of typing statistics. Only counts: which key and which app, never the
/// order of keys, so nothing that was typed can be reconstructed.
public sealed class TypingDay
{
    public int Total { get; set; }
    public int[] Hours { get; set; } = new int[24];
    public Dictionary<string, int> Keys { get; set; } = new();
    public Dictionary<string, int> Apps { get; set; } = new();
    public double ActiveSeconds { get; set; }
    public double LongestStreak { get; set; }
}

public sealed class TypingMonitor
{
    public static TypingMonitor Shared { get; } = new();
    public Dictionary<string, TypingDay> Days { get; private set; } = new();
    public bool Running => _hook != IntPtr.Zero;
    public event Action? Changed;

    IntPtr _hook;
    HookProc? _proc;               // keep the delegate alive while hooked
    readonly HashSet<int> _down = new();
    DateTime? _last, _streakStart;
    bool _dirty;
    DispatcherTimer? _save;
    IntPtr _lastHwnd; string _app = "未知";
    readonly Dictionary<uint, string> _procNames = new();

    static string FilePath => Path.Combine(Paths.Support, "typing.json");

    TypingMonitor()
    {
        try { if (File.Exists(FilePath)) Days = JsonSerializer.Deserialize<Dictionary<string, TypingDay>>(File.ReadAllText(FilePath)) ?? new(); } catch { }
    }

    public static string Key(DateTime d) => d.ToString("yyyy-MM-dd");
    public TypingDay Today => Days.TryGetValue(Key(DateTime.Now), out var d) ? d : new TypingDay();
    public TypingDay Day(int offset) => Days.TryGetValue(Key(DateTime.Now.AddDays(-offset)), out var d) ? d : new TypingDay();

    public void Apply() { if (Settings.Shared.TypingEnabled) Start(); else Stop(); }

    void Start()
    {
        if (_hook != IntPtr.Zero) return;
        _proc = HookCallback;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        _save = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _save.Tick += (_, _) => Save();
        _save.Start();
        Changed?.Invoke();
    }

    public void Stop()
    {
        if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
        _save?.Stop(); _save = null;
        Save();
        Changed?.Invoke();
    }

    IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            int vk = Marshal.ReadInt32(lParam);
            if (msg is WM_KEYDOWN or WM_SYSKEYDOWN)
            {
                // Holding a key repeats key-down; count the first one only.
                if (_down.Add(vk)) Record(vk);
            }
            else if (msg is WM_KEYUP or WM_SYSKEYUP) _down.Remove(vk);
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    void Record(int vk)
    {
        var now = DateTime.Now;
        var key = Key(now);
        if (!Days.TryGetValue(key, out var d)) { d = new TypingDay(); Days[key] = d; }
        d.Total++;
        d.Hours[now.Hour]++;
        var k = vk.ToString();
        d.Keys[k] = d.Keys.GetValueOrDefault(k) + 1;
        var app = ForegroundApp();
        d.Apps[app] = d.Apps.GetValueOrDefault(app) + 1;
        if (_last is { } l && (now - l).TotalSeconds < 5)
        {
            d.ActiveSeconds += (now - l).TotalSeconds;
            if (_streakStart is { } s) d.LongestStreak = Math.Max(d.LongestStreak, (now - s).TotalSeconds);
        }
        else _streakStart = now;
        _last = now;
        _dirty = true;
    }

    string ForegroundApp()
    {
        var h = GetForegroundWindow();
        if (h == _lastHwnd) return _app;
        _lastHwnd = h;
        GetWindowThreadProcessId(h, out uint pid);
        if (!_procNames.TryGetValue(pid, out var name))
        {
            try
            {
                using var p = Process.GetProcessById((int)pid);
                name = p.ProcessName;
                try { var desc = p.MainModule?.FileVersionInfo.FileDescription; if (!string.IsNullOrWhiteSpace(desc) && desc.Length < 40) name = desc.Trim(); } catch { }
            }
            catch { name = "未知"; }
            _procNames[pid] = name;
        }
        _app = name;
        return name;
    }

    public void Save()
    {
        if (!_dirty) return;
        _dirty = false;
        var cutoff = Key(DateTime.Now.AddDays(-120));
        Days = Days.Where(kv => string.CompareOrdinal(kv.Key, cutoff) >= 0).ToDictionary(kv => kv.Key, kv => kv.Value);
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(Days)); } catch { }
    }

    public void ClearAll()
    {
        Days = new();
        try { File.Delete(FilePath); } catch { }
        Changed?.Invoke();
    }

    // ---- Win32

    delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
    const int WH_KEYBOARD_LL = 13, WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int idHook, HookProc fn, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
}

/// Standard keyboard with Windows virtual-key codes, for the heatmap.
public static class KeyboardLayout
{
    public sealed record K(int Vk, string Label, double Width = 1);

    public static readonly K[][] Rows =
    {
        new K[] { new(0xC0, "`"), new(0x31, "1"), new(0x32, "2"), new(0x33, "3"), new(0x34, "4"), new(0x35, "5"), new(0x36, "6"), new(0x37, "7"), new(0x38, "8"), new(0x39, "9"), new(0x30, "0"), new(0xBD, "-"), new(0xBB, "="), new(0x08, "⌫", 1.5) },
        new K[] { new(0x09, "Tab", 1.5), new(0x51, "Q"), new(0x57, "W"), new(0x45, "E"), new(0x52, "R"), new(0x54, "T"), new(0x59, "Y"), new(0x55, "U"), new(0x49, "I"), new(0x4F, "O"), new(0x50, "P"), new(0xDB, "["), new(0xDD, "]"), new(0xDC, "\\") },
        new K[] { new(0x14, "Caps", 1.75), new(0x41, "A"), new(0x53, "S"), new(0x44, "D"), new(0x46, "F"), new(0x47, "G"), new(0x48, "H"), new(0x4A, "J"), new(0x4B, "K"), new(0x4C, "L"), new(0xBA, ";"), new(0xDE, "'"), new(0x0D, "Enter", 1.75) },
        new K[] { new(0xA0, "Shift", 2.25), new(0x5A, "Z"), new(0x58, "X"), new(0x43, "C"), new(0x56, "V"), new(0x42, "B"), new(0x4E, "N"), new(0x4D, "M"), new(0xBC, ","), new(0xBE, "."), new(0xBF, "/"), new(0xA1, "Shift", 2.25) },
        new K[] { new(0xA2, "Ctrl", 1.25), new(0x5B, "Win", 1.25), new(0xA4, "Alt", 1.25), new(0x20, "空格", 5.5), new(0xA5, "Alt", 1.25), new(0xA3, "Ctrl", 1.25), new(0x25, "←"), new(0x28, "↓"), new(0x26, "↑"), new(0x27, "→") },
    };

    public static readonly Dictionary<int, string> Labels = Rows.SelectMany(r => r).GroupBy(k => k.Vk).ToDictionary(g => g.Key, g => g.First().Label);
}
