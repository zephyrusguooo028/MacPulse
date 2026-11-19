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
    /// Stroke width; for text the font size; for brush mosaic the brush diameter.
    public double Width;
    /// Mosaic only: frosted blur instead of pixel blocks / painted with a brush instead of a box.
    public bool Blur, Brush;

    /// A click without a real drag: dropped, so a click (or the first click of a
    /// double-click) never leaves a stray dot.
    public bool IsTiny
    {
        get
        {
            switch (Kind)
            {
                case ShotTool.Text: return false;
                case ShotTool.Arrow: return (B - A).Length < 5;
                case ShotTool.Pen: return Extent(Points) < 3;
                case ShotTool.Mosaic when Brush: return Extent(Points) < 3;
                default: var r = new Rect(A, B); return r.Width < 3 || r.Height < 3;   // rect, box mosaic
            }
        }
    }

    static double Extent(List<Point> pts)
    {
        if (pts.Count == 0) return 0;
        double x0 = pts.Min(p => p.X), x1 = pts.Max(p => p.X), y0 = pts.Min(p => p.Y), y1 = pts.Max(p => p.Y);
        return Math.Max(x1 - x0, y1 - y0);
    }
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

    /// A capture/editor overlay or a pinned shot is open (a restart would lose it).
    public static bool InProgress => Overlays.Count > 0 || Pins.Count > 0;

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
    int _strokeLevel = 1;           // 0 thin, 1 medium, 2 thick
    double _fontSize = 20;
    bool _mosaicBrush = true, _mosaicBlur;
    BitmapSource? _blurred;
    double StrokeWidth => new[] { 2.0, 4.0, 7.0 }[_strokeLevel];
    double BrushWidth => new[] { 14.0, 26.0, 44.0 }[_strokeLevel];
    TextBox? _textBox;
    /// A typed text that is selected: it can be dragged, resized from its corner, restyled or deleted.
    ShotMark? _selText;
    /// Current drag on a text: moving it (grab offset from its box), or resizing it from the corner handle.
    (ShotMark Mark, Vector Offset, bool Resize, double StartSize, Rect StartRect)? _textDrag;

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
        // Double-click is caught on the way down, before a text box under the pointer takes it.
        PreviewMouseLeftButtonDown += OnPreviewDown;
        MouseLeftButtonDown += OnDown;
        MouseLeftButtonUp += OnUp;
        MouseRightButtonUp += (_, _) =>
        {
            if (!_editing) { Screenshot.CloseAll(); return; }
            CloseTextBox();
            _editing = false; _selection = null; _marks.Clear(); _tool = null; _selText = null; _draft = null;
            Redraw();
        };
        KeyDown += OnKey;
        Closed += (_, _) => Screenshot.Closed(this);
    }

    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hgt, uint flags);

    /// Frosted copy of the screen, made the first time blur is used.
    BitmapSource Blurred()
    {
        if (_blurred is not null) return _blurred;
        int w = _image.PixelWidth, h = _image.PixelHeight;
        var img = new Image { Source = _image, Width = w, Height = h, Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 28, KernelType = System.Windows.Media.Effects.KernelType.Gaussian } };
        img.Measure(new Size(w, h)); img.Arrange(new Rect(0, 0, w, h));
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(img);
        rtb.Freeze();
        return _blurred = rtb;
    }

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
                if (_textDrag is { } d)
                {
                    if (d.Resize)
                    {
                        double fx = (p.X - d.StartRect.Left) / Math.Max(d.StartRect.Width, 1);
                        double fy = (p.Y - d.StartRect.Top) / Math.Max(d.StartRect.Height, 1);
                        d.Mark.Width = _fontSize = Math.Clamp(d.StartSize * Math.Max((fx + fy) / 2, 0.1), 8, 220);
                    }
                    else
                    {
                        // Keep the text's box inside the selection.
                        var r = TextRect(d.Mark);
                        double x = Math.Clamp(p.X - d.Offset.X, sel.Left, Math.Max(sel.Left, sel.Right - Math.Min(r.Width, sel.Width)));
                        double y = Math.Clamp(p.Y - d.Offset.Y, sel.Top, Math.Max(sel.Top, sel.Bottom - Math.Min(r.Height, sel.Height)));
                        d.Mark.A = new Point(x - TextInset.X, y - TextInset.Y);
                    }
                }
                else if (_tool is null)
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
                    if (_draft.Kind == ShotTool.Pen || _draft.Brush) _draft.Points.Add(q); else _draft.B = q;
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
        else UpdateCursor(p);
    }

    /// Resize cursor over the selected text's handle, move cursor over a grabbable text.
    void UpdateCursor(Point p)
    {
        Cursor c = _tool is not null ? Cursors.Cross : _selection?.Contains(p) == true ? Cursors.SizeAll : Cursors.Arrow;
        if (_selText is { } m && _textBox is null && HandleRect(TextRect(m)).Contains(p)) c = Cursors.SizeNWSE;
        else if ((_tool is null || _tool == ShotTool.Text) && TextHit(p) is not null) c = Cursors.SizeAll;
        if (Cursor != c) Cursor = c;
    }

    /// Double-click inside the selection: on a typed text, edit it again; anywhere else, copy and close,
    /// whatever tool is active (an empty text box opened by the first click is dropped first).
    void OnPreviewDown(object s, MouseButtonEventArgs e)
    {
        if (e.ClickCount < 2 || !_editing || _selection is not { } sel) return;
        var p = e.GetPosition(this);
        if (!sel.Contains(p) || e.OriginalSource is not DependencyObject d || IsInside(d, _toolbar)) return;
        bool inBox = _textBox is not null && IsInside(d, _textBox);
        if (inBox && !string.IsNullOrWhiteSpace(_textBox!.Text)) return;   // let the box select a word
        e.Handled = true;
        _draft = null;
        _textDrag = null; _dragStart = null; _moveOffset = null;
        ReleaseMouseCapture();
        if (!inBox && TextHit(p) is { } m) { EditText(m); return; }
        if (_textBox is not null && string.IsNullOrWhiteSpace(_textBox.Text)) CloseTextBox();
        CopyAndClose();
    }

    void OnDown(object s, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(this);
        if (e.OriginalSource is DependencyObject d && IsInside(d, _toolbar)) return;
        _dragStart = p;
        CaptureMouse();
        if (!_editing || _selection is not { } sel) return;
        // Grab the selected text's corner handle, or any typed text, before anything else.
        if (_selText is { } cur && _textBox is null && HandleRect(TextRect(cur)).Contains(p))
        {
            _textDrag = (cur, default, true, cur.Width, TextRect(cur));
            return;
        }
        if ((_tool is null || _tool == ShotTool.Text) && TextHit(p) is { } hit)
        {
            if (_textBox is not null) CommitText();
            SelectText(hit);
            var r = TextRect(hit);
            _textDrag = (hit, p - r.TopLeft, false, hit.Width, r);
            Redraw();
            return;
        }
        // Clicking anywhere else deselects.
        if (_selText is not null) { _selText = null; Redraw(); }
        if (!sel.Contains(p)) return;
        if (_tool is null) _moveOffset = new Point(p.X - sel.X, p.Y - sel.Y);
        else if (_tool == ShotTool.Text)
        {
            PlaceText(p);
            _selText = null;
            _dragStart = null; ReleaseMouseCapture();
            Redraw();
        }
        else
        {
            bool mosaic = _tool == ShotTool.Mosaic;
            _draft = new ShotMark
            {
                Kind = _tool.Value, A = p, B = p, Points = { p }, Color = _color,
                Width = mosaic ? (_mosaicBrush ? BrushWidth : 0) : StrokeWidth, Blur = mosaic && _mosaicBlur, Brush = mosaic && _mosaicBrush,
            };
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
        else if (_draft is not null)
        {
            // A click without a real drag leaves nothing behind.
            if (!_draft.IsTiny) { _marks.Add(_draft); _selText = null; }
            _draft = null;
        }
        _dragStart = null; _moveOffset = null; _textDrag = null;
        Redraw();
    }

    void OnKey(object s, KeyEventArgs e)
    {
        if (_textBox is not null)
        {
            if (e.Key == Key.Enter) { CommitText(); Redraw(); e.Handled = true; }
            else if (e.Key == Key.Escape) { CloseTextBox(); Redraw(); e.Handled = true; }
            else if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control) { Undo(); e.Handled = true; }
            return;
        }
        if (e.Key == Key.Escape) Screenshot.CloseAll();
        else if (e.Key == Key.Enter) CopyAndClose();
        else if (e.Key is Key.Delete or Key.Back && _selText is not null) { DeleteSelected(); e.Handled = true; }
        else if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control) Undo();
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control) CopyAndClose();
    }

    /// Undo closes an open text box; otherwise it removes the last mark.
    void Undo()
    {
        if (_textBox is not null) { CloseTextBox(); Redraw(); return; }
        if (_marks.Count > 0) { _marks.RemoveAt(_marks.Count - 1); _selText = null; Redraw(); }
    }

    static bool IsInside(DependencyObject? d, DependencyObject? container)
    {
        if (container is null) return false;
        while (d is not null)
        {
            if (d == container) return true;
            d = d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    // ---- text

    /// Where a text mark's glyphs start relative to its anchor (the text box's corner).
    static readonly Vector TextInset = new(3, 2);
    static readonly Typeface TextFace = new(Theme.Font, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    /// The box a text mark covers on screen.
    Rect TextRect(ShotMark m)
    {
        var ft = new FormattedText(m.Text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, TextFace, m.Width, Brushes.Black,
                                   VisualTreeHelper.GetDpi(this).PixelsPerDip);
        return new Rect(m.A + TextInset, new Size(Math.Ceiling(ft.WidthIncludingTrailingWhitespace) + 2, Math.Ceiling(ft.Height)));
    }

    ShotMark? TextHit(Point p)
    {
        for (int i = _marks.Count - 1; i >= 0; i--)
        {
            var m = _marks[i];
            if (m.Kind != ShotTool.Text) continue;
            var r = TextRect(m); r.Inflate(4, 4);
            if (r.Contains(p)) return m;
        }
        return null;
    }

    static Rect HandleRect(Rect r) => new(r.Right - 11, r.Bottom - 11, 22, 22);   // 12 px dot plus some slack

    /// Selecting a text makes its size and colour the current ones, so the options row shows them.
    void SelectText(ShotMark m) { _selText = m; _fontSize = m.Width; _color = m.Color; }

    /// Font size / colour picked while a text is selected also restyles it.
    void RestyleSelected(double? size = null, Color? color = null)
    {
        if (_selText is not { } m || _textBox is not null) return;
        if (size is { } fs) m.Width = fs;
        if (color is { } c) m.Color = c;
    }

    void DeleteSelected()
    {
        if (_selText is null) return;
        _marks.Remove(_selText);
        _selText = null;
        Redraw();
    }

    /// Opens a typed text again for editing, with its own text, colour and size.
    void EditText(ShotMark m)
    {
        CommitText();
        _marks.Remove(m);
        _selText = null;
        _color = m.Color; _fontSize = m.Width;
        _tool = ShotTool.Text; Cursor = Cursors.Cross;
        PlaceText(m.A, m.Text);
        Redraw();
    }

    void PlaceText(Point p, string text = "")
    {
        if (_textBox is not null) CommitText();
        _textBox = new TextBox
        {
            MinWidth = 260, Text = text, FontSize = _fontSize, FontWeight = FontWeights.SemiBold, FontFamily = Theme.Font, Foreground = new SolidColorBrush(_color),
            Background = Brushes.Transparent, BorderBrush = new SolidColorBrush(_color), BorderThickness = new Thickness(1), Tag = p,
        };
        Canvas.SetLeft(_textBox, p.X); Canvas.SetTop(_textBox, p.Y);
        _ui.Children.Add(_textBox);
        // Focus once it is in the visual tree, otherwise typing goes nowhere.
        var box = _textBox;
        Dispatcher.BeginInvoke(() => { Activate(); box.Focus(); Keyboard.Focus(box); box.CaretIndex = box.Text.Length; }, System.Windows.Threading.DispatcherPriority.Input);
    }

    /// Puts the text being typed into the picture (or drops it when empty); the new text stays selected.
    void CommitText()
    {
        if (_textBox is null) return;
        if (!string.IsNullOrWhiteSpace(_textBox.Text))
        {
            var m = new ShotMark { Kind = ShotTool.Text, A = (Point)_textBox.Tag, Text = _textBox.Text, Color = _color, Width = _fontSize };
            _marks.Add(m);
            _selText = m;
        }
        CloseTextBox();
        DrawMarks();
    }

    void CloseTextBox()
    {
        if (_textBox is null) return;
        _ui.Children.Remove(_textBox);
        _textBox = null;
        Focus();
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
                var t = new TextBlock { Text = m.Text, Foreground = brush, FontSize = m.Width, FontWeight = FontWeights.SemiBold, FontFamily = Theme.Font };
                Canvas.SetLeft(t, m.A.X + 3); Canvas.SetTop(t, m.A.Y + 2);
                return t;
            default: // mosaic: the pixelated or blurred copy, clipped to the box or the brush stroke
            {
                Geometry clip;
                if (m.Brush)
                {
                    var geo = new StreamGeometry();
                    using (var ctx = geo.Open())
                    {
                        ctx.BeginFigure(m.Points[0], false, false);
                        foreach (var pt in m.Points.Skip(1)) ctx.LineTo(pt, true, true);
                        if (m.Points.Count == 1) ctx.LineTo(new Point(m.Points[0].X + 0.1, m.Points[0].Y), true, true);
                    }
                    clip = geo.GetWidenedPathGeometry(new Pen(Brushes.Black, m.Width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round });
                }
                else clip = new RectangleGeometry(new Rect(m.A, m.B));
                return new Image { Source = m.Blur ? Blurred() : _pixelated, Stretch = Stretch.Fill, Width = ActualWidth, Height = ActualHeight, Clip = clip };
            }
        }
    }

    void Redraw(Point? mouse = null)
    {
        _ui.Children.Clear();
        if (_textBox is not null)
        {
            // Size / colour picked while typing apply to the open box too.
            _textBox.FontSize = _fontSize;
            _textBox.Foreground = new SolidColorBrush(_color); _textBox.BorderBrush = new SolidColorBrush(_color);
            _ui.Children.Add(_textBox);
        }
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
        if (_editing && _textBox is null && _selText is { } st && _marks.Contains(st)) AddTextSelection(TextRect(st));
        if (_editing && _selection is { } sel) AddToolbar(sel);
        DrawMarks();
    }

    /// Dashed box around the selected text, with the round resize handle at its bottom-right corner.
    void AddTextSelection(Rect r)
    {
        var box = r; box.Inflate(3, 2);
        var shadow = new Rectangle { Width = box.Width, Height = box.Height, Stroke = new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)), StrokeThickness = 2, IsHitTestVisible = false };
        var dash = new Rectangle { Width = box.Width, Height = box.Height, Stroke = new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)), StrokeThickness = 1, StrokeDashArray = { 4, 3 }, IsHitTestVisible = false };
        foreach (var e in new[] { shadow, dash }) { Canvas.SetLeft(e, box.X); Canvas.SetTop(e, box.Y); _ui.Children.Add(e); }
        var dot = new Ellipse { Width = 12, Height = 12, Fill = Brushes.White, Stroke = Theme.AccentBrush, StrokeThickness = 2, IsHitTestVisible = false };
        Canvas.SetLeft(dot, r.Right - 6); Canvas.SetTop(dot, r.Bottom - 6);
        _ui.Children.Add(dot);
    }

    Border? _toolbar;

    void AddToolbar(Rect sel)
    {
        var accent = Theme.AccentColor;
        var ink = new SolidColorBrush(Color.FromRgb(30, 30, 30));
        // Compact icon-only tile; the tooltip carries the name and shortcut.
        Border Tile(UIElement icon, string tip, bool on, Action a, double width = 30, Brush? fill = null, Brush? hover = null)
        {
            var normal = fill ?? (on ? Theme.B(accent, 0.25) : Brushes.Transparent);
            var over = hover ?? (on ? Theme.B(accent, 0.32) : new SolidColorBrush(Color.FromArgb(22, 0, 0, 0)));
            var b = new Border
            {
                Child = icon, Width = width, Height = 26, CornerRadius = new CornerRadius(fill is null ? 6 : 7), Margin = new Thickness(1, 0, 1, 0),
                Background = normal, Cursor = Cursors.Hand, ToolTip = tip,
            };
            b.MouseEnter += (_, _) => b.Background = over;
            b.MouseLeave += (_, _) => b.Background = normal;
            b.MouseLeftButtonUp += (_, e) => { e.Handled = true; a(); };
            return b;
        }
        UIElement Icon(string glyph, bool on)
        {
            var g = Glyph(glyph, 14, on ? Theme.AccentBrush : ink);
            g.HorizontalAlignment = HorizontalAlignment.Center;
            return g;
        }
        UIElement Sep() => new Border { Width = 1, Height = 16, Background = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)), Margin = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (tool, glyph, tip) in new[]
        {
            (ShotTool.Rect, "", L("矩形", "Rectangle")), (ShotTool.Arrow, "", L("箭头", "Arrow")), (ShotTool.Pen, "", L("画笔", "Pen")),
            (ShotTool.Text, "", L("文字（双击文字可再次编辑）", "Text (double-click a text to edit it)")), (ShotTool.Mosaic, "", L("马赛克 / 模糊", "Mosaic / blur")),
        })
        {
            var t = tool;
            bool on = _tool == t;
            // Icon fonts' text glyphs vary; the text tool draws a plain bold A.
            UIElement icon = t == ShotTool.Text
                ? new TextBlock { Text = "A", FontFamily = new FontFamily("Georgia, Cambria, Times New Roman, Segoe UI"), FontSize = 16, FontWeight = FontWeights.Bold,
                                  Foreground = on ? Theme.AccentBrush : ink, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                : Icon(glyph, on);
            row.Children.Add(Tile(icon, tip, on, () =>
            {
                _tool = _tool == t ? null : t;
                if (_tool != ShotTool.Text) _selText = null;
                Cursor = _tool is null ? Cursors.SizeAll : Cursors.Cross;
                Redraw();
            }));
        }
        row.Children.Add(Sep());
        row.Children.Add(Tile(Icon("", false), L("撤销  Ctrl+Z", "Undo  Ctrl+Z"), false, Undo));
        row.Children.Add(Tile(Icon("", false), L("识别图中文字", "Recognize text"), false, Ocr));
        row.Children.Add(Tile(Icon("", false), L("贴到屏幕", "Pin to screen"), false, Pin));
        row.Children.Add(Tile(Icon("", false), L("保存到文件", "Save to file"), false, Save));
        row.Children.Add(Sep());
        row.Children.Add(Tile(Icon("", false), L("取消  Esc", "Cancel  Esc"), false, Screenshot.CloseAll));
        var check = Glyph("", 13, Brushes.White); check.FontWeight = FontWeights.Bold; check.HorizontalAlignment = HorizontalAlignment.Center;
        var copy = Tile(check, L("复制到剪贴板（回车 / 双击选区）", "Copy to clipboard (Return / double-click)"), false, CopyAndClose, 34,
                        Theme.AccentBrush, Theme.B(Color.FromRgb((byte)(accent.R * 0.88), (byte)(accent.G * 0.88), (byte)(accent.B * 0.88))));
        copy.Margin = new Thickness(3, 0, 1, 0);
        row.Children.Add(copy);

        Border Panel(UIElement child, Thickness pad) => new()
        {
            Background = new SolidColorBrush(Color.FromArgb(245, 250, 250, 250)), CornerRadius = new CornerRadius(8), Padding = pad, Child = child,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, ShadowDepth = 3, Opacity = 0.3 }, HorizontalAlignment = HorizontalAlignment.Left,
        };
        var rows = new StackPanel();
        rows.Children.Add(Panel(row, new Thickness(4)));
        // Row 2: options for the selected tool, or the text options while a text is selected.
        if ((_selText is not null && _textBox is null ? ShotTool.Text : _tool) is { } active)
        {
            var second = Panel(Options(active), new Thickness(5)); second.Margin = new Thickness(0, 4, 0, 0);
            rows.Children.Add(second);
        }
        _toolbar = new Border { Child = rows, Cursor = Cursors.Arrow, Background = Brushes.Transparent };
        _toolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double w0 = _toolbar.DesiredSize.Width, h0 = _toolbar.DesiredSize.Height;
        double x = Math.Clamp(sel.Right - w0, 8, Math.Max(8, ActualWidth - w0 - 8));
        double y = sel.Bottom + 8;
        if (y + h0 > ActualHeight - 8) y = sel.Top - h0 - 8;
        if (y < 8) y = sel.Bottom - h0 - 8;
        Canvas.SetLeft(_toolbar, x); Canvas.SetTop(_toolbar, y);
        _ui.Children.Add(_toolbar);
    }

    /// Row 2: options for the selected tool, like WeChat's screenshot bar.
    StackPanel Options(ShotTool tool)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        Border Chip(UIElement content, bool on, string tip, Action a)
        {
            var b = new Border { Child = content, Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(1, 0, 1, 0), CornerRadius = new CornerRadius(6), Cursor = Cursors.Hand, ToolTip = tip,
                                 Background = on ? Theme.B(Theme.CpuC, 0.25) : Brushes.Transparent, MinHeight = 28 };
            b.MouseLeftButtonUp += (_, e) => { a(); e.Handled = true; Redraw(); };
            return b;
        }
        UIElement Sep() => new Border { Width = 1, Background = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)), Margin = new Thickness(6, 4, 6, 4) };
        void Colors()
        {
            foreach (var c in Screenshot.Colors)
            {
                var col = c;
                var dot = new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(c), Margin = new Thickness(3, 6, 3, 6), Cursor = Cursors.Hand,
                                       BorderBrush = _color == c ? Brushes.Black : new SolidColorBrush(Color.FromArgb(60, 0, 0, 0)), BorderThickness = new Thickness(_color == c ? 2 : 1) };
                dot.MouseLeftButtonUp += (_, ev) => { _color = col; RestyleSelected(color: col); ev.Handled = true; Redraw(); };
                row.Children.Add(dot);
            }
        }
        void Sizes(double[] dots, string[] tips)
        {
            for (int i = 0; i < 3; i++)
            {
                int level = i;
                var dot = new Ellipse { Width = dots[i], Height = dots[i], Fill = Brushes.Black, VerticalAlignment = VerticalAlignment.Center };
                row.Children.Add(Chip(new Grid { Width = 20, Height = 22, Children = { dot } }, _strokeLevel == i, tips[i], () => _strokeLevel = level));
            }
        }
        UIElement Word(string t) => new TextBlock { Text = t, FontSize = 12, Foreground = Brushes.Black, VerticalAlignment = VerticalAlignment.Center };
        switch (tool)
        {
            case ShotTool.Text:
                foreach (var (size, zh, en) in new[] { (14.0, "小", "Small"), (20.0, "中", "Medium"), (28.0, "大", "Large"), (40.0, "特大", "X-Large"), (56.0, "超大", "XX-Large") })
                {
                    var fs = size;
                    var a = new TextBlock { Text = "A", FontSize = 8 + size / 4, FontWeight = FontWeights.SemiBold, Foreground = Brushes.Black, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
                    row.Children.Add(Chip(new Grid { Width = 20, Height = 24, Children = { a } }, Math.Abs(_fontSize - size) < 0.5, L($"{zh}（{size:0}）", $"{en} ({size:0})"), () => { _fontSize = fs; RestyleSelected(size: fs); }));
                }
                row.Children.Add(Sep());
                Colors();
                break;
            case ShotTool.Mosaic:
                row.Children.Add(Chip(Word(L("涂抹", "Brush")), _mosaicBrush, L("按住拖动涂抹", "Drag to paint"), () => _mosaicBrush = true));
                row.Children.Add(Chip(Word(L("框选", "Box")), !_mosaicBrush, L("拖出一个框", "Drag a box"), () => _mosaicBrush = false));
                row.Children.Add(Sep());
                row.Children.Add(Chip(Word(L("马赛克", "Pixelate")), !_mosaicBlur, L("方块马赛克", "Pixel blocks"), () => _mosaicBlur = false));
                row.Children.Add(Chip(Word(L("模糊", "Blur")), _mosaicBlur, L("毛玻璃模糊", "Frosted blur"), () => _mosaicBlur = true));
                if (_mosaicBrush) { row.Children.Add(Sep()); Sizes(new[] { 8.0, 12.0, 17.0 }, new[] { L("细", "Small"), L("中", "Medium"), L("粗", "Large") }); }
                break;
            default:
                Sizes(new[] { 4.0, 7.0, 11.0 }, new[] { L("细", "Thin"), L("中", "Medium"), L("粗", "Thick") });
                row.Children.Add(Sep());
                Colors();
                break;
        }
        return row;
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

    /// Recognize text in the selection (implemented in UI/ShotOcr.cs).
    void Ocr()
    {
        var img = Export();
        if (img is null) return;
        Screenshot.CloseAll();
        ShotOcr.Run(img);
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
        _selection = selection; _editing = true; _tool = ShotTool.Mosaic;
        _marks.AddRange(marks);
        Redraw();
    }

    /// Test hook: select the last text mark (no tool), as after clicking it.
    public void DemoSelectText()
    {
        _tool = null;
        if (_marks.LastOrDefault(m => m.Kind == ShotTool.Text) is { } m) SelectText(m);
        Redraw();
    }

    /// Test hook: restyle the selected text and check that a click-sized pen mark is dropped.
    public string DemoChecks()
    {
        RestyleSelected(size: 40);
        var size = _selText?.Width ?? 0;
        bool tiny = new ShotMark { Kind = ShotTool.Pen, Points = { new Point(10, 10), new Point(10, 10) }, Width = 4 }.IsTiny;
        Redraw();
        return $"text restyle size={size:0} tinyClickDropped={tiny}";
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
