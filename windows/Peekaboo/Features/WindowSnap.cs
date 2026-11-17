using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;

namespace Peekaboo;

/// Keyboard window layouts (Magnet style). Drag-to-edge snapping is left to Windows' own Snap.
public enum SnapAction
{
    LeftHalf, RightHalf, TopHalf, BottomHalf,
    TopLeft, TopRight, BottomLeft, BottomRight,
    LeftThird, CenterThird, RightThird, LeftTwoThirds, RightTwoThirds,
    Maximize, Center, Restore, PrevDisplay, NextDisplay,
}

/// Rectangle in physical screen pixels (left, top, right, bottom), like Win32 RECT.
public readonly record struct SnapRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public override string ToString() => $"({Left},{Top} {Width}x{Height})";
}

/// Window layout settings, owned by this feature (window-snap-settings.json in the app data folder).
public sealed class WindowSnapSettings
{
    public bool Enabled { get; set; } = true;
    /// Per-action shortcut overrides by action name. A null value disables that shortcut.
    public Dictionary<string, HotkeyData?> Overrides { get; set; } = new();

    static string FilePath => System.IO.Path.Combine(Peekaboo.Paths.Support, "window-snap-settings.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static WindowSnapSettings Load()
    {
        try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<WindowSnapSettings>(File.ReadAllText(FilePath), Json) ?? new(); }
        catch { }
        return new();
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json)); } catch { }
    }
}

/// Global layout shortcuts through RegisterHotKey on a hidden message window, plus a
/// foreground-window tracker so the page's tiles act on the last window that isn't Peekaboo.
/// Everything runs on the WPF dispatcher thread.
public static class WindowSnap
{
    public static WindowSnapSettings S { get; private set; } = new();
    /// Raised on the UI thread when settings, registration state or the tracked window change.
    public static event Action? Changed;
    public static bool Demo { get; } = Environment.GetCommandLineArgs().Contains("--demo");

    public static readonly SnapAction[] All = Enum.GetValues<SnapAction>();
    /// Actions whose shortcut failed to register (taken by another program, or a duplicate).
    static readonly HashSet<SnapAction> Failed = new();

    const int WM_HOTKEY = 0x0312, HotkeyBase = 0xC00;
    static HwndSource? _src;
    static IntPtr _hook;
    static WinEventProc? _hookProc;   // kept alive: the native hook holds a pointer to it
    static IntPtr _last;

    /// Window rect (incl. invisible borders) and maximized state before our first snap, per window.
    static readonly Dictionary<IntPtr, (RECT rect, bool maximized)> PreSnap = new();
    /// The window rect we last put each window in.
    static readonly Dictionary<IntPtr, RECT> Snapped = new();

    // ---- defaults

    const uint CA = HotkeyData.CTRL | HotkeyData.ALT;
    static HotkeyData K(int vk, string label, uint mods = CA) => new() { Vk = vk, Mods = mods, Label = label };

    public static HotkeyData Default(SnapAction a) => a switch
    {
        SnapAction.LeftHalf => K(0x25, "←"),
        SnapAction.RightHalf => K(0x27, "→"),
        SnapAction.TopHalf => K(0x26, "↑"),
        SnapAction.BottomHalf => K(0x28, "↓"),
        SnapAction.TopLeft => K(0x55, "U"),
        SnapAction.TopRight => K(0x49, "I"),
        SnapAction.BottomLeft => K(0x4A, "J"),
        SnapAction.BottomRight => K(0x4B, "K"),
        SnapAction.LeftThird => K(0x44, "D"),
        SnapAction.CenterThird => K(0x46, "F"),
        SnapAction.RightThird => K(0x47, "G"),
        SnapAction.LeftTwoThirds => K(0x45, "E"),
        SnapAction.RightTwoThirds => K(0x54, "T"),
        SnapAction.Maximize => K(0x0D, "Enter"),
        SnapAction.Center => K(0x43, "C"),
        SnapAction.Restore => K(0x08, "Backspace"),
        SnapAction.PrevDisplay => K(0x25, "←", CA | HotkeyData.WIN),
        _ => K(0x27, "→", CA | HotkeyData.WIN),
    };

