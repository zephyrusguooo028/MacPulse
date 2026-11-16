using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Peekaboo;

public sealed class InstalledEntry
{
    public required string Name { get; init; }
    public string Publisher = "", Version = "", InstallLocation = "", UninstallString = "", QuietUninstallString = "";
    public string? IconPath;
    public long Size;            // bytes, from EstimatedSize
    public DateTime? InstallDate;
    public bool WindowsInstaller;
    public required RegistryHive Hive { get; init; }
    public required RegistryView View { get; init; }
    public required string KeyPath { get; init; }   // full subkey path under the hive
    public required string KeyName { get; init; }
    /// Why Peekaboo won't offer to uninstall it (runtimes, drivers, Windows parts), or null.
    public string? Protected;
    public string Id => $"{Hive}|{View}|{KeyPath}";
}

public sealed record UninstallLeftover(string Path, string Kind, long Size, bool Recommended, string? Note);

/// Reads the Windows "Programs and Features" list and finds what uninstallers leave behind.
public static class Uninstaller
{
    const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    const string UninstallKey32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    static readonly Regex RuntimeRx = new(@"Visual C\+\+.*(Redistributable|Runtime)|\.NET (Runtime|Desktop Runtime|Host|Framework|Core|SDK|Targeting Pack|AppHost)|Windows Desktop Runtime|ASP\.NET Core|WebView2 Runtime|Windows App Runtime|WindowsAppRuntime|DirectX|Visual Studio .*Redistributable|VC_?Redist|Microsoft Update Health Tools|Windows PC Health Check", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex DriverRx = new(@"\bdrivers?\b|驱动|chipset|\bfirmware\b|Intel\(R\) (Management Engine|Graphics|Wireless|Serial IO|Rapid Storage|Bluetooth)|Realtek .*(Audio|Ethernet|Card Reader|Bluetooth)|NVIDIA (Graphics|HD Audio|PhysX|USBC|FrameView)|AMD (Chipset|Software)|Synaptics|Precision Touchpad", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex WindowsRx = new(@"^(Microsoft )?Windows\b|^Update for |^Security Update|^Hotfix|^KB\d{6,}", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static List<InstalledEntry> Read()
    {
        var list = new List<InstalledEntry>();
        // Explicit views make this independent of whether Peekaboo runs as 32- or 64-bit.
        ReadRoot(list, RegistryHive.LocalMachine, RegistryView.Registry64, UninstallKey);
        if (Environment.Is64BitOperatingSystem) ReadRoot(list, RegistryHive.LocalMachine, RegistryView.Registry64, UninstallKey32);
        ReadRoot(list, RegistryHive.CurrentUser, RegistryView.Registry64, UninstallKey);
        if (Environment.Is64BitOperatingSystem) ReadRoot(list, RegistryHive.CurrentUser, RegistryView.Registry64, UninstallKey32);
        // The same product can be listed twice (per-user + per-machine, or both views).
        return list.GroupBy(e => (e.Name.ToLowerInvariant(), e.Version.ToLowerInvariant())).Select(g => g.First())
                   .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    static void ReadRoot(List<InstalledEntry> list, RegistryHive hive, RegistryView view, string path)
    {
        try
        {
            using var bk = RegistryKey.OpenBaseKey(hive, view);
            using var root = bk.OpenSubKey(path);
            if (root is null) return;
            foreach (var name in root.GetSubKeyNames())
            {
                try
                {
                    using var k = root.OpenSubKey(name);
                    if (k is null) continue;
                    var display = (k.GetValue("DisplayName") as string)?.Trim();
                    if (string.IsNullOrEmpty(display)) continue;
                    if (Convert.ToInt32(k.GetValue("SystemComponent") ?? 0) == 1) continue;
                    if (k.GetValue("ParentKeyName") is not null) continue;
                    var release = k.GetValue("ReleaseType") as string ?? "";
                    if (release.Contains("Update", StringComparison.OrdinalIgnoreCase) || release.Contains("Hotfix", StringComparison.OrdinalIgnoreCase)) continue;
                    if (Regex.IsMatch(name, @"^KB\d+$", RegexOptions.IgnoreCase)) continue;

                    var e = new InstalledEntry
                    {
                        Name = display, Hive = hive, View = view, KeyPath = path + "\\" + name, KeyName = name,
                        Publisher = (k.GetValue("Publisher") as string ?? "").Trim(),
                        Version = (k.GetValue("DisplayVersion") as string ?? "").Trim(),
                        InstallLocation = (k.GetValue("InstallLocation") as string ?? "").Trim().Trim('"'),
                        UninstallString = (k.GetValue("UninstallString") as string ?? "").Trim(),
                        QuietUninstallString = (k.GetValue("QuietUninstallString") as string ?? "").Trim(),
                        WindowsInstaller = Convert.ToInt32(k.GetValue("WindowsInstaller") ?? 0) == 1,
                    };
                    if (k.GetValue("EstimatedSize") is int kb && kb > 0) e.Size = kb * 1024L;
                    if (k.GetValue("InstallDate") is string d && DateTime.TryParseExact(d.Trim(), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)) e.InstallDate = dt;
                    e.IconPath = IconFile(k.GetValue("DisplayIcon") as string);
                    if (IsSelf(e)) continue;
                    e.Protected = Classify(e);
                    list.Add(e);
                }
                catch { }
            }
        }
        catch { }
    }

    /// "C:\x\app.exe,0" or "\"C:\x\app.exe\"" → the file, if it exists.
    static string? IconFile(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        var s = Environment.ExpandEnvironmentVariables(v.Trim());
        if (s.StartsWith('"')) { int end = s.IndexOf('"', 1); if (end > 1) s = s[1..end]; }
        else { int comma = s.LastIndexOf(','); if (comma > 2 && int.TryParse(s[(comma + 1)..].Trim(), out _)) s = s[..comma]; }
        try { return File.Exists(s) ? s : null; } catch { return null; }
    }

    static bool IsSelf(InstalledEntry e)
    {
        if (e.Name.Contains("Peekaboo", StringComparison.OrdinalIgnoreCase)) return true;
        var me = AppContext.BaseDirectory.TrimEnd('\\') + "\\";
        var loc = e.InstallLocation.TrimEnd('\\');
        if (loc.Length <= 3 || loc.Equals(Paths.Home.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return false;
        return me.StartsWith(loc + "\\", StringComparison.OrdinalIgnoreCase);
    }

    static string? Classify(InstalledEntry e)
    {
        if (RuntimeRx.IsMatch(e.Name)) return L("这是很多应用都依赖的运行库，卸载后其他程序可能打不开。Peekaboo 不提供卸载。", "This is a runtime many apps depend on; removing it can break other programs. Peekaboo doesn't offer to uninstall it.");
        if (DriverRx.IsMatch(e.Name)) return L("这是硬件驱动，请在设备管理器或厂商工具里管理。", "This is a hardware driver; manage it in Device Manager or the vendor's tool.");
        bool microsoft = e.Publisher.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase);
        if (microsoft && WindowsRx.IsMatch(e.Name)) return L("这是 Windows 的组成部分，请在 Windows 设置里管理。", "This is part of Windows; manage it in Windows Settings.");
        return null;
    }

    public static bool StillInstalled(InstalledEntry e)
    {
        try
        {
            using var bk = RegistryKey.OpenBaseKey(e.Hive, e.View);
            using var k = bk.OpenSubKey(e.KeyPath);
            return k is not null;
        }
        catch { return false; }
    }

    /// Turns an UninstallString into something Process.Start can run. msiexec "/I" (which
    /// opens the repair/modify UI) becomes "/X" (remove).
    public static ProcessStartInfo? StartInfo(InstalledEntry e)
    {
        var cmd = Environment.ExpandEnvironmentVariables(e.UninstallString.Length > 0 ? e.UninstallString : e.QuietUninstallString).Trim();
        var guidRx = new Regex(@"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}");
        bool isMsi = Regex.IsMatch(cmd, @"(^|[\\""\s])msiexec(\.exe)?([""\s]|$)", RegexOptions.IgnoreCase);
        if (isMsi || (cmd.Length == 0 && e.WindowsInstaller))
        {
            var m = guidRx.Match(cmd);
            var code = m.Success ? m.Value : guidRx.IsMatch(e.KeyName) ? e.KeyName : null;
            return code is null ? null : new ProcessStartInfo("msiexec.exe", $"/X{code}") { UseShellExecute = true };
        }
        if (cmd.Length == 0) return null;
        string file, args;
        if (cmd.StartsWith('"'))
        {
            int end = cmd.IndexOf('"', 1);
            if (end < 0) return null;
            file = cmd[1..end]; args = cmd[(end + 1)..].Trim();
        }
        else
        {
            // Unquoted paths may contain spaces: "C:\Program Files\Foo\uninst.exe /S".
            int exe = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exe > 0 && (exe + 4 == cmd.Length || cmd[exe + 4] == ' ')) { file = cmd[..(exe + 4)]; args = cmd[(exe + 4)..].Trim(); }
            else { int sp = cmd.IndexOf(' '); file = sp < 0 ? cmd : cmd[..sp]; args = sp < 0 ? "" : cmd[(sp + 1)..].Trim(); }
        }
        return new ProcessStartInfo(file, args) { UseShellExecute = true, WorkingDirectory = SafeDir(file) };
    }

    static string SafeDir(string file)
    {
        try { var d = Path.GetDirectoryName(file); return d is not null && Directory.Exists(d) ? d : Environment.SystemDirectory; }
        catch { return Environment.SystemDirectory; }
    }

    // MARK: Leftovers

    static readonly HashSet<string> GenericNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "microsoft", "windows", "microsoft corporation", "google", "google llc", "apple", "intel", "nvidia", "amd", "packages", "programs", "temp", "cache", "common files",
        "app", "apps", "bin", "x64", "x86", "data", "update", "updater", "installer", "setup", "helper", "launcher", "tools", "software", "peekaboo", "package cache", "crashdumps", "d3dscache",
    };

    static readonly string[] ProtectedProgramData = { "Microsoft", "Package Cache", "Packages", "USOShared", "USOPrivate", "ssh", "regid.1991-06.com.microsoft", "WindowsHolographicDevices", "Windows App Certification Kit", "Microsoft OneDrive", "Peekaboo" };
    static readonly string[] ProtectedProgramFiles = { "Common Files", "WindowsApps", "ModifiableWindowsApps", "Windows Defender", "Windows Defender Advanced Threat Protection", "Windows Mail", "Windows Media Player", "Windows NT", "Windows Photo Viewer", "Windows Portable Devices", "Windows Security", "WindowsPowerShell", "Windows Sidebar", "Internet Explorer", "Microsoft", "Microsoft Office", "Microsoft Update Health Tools", "dotnet", "Reference Assemblies", "MSBuild", "Uninstall Information" };

    static string ProgramData => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    static IEnumerable<string> ProgramFilesRoots => new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }.Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);

    /// Only folders inside AppData (checked by CleanupExecutor.IsSafeTarget), one level or
    /// more inside ProgramData, or an app folder inside Program Files. Never the roots or
    /// Windows / Microsoft folders, and never whitelisted paths.
    public static bool SafeLeftover(string path)
    {
        string full;
        try { full = Path.GetFullPath(path).TrimEnd('\\'); } catch { return false; }
        if (Settings.Shared.IsWhitelisted(full)) return false;
        if (full.StartsWith(Paths.Support.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return false;
        if (full.StartsWith(Paths.Home.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            return CleanupExecutor.IsSafeTarget(full) && full.Contains("\\AppData\\", StringComparison.OrdinalIgnoreCase);
        bool Under(string root, string[] blocked)
        {
            var r = root.TrimEnd('\\');
            if (r.Length < 4 || !full.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase)) return false;
            var first = full[(r.Length + 1)..].Split('\\')[0];
            return !blocked.Any(b => first.Equals(b, StringComparison.OrdinalIgnoreCase)) && !first.StartsWith("Windows", StringComparison.OrdinalIgnoreCase) && !first.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase);
        }
        return Under(ProgramData, ProtectedProgramData) || ProgramFilesRoots.Any(r => Under(r, ProtectedProgramFiles));
    }

    static string Stem(string name)
    {
        // "Foo Bar 2.3.1 (x64)" → "Foo Bar"
        var s = name;
        for (int i = 0; i < 3; i++)
            s = Regex.Replace(s, @"\s*[\(\[]?\s*(x64|x86|64-bit|32-bit|amd64|arm64|v?\d+(\.\d+)+|\d{4})\s*[\)\]]?\s*$", "", RegexOptions.IgnoreCase).Trim();
        return s.Trim(' ', '-', '_');
    }

    static string Company(string publisher) =>
        Regex.Replace(publisher, @"[,.]?\s*(Inc\.?|LLC|Ltd\.?|Limited|Corporation|Corp\.?|GmbH|Co\.,? ?Ltd\.?|S\.A\.|AG|B\.V\.|Pty|有限公司|股份有限公司)\s*$", "", RegexOptions.IgnoreCase).Trim(' ', ',', '.');

    /// Folders in AppData, LocalAppData\Programs and ProgramData named after the app (ticked)
    /// or its publisher (not ticked: they may hold the vendor's other apps), plus the
    /// install folder if the uninstaller left it behind.
    public static List<UninstallLeftover> FindLeftovers(InstalledEntry e, IReadOnlyCollection<InstalledEntry> others)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in new[] { e.Name, Stem(e.Name) }) if (n.Length >= 3 && !GenericNames.Contains(n)) names.Add(n);
        if (e.InstallLocation.Length > 3)
        {
            var leaf = Path.GetFileName(e.InstallLocation.TrimEnd('\\'));
            if (leaf.Length >= 3 && !GenericNames.Contains(leaf)) names.Add(leaf);
        }
        var company = Company(e.Publisher);
        bool companyOk = company.Length >= 3 && !GenericNames.Contains(company) && !e.Publisher.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase);

        // Names and folders that belong to other installed programs are left alone.
        var otherNames = new HashSet<string>(others.Where(o => o.Id != e.Id).SelectMany(o => new[] { o.Name, Stem(o.Name) }), StringComparer.OrdinalIgnoreCase);
        var otherDirs = others.Where(o => o.Id != e.Id && o.InstallLocation.Length > 3).Select(o => o.InstallLocation.TrimEnd('\\')).ToList();
        bool UsedByOther(string p) => otherDirs.Any(d => d.Equals(p, StringComparison.OrdinalIgnoreCase) || d.StartsWith(p + "\\", StringComparison.OrdinalIgnoreCase) || p.StartsWith(d + "\\", StringComparison.OrdinalIgnoreCase));
        names.RemoveWhere(n => otherNames.Contains(n) && !n.Equals(e.Name, StringComparison.OrdinalIgnoreCase));

        var roots = new[]
        {
            (Paths.RoamingAppData, "AppData\\Roaming"),
            (Paths.LocalAppData, "AppData\\Local"),
            (Path.Combine(Paths.LocalAppData, "Programs"), "AppData\\Local\\Programs"),
            (ProgramData, "ProgramData"),
        };
        var found = new Dictionary<string, UninstallLeftover>(StringComparer.OrdinalIgnoreCase);
        void Add(string path, string kind, bool rec, string? note)
        {
            path = path.TrimEnd('\\');
            if (found.ContainsKey(path) || UsedByOther(path) || !SafeLeftover(path)) return;
            try { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return; } catch { return; }
            found[path] = new UninstallLeftover(path, kind, FileSizer.Size(path), rec, note);
        }
        foreach (var (root, label) in roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var n in names) { var p = Path.Combine(root, n); if (Directory.Exists(p)) Add(p, label, true, null); }
            if (!companyOk) continue;
            var cp = Path.Combine(root, company);
            if (!Directory.Exists(cp)) continue;
            bool inner = false;
            foreach (var n in names) { var p = Path.Combine(cp, n); if (Directory.Exists(p)) { Add(p, label, true, null); inner = true; } }
            if (!inner) Add(cp, label, false, L("以开发者命名，可能还有它的其他应用的数据，请确认", "Named after the publisher; it may hold data of their other apps, please check"));
        }
        if (e.InstallLocation.Length > 3 && Directory.Exists(e.InstallLocation))
            Add(e.InstallLocation, L("安装目录", "Install folder"), true, null);
        return found.Values.OrderByDescending(l => l.Size).ToList();
    }
}

