using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using static Peekaboo.UIX;

namespace Peekaboo;

public sealed class ProjectsPage : PageView
{
    string _sort = "total";
    string? _selected;
    readonly HashSet<string> _picked = new(StringComparer.OrdinalIgnoreCase);
    ProjectModel M => ProjectModel.Shared;

    public override void Shown()
    {
        M.Changed += Build;
        if (M.Projects.Count == 0 && !M.Scanning) M.Scan();
        Build();
    }
    public override void Hidden() => M.Changed -= Build;

    IEnumerable<Project> Sorted() => _sort switch
    {
        "artifacts" => M.Projects.OrderByDescending(p => p.ArtifactSize),
        "recent" => M.Projects.OrderByDescending(p => p.LastModified ?? DateTime.MinValue),
        "stale" => M.Projects.OrderBy(p => p.LastModified ?? DateTime.MinValue),
        _ => M.Projects.OrderByDescending(p => p.TotalSize),
    };

    void Build()
    {
        var sort = new ComboBox { Width = 130, Margin = new Thickness(0, 0, 8, 0) };
        foreach (var (k, t) in new[] { ("total", L("按总大小", "By size")), ("artifacts", L("按可清理", "By cleanable")), ("recent", L("按最近修改", "Recently modified")), ("stale", L("按最久没动", "Least recently modified")) })
        {
            var item = new ComboBoxItem { Content = t, Tag = k };
            sort.Items.Add(item);
            if (k == _sort) sort.SelectedItem = item;
        }
        sort.SelectionChanged += (_, _) => { _sort = (string)((ComboBoxItem)sort.SelectedItem).Tag; Build(); };
        var actions = H(8);
        if (M.Scanning) actions.Children.Add(T(M.Status, 12.5, color: Theme.Secondary));
        actions.Children.Add(sort);
        var rescan = Btn(M.Projects.Count == 0 ? L("扫描项目", "Scan projects") : L("重新扫描", "Rescan"), M.Scan, primary: true, glyph: "");
        rescan.IsEnabled = !M.Scanning;
        actions.Children.Add(rescan);

        var total = M.Projects.Sum(p => p.TotalSize);
        var subtitle = M.Projects.Count == 0 ? L("自动找出开发项目，看源码、依赖和构建产物各占多少", "Find your development projects and see how much space source, dependencies and build output take")
                                             : L($"{M.Projects.Count} 个项目，共 {Fmt.Bytes(total)}，其中依赖和构建产物 {Fmt.Bytes(M.TotalArtifacts)}", $"{M.Projects.Count} projects, {Fmt.Bytes(total)} total, {Fmt.Bytes(M.TotalArtifacts)} in dependencies and build output");
        if (M.Projects.Count == 0)
        {
            Content = V(16, PageHeader(L("项目", "Projects"), subtitle, actions),
                Card(L("项目目录", "Project folders"), "", Empty("", M.Scanning ? L("正在扫描…", "Scanning…") : L("还没扫描", "Not scanned yet"),
                    L("会在这些目录里找项目：", "Looks for projects in: ") + string.Join(L("、", ", "), Settings.Shared.ProjectRootPaths.Select(Fmt.Tilde)) + L("\n可以在设置里修改。", "\nYou can change this in Settings."))));
            return;
        }

        var list = Sorted().ToList();
        double max = Math.Max(1, list.Max(p => p.TotalSize));
        var rows = V(6);
        foreach (var p in list)
        {
            bool stale = Fmt.DaysAgo(p.LastModified) >= Settings.Shared.StaleProjectDays;
            var tags = H(4);
            foreach (var t in p.Tags.Take(3)) tags.Children.Add(new Border { Background = Theme.Line, CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 1, 6, 1), Child = T(t == "小程序" ? L("小程序", "Mini Program") : t, 10.5) });
            if (stale) tags.Children.Add(T(L($"{Fmt.DaysAgo(p.LastModified)} 天没动", $"untouched {Fmt.DaysAgo(p.LastModified)} days"), 11, color: Theme.B(Theme.WarnC)));
            var bar = Stacked(new List<(string, double, Color)> { (L("源码", "Source"), p.SourceSize, Theme.CpuC), ("Git", p.GitSize, Theme.MemC), (L("可清理", "Cleanable"), p.ArtifactSize, Theme.NetOutC) }, max, 6, legend: false);
            var row = new Border
            {
                Padding = new Thickness(12, 10, 12, 10), CornerRadius = new CornerRadius(10), Cursor = Cursors.Hand,
                Background = _selected == p.Path ? Theme.AccentSoftBrush : Theme.Card, BorderBrush = Theme.Line, BorderThickness = new Thickness(1),
                Child = V(6,
                    Row(H(8, Glyph("", 14, Theme.B(Theme.CpuC)), T(p.DisplayName, 13.5, FontWeights.SemiBold), tags), T(Fmt.Bytes(p.TotalSize), 13.5, FontWeights.SemiBold)),
                    bar,
                    Row(T(L($"源码 {Fmt.Bytes(p.SourceSize)}", $"Source {Fmt.Bytes(p.SourceSize)}") + (p.GitSize > 0 ? $" · Git {Fmt.Bytes(p.GitSize)}" : "") + (p.ArtifactSize > 0 ? L($" · 可清理 {Fmt.Bytes(p.ArtifactSize)}", $" · cleanable {Fmt.Bytes(p.ArtifactSize)}") : ""), 11.5, color: Theme.Secondary),
                        T(L(Fmt.Relative(p.LastModified) + "修改", "Modified " + Fmt.Relative(p.LastModified)), 11.5, color: Theme.Secondary))),
            };
            var proj = p;
            row.MouseLeftButtonUp += (_, _) => { _selected = proj.Path; _picked.Clear(); foreach (var a in proj.Artifacts.Where(a => a.Safe)) _picked.Add(a.Path); Build(); };
            var cm = new ContextMenu();
            var mi1 = new MenuItem { Header = L("在资源管理器中打开", "Open in File Explorer") }; mi1.Click += (_, _) => System.Diagnostics.Process.Start("explorer.exe", $"\"{proj.Path}\""); cm.Items.Add(mi1);
            var mi2 = new MenuItem { Header = L("在终端中打开", "Open in Terminal") }; mi2.Click += (_, _) => OpenTerminal(proj.Path); cm.Items.Add(mi2);
            row.ContextMenu = cm;
            rows.Children.Add(row);
        }

