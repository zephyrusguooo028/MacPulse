using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static Peekaboo.UIX;

namespace Peekaboo;

public sealed class DuplicatesPage : PageView
{
    /// Sidebar badge: what exact duplicates would free, once a scan has run.
    public static string? Badge => DuplicatesModel.Shared.LastScan is not null && DuplicatesModel.Shared.ReclaimableExact > 0 ? Fmt.Bytes(DuplicatesModel.Shared.ReclaimableExact) : null;

    DupKind _tab = DupKind.Exact;
    DuplicatesModel M => DuplicatesModel.Shared;
    readonly Dictionary<string, ContentControl> _groupHosts = new();
    readonly TextBlock _status = T("", 12.5, color: Theme.Secondary);
    readonly ProgressBar _progress = new() { Width = 120, Height = 6, Minimum = 0, Maximum = 1 };
    TextBlock? _selectedTile;
    Button? _recycleBtn;
    bool _builtWhileScanning;

    static readonly ConcurrentDictionary<string, ImageSource> Thumbs = new(StringComparer.OrdinalIgnoreCase);

    public override void Shown() { M.Changed += OnChanged; Build(); }
    public override void Hidden() => M.Changed -= OnChanged;

    void OnChanged()
    {
        // While scanning only the progress line changes; avoid rebuilding everything.
        if (M.Scanning && _builtWhileScanning)
        {
            _status.Text = M.Status;
            _progress.IsIndeterminate = M.Fraction is null;
            _progress.Value = M.Fraction ?? 0;
            return;
        }
        Build();
    }

    static TextBlock Wrap(string t, double size = 12, Brush? color = null) { var x = T(t, size, color: color ?? Theme.Secondary); x.TextWrapping = TextWrapping.Wrap; x.TextTrimming = TextTrimming.None; return x; }

    void Build()
    {
        _builtWhileScanning = M.Scanning;
        _groupHosts.Clear();
        var actions = H(8);
        if (M.Scanning)
        {
            _status.Text = M.Status;
            _progress.IsIndeterminate = M.Fraction is null; _progress.Value = M.Fraction ?? 0;
            actions.Children.Add(_progress);
            actions.Children.Add(_status);
            actions.Children.Add(Btn(L("取消", "Cancel"), M.Cancel));
        }
        else
        {
            var scan = Btn(M.LastScan is null ? L("开始扫描", "Scan") : L("重新扫描", "Rescan"), M.Scan, primary: true, glyph: "");
            scan.IsEnabled = M.Folders.Count > 0 && !M.Working;
            actions.Children.Add(scan);
        }
        var subtitle = M.LastScan is null ? L("找出重复的文件和相似的图片，腾出空间", "Find duplicate files and similar images to free up space")
                                          : L($"扫描了 {M.Scanned} 个文件 · {Fmt.Relative(M.LastScan)}", $"Scanned {M.Scanned} files · {Fmt.Relative(M.LastScan)}");
        var page = V(14, PageHeader(L("重复文件", "Duplicates"), subtitle, actions), SettingsCard());

        if (M.LastScan is null)
        {
            page.Children.Add(Card(L("重复文件", "Duplicates"), "", Empty("", M.Scanning ? L("正在扫描…", "Scanning…") : L("还没扫描", "Not scanned yet"),
                L("找出内容完全相同的文件和看起来很像的图片。只会读取文件，不会改动任何东西；删除前会让你确认。", "Finds files with identical contents and pictures that look alike. Files are only read, never changed; you confirm before anything is removed."))));
            Content = page;
            return;
        }

        page.Children.Add(Summary());
        page.Children.Add(Toolbar());
        if (M.LastResult is { } r) page.Children.Add(T(r, 12, color: Theme.B(Theme.DiskC)));
        var groups = _tab == DupKind.Exact ? M.Exact : M.Similar;
        if (groups.Count == 0)
        {
            page.Children.Add(Card(_tab == DupKind.Exact ? L("完全相同", "Identical") : L("相似图片", "Similar images"), "",
                Empty("", L("没有找到", "Nothing found"), _tab == DupKind.Exact ? L("这些文件夹里没有内容完全相同的文件。", "No files with identical contents in these folders.") : L("没有看起来相似的图片。", "No similar-looking images."))));
        }
        else
        {
            const int max = 150;
            foreach (var g in groups.Take(max))
            {
                var host = new ContentControl { Content = GroupCard(g) };
                _groupHosts[g.Id] = host;
                page.Children.Add(host);
            }
            if (groups.Count > max) page.Children.Add(T(L($"还有 {groups.Count - max} 组未显示，处理完这些后重新扫描即可看到。", $"{groups.Count - max} more groups not shown. Rescan after handling these."), 12, color: Theme.Secondary));
        }
        Content = page;
    }

