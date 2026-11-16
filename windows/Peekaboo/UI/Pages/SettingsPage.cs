using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using static Peekaboo.UIX;

namespace Peekaboo;

public sealed class SettingsPage : PageView
{
    Settings S => Settings.Shared;
    readonly List<(Image img, TrayIcon icon)> _previews = new();
    readonly Dictionary<string, System.Windows.Media.Imaging.BitmapSource> _cache = new();

    public override void Shown()
    {
        TrayController.Shared.Viewer(true);
        TrayController.Shared.FrameChanged += Animate;
        Build();
    }

    public override void Hidden()
    {
        TrayController.Shared.Viewer(false);
        TrayController.Shared.FrameChanged -= Animate;
    }

    void Save() { S.Save(); Build(); }

    void Animate()
    {
        int f = TrayController.Shared.Frame;
        foreach (var (img, icon) in _previews)
        {
            var c = S.Icon == icon ? Theme.AccentColor : ((SolidColorBrush)Theme.Text).Color;
            int fr = f % IconArt.Frames(icon);
            var key = $"{icon}{fr}{c}";
            if (!_cache.TryGetValue(key, out var src))
            {
                using var bmp = IconArt.Render(icon, fr, SystemMonitor.Shared.Cpu.Total, System.Drawing.Color.FromArgb(c.R, c.G, c.B), 72);
                src = IconArt.ToWpf(bmp);
                _cache[key] = src;
            }
            img.Source = src;
        }
    }

    void Build()
    {
        _previews.Clear();

        // ---- tray
        var grid = new UniformGrid { Columns = 5 };
        foreach (var icon in Enum.GetValues<TrayIcon>())
        {
            var ic = icon;
            FrameworkElement visual;
            if (icon == TrayIcon.None) visual = Glyph("", 22, Theme.Tertiary);
            else { var img = new Image { Width = 36, Height = 36 }; RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor); _previews.Add((img, icon)); visual = img; }
            visual.HorizontalAlignment = HorizontalAlignment.Center;
            var label = T(IconArt.Title(icon), 12); label.HorizontalAlignment = HorizontalAlignment.Center;
            var tile = new Border
            {
                Margin = new Thickness(4), Padding = new Thickness(6, 10, 6, 8), CornerRadius = new CornerRadius(10), Cursor = Cursors.Hand,
                Background = S.Icon == icon ? Theme.AccentSoftBrush : Theme.CardAlt,
                BorderBrush = S.Icon == icon ? Theme.AccentBrush : Brushes.Transparent, BorderThickness = new Thickness(1.5),
                Child = V(6, new Grid { Height = 36, Children = { visual } }, label),
            };
            tile.MouseLeftButtonUp += (_, _) => { S.Icon = ic; Save(); };
            grid.Children.Add(tile);
        }
        Animate();

