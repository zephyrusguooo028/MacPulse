using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Peekaboo;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        Settings.Load();
        Theme.Load();
        if (Arg(args, "--selftest") is { } report) { SelfTest.Run(report); return 0; }
        if (Arg(args, "--icon-sheet") is { } sheet) { SelfTest.IconSheet(sheet); return 0; }

        using var mutex = new Mutex(true, "Peekaboo.SingleInstance", out bool first);
        if (!first && Arg(args, "--snapshot") is null) return 0;

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += (_, _) =>
        {
            var m = SystemMonitor.Shared;
            m.Ticked += Alerts.Check;
            m.Start();
            TrayController.Shared.Install();
            TypingMonitor.Shared.Apply();
            if (Arg(args, "--snapshot") is { } dir) _ = Snapshot.Run(dir);
            else if (!args.Contains("--background")) MainWindow.Show(Page.Overview);
        };
        app.Exit += (_, _) => { SystemMonitor.Shared.Shutdown(); TypingMonitor.Shared.Stop(); TrayController.Shared.Dispose(); };
        app.DispatcherUnhandledException += (_, e) =>
        {
            try { File.AppendAllText(Path.Combine(Paths.Support, "errors.log"), $"{DateTime.Now:u} {e.Exception}\n\n"); } catch { }
            e.Handled = true;
        };
        return app.Run();
    }

    static string? Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}

/// Threshold alerts with cooldowns, shown as tray balloon notifications.
static class Alerts
{
    static readonly Dictionary<string, DateTime> Last = new();
    static DateTime? _pressureSince, _cpuSince;
    static DateTime _lastTipCheck = DateTime.MinValue;

    public static void Check()
    {
        var m = SystemMonitor.Shared; var s = Settings.Shared; var now = DateTime.Now;
        if (s.AlertDisk && m.Disk.Total > 0 && m.Disk.Free < s.AlertDiskGB * 1073741824L)
            Fire("disk", TimeSpan.FromHours(6), "磁盘空间不足", $"{m.Disk.Name} 只剩 {Fmt.Bytes(m.Disk.Free)}，打开 Peekaboo 看看哪些可以清理。", Page.Cleanup);
        if (s.AlertMemory)
        {
            if (m.Memory.PressureLevel >= 4) { _pressureSince ??= now; if ((now - _pressureSince.Value).TotalSeconds > 30) Fire("mem", TimeSpan.FromHours(1), "内存压力很高", $"已用 {Fmt.Bytes(m.Memory.Used)} / {Fmt.Bytes(m.Memory.Total)}，可以关掉一些占内存的应用。", Page.Processes); }
            else _pressureSince = null;
        }
        if (s.AlertCpu)
        {
            if (m.Cpu.Total * 100 >= s.AlertCpuPercent) { _cpuSince ??= now; if ((now - _cpuSince.Value).TotalMinutes > 5) Fire("cpu", TimeSpan.FromHours(1), "CPU 持续高负载", $"过去 5 分钟 CPU 一直在 {s.AlertCpuPercent}% 以上。", Page.Processes); }
            else _cpuSince = null;
        }
        if (s.CleanupTips && (now - _lastTipCheck).TotalHours > 6)
        {
            _lastTipCheck = now;
            if (s.LastCleanupTip is null || (now - s.LastCleanupTip.Value).TotalDays > 7)
            {
                long threshold = s.CleanupTipGB * 1073741824L;
                Task.Run(() => CleanupScanner.DevCacheItems().Concat(CleanupScanner.TempAndLogItems()).Where(i => i.Recommended).Sum(i => i.Size))
                    .ContinueWith(t => UiThread.Run(() =>
                    {
                        s.LastCleanupTip = DateTime.Now; s.Save();
                        if (t.Result >= threshold) Fire("tip", TimeSpan.Zero, $"可以清理 {Fmt.Bytes(t.Result)}", $"开发缓存和临时文件占了 {Fmt.Bytes(t.Result)}，点这里查看并清理。", Page.Cleanup);
                    }));
            }
        }
    }

