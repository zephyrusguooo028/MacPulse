using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using static Peekaboo.UIX;

namespace Peekaboo;

public sealed class ProcessesPage : PageView
{
    bool _grouped = true, _byMemory;
    string _query = "";
    readonly TextBox _search = new() { Width = 180, Padding = new Thickness(6, 4, 6, 4), VerticalContentAlignment = VerticalAlignment.Center };
    readonly ContentControl _table = new();
    readonly ContentControl _top = new();
    readonly TextBlock _subtitle = T("", 13, color: Theme.Secondary);

    public ProcessesPage()
    {
        _search.TextChanged += (_, _) => { _query = _search.Text; Fill(); };
        Content = V(16,
            Row(V(2, T("进程", 26, FontWeights.Bold), _subtitle), H(8, _search, Toggle("按应用", "全部进程", () => _grouped, v => _grouped = v), Toggle("CPU", "内存", () => !_byMemory, v => _byMemory = !v))),
            _top,
            Panel(_table, 0),
            WrapNote("只显示当前用户能读取的进程。CPU 100% = 占满一个核心。「退出」会像点关闭按钮一样让应用自己保存后关闭。"));
    }

    FrameworkElement Toggle(string a, string b, Func<bool> get, Action<bool> set)
    {
        var s = H(2);
        void Paint()
        {
            s.Children.Clear();
            foreach (var (label, val) in new[] { (a, true), (b, false) })
            {
                var btn = Btn(label, () => { set(val); Paint(); Fill(); }, primary: get() == val);
                btn.Padding = new Thickness(10, 3, 10, 3);
                s.Children.Add(btn);
            }
        }
        Paint();
        return s;
    }

    static TextBlock WrapNote(string t) { var x = T(t, 12, color: Theme.Secondary); x.TextWrapping = TextWrapping.Wrap; return x; }

    public override void Shown() { SystemMonitor.Shared.WantProcesses(true); Fill(); }
    public override void Hidden() => SystemMonitor.Shared.WantProcesses(false);
    public override void Tick() => Fill();

    void Fill()
    {
        var m = SystemMonitor.Shared;
        _subtitle.Text = $"{m.Processes.Count} 个进程 · CPU {Fmt.Pct(m.Cpu.Total)} · 内存 {Fmt.Bytes(m.Memory.Used)} / {Fmt.Bytes(m.Memory.Total)}";
        _top.Content = Card("CPU", "", Spark(m.Samples.Select(s => s.Cpu).ToList(), Theme.CpuC, 40));

        IEnumerable<AppGroup> rows = _grouped ? m.Groups : m.Processes.Select(p => new AppGroup { Id = "p" + p.Pid, Name = p.Name, Path = p.Path, Pids = new() { p.Pid }, Cpu = p.Cpu, Memory = p.Memory });
        if (_query.Length > 0) rows = rows.Where(g => g.Name.Contains(_query, StringComparison.OrdinalIgnoreCase));
        rows = _byMemory ? rows.OrderByDescending(g => g.Memory) : rows.OrderByDescending(g => g.Cpu).ThenByDescending(g => g.Memory);

        var grid = new Grid();
        foreach (var w in new[] { new GridLength(1, GridUnitType.Star), new GridLength(70), new GridLength(80), new GridLength(100), new GridLength(130) })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        int r = 0;
        void Cell(UIElement e, int col, bool right = false)
        {
            if (e is FrameworkElement fe) { fe.Margin = new Thickness(12, 6, 12, 6); fe.VerticalAlignment = VerticalAlignment.Center; if (right) fe.HorizontalAlignment = HorizontalAlignment.Right; }
            Grid.SetRow(e, r); Grid.SetColumn(e, col); grid.Children.Add(e);
        }
        grid.RowDefinitions.Add(new RowDefinition());
        Cell(T("名称", 12, FontWeights.SemiBold, Theme.Secondary), 0);
        Cell(T(_grouped ? "进程数" : "PID", 12, FontWeights.SemiBold, Theme.Secondary), 1, true);
        Cell(T("CPU", 12, FontWeights.SemiBold, Theme.Secondary), 2, true);
        Cell(T("内存", 12, FontWeights.SemiBold, Theme.Secondary), 3, true);
        foreach (var g in rows.Take(120))
        {
            r++;
            grid.RowDefinitions.Add(new RowDefinition());
            var line = new Border { BorderBrush = Theme.Line, BorderThickness = new Thickness(0, 1, 0, 0) };
            Grid.SetRow(line, r); Grid.SetColumnSpan(line, 5); grid.Children.Add(line);
            Cell(H(8, PopupWindow.AppIcon(g.Path, 18), T(g.Name, 13)), 0);
            Cell(T(_grouped ? g.Pids.Count.ToString() : g.Pids[0].ToString(), 12.5, color: Theme.Secondary), 1, true);
            Cell(T((g.Cpu * 100).ToString("0.0") + "%", 12.5, color: g.Cpu > 0.8 ? Theme.B(Theme.DangerC) : g.Cpu > 0.3 ? Theme.B(Theme.WarnC) : Theme.Text), 2, true);
            Cell(T(Fmt.Bytes(g.Memory), 12.5), 3, true);
            var grp = g;
            var quit = Btn("退出", () => Quit(grp, false)); quit.Padding = new Thickness(10, 2, 10, 2);
            var kill = Btn("强制", () => Quit(grp, true)); kill.Padding = new Thickness(10, 2, 10, 2);
            Cell(H(6, quit, kill), 4, true);
        }
        _table.Content = grid;
    }

    void Quit(AppGroup g, bool force)
    {
        if (!Confirm(force ? $"强制结束「{g.Name}」？" : $"关闭「{g.Name}」？", force ? "强制结束不会保存未保存的内容。" : "应用会自己处理保存，和点关闭按钮一样。")) return;
        if (!ProcessReader.Quit(g, force)) Info("没能关闭", force ? "可能没有权限，或者它是系统进程。" : "这个进程没有窗口，可以试试「强制」。");
        SystemMonitor.Shared.RefreshProcesses();
        Fill();
    }
}
