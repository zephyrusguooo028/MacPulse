using System.Diagnostics;
using System.Text.Json;

namespace Peekaboo;

public enum CleanupCategory { DevCache, ProjectArtifacts, AppCache, TempAndLogs, Downloads, LargeFiles }

public sealed record CleanupItem(string Path, string Title, string Subtitle, long Size, CleanupCategory Category, bool Recommended, bool ContentsOnly = false);

public sealed record CleanupRecord(DateTime Date, long Bytes, int Count, bool Permanent);

public static class CleanupText
{
    public static string Title(CleanupCategory c) => c switch
    {
        CleanupCategory.DevCache => L("开发工具缓存", "Developer tool caches"),
        CleanupCategory.ProjectArtifacts => L("项目依赖与构建产物", "Project dependencies and build output"),
        CleanupCategory.AppCache => L("应用缓存", "App caches"),
        CleanupCategory.TempAndLogs => L("临时文件与日志", "Temp files and logs"),
        CleanupCategory.Downloads => L("下载文件夹里的旧文件", "Old files in Downloads"),
        _ => L("大文件", "Large files"),
    };

    public static string Note(CleanupCategory c) => c switch
    {
        CleanupCategory.DevCache => L("npm / pip / Go / NuGet 等工具的下载和编译缓存，删掉后用到时会重新下载。", "Download and build caches for npm, pip, Go, NuGet and other tools. They’re re-downloaded when needed."),
        CleanupCategory.ProjectArtifacts => L("node_modules、dist、bin/obj 等可重新生成的目录。默认只勾选长期没改动的项目。", "Regenerable folders like node_modules, dist and bin/obj. Only long-untouched projects are selected by default."),
        CleanupCategory.AppCache => L("浏览器和各应用的缓存。默认只勾选当前没在运行的应用。", "Browser and app caches. Only apps that aren’t running are selected by default."),
        CleanupCategory.TempAndLogs => L("系统临时文件夹、崩溃转储和日志。正在使用的文件会自动跳过。", "System temp folder, crash dumps and logs. Files in use are skipped automatically."),
        CleanupCategory.Downloads => L("下载文件夹里很久没动过的文件和安装包，请自己确认后勾选。", "Files and installers in Downloads that haven’t been touched in a long time. Review before selecting."),
        _ => L("整个用户目录里的大文件，来自磁盘页的扫描结果，请自己确认后勾选。", "Large files across your user folder, from the Disk page scan. Review before selecting."),
    };

    public static string Glyph(CleanupCategory c) => c switch
    {
        CleanupCategory.DevCache => "",       // repair
        CleanupCategory.ProjectArtifacts => "", // package
        CleanupCategory.AppCache => "",        // app
        CleanupCategory.TempAndLogs => "",     // report
        CleanupCategory.Downloads => "",       // download
        _ => "",                               // page
    };
}

public static class CleanupScanner
{
    sealed record Dev(string Path, string Title, bool Recommended);

    static Dev[] DevCaches => new Dev[]
    {
        new("%LocalAppData%\\npm-cache", L("npm 缓存", "npm cache"), true),
        new("%AppData%\\npm-cache", L("npm 缓存（旧位置）", "npm cache (old location)"), true),
        new("%LocalAppData%\\Yarn\\Cache", L("Yarn 缓存", "Yarn cache"), true),
        new("%LocalAppData%\\pnpm\\store", L("pnpm 存储", "pnpm store"), false),
        new("~\\.bun\\install\\cache", L("Bun 缓存", "Bun cache"), true),
        new("%LocalAppData%\\pip\\Cache", L("pip 缓存", "pip cache"), true),
        new("%LocalAppData%\\go-build", L("Go 编译缓存", "Go build cache"), true),
        new("%LocalAppData%\\NuGet\\v3-cache", L("NuGet 下载缓存", "NuGet download cache"), true),
        new("%LocalAppData%\\NuGet\\plugins-cache", L("NuGet 插件缓存", "NuGet plugins cache"), true),
        new("~\\.nuget\\packages", L("NuGet 全局包", "NuGet global packages"), false),
        new("~\\.gradle\\caches", L("Gradle 缓存", "Gradle cache"), true),
        new("~\\.cargo\\registry\\cache", L("Cargo 下载缓存", "Cargo download cache"), true),
        new("%LocalAppData%\\ms-playwright", L("Playwright 浏览器", "Playwright browsers"), false),
        new("~\\.cache\\huggingface", L("Hugging Face 模型缓存", "Hugging Face model cache"), false),
        new("%LocalAppData%\\electron\\Cache", L("Electron 下载缓存", "Electron download cache"), true),
        new("%LocalAppData%\\node-gyp\\Cache", L("node-gyp 缓存", "node-gyp cache"), true),
    };

