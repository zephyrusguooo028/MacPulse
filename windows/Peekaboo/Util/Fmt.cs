using System.Globalization;

namespace Peekaboo;

/// Picks the Chinese or English text for the current language setting.
public static class Loc
{
    public static bool English => Settings.Shared.Language switch
    {
        AppLanguage.Zh => false,
        AppLanguage.En => true,
        _ => !CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase),
    };

    public static string L(string zh, string en) => English ? en : zh;
}

internal static class Fmt
{
    public static string Bytes(double v)
    {
        if (v < 0) v = 0;
        if (v < 1024) return $"{(long)v} B";
        string[] units = { "KB", "MB", "GB", "TB" };
        double x = v / 1024; int i = 0;
        while (x >= 1024 && i < units.Length - 1) { x /= 1024; i++; }
        string n = x >= 100 ? x.ToString("0", CultureInfo.InvariantCulture)
                 : x >= 10 ? x.ToString("0.0", CultureInfo.InvariantCulture)
                 : x.ToString("0.00", CultureInfo.InvariantCulture);
        return $"{n} {units[i]}";
    }

    public static string Rate(double bps) => Bytes(bps) + "/s";
    public static string Pct(double v, int digits = 0) => (v * 100).ToString(digits == 0 ? "0" : "0." + new string('0', digits), CultureInfo.InvariantCulture) + "%";

    /// Compact rate for the tray: "820K", "1.2M".
    public static string Short(double bps)
    {
        double k = bps / 1024;
        if (k < 1) return "0K";
        if (k < 1000) return k.ToString("0", CultureInfo.InvariantCulture) + "K";
        double m = k / 1024;
        return (m < 10 ? m.ToString("0.0", CultureInfo.InvariantCulture) : m.ToString("0", CultureInfo.InvariantCulture)) + "M";
    }

    public static string DiskShort(long b)
    {
        double gb = b / 1073741824.0;
        return gb >= 100 ? gb.ToString("0", CultureInfo.InvariantCulture) + "G" : gb.ToString("0.0", CultureInfo.InvariantCulture) + "G";
    }

    public static string Relative(DateTime? d)
    {
        if (d is null) return "—";
        var s = DateTime.Now - d.Value;
        if (s.TotalMinutes < 1) return L("刚刚", "just now");
        if (s.TotalHours < 1) return L($"{(int)s.TotalMinutes} 分钟前", $"{(int)s.TotalMinutes} min ago");
        if (s.TotalDays < 1) return L($"{(int)s.TotalHours} 小时前", $"{(int)s.TotalHours} h ago");
        if (s.TotalDays < 30) return L($"{(int)s.TotalDays} 天前", $"{(int)s.TotalDays} days ago");
        if (s.TotalDays < 365) return L($"{(int)(s.TotalDays / 30)} 个月前", $"{(int)(s.TotalDays / 30)} months ago");
        return L($"{(int)(s.TotalDays / 365)} 年前", $"{(int)(s.TotalDays / 365)} years ago");
    }

    public static int DaysAgo(DateTime? d) => d is null ? 0 : (int)(DateTime.Now - d.Value).TotalDays;

    public static string Uptime()
    {
        var t = TimeSpan.FromMilliseconds(Native.GetTickCount64());
        if (t.TotalDays >= 1) return L($"{(int)t.TotalDays} 天 {t.Hours} 小时", $"{(int)t.TotalDays} d {t.Hours} h");
        if (t.TotalHours >= 1) return L($"{t.Hours} 小时 {t.Minutes} 分", $"{t.Hours} h {t.Minutes} min");
        return L($"{t.Minutes} 分钟", $"{t.Minutes} min");
    }

    public static string Tilde(string path)
    {
        var home = Paths.Home;
        return path.StartsWith(home, StringComparison.OrdinalIgnoreCase) ? "~" + path[home.Length..] : path;
    }
}

internal static class Paths
{
    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public static string RoamingAppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    public static string Downloads => Path.Combine(Home, "Downloads");
    public static string H(string rel) => Path.Combine(Home, rel);

    public static string Support
    {
        get
        {
            var d = Path.Combine(LocalAppData, "Peekaboo");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    public static string SystemDrive => Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
}

internal static class FileSizer
{
    static readonly EnumerationOptions Opts = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        ReturnSpecialDirectories = false,
    };

    public static long Size(string path, Func<bool>? cancel = null)
    {
        try
        {
            if (File.Exists(path)) return new FileInfo(path).Length;
            if (!Directory.Exists(path)) return 0;
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return 0;
            long total = 0; int n = 0;
            foreach (var f in new DirectoryInfo(path).EnumerateFiles("*", Opts))
            {
                if ((++n & 0xFFF) == 0 && cancel?.Invoke() == true) break;
                try { total += f.Length; } catch { }
            }
            return total;
        }
        catch { return 0; }
    }

    public static DateTime? Modified(string path)
    {
        try { return Directory.Exists(path) ? Directory.GetLastWriteTime(path) : File.GetLastWriteTime(path); }
        catch { return null; }
    }
}