        var detail = Detail();
        detail.Width = 380;
        var body = new DockPanel();
        DockPanel.SetDock(detail, Dock.Right); detail.Margin = new Thickness(14, 0, 0, 0); detail.VerticalAlignment = VerticalAlignment.Top;
        body.Children.Add(detail);
        body.Children.Add(rows);
        Content = V(16, PageHeader(L("项目", "Projects"), subtitle, actions), body);
    }

    static void OpenTerminal(string path)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("wt.exe", $"-d \"{path}\"") { UseShellExecute = true }); }
        catch { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell.exe") { WorkingDirectory = path, UseShellExecute = true }); } catch { } }
    }

    Border Detail()
    {
        var p = M.Projects.FirstOrDefault(x => x.Path == _selected);
        if (p is null) return Card(L("详情", "Details"), "", Empty("", L("选一个项目", "Select a project"), L("左边点一个项目，查看它的依赖和构建产物，并可以单独清理。", "Click a project on the left to see its dependencies and build output and clean them individually.")));
        var s = V(10,
            new TextBlock { Text = p.Path, FontSize = 11.5, Foreground = Theme.Secondary, TextWrapping = TextWrapping.Wrap },
            Stacked(new List<(string, double, Color)> { (L("源码", "Source"), p.SourceSize, Theme.CpuC), ("Git", p.GitSize, Theme.MemC), (L("可清理", "Cleanable"), p.ArtifactSize, Theme.NetOutC) }, p.TotalSize, 12),
            H(8, Btn(L("在资源管理器中打开", "Open in File Explorer"), () => System.Diagnostics.Process.Start("explorer.exe", $"\"{p.Path}\"")), Btn(L("在终端中打开", "Open in Terminal"), () => OpenTerminal(p.Path))));
        if (p.Artifacts.Count == 0) s.Children.Add(T(L("没有可清理的依赖或构建产物。", "No dependencies or build output to clean."), 12.5, color: Theme.Secondary));
        else
        {
            s.Children.Add(T(L("依赖与构建产物", "Dependencies and build output"), 13, FontWeights.SemiBold));
            foreach (var a in p.Artifacts)
            {
                var rel = Path.GetRelativePath(p.Path, a.Path);
                var hint = T(a.Safe ? $"{a.KindText} · {a.Hint}" : L($"{a.KindText} · 不在 .gitignore 里，可能是源码，请确认", $"{a.KindText} · not in .gitignore, may be source code, please check"), 11, color: a.Safe ? Theme.Secondary : Theme.B(Theme.WarnC));
                hint.TextWrapping = TextWrapping.Wrap; hint.TextTrimming = TextTrimming.None;
                var art = a;
                var cb = Check("", _picked.Contains(a.Path), on => { if (on) _picked.Add(art.Path); else _picked.Remove(art.Path); Build(); });
                cb.VerticalAlignment = VerticalAlignment.Top;
                s.Children.Add(Row(H(6, cb, V(1, T(rel, 12.5), hint)), T(Fmt.Bytes(a.Size), 12.5)));
            }
            long size = p.Artifacts.Where(a => _picked.Contains(a.Path)).Sum(a => a.Size);
            var recycle = Btn(L("移到回收站…", "Move to Recycle Bin…"), () => Clean(p, false));
            var delete = Btn(L("直接删除…", "Delete permanently…"), () => Clean(p, true));
            recycle.IsEnabled = delete.IsEnabled = _picked.Count > 0 && !CleanupModel.Shared.Working;
            s.Children.Add(Row(T(L($"已选 {Fmt.Bytes(size)}", $"{Fmt.Bytes(size)} selected"), 13, FontWeights.Medium), H(6, recycle, delete)));
        }
        return Card(p.DisplayName, "", s);
    }

    void Clean(Project p, bool permanent)
    {
        var items = p.Artifacts.Where(a => _picked.Contains(a.Path))
            .Select(a => new CleanupItem(a.Path, Path.GetFileName(a.Path), "", a.Size, CleanupCategory.ProjectArtifacts, false)).ToList();
        long size = items.Sum(i => i.Size);
        var msg = string.Join("\n", items.Take(8).Select(i => "• " + Fmt.Tilde(i.Path))) + (items.Count > 8 ? L($"\n…等 {items.Count} 项", $"\n…{items.Count} items in total") : "")
                  + (permanent ? L("\n\n直接删除不能恢复。这些目录可以通过重新安装依赖或重新构建生成。", "\n\nPermanent deletion can’t be undone. These folders can be regenerated by reinstalling dependencies or rebuilding.") : L("\n\n可以在回收站里恢复；清空回收站后才会释放空间。", "\n\nYou can restore them from the Recycle Bin; space is freed once you empty it."));
        if (!Confirm(permanent ? L($"直接删除 {Fmt.Bytes(size)}？", $"Permanently delete {Fmt.Bytes(size)}?") : L($"把 {Fmt.Bytes(size)} 移到回收站？", $"Move {Fmt.Bytes(size)} to the Recycle Bin?"), msg)) return;
        CleanupModel.Shared.Perform(items, permanent, o =>
        {
            _picked.Clear();
            if (o.Failures.Count > 0) Info(L($"有 {o.Failures.Count} 项没清理成功", $"{o.Failures.Count} items couldn’t be cleaned"), string.Join("\n", o.Failures.Select(f => L($"{Fmt.Tilde(f.path)}：{f.error}", $"{Fmt.Tilde(f.path)}: {f.error}"))));
        });
    }
}
