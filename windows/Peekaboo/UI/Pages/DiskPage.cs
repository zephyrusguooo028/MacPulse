using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using static Peekaboo.UIX;

namespace Peekaboo;

public sealed class DiskPage : PageView
{
    DiskNode? _hovered;
    readonly TextBlock _hoverLine = T("", 12.5, color: Theme.Secondary);
    DiskModel M => DiskModel.Shared;

    public override void Shown() { M.Changed += Build; Build(); }
    public override void Hidden() => M.Changed -= Build;

    void Build()
    {
        var d = SystemMonitor.Shared.Disk;
        var actions = H(8);
        if (M.Scanning) actions.Children.Add(Btn("停止", M.Cancel));
        else
        {
            actions.Children.Add(Btn("选择文件夹…", ChooseFolder, glyph: ""));
            actions.Children.Add(Btn(M.Root is null ? "扫描用户目录" : "重新扫描", () => M.Scan(Paths.Home), primary: true, glyph: ""));
        }

        var drives = V(12);
        foreach (var dr in DiskReader.All())
        {
            drives.Children.Add(V(6, Row(T(dr.Name, 13, FontWeights.SemiBold), T($"可用 {Fmt.Bytes(dr.Free)} / 共 {Fmt.Bytes(dr.Total)}", 12, color: Theme.Secondary)),
                Stacked(new List<(string, double, Color)> { ("已用", dr.Used, Theme.CpuC), ("空闲", dr.Free, Color.FromArgb(120, Theme.DiskC.R, Theme.DiskC.G, Theme.DiskC.B)) }, dr.Total, 14, legend: false)));
        }
        if (M.Root is { } r && r.Path.Equals(Paths.Home, StringComparison.OrdinalIgnoreCase))
        {
            var note = T($"其中你的用户目录占 {Fmt.Bytes(r.Size)}，其余是 Windows 系统、Program Files 里的程序、页面文件和休眠文件等。", 12, color: Theme.Secondary);
            note.TextWrapping = TextWrapping.Wrap;
            drives.Children.Add(note);
        }

        var parts = new List<UIElement> { PageHeader("磁盘", $"{d.Name} 共 {Fmt.Bytes(d.Total)}，可用 {Fmt.Bytes(d.Free)}", actions), Card("本机磁盘", "", drives) };

        if (M.Scanning)
            parts.Add(Card($"正在扫描 {Fmt.Tilde(M.ScanRoot)}", "", T($"已统计 {M.Files:N0} 个文件，{Fmt.Bytes(M.Bytes)}", 13)));

        if (M.Current is { } cur)
        {
            parts.Add(Breadcrumb(cur));
            var map = Treemap(cur);
            var list = ChildList(cur);
            list.Width = 330;
            var body = new DockPanel { Height = 470 };
            DockPanel.SetDock(list, Dock.Right); list.Margin = new Thickness(14, 0, 0, 0);
            body.Children.Add(list);
            body.Children.Add(V(6, map, _hoverLine));
            parts.Add(body);
            _hoverLine.Text = "鼠标移到方块上查看详情，点击文件夹进入，右键可以在资源管理器中显示或移到回收站";
        }
        else if (!M.Scanning)
        {
            var start = Btn("开始扫描", () => M.Scan(Paths.Home), primary: true, glyph: "");
            start.HorizontalAlignment = HorizontalAlignment.Center;
            parts.Add(Card("看看空间都去哪了", "", V(4, Empty("", "还没扫描", $"扫描你的用户目录（{Paths.Home}），用方块图显示每个文件夹占了多少空间。\n第一次扫描可能需要一两分钟。"), start)));
        }
        Content = V(16, parts.ToArray());
    }

    FrameworkElement Breadcrumb(DiskNode node)
    {
        var s = H(4);
        var up = Btn("", M.Up, glyph: ""); up.IsEnabled = node.Parent is not null; up.Padding = new Thickness(8, 4, 8, 4);
        s.Children.Add(up);
        foreach (var n in node.Chain())
        {
            if (n.Parent is not null) s.Children.Add(Glyph("", 10, Theme.Tertiary));
            var t = T(n.Parent is null ? Fmt.Tilde(n.Path) : n.Name, 13, n == node ? FontWeights.SemiBold : FontWeights.Normal, n == node ? Theme.Text : Theme.Secondary);
            t.Cursor = Cursors.Hand; t.VerticalAlignment = VerticalAlignment.Center;
            var target = n;
            t.MouseLeftButtonUp += (_, _) => { M.Current = target; Build(); };
            s.Children.Add(t);
        }
        var info = T($"{Fmt.Bytes(node.Size)} · {node.FileCount:N0} 个文件" + (M.LastScan is { } ls ? $" · 扫描于 {ls:HH:mm}" : ""), 12.5, color: Theme.Secondary);
        return Row(s, info);
    }

