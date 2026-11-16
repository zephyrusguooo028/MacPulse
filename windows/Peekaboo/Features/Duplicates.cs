using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Peekaboo;

public sealed record DupFile(string Path, long Size, DateTime Modified)
{
    public bool IsImage => DupEngine.ImageExt.Contains(System.IO.Path.GetExtension(Path).ToLowerInvariant());
}

public enum DupKind { Exact, Similar }

public sealed class DupGroup
{
    public required string Id { get; init; }
    public required DupKind Kind { get; init; }
    public required List<DupFile> Files { get; init; }
    public long Total => Files.Sum(f => f.Size);
    /// Exact copies: every copy but one. Similar images: everything but the largest.
    public long Reclaimable => Files.Count < 2 ? 0 : Kind == DupKind.Exact ? Files[0].Size * (Files.Count - 1) : Total - Files.Max(f => f.Size);
}

/// Finds exact duplicates (size → first 64 KB → full SHA-256) and similar images
/// (dHash, Hamming distance ≤ 5). Read-only.
public sealed class DupEngine
{
    public static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".heic", ".webp", ".gif", ".bmp", ".tiff", ".tif" };
    static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase) { "node_modules", ".git", ".svn", ".hg", "$RECYCLE.BIN", "System Volume Information" };
    const int PartialBytes = 64 * 1024;
    const int Threshold = 5;
    const int Workers = 4;
    // OneDrive / cloud placeholders: reading them would download the file.
    const FileAttributes RecallOnOpen = (FileAttributes)0x40000, RecallOnDataAccess = (FileAttributes)0x400000;

    readonly CancellationTokenSource _cts = new();
    public bool Cancelled => _cts.IsCancellationRequested;
    public void Cancel() => _cts.Cancel();

    public volatile string Phase = "";
    int _done, _total;
    public (string phase, int done, int total) Progress => (Phase, Volatile.Read(ref _done), Volatile.Read(ref _total));
    void SetPhase(string p, int total) { Phase = p; Volatile.Write(ref _done, 0); Volatile.Write(ref _total, total); }
    void Tick() => Interlocked.Increment(ref _done);

    public sealed class Result
    {
        public List<DupGroup> Exact = new(), Similar = new();
        public int Scanned;
    }

    public Result Run(IEnumerable<string> roots, long minSize)
    {
        var r = new Result();
        try
        {
            var files = Collect(roots, minSize);
            r.Scanned = files.Count;
            if (Cancelled) return r;
            r.Exact = ExactGroups(files);
            if (Cancelled) return r;
            // Exact copies collapse to one representative so they don't appear twice.
            var inExact = new HashSet<string>(r.Exact.SelectMany(g => g.Files.Skip(1)).Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
            r.Similar = SimilarGroups(files.Where(f => f.IsImage && !inExact.Contains(f.Path)).ToList());
        }
        catch (OperationCanceledException) { }
        return r;
    }

    public List<DupFile> Collect(IEnumerable<string> roots, long minSize)
    {
        SetPhase(L("正在列出文件…", "Listing files…"), 0);
        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = false, IgnoreInaccessible = true, ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint | FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess,
        };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var outList = new List<DupFile>();
        var stack = new Stack<string>(roots.Where(Directory.Exists));
        while (stack.Count > 0)
        {
            _cts.Token.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            IEnumerable<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", opts).ToList(); } catch { continue; }
            foreach (var e in entries)
            {
                if (e is DirectoryInfo d)
                {
                    if (!SkipDirs.Contains(d.Name) && !d.Name.StartsWith('.')) stack.Push(d.FullName);
                    continue;
                }
                if (e is not FileInfo f || f.Name.StartsWith('.')) continue;
                long len;
                try { len = f.Length; } catch { continue; }
                if (len < minSize || !seen.Add(f.FullName)) continue;
                outList.Add(new DupFile(f.FullName, len, f.LastWriteTime));
                Tick();
            }
        }
        return outList;
    }

    List<DupGroup> ExactGroups(List<DupFile> files)
    {
        var bySize = files.GroupBy(f => f.Size).Where(g => g.Count() > 1).Select(g => g.ToList()).ToList();
        var candidates = bySize.SelectMany(g => g).ToList();
        SetPhase(L("正在比较文件开头…", "Comparing file headers…"), candidates.Count);
        var partial = HashAll(candidates, f => Sha(f.Path, PartialBytes));

        var groups = new List<List<DupFile>>();
        var needFull = new List<DupFile>();
        foreach (var same in bySize)
            foreach (var g in same.Where(f => partial.ContainsKey(f.Path)).GroupBy(f => partial[f.Path]).Where(g => g.Count() > 1))
            {
                if (g.First().Size <= PartialBytes) groups.Add(g.ToList()); else needFull.AddRange(g);
            }
        SetPhase(L("正在计算完整指纹…", "Hashing full files…"), needFull.Count);
        var full = HashAll(needFull, f => Sha(f.Path, null));
        groups.AddRange(needFull.Where(f => full.ContainsKey(f.Path)).GroupBy(f => f.Size + "-" + full[f.Path]).Where(g => g.Count() > 1).Select(g => g.ToList()));

        return groups.Select(g =>
        {
            var fs = g.OrderByDescending(f => f.Modified).ToList();
            return new DupGroup { Id = "x|" + string.Join("|", fs.Select(f => f.Path).OrderBy(p => p, StringComparer.OrdinalIgnoreCase)), Kind = DupKind.Exact, Files = fs };
        }).OrderByDescending(g => g.Reclaimable).ToList();
    }

    Dictionary<string, string> HashAll(List<DupFile> files, Func<DupFile, string?> hash)
    {
        var outMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var gate = new object();
        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Workers, CancellationToken = _cts.Token }, f =>
        {
            var h = hash(f);
            if (h is not null) lock (gate) outMap[f.Path] = h;
            Tick();
        });
        return outMap;
    }

    string? Sha(string path, int? limit)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
            using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buf = new byte[limit is { } l ? Math.Min(l, 1 << 20) : 1 << 20];
            long remaining = limit ?? long.MaxValue;
            while (remaining > 0)
            {
                if (Cancelled) return null;
                int n = fs.Read(buf, 0, (int)Math.Min(buf.Length, remaining));
                if (n <= 0) break;
                h.AppendData(buf, 0, n);
                remaining -= n;
            }
            return Convert.ToHexString(h.GetHashAndReset());
        }
        catch { return null; }
    }

    List<DupGroup> SimilarGroups(List<DupFile> images)
    {
        SetPhase(L("正在比较图片…", "Comparing images…"), images.Count);
        var hashes = new ulong?[images.Count];
        Parallel.For(0, images.Count, new ParallelOptions { MaxDegreeOfParallelism = Workers, CancellationToken = _cts.Token }, i =>
        {
            hashes[i] = DHash(images[i].Path);
            Tick();
        });
        // Flat images all hash to 0 or all ones; leave them out.
        var items = images.Select((f, i) => (f, h: hashes[i])).Where(x => x.h is { } v && v != 0 && v != ulong.MaxValue).Select(x => (x.f, h: x.h!.Value)).ToList();
        var used = new bool[items.Count];
        var groups = new List<DupGroup>();
        // Greedy clustering around a seed; union-find would chain loosely related pictures.
        for (int i = 0; i < items.Count; i++)
        {
            if (used[i]) continue;
            var members = new List<DupFile> { items[i].f };
            for (int j = i + 1; j < items.Count; j++)
            {
                if (used[j]) continue;
                if (ulong.PopCount(items[i].h ^ items[j].h) <= Threshold) { used[j] = true; members.Add(items[j].f); }
            }
            if (members.Count < 2) continue;
            used[i] = true;
            var fs = members.OrderByDescending(f => f.Modified).ToList();
            groups.Add(new DupGroup { Id = "s|" + string.Join("|", fs.Select(f => f.Path).OrderBy(p => p, StringComparer.OrdinalIgnoreCase)), Kind = DupKind.Similar, Files = fs });
        }
        return groups.OrderByDescending(g => g.Reclaimable).ToList();
    }

    /// Difference hash via WIC: decode straight to 9×8, convert to 8-bit gray.
    public static ulong? DHash(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bi.DecodePixelWidth = 9;
            bi.DecodePixelHeight = 8;
            bi.StreamSource = fs;
            bi.EndInit();
            bi.Freeze();
            BitmapSource src = bi;
            if (src.PixelWidth != 9 || src.PixelHeight != 8)
                src = new TransformedBitmap(src, new ScaleTransform(9.0 / src.PixelWidth, 8.0 / src.PixelHeight));
            var gray = new FormatConvertedBitmap(src, PixelFormats.Gray8, null, 0);
            var px = new byte[9 * 8];
            gray.CopyPixels(px, 9, 0);
            ulong hash = 0;
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                {
                    hash <<= 1;
                    if (px[y * 9 + x] > px[y * 9 + x + 1]) hash |= 1;
                }
            return hash;
        }
        catch { return null; }
    }
}

