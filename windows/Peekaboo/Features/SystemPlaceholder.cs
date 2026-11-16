// Scaffolding for v1.1 features. Each feature owner replaces its placeholder by
// deleting it here and adding a real implementation in its own file(s).
using System.Windows;
using System.Windows.Media.Imaging;
using static Peekaboo.UIX;

namespace Peekaboo;

// Battery + network + desktop widget (owner: system)
public sealed class BatteryPage : PageView { public static string? Badge => null; public BatteryPage() { Content = T("Battery"); } }
public sealed class NetworkPage : PageView { public static string? Badge => null; public NetworkPage() { Content = T("Network"); } }
public static class DesktopWidget { public static void Start() { } }
public static class WidgetSettingsCard { public static UIElement Build() => new System.Windows.Controls.Border(); }
