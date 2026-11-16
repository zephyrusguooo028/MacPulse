using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using static Peekaboo.UIX;

namespace Peekaboo;

public sealed class TypingPage : PageView
{
    int _range = 7;
    TypingMonitor M => TypingMonitor.Shared;
    Settings S => Settings.Shared;

    public override void Shown() { M.Changed += Build; Build(); }
    public override void Hidden() => M.Changed -= Build;
    public override void Tick() => Build();

    void Build()
    {
        var toggle = Check(L("开启打字统计", "Enable typing stats"), S.TypingEnabled, on => { S.TypingEnabled = on; S.Save(); M.Apply(); });
        var parts = new List<UIElement> { PageHeader(L("打字", "Typing"), L("只统计按了多少次、按的哪个键，从不记录你打了什么内容", "Counts only how many keys you press and which ones, never what you type"), toggle) };

        if (!S.TypingEnabled && M.Days.Count == 0)
        {
            var note = T(L("说明：统计按键需要全局键盘钩子。这项技术和键盘记录器一样，个别杀毒软件可能会提示风险。Peekaboo 只计数、不记录内容、不联网，代码全部开源可查。", "Note: counting keystrokes needs a global keyboard hook, the same technique keyloggers use, so some antivirus software may flag it. Peekaboo only counts, never records content, never goes online, and its code is fully open source."), 12, color: Theme.Secondary);
            note.TextWrapping = TextWrapping.Wrap; note.TextTrimming = TextTrimming.None;
            parts.Add(Card(L("打字统计", "Typing stats"), "", V(8, Empty("", L("还没开启", "Not enabled yet"), L("打开右上角的开关，就能看到每天敲了多少键、哪个时段最勤快、最常按的键和在哪些应用里打字最多。", "Turn on the switch at the top right to see how many keys you press each day, your busiest hours, your most-used keys and the apps you type in most.")), note)));
            Content = V(16, parts.ToArray());
            return;
        }

        var d = M.Today; var y = M.Day(1);
        double progress = (double)d.Total / Math.Max(S.TypingGoal, 1);
        double kpm = d.ActiveSeconds > 30 ? d.Total / (d.ActiveSeconds / 60) : 0;
        var today = Card(L("今天", "Today"), "", new DockPanel
        {
            Children =
            {
                WithDock(Ring(Math.Min(progress, 1), Theme.AccentColor, Fmt.Pct(Math.Min(progress, 9.99)), 84, 9, L("目标", "goal")), Dock.Left, new Thickness(0, 0, 16, 0)),
                V(4, T(d.Total.ToString("N0"), 34, FontWeights.Bold), T(L($"次按键 · 目标 {S.TypingGoal:N0}", $"keystrokes · goal {S.TypingGoal:N0}"), 12, color: Theme.Secondary),
                     T(y.Total > 0 ? L($"昨天 {y.Total:N0}（{(d.Total >= y.Total ? "已超过" : $"还差 {y.Total - d.Total:N0}")}）", $"Yesterday {y.Total:N0} ({(d.Total >= y.Total ? "beaten" : $"{y.Total - d.Total:N0} to go")})") : L("昨天没有记录", "No record yesterday"), 12, color: Theme.Secondary)),
            },
        });
        var rhythm = Card(L("节奏", "Rhythm"), "", V(8,
            Row(T(L("打字时长", "Typing time"), 12.5, color: Theme.Secondary), T(Minutes(d.ActiveSeconds), 12.5)),
            Row(T(L("最长连续", "Longest streak"), 12.5, color: Theme.Secondary), T(Minutes(d.LongestStreak), 12.5)),
            Row(T(L("平均速度", "Average speed"), 12.5, color: Theme.Secondary), T(kpm > 0 ? L($"{kpm:0} 键/分钟", $"{kpm:0} keys/min") : "—", 12.5)),
            Row(T(L("状态", "Status"), 12.5, color: Theme.Secondary), T(M.Running ? L("统计中", "Counting") : L("已暂停", "Paused"), 12.5, color: M.Running ? Theme.B(Theme.DiskC) : Theme.Secondary))));
        rhythm.Width = 300;
        parts.Add(SideBySide(today, rhythm));

        var hourly = Card(L("今天每小时", "Today by hour"), "", Bars(d.Hours.Select(h => (double)h).ToList(), Theme.AccentColor, 160, i => i % 6 == 0 ? L($"{i}点", $"{i}h") : ""));
        var range = H(2);
        foreach (var r in new[] { 7, 30 })
        {
            var b = Btn(L($"{r} 天", $"{r} days"), () => { _range = r; Build(); }, primary: _range == r); b.Padding = new Thickness(10, 2, 10, 2);
            range.Children.Add(b);
        }
        var daysVals = Enumerable.Range(0, _range).Reverse().Select(o => (double)M.Day(o).Total).ToList();
        var daily = Card(L("每天", "Daily"), "", Bars(daysVals, Theme.MemC, 160, i => (_range == 7 || i % 5 == 0) ? DateTime.Now.AddDays(-(_range - 1 - i)).ToString("M/d") : "", S.TypingGoal), range);
        parts.Add(Halves(hourly, daily));

        parts.Add(Card(L("键盘热力图（今天）", "Keyboard heatmap (today)"), "", Heatmap(d)));

        var topKeys = V(6);
        var keys = d.Keys.OrderByDescending(k => k.Value).Take(8).ToList();
        if (keys.Count == 0) topKeys.Children.Add(T(L("今天还没有数据", "No data yet today"), 12.5, color: Theme.Secondary));
        foreach (var (k, v) in keys)
            topKeys.Children.Add(BarRow(KeyboardLayout.Labels.GetValueOrDefault(int.Parse(k), L($"键 {k}", $"Key {k}")), (double)v / keys[0].Value, v.ToString("N0"), Theme.AccentColor));
        var topApps = V(6);
        var apps = d.Apps.OrderByDescending(a => a.Value).Take(8).ToList();
        if (apps.Count == 0) topApps.Children.Add(T(L("今天还没有数据", "No data yet today"), 12.5, color: Theme.Secondary));
        foreach (var (a, v) in apps)
            topApps.Children.Add(BarRow(a == "未知" ? L("未知", "Unknown") : a, (double)v / Math.Max(d.Total, 1), Fmt.Pct((double)v / Math.Max(d.Total, 1)), Theme.MemC));
        parts.Add(Halves(Card(L("最常按的键", "Most-used keys"), "", topKeys), Card(L("在哪里打字最多", "Where you type most"), "", topApps)));

        var goalStepper = H(6, T(L("每日目标", "Daily goal"), 13),
            Btn("", () => { S.TypingGoal = Math.Max(1000, S.TypingGoal - 1000); S.Save(); Build(); }, glyph: ""),
            T(S.TypingGoal.ToString("N0"), 13, FontWeights.SemiBold),
            Btn("", () => { S.TypingGoal = Math.Min(100000, S.TypingGoal + 1000); S.Save(); Build(); }, glyph: ""));
        parts.Add(Row(goalStepper, Btn(L("清空全部打字数据…", "Clear all typing data…"), () => { if (Confirm(L("清空打字数据？", "Clear typing data?"), L("会删除所有已记录的打字统计，不能恢复。", "This deletes all recorded typing stats. It can’t be undone."))) M.ClearAll(); })));
        Content = V(16, parts.ToArray());
    }

