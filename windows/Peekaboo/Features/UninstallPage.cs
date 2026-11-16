using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using static Peekaboo.UIX;

namespace Peekaboo;

public sealed class UninstallPage : PageView
{
    string _sort = "size";
    string _query = "";
    readonly TextBox _search = new() { Width = 170, Padding = new Thickness(6, 4, 6, 4), VerticalContentAlignment = VerticalAlignment.Center };
    readonly ComboBox _sortBox = new() { Width = 130 };
    readonly TextBlock _subtitle = T("", 13, color: Theme.Secondary);
    readonly ContentControl _status = new();
    readonly StackPanel _list = new();
    readonly ContentControl _detail = new() { Width = 390, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
    UninstallModel M => UninstallModel.Shared;

    public UninstallPage()
    {
        _search.TextChanged += (_, _) => { _query = _search.Text.Trim(); FillList(); };
        foreach (var (k, t) in new[] { ("size", L("按大小", "By size")), ("name", L("按名称", "By name")), ("date", L("按安装日期", "By install date")), ("old", L("按最早安装", "Oldest installed")) })
        {
            var item = new ComboBoxItem { Content = t, Tag = k };
            _sortBox.Items.Add(item);
            if (k == _sort) _sortBox.SelectedItem = item;
        }
        _sortBox.SelectionChanged += (_, _) => { _sort = (string)((ComboBoxItem)_sortBox.SelectedItem).Tag; FillList(); };
        var reload = Btn("", M.Load, glyph: ""); reload.ToolTip = L("重新读取", "Reload");

        var body = new DockPanel();
        DockPanel.SetDock(_detail, Dock.Right);
        body.Children.Add(_detail);
        body.Children.Add(_list);
        Content = V(16, Row(V(2, T(L("卸载应用", "Uninstall"), 26, FontWeights.Bold), _subtitle), H(8, _search, _sortBox, reload)), _status, body);
    }

    public override void Shown()
    {
        M.Changed += Refresh;
        if (M.Entries.Count == 0 && !M.Loading) M.Load();
        Refresh();
    }
    public override void Hidden() => M.Changed -= Refresh;

    void Refresh()
    {
        long total = M.Entries.Sum(e => e.Size);
        _subtitle.Text = M.Loading ? L("正在读取已安装的程序…", "Reading installed programs…")
                       : L($"{M.Entries.Count} 个程序，约 {Fmt.Bytes(total)}（大小由安装程序登记，可能不准）", $"{M.Entries.Count} programs, about {Fmt.Bytes(total)} (sizes are reported by the installers and may be off)");
        _status.Content = M.LastResult is { } r ? T(r, 12.5, color: Theme.B(Theme.DiskC)) : null;
        FillList();
        _detail.Content = Detail();
    }

    IEnumerable<InstalledEntry> Sorted()
    {
        IEnumerable<InstalledEntry> l = M.Entries;
        if (_query.Length > 0) l = l.Where(e => e.Name.Contains(_query, StringComparison.CurrentCultureIgnoreCase) || e.Publisher.Contains(_query, StringComparison.CurrentCultureIgnoreCase));
        return _sort switch
        {
            "name" => l.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase),
            "date" => l.OrderByDescending(e => e.InstallDate ?? DateTime.MinValue),
            "old" => l.OrderBy(e => e.InstallDate ?? DateTime.MaxValue),
            _ => l.OrderByDescending(e => e.Size),
        };
    }

