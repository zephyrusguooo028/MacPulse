using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using static Peekaboo.UIX;

namespace Peekaboo;

public enum WidgetSize { Small, Medium }

/// Desktop widget options, saved in widget.json next to settings.json.
public sealed class WidgetSettings
{
    public bool Enabled { get; set; }
    public WidgetSize Size { get; set; } = WidgetSize.Medium;
    public bool ShowCpu { get; set; } = true;
    public bool ShowMemory { get; set; } = true;
    public bool ShowDisk { get; set; } = true;
    public bool ShowNetwork { get; set; } = true;
    public bool ShowBattery { get; set; } = true;
    public bool ShowTyping { get; set; } = true;
    public double Opacity { get; set; } = 1;
    public bool OnTop { get; set; }
    public double? Left { get; set; }
    public double? Top { get; set; }

    static string FilePath => Path.Combine(Paths.Support, "widget.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    static WidgetSettings? _shared;

    public static WidgetSettings Shared
    {
        get
        {
            if (_shared is null)
            {
                try { if (File.Exists(FilePath)) _shared = JsonSerializer.Deserialize<WidgetSettings>(File.ReadAllText(FilePath), Json); } catch { }
                _shared ??= new WidgetSettings();
            }
            return _shared;
        }
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json)); } catch { }
    }
}

public static class DesktopWidget
{
    static WidgetWindow? _window;

    /// Called once at launch. Also starts the battery history recorder, which runs in the
    /// background whether or not the battery page is open.
    public static void Start()
    {
        BatteryHistory.Start();
        Apply();
    }

    /// Shows, hides or restyles the widget to match the settings.
    public static void Apply()
    {
        var s = WidgetSettings.Shared;
        if (!s.Enabled)
        {
            _window?.Close();
            _window = null;
            return;
        }
        if (_window is null)
        {
            _window = new WidgetWindow();
            _window.Closed += (_, _) => _window = null;
            _window.Show();
        }
        _window.ApplySettings();
    }

    public static void Hide()
    {
        WidgetSettings.Shared.Enabled = false;
        WidgetSettings.Shared.Save();
        Apply();
    }
}

/// Borderless mini window. By default it lives at the bottom of the z-order, just above
/// the desktop; "always on top" makes it a topmost window instead.
public sealed class WidgetWindow : Window
{
    readonly Border _card;
    readonly Image _icon = new() { Stretch = Stretch.Uniform };
    readonly Dictionary<string, System.Windows.Media.Imaging.BitmapSource> _frames = new();
    IntPtr _hwnd;

    static readonly IntPtr HWND_BOTTOM = new(1), HWND_TOPMOST = new(-1), HWND_NOTOPMOST = new(-2);
    const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
    const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80;
    const int WM_WINDOWPOSCHANGING = 0x0046;

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

    [StructLayout(LayoutKind.Sequential)]
    struct WINDOWPOS { public IntPtr hwnd, hwndInsertAfter; public int x, y, cx, cy; public uint flags; }

    public WidgetWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        FontFamily = Theme.Font;
        Title = "Peekaboo widget";
        RenderOptions.SetBitmapScalingMode(_icon, BitmapScalingMode.NearestNeighbor);

