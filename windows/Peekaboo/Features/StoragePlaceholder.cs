// Scaffolding for v1.1 features. Each feature owner replaces its placeholder by
// deleting it here and adding a real implementation in its own file(s).
using System.Windows;
using System.Windows.Media.Imaging;
using static Peekaboo.UIX;

namespace Peekaboo;

// Duplicates + uninstaller (owner: storage)
public sealed class DuplicatesPage : PageView { public static string? Badge => null; public DuplicatesPage() { Content = T("Duplicates"); } }
public sealed class UninstallPage : PageView { public UninstallPage() { Content = T("Uninstall"); } }
