using System.Windows;
using System.Windows.Controls;
using static Peekaboo.UIX;

namespace Peekaboo;

public sealed class ScreenshotPage : PageView
{
    Settings S => Settings.Shared;

    public override void Shown() => Build();

    void Build()
    {
        var start = Btn("开始截图", () => { MainWindow.Instance?.Hide(); Screenshot.Start(300); }, primary: true, glyph: "");

        var hk = new ComboBox { Width = 200 };
        foreach (var k in Enum.GetValues<ShotHotkey>())
        {
            var item = new ComboBoxItem { Content = GlobalHotkey.Title(k), Tag = k };
            hk.Items.Add(item);
            if (k == S.ShotHotkey) hk.SelectedItem = item;
        }
        var status = T("", 12, color: Theme.Secondary);
        hk.SelectionChanged += (_, _) =>
        {
            S.ShotHotkey = (ShotHotkey)((ComboBoxItem)hk.SelectedItem).Tag; S.Save();
            bool ok = GlobalHotkey.Apply(S.ShotHotkey, () => Screenshot.Start());
            status.Text = ok ? "" : "这个快捷键被别的程序占用了，换一个试试。";
        };
        var folder = Btn("更改…", () =>
        {
            var d = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = Settings.Expand(S.ShotFolder) };
            if (d.ShowDialog() == true) { S.ShotFolder = Fmt.Tilde(d.FolderName); S.Save(); Build(); }
        });

        TextBlock Tip(string t) { var x = T(t, 12.5, color: Theme.Secondary); x.TextWrapping = TextWrapping.Wrap; x.TextTrimming = TextTrimming.None; return x; }
        var closeAll = Btn("全部关闭", () => { foreach (var p in Screenshot.Pins.ToList()) p.Close(); Build(); });
        closeAll.IsEnabled = Screenshot.Pins.Count > 0;

        Content = V(16,
            PageHeader("截图", "框选截图、标注，然后复制、保存或贴在屏幕上", start),
            Card("快捷键与保存", "", V(10,
                H(8, T("截图快捷键", 13), hk, status),
                H(8, T("保存到", 13), T(S.ShotFolder, 13, color: Theme.Secondary), folder))),
            Card("怎么用", "", V(8,
                Tip("• 鼠标移到窗口上会自动高亮，单击直接截这个窗口；拖动可以自由框选。"),
                Tip("• 框选后可以拖动选区；选工具后在选区里画矩形、箭头、画笔、文字或马赛克。"),
                Tip("• 回车或双击：复制到剪贴板。Ctrl+Z 撤销，Esc 取消，右键重新框选。"),
                Tip("• 「贴到屏幕」会把截图钉在最上层：拖动移动、滚轮缩放、双击关闭、右键调透明度。"))),
            Card("贴图", "", Row(T($"当前屏幕上有 {Screenshot.Pins.Count} 张贴图", 13), closeAll)));
    }
}