    void FillList()
    {
        _list.Children.Clear();
        var list = Sorted().ToList();
        if (list.Count == 0 && !M.Loading)
        {
            _list.Children.Add(Panel(Empty("", L("没有程序", "No programs"), _query.Length > 0 ? L($"没有匹配「{_query}」的程序。", $"No programs match “{_query}”.") : L("没有读取到已安装的程序。", "No installed programs were found."))));
            return;
        }
        double max = Math.Max(1, list.Count == 0 ? 1 : list.Max(e => e.Size));
        foreach (var e in list)
        {
            var entry = e;
            var meta = string.Join(" · ", new[] { e.Publisher, e.Version, e.InstallDate is { } d ? L($"安装于 {d:yyyy-MM-dd}", $"installed {d:yyyy-MM-dd}") : "" }.Where(s => s.Length > 0));
            var right = T(e.Size > 0 ? Fmt.Bytes(e.Size) : "—", 13, FontWeights.SemiBold);
            var title = H(8, T(e.Name, 13.5, FontWeights.SemiBold));
            if (e.Protected is not null) title.Children.Add(new Border { Background = Theme.B(Theme.WarnC, 0.15), CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 0, 6, 1), Child = T(L("系统组件", "System"), 10.5, color: Theme.B(Theme.WarnC)) });
            var row = new Border
            {
                Padding = new Thickness(12, 9, 12, 9), CornerRadius = new CornerRadius(10), Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 0, 4),
                Background = M.SelectedId == e.Id ? Theme.AccentSoftBrush : Theme.Card, BorderBrush = Theme.Line, BorderThickness = new Thickness(1),
            };
            var icon = PopupWindow.AppIcon(e.IconPath, 28);
            icon.Margin = new Thickness(0, 0, 10, 0);
            icon.VerticalAlignment = VerticalAlignment.Center;
            var dock = new DockPanel();
            DockPanel.SetDock(icon, Dock.Left);
            dock.Children.Add(icon);
            dock.Children.Add(V(5, Row(title, right), Bar(e.Size / max, Color.FromRgb(204, 77, 89), 5), T(meta, 11.5, color: Theme.Secondary)));
            row.Child = dock;
            row.MouseLeftButtonUp += (_, _) => M.Select(entry);
            _list.Children.Add(row);
        }
    }

    FrameworkElement Detail()
    {
        if (M.Running is { } run)
        {
            return Card(run.Name, "", V(12,
                H(10, new ProgressBar { IsIndeterminate = true, Width = 120, Height = 6, VerticalAlignment = VerticalAlignment.Center }, T(L("正在等卸载程序完成…", "Waiting for the uninstaller to finish…"), 13)),
                Note(L("请在弹出的卸载程序里完成操作。有些卸载程序会在后台继续运行，Peekaboo 会等到它从程序列表里消失（最多 10 分钟）。", "Finish the steps in the uninstaller window. Some uninstallers keep running in the background; Peekaboo waits until the program leaves the list (up to 10 minutes).")),
                Btn(L("卸载已完成，查找残留", "Done, look for leftovers"), M.StopWaiting, primary: true)));
        }
        var e = M.Selected ?? M.LeftoverFor;
        if (e is null)
            return Card(L("详情", "Details"), "", Empty("", L("选一个程序", "Pick a program"),
                L("在左边选一个程序。卸载会运行它自带的卸载程序，结束后再找出它留在 AppData 和 ProgramData 里的文件，由你决定是否移到回收站。", "Choose a program on the left. Uninstalling runs its own uninstaller, then finds files it left in AppData and ProgramData so you can decide whether to recycle them.")));

        var s = V(10);
        var meta = V(3);
        foreach (var line in new[] { e.Publisher, e.Version.Length > 0 ? L($"版本 {e.Version}", $"Version {e.Version}") : "", e.InstallLocation, e.InstallDate is { } d ? L($"安装于 {d:yyyy-MM-dd}", $"Installed {d:yyyy-MM-dd}") : "" }.Where(x => x.Length > 0))
            meta.Children.Add(Note(line));
        var head = new DockPanel();
        var icon = PopupWindow.AppIcon(e.IconPath, 40); icon.Margin = new Thickness(0, 0, 12, 0); icon.VerticalAlignment = VerticalAlignment.Top;
        DockPanel.SetDock(icon, Dock.Left);
        head.Children.Add(icon); head.Children.Add(meta);
        s.Children.Add(head);

        bool installed = M.Entries.Any(x => x.Id == e.Id);
        if (e.Protected is { } why)
        {
            s.Children.Add(Note(why, Theme.B(Theme.WarnC)));
        }
        else if (installed)
        {
            var btn = Btn(L("卸载", "Uninstall"), () => M.Uninstall(e), primary: true, glyph: "");
            btn.Background = Theme.B(Theme.DangerC);
            btn.HorizontalAlignment = HorizontalAlignment.Stretch;
            s.Children.Add(btn);
            s.Children.Add(Note(L("会运行程序自带的卸载程序，可能弹出管理员权限提示。", "Runs the program's own uninstaller; you may see an administrator prompt.")));
        }

        if (M.LeftoverFor?.Id == e.Id)
        {
            s.Children.Add(new Border { Height = 1, Background = Theme.Line, Margin = new Thickness(0, 4, 0, 4) });
            long picked = M.Leftovers.Where(l => M.Picked.Contains(l.Path)).Sum(l => l.Size);
            s.Children.Add(Row(H(8, T(L("残留文件", "Leftover files"), 13, FontWeights.SemiBold)), T(Fmt.Bytes(picked), 13)));
            if (M.FindingLeftovers) s.Children.Add(Note(L("正在查找…", "Looking…")));
            else if (M.Leftovers.Count == 0) s.Children.Add(Note(L("没有找到残留，卸载得很干净。", "Nothing left behind.")));
            foreach (var l in M.Leftovers)
            {
                var lo = l;
                var box = new CheckBox { IsChecked = M.Picked.Contains(l.Path), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 8, 0) };
                box.Click += (_, _) => { if (box.IsChecked == true) M.Picked.Add(lo.Path); else M.Picked.Remove(lo.Path); Refresh(); };
                var info = V(1, T(l.Kind, 12.5), T(Fmt.Tilde(l.Path), 11.5, color: Theme.Secondary));
                if (l.Note is { } n) info.Children.Add(Note(n, Theme.B(Theme.WarnC)));
                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                DockPanel.SetDock(box, Dock.Left);
                var size = T(Fmt.Bytes(l.Size), 12.5); size.Margin = new Thickness(8, 0, 0, 0);
                DockPanel.SetDock(size, Dock.Right);
                row.Children.Add(box); row.Children.Add(size); row.Children.Add(info);
                var menu = new ContextMenu();
                var mi1 = new MenuItem { Header = L("在资源管理器中显示", "Show in File Explorer") }; mi1.Click += (_, _) => Reveal(lo.Path); menu.Items.Add(mi1);
                var mi2 = new MenuItem { Header = L("复制路径", "Copy path") }; mi2.Click += (_, _) => Clipboard.SetText(lo.Path); menu.Items.Add(mi2);
                var mi3 = new MenuItem { Header = L("加入白名单（永不清理）", "Add to whitelist (never clean)") };
                mi3.Click += (_, _) => { Settings.Shared.Whitelist.Add(Fmt.Tilde(lo.Path)); Settings.Shared.Save(); M.Picked.Remove(lo.Path); M.Leftovers.Remove(lo); Refresh(); };
                menu.Items.Add(mi3);
                row.ContextMenu = menu;
                s.Children.Add(row);
            }
            if (M.Leftovers.Count > 0)
            {
                var rb = Btn(L("移到回收站", "Move to Recycle Bin"), M.RecycleLeftovers, primary: true, glyph: "");
                rb.IsEnabled = picked > 0 && !M.Working;
                rb.HorizontalAlignment = HorizontalAlignment.Stretch;
                s.Children.Add(rb);
                s.Children.Add(Note(L("ProgramData 和 Program Files 里的文件夹可能需要管理员权限，移不走的会告诉你。", "Folders in ProgramData and Program Files may need administrator rights; anything that can't be moved is reported.")));
            }
        }
        return Card(e.Name, "", s);
    }

    static TextBlock Note(string t, Brush? color = null) { var x = T(t, 12, color: color ?? Theme.Secondary); x.TextWrapping = TextWrapping.Wrap; x.TextTrimming = TextTrimming.None; return x; }
}