public enum KeepRule { Newest, Oldest, Largest }

public sealed class DuplicatesModel
{
    public static DuplicatesModel Shared { get; } = new();
    public List<string> Folders = new();
    public int MinKB = 100;
    public KeepRule Rule = KeepRule.Newest;
    public string? Preferred;

    public List<DupGroup> Exact = new(), Similar = new();
    public HashSet<string> Selected = new(StringComparer.OrdinalIgnoreCase);
    public bool Scanning, Working;
    public string Status = "";
    public double? Fraction;
    public int Scanned;
    public DateTime? LastScan;
    public string? LastResult;
    public event Action? Changed;
    DupEngine? _engine;

    static string ConfigPath => Path.Combine(Paths.Support, "duplicates.json");
    public static List<string> DefaultFolders => new[] { "Downloads", "Documents", "Desktop", "Pictures" }.Select(Paths.H).ToList();

    sealed class Config { public List<string>? Folders { get; set; } public int MinKB { get; set; } }

    DuplicatesModel()
    {
        try
        {
            var c = JsonSerializer.Deserialize<Config>(File.ReadAllText(ConfigPath));
            if (c?.Folders is { } f) Folders = f;
            if (c?.MinKB > 0) MinKB = c.MinKB;
        }
        catch { }
        if (Folders.Count == 0) Folders = DefaultFolders;
    }

