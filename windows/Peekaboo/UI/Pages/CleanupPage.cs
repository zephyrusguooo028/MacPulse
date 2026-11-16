using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using static Peekaboo.UIX;

namespace Peekaboo;

public sealed class CleanupPage : PageView
{
    bool _permanent;
    readonly HashSet<string> _pickedHogs = new(StringComparer.OrdinalIgnoreCase);
    bool _hogsInit;
    string? _hogResult;
    readonly HashSet<CleanupCategory> _collapsed = new() { CleanupCategory.AppCache, CleanupCategory.TempAndLogs };
    CleanupModel M => CleanupModel.Shared;

    public override void Tick() { if (!M.Working) Build(); }

    public override void Shown()
    {
        SystemMonitor.Shared.WantProcesses(true);
        M.Changed += Build;
        ProjectModel.Shared.Changed += Resync;
        DiskModel.Shared.Changed += Resync;
        if (M.LastScan is null && !M.Scanning) M.Scan();
        Build();
    }

    public override void Hidden()
    {
        SystemMonitor.Shared.WantProcesses(false);
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
        var rescan = Btn(M.LastScan is null ? L("开始扫描", "Start scan") : L("重新扫描", "Rescan"), M.Scan, glyph: "");
        rescan.IsEnabled = !M.Scanning;
        var header = PageHeader(L("清理", "Cleanup"), M.LastScan is null ? L("找出可以安全删掉的缓存、构建产物和旧文件", "Find caches, build output and old files that are safe to delete") : L($"找到 {Fmt.Bytes(found)}，推荐清理 {Fmt.Bytes(recommended)}", $"Found {Fmt.Bytes(found)}, {Fmt.Bytes(recommended)} recommended"),
                                H(8, T(M.Scanning || ProjectModel.Shared.Scanning ? L("扫描中…", "Scanning…") : "", 12.5, color: Theme.Secondary), rescan));

        // Selected / action card
        var go = Btn(M.Working ? L("清理中…", "Cleaning…") : L("清理所选", "Clean selected"), Run, primary: true, glyph: "");
        go.IsEnabled = M.Selected.Count > 0 && !M.Working;
        if (_permanent) go.Background = Theme.B(Theme.DangerC);
        var mode = H(2);
        foreach (var (label, val) in new[] { (L("移到回收站", "Move to Recycle Bin"), false), (L("直接删除", "Delete permanently"), true) })
        {
            var b = Btn(label, () => { _permanent = val; Build(); }, primary: _permanent == val);
            b.Padding = new Thickness(10, 3, 10, 3);
            mode.Children.Add(b);
        }
        TextBlock note = T(_permanent ? L("直接删除不能恢复，空间马上释放。", "Permanent deletion can’t be undone; space is freed immediately.") : L("移到回收站可以恢复，但要清空回收站后空间才真正释放。", "Items in the Recycle Bin can be restored, but space is only freed after you empty it."), 11.5,
                     color: _permanent ? Theme.B(Theme.DangerC) : Theme.Secondary);
        note.TextWrapping = TextWrapping.Wrap; note.TextTrimming = TextTrimming.None;
        var action = Card(L("已选择", "Selected"), "", V(10,
            Row(H(8, T(Fmt.Bytes(M.SelectedSize), 32, FontWeights.Bold), T(L($"{M.SelectedItems.Count()} 项", $"{M.SelectedItems.Count()} items"), 13, color: Theme.Secondary)), go),
            mode,
            H(6, Btn(L("全选推荐", "Select recommended"), () => { M.Selected = M.All.Where(i => i.Recommended).Select(i => i.Path).ToHashSet(StringComparer.OrdinalIgnoreCase); Build(); }),
                           Btn(L("全不选", "Select none"), () => { M.Selected.Clear(); Build(); })),
            note));

        var empty = Btn(L("清空回收站…", "Empty Recycle Bin…"), () =>
        {
            if (Confirm(L("清空回收站？", "Empty Recycle Bin?"), L($"将永久删除回收站里的 {Fmt.Bytes(M.RecycleSize)}，不能恢复。", $"This permanently deletes {Fmt.Bytes(M.RecycleSize)} in the Recycle Bin. It can’t be undone."))) M.EmptyRecycleBin();
        });
        empty.IsEnabled = M.RecycleSize > 0 && !M.Working;
        var bin = Card(L("回收站", "Recycle Bin"), "", V(8, T(Fmt.Bytes(M.RecycleSize), 24, FontWeights.SemiBold), empty));
        bin.Width = 220;
        var freed = M.Records.Sum(r => r.Bytes);
        var last = M.Records.LastOrDefault();
        var hist = Card(L("累计已清理", "Total cleaned"), "", V(8, T(Fmt.Bytes(freed), 24, FontWeights.SemiBold),
            T(last is null ? L("还没清理过", "Nothing cleaned yet") : L($"上次 {last.Date:MM-dd HH:mm} · {Fmt.Bytes(last.Bytes)}", $"Last {last.Date:MM-dd HH:mm} · {Fmt.Bytes(last.Bytes)}"), 11.5, color: Theme.Secondary)));
        hist.Width = 200;
        var top = new DockPanel();
        DockPanel.SetDock(hist, Dock.Right); DockPanel.SetDock(bin, Dock.Right);
        hist.Margin = new Thickness(14, 0, 0, 0); bin.Margin = new Thickness(14, 0, 0, 0);
        top.Children.Add(hist); top.Children.Add(bin); top.Children.Add(action);

        var parts = new List<UIElement> { header, top, HogsCard() };
        if (M.LastResult is { } res) parts.Add(H(6, Glyph("", 14, Theme.B(Theme.DiskC)), T(res, 13, color: Theme.B(Theme.DiskC))));
        if (M.LastScan is null && !M.Scanning)
            parts.Add(Card(L("准备好了", "Ready"), "", Empty("", L("点「开始扫描」", "Click “Start scan”"), L("会检查开发工具缓存、项目依赖和构建产物、应用缓存、临时文件、下载文件夹和大文件。\n扫描只读取，不会删除任何东西。", "Checks developer tool caches, project dependencies and build output, app caches, temp files, Downloads and large files.\nScanning only reads; nothing is deleted."))));
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
        string sub = items.Count == 0 ? EmptyText(cat) : L($"已选 {Fmt.Bytes(sel.Sum(i => i.Size))} · {items.Count} 项", $"{Fmt.Bytes(sel.Sum(i => i.Size))} selected · {items.Count} items");
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
                if (it.Recommended) title.Children.Add(new Border { Background = Theme.B(Theme.DiskC, 0.15), CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 0, 6, 1), Child = T(L("推荐", "Recommended"), 10.5, color: Theme.B(Theme.DiskC)) });
                if (Settings.Shared.IsWhitelisted(it.Path)) title.Children.Add(T(L("白名单", "Whitelisted"), 10.5, color: Theme.Secondary));
                var reveal = Btn("", () => Reveal(it.Path), glyph: ""); reveal.Padding = new Thickness(6, 3, 6, 3); reveal.Background = Brushes.Transparent; reveal.BorderBrush = Brushes.Transparent;
                var row = Row(H(10, cb, V(1, title, T(it.Subtitle, 11.5, color: Theme.Secondary))), H(8, T(Fmt.Bytes(it.Size), 13), reveal));
                row.Margin = new Thickness(0, 4, 0, 4);
                var cm = new ContextMenu();
                var m1 = new MenuItem { Header = L("在资源管理器中显示", "Show in File Explorer") }; m1.Click += (_, _) => Reveal(it.Path); cm.Items.Add(m1);
                var m2 = new MenuItem { Header = L("加入白名单（永不清理）", "Add to whitelist (never clean)") };
                m2.Click += (_, _) => { Settings.Shared.Whitelist.Add(Fmt.Tilde(it.Path)); Settings.Shared.Save(); M.Selected.Remove(it.Path); Build(); };
                cm.Items.Add(m2);
                row.ContextMenu = cm;
                body.Children.Add(row);
            }
            if (items.Count > 60) body.Children.Add(T(L($"还有 {items.Count - 60} 项未显示", $"{items.Count - 60} more items not shown"), 11.5, color: Theme.Secondary));
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
        CleanupCategory.LargeFiles => DiskModel.Shared.Root is null ? L("先在「磁盘」页扫描", "Scan on the Disk page first") : L($"没有超过 {Settings.Shared.LargeFileMB} MB 的文件", $"No files over {Settings.Shared.LargeFileMB} MB"),
        CleanupCategory.ProjectArtifacts => ProjectModel.Shared.Scanning ? L("正在扫描项目…", "Scanning projects…") : L("没有找到", "Nothing found"),
        _ => M.Scanning ? L("扫描中…", "Scanning…") : L("没有找到", "Nothing found"),
    };

    void Run()
    {
        var list = M.SelectedItems.ToList();
        long size = list.Sum(i => i.Size);
        var preview = string.Join("\n", list.Take(10).Select(i => $"• {i.Title}  {Fmt.Bytes(i.Size)}")) + (list.Count > 10 ? L($"\n…等 {list.Count} 项", $"\n…{list.Count} items in total") : "");
        if (!Confirm(_permanent ? L($"直接删除 {Fmt.Bytes(size)}？", $"Permanently delete {Fmt.Bytes(size)}?") : L($"把 {Fmt.Bytes(size)} 移到回收站？", $"Move {Fmt.Bytes(size)} to the Recycle Bin?"), preview + (_permanent ? L("\n\n直接删除不能恢复。", "\n\nPermanent deletion can’t be undone.") : L("\n\n会移到回收站，可以恢复。", "\n\nItems go to the Recycle Bin and can be restored.")))) return;
        M.Perform(list, _permanent, o =>
        {
            if (o.Failures.Count > 0 || o.Skipped.Count > 0)
            {
                var lines = o.Failures.Take(8).Select(f => $"{Fmt.Tilde(f.path)}：{f.error}").ToList();
                if (o.Skipped.Count > 0) lines.Add(L($"跳过 {o.Skipped.Count} 项（白名单或受保护位置）", $"Skipped {o.Skipped.Count} items (whitelisted or protected)"));
                Info(L("部分项目没有清理", "Some items weren’t cleaned"), string.Join("\n", lines));
            }
        });
    }

    Border HogsCard()
    {
        var hogs = ResourceHogs.Find();
        if (!_hogsInit && SystemMonitor.Shared.ProcessSamples >= 3) { foreach (var h in hogs.Where(h => h.Recommended)) _pickedHogs.Add(h.Group.Id); _hogsInit = true; }
        var picked = hogs.Where(h => _pickedHogs.Contains(h.Group.Id)).ToList();
        var (title, detail, level) = ResourceHogs.MemorySummary();
        var color = level >= 4 ? Theme.DangerC : level >= 2 ? Theme.WarnC : Theme.DiskC;
        var quit = Btn(L("退出所选", "Quit selected"), () =>
        {
            var names = string.Join("\n", picked.Take(8).Select(h => "• " + h.Group.Name));
            if (!Confirm(L($"关闭 {picked.Count} 个应用？", $"Close {picked.Count} apps?"),
                         names + L("\n\n会像点关闭按钮一样让它们自己保存后退出，有未保存内容的应用会先询问你。", "\n\nThey close like clicking X, so apps with unsaved work will ask you first."))) return;
            var r = ResourceHogs.Quit(picked);
            foreach (var h in picked) _pickedHogs.Remove(h.Group.Id);
            _hogResult = L($"已请求关闭 {r.count} 个应用，约可释放 {Fmt.Bytes(r.memory)} 内存", $"Asked {r.count} apps to close, freeing about {Fmt.Bytes(r.memory)}");
            SystemMonitor.Shared.RefreshProcesses();
            Build();
        }, primary: true, glyph: "\uE711");
        quit.IsEnabled = picked.Count > 0;

        var detailT = T(detail, 12, color: Theme.Secondary); detailT.TextWrapping = TextWrapping.Wrap; detailT.TextTrimming = TextTrimming.None;
        var m = SystemMonitor.Shared;
        var body = V(10,
            Row(H(10, new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(5), Background = Theme.B(color), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 5, 0, 0) },
                      V(2, T(title, 14, FontWeights.SemiBold), detailT)),
                V(2, T(Fmt.Pct(m.Memory.UsedFraction), 18, FontWeights.SemiBold), T(L($"内存 · CPU {Fmt.Pct(m.Cpu.Total)}", $"memory · CPU {Fmt.Pct(m.Cpu.Total)}"), 11, color: Theme.Secondary))));
        if (_hogResult is { } res) body.Children.Add(T(res, 12.5, color: Theme.B(Theme.DiskC)));
        body.Children.Add(new Border { Height = 1, Background = Theme.Line });
        if (hogs.Count == 0)
            body.Children.Add(T(m.ProcessSamples < 3 ? L("正在观察各个应用的占用情况，几秒后显示…", "Watching app usage, results in a few seconds…")
                                                     : L("没有发现持续占用 CPU 或长时间闲置的大内存应用，一切正常。", "No busy or long-idle memory-heavy apps found. All good."), 12.5, color: Theme.Secondary));
        foreach (var h in hogs)
        {
            var hog = h;
            var cb = Check("", _pickedHogs.Contains(h.Group.Id), on => { if (on) _pickedHogs.Add(hog.Group.Id); else _pickedHogs.Remove(hog.Group.Id); Build(); });
            var tagC = h.Cpu ? Theme.DangerC : Theme.WarnC;
            var tag = new Border { Background = Theme.B(tagC, 0.15), CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 0, 6, 1), Child = T(h.Cpu ? "CPU" : L("闲置", "Idle"), 10.5, color: Theme.B(tagC)) };
            body.Children.Add(Row(H(10, cb, PopupWindow.AppIcon(h.Group.Path, 20), V(1, H(6, T(h.Group.Name, 13), tag), T(h.Reason, 11.5, color: Theme.Secondary))),
                                 H(12, T(Fmt.Bytes(h.Group.Memory), 12.5), T($"{h.Group.Cpu * 100:0}%", 12.5, color: Theme.Secondary))));
        }
        var note = T(L("只会正常关闭应用（像点关闭按钮），不会强制结束；系统进程、资源管理器和 Peekaboo 自己不会出现在这里。",
                       "Apps are closed normally (like clicking X), never force-killed. System processes, Explorer and Peekaboo itself are never listed."), 11, color: Theme.Tertiary);
        note.TextWrapping = TextWrapping.Wrap; note.TextTrimming = TextTrimming.None;
        body.Children.Add(note);
        return Card(L("释放内存和 CPU", "Free up memory and CPU"), "\uE964", body, quit);
    }
}
