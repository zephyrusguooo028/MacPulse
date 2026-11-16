using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;

namespace Peekaboo;

/// Small builder helpers so pages can be written in plain C#.
/// StackPanel that puts `Spacing` between children, however they are added.
public sealed class SpacedStack : StackPanel
{
    public double Spacing { get; set; }

    protected override void OnVisualChildrenChanged(DependencyObject added, DependencyObject removed)
    {
        base.OnVisualChildrenChanged(added, removed);
        if (added is FrameworkElement fe && Children.Count > 1)
        {
            var m = fe.Margin;
            fe.Margin = Orientation == Orientation.Vertical ? new Thickness(m.Left, m.Top + Spacing, m.Right, m.Bottom)
                                                            : new Thickness(m.Left + Spacing, m.Top, m.Right, m.Bottom);
        }
    }
}

public static class UIX
{
    public static TextBlock T(string text, double size = 13, FontWeight? weight = null, Brush? color = null)
        => new() { Text = text, FontSize = size, FontWeight = weight ?? FontWeights.Normal, Foreground = color ?? Theme.Text, FontFamily = Theme.Font, TextTrimming = TextTrimming.CharacterEllipsis };

    public static TextBlock Glyph(string g, double size = 14, Brush? color = null)
        => new() { Text = g, FontFamily = Theme.IconFont, FontSize = size, Foreground = color ?? Theme.Secondary, VerticalAlignment = VerticalAlignment.Center };

    public static StackPanel V(double spacing, params UIElement[] kids) => Stack(Orientation.Vertical, spacing, kids);
    public static StackPanel H(double spacing, params UIElement[] kids) => Stack(Orientation.Horizontal, spacing, kids);

    static StackPanel Stack(Orientation o, double spacing, UIElement[] kids)
    {
        var s = new SpacedStack { Orientation = o, Spacing = spacing };
        foreach (var k in kids) s.Children.Add(k);
        return s;
    }

