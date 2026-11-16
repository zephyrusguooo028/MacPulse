using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using static Peekaboo.UIX;

namespace Peekaboo;

/// Sidebar page: clipboard history with search, filters, pin, delete and copy.
public sealed class ClipboardPage : PageView
{
    public static string? Badge => ClipboardService.S.Enabled && ClipboardService.Items.Count > 0 ? ClipboardService.Items.Count.ToString("N0") : null;

    readonly TextBox _search = new()
    {
        Width = 240, Padding = new Thickness(6, 4, 6, 4), VerticalContentAlignment = VerticalAlignment.Center,
        Background = Theme.Card, Foreground = Theme.Text, CaretBrush = Theme.Text, BorderBrush = Theme.Line,
    };
    readonly StackPanel _chips = new() { Orientation = Orientation.Horizontal };
    readonly ContentControl _body = new();
    readonly ContentControl _actions = new();
    readonly ContentControl _list = new();
    readonly FrameworkElement _enabledView;
    ClipKind? _filter;
    bool _dirty;
    Guid? _copied;

    public ClipboardPage()
    {
        var placeholder = T(L("搜索", "Search"), 13, color: Theme.Tertiary);
        placeholder.IsHitTestVisible = false; placeholder.VerticalAlignment = VerticalAlignment.Center; placeholder.Margin = new Thickness(9, 0, 0, 0);
        var searchBox = new Grid { VerticalAlignment = VerticalAlignment.Center };
        searchBox.Children.Add(_search); searchBox.Children.Add(placeholder);
        _search.TextChanged += (_, _) => { placeholder.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; Fill(); };
        _enabledView = V(16, H(10, searchBox, _chips), _list);
        var privacy = T(L($"剪贴板历史只保存在本机（{Fmt.Tilde(ClipboardService.Folder)}），从不联网或上传。密码管理器等标记为「不要记录」的内容会自动跳过。",
                          $"Clipboard history is stored only on this PC ({Fmt.Tilde(ClipboardService.Folder)}) and is never uploaded. Content that password managers mark as “don’t record” is skipped automatically."), 12.5, color: Theme.Secondary);
        privacy.TextWrapping = TextWrapping.Wrap; privacy.TextTrimming = TextTrimming.None;
        var hotkey = ClipboardService.S.Hotkey?.Title ?? "—";
        Content = V(16,
            PageHeader(L("剪贴板", "Clipboard"), L($"复制过的文字、图片、链接和文件都在这里，按 {hotkey} 随时调出", $"Everything you’ve copied: text, images, links and files. Press {hotkey} to bring it up anywhere"), _actions),
            _body,
            Card(L("隐私", "Privacy"), "", privacy));
        Unloaded += (_, _) => ClipboardService.Changed -= OnChanged;
    }

    public override void Shown()
    {
        ClipboardService.Changed -= OnChanged;
        ClipboardService.Changed += OnChanged;
        Fill();
    }

    public override void Hidden() => ClipboardService.Changed -= OnChanged;

    public override void Tick() { if (_dirty) Fill(); }

    void OnChanged() { if (IsVisible) Fill(); else _dirty = true; }

