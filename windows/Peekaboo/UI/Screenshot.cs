using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;
using SD = System.Drawing;
using static Peekaboo.UIX;

namespace Peekaboo;

/// A user-recorded shortcut: virtual-key code + RegisterHotKey modifier mask.
public sealed class HotkeyData
{
    public int Vk { get; set; }
    public uint Mods { get; set; }
    public string Label { get; set; } = "";
    public const uint ALT = 1, CTRL = 2, SHIFT = 4, WIN = 8;
    public static HotkeyData Default => new() { Vk = 0x41, Mods = ALT | SHIFT, Label = "A" };

    public string Title
    {
        get
        {
            var parts = new List<string>();
            if ((Mods & CTRL) != 0) parts.Add("Ctrl");
            if ((Mods & ALT) != 0) parts.Add("Alt");
            if ((Mods & SHIFT) != 0) parts.Add("Shift");
            if ((Mods & WIN) != 0) parts.Add("Win");
            parts.Add(Label);
            return string.Join(" + ", parts);
        }
    }

    public bool Same(HotkeyData? o) => o is not null && o.Vk == Vk && o.Mods == Mods;

    /// From a WPF key event. Null when a plain key without Ctrl/Alt/Win (except F-keys).
    public static HotkeyData? From(Key key, ModifierKeys mods)
    {
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System) return null;
        uint m = 0;
        if (mods.HasFlag(ModifierKeys.Control)) m |= CTRL;
        if (mods.HasFlag(ModifierKeys.Alt)) m |= ALT;
        if (mods.HasFlag(ModifierKeys.Shift)) m |= SHIFT;
        if (mods.HasFlag(ModifierKeys.Windows)) m |= WIN;
        bool isF = key >= Key.F1 && key <= Key.F24;
        if (!isF && (m & (CTRL | ALT | WIN)) == 0) return null;
        string label = key switch
        {
            >= Key.A and <= Key.Z => key.ToString(),
            >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
            Key.Space => "Space", Key.Snapshot => "PrintScreen", Key.Return => "Enter",
            _ => key.ToString(),
        };
        return new HotkeyData { Vk = KeyInterop.VirtualKeyFromKey(key), Mods = m, Label = label };
    }
}
public enum ShotTool { Rect, Arrow, Pen, Text, Mosaic }

public sealed class ShotMark
{
    public ShotTool Kind;
    public Point A, B;
    public List<Point> Points = new();
    public string Text = "";
    public Color Color;
    public double Width;
}

/// Global hotkey through RegisterHotKey on a hidden message window.
public static class GlobalHotkey
{
    static HwndSource? _src;
    static Action? _action;
    const int Id = 0xB00;

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public static bool Apply(HotkeyData? key, Action action)
    {
        _action = action;
        if (_src is null)
        {
            _src = new HwndSource(new HwndSourceParameters("PeekabooHotkeys") { Width = 0, Height = 0, WindowStyle = 0 });
            _src.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
            {
                if (msg == 0x0312 && w.ToInt32() == Id) { _action?.Invoke(); handled = true; }
                return IntPtr.Zero;
            });
        }
        UnregisterHotKey(_src.Handle, Id);
        if (key is null) return true;
        return RegisterHotKey(_src.Handle, Id, key.Mods | 0x4000 /* MOD_NOREPEAT */, (uint)key.Vk);
    }
}

/// Starts captures and owns pinned images.
public static class Screenshot
{
    static readonly List<ShotOverlay> Overlays = new();
    public static readonly List<PinWindow> Pins = new();
    public static readonly Color[] Colors =
    {
        Color.FromRgb(242, 64, 64), Color.FromRgb(255, 199, 26), Color.FromRgb(51, 199, 102), Color.FromRgb(51, 140, 255), System.Windows.Media.Colors.White, System.Windows.Media.Colors.Black,
    };