    Border SettingsCard()
    {
        var chips = new WrapPanel();
        foreach (var f in M.Folders)
        {
            var folder = f;
            var x = new Button { Content = Glyph("", 9, Theme.Tertiary), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = Cursors.Hand, Padding = new Thickness(4, 0, 0, 0), IsEnabled = !M.Scanning, ToolTip = L("不扫描这个文件夹", "Don't scan this folder") };
            x.Click += (_, _) => { M.Folders.Remove(folder); if (M.Preferred == folder) M.Preferred = null; M.Save(); Build(); };
            var chip = new Border { Background = Theme.Line, CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 4, 8, 4), Margin = new Thickness(0, 0, 6, 6), Child = H(6, Glyph("", 12, Theme.B(Theme.CpuC)), T(Fmt.Tilde(folder), 12.5), x) };
            chips.Children.Add(chip);
        }
        var add = Btn(L("添加文件夹…", "Add folder…"), AddFolder, glyph: "");
        add.IsEnabled = !M.Scanning; add.Margin = new Thickness(0, 0, 6, 6);
        chips.Children.Add(add);
        if (!M.Folders.SequenceEqual(DuplicatesModel.DefaultFolders, StringComparer.OrdinalIgnoreCase))
        {
            var reset = Btn(L("恢复默认", "Reset"), () => { M.Folders = DuplicatesModel.DefaultFolders; M.Save(); Build(); });
            reset.IsEnabled = !M.Scanning; reset.Margin = new Thickness(0, 0, 6, 6);
            chips.Children.Add(reset);
        }