    public void Save()
    {
        try { File.WriteAllText(ConfigPath, JsonSerializer.Serialize(new Config { Folders = Folders, MinKB = MinKB })); } catch { }
    }

    public long ReclaimableExact => Exact.Sum(g => g.Reclaimable);
    public long ReclaimableSimilar => Similar.Sum(g => g.Reclaimable);
    public IEnumerable<DupGroup> AllGroups => Exact.Concat(Similar);
    public List<DupFile> AllFiles => AllGroups.SelectMany(g => g.Files).DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
    public List<DupFile> SelectedFiles => AllFiles.Where(f => Selected.Contains(f.Path)).ToList();

    public void Scan()
    {
        _engine?.Cancel();
        var engine = new DupEngine();
        _engine = engine;
        Scanning = true; Status = L("准备中…", "Preparing…"); Fraction = null; LastResult = null;
        Changed?.Invoke();
        var roots = Folders.Where(Directory.Exists).ToList();
        long min = MinKB * 1024L;
        Task.Run(() =>
        {
            var r = engine.Run(roots, min);
            UiThread.Run(() =>
            {
                if (_engine != engine) return;
                Scanning = false; Status = ""; Fraction = null;
                if (!engine.Cancelled)
                {
                    Exact = r.Exact; Similar = r.Similar; Scanned = r.Scanned; LastScan = DateTime.Now;
                    SmartSelect();
                }
                Changed?.Invoke();
            });
        });
        Task.Run(async () =>
        {
            while (Scanning && _engine == engine)
            {
                await Task.Delay(300);
                var (phase, done, total) = engine.Progress;
                UiThread.Run(() =>
                {
                    if (!Scanning || _engine != engine) return;
                    Status = total > 0 ? $"{phase} {done}/{total}" : done > 0 ? $"{phase} {done}" : phase;
                    Fraction = total > 0 ? (double)done / total : null;
                    Changed?.Invoke();
                });
            }
        });
    }

    public void Cancel()
    {
        _engine?.Cancel(); _engine = null;
        Scanning = false; Status = ""; Fraction = null;
        Changed?.Invoke();
    }

    public static bool Deletable(string path) => CleanupExecutor.IsSafeTarget(path) && !Settings.Shared.IsWhitelisted(path);