        _card = new Border
        {
            Background = Theme.Card,
            BorderBrush = Theme.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Margin = new Thickness(8),   // room for the shadow
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Opacity = Theme.Dark ? 0.5 : 0.18 },
        };
        Content = _card;

        var s = WidgetSettings.Shared;
        WindowStartupLocation = WindowStartupLocation.Manual;
        var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (s.Left is { } l && s.Top is { } t && vs.Contains(new Point(l + 20, t + 20))) { Left = l; Top = t; }
        else { var wa = SystemParameters.WorkArea; Left = wa.Right - 290; Top = wa.Top + 24; }

        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed) return;
            try { DragMove(); } catch { }
            s.Left = Left; s.Top = Top; s.Save();
        };
        ContextMenu = BuildMenu();
        ContextMenuOpening += (_, _) => ContextMenu = BuildMenu();

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            // Tool window: no Alt+Tab entry.
            long ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
            SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TOOLWINDOW));
            HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        };
        Loaded += (_, _) => PlaceInZOrder();
        Activated += (_, _) => PlaceInZOrder();

        SystemMonitor.Shared.Ticked += Rebuild;
        TrayController.Shared.Viewer(true);
        TrayController.Shared.FrameChanged += AnimateIcon;
        Closed += (_, _) =>
        {
            SystemMonitor.Shared.Ticked -= Rebuild;
            TrayController.Shared.Viewer(false);
            TrayController.Shared.FrameChanged -= AnimateIcon;
        };
        Rebuild();
        AnimateIcon();
    }

    public void ApplySettings()
    {
        var s = WidgetSettings.Shared;
        Opacity = Math.Clamp(s.Opacity, 0.3, 1);
        Topmost = s.OnTop;
        PlaceInZOrder();
        Rebuild();
    }

    void PlaceInZOrder()
    {
        if (_hwnd == IntPtr.Zero) return;
        var after = WidgetSettings.Shared.OnTop ? HWND_TOPMOST : HWND_BOTTOM;
        if (!WidgetSettings.Shared.OnTop) SetWindowPos(_hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        SetWindowPos(_hwnd, after, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// Keeps the window at the bottom whenever Windows tries to raise it (clicks, Alt+Tab, etc.).
    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_WINDOWPOSCHANGING && !WidgetSettings.Shared.OnTop && lParam != IntPtr.Zero)
        {
            var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
            if ((pos.flags & SWP_NOZORDER) == 0)
            {
                pos.hwndInsertAfter = HWND_BOTTOM;
                Marshal.StructureToPtr(pos, lParam, false);
            }
        }
        return IntPtr.Zero;
    }

    ContextMenu BuildMenu()
    {
        var m = new ContextMenu();
        void Add(string header, Action a, bool? check = null)
        {
            var i = new MenuItem { Header = header };
            if (check is { } c) { i.IsCheckable = true; i.IsChecked = c; }
            i.Click += (_, _) => a();
            m.Items.Add(i);
        }
        Add(L("打开 Peekaboo", "Open Peekaboo"), () => MainWindow.Show(Page.Overview));
        Add(L("小组件设置…", "Widget settings…"), () => MainWindow.Show(Page.Settings));
        m.Items.Add(new Separator());
        Add(L("总在最前", "Always on top"), () => { var s = WidgetSettings.Shared; s.OnTop = !s.OnTop; s.Save(); DesktopWidget.Apply(); }, WidgetSettings.Shared.OnTop);
        Add(L("隐藏小组件", "Hide widget"), DesktopWidget.Hide);
        return m;
    }

    void AnimateIcon()
    {
        var icon = Settings.Shared.Icon == TrayIcon.None ? TrayIcon.Ghost : Settings.Shared.Icon;
        int f = TrayController.Shared.Frame % IconArt.Frames(icon);
        var c = Theme.AccentColor;
        string key = $"{icon}{f}{c}";
        if (!_frames.TryGetValue(key, out var img))
        {
            using var bmp = IconArt.Render(icon, f, SystemMonitor.Shared.Cpu.Total, System.Drawing.Color.FromArgb(c.R, c.G, c.B), 64);
            img = IconArt.ToWpf(bmp);
            if (_frames.Count > 200) _frames.Clear();
            _frames[key] = img;
        }
        _icon.Source = img;
    }

    void Rebuild()
    {
        var s = WidgetSettings.Shared;
        var m = SystemMonitor.Shared;
        bool medium = s.Size == WidgetSize.Medium;
        double width = medium ? 236 : 160, fs = medium ? 12 : 11;
        _icon.Width = _icon.Height = medium ? 20 : 16;

        var body = V(medium ? 10 : 7);
        var title = H(6, _icon, T("Peekaboo", medium ? 13 : 11.5, FontWeights.SemiBold));
        UIElement? battery = null;
        if (s.ShowBattery && m.Battery is { } b)
            battery = H(3, Glyph(b.Charging ? "\uE945" : "\uEBA7", fs - 1), T(Fmt.Pct(b.Percent), fs - 0.5, color: Theme.Secondary));
        if (_icon.Parent is Panel old) old.Children.Remove(_icon);
        body.Children.Add(Row(title, battery));

        var memC = m.Memory.PressureLevel >= 4 ? Theme.DangerC : Theme.MemC;
        if (medium && (s.ShowCpu || s.ShowMemory))
        {
            var rings = H(18);
            rings.HorizontalAlignment = HorizontalAlignment.Center;
            if (s.ShowCpu) rings.Children.Add(Labeled(Ring(m.Cpu.Total, Theme.Load(m.Cpu.Total), Fmt.Pct(m.Cpu.Total), 62, 6), "CPU"));
            if (s.ShowMemory) rings.Children.Add(Labeled(Ring(m.Memory.UsedFraction, memC, Fmt.Pct(m.Memory.UsedFraction), 62, 6), L("内存", "Memory")));
            body.Children.Add(rings);
        }
        else
        {
            if (s.ShowCpu) body.Children.Add(V(3, Row(T("CPU", fs, color: Theme.Secondary), T(Fmt.Pct(m.Cpu.Total), fs)), Bar(m.Cpu.Total, Theme.Load(m.Cpu.Total), 5)));
            if (s.ShowMemory) body.Children.Add(V(3, Row(T(L("内存", "Mem"), fs, color: Theme.Secondary), T(Fmt.Pct(m.Memory.UsedFraction), fs)), Bar(m.Memory.UsedFraction, memC, 5)));
        }
        if (s.ShowDisk)
            body.Children.Add(Row(H(6, Glyph("", fs, Theme.B(m.Disk.UsedFraction > 0.9 ? Theme.DangerC : Theme.DiskC)), T(L("磁盘可用", "Disk free"), fs, color: Theme.Secondary)), T(Fmt.Bytes(m.Disk.Free), fs)));
        if (s.ShowNetwork)
            body.Children.Add(Row(T("↓ " + (medium ? Fmt.Rate(m.NetIn) : Fmt.Short(m.NetIn) + "/s"), fs, FontWeights.Medium, Theme.B(Theme.NetInC)),
                                  T("↑ " + (medium ? Fmt.Rate(m.NetOut) : Fmt.Short(m.NetOut) + "/s"), fs, FontWeights.Medium, Theme.B(Theme.NetOutC))));
        if (s.ShowTyping && TypingMonitor.Shared.Running)
            body.Children.Add(Row(H(6, Glyph("", fs, Theme.B(Theme.MemC)), T(L("今日按键", "Keys today"), fs, color: Theme.Secondary)), T(TypingMonitor.Shared.Today.Total.ToString("N0"), fs)));

        body.Width = width;
        _card.Padding = new Thickness(medium ? 14 : 10);
        _card.Child = body;
    }

    static FrameworkElement Labeled(FrameworkElement ring, string label)
    {
        var t = T(label, 10.5, color: Theme.Secondary); t.HorizontalAlignment = HorizontalAlignment.Center;
        return V(4, ring, t);
    }
}