    public static bool HotkeyOK { get; private set; } = true;
    public static void ApplyHotkey() => HotkeyOK = GlobalHotkey.Apply(Settings.Shared.ShotKey, () => Start());
    public static void SuspendHotkey() => GlobalHotkey.Apply(null, () => { });

    public static async void Start(int delayMs = 0)
    {
        if (Overlays.Count > 0) return;
        if (delayMs > 0) await Task.Delay(delayMs);
        var windows = WindowRects();
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            var b = screen.Bounds;
            using var bmp = new SD.Bitmap(b.Width, b.Height, SD.Imaging.PixelFormat.Format32bppArgb);
            using (var g = SD.Graphics.FromImage(bmp)) g.CopyFromScreen(b.Left, b.Top, 0, 0, b.Size);
            var image = ToSource(bmp);
            var local = windows.Select(r => { r.Intersect(new Rect(b.Left, b.Top, b.Width, b.Height)); return r; })
                               .Where(r => !r.IsEmpty && r.Width > 30 && r.Height > 20)
                               .Select(r => new Rect(r.X - b.Left, r.Y - b.Top, r.Width, r.Height)).ToList();
            var o = new ShotOverlay(image, local, b);
            Overlays.Add(o);
            o.Show();
        }
        Overlays.FirstOrDefault()?.Activate();
    }

    public static void CloseAll()
    {
        foreach (var o in Overlays.ToList()) o.Close();
        Overlays.Clear();
    }

    internal static void Closed(ShotOverlay o) => Overlays.Remove(o);

    public static BitmapSource ToSource(SD.Bitmap bmp)
    {
        var h = bmp.GetHbitmap();
        try
        {
            var s = Imaging.CreateBitmapSourceFromHBitmap(h, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            s.Freeze();
            return s;
        }
        finally { DeleteObject(h); }
    }

    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);

    // ---- visible top-level windows, front to back, in physical pixels

    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc fn, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr h);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int v, int size);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }

    static List<Rect> WindowRects()
    {
        var list = new List<Rect>();
        int me = Environment.ProcessId;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || IsIconic(h) || GetWindowTextLength(h) == 0) return true;
            if (DwmGetWindowAttribute(h, 14, out int cloaked, 4) == 0 && cloaked != 0) return true; // DWMWA_CLOAKED
            if (DwmGetWindowAttribute(h, 9, out RECT r, Marshal.SizeOf<RECT>()) != 0) return true;  // DWMWA_EXTENDED_FRAME_BOUNDS
            if (r.R - r.L < 40 || r.B - r.T < 30) return true;
            list.Add(new Rect(r.L, r.T, r.R - r.L, r.B - r.T));
            return true;
        }, IntPtr.Zero);
        return list;
    }
}

/// Full-screen overlay for one monitor: select, annotate, then copy / save / pin.
public sealed class ShotOverlay : Window
{
    readonly BitmapSource _image;
    readonly BitmapSource _pixelated;
    readonly List<Rect> _windowsPx;
    readonly System.Drawing.Rectangle _bounds;
    double _scale = 1;   // physical pixels per DIP

    readonly Grid _stage = new();          // image + marks (what gets exported)
    readonly Canvas _marksLayer = new();
    readonly Canvas _ui = new();           // dim, border, toolbar
    readonly List<ShotMark> _marks = new();
    ShotMark? _draft;
    Rect? _selection, _hover;
    Point? _dragStart, _moveOffset;
    bool _editing;
    ShotTool? _tool;
    Color _color = Screenshot.Colors[0];
    double _width = 3;
    TextBox? _textBox;

