namespace Peekaboo;

public sealed class DiskNode
{
    public string Path = "";
    public string Name = "";
    public bool IsDirectory;
    public bool IsAggregate;
    public long Size;
    public int FileCount;
    public List<DiskNode> Children = new();
    public DiskNode? Parent;

    public List<DiskNode> Chain()
    {
        var l = new List<DiskNode>();
        for (var n = this; n is not null; n = n.Parent) l.Insert(0, n);
        return l;
    }

    public void Remove(DiskNode child)
    {
        Children.Remove(child);
        for (var n = this; n is not null; n = n.Parent) n.Size -= child.Size;
    }
}

public sealed record LargeFile(string Path, long Size, DateTime Modified);

/// Recursive size scan. Small files and folders are folded into one "其他" node so the
/// tree stays light; large files are collected on the side for the cleanup page.
public sealed class DiskScanEngine
{
    readonly long _large;
    readonly object _lock = new();
    int _files; long _bytes; volatile bool _cancel;
    readonly List<LargeFile> _largeFiles = new();
    const long SmallFile = 8L << 20, SmallDir = 1L << 20;

    public DiskScanEngine(long largeThreshold) => _large = largeThreshold;
    public (int files, long bytes) Progress { get { lock (_lock) return (_files, _bytes); } }
    public List<LargeFile> LargeFiles { get { lock (_lock) return _largeFiles.OrderByDescending(f => f.Size).ToList(); } }
    public void Cancel() => _cancel = true;

    public DiskNode Scan(string root)
    {
        var node = new DiskNode { Path = root, Name = root, IsDirectory = true };
        DirectoryInfo[] dirs; FileInfo[] files;
        try { var di = new DirectoryInfo(root); dirs = di.GetDirectories(); files = di.GetFiles(); }
        catch { return node; }
        var results = new DiskNode?[dirs.Length];
        Parallel.For(0, dirs.Length, i => results[i] = Visit(dirs[i]));
        var kids = results.Where(r => r is not null).Cast<DiskNode>().ToList();
        kids.AddRange(files.Select(FileNode));
        Attach(kids, node);
        return node;
    }

    DiskNode? Visit(DirectoryInfo d)
    {
        if (_cancel) return null;
        try { if ((d.Attributes & FileAttributes.ReparsePoint) != 0) return null; } catch { return null; }
        var node = new DiskNode { Path = d.FullName, Name = d.Name, IsDirectory = true };
        var kids = new List<DiskNode>();
        try
        {
            foreach (var sub in d.EnumerateDirectories()) { var n = Visit(sub); if (n is not null) kids.Add(n); }
            foreach (var f in d.EnumerateFiles()) kids.Add(FileNode(f));
        }
        catch { }
        Attach(kids, node);
        return node;
    }

    DiskNode FileNode(FileInfo f)
    {
        long size = 0;
        try { size = f.Length; } catch { }
        lock (_lock)
        {
            _files++; _bytes += size;
            if (size >= _large) { DateTime m = DateTime.MinValue; try { m = f.LastWriteTime; } catch { } _largeFiles.Add(new LargeFile(f.FullName, size, m)); }
        }
        return new DiskNode { Path = f.FullName, Name = f.Name, Size = size, FileCount = 1 };
    }

    static void Attach(List<DiskNode> kids, DiskNode node)
    {
        var keep = new List<DiskNode>();
        long smallSize = 0; int smallCount = 0, files = 0; long total = 0;
        foreach (var k in kids)
        {
            total += k.Size; files += k.FileCount;
            if (k.Size < (k.IsDirectory ? SmallDir : SmallFile)) { if (k.Size > 0) { smallSize += k.Size; smallCount++; } }
            else keep.Add(k);
        }
        if (smallCount > 0)
            keep.Add(new DiskNode { Path = node.Path, Name = L($"其他 {smallCount} 个小项目", $"{smallCount} other small items"), IsAggregate = true, Size = smallSize, FileCount = smallCount });
        keep.Sort((a, b) => b.Size.CompareTo(a.Size));
        foreach (var k in keep) k.Parent = node;
        node.Children = keep; node.Size = total; node.FileCount = files;
    }
}

/// Squarified treemap layout; values must be sorted descending.
public static class Treemap
{
    public static System.Windows.Rect[] Layout(IReadOnlyList<double> values, System.Windows.Rect rect)
    {
        var result = new System.Windows.Rect[values.Count];
        double total = values.Sum();
        if (total <= 0 || rect.Width <= 0 || rect.Height <= 0) return result;
        double scale = rect.Width * rect.Height / total;
        var areas = values.Select(v => Math.Max(v * scale, 0.0001)).ToArray();
        var r = rect; int i = 0;
        while (i < areas.Length)
        {
            double shortSide = Math.Min(r.Width, r.Height);
            var row = new List<double> { areas[i] }; int j = i + 1;
            while (j < areas.Length)
            {
                var cand = new List<double>(row) { areas[j] };
                if (Worst(cand, shortSide) <= Worst(row, shortSide)) { row = cand; j++; } else break;
            }
            double sum = row.Sum();
            if (r.Width >= r.Height)
            {
                double w = sum / r.Height, y = r.Y;
                for (int k = 0; k < row.Count; k++) { double h = row[k] / w; result[i + k] = new System.Windows.Rect(r.X, y, w, h); y += h; }
                r = new System.Windows.Rect(r.X + w, r.Y, Math.Max(r.Width - w, 0), r.Height);
            }
            else
            {
                double h = sum / r.Width, x = r.X;
                for (int k = 0; k < row.Count; k++) { double w = row[k] / h; result[i + k] = new System.Windows.Rect(x, r.Y, w, h); x += w; }
                r = new System.Windows.Rect(r.X, r.Y + h, r.Width, Math.Max(r.Height - h, 0));
            }
            i = j;
        }
        return result;
    }

    static double Worst(List<double> row, double w)
    {
        double s = row.Sum(), mx = row.Max(), mn = row.Min();
        if (s <= 0 || mn <= 0) return double.PositiveInfinity;
        return Math.Max(w * w * mx / (s * s), s * s / (w * w * mn));
    }
}