    static string Expand(string p) => Settings.Expand(p);

    public static List<CleanupItem> DevCacheItems()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<CleanupItem>();
        foreach (var d in DevCaches)
        {
            var p = Expand(d.Path);
            if (!Directory.Exists(p) || !seen.Add(p)) continue;
            long s = FileSizer.Size(p);
            if (s > 1 << 20) list.Add(new CleanupItem(p, d.Title, Fmt.Tilde(p), s, CleanupCategory.DevCache, d.Recommended));
        }
        return list;
    }

    static readonly HashSet<string> CacheNames = new(StringComparer.OrdinalIgnoreCase)
    { "Cache", "Code Cache", "GPUCache", "CachedData", "cache2", "ShaderCache", "GrShaderCache", "DawnCache", "DawnGraphiteCache", "Service Worker\\CacheStorage" };

    /// Finds cache folders under AppData and groups them by the app's top-level folder.
    public static List<CleanupItem> AppCacheItems(HashSet<string> running)
    {
        var devPaths = new HashSet<string>(DevCaches.Select(d => Expand(d.Path)), StringComparer.OrdinalIgnoreCase);
        var hits = new List<(string app, string path)>();
        foreach (var root in new[] { Paths.LocalAppData, Paths.RoamingAppData })
        {
            IEnumerable<string> apps;
            try { apps = Directory.EnumerateDirectories(root); } catch { continue; }
            foreach (var app in apps)
            {
                var name = Path.GetFileName(app);
                if (name is "Temp" or "Packages" or "Microsoft" or "npm-cache" or "pip" or "NuGet" or "Yarn" or "go-build" or "Peekaboo") continue;
                FindCaches(app, 0, devPaths, p => hits.Add((name, p)));
            }
            // Edge and Teams live under Microsoft\ — look one level deeper there.
            var ms = Path.Combine(root, "Microsoft");
            if (Directory.Exists(ms))
                foreach (var app in SafeDirs(ms))
                    FindCaches(app, 0, devPaths, p => hits.Add(("Microsoft " + Path.GetFileName(app), p)));
        }
        var items = new List<CleanupItem>();
        foreach (var g in hits.GroupBy(h => h.app, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var (app, path) in g)
            {
                long s = FileSizer.Size(path);
                if (s < 10L << 20) continue;
                bool isRunning = MatchesRunning(app, running);
                var rel = Fmt.Tilde(path);
                items.Add(new CleanupItem(path, $"{app} · {Path.GetFileName(path)}", isRunning ? rel + L(" · 正在运行", " · running") : rel, s,
                                          CleanupCategory.AppCache, !isRunning, ContentsOnly: true));
            }
        }
        return items;
    }

    static IEnumerable<string> SafeDirs(string p)
    {
        try { return Directory.EnumerateDirectories(p).ToList(); } catch { return Array.Empty<string>(); }
    }

    static void FindCaches(string dir, int depth, HashSet<string> exclude, Action<string> hit)
    {
        if (depth > 4) return;
        foreach (var d in SafeDirs(dir))
        {
            try { if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) continue; } catch { continue; }
            if (exclude.Contains(d)) continue;
            var name = Path.GetFileName(d);
            if (CacheNames.Contains(name)) { hit(d); continue; }
            if (name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)) continue;
            FindCaches(d, depth + 1, exclude, hit);
        }
    }

    public static HashSet<string> RunningNames()
    {
        var s = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses()) { using (p) s.Add(p.ProcessName); }
        return s;
    }

    static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
    { "microsoft", "user", "data", "cache", "local", "roaming", "app", "apps", "helper", "service", "windows", "software", "inc" };

    static IEnumerable<string> Words(string s)
    {
        var cur = new System.Text.StringBuilder();
        var outList = new List<string>();
        foreach (var ch in s)
        {
            if (char.IsLetterOrDigit(ch))
            {
                if (char.IsUpper(ch) && cur.Length > 0 && char.IsLower(cur[^1])) { outList.Add(cur.ToString()); cur.Clear(); }
                cur.Append(ch);
            }
            else if (cur.Length > 0) { outList.Add(cur.ToString()); cur.Clear(); }
        }
        if (cur.Length > 0) outList.Add(cur.ToString());
        return outList.Select(w => w.ToLowerInvariant()).Where(w => w.Length >= 4 && !Generic.Contains(w));
    }

    public static bool MatchesRunning(string folder, HashSet<string> running)
    {
        if (running.Contains(folder)) return true;
        var ws = Words(folder).ToList();
        if (ws.Count == 0) return false;
        var rw = new HashSet<string>(running.SelectMany(Words));
        return ws.Any(rw.Contains);
    }

    public static List<CleanupItem> TempAndLogItems()
    {
        var list = new List<CleanupItem>();
        var temp = Path.GetTempPath().TrimEnd('\\');
        long ts = FileSizer.Size(temp);
        if (ts > 1 << 20) list.Add(new CleanupItem(temp, L("系统临时文件", "System temp files"), Fmt.Tilde(temp), ts, CleanupCategory.TempAndLogs, true, ContentsOnly: true));
        var dumps = Path.Combine(Paths.LocalAppData, "CrashDumps");
        long ds = FileSizer.Size(dumps);
        if (ds > 1 << 20) list.Add(new CleanupItem(dumps, L("崩溃转储", "Crash dumps"), Fmt.Tilde(dumps), ds, CleanupCategory.TempAndLogs, true, ContentsOnly: true));
        var wer = Path.Combine(Paths.LocalAppData, "Microsoft", "Windows", "WER");
        long ws = FileSizer.Size(wer);
        if (ws > 1 << 20) list.Add(new CleanupItem(wer, L("Windows 错误报告", "Windows Error Reporting"), Fmt.Tilde(wer), ws, CleanupCategory.TempAndLogs, true, ContentsOnly: true));
        return list;
    }

    static readonly HashSet<string> InstallerExts = new(StringComparer.OrdinalIgnoreCase) { ".exe", ".msi", ".zip", ".7z", ".rar", ".iso", ".msix", ".appx" };

    public static List<CleanupItem> DownloadItems(int olderThanDays)
    {
        var list = new List<CleanupItem>();
        if (!Directory.Exists(Paths.Downloads)) return list;
        var cutoff = DateTime.Now.AddDays(-olderThanDays);
        var installerCutoff = DateTime.Now.AddDays(-14);
        foreach (var p in Directory.EnumerateFileSystemEntries(Paths.Downloads))
        {
            try
            {
                var name = Path.GetFileName(p);
                if (name.StartsWith('.') || name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                var last = new[] { File.GetLastWriteTime(p), File.GetLastAccessTime(p), File.GetCreationTime(p) }.Max();
                bool installer = InstallerExts.Contains(Path.GetExtension(p));
                if (!(last < cutoff || (installer && last < installerCutoff))) continue;
                long s = FileSizer.Size(p);
                if (s < 5 << 20) continue;
                list.Add(new CleanupItem(p, name, L($"{(installer ? "安装包/压缩包" : "文件")} · {Fmt.DaysAgo(last)} 天没动过", $"{(installer ? "Installer/archive" : "File")} · untouched for {Fmt.DaysAgo(last)} days"), s, CleanupCategory.Downloads, false));
            }
            catch { }
        }
        return list;
    }

    public static (long size, long items) RecycleBin()
    {
        var info = new Native.SHQUERYRBINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.SHQUERYRBINFO>() };
        return Native.SHQueryRecycleBin(null, ref info) == 0 ? (info.i64Size, info.i64NumItems) : (0, 0);
    }
}