    void Fill()
    {
        _dirty = false;
        var items = ClipboardService.Items;
        var quick = Btn(L("快速面板", "Quick picker"), ClipboardPicker.Toggle, glyph: "");
        quick.IsEnabled = ClipboardService.S.Enabled;
        var clear = Btn(L("清空", "Clear"), ClipboardService.ConfirmClear, glyph: "");
        clear.IsEnabled = items.Any(i => !i.Pinned);
        _actions.Content = H(8, quick, clear);

        if (!ClipboardService.S.Enabled)
        {
            var on = Btn(L("开启剪贴板历史", "Turn on clipboard history"), () => { ClipboardService.S.Enabled = true; ClipboardService.S.Save(); ClipboardService.Apply(); Fill(); }, primary: true);
            on.HorizontalAlignment = HorizontalAlignment.Center;
            _body.Content = Card(L("剪贴板历史", "Clipboard history"), "", V(0,
                Empty("", L("剪贴板历史已关闭", "Clipboard history is off"), L("打开后，Peekaboo 会在本机记住你复制过的内容。", "When it’s on, Peekaboo remembers what you copy, on this PC only.")), on));
            return;
        }

        _chips.Children.Clear();
        void Chip(ClipKind? k, string title, int count)
        {
            var b = Btn($"{title}  {count}", () => { _filter = k; Fill(); }, primary: _filter == k, glyph: k is { } kk ? ClipItem.KindGlyph(kk) : null);
            b.Padding = new Thickness(10, 3, 10, 3); b.Margin = new Thickness(0, 0, 6, 0);
            _chips.Children.Add(b);
        }
        Chip(null, L("全部", "All"), items.Count);
        foreach (var k in Enum.GetValues<ClipKind>()) Chip(k, ClipItem.KindTitle(k), items.Count(i => i.Kind == k));

        var q = _search.Text.Trim();
        var shown = items.Where(i => (_filter is null || i.Kind == _filter) && i.Matches(q))
                         .OrderByDescending(i => i.Pinned).ThenByDescending(i => i.Date).ToList();
        FrameworkElement list;
        if (shown.Count == 0)
        {
            list = Empty("", items.Count == 0 ? L("还没有记录", "Nothing yet") : L("没有匹配的条目", "No matching items"),
                items.Count == 0 ? L("复制一段文字、一张图片或一个文件，它就会出现在这里。", "Copy some text, an image or a file and it shows up here.") : L("换个关键词或筛选条件试试。", "Try another search or filter."));
        }
        else
        {
            var stack = new StackPanel();
            for (int i = 0; i < shown.Count; i++)
            {
                if (i > 0) stack.Children.Add(new Border { Height = 1, Background = Theme.Line, Margin = new Thickness(54, 0, 0, 0) });
                stack.Children.Add(RowFor(shown[i]));
            }
            list = stack;
        }
        // The search bar is created once so typing keeps focus; only the list card is rebuilt.
        var pinned = items.Count(i => i.Pinned);
        _list.Content = Card(L("历史记录", "History"), "", list, T(L($"{items.Count} / {ClipboardService.S.MaxItems} 条 · 已固定 {pinned}", $"{items.Count} / {ClipboardService.S.MaxItems} · {pinned} pinned"), 12, color: Theme.Secondary));
        if (_body.Content != _enabledView) _body.Content = _enabledView;
    }

    FrameworkElement RowFor(ClipItem item)
    {
        var meta = string.Join(" · ", new[] { item.Source, Fmt.Relative(item.Date) }.Where(s => !string.IsNullOrEmpty(s)));
        var text = T(item.Preview, 13, color: item.Kind == ClipKind.Link ? Theme.AccentBrush : Theme.Text);
        text.TextWrapping = TextWrapping.Wrap; text.MaxHeight = 38;
        var body = V(3, text, H(4, Glyph(ClipItem.KindGlyph(item.Kind), 10), T(meta, 11.5, color: Theme.Secondary)));
        body.VerticalAlignment = VerticalAlignment.Center;

        Button Icon(string glyph, string tip, Brush? color, Action a)
        {
            var b = new Button
            {
                Content = Glyph(glyph, 14, color ?? Theme.Secondary), ToolTip = tip, Width = 30, Height = 30, Cursor = Cursors.Hand,
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), Focusable = false,
            };
            b.Template = FlatTemplate();
            b.Click += (_, _) => a();
            return b;
        }
        bool copied = _copied == item.Id;
        var buttons = H(2,
            Icon(copied ? "" : "", copied ? L("已复制", "Copied") : L("复制", "Copy"), copied ? Theme.B(Theme.DiskC) : null, () =>
            {
                if (!ClipboardService.Copy(item)) return;
                _copied = item.Id;
                var id = item.Id;
                _ = Task.Delay(1500).ContinueWith(_ => UiThread.Run(() => { if (_copied == id) { _copied = null; Fill(); } }));
            }),
            Icon(item.Pinned ? "" : "", item.Pinned ? L("取消固定", "Unpin") : L("固定（不会被自动清除）", "Pin (never removed automatically)"), item.Pinned ? Theme.AccentBrush : null, () => ClipboardService.TogglePin(item)),
            Icon("", L("删除", "Delete"), null, () => ClipboardService.Delete(item)));