    public static HotkeyData? Hotkey(SnapAction a) => S.Overrides.TryGetValue(a.ToString(), out var k) ? k : Default(a);
    public static bool IsCustom(SnapAction a) => S.Overrides.ContainsKey(a.ToString());
    public static bool InUse(SnapAction a) => Failed.Contains(a);

    public static string Title(SnapAction a) => a switch
    {
        SnapAction.LeftHalf => L("左半屏", "Left half"),
        SnapAction.RightHalf => L("右半屏", "Right half"),
        SnapAction.TopHalf => L("上半屏", "Top half"),
        SnapAction.BottomHalf => L("下半屏", "Bottom half"),
        SnapAction.TopLeft => L("左上角", "Top left"),
        SnapAction.TopRight => L("右上角", "Top right"),
        SnapAction.BottomLeft => L("左下角", "Bottom left"),
        SnapAction.BottomRight => L("右下角", "Bottom right"),
        SnapAction.LeftThird => L("左三分之一", "Left third"),
        SnapAction.CenterThird => L("中间三分之一", "Center third"),
        SnapAction.RightThird => L("右三分之一", "Right third"),
        SnapAction.LeftTwoThirds => L("左三分之二", "Left two thirds"),
        SnapAction.RightTwoThirds => L("右三分之二", "Right two thirds"),
        SnapAction.Maximize => L("铺满", "Maximize"),
        SnapAction.Center => L("居中", "Center"),
        SnapAction.Restore => L("还原", "Restore"),
        SnapAction.PrevDisplay => L("上一个显示器", "Previous display"),
        _ => L("下一个显示器", "Next display"),
    };

    // ---- lifecycle

    /// Called once at startup (UI thread).
    public static void Start()
    {
        S = WindowSnapSettings.Load();
        if (_src is null)
        {
            _src = new HwndSource(new HwndSourceParameters("PeekabooWindowSnap") { Width = 0, Height = 0, WindowStyle = 0 });
            _src.AddHook(WndProc);
            if (Application.Current is { } app) app.Exit += (_, _) => Stop();
        }
        if (_hook == IntPtr.Zero)
        {
            _hookProc = OnForeground;
            // Out of context: callbacks arrive on this (UI) thread's message loop. Skips Peekaboo's own windows.
            _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _hookProc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
            Track(GetForegroundWindow());
        }
        ApplyHotkeys();
    }

    /// Re-registers every shortcut after a settings change and records which ones failed.
    public static void ApplyHotkeys()
    {
        if (_src is null) return;
        Failed.Clear();
        foreach (var a in All) UnregisterHotKey(_src.Handle, HotkeyBase + (int)a);
        // Demo runs sit beside the real app; don't steal its shortcuts.
        if (S.Enabled && !Demo)
            foreach (var a in All)
                if (Hotkey(a) is { } k && !RegisterHotKey(_src.Handle, HotkeyBase + (int)a, k.Mods | 0x4000 /* MOD_NOREPEAT */, (uint)k.Vk))
                    Failed.Add(a);
        Changed?.Invoke();
    }

    /// While recording a new shortcut, so the existing ones don't swallow the keys.
    public static void SuspendHotkeys()
    {
        if (_src is null) return;
        foreach (var a in All) UnregisterHotKey(_src.Handle, HotkeyBase + (int)a);
    }

    public static void SetEnabled(bool on) { S.Enabled = on; S.Save(); ApplyHotkeys(); }

    /// null = shortcut cleared (disabled).
    public static void SetHotkey(SnapAction a, HotkeyData? k)
    {
        if (k is not null && Default(a).Same(k)) S.Overrides.Remove(a.ToString());
        else S.Overrides[a.ToString()] = k;
        S.Save();
        ApplyHotkeys();
    }

    public static void RestoreDefaults() { S.Overrides.Clear(); S.Save(); ApplyHotkeys(); }