public static class WidgetSettingsCard
{
    public static UIElement Build()
    {
        var s = WidgetSettings.Shared;
        var options = V(10);
        void Changed() { s.Save(); DesktopWidget.Apply(); }

        var size = H(4);
        void PaintSize()
        {
            size.Children.Clear();
            foreach (var (label, val) in new[] { (L("小", "Small"), WidgetSize.Small), (L("中", "Medium"), WidgetSize.Medium) })
            {
                var v = val;
                var b = Btn(label, () => { s.Size = v; Changed(); PaintSize(); }, primary: s.Size == val);
                b.Padding = new Thickness(14, 3, 14, 3);
                size.Children.Add(b);
            }
        }
        PaintSize();
        options.Children.Add(H(10, T(L("大小", "Size"), 13), size));

        var metrics = new WrapPanel();
        void M(string label, bool on, Action<bool> set)
        {
            var c = Check(label, on, v => { set(v); Changed(); });
            c.Margin = new Thickness(0, 0, 16, 4);
            metrics.Children.Add(c);
        }
        M("CPU", s.ShowCpu, v => s.ShowCpu = v);
        M(L("内存", "Memory"), s.ShowMemory, v => s.ShowMemory = v);
        M(L("磁盘可用", "Free disk"), s.ShowDisk, v => s.ShowDisk = v);
        M(L("网速", "Network"), s.ShowNetwork, v => s.ShowNetwork = v);
        M(L("电池", "Battery"), s.ShowBattery, v => s.ShowBattery = v);
        M(L("今日按键", "Keys today"), s.ShowTyping, v => s.ShowTyping = v);
        options.Children.Add(metrics);

        var pct = T(Fmt.Pct(s.Opacity), 12.5, color: Theme.Secondary); pct.VerticalAlignment = VerticalAlignment.Center;
        var slider = new Slider { Minimum = 0.3, Maximum = 1, Value = Math.Clamp(s.Opacity, 0.3, 1), Width = 200, VerticalAlignment = VerticalAlignment.Center };
        slider.ValueChanged += (_, e) => { s.Opacity = Math.Round(e.NewValue, 2); pct.Text = Fmt.Pct(s.Opacity); Changed(); };
        options.Children.Add(H(10, T(L("不透明度", "Opacity"), 13), slider, pct));
        options.Children.Add(Check(L("总在最前（盖在其他窗口上面）", "Always on top (above other windows)"), s.OnTop, v => { s.OnTop = v; Changed(); }));
        options.Visibility = s.Enabled ? Visibility.Visible : Visibility.Collapsed;

        var note = T(L("显示动画图标和实时数据，可以拖到任意位置；右键可以打开 Peekaboo 或隐藏。默认待在其他窗口下方，只在桌面上看得到。",
                       "Shows the animated icon and live numbers. Drag it anywhere; right-click to open Peekaboo or hide it. By default it stays below other windows, so you see it on the desktop."),
                     12, color: Theme.Secondary);
        note.TextWrapping = TextWrapping.Wrap; note.TextTrimming = TextTrimming.None;
        var enable = Check(L("在桌面显示一个悬浮小窗", "Show a small floating window on the desktop"), s.Enabled, v =>
        {
            s.Enabled = v; Changed();
            options.Visibility = v ? Visibility.Visible : Visibility.Collapsed;
        });
        return Card(L("桌面小组件", "Desktop widget"), "", V(10, enable, note, options));
    }
}