    static void Fire(string key, TimeSpan cooldown, string title, string body, Page tab)
    {
        if (Last.TryGetValue(key, out var l) && DateTime.Now - l < cooldown) return;
        Last[key] = DateTime.Now;
        TrayController.Shared.Notify(title, body, tab);
    }
}

/// `--snapshot <dir>`: opens every page with live data and saves PNGs (used by CI).
static class Snapshot
{
    public static async Task Run(string dir)
    {
        Directory.CreateDirectory(dir);
        SystemMonitor.Shared.WantProcesses(true);
        await Task.Delay(5000);

        PopupWindow.Toggle();
        await Task.Delay(1500);
        foreach (Window w in Application.Current.Windows) if (w is PopupWindow p) { Save(p, Path.Combine(dir, "0-popup.png")); p.Close(); }

        MainWindow.Show(Page.Overview);
        await Task.Delay(2500);
        Save(MainWindow.Instance!, Path.Combine(dir, "1-overview.png"));

        MainWindow.Instance!.Navigate(Page.Disk);
        DiskModel.Shared.Scan(Paths.Home);
        while (DiskModel.Shared.Scanning) await Task.Delay(500);
        await Task.Delay(1500);
        Save(MainWindow.Instance!, Path.Combine(dir, "2-disk.png"));

        MainWindow.Instance!.Navigate(Page.Projects);
        await Task.Delay(1000);
        while (ProjectModel.Shared.Scanning) await Task.Delay(500);
        await Task.Delay(1000);
        Save(MainWindow.Instance!, Path.Combine(dir, "3-projects.png"));

        MainWindow.Instance!.Navigate(Page.Processes);
        await Task.Delay(4000);
        Save(MainWindow.Instance!, Path.Combine(dir, "4-processes.png"));

        MainWindow.Instance!.Navigate(Page.Cleanup);
        await Task.Delay(1000);
        while (CleanupModel.Shared.Scanning || ProjectModel.Shared.Scanning) await Task.Delay(500);
        await Task.Delay(1000);
        Save(MainWindow.Instance!, Path.Combine(dir, "5-cleanup.png"));

        // Exercise the keyboard hook: simulate keys and check they were counted.
        Settings.Shared.TypingEnabled = true;
        TypingMonitor.Shared.Apply();
        MainWindow.Instance!.Navigate(Page.Typing);
        int before = TypingMonitor.Shared.Today.Total;
        foreach (var ch in "peekaboo is counting keys only 1234567890")
        {
            byte vk = ch == ' ' ? (byte)0x20 : (byte)char.ToUpperInvariant(ch);
            keybd_event(vk, 0, 0, UIntPtr.Zero);
            await Task.Delay(15);
            keybd_event(vk, 0, 2, UIntPtr.Zero);
            await Task.Delay(15);
        }
        await Task.Delay(1500);
        File.WriteAllText(Path.Combine(dir, "typing.txt"), $"hook running={TypingMonitor.Shared.Running} counted={TypingMonitor.Shared.Today.Total - before} of 41");
        Save(MainWindow.Instance!, Path.Combine(dir, "7-typing.png"));

        MainWindow.Instance!.Navigate(Page.Settings);
        await Task.Delay(1500);
        Save(MainWindow.Instance!, Path.Combine(dir, "6-settings.png"));
        Application.Current.Shutdown();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    static void Save(Window w, string file)
    {
        try
        {
            var el = (FrameworkElement)w.Content;
            el.UpdateLayout();
            double dpi = VisualTreeHelper.GetDpi(el).PixelsPerInchX;
            int width = (int)Math.Max(1, el.ActualWidth * dpi / 96), height = (int)Math.Max(1, el.ActualHeight * dpi / 96);
            var rtb = new RenderTargetBitmap(width, height, dpi, dpi, PixelFormats.Pbgra32);
            var bg = new DrawingVisual();
            using (var ctx = bg.RenderOpen()) ctx.DrawRectangle(Theme.Window, null, new Rect(0, 0, el.ActualWidth, el.ActualHeight));
            rtb.Render(bg);
            rtb.Render(el);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using var fs = File.Create(file);
            enc.Save(fs);
        }
        catch (Exception e) { File.WriteAllText(file + ".error.txt", e.ToString()); }
    }
}