    static void Stop()
    {
        if (_hook != IntPtr.Zero) { UnhookWinEvent(_hook); _hook = IntPtr.Zero; }
        if (_src is null) return;
        SuspendHotkeys();
        _src.RemoveHook(WndProc);
        _src.Dispose();
        _src = null;
    }

    static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            int id = w.ToInt32() - HotkeyBase;
            if (id >= 0 && id < All.Length)
            {
                handled = true;
                try { Perform(All[id], GetForegroundWindow()); } catch { }
            }
        }
        return IntPtr.Zero;
    }

    // ---- foreground tracking

    static void OnForeground(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 /* OBJID_WINDOW */) return;
        Track(hwnd);
    }

    static void Track(IntPtr h)
    {
        if (h == IntPtr.Zero || h == _last || IsOwn(h) || !Snappable(h)) return;
        _last = h;
        Changed?.Invoke();
    }

    static bool IsOwn(IntPtr h)
    {
        GetWindowThreadProcessId(h, out uint pid);
        return pid == (uint)Environment.ProcessId;
    }

    /// The last foreground window that isn't Peekaboo, if it is still around.
    public static IntPtr LastTarget => _last != IntPtr.Zero && IsWindow(_last) ? _last : IntPtr.Zero;

    /// "Notepad — notes.txt" style label for the tracked window, or null.
    public static string? LastTargetTitle
    {
        get
        {
            var h = LastTarget;
            if (h == IntPtr.Zero) return null;
            var sb = new StringBuilder(256);
            GetWindowText(h, sb, sb.Capacity);
            string title = sb.ToString().Trim();
            string? app = null;
            try
            {
                GetWindowThreadProcessId(h, out uint pid);
                using var p = Process.GetProcessById((int)pid);
                app = p.ProcessName;
            }
            catch { }
            if (title.Length == 0) return app;
            if (title.Length > 60) title = title[..60] + "…";
            return title;
        }
    }

    /// Applies a layout to the last non-Peekaboo foreground window (tiles on the page).
    public static bool ApplyToLast(SnapAction a) => LastTarget is var h && h != IntPtr.Zero && Perform(a, h);

    // ---- rect math (pure, checked by --selftest)

    static int Frac(int start, int length, int num, int den) => start + (int)((long)length * num / den);

    /// Target visible frame for a layout inside `work`. `current` is the window's visible frame (for Center).
    /// Null for actions that don't map to a region of one work area (Restore, display moves).
    public static SnapRect? Compute(SnapAction a, SnapRect work, SnapRect current)
    {
        int l = work.Left, t = work.Top, r = work.Right, b = work.Bottom, w = work.Width, h = work.Height;
        int X(int n, int k) => Frac(l, w, k, n);
        int Y(int n, int k) => Frac(t, h, k, n);
        return a switch
        {
            SnapAction.LeftHalf => new SnapRect(l, t, X(2, 1), b),
            SnapAction.RightHalf => new SnapRect(X(2, 1), t, r, b),
            SnapAction.TopHalf => new SnapRect(l, t, r, Y(2, 1)),
            SnapAction.BottomHalf => new SnapRect(l, Y(2, 1), r, b),
            SnapAction.TopLeft => new SnapRect(l, t, X(2, 1), Y(2, 1)),
            SnapAction.TopRight => new SnapRect(X(2, 1), t, r, Y(2, 1)),
            SnapAction.BottomLeft => new SnapRect(l, Y(2, 1), X(2, 1), b),
            SnapAction.BottomRight => new SnapRect(X(2, 1), Y(2, 1), r, b),
            SnapAction.LeftThird => new SnapRect(l, t, X(3, 1), b),
            SnapAction.CenterThird => new SnapRect(X(3, 1), t, X(3, 2), b),
            SnapAction.RightThird => new SnapRect(X(3, 2), t, r, b),
            SnapAction.LeftTwoThirds => new SnapRect(l, t, X(3, 2), b),
            SnapAction.RightTwoThirds => new SnapRect(X(3, 1), t, r, b),
            SnapAction.Maximize => work,
            SnapAction.Center => CenterIn(work, current.Width, current.Height),
            _ => null,
        };
    }

    static SnapRect CenterIn(SnapRect work, int cw, int ch)
    {
        int w = Math.Min(cw, work.Width), h = Math.Min(ch, work.Height);
        int x = work.Left + (work.Width - w) / 2, y = work.Top + (work.Height - h) / 2;
        return new SnapRect(x, y, x + w, y + h);
    }

    /// Same relative position and size on another work area (so a left half stays a left half).
    public static SnapRect MoveBetween(SnapRect frame, SnapRect from, SnapRect to)
    {
        if (from.Width <= 0 || from.Height <= 0) return frame;
        int MX(int x) => to.Left + (int)Math.Round((double)(x - from.Left) * to.Width / from.Width);
        int MY(int y) => to.Top + (int)Math.Round((double)(y - from.Top) * to.Height / from.Height);
        int l = MX(frame.Left), t = MY(frame.Top), r = MX(frame.Right), b = MY(frame.Bottom);
        // Keep the whole window on the target display.
        int w = Math.Min(r - l, to.Width), h = Math.Min(b - t, to.Height);
        l = Math.Clamp(l, to.Left, to.Right - w); t = Math.Clamp(t, to.Top, to.Bottom - h);
        return new SnapRect(l, t, l + w, t + h);
    }

    /// Window rect for a desired visible frame, given the window's current window rect and visible frame
    /// (Windows 10/11 windows have invisible resize borders outside the visible frame).
    public static SnapRect Expand(SnapRect target, SnapRect windowRect, SnapRect frame) => new(
        target.Left - (frame.Left - windowRect.Left),
        target.Top - (frame.Top - windowRect.Top),
        target.Right + (windowRect.Right - frame.Right),
        target.Bottom + (windowRect.Bottom - frame.Bottom));

    // ---- applying

    static readonly HashSet<string> ShellClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow", "MultitaskingViewFrame", "ForegroundStaging", "TaskSwitcherWnd",
    };

    /// A visible, resizable, non-minimized top-level window that isn't the desktop or taskbar.
    public static bool Snappable(IntPtr h)
    {
        if (h == IntPtr.Zero || !IsWindow(h) || !IsWindowVisible(h) || IsIconic(h)) return false;
        var cls = new StringBuilder(256);
        GetClassName(h, cls, cls.Capacity);
        if (ShellClasses.Contains(cls.ToString())) return false;
        uint style = GetWindowLong(h, GWL_STYLE);
        return (style & WS_CHILD) == 0 && (style & WS_THICKFRAME) != 0;
    }

    /// Runs `f` with per-monitor-v2 DPI awareness so window and monitor rects are physical pixels.
    /// The manifest already declares PerMonitorV2, so this is only a safeguard.
    static T PerMonitor<T>(Func<T> f)
    {
        IntPtr old = IntPtr.Zero;
        try { old = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); } catch { }
        try { return f(); }
        finally { if (old != IntPtr.Zero) try { SetThreadDpiAwarenessContext(old); } catch { } }
    }

    public static bool Perform(SnapAction a, IntPtr h) => Snappable(h) && PerMonitor(() => PerformCore(a, h));

    static bool PerformCore(SnapAction a, IntPtr h)
    {
        Prune();
        bool wasMax = IsZoomed(h);
        if (a == SnapAction.Restore)
        {
            if (!PreSnap.TryGetValue(h, out var pre)) return false;
            if (wasMax) ShowWindow(h, SW_RESTORE);
            var r = pre.rect;
            var before = MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST);
            SetWindowPos(h, IntPtr.Zero, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, SWP_NOZORDER | SWP_NOACTIVATE);
            // Crossing to a display with another DPI lets the app resize itself; set it again.
            if (MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST) != before)
                SetWindowPos(h, IntPtr.Zero, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, SWP_NOZORDER | SWP_NOACTIVATE);
            if (pre.maximized) ShowWindow(h, SW_MAXIMIZE);
            PreSnap.Remove(h); Snapped.Remove(h);
            return true;
        }

        if (wasMax) ShowWindow(h, SW_RESTORE);
        if (!GetWindowRect(h, out var wr)) return false;
        var frame = Frame(h, wr);
        var mon = MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST);
        if (WorkArea(mon) is not { } work) return false;

        SnapRect? target;
        if (a is SnapAction.PrevDisplay or SnapAction.NextDisplay)
        {
            var mons = Monitors();
            int i = mons.FindIndex(m => m.handle == mon);
            if (mons.Count < 2 || i < 0) return false;
            var to = mons[(i + (a == SnapAction.NextDisplay ? 1 : mons.Count - 1)) % mons.Count].work;
            // A window we restored from maximized goes back to maximized-looking: fill the other work area.
            target = wasMax ? to : MoveBetween(frame, work, to);
        }
        else target = Compute(a, work, frame);
        if (target is not { } t) return false;

        // Remember where it was, unless it's still where we last put it (repeated snaps keep the original).
        if (!(PreSnap.ContainsKey(h) && Snapped.TryGetValue(h, out var last) && Same(last, wr)))
            PreSnap[h] = (wr, wasMax);
        Place(h, t);
        if (GetWindowRect(h, out var now)) Snapped[h] = now;
        return true;
    }

    static void Place(IntPtr h, SnapRect target)
    {
        var before = MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST);
        for (int pass = 0; pass < 2; pass++)
        {
            if (!GetWindowRect(h, out var wr)) return;
            var r = Expand(target, ToSnap(wr), Frame(h, wr));
            SetWindowPos(h, IntPtr.Zero, r.Left, r.Top, r.Width, r.Height, SWP_NOZORDER | SWP_NOACTIVATE);
            // Moving onto a display with another DPI changes the border size and the app may resize
            // itself on WM_DPICHANGED; a second pass re-measures and fixes the rect.
            if (MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST) == before) return;
            before = MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST);
        }
    }

    static SnapRect Frame(IntPtr h, RECT wr)
        => DwmGetWindowAttribute(h, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT f, Marshal.SizeOf<RECT>()) == 0 && f.Right > f.Left ? ToSnap(f) : ToSnap(wr);

    static SnapRect ToSnap(RECT r) => new(r.Left, r.Top, r.Right, r.Bottom);
    static bool Same(RECT a, RECT b) => a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    static SnapRect? WorkArea(IntPtr mon)
    {
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        return mon != IntPtr.Zero && GetMonitorInfo(mon, ref mi) ? ToSnap(mi.rcWork) : null;
    }

    /// Displays left to right, then top to bottom.
    static List<(IntPtr handle, SnapRect work, SnapRect bounds)> Monitors()
    {
        var list = new List<(IntPtr, SnapRect, SnapRect)>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr m, IntPtr dc, ref RECT rc, IntPtr data) =>
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(m, ref mi)) list.Add((m, ToSnap(mi.rcWork), ToSnap(mi.rcMonitor)));
            return true;
        }, IntPtr.Zero);
        return list.OrderBy(x => x.Item3.Left).ThenBy(x => x.Item3.Top).ToList();
    }

    static void Prune()
    {
        foreach (var h in PreSnap.Keys.Where(k => !IsWindow(k)).ToList()) { PreSnap.Remove(h); Snapped.Remove(h); }
    }

    // ---- native

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, ref RECT rc, IntPtr data);

    const uint EVENT_SYSTEM_FOREGROUND = 3, WINEVENT_OUTOFCONTEXT = 0, WINEVENT_SKIPOWNPROCESS = 2;
    const int GWL_STYLE = -16, SW_RESTORE = 9, SW_MAXIMIZE = 3, DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    const uint WS_THICKFRAME = 0x00040000, WS_CHILD = 0x40000000, MONITOR_DEFAULTTONEAREST = 2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
    static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern uint GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT value, int size);
}