    /// Row with a left part, flexible middle, right part.
    public static Grid Row(UIElement left, UIElement? right = null, double gap = 8)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (left is FrameworkElement lf && lf.VerticalAlignment == VerticalAlignment.Stretch) lf.VerticalAlignment = VerticalAlignment.Center;
        g.Children.Add(left);
        if (right is FrameworkElement rf && rf.VerticalAlignment == VerticalAlignment.Stretch) rf.VerticalAlignment = VerticalAlignment.Center;
        if (right is not null) { Grid.SetColumn(right, 1); if (right is FrameworkElement fe) fe.Margin = new Thickness(gap, 0, 0, 0); g.Children.Add(right); }
        return g;
    }

    public static Border Card(string title, string? glyph, UIElement content, UIElement? trailing = null)
    {
        var header = glyph is not null ? H(8, Glyph(glyph, 14), T(title, 14, FontWeights.SemiBold)) : H(0, T(title, 14, FontWeights.SemiBold));
        var body = V(12, Row(header, trailing), content);
        return Panel(body);
    }

    public static Border Panel(UIElement child, double padding = 16, Brush? bg = null) => new()
    {
        Background = bg ?? Theme.Card,
        CornerRadius = new CornerRadius(10),
        BorderBrush = Theme.Line,
        BorderThickness = new Thickness(1),
        Padding = new Thickness(padding),
        Child = child,
    };

    public static Button Btn(string text, Action onClick, bool primary = false, string? glyph = null)
    {
        var parts = new List<UIElement>();
        if (glyph is not null) parts.Add(Glyph(glyph, 12, primary ? Theme.OnAccent : Theme.Text));
        if (text.Length > 0) parts.Add(T(text, 13, FontWeights.Medium, primary ? Theme.OnAccent : Theme.Text));
        var content = H(6, parts.ToArray());
        var b = new Button
        {
            Content = content,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(12, 6, 12, 6),
            Cursor = Cursors.Hand,
            Background = primary ? Theme.AccentBrush : Theme.CardAlt,
            BorderBrush = primary ? Brushes.Transparent : Theme.Line,
            BorderThickness = new Thickness(1),
            Template = RoundedButtonTemplate(),
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    static ControlTemplate? _btnTemplate;
    static ControlTemplate RoundedButtonTemplate()
    {
        if (_btnTemplate is not null) return _btnTemplate;
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(cp);
        var t = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(UIElement.OpacityProperty, 0.85));
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.4));
        t.Triggers.Add(hover); t.Triggers.Add(disabled);
        _btnTemplate = t;
        return t;
    }

    public static CheckBox Check(string text, bool on, Action<bool> changed)
    {
        var c = new CheckBox { Content = T(text), IsChecked = on, VerticalContentAlignment = VerticalAlignment.Center, Foreground = Theme.Text };
        c.Checked += (_, _) => changed(true);
        c.Unchecked += (_, _) => changed(false);
        return c;
    }

    public static FrameworkElement Bar(double fraction, Color color, double height = 6)
    {
        var g = new Grid { Height = height };
        g.Children.Add(new Border { Background = Theme.Line, CornerRadius = new CornerRadius(height / 2) });
        var fill = new Border { Background = Theme.B(color), CornerRadius = new CornerRadius(height / 2), HorizontalAlignment = HorizontalAlignment.Left };
        g.Children.Add(fill);
        g.SizeChanged += (_, e) => fill.Width = Math.Max(height, e.NewSize.Width * Math.Clamp(fraction, 0, 1));
        return g;
    }

    public static FrameworkElement Stacked(IList<(string label, double value, Color color)> segs, double total, double height = 14, bool legend = true)
    {
        var bar = new Grid { Height = height, ClipToBounds = true };
        bar.Children.Add(new Border { Background = Theme.Line, CornerRadius = new CornerRadius(4) });
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        bar.Children.Add(new Border { CornerRadius = new CornerRadius(4), Child = row, ClipToBounds = true });
        var parts = segs.Select(s => new Border { Background = Theme.B(s.color), Margin = new Thickness(0, 0, 1.5, 0) }).ToList();
        foreach (var p in parts) row.Children.Add(p);
        bar.SizeChanged += (_, e) =>
        {
            for (int i = 0; i < segs.Count; i++) parts[i].Width = total > 0 ? Math.Max(0, e.NewSize.Width * segs[i].value / total - 1.5) : 0;
        };
        if (!legend) return bar;
        var lg = new WrapPanel();
        foreach (var s in segs)
        {
            var item = H(5, new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(2), Background = Theme.B(s.color) },
                           T(s.label, 12, color: Theme.Secondary), T(Fmt.Bytes(s.value), 12));
            item.Margin = new Thickness(0, 0, 14, 0);
            lg.Children.Add(item);
        }
        return V(8, bar, lg);
    }

    /// Ring gauge with a centered label.
    public static FrameworkElement Ring(double value, Color color, string label, double size = 72, double thickness = 7, string? caption = null)
    {
        var g = new Grid { Width = size, Height = size };
        g.Children.Add(new Ellipse { Stroke = Theme.B(color, 0.18), StrokeThickness = thickness, Margin = new Thickness(thickness / 2) });
        g.Children.Add(Arc(value, color, size, thickness));
        var center = V(0, T(label, size > 60 ? 17 : 14, FontWeights.SemiBold));
        ((TextBlock)center.Children[0]).HorizontalAlignment = HorizontalAlignment.Center;
        if (caption is not null) { var c = T(caption, 10, color: Theme.Secondary); c.HorizontalAlignment = HorizontalAlignment.Center; center.Children.Add(c); }
        center.HorizontalAlignment = HorizontalAlignment.Center; center.VerticalAlignment = VerticalAlignment.Center;
        g.Children.Add(center);
        return g;
    }

    static Path Arc(double v, Color color, double size, double thickness)
    {
        v = Math.Clamp(v, 0.001, 0.9999);
        double r = (size - thickness) / 2, c = size / 2;
        double a = v * 2 * Math.PI - Math.PI / 2;
        var start = new Point(c, c - r);
        var end = new Point(c + r * Math.Cos(a), c + r * Math.Sin(a));
        var fig = new PathFigure { StartPoint = start, IsClosed = false };
        fig.Segments.Add(new ArcSegment(end, new Size(r, r), 0, v > 0.5, SweepDirection.Clockwise, true));
        return new Path
        {
            Data = new PathGeometry(new[] { fig }),
            Stroke = Theme.B(color),
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };
    }

    /// Area sparkline. maxValue null = auto scale.
    public static FrameworkElement Spark(IList<double> values, Color color, double height, double? maxValue = 1)
    {
        var canvas = new Canvas { Height = height, ClipToBounds = true };
        void Draw(double w)
        {
            canvas.Children.Clear();
            if (values.Count < 2 || w <= 0) return;
            double top = maxValue ?? Math.Max(values.Max(), 1);
            var pts = values.Select((v, i) => new Point(w * i / (values.Count - 1), height * (1 - Math.Min(v / top, 1)))).ToList();
            var area = new Polygon { Fill = new LinearGradientBrush(Color.FromArgb(90, color.R, color.G, color.B), Color.FromArgb(5, color.R, color.G, color.B), 90) };
            area.Points.Add(new Point(0, height));
            foreach (var p in pts) area.Points.Add(p);
            area.Points.Add(new Point(w, height));
            canvas.Children.Add(area);
            var line = new Polyline { Stroke = Theme.B(color), StrokeThickness = 1.5, StrokeLineJoin = PenLineJoin.Round };
            foreach (var p in pts) line.Points.Add(p);
            canvas.Children.Add(line);
        }
        canvas.SizeChanged += (_, e) => Draw(e.NewSize.Width);
        return canvas;
    }

    /// Multi-series line chart with gridlines and simple axis labels.
    public static FrameworkElement Chart(IList<(IList<double> values, Color color, bool area)> series, double height, double maxY,
                                         Func<double, string> yLabel, IList<DateTime>? times = null, Func<DateTime, string>? xLabel = null)
    {
        var root = new Grid { Height = height };
        var canvas = new Canvas { ClipToBounds = false };
        root.Children.Add(canvas);
        void Draw(double W)
        {
            canvas.Children.Clear();
            double left = 0, right = 44, bottom = times is null ? 4 : 20, H = height - bottom;
            double w = W - left - right;
            if (w <= 10) return;
            foreach (var f in new[] { 0.0, 0.5, 1.0 })
            {
                double y = H * (1 - f);
                canvas.Children.Add(new Line { X1 = left, X2 = left + w, Y1 = y, Y2 = y, Stroke = Theme.Line, StrokeThickness = 1 });
                var lb = T(yLabel(maxY * f), 10, color: Theme.Tertiary);
                Canvas.SetLeft(lb, left + w + 6); Canvas.SetTop(lb, y - 7);
                canvas.Children.Add(lb);
            }
            foreach (var (values, color, area) in series)
            {
                if (values.Count < 2) continue;
                var pts = values.Select((v, i) => new Point(left + w * i / (values.Count - 1), H * (1 - Math.Clamp(v / maxY, 0, 1)))).ToList();
                if (area)
                {
                    var poly = new Polygon { Fill = new LinearGradientBrush(Color.FromArgb(80, color.R, color.G, color.B), Color.FromArgb(4, color.R, color.G, color.B), 90) };
                    poly.Points.Add(new Point(pts[0].X, H)); foreach (var p in pts) poly.Points.Add(p); poly.Points.Add(new Point(pts[^1].X, H));
                    canvas.Children.Add(poly);
                }
                var line = new Polyline { Stroke = Theme.B(color), StrokeThickness = 1.6, StrokeLineJoin = PenLineJoin.Round };
                foreach (var p in pts) line.Points.Add(p);
                canvas.Children.Add(line);
            }
            if (times is { Count: > 1 } && xLabel is not null)
            {
                int n = 5;
                for (int k = 0; k <= n; k++)
                {
                    int idx = (int)Math.Round((times.Count - 1) * k / (double)n);
                    var lb = T(xLabel(times[idx]), 10, color: Theme.Tertiary);
                    double x = left + w * idx / (times.Count - 1);
                    Canvas.SetLeft(lb, Math.Clamp(x - 16, 0, w - 30)); Canvas.SetTop(lb, H + 4);
                    canvas.Children.Add(lb);
                }
            }
        }
        root.SizeChanged += (_, e) => Draw(e.NewSize.Width);
        return root;
    }

    public static FrameworkElement Empty(string glyph, string title, string detail)
    {
        var g = Glyph(glyph, 36, Theme.Tertiary); g.HorizontalAlignment = HorizontalAlignment.Center;
        var t = T(title, 15, FontWeights.SemiBold); t.HorizontalAlignment = HorizontalAlignment.Center;
        var d = T(detail, 13, color: Theme.Secondary); d.HorizontalAlignment = HorizontalAlignment.Center; d.TextAlignment = TextAlignment.Center; d.TextWrapping = TextWrapping.Wrap;
        var s = V(10, g, t, d); s.Margin = new Thickness(0, 40, 0, 40); s.HorizontalAlignment = HorizontalAlignment.Center;
        return s;
    }

    public static FrameworkElement PageHeader(string title, string subtitle, UIElement? trailing = null)
    {
        var left = V(2, T(title, 26, FontWeights.Bold), T(subtitle, 13, color: Theme.Secondary));
        return Row(left, trailing);
    }

    public static bool Confirm(string title, string message, string ok = "确定", bool danger = true)
        => MessageBox.Show(message, title, MessageBoxButton.OKCancel, danger ? MessageBoxImage.Warning : MessageBoxImage.Information) == MessageBoxResult.OK;

    public static void Info(string title, string message) => MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public static void Reveal(string path)
    {
        try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\""); } catch { }
    }

    public static ContextMenu FileMenu(string path, long size, Action onRecycled)
    {
        var m = new ContextMenu();
        void Add(string h, Action a) { var i = new MenuItem { Header = h }; i.Click += (_, _) => a(); m.Items.Add(i); }
        Add("在资源管理器中显示", () => Reveal(path));
        Add("复制路径", () => Clipboard.SetText(path));
        Add("加入白名单（永不清理）", () => { Settings.Shared.Whitelist.Add(Fmt.Tilde(path)); Settings.Shared.Save(); });
        m.Items.Add(new Separator());
        Add("移到回收站…", () => { if (RecycleOne(path, size)) onRecycled(); });
        return m;
    }

    public static bool RecycleOne(string path, long size)
    {
        if (!CleanupExecutor.IsSafeTarget(path)) { Info("不能删除这个位置", $"{Fmt.Tilde(path)} 是受保护的系统或个人目录。"); return false; }
        if (Settings.Shared.IsWhitelisted(path)) { Info("这个位置在白名单里", $"{Fmt.Tilde(path)} 已被加入白名单，Peekaboo 不会删除它。"); return false; }
        if (!Confirm("移到回收站？", $"{Fmt.Tilde(path)}\n{Fmt.Bytes(size)}\n\n可以在回收站里恢复。")) return false;
        var err = Native.Recycle(path);
        if (err is not null) { Info("没能移到回收站", err); return false; }
        return true;
    }
}