        var min = new ComboBox { Width = 100, IsEnabled = !M.Scanning };
        foreach (var kb in new[] { 10, 100, 1024, 10 * 1024, 100 * 1024 })
        {
            var item = new ComboBoxItem { Content = Fmt.Bytes(kb * 1024.0), Tag = kb };
            min.Items.Add(item);
            if (kb == M.MinKB) min.SelectedItem = item;
        }
        min.SelectionChanged += (_, _) => { if (min.SelectedItem is ComboBoxItem it) { M.MinKB = (int)it.Tag; M.Save(); } };
        return Card(L("扫描范围", "What to scan"), "",
            V(6, chips, Wrap(L("会跳过隐藏和系统文件夹、node_modules、.git、符号链接/联接点，以及只在 OneDrive 云端的文件。", "Hidden and system folders, node_modules, .git, symlinks/junctions and cloud-only OneDrive files are skipped."))),
            H(6, T(L("最小文件", "Minimum size"), 12.5, color: Theme.Secondary), min));
    }

    void AddFolder()
    {
        var d = new Microsoft.Win32.OpenFolderDialog { Multiselect = true, InitialDirectory = Paths.Home, Title = L("选择要扫描的文件夹", "Choose folders to scan") };
        if (d.ShowDialog() != true) return;
        foreach (var f in d.FolderNames) if (!M.Folders.Contains(f, StringComparer.OrdinalIgnoreCase)) M.Folders.Add(f);
        M.Save();
        Build();
    }

    FrameworkElement Summary()
    {
        FrameworkElement Tile(string title, string value, string caption, Color c, out TextBlock valueText)
        {
            valueText = T(value, 22, FontWeights.SemiBold, Theme.B(c));
            var cap = T(caption, 12.5, color: Theme.Secondary); cap.VerticalAlignment = VerticalAlignment.Bottom; cap.Margin = new Thickness(0, 0, 0, 4);
            return Panel(V(2, T(title, 12, color: Theme.Secondary), H(6, valueText, cap)), 12);
        }
        var grid = new Grid();
        for (int i = 0; i < 3; i++) grid.ColumnDefinitions.Add(new ColumnDefinition());
        var a = Tile(L("完全相同", "Identical"), M.Exact.Count.ToString(), L($"组 · 可释放 {Fmt.Bytes(M.ReclaimableExact)}", $"groups · {Fmt.Bytes(M.ReclaimableExact)} reclaimable"), Theme.NetOutC, out _);
        var b = Tile(L("相似图片", "Similar images"), M.Similar.Count.ToString(), L($"组 · 约 {Fmt.Bytes(M.ReclaimableSimilar)}", $"groups · about {Fmt.Bytes(M.ReclaimableSimilar)}"), Theme.MemC, out _);
        var c = Tile(L("已选择", "Selected"), "", "", Theme.AccentColor, out var selText);
        _selectedTile = selText;
        a.Margin = new Thickness(0, 0, 6, 0); b.Margin = new Thickness(6, 0, 6, 0); c.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(b, 1); Grid.SetColumn(c, 2);
        grid.Children.Add(a); grid.Children.Add(b); grid.Children.Add(c);
        RefreshSelected();
        return grid;
    }

    void RefreshSelected()
    {
        var sel = M.SelectedFiles;
        if (_selectedTile is not null)
        {
            _selectedTile.Text = sel.Count.ToString();
            if (_selectedTile.Parent is StackPanel sp && sp.Children.Count > 1 && sp.Children[1] is TextBlock cap)
                cap.Text = L($"个文件 · {Fmt.Bytes(sel.Sum(f => f.Size))}", $"files · {Fmt.Bytes(sel.Sum(f => f.Size))}");
        }
        if (_recycleBtn is not null) _recycleBtn.IsEnabled = sel.Count > 0 && !M.Working && !M.Scanning;
    }

    FrameworkElement Toolbar()
    {
        var tabs = H(2);
        foreach (var (kind, label) in new[] { (DupKind.Exact, L($"完全相同 ({M.Exact.Count})", $"Identical ({M.Exact.Count})")), (DupKind.Similar, L($"相似图片 ({M.Similar.Count})", $"Similar images ({M.Similar.Count})")) })
        {
            var k = kind;
            var btn = Btn(label, () => { _tab = k; Build(); }, primary: _tab == k);
            btn.Padding = new Thickness(12, 4, 12, 4);
            tabs.Children.Add(btn);
        }

        var smart = Btn(L("智能选择", "Smart select"), () => { }, glyph: "");
        var menu = new ContextMenu();
        void Item(string header, bool check, Action a) { var mi = new MenuItem { Header = header, IsCheckable = true, IsChecked = check }; mi.Click += (_, _) => a(); menu.Items.Add(mi); }
        menu.Items.Add(new MenuItem { Header = L("每组保留一个", "Keep one per group"), IsEnabled = false });
        foreach (var (rule, title) in new[] { (KeepRule.Newest, L("保留最新的", "Keep the newest")), (KeepRule.Oldest, L("保留最早的", "Keep the oldest")), (KeepRule.Largest, L("保留最大的", "Keep the largest")) })
        {
            var r = rule;
            Item(title, M.Rule == r, () => { M.Rule = r; M.SmartSelect(); });
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = L("优先保留位于此处的文件", "Prefer keeping files in"), IsEnabled = false });
        Item(L("不指定", "No preference"), M.Preferred is null, () => { M.Preferred = null; M.SmartSelect(); });
        foreach (var f in M.Folders) { var folder = f; Item(Fmt.Tilde(folder), M.Preferred == folder, () => { M.Preferred = folder; M.SmartSelect(); }); }
        menu.Items.Add(new Separator());
        var none = new MenuItem { Header = L("全部取消选择", "Select none") }; none.Click += (_, _) => M.SelectNone(); menu.Items.Add(none);
        smart.ContextMenu = menu;
        smart.Click += (_, _) => { menu.PlacementTarget = smart; menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom; menu.IsOpen = true; };

        _recycleBtn = Btn(L("移到回收站", "Move to Recycle Bin"), M.RecycleSelected, primary: true, glyph: "");
        _recycleBtn.Background = Theme.B(Theme.DangerC);
        RefreshSelected();
        return Row(tabs, H(8, smart, _recycleBtn));
    }

    FrameworkElement GroupCard(DupGroup g)
    {
        var title = g.Kind == DupKind.Exact ? L($"{g.Files.Count} 个相同文件 · 每个 {Fmt.Bytes(g.Files[0].Size)}", $"{g.Files.Count} identical files · {Fmt.Bytes(g.Files[0].Size)} each")
                                            : L($"{g.Files.Count} 张相似图片", $"{g.Files.Count} similar images");
        var rows = V(0, new Border { Padding = new Thickness(14, 10, 14, 10), BorderBrush = Theme.Line, BorderThickness = new Thickness(0, 0, 0, 1),
            Child = Row(H(8, Glyph(g.Kind == DupKind.Exact ? "" : "", 13), T(title, 13, FontWeights.SemiBold)), T(L($"可释放 {Fmt.Bytes(g.Reclaimable)}", $"{Fmt.Bytes(g.Reclaimable)} reclaimable"), 12.5, color: Theme.Secondary)) });
        foreach (var f in g.Files) rows.Children.Add(FileRow(g, f));
        return Panel(rows, 0);
    }

    FrameworkElement FileRow(DupGroup g, DupFile f)
    {
        bool on = M.Selected.Contains(f.Path);
        bool can = on || M.CanSelect(f);
        var box = new CheckBox { IsChecked = on, IsEnabled = can, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        if (!can) box.ToolTip = DuplicatesModel.Deletable(f.Path) ? L("每组至少保留一个文件", "At least one file per group is kept") : L("白名单或受保护位置，不会删除", "Whitelisted or protected; never deleted");
        box.Click += (_, _) =>
        {
            M.Toggle(f, box.IsChecked == true);
            // Refresh every group this file is in (it may be in both tabs).
            foreach (var gg in M.GroupsWith(f.Path)) if (_groupHosts.TryGetValue(gg.Id, out var host)) host.Content = GroupCard(gg);
            RefreshSelected();
        };

        var name = H(6, T(Path.GetFileName(f.Path), 13));
        if (!on) name.Children.Add(new Border { Background = Theme.B(Theme.DiskC, 0.15), CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 0, 6, 1), Child = T(L("保留", "Keep"), 10.5, color: Theme.B(Theme.DiskC)) });
        if (Settings.Shared.IsWhitelisted(f.Path)) name.Children.Add(T(L("白名单", "Whitelisted"), 10.5, color: Theme.Secondary));
        var info = V(1, name, T(Fmt.Tilde(Path.GetDirectoryName(f.Path) ?? ""), 11.5, color: Theme.Secondary));
        var size = V(1, T(Fmt.Bytes(f.Size), 13), T(f.Modified.ToString("yyyy-MM-dd HH:mm"), 11.5, color: Theme.Secondary));
        foreach (var c in size.Children) ((TextBlock)c).HorizontalAlignment = HorizontalAlignment.Right;
        var reveal = new Button { Content = Glyph("", 13), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = Cursors.Hand, ToolTip = L("在资源管理器中显示", "Show in File Explorer"), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        reveal.Click += (_, _) => Reveal(f.Path);

        var grid = new Grid();
        foreach (var w in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto }) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        var thumb = Thumb(f); thumb.Margin = new Thickness(0, 0, 10, 0);
        info.VerticalAlignment = VerticalAlignment.Center; size.VerticalAlignment = VerticalAlignment.Center;
        UIElement[] cells = { box, thumb, info, size, reveal };
        for (int i = 0; i < cells.Length; i++) { Grid.SetColumn(cells[i], i); grid.Children.Add(cells[i]); }

        var menu = new ContextMenu();
        void Add(string h, Action a) { var mi = new MenuItem { Header = h }; mi.Click += (_, _) => a(); menu.Items.Add(mi); }
        Add(L("在资源管理器中显示", "Show in File Explorer"), () => Reveal(f.Path));
        Add(L("复制路径", "Copy path"), () => Clipboard.SetText(f.Path));
        Add(L("加入白名单（永不清理）", "Add to whitelist (never clean)"), () =>
        {
            Settings.Shared.Whitelist.Add(Fmt.Tilde(f.Path)); Settings.Shared.Save();
            M.Selected.Remove(f.Path);
            Build();
        });
        return new Border { Padding = new Thickness(14, 6, 14, 6), Background = on ? Theme.B(Theme.DangerC, 0.06) : Brushes.Transparent, Child = grid, ContextMenu = menu };
    }

    static FrameworkElement Thumb(DupFile f)
    {
        if (!f.IsImage)
        {
            var icon = PopupWindow.AppIcon(f.Path, 32);
            return new Border { Width = 40, Height = 40, Child = icon };
        }
        var img = new Image { Width = 40, Height = 40, Stretch = Stretch.UniformToFill };
        var frame = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(6), Background = Theme.Line, ClipToBounds = true, Child = img };
        if (Thumbs.TryGetValue(f.Path, out var cached)) { img.Source = cached; return frame; }
        Task.Run(() =>
        {
            try
            {
                using var fs = new FileStream(f.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bi.DecodePixelWidth = 80;
                bi.StreamSource = fs;
                bi.EndInit();
                bi.Freeze();
                Thumbs[f.Path] = bi;
                UiThread.Run(() => img.Source = bi);
            }
            catch { }
        });
        return frame;
    }
}
