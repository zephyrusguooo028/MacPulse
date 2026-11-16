namespace Peekaboo;

public enum ArtifactKind { Dependency, Build, Cache }

public sealed record Artifact(string Path, ArtifactKind Kind, long Size, bool Safe)
{
    public string KindText => Kind switch { ArtifactKind.Dependency => L("依赖", "Dependency"), ArtifactKind.Build => L("构建产物", "Build output"), _ => L("缓存", "Cache") };
    public string Hint => Kind switch
    {
        ArtifactKind.Dependency => L("重新安装依赖即可恢复（npm install 等）", "Reinstall dependencies to restore (npm install, etc.)"),
        ArtifactKind.Build => L("重新构建即可生成", "Rebuild to regenerate"),
        _ => L("工具缓存，会自动重建", "Tool cache, rebuilt automatically"),
    };
}

public sealed class Project
{
    public string Path = "";
    public string DisplayName = "";
    public List<string> Tags = new();
    public long SourceSize, GitSize;
    public List<Artifact> Artifacts = new();
    public DateTime? LastModified;
    public long ArtifactSize => Artifacts.Sum(a => a.Size);
    public long TotalSize => SourceSize + GitSize + ArtifactSize;
    public long SafeArtifactSize => Artifacts.Where(a => a.Safe).Sum(a => a.Size);
}

public static class ProjectRules
{
    public static readonly (string file, string tag)[] Markers =
    {
        ("package.json", "Node"), ("project.config.json", "小程序"), ("go.mod", "Go"), ("Cargo.toml", "Rust"),
        ("pyproject.toml", "Python"), ("requirements.txt", "Python"), ("Package.swift", "Swift"),
        ("pom.xml", "Java"), ("build.gradle", "Gradle"), ("build.gradle.kts", "Gradle"), ("pubspec.yaml", "Flutter"),
        ("composer.json", "PHP"), ("Gemfile", "Ruby"), ("CMakeLists.txt", "C/C++"),
    };

    public static readonly Dictionary<string, ArtifactKind> AlwaysSafe = new(StringComparer.OrdinalIgnoreCase)
    {
        ["node_modules"] = ArtifactKind.Dependency, ["miniprogram_npm"] = ArtifactKind.Dependency, [".venv"] = ArtifactKind.Dependency,
        ["venv"] = ArtifactKind.Dependency, ["packages"] = ArtifactKind.Dependency, [".pnpm-store"] = ArtifactKind.Dependency,
        [".next"] = ArtifactKind.Build, [".nuxt"] = ArtifactKind.Build, [".output"] = ArtifactKind.Build, [".svelte-kit"] = ArtifactKind.Build,
        [".angular"] = ArtifactKind.Build, ["coverage"] = ArtifactKind.Build, ["storybook-static"] = ArtifactKind.Build,
        [".turbo"] = ArtifactKind.Cache, [".parcel-cache"] = ArtifactKind.Cache, [".vite"] = ArtifactKind.Cache, [".cache"] = ArtifactKind.Cache,
        ["__pycache__"] = ArtifactKind.Cache, [".pytest_cache"] = ArtifactKind.Cache, [".mypy_cache"] = ArtifactKind.Cache,
        [".gradle"] = ArtifactKind.Cache, [".dart_tool"] = ArtifactKind.Cache, [".expo"] = ArtifactKind.Cache, [".vs"] = ArtifactKind.Cache,
    };

    /// Usually generated but sometimes real source: safe only when git-ignored.
    public static readonly Dictionary<string, ArtifactKind> Maybe = new(StringComparer.OrdinalIgnoreCase)
    {
        ["dist"] = ArtifactKind.Build, ["build"] = ArtifactKind.Build, ["out"] = ArtifactKind.Build, ["target"] = ArtifactKind.Build,
        ["bin"] = ArtifactKind.Build, ["obj"] = ArtifactKind.Build, ["unpackage"] = ArtifactKind.Build, ["release"] = ArtifactKind.Build,
    };

    public static readonly HashSet<string> SkipWhenSearching = new(StringComparer.OrdinalIgnoreCase)
    { "node_modules", "AppData", "Pictures", "Videos", "Music", "OneDrive", "$RECYCLE.BIN" };
}

