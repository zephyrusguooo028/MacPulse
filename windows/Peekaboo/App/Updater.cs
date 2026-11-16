using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace Peekaboo;

/// Checks GitHub Releases for a newer version and replaces the running exe.
/// This is the only network access in Peekaboo; it can be turned off in Settings.
public static class Updater
{
    const string Repo = "zephyrusguooo028/Peekaboo";
    public enum Status { Idle, Checking, UpToDate, Available, Downloading, Installing, Failed }

    public static Status State { get; private set; } = Status.Idle;
    public static string? NewVersion { get; private set; }
    public static string Notes { get; private set; } = "";
    public static string? Error { get; private set; }
    public static double Progress { get; private set; }
    public static event Action? Changed;

    static string? _url, _sha;
    static System.Windows.Threading.DispatcherTimer? _timer;
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    public static string Current => (typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0)).ToString(3);

    static void Set(Status s) { State = s; UiThread.Run(() => Changed?.Invoke()); }

    public static void Start()
    {
        // Clean up the previous exe left behind by an update.
        try { var old = Environment.ProcessPath + ".old"; if (File.Exists(old)) File.Delete(old); } catch { }
        if (!Settings.Shared.AutoUpdate) return;
        _timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromHours(24) };
        _timer.Tick += async (_, _) => await Check(silent: true);
        _timer.Start();
        if (Settings.Shared.LastUpdateCheck is not { } last || (DateTime.Now - last).TotalHours > 24)
            Task.Delay(10_000).ContinueWith(_ => Check(silent: true));
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

    public static async Task Check(bool silent = false)
    {
        if (State is Status.Downloading or Status.Installing) return;
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
            Set(Status.Available);
        }
        catch (Exception e)
        {
            Error = L($"检查失败：{e.Message}", $"Check failed: {e.Message}");
            Set(silent ? Status.Idle : Status.Failed);
        }
    }

    /// Downloads and verifies the new exe, swaps it in and restarts.
    public static async Task Install()
    {
        if (_url is null) return;
        var exe = Environment.ProcessPath!;
        var tmp = Path.Combine(Path.GetTempPath(), "Peekaboo-update.exe");
        try
        {
            Set(Status.Downloading);
            using (var resp = await Http.GetAsync(_url, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? 0, done = 0;
                await using var src = await resp.Content.ReadAsStreamAsync();
                await using var dst = File.Create(tmp);
                var buf = new byte[1 << 16];
                int n;
                while ((n = await src.ReadAsync(buf)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n));
                    done += n;
                    if (total > 0) { Progress = (double)done / total; UiThread.Run(() => Changed?.Invoke()); }
                }
            }
            if (_sha is not null)
            {
                await using var f = File.OpenRead(tmp);
                var got = Convert.ToHexString(await SHA256.HashDataAsync(f)).ToLowerInvariant();
                if (got != _sha.ToLowerInvariant()) throw new Exception(L("校验失败，下载的文件不完整", "Checksum mismatch, download corrupted"));
            }
            Set(Status.Installing);
            // A running exe can be renamed but not overwritten: move it aside, put the new one in place.
            var old = exe + ".old";
            if (File.Exists(old)) File.Delete(old);
            File.Move(exe, old);
            try { File.Move(tmp, exe); }
            catch { File.Move(old, exe); throw; }
            Process.Start(new ProcessStartInfo(exe, "--background") { UseShellExecute = true });
            UiThread.Run(() => System.Windows.Application.Current.Shutdown());
        }
        catch (UnauthorizedAccessException)
        {
            Error = L("没有权限替换当前程序（可能在 Program Files 里）。已下载到临时文件夹，请手动替换。", "No permission to replace the running exe (it may be in Program Files). The new version was downloaded to your Temp folder; replace it manually.");
            Set(Status.Failed);
            UIX.Reveal(tmp);
        }
        catch (Exception e)
        {
            Error = L($"更新失败：{e.Message}", $"Update failed: {e.Message}");
            Set(Status.Failed);
        }
    }
}