        var row = new DockPanel { Margin = new Thickness(0, 8, 0, 8) };
        var thumb = Thumb(item, 42); thumb.Margin = new Thickness(0, 0, 12, 0);
        DockPanel.SetDock(thumb, Dock.Left); row.Children.Add(thumb);
        DockPanel.SetDock(buttons, Dock.Right); buttons.VerticalAlignment = VerticalAlignment.Center; buttons.Margin = new Thickness(8, 0, 0, 0); row.Children.Add(buttons);
        row.Children.Add(body);

        var m = new ContextMenu();
        void Add(string h, Action a) { var mi = new MenuItem { Header = h }; mi.Click += (_, _) => a(); m.Items.Add(mi); }
        Add(L("复制", "Copy"), () => ClipboardService.Copy(item));
        Add(item.Pinned ? L("取消固定", "Unpin") : L("固定", "Pin"), () => ClipboardService.TogglePin(item));
        if (item.Kind == ClipKind.File && item.Paths?.FirstOrDefault() is { } p) Add(L("在资源管理器中显示", "Show in File Explorer"), () => Reveal(p));
        if (item.Kind == ClipKind.Link && item.Text is { } url)
            Add(L("在浏览器中打开", "Open in browser"), () => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url.Trim()) { UseShellExecute = true }); } catch { } });
        m.Items.Add(new Separator());
        Add(L("删除", "Delete"), () => ClipboardService.Delete(item));
        row.ContextMenu = m;
        row.Background = Brushes.Transparent;   // so the whole row takes right-clicks
        return row;
    }

    static ControlTemplate? _flat;
    static ControlTemplate FlatTemplate()
    {
        if (_flat is not null) return _flat;
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(cp);
        var t = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BackgroundProperty, Theme.Hover));
        t.Triggers.Add(hover);
        _flat = t;
        return t;
    }

    /// Thumbnail for images, shell icon for files, kind glyph otherwise.
    public static FrameworkElement Thumb(ClipItem item, double size)
    {
        var box = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(7), Background = Theme.Hover, ClipToBounds = true };
        if (item.Kind == ClipKind.Image && ClipboardService.Thumbnail(item) is { } img)
        {
            box.Background = new ImageBrush(img) { Stretch = Stretch.UniformToFill };
            return box;
        }
        if (item.Kind == ClipKind.File && item.Paths?.FirstOrDefault() is { } p && FileIcon(p) is { } icon)
        {
            box.Background = Brushes.Transparent;
            box.Child = new Image { Source = icon, Width = size * 0.8, Height = size * 0.8 };
            return box;
        }
        var g = Glyph(ClipItem.KindGlyph(item.Kind), size * 0.4, item.Kind == ClipKind.Link ? Theme.AccentBrush : Theme.Secondary);
        g.HorizontalAlignment = HorizontalAlignment.Center;
        box.Child = g;
        return box;
    }

    static System.Windows.Media.Imaging.BitmapSource? FileIcon(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var ico = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (ico is null) return null;
            var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(ico.Handle, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch { return null; }
    }
}

/// Clipboard card in Settings.
public static class ClipboardSettingsCard
{
    public static UIElement Build()
    {
        var host = new ContentControl();
        void Paint() => host.Content = Card(L("剪贴板历史", "Clipboard history"), "", Body(Paint, host));
        Paint();
        return host;
    }

