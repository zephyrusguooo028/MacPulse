using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using static Peekaboo.UIX;

namespace Peekaboo;

/// The quick picker (Alt+Shift+V by default): search field plus recent items.
/// Up/Down + Enter (or a click) copies the item back; Esc or clicking elsewhere closes.
public sealed class ClipboardPicker : Window
{
    static ClipboardPicker? _open;

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();

    public static void Toggle()
    {
        if (_open is not null) { _open.Close(); return; }
        // Remember who was in front, for auto-paste. The hotkey message lets us take the foreground.
        var w = new ClipboardPicker(GetForegroundWindow());
        _open = w;
        w.Show();
        w.Activate();
        w._search.Focus();
    }

    readonly IntPtr _previous;
    readonly TextBox _search = new()
    {
        FontSize = 16, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
        Foreground = Theme.Text, CaretBrush = Theme.Text, VerticalContentAlignment = VerticalAlignment.Center,
    };
    readonly StackPanel _list = new();
    readonly ScrollViewer _scroll;
    readonly TextBlock _count = T("", 11.5, color: Theme.Tertiary);
    List<ClipItem> _results = new();
    readonly List<Border> _rows = new();
    int _sel;
    bool _closing;

    ClipboardPicker(IntPtr previous)
    {
        _previous = previous;
        Title = L("剪贴板", "Clipboard");
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; Topmost = true;
        Width = 580; Height = 460;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Theme.Window;
        BorderBrush = Theme.Line; BorderThickness = new Thickness(1);
        FontFamily = Theme.Font;
        SourceInitialized += (_, _) => Native.RoundCorners(this);

        var placeholder = T(L("搜索剪贴板历史", "Search clipboard history"), 16, color: Theme.Tertiary);
        placeholder.IsHitTestVisible = false; placeholder.VerticalAlignment = VerticalAlignment.Center; placeholder.Margin = new Thickness(3, 0, 0, 0);
        var field = new Grid();
        field.Children.Add(placeholder);
        field.Children.Add(_search);
        _search.TextChanged += (_, _) => { placeholder.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; Fill(); };

        var header = new Grid { Margin = new Thickness(16, 12, 16, 10) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var glass = Glyph("", 15); glass.Margin = new Thickness(0, 0, 10, 0);
        header.Children.Add(glass);
        Grid.SetColumn(field, 1); header.Children.Add(field);
        var key = T(ClipboardService.S.Hotkey?.Title ?? "", 11, color: Theme.Tertiary); key.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(key, 2); header.Children.Add(key);

        _scroll = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(6), Focusable = false };

        FrameworkElement Hint(string k, string label)
        {
            var b = new Border { Background = Theme.Hover, CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 1, 5, 1), Child = T(k, 11, FontWeights.SemiBold) };
            var s = H(4, b, T(label, 11.5, color: Theme.Secondary));
            s.Margin = new Thickness(0, 0, 14, 0);
            return s;
        }
        var footer = Row(H(0, Hint("↑↓", L("选择", "Select")), Hint("Enter", ClipboardService.S.AutoPaste ? L("粘贴", "Paste") : L("复制", "Copy")), Hint("Esc", L("关闭", "Close"))), _count);
        footer.Margin = new Thickness(14, 8, 14, 8);

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var line1 = new Border { Height = 1, Background = Theme.Line }; DockPanel.SetDock(line1, Dock.Top); root.Children.Add(line1);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var line2 = new Border { Height = 1, Background = Theme.Line }; DockPanel.SetDock(line2, Dock.Bottom); root.Children.Add(line2);
        root.Children.Add(_scroll);
        Content = root;

        PreviewKeyDown += OnKey;
        Deactivated += (_, _) => { if (!_closing) Close(); };
        Closing += (_, _) => _closing = true;
        Closed += (_, _) => { if (_open == this) _open = null; };
        Fill();
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: e.Handled = true; Close(); break;
            case Key.Down: e.Handled = true; Select(Math.Min(_sel + 1, _results.Count - 1)); break;
            case Key.Up: e.Handled = true; Select(Math.Max(_sel - 1, 0)); break;
            case Key.Enter: e.Handled = true; if (_sel >= 0 && _sel < _results.Count) Choose(_results[_sel]); break;
        }
    }

    void Fill()
    {
        var q = _search.Text.Trim();
        _results = ClipboardService.Items.Where(i => i.Matches(q)).Take(60).ToList();
        _list.Children.Clear(); _rows.Clear();
        _count.Text = L($"{ClipboardService.Items.Count} 条", $"{ClipboardService.Items.Count} items");
        if (_results.Count == 0)
        {
            _list.Children.Add(Empty("", ClipboardService.Items.Count == 0 ? L("还没有记录", "Nothing yet") : L("没有匹配的条目", "No matching items"),
                ClipboardService.Items.Count == 0 ? L("复制点东西试试。", "Copy something to get started.") : L("换个关键词试试。", "Try another search.")));
            return;
        }
        for (int i = 0; i < _results.Count; i++)
        {
            var item = _results[i]; int idx = i;
            var meta = string.Join(" · ", new[] { item.Source, Fmt.Relative(item.Date) }.Where(s => !string.IsNullOrEmpty(s)));
            var text = T(item.Preview, 13); text.TextWrapping = TextWrapping.Wrap; text.MaxHeight = 36;
            var sub = T((item.Pinned ? L("已固定 · ", "Pinned · ") : "") + meta, 11, color: Theme.Secondary);
            var body = V(2, text, sub); body.VerticalAlignment = VerticalAlignment.Center;
            var row = new DockPanel();
            var thumb = ClipboardPage.Thumb(item, 40); thumb.Margin = new Thickness(0, 0, 10, 0);
            DockPanel.SetDock(thumb, Dock.Left); row.Children.Add(thumb); row.Children.Add(body);
            var b = new Border { Child = row, Padding = new Thickness(8, 6, 8, 6), CornerRadius = new CornerRadius(7), Cursor = Cursors.Hand, Background = Brushes.Transparent };
            b.MouseEnter += (_, _) => Select(idx, scroll: false);
            b.MouseLeftButtonUp += (_, _) => Choose(item);
            _rows.Add(b);
            _list.Children.Add(b);
        }
        Select(0);
    }

    void Select(int i, bool scroll = true)
    {
        if (_rows.Count == 0) return;
        _sel = Math.Clamp(i, 0, _rows.Count - 1);
        for (int k = 0; k < _rows.Count; k++) _rows[k].Background = k == _sel ? Theme.AccentSoftBrush : Brushes.Transparent;
        if (scroll) _rows[_sel].BringIntoView();
    }

    void Choose(ClipItem item)
    {
        bool ok = ClipboardService.Copy(item);
        _closing = true;
        Close();
        if (ok && ClipboardService.S.AutoPaste) ClipboardService.PasteInto(_previous);
    }
}
