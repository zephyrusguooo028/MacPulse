using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using static Peekaboo.UIX;

namespace Peekaboo;

public sealed class CleanupPage : PageView
{
    bool _permanent;
    readonly HashSet<CleanupCategory> _collapsed = new() { CleanupCategory.AppCache, CleanupCategory.TempAndLogs };
    CleanupModel M => CleanupModel.Shared;

    public override void Shown()
    {
        M.Changed += Build;
        ProjectModel.Shared.Changed += Resync;
        DiskModel.Shared.Changed += Resync;
        if (M.LastScan is null && !M.Scanning) M.Scan();
        Build();
    }

    public override void Hidden()
    {
        M.Changed -= Build;
        ProjectModel.Shared.Changed -= Resync;
        DiskModel.Shared.Changed -= Resync;
    }

    void Resync()
    {
        if (ProjectModel.Shared.Scanning || DiskModel.Shared.Scanning) return;
        var before = M.All.Select(i => i.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        M.SyncFromOthers();
        foreach (var i in M.All.Where(i => i.Recommended && !before.Contains(i.Path))) M.Selected.Add(i.Path);
    }

    void Build()
    {
        long found = M.All.Sum(i => i.Size), recommended = M.All.Where(i => i.Recommended).Sum(i => i.Size);
        var rescan = Btn(M.LastScan is null ? "开始扫描" : "重新扫描", M.Scan, glyph: "");
        rescan.IsEnabled = !M.Scanning;
        var header = PageHeader("清理", M.LastScan is null ? "找出可以安全删掉的缓存、构建产物和旧文件" : $"找到 {Fmt.Bytes(found)}，推荐清理 {Fmt.Bytes(recommended)}",
                                H(8, T(M.Scanning || ProjectModel.Shared.Scanning ? "扫描中…" : "", 12.5, color: Theme.Secondary), rescan));

        // Selected / action card
        var go = Btn(M.Working ? "清理中…" : "清理所选", Run, primary: true, glyph: "");
        go.IsEnabled = M.Selected.Count > 0 && !M.Working;
        if (_permanent) go.Background = Theme.B(Theme.DangerC);
        var mode = H(2);
        foreach (var (label, val) in new[] { ("移到回收站", false), ("直接删除", true) })
        {
            var b = Btn(label, () => { _permanent = val; Build(); }, primary: _permanent == val);
            b.Padding = new Thickness(10, 3, 10, 3);
            mode.Children.Add(b);
        }
        var note = T(_permanent ? "直接删除不能恢复，空间马上释放。" : "移到回收站可以恢复，但要清空回收站后空间才真正释放。", 11.5,
                     color: _permanent ? Theme.B(Theme.DangerC) : Theme.Secondary);
        var action = Card("已选择", "", V(10,
            Row(H(8, T(Fmt.Bytes(M.SelectedSize), 32, FontWeights.Bold), T($"{M.SelectedItems.Count()} 项", 13, color: Theme.Secondary)), go),
            mode,
            H(6, Btn("全选推荐", () => { M.Selected = M.All.Where(i => i.Recommended).Select(i => i.Path).ToHashSet(StringComparer.OrdinalIgnoreCase); Build(); }),
                           Btn("全不选", () => { M.Selected.Clear(); Build(); })),
            note));

        var empty = Btn("清空回收站…", () =>
        {
            if (Confirm("清空回收站？", $"将永久删除回收站里的 {Fmt.Bytes(M.RecycleSize)}，不能恢复。")) M.EmptyRecycleBin();
        });
        empty.IsEnabled = M.RecycleSize > 0 && !M.Working;
        var bin = Card("回收站", "", V(8, T(Fmt.Bytes(M.RecycleSize), 24, FontWeights.SemiBold), empty));
        bin.Width = 220;
        var freed = M.Records.Sum(r => r.Bytes);
        var last = M.Records.LastOrDefault();
        var hist = Card("累计已清理", "", V(8, T(Fmt.Bytes(freed), 24, FontWeights.SemiBold),
            T(last is null ? "还没清理过" : $"上次 {last.Date:MM-dd HH:mm} · {Fmt.Bytes(last.Bytes)}", 11.5, color: Theme.Secondary)));
        hist.Width = 200;
        var top = new DockPanel();
        DockPanel.SetDock(hist, Dock.Right); DockPanel.SetDock(bin, Dock.Right);
        hist.Margin = new Thickness(14, 0, 0, 0); bin.Margin = new Thickness(14, 0, 0, 0);
        top.Children.Add(hist); top.Children.Add(bin); top.Children.Add(action);

        var parts = new List<UIElement> { header, top };
        if (M.LastResult is { } res) parts.Add(H(6, Glyph("", 14, Theme.B(Theme.DiskC)), T(res, 13, color: Theme.B(Theme.DiskC))));
        if (M.LastScan is null && !M.Scanning)
            parts.Add(Card("准备好了", "", Empty("", "点「开始扫描」", "会检查开发工具缓存、项目依赖和构建产物、应用缓存、临时文件、下载文件夹和大文件。\n扫描只读取，不会删除任何东西。")));
        else
            foreach (var c in Enum.GetValues<CleanupCategory>()) parts.Add(Section(c));
        Content = V(14, parts.ToArray());
    }

    Border Section(CleanupCategory cat)
    {
        var items = M.Items.TryGetValue(cat, out var l) ? l : new();
        var sel = items.Where(i => M.Selected.Contains(i.Path)).ToList();
        bool open = !_collapsed.Contains(cat) && items.Count > 0;
        var all = Check("", items.Count > 0 && sel.Count == items.Count, on =>
        {
            foreach (var i in items) { if (on) M.Selected.Add(i.Path); else M.Selected.Remove(i.Path); }
            Build();
        });
        all.IsEnabled = items.Count > 0;
        string sub = items.Count == 0 ? EmptyText(cat) : $"已选 {Fmt.Bytes(sel.Sum(i => i.Size))} · {items.Count} 项";
        var noteT = T(CleanupText.Note(cat), 11.5, color: Theme.Secondary);
        var chevron = Glyph(open ? "" : "", 12);
        var head = Row(H(10, all, Glyph(CleanupText.Glyph(cat), 16, Theme.B(Theme.CpuC)), V(2, T(CleanupText.Title(cat), 14, FontWeights.SemiBold), noteT)),
                       H(12, V(2, T(Fmt.Bytes(M.Total(cat)), 14, FontWeights.SemiBold), T(sub, 11.5, color: Theme.Secondary)), chevron));
        head.Cursor = Cursors.Hand;
        head.Background = Brushes.Transparent;
        head.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && FindParent<CheckBox>(d) is not null) return;
            if (!_collapsed.Add(cat)) _collapsed.Remove(cat);
            Build();
        };
        var body = V(0, head);
        if (open)
        {
            body.Children.Add(new Border { Height = 1, Background = Theme.Line, Margin = new Thickness(0, 12, 0, 6) });
            foreach (var item in items.Take(60))
            {
                var it = item;
                var cb = Check("", M.Selected.Contains(it.Path), on => { if (on) M.Selected.Add(it.Path); else M.Selected.Remove(it.Path); Build(); });
                var title = H(6, T(it.Title, 13));
                if (it.Recommended) title.Children.Add(new Border { Background = Theme.B(Theme.DiskC, 0.15), CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 0, 6, 1), Child = T("推荐", 10.5, color: Theme.B(Theme.DiskC)) });
                if (Settings.Shared.IsWhitelisted(it.Path)) title.Children.Add(T("白名单", 10.5, color: Theme.Secondary));
                var reveal = Btn("", () => Reveal(it.Path), glyph: ""); reveal.Padding = new Thickness(6, 3, 6, 3); reveal.Background = Brushes.Transparent; reveal.BorderBrush = Brushes.Transparent;
                var row = Row(H(10, cb, V(1, title, T(it.Subtitle, 11.5, color: Theme.Secondary))), H(8, T(Fmt.Bytes(it.Size), 13), reveal));
                row.Margin = new Thickness(0, 4, 0, 4);
                var cm = new ContextMenu();
                var m1 = new MenuItem { Header = "在资源管理器中显示" }; m1.Click += (_, _) => Reveal(it.Path); cm.Items.Add(m1);
                var m2 = new MenuItem { Header = "加入白名单（永不清理）" };
                m2.Click += (_, _) => { Settings.Shared.Whitelist.Add(Fmt.Tilde(it.Path)); Settings.Shared.Save(); M.Selected.Remove(it.Path); Build(); };
                cm.Items.Add(m2);
                row.ContextMenu = cm;
                body.Children.Add(row);
            }
            if (items.Count > 60) body.Children.Add(T($"还有 {items.Count - 60} 项未显示", 11.5, color: Theme.Secondary));
        }
        return Panel(body, 14);
    }

    static T? FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        while (d is not null) { if (d is T t) return t; d = VisualTreeHelper.GetParent(d); }
        return null;
    }

    string EmptyText(CleanupCategory c) => c switch
    {
        CleanupCategory.LargeFiles => DiskModel.Shared.Root is null ? "先在「磁盘」页扫描" : $"没有超过 {Settings.Shared.LargeFileMB} MB 的文件",
        CleanupCategory.ProjectArtifacts => ProjectModel.Shared.Scanning ? "正在扫描项目…" : "没有找到",
        _ => M.Scanning ? "扫描中…" : "没有找到",
    };

    void Run()
    {
        var list = M.SelectedItems.ToList();
        long size = list.Sum(i => i.Size);
        var preview = string.Join("\n", list.Take(10).Select(i => $"• {i.Title}  {Fmt.Bytes(i.Size)}")) + (list.Count > 10 ? $"\n…等 {list.Count} 项" : "");
        if (!Confirm(_permanent ? $"直接删除 {Fmt.Bytes(size)}？" : $"把 {Fmt.Bytes(size)} 移到回收站？", preview + (_permanent ? "\n\n直接删除不能恢复。" : "\n\n会移到回收站，可以恢复。"))) return;
        M.Perform(list, _permanent, o =>
        {
            if (o.Failures.Count > 0 || o.Skipped.Count > 0)
            {
                var lines = o.Failures.Take(8).Select(f => $"{Fmt.Tilde(f.path)}：{f.error}").ToList();
                if (o.Skipped.Count > 0) lines.Add($"跳过 {o.Skipped.Count} 项（白名单或受保护位置）");
                Info("部分项目没有清理", string.Join("\n", lines));
            }
        });
    }
}