    public ShotOverlay(BitmapSource image, List<Rect> windowsPx, System.Drawing.Rectangle bounds)
    {
        _image = image; _windowsPx = windowsPx; _bounds = bounds;
        _pixelated = Pixelate(image);
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; Topmost = true;
        AllowsTransparency = false; Background = Brushes.Black; Cursor = Cursors.Cross;
        Left = bounds.Left; Top = bounds.Top; Width = bounds.Width; Height = bounds.Height;
        _stage.Children.Add(new Image { Source = image, Stretch = Stretch.Fill });
        _stage.Children.Add(_marksLayer);
        var root = new Grid();
        root.Children.Add(_stage);
        root.Children.Add(_ui);
        Content = root;
        SourceInitialized += (_, _) =>
        {
            // Place exactly over the monitor in physical pixels, whatever its DPI.
            var h = new WindowInteropHelper(this).Handle;
            SetWindowPos(h, new IntPtr(-1), bounds.Left, bounds.Top, bounds.Width, bounds.Height, 0x0040);
        };
        Loaded += (_, _) => { _scale = _image.PixelWidth / Math.Max(1, ActualWidth); Redraw(); };
        MouseMove += OnMove;
        MouseLeftButtonDown += OnDown;
        MouseLeftButtonUp += OnUp;
        MouseRightButtonUp += (_, _) => { if (_editing) { _editing = false; _selection = null; _marks.Clear(); _tool = null; Redraw(); } else Screenshot.CloseAll(); };
        KeyDown += OnKey;
        Closed += (_, _) => Screenshot.Closed(this);
    }

    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hgt, uint flags);

    static BitmapSource Pixelate(BitmapSource src)
    {
        double f = 1.0 / 12;
        var small = new TransformedBitmap(src, new ScaleTransform(f, f));
        var rtb = new RenderTargetBitmap(src.PixelWidth, src.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        var group = new DrawingGroup();
        RenderOptions.SetBitmapScalingMode(group, BitmapScalingMode.NearestNeighbor);
        using (var gc = group.Open()) gc.DrawImage(small, new Rect(0, 0, src.PixelWidth, src.PixelHeight));
        var dv = new DrawingVisual();
        using (var c = dv.RenderOpen()) c.DrawDrawing(group);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    Rect? WindowAt(Point p)
    {
        var px = new Point(p.X * _scale, p.Y * _scale);
        foreach (var r in _windowsPx) if (r.Contains(px)) return new Rect(r.X / _scale, r.Y / _scale, r.Width / _scale, r.Height / _scale);
        return null;
    }

    Size StageSize => new(ActualWidth, ActualHeight);

    void OnMove(object s, MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (e.LeftButton == MouseButtonState.Pressed && _dragStart is { } start)
        {
            if (!_editing)
            {
                if (Math.Abs(p.X - start.X) + Math.Abs(p.Y - start.Y) > 4) _selection = new Rect(start, p);
            }
            else if (_selection is { } sel)
            {
                var q = new Point(Math.Clamp(p.X, sel.Left, sel.Right), Math.Clamp(p.Y, sel.Top, sel.Bottom));
                if (_tool is null)
                {
                    if (_moveOffset is { } o)
                    {
                        var r = sel;
                        r.X = Math.Clamp(p.X - o.X, 0, ActualWidth - r.Width); r.Y = Math.Clamp(p.Y - o.Y, 0, ActualHeight - r.Height);
                        _selection = r;
                    }
                }
                else if (_draft is not null)
                {
                    if (_draft.Kind == ShotTool.Pen) _draft.Points.Add(q); else _draft.B = q;
                    DrawMarks();
                    return;
                }
            }
            Redraw();
        }
        else if (!_editing)
        {
            _hover = WindowAt(p);
            Redraw(p);
        }
    }

    void OnDown(object s, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(this);
        if (e.ClickCount == 2 && _editing && _tool is null) { CopyAndClose(); return; }
        if (e.OriginalSource is DependencyObject d && IsInToolbar(d)) return;
        _dragStart = p;
        CaptureMouse();
        if (_editing && _selection is { } sel && sel.Contains(p))
        {
            if (_tool is null) _moveOffset = new Point(p.X - sel.X, p.Y - sel.Y);
            else if (_tool == ShotTool.Text) { PlaceText(p); _dragStart = null; ReleaseMouseCapture(); }
            else _draft = new ShotMark { Kind = _tool.Value, A = p, B = p, Points = { p }, Color = _color, Width = _width };
        }
    }

    void OnUp(object s, MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();
        var p = e.GetPosition(this);
        if (_dragStart is null) return;
        if (!_editing)
        {
            _selection ??= WindowAt(p) ?? new Rect(0, 0, ActualWidth, ActualHeight);
            if (_selection.Value.Width > 4 && _selection.Value.Height > 4) _editing = true; else _selection = null;
        }
        else if (_draft is not null) { _marks.Add(_draft); _draft = null; }
        _dragStart = null; _moveOffset = null;
        Redraw();
    }

    void OnKey(object s, KeyEventArgs e)
    {
        if (_textBox is not null)
        {
            if (e.Key == Key.Enter) { CommitText(); e.Handled = true; }
            else if (e.Key == Key.Escape) { _ui.Children.Remove(_textBox); _textBox = null; e.Handled = true; }
            return;
        }
        if (e.Key == Key.Escape) Screenshot.CloseAll();
        else if (e.Key == Key.Enter) CopyAndClose();
        else if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control) Undo();
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control) CopyAndClose();
    }

    void Undo() { if (_marks.Count > 0) { _marks.RemoveAt(_marks.Count - 1); DrawMarks(); } }

    void PlaceText(Point p)
    {
        if (_textBox is not null) CommitText();
        _textBox = new TextBox
        {
            Width = 260, FontSize = 14 + _width * 3, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(_color),
            Background = Brushes.Transparent, BorderBrush = new SolidColorBrush(_color), BorderThickness = new Thickness(1), Tag = p,
        };
        Canvas.SetLeft(_textBox, p.X); Canvas.SetTop(_textBox, p.Y);
        _ui.Children.Add(_textBox);
        _textBox.Focus();
    }

    void CommitText()
    {
        if (_textBox is null) return;
        if (!string.IsNullOrWhiteSpace(_textBox.Text))
            _marks.Add(new ShotMark { Kind = ShotTool.Text, A = (Point)_textBox.Tag, Text = _textBox.Text, Color = _color, Width = _width });
        _ui.Children.Remove(_textBox);
        _textBox = null;
        DrawMarks();
    }

    // ---- drawing

    void DrawMarks()
    {
        _marksLayer.Children.Clear();
        foreach (var m in _draft is null ? _marks : _marks.Append(_draft)) _marksLayer.Children.Add(Visual(m));
    }

    UIElement Visual(ShotMark m)
    {
        var brush = new SolidColorBrush(m.Color);
        switch (m.Kind)
        {
            case ShotTool.Rect:
                var r = new Rect(m.A, m.B);
                var rect = new Rectangle { Width = r.Width, Height = r.Height, Stroke = brush, StrokeThickness = m.Width, RadiusX = 2, RadiusY = 2 };
                Canvas.SetLeft(rect, r.X); Canvas.SetTop(rect, r.Y);
                return rect;
            case ShotTool.Arrow:
            {
                double ang = Math.Atan2(m.B.Y - m.A.Y, m.B.X - m.A.X), head = Math.Max(10, m.Width * 4);
                var g = new GeometryGroup();
                var end = new Point(m.B.X - Math.Cos(ang) * head * 0.6, m.B.Y - Math.Sin(ang) * head * 0.6);
                var shaft = new Path { Data = new LineGeometry(m.A, end), Stroke = brush, StrokeThickness = m.Width, StrokeStartLineCap = PenLineCap.Round };
                var tip = new PathFigure { StartPoint = m.B, IsClosed = true, IsFilled = true };
                tip.Segments.Add(new LineSegment(new Point(m.B.X - Math.Cos(ang - 0.45) * head, m.B.Y - Math.Sin(ang - 0.45) * head), true));
                tip.Segments.Add(new LineSegment(new Point(m.B.X - Math.Cos(ang + 0.45) * head, m.B.Y - Math.Sin(ang + 0.45) * head), true));
                var c = new Canvas();
                c.Children.Add(shaft);
                c.Children.Add(new Path { Data = new PathGeometry(new[] { tip }), Fill = brush });
                return c;
            }
            case ShotTool.Pen:
                var pl = new Polyline { Stroke = brush, StrokeThickness = m.Width, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
                foreach (var p in m.Points) pl.Points.Add(p);
                return pl;
            case ShotTool.Text:
                var t = new TextBlock { Text = m.Text, Foreground = brush, FontSize = 14 + m.Width * 3, FontWeight = FontWeights.SemiBold, FontFamily = Theme.Font };
                Canvas.SetLeft(t, m.A.X + 3); Canvas.SetTop(t, m.A.Y + 2);
                return t;
            default: // mosaic: the pixelated copy, clipped to the rectangle
                var mr = new Rect(m.A, m.B);
                var img = new Image { Source = _pixelated, Stretch = Stretch.Fill, Width = ActualWidth, Height = ActualHeight, Clip = new RectangleGeometry(mr) };
                return img;
        }
    }

    void Redraw(Point? mouse = null)
    {
        _ui.Children.Clear();
        if (_textBox is not null) _ui.Children.Add(_textBox);
        var hole = _selection ?? (_dragStart is null ? _hover : null);
        Geometry dim = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight));
        if (hole is { } h) dim = new CombinedGeometry(GeometryCombineMode.Exclude, dim, new RectangleGeometry(h));
        _ui.Children.Add(new Path { Data = dim, Fill = new SolidColorBrush(Color.FromArgb(115, 0, 0, 0)), IsHitTestVisible = false });
        if (hole is { } hr)
        {
            var border = new Rectangle { Width = hr.Width, Height = hr.Height, Stroke = Theme.B(Theme.CpuC), StrokeThickness = 1.5, IsHitTestVisible = false };
            Canvas.SetLeft(border, hr.X); Canvas.SetTop(border, hr.Y);
            _ui.Children.Add(border);
            var label = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)), CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 1, 6, 1), IsHitTestVisible = false,
                Child = T($"{(int)(hr.Width * _scale)} × {(int)(hr.Height * _scale)}", 11, FontWeights.Medium, Brushes.White),
            };
            Canvas.SetLeft(label, hr.X); Canvas.SetTop(label, Math.Max(hr.Y - 24, 4));
            _ui.Children.Add(label);
        }
        if (_editing && _selection is { } sel) AddToolbar(sel);
        DrawMarks();
    }

    Border? _toolbar;
    bool IsInToolbar(DependencyObject d)
    {
        while (d is not null) { if (d == _toolbar) return true; d = VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d); }
        return false;
    }

    void AddToolbar(Rect sel)
    {
        Button Tool(string glyph, string tip, bool on, Action a)
        {
            var b = new Button
            {
                Content = Glyph(glyph, 15, Brushes.Black), Width = 32, Height = 30, ToolTip = tip, Cursor = Cursors.Hand,
                Background = on ? Theme.B(Theme.CpuC, 0.25) : Brushes.Transparent, BorderThickness = new Thickness(0),
            };
            b.Click += (_, _) => a();
            return b;
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (tool, glyph, tip) in new[] { (ShotTool.Rect, "", L("矩形", "Rectangle")), (ShotTool.Arrow, "", L("箭头", "Arrow")), (ShotTool.Pen, "", L("画笔", "Pen")), (ShotTool.Text, "", L("文字", "Text")), (ShotTool.Mosaic, "", L("马赛克", "Mosaic")) })
        {
            var t = tool;
            row.Children.Add(Tool(glyph, tip, _tool == t, () => { _tool = _tool == t ? null : t; Cursor = _tool is null ? Cursors.SizeAll : Cursors.Cross; Redraw(); }));
        }
        row.Children.Add(new Border { Width = 1, Background = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)), Margin = new Thickness(6, 4, 6, 4) });
        foreach (var c in Screenshot.Colors)
        {
            var col = c;
            var dot = new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(c), Margin = new Thickness(3, 7, 3, 7), Cursor = Cursors.Hand,
                                   BorderBrush = _color == c ? Brushes.Black : new SolidColorBrush(Color.FromArgb(60, 0, 0, 0)), BorderThickness = new Thickness(_color == c ? 2 : 1) };
            dot.MouseLeftButtonUp += (_, ev) => { _color = col; ev.Handled = true; Redraw(); };
            row.Children.Add(dot);
        }
        foreach (var w in new[] { 2.0, 4.0, 7.0 })
        {
            var wid = w;
            row.Children.Add(Tool(w < 3 ? "" : w < 5 ? "" : "", L($"{w:0} 号粗细", $"Size {w:0}"), Math.Abs(_width - w) < 0.1, () => { _width = wid; Redraw(); }));
        }
        row.Children.Add(new Border { Width = 1, Background = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)), Margin = new Thickness(6, 4, 6, 4) });
        row.Children.Add(Tool("", L("撤销 Ctrl+Z", "Undo Ctrl+Z"), false, Undo));
        row.Children.Add(Tool("", L("贴到屏幕", "Pin to screen"), false, Pin));
        row.Children.Add(Tool("", L("保存", "Save"), false, Save));
        row.Children.Add(Tool("", L("取消 Esc", "Cancel Esc"), false, Screenshot.CloseAll));
        var copy = new Button { Content = T(L("✓ 复制", "✓ Copy"), 12.5, FontWeights.SemiBold, Brushes.White), Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(4, 0, 0, 0), Background = Theme.AccentBrush, BorderThickness = new Thickness(0), Cursor = Cursors.Hand, ToolTip = L("复制到剪贴板（回车 / 双击）", "Copy to clipboard (Enter / double-click)") };
        copy.Click += (_, _) => CopyAndClose();
        row.Children.Add(copy);
        _toolbar = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(245, 250, 250, 250)), CornerRadius = new CornerRadius(8), Padding = new Thickness(5), Child = row, Cursor = Cursors.Arrow,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, ShadowDepth = 3, Opacity = 0.3 },
        };
        _toolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double w0 = _toolbar.DesiredSize.Width, h0 = _toolbar.DesiredSize.Height;
        double x = Math.Clamp(sel.Right - w0, 8, Math.Max(8, ActualWidth - w0 - 8));
        double y = sel.Bottom + 8;
        if (y + h0 > ActualHeight - 8) y = sel.Top - h0 - 8;
        if (y < 8) y = sel.Bottom - h0 - 8;
        Canvas.SetLeft(_toolbar, x); Canvas.SetTop(_toolbar, y);
        _ui.Children.Add(_toolbar);
    }

    // ---- export

    BitmapSource? Export()
    {
        if (_selection is not { } sel || sel.Width < 1) return null;
        CommitText();
        _stage.UpdateLayout();
        int W = _image.PixelWidth, H = _image.PixelHeight;
        double dpi = 96 * _scale;
        var rtb = new RenderTargetBitmap(W, H, dpi, dpi, PixelFormats.Pbgra32);
        rtb.Render(_stage);
        var px = new Int32Rect((int)Math.Round(sel.X * _scale), (int)Math.Round(sel.Y * _scale),
                               (int)Math.Round(sel.Width * _scale), (int)Math.Round(sel.Height * _scale));
        px.Width = Math.Min(px.Width, W - px.X); px.Height = Math.Min(px.Height, H - px.Y);
        var crop = new CroppedBitmap(rtb, px);
        var outBmp = new WriteableBitmap(crop);
        outBmp.Freeze();
        return outBmp;
    }

    public BitmapSource? ExportForTest() => Export();

    void CopyAndClose()
    {
        var img = Export();
        if (img is not null) Clipboard.SetImage(img);
        Screenshot.CloseAll();
    }

    void Save()
    {
        var img = Export();
        if (img is null) return;
        var folder = Settings.Expand(Settings.Shared.ShotFolder);
        Directory.CreateDirectory(folder);
        var file = System.IO.Path.Combine(folder, L($"截图 {DateTime.Now:yyyy-MM-dd HH.mm.ss}.png", $"Screenshot {DateTime.Now:yyyy-MM-dd HH.mm.ss}.png"));
        using (var fs = File.Create(file)) { var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(img)); enc.Save(fs); }
        Clipboard.SetImage(img);
        Screenshot.CloseAll();
        Reveal(file);
    }

    void Pin()
    {
        var img = Export();
        if (img is null || _selection is not { } sel) return;
        var at = new Point(_bounds.Left + sel.X * _scale, _bounds.Top + sel.Y * _scale);
        Screenshot.CloseAll();
        var p = new PinWindow(img, at, _scale);
        Screenshot.Pins.Add(p);
        p.Closed += (_, _) => Screenshot.Pins.Remove(p);
        p.Show();
    }

    // ---- test hook: build a demo state without real mouse input

    public void Demo(Rect selection, IEnumerable<ShotMark> marks)
    {
        _selection = selection; _editing = true; _tool = ShotTool.Arrow;
        _marks.AddRange(marks);
        Redraw();
    }
}

