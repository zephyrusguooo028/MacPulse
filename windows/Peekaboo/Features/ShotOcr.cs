using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using static Peekaboo.UIX;

namespace Peekaboo;

/// Text recognition for the screenshot toolbar's "OCR" button, with the OCR engine
/// built into Windows (Windows.Media.Ocr). Runs on-device; copies the result right away.
public static class ShotOcr
{
    public static async void Run(BitmapSource image)
    {
        var win = new OcrWindow();
        win.Show();
        win.Activate();
        try
        {
            var engine = OcrEngine.TryCreateFromUserProfileLanguages()
                         ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"));
            if (engine is null) { win.ShowNoLanguage(); return; }

            // WPF bitmap → PNG bytes (UI thread) → SoftwareBitmap → OCR (WinRT async, off the UI thread).
            if (!image.IsFrozen && image.CanFreeze) image.Freeze();
            byte[] png;
            using (var ms = new MemoryStream())
            {
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
                enc.Save(ms);
                png = ms.ToArray();
            }
            // Windows OCR rejects images larger than OcrEngine.MaxImageDimension on either side.
            if (image.PixelWidth > OcrEngine.MaxImageDimension || image.PixelHeight > OcrEngine.MaxImageDimension)
            {
                win.ShowResult(null, L($"选区太大（最大 {OcrEngine.MaxImageDimension} 像素），请框选小一点的区域。", $"The selection is too large (max {OcrEngine.MaxImageDimension} px). Please select a smaller area."));
                return;
            }

            string text = await Task.Run(async () =>
            {
                using var stream = new InMemoryRandomAccessStream();
                await stream.WriteAsync(png.AsBuffer());
                stream.Seek(0);
                var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
                using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                var result = await engine.RecognizeAsync(bitmap);
                // Chinese lines come back with spaces between characters; drop those.
                bool cjk = engine.RecognizerLanguage.LanguageTag.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                           || engine.RecognizerLanguage.LanguageTag.StartsWith("ja", StringComparison.OrdinalIgnoreCase);
                return string.Join("\n", result.Lines.Select(l => cjk ? JoinCjk(l) : l.Text));
            });
            win.ShowResult(text.Trim(), null);
        }
        catch (Exception e)
        {
            win.ShowResult(null, L("识别失败：", "Recognition failed: ") + e.Message);
        }
    }

    /// Joins words, keeping a space only between two Latin/digit words.
    static string JoinCjk(OcrLine line)
    {
        var sb = new System.Text.StringBuilder();
        string? prev = null;
        foreach (var w in line.Words)
        {
            if (prev is not null && IsLatin(prev[^1]) && IsLatin(w.Text[0])) sb.Append(' ');
            sb.Append(w.Text);
            prev = w.Text;
        }
        return sb.ToString();
    }

    static bool IsLatin(char c) => c < 0x2E80;
}

/// Small floating window: editable text, Copy / Close.
sealed class OcrWindow : Window
{
    readonly ContentControl _body = new();
    readonly ContentControl _footer = new();
    readonly TextBox _text = new()
    {
        AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        FontSize = 13.5, Padding = new Thickness(6), Background = Theme.Card, Foreground = Theme.Text, CaretBrush = Theme.Text,
        BorderBrush = Theme.Line, BorderThickness = new Thickness(1),
    };

    public OcrWindow()
    {
        Title = L("识别到的文字", "Recognized text");
        Width = 480; Height = 360; MinWidth = 340; MinHeight = 220;
        Topmost = true; ShowInTaskbar = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Theme.Window;
        FontFamily = Theme.Font;
        SourceInitialized += (_, _) => Native.RoundCorners(this);
        KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };

        var spinner = T(L("正在识别文字…", "Recognizing text…"), 13, color: Theme.Secondary);
        spinner.HorizontalAlignment = HorizontalAlignment.Center; spinner.VerticalAlignment = VerticalAlignment.Center;
        _body.Content = spinner;
        _footer.Content = Row(T(""), Btn(L("关闭", "Close"), Close));

        var root = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(_footer, Dock.Bottom); _footer.Margin = new Thickness(0, 10, 0, 0);
        root.Children.Add(_footer);
        root.Children.Add(_body);
        Content = root;
    }

    public void ShowResult(string? text, string? error)
    {
        if (!string.IsNullOrEmpty(text))
        {
            _text.Text = text;
            _body.Content = _text;
            var status = T("", 12, color: Theme.Secondary);
            void Copy()
            {
                try { Clipboard.SetText(_text.Text); status.Text = L("✓ 已复制到剪贴板", "✓ Copied to the clipboard"); status.Foreground = Theme.B(Theme.DiskC); }
                catch { status.Text = L("没能复制，剪贴板被别的程序占用了，再点一次「复制」试试。", "Couldn’t copy: the clipboard is busy. Click Copy again."); status.Foreground = Theme.B(Theme.WarnC); }
            }
            _text.TextChanged += (_, _) => { status.Text = L("改过的文字点「复制」再拷贝一次", "Edited? Click Copy to copy it again"); status.Foreground = Theme.Secondary; };
            Copy();
            _footer.Content = Row(status, H(8, Btn(L("复制", "Copy"), Copy, primary: true), Btn(L("关闭", "Close"), Close)));
            _text.Focus();
            return;
        }
        var detail = T(error ?? L("试试框选更大、更清晰的文字区域。", "Try selecting a larger, clearer area of text."), 12.5, color: Theme.Secondary);
        detail.TextWrapping = TextWrapping.Wrap; detail.TextTrimming = TextTrimming.None; detail.TextAlignment = TextAlignment.Center;
        _body.Content = Empty("", error is null ? L("没有找到文字", "No text found") : L("出错了", "Something went wrong"), "");
        ((Panel)((FrameworkElement)_body.Content)).Children.Add(detail);
    }

    public void ShowNoLanguage()
    {
        var steps = T(L("Windows 的文字识别需要至少安装一种带「光学字符识别」的语言包。\n\n打开 设置 › 时间和语言 › 语言和区域，点「添加语言」，选择中文（简体）或英语，安装时勾选「光学字符识别」（或在已安装语言的「语言选项」里添加），然后再试一次。",
                        "Windows text recognition needs at least one language pack with “Optical character recognition”.\n\nOpen Settings › Time & language › Language & region, click “Add a language”, choose English or Chinese (Simplified) and include “Optical character recognition” (or add it under an installed language’s Language options), then try again."), 13);
        steps.TextWrapping = TextWrapping.Wrap; steps.TextTrimming = TextTrimming.None;
        var open = Btn(L("打开语言设置", "Open language settings"), () =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:regionlanguage") { UseShellExecute = true }); } catch { }
        }, primary: true);
        _body.Content = V(12, T(L("没有可用的文字识别语言", "No text recognition language installed"), 15, FontWeights.SemiBold), steps);
        _footer.Content = Row(T(""), H(8, open, Btn(L("关闭", "Close"), Close)));
    }
}