    static string Minutes(double s) { int m = (int)(s / 60); return m >= 60 ? L($"{m / 60} 小时 {m % 60} 分", $"{m / 60} h {m % 60} min") : L($"{m} 分钟", $"{m} min"); }

    static UIElement WithDock(FrameworkElement e, Dock d, Thickness margin) { DockPanel.SetDock(e, d); e.Margin = margin; e.VerticalAlignment = VerticalAlignment.Center; return e; }

    static FrameworkElement SideBySide(FrameworkElement main, FrameworkElement side)
    {
        var p = new DockPanel();
        DockPanel.SetDock(side, Dock.Right); side.Margin = new Thickness(14, 0, 0, 0);
        p.Children.Add(side); p.Children.Add(main);
        return p;
    }

    static FrameworkElement Halves(FrameworkElement a, FrameworkElement b)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition()); g.ColumnDefinitions.Add(new ColumnDefinition());
        a.Margin = new Thickness(0, 0, 7, 0); b.Margin = new Thickness(7, 0, 0, 0);
        Grid.SetColumn(b, 1);
        g.Children.Add(a); g.Children.Add(b);
        return g;
    }

    static FrameworkElement BarRow(string label, double fraction, string value, Color c)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        var l = T(label, 12.5); var bar = Bar(fraction, c, 6); bar.VerticalAlignment = VerticalAlignment.Center;
        var v = T(value, 12.5); v.TextAlignment = TextAlignment.Right;
        Grid.SetColumn(bar, 1); Grid.SetColumn(v, 2);
        g.Children.Add(l); g.Children.Add(bar); g.Children.Add(v);
        return g;
    }

    /// Simple vertical bar chart with optional dashed goal line.
    static FrameworkElement Bars(IList<double> values, Color c, double height, Func<int, string> label, double? goal = null)
    {
        var canvas = new Canvas { Height = height + 18 };
        void Draw(double w)
        {
            canvas.Children.Clear();
            if (w <= 0 || values.Count == 0) return;
            double max = Math.Max(Math.Max(values.Max(), goal ?? 0), 1);
            double slot = w / values.Count, bw = Math.Max(2, slot * 0.7);
            for (int i = 0; i < values.Count; i++)
            {
                double h = height * values[i] / max;
                var r = new Border { Width = bw, Height = Math.Max(values[i] > 0 ? 2 : 0, h), CornerRadius = new CornerRadius(3, 3, 0, 0), Background = Theme.B(c) };
                Canvas.SetLeft(r, i * slot + (slot - bw) / 2); Canvas.SetTop(r, height - r.Height);
                canvas.Children.Add(r);
                var lb = label(i);
                if (lb.Length > 0) { var t = T(lb, 10, color: Theme.Tertiary); Canvas.SetLeft(t, i * slot); Canvas.SetTop(t, height + 3); canvas.Children.Add(t); }
            }
            if (goal is { } g && g > 0)
            {
                double y = height - height * g / max;
                canvas.Children.Add(new System.Windows.Shapes.Line { X1 = 0, X2 = w, Y1 = y, Y2 = y, Stroke = Theme.Secondary, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 4, 3 } });
            }
        }
        canvas.SizeChanged += (_, e) => Draw(e.NewSize.Width);
        return canvas;
    }

    static FrameworkElement Heatmap(TypingDay d)
    {
        int max = Math.Max(1, d.Keys.Values.DefaultIfEmpty(1).Max());
        var root = new StackPanel();
        void Draw(double w)
        {
            root.Children.Clear();
            double unit = (w - 14 * 4) / 15.5;
            foreach (var row in KeyboardLayout.Rows)
            {
                var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
                foreach (var k in row)
                {
                    int n = d.Keys.GetValueOrDefault(k.Vk.ToString());
                    double heat = n == 0 ? 0 : 0.15 + 0.85 * Math.Sqrt((double)n / max);
                    var c = Theme.AccentColor;
                    var label = V(0, T(k.Label, 12, FontWeights.SemiBold, heat > 0.55 ? Brushes.White : Theme.Text));
                    if (n > 0) label.Children.Add(T(n.ToString(), 9.5, color: heat > 0.55 ? Brushes.White : Theme.Secondary));
                    foreach (FrameworkElement ch in label.Children) ch.HorizontalAlignment = HorizontalAlignment.Center;
                    label.VerticalAlignment = VerticalAlignment.Center;
                    line.Children.Add(new Border
                    {
                        Width = Math.Max(10, unit * k.Width + 4 * (k.Width - 1)), Height = Math.Max(10, unit * 0.82), Margin = new Thickness(0, 0, 4, 0),
                        CornerRadius = new CornerRadius(6), Background = heat > 0 ? Theme.B(c, heat) : Theme.CardAlt, Child = label,
                        ToolTip = L($"{k.Label}：{n} 次", $"{k.Label}: {n} times"),
                    });
                }
                root.Children.Add(line);
            }
        }
        root.SizeChanged += (_, e) => { if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 1) Draw(e.NewSize.Width); };
        root.Loaded += (_, _) => Draw(root.ActualWidth);
        return root;
    }
}
