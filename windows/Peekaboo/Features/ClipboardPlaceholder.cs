// Scaffolding for v1.1 features. Each feature owner replaces its placeholder by
// deleting it here and adding a real implementation in its own file(s).
using System.Windows;
using System.Windows.Media.Imaging;
using static Peekaboo.UIX;

namespace Peekaboo;

// Clipboard history + OCR (owner: clipboard)
public static class ClipboardService { public static void Start() { } }
public sealed class ClipboardPage : PageView { public static string? Badge => null; public ClipboardPage() { Content = T("Clipboard"); } }
public static class ClipboardSettingsCard { public static UIElement Build() => new System.Windows.Controls.Border(); }
public static class ShotOcr { public static void Run(BitmapSource image) { } }