public sealed class ProjectScanEngine
{
    volatile bool _cancel;
    public void Cancel() => _cancel = true;

    public List<(string path, List<string> tags, string root)> Find(IEnumerable<string> roots)
    {
        var found = new List<(string, List<string>, string)>();
        foreach (var r in roots) Search(r, r, 0, found);
        return found;
    }

    void Search(string dir, string root, int depth, List<(string, List<string>, string)> found)
    {
        if (_cancel || depth > 4) return;
        string[] names;
        try { names = Directory.EnumerateFileSystemEntries(dir).Select(System.IO.Path.GetFileName).Where(n => n is not null).Cast<string>().ToArray(); }
        catch { return; }
        var set = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        var tags = ProjectRules.Markers.Where(m => set.Contains(m.file)).Select(m => m.tag).ToList();
        if (set.Contains(".git")) tags.Add("Git");
        if (set.Any(n => n.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))) tags.Add(".NET");
        if (tags.Count > 0 && !dir.Equals(root, StringComparison.OrdinalIgnoreCase)) { found.Add((dir, tags.Distinct().ToList(), root)); return; }
        foreach (var n in names)
        {
            if (n.StartsWith('.') || ProjectRules.SkipWhenSearching.Contains(n)) continue;
            var p = System.IO.Path.Combine(dir, n);
            try
            {
                if (!Directory.Exists(p) || (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) continue;
            }
            catch { continue; }
            Search(p, root, depth + 1, found);
        }
    }

    public Project Measure(string path, List<string> tags, string root)
    {
        var ignored = GitIgnoreNames(path);
        var pr = new Project { Path = path, Tags = tags };
        var rel = System.IO.Path.GetRelativePath(root, path);
        pr.DisplayName = string.Join(" › ", rel.Split('\\', '/'));
        Walk(path, path, ignored, pr);
        pr.Artifacts.Sort((a, b) => b.Size.CompareTo(a.Size));
        return pr;
    }

    void Walk(string dir, string projectRoot, HashSet<string> ignored, Project pr)
    {
        if (_cancel) return;
        try
        {
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles())
            {
                try
                {
                    pr.SourceSize += f.Length;
                    var m = f.LastWriteTime;
                    if (pr.LastModified is null || m > pr.LastModified) pr.LastModified = m;
                }
                catch { }
            }
            foreach (var d in new DirectoryInfo(dir).EnumerateDirectories())
            {
                if ((d.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                var name = d.Name;
                if (name.Equals(".git", StringComparison.OrdinalIgnoreCase) && dir == projectRoot) { pr.GitSize = FileSizer.Size(d.FullName); continue; }
                if (ProjectRules.AlwaysSafe.TryGetValue(name, out var k))
                {
                    // "packages" is only a dependency folder for NuGet-style layouts next to a .sln.
                    if (name.Equals("packages", StringComparison.OrdinalIgnoreCase) && !pr.Tags.Contains(".NET")) { Walk(d.FullName, projectRoot, ignored, pr); continue; }
                    pr.Artifacts.Add(new Artifact(d.FullName, k, FileSizer.Size(d.FullName), true));
                    continue;
                }
                if (ProjectRules.Maybe.TryGetValue(name, out var mk))
                {
                    bool safe = ignored.Contains(name) || GitIgnoreNames(dir).Contains(name);
                    pr.Artifacts.Add(new Artifact(d.FullName, mk, FileSizer.Size(d.FullName), safe));
                    continue;
                }
                Walk(d.FullName, projectRoot, ignored, pr);
            }
        }
        catch { }
    }

    /// Plain folder names listed in a .gitignore ("dist", "/dist", "dist/", "**/dist").
    public static HashSet<string> GitIgnoreNames(string dir)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var raw in File.ReadAllLines(System.IO.Path.Combine(dir, ".gitignore")))
            {
                var l = raw.Trim();
                if (l.Length == 0 || l.StartsWith('#') || l.StartsWith('!')) continue;
                l = l.TrimStart('/').TrimEnd('/');
                if (l.StartsWith("**/")) l = l[3..];
                if (!l.Contains('/') && !l.Contains('*')) set.Add(l);
            }
        }
        catch { }
        return set;
    }
}
