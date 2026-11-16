using System.Windows;
using System.Windows.Media;

namespace Peekaboo;

/// Colours follow the Windows app theme (light / dark).
public static class Theme
{
    public static bool Dark { get; private set; }

    public static Brush Window = null!, Card = null!, CardAlt = null!, Text = null!, Secondary = null!, Tertiary = null!, Line = null!, Hover = null!, Accent = null!, AccentSoft = null!;

    public static readonly Color CpuC = Color.FromRgb(64, 133, 245), MemC = Color.FromRgb(158, 97, 242), DiskC = Color.FromRgb(33, 179, 140),
        NetInC = Color.FromRgb(26, 166, 217), NetOutC = Color.FromRgb(245, 140, 51), WarnC = Color.FromRgb(245, 158, 26), DangerC = Color.FromRgb(235, 71, 77);

    public static readonly Color[] TreemapC =
    {
        Color.FromRgb(64, 133, 245), Color.FromRgb(158, 97, 242), Color.FromRgb(33, 179, 140), Color.FromRgb(245, 140, 51),
        Color.FromRgb(230, 84, 140), Color.FromRgb(26, 166, 217), Color.FromRgb(191, 166, 38), Color.FromRgb(115, 140, 166),
    };

    public static readonly FontFamily Font = new("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI");
    public static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    public static void Load()
    {
        Dark = !Settings.AppsAreLight;
        Window = B(Dark ? Color.FromRgb(32, 32, 32) : Color.FromRgb(243, 243, 243));
        Card = B(Dark ? Color.FromRgb(43, 43, 43) : Colors.White);
        CardAlt = B(Dark ? Color.FromArgb(18, 255, 255, 255) : Color.FromArgb(10, 0, 0, 0));
        Text = B(Dark ? Colors.White : Color.FromRgb(26, 26, 26));
        Secondary = B(Dark ? Color.FromArgb(170, 255, 255, 255) : Color.FromArgb(150, 0, 0, 0));
        Tertiary = B(Dark ? Color.FromArgb(110, 255, 255, 255) : Color.FromArgb(100, 0, 0, 0));
        Line = B(Dark ? Color.FromArgb(28, 255, 255, 255) : Color.FromArgb(20, 0, 0, 0));
        Hover = B(Dark ? Color.FromArgb(22, 255, 255, 255) : Color.FromArgb(12, 0, 0, 0));
        Accent = B(Dark ? Color.FromRgb(96, 205, 255) : Color.FromRgb(0, 95, 184));
        AccentSoft = B(Dark ? Color.FromArgb(40, 96, 205, 255) : Color.FromArgb(28, 0, 95, 184));
    }

    public static string ChoiceTitle(ThemeChoice t) => t switch
    {
        ThemeChoice.System => "跟随系统", ThemeChoice.Mono => "黑白", ThemeChoice.Load => "跟随负载", ThemeChoice.Pink => "樱花粉",
        ThemeChoice.Mint => "薄荷绿", ThemeChoice.Purple => "葡萄紫", ThemeChoice.Orange => "橘子橙", _ => "天空蓝",
    };

    public static Color ChoiceColor(ThemeChoice t, double cpu) => t switch
    {
        ThemeChoice.System => Settings.SystemAccent,
        ThemeChoice.Mono => Dark ? Colors.White : Color.FromRgb(30, 30, 30),
        ThemeChoice.Load => Load(cpu),
        ThemeChoice.Pink => Color.FromRgb(245, 115, 158),
        ThemeChoice.Mint => Color.FromRgb(46, 189, 158),
        ThemeChoice.Purple => Color.FromRgb(148, 102, 242),
        ThemeChoice.Orange => Color.FromRgb(250, 148, 51),
        _ => Color.FromRgb(64, 153, 250),
    };

    /// The user's highlight colour right now.
    public static Color AccentColor => ChoiceColor(Settings.Shared.Theme, SystemMonitor.Shared.Cpu.Total);
    public static Brush AccentBrush => B(AccentColor);
    public static Brush AccentSoftBrush => B(AccentColor, 0.15);
    /// Text on top of the accent colour.
    public static Brush OnAccent => Settings.Shared.Theme == ThemeChoice.Mono && Dark ? B(Color.FromRgb(30, 30, 30)) : Brushes.White;

    public static SolidColorBrush B(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    public static SolidColorBrush B(Color c, double alpha) => B(Color.FromArgb((byte)(alpha * 255), c.R, c.G, c.B));

    public static Color Load(double v) => v < 0.6 ? DiskC : v < 0.85 ? WarnC : DangerC;
}