public sealed class UninstallModel
{
    public static UninstallModel Shared { get; } = new();
    public List<InstalledEntry> Entries = new();
    public bool Loading;
    public string? SelectedId;
    /// Set while the vendor uninstaller runs; cleared when it finishes or the user stops waiting.
    public InstalledEntry? Running;
    public InstalledEntry? LeftoverFor;
    public List<UninstallLeftover> Leftovers = new();
    public HashSet<string> Picked = new(StringComparer.OrdinalIgnoreCase);
    public bool FindingLeftovers, Working;
    public string? LastResult;
    public event Action? Changed;
    CancellationTokenSource? _wait;

    public InstalledEntry? Selected => Entries.FirstOrDefault(e => e.Id == SelectedId);

    public void Load()
    {
        if (Loading) return;
        Loading = true; Changed?.Invoke();
        Task.Run(() =>
        {
            var list = Uninstaller.Read();
            UiThread.Run(() => { Entries = list; Loading = false; Changed?.Invoke(); });
        });
    }

    public void Select(InstalledEntry e)
    {
        SelectedId = e.Id;
        if (LeftoverFor?.Id != e.Id) { LeftoverFor = null; Leftovers = new(); Picked.Clear(); }
        Changed?.Invoke();
    }

    /// Runs the program's own uninstaller, waits for it, then looks for leftovers.
    public void Uninstall(InstalledEntry e)
    {
        if (e.Protected is not null || Running is not null) return;
        var psi = Uninstaller.StartInfo(e);
        if (psi is null)
        {
            UIX.Info(L("找不到卸载程序", "No uninstaller found"), L($"{e.Name} 没有登记卸载命令。可以在 Windows 设置 → 应用 里试试。", $"{e.Name} has no registered uninstall command. Try Windows Settings → Apps."));
            return;
        }
        if (!UIX.Confirm(L($"卸载 {e.Name}？", $"Uninstall {e.Name}?"),
                L($"会打开 {e.Name} 自带的卸载程序（可能弹出管理员权限提示），请按它的提示完成卸载。\n\n命令：{psi.FileName} {psi.Arguments}\n\n卸载结束后，Peekaboo 会查找它留下的文件，由你决定是否移到回收站。",
                  $"{e.Name}'s own uninstaller will open (you may see an administrator prompt). Follow its steps to finish.\n\nCommand: {psi.FileName} {psi.Arguments}\n\nAfterwards Peekaboo looks for files it left behind, and you decide whether to move them to the Recycle Bin."))) return;
        Process? proc;
        try { proc = Process.Start(psi); }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            LastResult = L("已取消（没有允许管理员权限）", "Canceled (administrator prompt was declined)"); Changed?.Invoke(); return;
        }
        catch (Exception ex)
        {
            UIX.Info(L("没能启动卸载程序", "Couldn't start the uninstaller"), ex.Message); return;
        }
        Running = e; LastResult = null;
        var cts = new CancellationTokenSource();
        _wait = cts;
        Changed?.Invoke();
        Task.Run(async () =>
        {
            try
            {
                if (proc is not null) await proc.WaitForExitAsync(cts.Token);
                // Many uninstallers hand off to a copy of themselves and exit at once, so also
                // wait (up to 10 minutes) for the program to drop out of the registry.
                var until = DateTime.Now.AddMinutes(10);
                while (DateTime.Now < until && Uninstaller.StillInstalled(e)) await Task.Delay(2000, cts.Token);
            }
            catch (OperationCanceledException) { }
            catch { }
            UiThread.Run(() => { if (_wait == cts) Finished(e); });
        });
    }

    /// The user says the uninstaller is done (or gave up): stop waiting and look for leftovers.
    public void StopWaiting()
    {
        if (Running is { } e) { _wait?.Cancel(); _wait = null; Finished(e); }
    }

    void Finished(InstalledEntry e)
    {
        _wait = null;
        Running = null;
        bool gone = !Uninstaller.StillInstalled(e);
        if (gone) Entries.RemoveAll(x => x.Id == e.Id);
        LastResult = gone ? L($"{e.Name} 已卸载", $"{e.Name} was uninstalled") : L($"{e.Name} 似乎还在（卸载可能被取消了）", $"{e.Name} still seems to be installed (the uninstall may have been canceled)");
        if (gone) FindLeftovers(e);
        Changed?.Invoke();
    }

    public void FindLeftovers(InstalledEntry e)
    {
        LeftoverFor = e; SelectedId = e.Id;
        FindingLeftovers = true; Leftovers = new(); Picked.Clear();
        Changed?.Invoke();
        var others = Entries.ToList();
        Task.Run(() =>
        {
            var found = Uninstaller.FindLeftovers(e, others);
            UiThread.Run(() =>
            {
                if (LeftoverFor?.Id != e.Id) return;
                Leftovers = found;
                Picked = new HashSet<string>(found.Where(l => l.Recommended).Select(l => l.Path), StringComparer.OrdinalIgnoreCase);
                FindingLeftovers = false;
                Changed?.Invoke();
            });
        });
    }

    public void RecycleLeftovers()
    {
        var list = Leftovers.Where(l => Picked.Contains(l.Path) && Uninstaller.SafeLeftover(l.Path)).ToList();
        if (list.Count == 0) return;
        long size = list.Sum(l => l.Size);
        var preview = string.Join("\n", list.Take(12).Select(l => $"• {Fmt.Tilde(l.Path)}  {Fmt.Bytes(l.Size)}")) + (list.Count > 12 ? L($"\n…等 {list.Count} 项", $"\n…{list.Count} items in total") : "");
        if (!UIX.Confirm(L($"把 {list.Count} 项残留（{Fmt.Bytes(size)}）移到回收站？", $"Move {list.Count} leftovers ({Fmt.Bytes(size)}) to the Recycle Bin?"),
                         preview + L("\n\n可以在回收站里恢复。", "\n\nYou can restore them from the Recycle Bin."))) return;
        Working = true; Changed?.Invoke();
        Task.Run(() =>
        {
            var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var failures = new List<(string, string)>();
            foreach (var l in list)
            {
                var err = Native.Recycle(l.Path);
                if (err is null && !Directory.Exists(l.Path) && !File.Exists(l.Path)) done.Add(l.Path);
                else failures.Add((l.Path, err ?? L("没有权限（ProgramData 和 Program Files 里的文件夹可能需要以管理员身份运行 Peekaboo）", "Access denied (folders in ProgramData and Program Files may need Peekaboo to run as administrator)")));
            }
            UiThread.Run(() =>
            {
                Working = false;
                long freed = list.Where(l => done.Contains(l.Path)).Sum(l => l.Size);
                Leftovers.RemoveAll(l => done.Contains(l.Path));
                Picked.ExceptWith(done);
                LastResult = L($"已把 {done.Count} 项残留（{Fmt.Bytes(freed)}）移到回收站", $"Moved {done.Count} leftovers ({Fmt.Bytes(freed)}) to the Recycle Bin");
                Changed?.Invoke();
                if (failures.Count > 0)
                    UIX.Info(L($"有 {failures.Count} 项没能移走", $"{failures.Count} items couldn't be moved"), string.Join("\n", failures.Take(8).Select(f => $"{Fmt.Tilde(f.Item1)}: {f.Item2}")));
            });
        });
    }
}