        var swatches = H(10, T("主题色", 13));
        foreach (var t in Enum.GetValues<ThemeChoice>())
        {
            var tc = t;
            Brush fill = t switch
            {
                ThemeChoice.System => new LinearGradientBrush(new GradientStopCollection { new(Colors.Red, 0), new(Colors.Orange, 0.25), new(Colors.LimeGreen, 0.5), new(Colors.DodgerBlue, 0.75), new(Colors.MediumPurple, 1) }, 0),
                ThemeChoice.Load => new LinearGradientBrush(Theme.DiskC, Theme.DangerC, 0),
                _ => Theme.B(Theme.ChoiceColor(t, 0)),
            };
            var dot = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = fill, BorderBrush = S.Theme == t ? Theme.Text : Theme.Line, BorderThickness = new Thickness(S.Theme == t ? 2 : 1), HorizontalAlignment = HorizontalAlignment.Center };
            var cap = T(Theme.ChoiceTitle(t), 10.5, color: Theme.Secondary); cap.HorizontalAlignment = HorizontalAlignment.Center;
            var sw = V(4, dot, cap); sw.Cursor = Cursors.Hand; sw.Background = Brushes.Transparent;
            sw.MouseLeftButtonUp += (_, _) => { S.Theme = tc; Save(); MainWindow.Instance?.Navigate(Page.Settings); };
            swatches.Children.Add(sw);
        }

        var metrics = new UniformGrid { Columns = 4 };
        var titles = new Dictionary<TrayMetric, string>
        {
            [TrayMetric.Cpu] = "CPU 占用", [TrayMetric.CpuGraph] = "CPU 走势图", [TrayMetric.Memory] = "内存占用", [TrayMetric.Disk] = "磁盘剩余（GB）",
            [TrayMetric.Network] = "下载速度", [TrayMetric.Gpu] = "GPU 占用", [TrayMetric.Battery] = "电池",
        };
        foreach (var m in Enum.GetValues<TrayMetric>())
        {
            var mm = m;
            var c = Check(titles[m], S.Shows(m), on => S.SetShown(mm, on));
            c.Margin = new Thickness(0, 4, 0, 4);
            metrics.Children.Add(c);
        }
        var interval = new ComboBox { Width = 100 };
        foreach (var v in new[] { 1.0, 2.0, 3.0, 5.0 })
        {
            var item = new ComboBoxItem { Content = $"{v:0} 秒", Tag = v };
            interval.Items.Add(item);
            if (Math.Abs(S.SampleInterval - v) < 0.01) interval.SelectedItem = item;
        }
        interval.SelectionChanged += (_, _) => { S.SampleInterval = (double)((ComboBoxItem)interval.SelectedItem).Tag; S.Save(); SystemMonitor.Shared.Reschedule(); };

        var tray = Card("任务栏托盘", "", V(12,
            T("动画图标", 13, FontWeights.SemiBold),
            Note("CPU 越忙，动画越快。动画本身大约占 1–2% CPU，选「不显示」最省电。"),
            grid,
            Check("图标按 CPU 负载变色（绿 / 橙 / 红）", S.IconTint, on => { S.IconTint = on; S.Save(); }),
            swatches,
            new Border { Height = 1, Background = Theme.Line },
            T("托盘里显示的数值", 13, FontWeights.SemiBold),
            Note("Windows 托盘只能放图标，所以每个勾选的数值都会单独占一个小图标，鼠标悬停可以看到详情。"),
            metrics,
            H(8, T("刷新间隔", 13), interval, Note("间隔越长越省电"))));

        // ---- general
        var login = Check("开机时自动启动", Settings.LaunchAtLogin, on => Settings.LaunchAtLogin = on);
        var general = Card("通用", "", V(10, login));

        // ---- alerts
        var alerts = Card("提醒", "", V(10,
            H(8, Check("磁盘可用空间低于", S.AlertDisk, on => { S.AlertDisk = on; S.Save(); }), Stepper(() => S.AlertDiskGB, v => S.AlertDiskGB = v, 5, 500, 5, " GB"), T("时提醒", 13)),
            Check("内存压力持续紧张时提醒", S.AlertMemory, on => { S.AlertMemory = on; S.Save(); }),
            H(8, Check("CPU 连续 5 分钟高于", S.AlertCpu, on => { S.AlertCpu = on; S.Save(); }), Stepper(() => S.AlertCpuPercent, v => S.AlertCpuPercent = v, 50, 100, 5, "%"), T("时提醒", 13)),
            H(8, Check("每周检查一次，可清理空间超过", S.CleanupTips, on => { S.CleanupTips = on; S.Save(); }), Stepper(() => S.CleanupTipGB, v => S.CleanupTipGB = v, 1, 100, 1, " GB"), T("时提示", 13)),
            Note("同一种提醒有冷却时间，不会反复打扰。")));

        var rules = Card("清理规则", "", V(10,
            H(8, T("下载文件夹里超过", 13), Stepper(() => S.DownloadsOldDays, v => S.DownloadsOldDays = v, 7, 720, 15, " 天"), T("没动的文件算「旧文件」", 13)),
            H(8, T("超过", 13), Stepper(() => S.LargeFileMB, v => S.LargeFileMB = v, 50, 10000, 50, " MB"), T("的文件算「大文件」", 13)),
            H(8, T("超过", 13), Stepper(() => S.StaleProjectDays, v => S.StaleProjectDays = v, 7, 365, 7, " 天"), T("没改动的项目，默认勾选它的依赖和构建产物", 13))));

        var about = Card("关于", "", V(6,
            Note("Peekaboo 只在本机读取系统信息，不联网、不上传任何数据。清理默认移到回收站，并且永远不碰系统目录和用户目录之外的文件。"),
            Note("CPU 温度在 Windows 上需要管理员权限才能读取，所以这里不显示。"),
            Note("数据保存在 " + Paths.Support)));

        var page = V(16, PageHeader("设置", "Peekaboo 1.0"), tray, general, alerts, rules,
            PathList("项目目录", "", "在这些目录里自动找开发项目（最多往下找 4 层）。", S.ProjectRoots),
            PathList("白名单", "", "这些位置永远不会被清理。也可以在任意列表里右键「加入白名单」。", S.Whitelist),
            about);
        page.MaxWidth = 820; page.HorizontalAlignment = HorizontalAlignment.Left;
        Content = page;
    }

    static TextBlock Note(string t) { var x = T(t, 12, color: Theme.Secondary); x.TextWrapping = TextWrapping.Wrap; x.TextTrimming = TextTrimming.None; return x; }

    FrameworkElement Stepper(Func<int> get, Action<int> set, int min, int max, int step, string unit)
    {
        var value = T($"{get()}{unit}", 13, FontWeights.SemiBold); value.MinWidth = 60; value.TextAlignment = TextAlignment.Center; value.VerticalAlignment = VerticalAlignment.Center;
        Button B(string g, int d)
        {
            var b = Btn("", () => { set(Math.Clamp(get() + d, min, max)); S.Save(); value.Text = $"{get()}{unit}"; }, glyph: g);
            b.Padding = new Thickness(6, 2, 6, 2);
            return b;
        }
        return H(4, B("", -step), value, B("", step));
    }

    Border PathList(string title, string glyph, string note, List<string> paths)
    {
        var s = V(6, Note(note));
        if (paths.Count == 0) s.Children.Add(T("（空）", 12.5, color: Theme.Tertiary));
        foreach (var p in paths.ToList())
        {
            var path = p;
            var exists = Directory.Exists(Settings.Expand(p)) || File.Exists(Settings.Expand(p));
            var remove = Btn("", () => { paths.Remove(path); Save(); }, glyph: ""); remove.Padding = new Thickness(6, 2, 6, 2);
            s.Children.Add(Row(H(8, Glyph("", 13), T(p, 13), T(exists ? "" : "不存在", 11.5, color: Theme.Tertiary)), remove));
        }
        var add = Btn("添加…", () =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = Paths.Home, Multiselect = true };
            if (dlg.ShowDialog() == true)
                foreach (var f in dlg.FolderNames) { var t = Fmt.Tilde(f); if (!paths.Contains(t)) paths.Add(t); }
            Save();
        }, glyph: "");
        return Card(title, glyph, s, add);
    }
}