public static class CleanupExecutor
{
    static readonly string[] Protected =
    {
        "", "Desktop", "Documents", "Downloads", "Pictures", "Music", "Videos", "OneDrive", "AppData",
        "AppData\\Local", "AppData\\Roaming", "AppData\\LocalLow", "AppData\\Local\\Microsoft", "AppData\\Roaming\\Microsoft",
        "AppData\\Local\\Packages", "AppData\\Local\\Programs", ".ssh", ".gnupg",
    };

    /// Last line of defence: only touch things inside the user profile, never the profile itself
    /// or one of its standard folders.
    public static bool IsSafeTarget(string path)
    {
        string full;
        try { full = Path.GetFullPath(path).TrimEnd('\\'); } catch { return false; }
        var home = Paths.Home.TrimEnd('\\');
        if (!full.StartsWith(home + "\\", StringComparison.OrdinalIgnoreCase)) return false;
        return !Protected.Any(p => full.Equals(p.Length == 0 ? home : home + "\\" + p, StringComparison.OrdinalIgnoreCase));
    }

    public sealed class Outcome
    {
        public long Freed; public int Count;
        public List<(string path, string error)> Failures = new();
        public List<string> Skipped = new();
    }

    public static Outcome Run(IEnumerable<CleanupItem> items, bool permanent)
    {
        var o = new Outcome();
        var s = Settings.Shared;
        foreach (var item in items)
        {
            if (s.IsWhitelisted(item.Path) || !IsSafeTarget(item.Path)) { o.Skipped.Add(item.Path); continue; }
            if (item.ContentsOnly)
            {
                // Clear what is inside; files in use are skipped quietly.
                long before = item.Size;
                foreach (var child in SafeEntries(item.Path))
                {
                    if (s.IsWhitelisted(child)) continue;
                    try
                    {
                        if (permanent)
                        {
                            if (Directory.Exists(child)) Directory.Delete(child, true); else File.Delete(child);
                        }
                        else Native.Recycle(child);
                    }
                    catch { }
                }
                long freed = Math.Max(before - FileSizer.Size(item.Path), 0);
                o.Freed += freed; o.Count++;
                continue;
            }
            try
            {
                string? err = null;
                if (permanent)
                {
                    if (Directory.Exists(item.Path)) Directory.Delete(item.Path, true); else File.Delete(item.Path);
                }
                else err = Native.Recycle(item.Path);
                if (err is null) { o.Freed += item.Size; o.Count++; } else o.Failures.Add((item.Path, err));
            }
            catch (Exception e) { o.Failures.Add((item.Path, e.Message)); }
        }
        if (o.Count > 0) Log(new CleanupRecord(DateTime.Now, o.Freed, o.Count, permanent));
        return o;
    }

    static IEnumerable<string> SafeEntries(string p)
    {
        try { return Directory.EnumerateFileSystemEntries(p).ToList(); } catch { return Array.Empty<string>(); }
    }

    public static long EmptyRecycleBin()
    {
        var (before, _) = CleanupScanner.RecycleBin();
        Native.SHEmptyRecycleBin(IntPtr.Zero, null, Native.SHERB_NOCONFIRMATION | Native.SHERB_NOPROGRESSUI | Native.SHERB_NOSOUND);
        var (after, _) = CleanupScanner.RecycleBin();
        long freed = Math.Max(before - after, 0);
        if (freed > 0) Log(new CleanupRecord(DateTime.Now, freed, 1, true));
        return freed;
    }

    static string LogPath => Path.Combine(Paths.Support, "cleanup-log.json");

    public static List<CleanupRecord> Records()
    {
        try { return JsonSerializer.Deserialize<List<CleanupRecord>>(File.ReadAllText(LogPath)) ?? new(); } catch { return new(); }
    }

    static void Log(CleanupRecord r)
    {
        var all = Records(); all.Add(r);
        try { File.WriteAllText(LogPath, JsonSerializer.Serialize(all)); } catch { }
    }
}
