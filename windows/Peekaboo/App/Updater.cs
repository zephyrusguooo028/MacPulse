using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Threading;

namespace Peekaboo;

/// Checks GitHub Releases for a newer version and replaces the running exe.
/// This is the only network access in Peekaboo; it can be turned off in Settings.
/// With "install automatically" on, a silent check downloads the new exe into
/// %LocalAppData%\Peekaboo\Updates (status Ready) and swaps it in once the user has been
/// idle for a while, or when Peekaboo quits.
public static class Updater
{
    const string Repo = "zephyrusguooo028/Peekaboo";
    public enum Status { Idle, Checking, UpToDate, Available, Downloading, Ready, Installing, Failed }

    public static Status State { get; private set; } = Status.Idle;
    public static string? NewVersion { get; private set; }
    public static string Notes { get; private set; } = "";
    public static string? Error { get; private set; }
    public static double Progress { get; private set; }
    public static event Action? Changed;

    /// How long the user must be away before a Ready update installs itself.
    public static readonly TimeSpan IdleBeforeInstall = TimeSpan.FromMinutes(15);

    static string? _url, _sha, _staged;
    static DispatcherTimer? _timer, _idleTimer;
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    public static string Current => (typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0)).ToString(3);

    static string UpdatesDir => Path.Combine(Paths.Support, "Updates");
    static bool AutoInstall => Settings.Shared.AutoUpdate && Settings.Shared.AutoInstallUpdate;

    static void Set(Status s) { State = s; UiThread.Run(() => Changed?.Invoke()); }

    public static void Start()
    {
        // Clean up the previous exe left behind by an update, and any staged download.
        try { var old = Environment.ProcessPath + ".old"; if (File.Exists(old)) File.Delete(old); } catch { }
        try { if (Directory.Exists(UpdatesDir)) foreach (var f in Directory.GetFiles(UpdatesDir)) File.Delete(f); } catch { }
        AnnounceInstalled();
        if (!Settings.Shared.AutoUpdate) return;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromHours(24) };
        _timer.Tick += async (_, _) => await Check(silent: true);
        _timer.Start();
        if (Settings.Shared.LastUpdateCheck is not { } last || (DateTime.Now - last).TotalHours > 24)
            Task.Delay(10_000).ContinueWith(_ => Check(silent: true));
    }

    /// One balloon after restarting into a version the updater put in place.
    static void AnnounceInstalled()
    {
        var s = Settings.Shared;
        if (s.UpdatedTo is not { } v) return;
        s.UpdatedTo = null; s.Save();
        if (Newer(v, Current)) return; // the swap did not take; stay quiet
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            // A balloon needs a non-empty body, so the message goes there under the app name.
            TrayController.Shared.Notify("Peekaboo", L($"已更新到 Peekaboo {Current}", $"Updated to Peekaboo {Current}"), Page.Settings);
        };
        t.Start();
    }

    public static bool Newer(string a, string b)
    {
        static int[] P(string s) => s.TrimStart('v', 'V').Split('.').Select(x => int.TryParse(x, out var n) ? n : 0).ToArray();
        var x = P(a); var y = P(b);
        for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            int p = i < x.Length ? x[i] : 0, q = i < y.Length ? y[i] : 0;
            if (p != q) return p > q;
        }
        return false;
    }

    /// True when a silent check should show a balloon for this version (once per version).
    public static bool ShouldNotify(string version, string? lastNotified) =>
        !string.Equals(version.TrimStart('v', 'V'), lastNotified?.TrimStart('v', 'V'), StringComparison.OrdinalIgnoreCase);

    public static async Task Check(bool silent = false)
    {
        if (State is Status.Downloading or Status.Ready or Status.Installing) return;
        Set(Status.Checking);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
            req.Headers.UserAgent.ParseAdd("Peekaboo-Updater");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var resp = await Http.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? "0";
            UiThread.Run(() => { Settings.Shared.LastUpdateCheck = DateTime.Now; Settings.Shared.Save(); });
            JsonElement? exe = null;
            foreach (var a in root.GetProperty("assets").EnumerateArray())
                if (a.GetProperty("name").GetString()?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true) { exe = a; break; }
            if (!Newer(tag, Current) || exe is null) { Set(Status.UpToDate); return; }
            _url = exe.Value.GetProperty("browser_download_url").GetString();
            _sha = exe.Value.TryGetProperty("digest", out var d) && d.GetString() is { } dg && dg.StartsWith("sha256:") ? dg[7..] : null;
            NewVersion = tag.TrimStart('v', 'V');
            Notes = string.Join("\n", (root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "").Split('\n').Take(12));
        }
        catch (Exception e)
        {
            Error = L($"检查失败：{e.Message}", $"Check failed: {e.Message}");
            Set(silent ? Status.Idle : Status.Failed);
            return;
        }

        if (!silent) { Set(Status.Available); return; }
        var v = NewVersion!;
        if (AutoInstall && await Download(silent: true) is not null)
        {
            Set(Status.Ready);
            UiThread.Run(() =>
            {
                StartIdleTimer();
                NotifyOnce(v, L($"Peekaboo {v} 已下载", $"Peekaboo {v} downloaded"),
                              L("离开电脑一会儿或退出 Peekaboo 时会自动安装。", "It installs by itself when you're away for a while or quit Peekaboo."));
            });
            return;
        }
        Set(Status.Available);
        UiThread.Run(() => NotifyOnce(v, L($"Peekaboo {v} 可以更新", $"Peekaboo {v} is available"),
                                         L("点这里查看并安装。", "Click to review and install.")));
    }

    static void NotifyOnce(string version, string title, string body)
    {
        var s = Settings.Shared;
        if (!ShouldNotify(version, s.LastNotifiedUpdate)) return;
        s.LastNotifiedUpdate = version; s.Save();
        TrayController.Shared.Notify(title, body, Page.Settings);
    }

    /// Settings toggles changed: a Ready update stops installing itself once auto-install is off
    /// (the staged file is kept, so "Download and install" reuses it).
    public static void SettingsChanged()
    {
        if (State == Status.Ready && !AutoInstall) { _idleTimer?.Stop(); Set(Status.Available); }
    }

    /// Manual install ("Download and install" / "Install now"): download if needed, swap, restart.
    public static async Task Install()
    {
        if (_url is null) return;
        _idleTimer?.Stop();
        var file = await Download(silent: false);
        if (file is not null) Swap(file, restart: true, silent: false);
    }

    /// Downloads and verifies the new exe into the Updates folder. Returns the staged path,
    /// or null on failure (status Failed when not silent; the caller decides otherwise).
    static async Task<string?> Download(bool silent)
    {
        if (_url is null || NewVersion is null) return null;
        var staged = Path.Combine(UpdatesDir, $"Peekaboo-{NewVersion}.exe");
        try
        {
            Directory.CreateDirectory(UpdatesDir);
            // Reuse a download of the same version made earlier in this run (already verified),
            // or one whose checksum still matches.
            if (File.Exists(staged) && (staged == _staged || (_sha is not null && await Verified(staged)))) return _staged = staged;
            Set(Status.Downloading);
            Progress = 0;
            var part = staged + ".part";
            using (var resp = await Http.GetAsync(_url, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? 0, done = 0;
                await using var src = await resp.Content.ReadAsStreamAsync();
                await using var dst = File.Create(part);
                var buf = new byte[1 << 16];
                int n;
                while ((n = await src.ReadAsync(buf)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n));
                    done += n;
                    if (total > 0) { Progress = (double)done / total; UiThread.Run(() => Changed?.Invoke()); }
                }
            }
            if (_sha is not null && !await Verified(part))
            {
                File.Delete(part);
                throw new Exception(L("校验失败，下载的文件不完整", "Checksum mismatch, download corrupted"));
            }
            File.Move(part, staged, overwrite: true);
            return _staged = staged;
        }
        catch (Exception e)
        {
            Error = L($"更新失败：{e.Message}", $"Update failed: {e.Message}");
            if (!silent) Set(Status.Failed);
            return null;
        }
    }

    static async Task<bool> Verified(string file)
    {
        await using var f = File.OpenRead(file);
        var got = Convert.ToHexString(await SHA256.HashDataAsync(f)).ToLowerInvariant();
        return got == _sha!.ToLowerInvariant();
    }

    /// Puts the staged exe in place of the running one; optionally restarts with --background.
    /// Must run on the UI thread.
    static bool Swap(string staged, bool restart, bool silent)
    {
        var exe = Environment.ProcessPath!;
        var s = Settings.Shared;
        try
        {
            Set(Status.Installing);
            s.UpdatedTo = NewVersion; s.Save();
            // A running exe can be renamed but not overwritten: move it aside, put the new one in place.
            var old = exe + ".old";
            if (File.Exists(old)) File.Delete(old);
            File.Move(exe, old);
            try { File.Move(staged, exe); }
            catch { File.Move(old, exe); throw; }
            if (restart)
            {
                Process.Start(new ProcessStartInfo(exe, "--background") { UseShellExecute = true });
                System.Windows.Application.Current.Shutdown();
            }
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            s.UpdatedTo = null; s.Save();
            Error = L("没有权限替换当前程序（可能在 Program Files 里）。新版本已下载，请手动替换。", "No permission to replace the running exe (it may be in Program Files). The new version was downloaded; replace it manually.");
            Set(Status.Failed);
            if (!silent) UIX.Reveal(staged);
        }
        catch (Exception e)
        {
            s.UpdatedTo = null; s.Save();
            Error = L($"更新失败：{e.Message}", $"Update failed: {e.Message}");
            Set(Status.Failed);
        }
        return false;
    }

    // ---- install while idle / on quit

    static void StartIdleTimer()
    {
        if (_idleTimer is null)
        {
            _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
            _idleTimer.Tick += (_, _) => IdleTick();
        }
        _idleTimer.Start();
    }

    static void IdleTick()
    {
        if (State != Status.Ready || !AutoInstall || _staged is null) { _idleTimer?.Stop(); return; }
        if (IdleTime() < IdleBeforeInstall || Busy()) return;
        _idleTimer?.Stop();
        Swap(_staged, restart: true, silent: true);
    }

    /// Called when Peekaboo quits or the Windows session ends: swap in a Ready update, no restart.
    public static void InstallOnExit()
    {
        if (State != Status.Ready || !AutoInstall || _staged is null || !File.Exists(_staged)) return;
        _idleTimer?.Stop();
        Swap(_staged, restart: false, silent: true);
    }

    /// Something the user is in the middle of that a restart would throw away.
    static bool Busy() =>
        System.Windows.Interop.ComponentDispatcher.IsThreadModal
        || Screenshot.InProgress
        || System.Windows.Application.Current?.Windows.OfType<OcrWindow>().Any() == true;

    [StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public uint cbSize, dwTime; }
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    /// Milliseconds between two GetTickCount-style readings, correct across the 49.7-day wrap.
    public static uint IdleMs(uint now, uint lastInput) => unchecked(now - lastInput);

    public static TimeSpan IdleTime()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
        return TimeSpan.FromMilliseconds(IdleMs(unchecked((uint)Environment.TickCount), info.dwTime));
    }
}