/// A screenshot pinned on top of everything: drag, wheel to zoom, double-click to close.
public sealed class PinWindow : Window
{
    readonly BitmapSource _img;
    double _zoom = 1;
    readonly double _baseW, _baseH;

    public PinWindow(BitmapSource img, Point physicalTopLeft, double scale)
    {
        _img = img;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; Topmost = true;
        AllowsTransparency = false; Background = Brushes.White;
        _baseW = img.PixelWidth / scale; _baseH = img.PixelHeight / scale;
        Width = _baseW; Height = _baseH;
        Left = physicalTopLeft.X / scale; Top = physicalTopLeft.Y / scale;
        BorderBrush = Theme.AccentBrush; BorderThickness = new Thickness(1);
        Content = new Image { Source = img, Stretch = Stretch.Fill };
        MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) Close(); else DragMove(); };
        MouseWheel += (_, e) => Zoom(e.Delta > 0 ? 1.06 : 1 / 1.06);
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        var m = new ContextMenu();
        void Add(string h, Action a) { var i = new MenuItem { Header = h }; i.Click += (_, _) => a(); m.Items.Add(i); }
        Add(L("复制", "Copy"), () => Clipboard.SetImage(_img));
        Add(L("另存为…", "Save as…"), () =>
        {
            var d = new Microsoft.Win32.SaveFileDialog { FileName = L("截图.png", "Screenshot.png"), Filter = "PNG|*.png" };
            if (d.ShowDialog() == true) { using var fs = File.Create(d.FileName); var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(_img)); enc.Save(fs); }
        });
        m.Items.Add(new Separator());
        Add(L("不透明", "Opaque"), () => Opacity = 1);
        Add(L("透明 25%", "25% transparent"), () => Opacity = 0.75);
        Add(L("透明 50%", "50% transparent"), () => Opacity = 0.5);
        Add(L("原始大小", "Actual size"), () => { _zoom = 1; Zoom(1); });
        m.Items.Add(new Separator());
        Add(L("关闭", "Close"), Close);
        ContextMenu = m;
        SourceInitialized += (_, _) => Native.RoundCorners(this);
    }

    void Zoom(double f)
    {
        _zoom = Math.Clamp(_zoom * f, 0.2, 5);
        double cx = Left + Width / 2, cy = Top + Height / 2;
        Width = _baseW * _zoom; Height = _baseH * _zoom;
        Left = cx - Width / 2; Top = cy - Height / 2;
    }
}
