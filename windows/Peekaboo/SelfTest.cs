using System.Text;
using SD = System.Drawing;

namespace Peekaboo;

/// `--selftest <report.txt>`: exercises every reader and scanner without UI.
static class SelfTest
{
    public static void Run(string reportPath)
    {
        var sb = new StringBuilder();
        void L(string s) => sb.AppendLine(s);
        try
        {
            var cpu = new CpuReader(); var net = new NetworkReader(); var procs = new ProcessReader(); var gpu = new GpuReader();
            cpu.Read(); net.Read(); procs.Read(); gpu.Read();
            Thread.Sleep(1500);
            var c = cpu.Read();
            L($"CPU total {Fmt.Pct(c.Total, 1)} user {Fmt.Pct(c.User, 1)} sys {Fmt.Pct(c.System, 1)} cores {c.PerCore.Length} [{string.Join(" ", c.PerCore.Select(v => Fmt.Pct(v)))}]");
            var m = MemoryReader.Read();
            L($"MEM used {Fmt.Bytes(m.Used)} / {Fmt.Bytes(m.Total)} cached {Fmt.Bytes(m.Cached)} commit {Fmt.Bytes(m.CommitUsed)}/{Fmt.Bytes(m.CommitLimit)} pressure {m.PressureLevel}");
            foreach (var d in DiskReader.All()) L($"DISK {d.Name} total {Fmt.Bytes(d.Total)} free {Fmt.Bytes(d.Free)}");
            var (ni, no) = net.Read();
            L($"NET in {Fmt.Rate(ni)} out {Fmt.Rate(no)}");
            L($"GPU {(gpu.Read() is { } g ? Fmt.Pct(g) : "n/a")}");
            L($"BATTERY {(BatteryReader.Read() is { } b ? Fmt.Pct(b.Percent) + (b.Charging ? " charging" : "") : "none")}");
            L($"UPTIME {Fmt.Uptime()}");
            var p = procs.Read();
            var groups = procs.Group(p).OrderByDescending(x => x.Memory).ToList();
            L($"PROCS {p.Count}, groups {groups.Count}. Top by memory:");
            foreach (var x in groups.Take(8)) L($"   {x.Name}  {Fmt.Bytes(x.Memory)}  cpu {Fmt.Pct(x.Cpu, 1)}  x{x.Pids.Count}");

            L("DEV CACHES:"); foreach (var i in CleanupScanner.DevCacheItems()) L($"   {i.Title} {Fmt.Bytes(i.Size)} {(i.Recommended ? "推荐" : "")} {i.Subtitle}");
            L("APP CACHES:"); foreach (var i in CleanupScanner.AppCacheItems(CleanupScanner.RunningNames()).OrderByDescending(i => i.Size).Take(10)) L($"   {i.Title} {Fmt.Bytes(i.Size)} {(i.Recommended ? "推荐" : "")} {i.Subtitle}");
            L("TEMP & LOGS:"); foreach (var i in CleanupScanner.TempAndLogItems()) L($"   {i.Title} {Fmt.Bytes(i.Size)} {i.Subtitle}");
            L("DOWNLOADS (old):"); foreach (var i in CleanupScanner.DownloadItems(90).Take(10)) L($"   {i.Title} {Fmt.Bytes(i.Size)} {i.Subtitle}");
            var (rb, rbn) = CleanupScanner.RecycleBin(); L($"RECYCLE BIN {Fmt.Bytes(rb)} ({rbn} items)");

            var engine = new ProjectScanEngine();
            var roots = Settings.Shared.ProjectRootPaths.ToList();
            L($"PROJECT ROOTS: {string.Join(", ", roots)}");
            foreach (var (path, tags, root) in engine.Find(roots))
            {
                var pr = engine.Measure(path, tags, root);
                L($"   {pr.DisplayName} [{string.Join(",", tags)}] total {Fmt.Bytes(pr.TotalSize)} src {Fmt.Bytes(pr.SourceSize)} git {Fmt.Bytes(pr.GitSize)} artifacts {Fmt.Bytes(pr.ArtifactSize)}");
                foreach (var a in pr.Artifacts.Take(4)) L($"      - {Path.GetFileName(a.Path)} {Fmt.Bytes(a.Size)} {(a.Safe ? "safe" : "UNSURE")}");
            }

            L($"SAFETY home={CleanupExecutor.IsSafeTarget(Paths.Home)} docs={CleanupExecutor.IsSafeTarget(Paths.H("Documents"))} appdata={CleanupExecutor.IsSafeTarget(Paths.LocalAppData)} windows={CleanupExecutor.IsSafeTarget("C:\\Windows")} npm={CleanupExecutor.IsSafeTarget(Path.Combine(Paths.LocalAppData, "npm-cache"))}");

            // Recycle round trip on a scratch file inside the profile.
            var scratch = Path.Combine(Paths.LocalAppData, "Peekaboo", "selftest-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(scratch);
            File.WriteAllBytes(Path.Combine(scratch, "a.bin"), new byte[2 << 20]);
            var o = CleanupExecutor.Run(new[] { new CleanupItem(scratch, "selftest", "", 2 << 20, CleanupCategory.DevCache, false) }, permanent: true);
            L($"DELETE roundtrip count={o.Count} freed={Fmt.Bytes(o.Freed)} stillExists={Directory.Exists(scratch)} failures={o.Failures.Count}");

            var rects = Treemap.Layout(new double[] { 6, 6, 4, 3, 2, 2, 1 }, new System.Windows.Rect(0, 0, 600, 400));
            L($"TREEMAP area {rects.Sum(r => r.Width * r.Height):0} of 240000");

            var tree = new DiskScanEngine(500L << 20).Scan(Paths.Home);
            L($"HOME SCAN {Fmt.Bytes(tree.Size)} files {tree.FileCount}; top: {string.Join(", ", tree.Children.Take(6).Select(k => $"{k.Name} {Fmt.Bytes(k.Size)}"))}");
            L("OK");
        }
        catch (Exception e) { L("FAILED: " + e); }
        File.WriteAllText(reportPath, sb.ToString());
    }

    public static void IconSheet(string file)
    {
        var icons = Enum.GetValues<TrayIcon>().Where(i => i != TrayIcon.None).ToList();
        int cell = 64, cols = 12;
        using var bmp = new SD.Bitmap(cols * cell + 120, icons.Count * 2 * cell);
        using var g = SD.Graphics.FromImage(bmp);
        using var font = new SD.Font("Microsoft YaHei UI", 14);
        int row = 0;
        foreach (var icon in icons)
            foreach (var dark in new[] { false, true })
            {
                var bg = dark ? SD.Color.FromArgb(32, 32, 32) : SD.Color.FromArgb(243, 243, 243);
                var fg = dark ? SD.Color.White : SD.Color.Black;
                using (var b = new SD.SolidBrush(bg)) g.FillRectangle(b, 0, row * cell, bmp.Width, cell);
                using (var t = new SD.SolidBrush(fg)) g.DrawString(IconArt.Title(icon), font, t, 8, row * cell + 20);
                for (int f = 0; f < IconArt.Frames(icon); f++)
                {
                    using var fr = IconArt.Render(icon, f, 0.6, fg, 48);
                    g.DrawImage(fr, 120 + f * cell + 8, row * cell + 8);
                }
                row++;
            }
        bmp.Save(file, SD.Imaging.ImageFormat.Png);
    }
}