    FrameworkElement Treemap(DiskNode node)
    {
        var canvas = new Canvas { ClipToBounds = true, Background = Theme.CardAlt, Height = 440 };
        var kids = node.Children.Where(c => c.Size > 0).Take(120).ToList();
        void Draw(Size size)
        {
            canvas.Children.Clear();
            var rects = Peekaboo.Treemap.Layout(kids.Select(k => (double)k.Size).ToList(), new Rect(0, 0, size.Width, size.Height));
            for (int i = 0; i < kids.Count; i++)
            {
                var k = kids[i]; var r = rects[i];
                if (r.Width < 2 || r.Height < 2) continue;
                var c = k.IsAggregate ? Color.FromRgb(140, 140, 140) : Theme.TreemapC[i % Theme.TreemapC.Length];
                var cell = new Border
                {
                    Width = r.Width - 2, Height = r.Height - 2, CornerRadius = new CornerRadius(4),
                    Background = new LinearGradientBrush(Color.FromArgb(235, c.R, c.G, c.B), Color.FromArgb(185, c.R, c.G, c.B), 45),
                    Cursor = k.IsDirectory ? Cursors.Hand : Cursors.Arrow,
                };
                if (r.Width > 56 && r.Height > 30)
                {
                    var label = V(1, T((k.IsDirectory ? "📁 " : "") + k.Name, 11.5, FontWeights.SemiBold, Brushes.White));
                    if (r.Height > 44) label.Children.Add(T(Fmt.Bytes(k.Size), 10.5, color: Theme.B(Colors.White, 0.85)));
                    label.Margin = new Thickness(6);
                    cell.Child = label;
                }
                cell.MouseEnter += (_, _) => { _hovered = k; cell.Opacity = 0.85; _hoverLine.Text = $"{(k.IsAggregate ? k.Name : Fmt.Tilde(k.Path))}    {Fmt.Bytes(k.Size)}{(k.IsDirectory ? " · 点击进入" : "")}"; };
                cell.MouseLeave += (_, _) => { cell.Opacity = 1; };
                cell.MouseLeftButtonUp += (_, _) => M.Open(k);
                if (!k.IsAggregate) cell.ContextMenu = FileMenu(k.Path, k.Size, () => M.Removed(k));
                Canvas.SetLeft(cell, r.X + 1); Canvas.SetTop(cell, r.Y + 1);
                canvas.Children.Add(cell);
            }
        }
        canvas.SizeChanged += (_, e) => Draw(e.NewSize);
        return new Border { CornerRadius = new CornerRadius(10), ClipToBounds = true, Child = canvas };
    }

    Border ChildList(DiskNode node)
    {
        long top = Math.Max(1, node.Children.FirstOrDefault()?.Size ?? 1);
        var s = V(2);
        int i = 0;
        foreach (var c in node.Children.Take(200))
        {
            var color = c.IsAggregate ? Color.FromRgb(140, 140, 140) : Theme.TreemapC[i % Theme.TreemapC.Length];
            var row = new Border
            {
                Padding = new Thickness(8, 5, 8, 5), CornerRadius = new CornerRadius(6), Background = Brushes.Transparent, Cursor = Cursors.Hand,
                Child = V(4,
                    Row(H(6, new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(2), Background = Theme.B(color) },
                              Glyph(c.IsAggregate ? "" : c.IsDirectory ? "" : "", 12), T(c.Name, 12.5)),
                        T(Fmt.Bytes(c.Size), 12, color: Theme.Secondary)),
                    Bar((double)c.Size / top, color, 4)),
            };
            var node2 = c;
            row.MouseEnter += (_, _) => row.Background = Theme.Hover;
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
            row.MouseLeftButtonUp += (_, _) => M.Open(node2);
            if (!c.IsAggregate) row.ContextMenu = FileMenu(c.Path, c.Size, () => M.Removed(node2));
            s.Children.Add(row);
            i++;
        }
        return Panel(new ScrollViewer { Content = s, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, 6);
    }

    void ChooseFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = Paths.Home, Title = "选择要扫描的文件夹" };
        if (dlg.ShowDialog() == true) M.Scan(dlg.FolderName);
    }
}