    static UIElement Body(Action repaint, FrameworkElement host)
    {
        var S = ClipboardService.S;
        var enabled = Check(L("记录剪贴板历史", "Keep clipboard history"), S.Enabled, on => { S.Enabled = on; S.Save(); ClipboardService.Apply(); repaint(); });

        // Shortcut recorder (same behaviour as the screenshot one).
        var status = T(ClipboardService.HotkeyOK ? "" : L("这个快捷键被别的程序占用了，换一个试试。", "This shortcut is used by another program. Try a different one."), 12, color: Theme.B(Theme.DangerC));
        var recorder = new Button { Padding = new Thickness(12, 5, 12, 5), MinWidth = 170, Cursor = Cursors.Hand, IsEnabled = S.Enabled,
            ToolTip = L("点一下，然后按下想要的组合键。Esc 取消，Backspace 清除。", "Click, then press the key combination you want. Esc cancels, Backspace clears.") };
        recorder.Content = T(S.Hotkey?.Title ?? L("未设置", "Not set"), 13, FontWeights.SemiBold);
        bool recording = false;
        KeyEventHandler? handler = null;
        void Stop()
        {
            recording = false;
            if (handler is not null && Window.GetWindow(host) is { } w) w.PreviewKeyDown -= handler;
            ClipboardService.ApplyHotkey();
            repaint();
        }
        recorder.Click += (_, _) =>
        {
            if (recording) { Stop(); return; }
            recording = true;
            ClipboardService.SuspendHotkey();
            recorder.Content = T(L("请按下快捷键…", "Press a shortcut…"), 13, FontWeights.SemiBold, Theme.AccentBrush);
            recorder.BorderBrush = Theme.AccentBrush;
            handler = (_, e) =>
            {
                var key = e.Key == Key.System ? e.SystemKey : e.Key;
                e.Handled = true;
                if (key == Key.Escape) { Stop(); return; }
                if (key == Key.Back && Keyboard.Modifiers == ModifierKeys.None) { S.Hotkey = null; S.Save(); Stop(); return; }
                if (HotkeyData.From(key, Keyboard.Modifiers) is { } spec) { S.Hotkey = spec; S.Save(); Stop(); }
                else if (key is not (Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin))
                    recorder.Content = T(L("要加上 Ctrl / Alt / Win", "Include Ctrl / Alt / Win"), 13, FontWeights.SemiBold, Theme.B(Theme.WarnC));
            };
            if (Window.GetWindow(host) is { } win) win.PreviewKeyDown += handler;
        };
        var reset = Btn(L("恢复默认", "Restore default"), () => { S.Hotkey = ClipboardSettings.DefaultHotkey; S.Save(); ClipboardService.ApplyHotkey(); repaint(); });
        reset.IsEnabled = S.Enabled && !ClipboardSettings.DefaultHotkey.Same(S.Hotkey);

        var max = new ComboBox { Width = 110 };
        foreach (var n in new[] { 50, 100, 200, 500, 1000 })
        {
            var it = new ComboBoxItem { Content = L($"{n} 条", $"{n} items"), Tag = n };
            max.Items.Add(it);
            if (S.MaxItems == n) max.SelectedItem = it;
        }
        max.SelectionChanged += (_, _) => { if (max.SelectedItem is ComboBoxItem { Tag: int n }) ClipboardService.SetMaxItems(n); };

        var autoPaste = Check(L("选中后自动粘贴到当前应用（模拟按下 Ctrl+V）", "Paste into the current app after choosing (sends Ctrl+V)"), S.AutoPaste, on => { S.AutoPaste = on; S.Save(); });

        var clear = Btn(L("清空历史…", "Clear history…"), () => { ClipboardService.ConfirmClear(); repaint(); });
        clear.IsEnabled = ClipboardService.Items.Any(i => !i.Pinned);
        var note = T(L($"当前 {ClipboardService.Items.Count} 条记录，只保存在本机。密码管理器的内容会自动跳过。", $"{ClipboardService.Items.Count} items, stored on this PC only. Password-manager content is skipped."), 12, color: Theme.Secondary);

        return V(10,
            enabled,
            H(8, T(L("快速面板快捷键", "Quick picker shortcut"), 13), recorder, reset, status),
            H(8, T(L("最多保留", "Keep up to"), 13), max, T(L("已固定的条目不会被自动清除", "Pinned items are never removed automatically"), 12, color: Theme.Secondary)),
            autoPaste,
            Row(note, clear));
    }
}