    public IEnumerable<DupGroup> GroupsWith(string path) => AllGroups.Where(g => g.Files.Any(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase)));

    /// A file may be selected only if it is deletable and each of its groups keeps another file.
    public bool CanSelect(DupFile f)
    {
        if (!Deletable(f.Path)) return false;
        return !GroupsWith(f.Path).Any(g => g.Files.All(x => x.Path.Equals(f.Path, StringComparison.OrdinalIgnoreCase) || Selected.Contains(x.Path)));
    }

    public bool Toggle(DupFile f, bool on)
    {
        if (!on) { Selected.Remove(f.Path); return true; }
        if (!CanSelect(f)) return false;
        Selected.Add(f.Path);
        return true;
    }

    public DupFile Keeper(DupGroup g)
    {
        IEnumerable<DupFile> pool = g.Files;
        if (Preferred is { } p)
        {
            var pre = pool.Where(f => f.Path.StartsWith(p.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)).ToList();
            if (pre.Count > 0) pool = pre;
        }
        var locked = pool.Where(f => !Deletable(f.Path)).ToList();
        if (locked.Count > 0) pool = locked;
        return Rule switch
        {
            KeepRule.Oldest => pool.OrderBy(f => f.Modified).First(),
            KeepRule.Largest => pool.OrderByDescending(f => f.Size).First(),
            _ => pool.OrderByDescending(f => f.Modified).First(),
        };
    }

    public void SmartSelect()
    {
        var sel = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in AllGroups)
        {
            var k = Keeper(g);
            keep.Add(k.Path);
            foreach (var f in g.Files) if (f != k && Deletable(f.Path)) sel.Add(f.Path);
        }
        sel.ExceptWith(keep);   // a file kept in one group is never removed through another
        Selected = sel;
        Changed?.Invoke();
    }

    public void SelectNone() { Selected.Clear(); Changed?.Invoke(); }

    public void RecycleSelected()
    {
        // Each group must keep at least one existing, unselected file, else none of it is touched.
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in AllGroups)
            if (!g.Files.Any(f => !Selected.Contains(f.Path) && File.Exists(f.Path)))
                foreach (var f in g.Files) blocked.Add(f.Path);
        var list = SelectedFiles.Where(f => !blocked.Contains(f.Path) && Deletable(f.Path)).ToList();
        int skipped = Selected.Count - list.Count;
        if (list.Count == 0)
        {
            UIX.Info(L("没有可以移走的文件", "Nothing to move"), L("每组至少要保留一个文件，白名单和受保护位置里的文件也不会被移走。", "Every group keeps at least one file, and whitelisted or protected files are never moved."));
            return;
        }
        long size = list.Sum(f => f.Size);
        var preview = string.Join("\n", list.Take(12).Select(f => $"• {Fmt.Tilde(f.Path)}  {Fmt.Bytes(f.Size)}")) + (list.Count > 12 ? L($"\n…等 {list.Count} 个文件", $"\n…{list.Count} files in total") : "");
        var skipNote = skipped > 0 ? L($"\n\n另有 {skipped} 个已选文件会被跳过（白名单、受保护位置或该组没有可保留的文件）。", $"\n\n{skipped} other selected files will be skipped (whitelisted, protected, or no file would remain in their group).") : "";
        if (!UIX.Confirm(L($"把 {list.Count} 个文件（{Fmt.Bytes(size)}）移到回收站？", $"Move {list.Count} files ({Fmt.Bytes(size)}) to the Recycle Bin?"),
                         preview + skipNote + L("\n\n每组都会保留至少一个文件。可以在回收站里恢复。", "\n\nAt least one file in every group is kept. You can restore them from the Recycle Bin."))) return;
        Working = true; Changed?.Invoke();
        Task.Run(() =>
        {
            var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var failures = new List<(string path, string err)>();
            foreach (var f in list)
            {
                var err = Native.Recycle(f.Path);
                if (err is null) done.Add(f.Path); else failures.Add((f.Path, err));
            }
            UiThread.Run(() =>
            {
                Working = false;
                long freed = list.Where(f => done.Contains(f.Path)).Sum(f => f.Size);
                Selected.ExceptWith(done);
                List<DupGroup> Prune(List<DupGroup> gs) => gs.Select(g => new DupGroup { Id = g.Id, Kind = g.Kind, Files = g.Files.Where(f => !done.Contains(f.Path)).ToList() }).Where(g => g.Files.Count > 1).ToList();
                Exact = Prune(Exact); Similar = Prune(Similar);
                LastResult = L($"已把 {done.Count} 个文件（{Fmt.Bytes(freed)}）移到回收站，清空回收站后才会真正释放空间", $"Moved {done.Count} files ({Fmt.Bytes(freed)}) to the Recycle Bin. Empty it to actually free the space");
                Changed?.Invoke();
                if (failures.Count > 0)
                    UIX.Info(L($"有 {failures.Count} 个文件没能移走", $"{failures.Count} files couldn't be moved"), string.Join("\n", failures.Take(8).Select(x => $"{Fmt.Tilde(x.path)}: {x.err}")));
            });
        });
    }
}
