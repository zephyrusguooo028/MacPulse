using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Peekaboo;

public enum ClipKind { Text, Link, Image, File }

public sealed class ClipItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ClipKind Kind { get; set; }
    public string? Text { get; set; }
    /// PNG file name inside the clipboard folder ("demo:N" in demo mode).
    public string? Image { get; set; }
    public int ImageWidth { get; set; }
    public int ImageHeight { get; set; }
    public List<string>? Paths { get; set; }
    public string? Source { get; set; }
    public DateTime Date { get; set; } = DateTime.Now;
    public bool Pinned { get; set; }
    /// De-duplication key.
    public string Key { get; set; } = "";

    [JsonIgnore]
    public string Preview => Kind switch
    {
        ClipKind.Text or ClipKind.Link => Flatten(Text ?? ""),
        ClipKind.Image => ImageWidth > 0 ? L($"图片 {ImageWidth} × {ImageHeight}", $"Image {ImageWidth} × {ImageHeight}") : L("图片", "Image"),
        _ => (Paths ?? new()) is { Count: > 1 } p ? L($"{System.IO.Path.GetFileName(p[0])} 等 {p.Count} 个文件", $"{System.IO.Path.GetFileName(p[0])} and {p.Count - 1} more")
                                                   : System.IO.Path.GetFileName(Paths?.FirstOrDefault() ?? ""),
    };

    static string Flatten(string s)
    {
        s = s.Trim();
        if (s.Length > 400) s = s[..400];
        return s.Replace("\r\n", " ⏎ ").Replace("\n", " ⏎ ").Replace("\t", "  ");
    }

    public bool Matches(string q)
    {
        if (q.Length == 0) return true;
        bool Has(string? s) => s is not null && s.Contains(q, StringComparison.CurrentCultureIgnoreCase);
        return Has(Text) || Has(Source) || (Paths?.Any(Has) ?? false) || Has(Preview);
    }

    public static string KindTitle(ClipKind k) => k switch
    {
        ClipKind.Text => L("文本", "Text"),
        ClipKind.Link => L("链接", "Links"),
        ClipKind.Image => L("图片", "Images"),
        _ => L("文件", "Files"),
    };

    public static string KindGlyph(ClipKind k) => k switch
    {
        ClipKind.Text => "",
        ClipKind.Link => "",
        ClipKind.Image => "",
        _ => "",
    };
}

/// Clipboard settings, owned by this feature (clipboard-settings.json in the app data folder).
public sealed class ClipboardSettings
{
    public bool Enabled { get; set; } = true;
    public int MaxItems { get; set; } = 200;
    public bool AutoPaste { get; set; }
    /// null = no shortcut.
    public HotkeyData? Hotkey { get; set; } = DefaultHotkey;

    public static HotkeyData DefaultHotkey => new() { Vk = 0x56, Mods = HotkeyData.CTRL | HotkeyData.SHIFT, Label = "V" };

    static string FilePath => System.IO.Path.Combine(Peekaboo.Paths.Support, "clipboard-settings.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static ClipboardSettings Load()
    {
        try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<ClipboardSettings>(File.ReadAllText(FilePath), Json) ?? new(); }
        catch { }
        return new();
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json)); } catch { }
    }
}

/// Clipboard history. Listens with AddClipboardFormatListener on a hidden message-only
/// window, so everything runs on the WPF dispatcher thread. Nothing leaves the PC.
public static class ClipboardService
{
    public static ClipboardSettings S { get; private set; } = new();
    public static readonly List<ClipItem> Items = new();
    /// Raised on the UI thread whenever the history or settings change.
    public static event Action? Changed;
    public static bool HotkeyOK { get; private set; } = true;
    public static bool Demo { get; } = Environment.GetCommandLineArgs().Contains("--demo");

    const string InternalFormat = "Peekaboo.Internal";
    const int WM_CLIPBOARDUPDATE = 0x031D, WM_HOTKEY = 0x0312, HotkeyId = 0xB01;
    const long MaxImageBytes = 40L << 20;
    const int MaxTextChars = 1_000_000;

    static HwndSource? _src;
    static bool _listening, _loaded, _pending;
    static Task _saveChain = Task.CompletedTask;
    static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    public static string Folder
    {
        get { var d = System.IO.Path.Combine(Paths.Support, "clipboard"); Directory.CreateDirectory(d); return d; }
    }
    static string IndexPath => System.IO.Path.Combine(Folder, "index.json");

    [DllImport("user32.dll", SetLastError = true)] static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    /// Called once at startup (UI thread).
    public static void Start()
    {
        S = ClipboardSettings.Load();
        Load();
        if (_src is null)
        {
            // Hidden window that receives clipboard and hotkey messages; never shown.
            _src = new HwndSource(new HwndSourceParameters("PeekabooClipboard") { Width = 0, Height = 0, WindowStyle = 0 });
            _src.AddHook(WndProc);
            if (Application.Current is { } app) app.Exit += (_, _) => Stop();
        }
        Apply();
    }

    /// Re-applies the enabled state and the hotkey after a settings change.
    public static void Apply()
    {
        if (_src is null) return;
        bool want = S.Enabled && !Demo;
        if (want && !_listening) _listening = AddClipboardFormatListener(_src.Handle);
        else if (!want && _listening) { RemoveClipboardFormatListener(_src.Handle); _listening = false; }
        ApplyHotkey();
        Changed?.Invoke();
    }

    public static void ApplyHotkey()
    {
        if (_src is null) return;
        UnregisterHotKey(_src.Handle, HotkeyId);
        HotkeyOK = true;
        // Demo runs sit beside the real app; don't steal its shortcut.
        if (S.Enabled && !Demo && S.Hotkey is { } k)
            HotkeyOK = RegisterHotKey(_src.Handle, HotkeyId, k.Mods | 0x4000 /* MOD_NOREPEAT */, (uint)k.Vk);
    }

    /// While recording a new shortcut, so the old one doesn't fire.
    public static void SuspendHotkey() { if (_src is not null) UnregisterHotKey(_src.Handle, HotkeyId); }

    static void Stop()
    {
        if (_src is null) return;
        if (_listening) RemoveClipboardFormatListener(_src.Handle);
        _listening = false;
        UnregisterHotKey(_src.Handle, HotkeyId);
        _src.RemoveHook(WndProc);
        _src.Dispose();
        _src = null;
    }

    static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE)
        {
            handled = true;
            // Read after the message returns; the owner may still hold the clipboard open.
            if (!_pending) { _pending = true; _ = CaptureSoon(); }
        }
        else if (msg == WM_HOTKEY && w.ToInt32() == HotkeyId)
        {
            handled = true;
            ClipboardPicker.Toggle();
        }
        return IntPtr.Zero;
    }

    // ---- capture

    static async Task CaptureSoon()
    {
        await Task.Delay(60);
        string? source = SourceApp();
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try { _pending = false; Capture(source); return; }
            catch (COMException) { await Task.Delay(100); }          // clipboard busy (CLIPBRD_E_CANT_OPEN)
            catch (ExternalException) { await Task.Delay(100); }
            catch (Exception) { return; }
        }
        _pending = false;
    }

    static void Capture(string? source)
    {
        if (!S.Enabled || Demo) return;
        var data = Clipboard.GetDataObject();
        if (data is null) return;
        if (data.GetDataPresent(InternalFormat)) return;
        // Password managers and other apps ask clipboard tools to skip their content.
        if (data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing") || data.GetDataPresent("Clipboard Viewer Ignore")) return;
        if (data.GetDataPresent("CanIncludeInClipboardHistory") && data.GetData("CanIncludeInClipboardHistory") is MemoryStream ms)
        {
            var b = ms.ToArray();
            if (b.Length >= 4 && BitConverter.ToInt32(b, 0) == 0) return;
        }

        // Files first: Explorer also puts file names as text.
        if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
        {
            Add(new ClipItem { Kind = ClipKind.File, Paths = files.ToList(), Source = source, Key = "f:" + string.Join("\n", files) });
            return;
        }
        if (data.GetDataPresent(DataFormats.UnicodeText) && data.GetData(DataFormats.UnicodeText) is string text && text.Trim().Length > 0)
        {
            if (text.Length > MaxTextChars) return;
            Add(new ClipItem { Kind = IsLink(text) ? ClipKind.Link : ClipKind.Text, Text = text, Source = source, Key = "t:" + text });
            return;
        }
        if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } img)
        {
            // The clipboard's InteropBitmap may not be freezable; a copy always is, and can be encoded off the UI thread.
            BitmapSource frozen = img.CanFreeze ? img : new WriteableBitmap(img);
            if (!frozen.IsFrozen) frozen.Freeze();
            var folder = Folder;
            Task.Run(() =>
            {
                byte[] png;
                using (var mem = new MemoryStream())
                {
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(frozen));
                    enc.Save(mem);
                    png = mem.ToArray();
                }
                if (png.Length > MaxImageBytes) return;
                var key = "i:" + Convert.ToHexString(SHA256.HashData(png));
                var name = Guid.NewGuid().ToString("N") + ".png";
                UiThread.Run(() =>
                {
                    var existing = Items.FindIndex(i => i.Key == key);
                    if (existing >= 0) { Bump(existing, source); return; }
                    try { File.WriteAllBytes(System.IO.Path.Combine(folder, name), png); } catch { return; }
                    Add(new ClipItem { Kind = ClipKind.Image, Image = name, ImageWidth = frozen.PixelWidth, ImageHeight = frozen.PixelHeight, Source = source, Key = key });
                });
            });
        }
    }

    public static bool IsLink(string s)
    {
        var t = s.Trim();
        if (t.Any(char.IsWhiteSpace)) return false;
        return Uri.TryCreate(t, UriKind.Absolute, out var u) && (u.Scheme is "http" or "https" or "ftp" || u.Scheme == "mailto");
    }

    static string? SourceApp()
    {
        try
        {
            var h = GetForegroundWindow();
            if (h == IntPtr.Zero) return null;
            GetWindowThreadProcessId(h, out uint pid);
            using var p = Process.GetProcessById((int)pid);
            try
            {
                var desc = p.MainModule?.FileVersionInfo.FileDescription;
                if (!string.IsNullOrWhiteSpace(desc)) return desc;
            }
            catch { }   // elevated or protected process
            return p.ProcessName;
        }
        catch { return null; }
    }

    static void Add(ClipItem item)
    {
        var i = Items.FindIndex(x => x.Key == item.Key);
        if (i >= 0) { Bump(i, item.Source); return; }
        Items.Insert(0, item);
        Evict();
        Save();
        Changed?.Invoke();
    }

    /// Re-copied: move to the top.
    static void Bump(int i, string? source)
    {
        var it = Items[i];
        Items.RemoveAt(i);
        it.Date = DateTime.Now;
        if (source is not null) it.Source = source;
        Items.Insert(0, it);
        Save();
        Changed?.Invoke();
    }

    static void Evict()
    {
        while (Items.Count > S.MaxItems)
        {
            int i = Items.FindLastIndex(x => !x.Pinned);
            if (i < 0) break;
            DeleteFile(Items[i]);
            Items.RemoveAt(i);
        }
    }

    // ---- persistence

    static void Load()
    {
        if (_loaded) return;
        _loaded = true;
        if (Demo) { Items.AddRange(ClipboardDemo.Items()); return; }
        try
        {
            if (File.Exists(IndexPath) && JsonSerializer.Deserialize<List<ClipItem>>(File.ReadAllText(IndexPath), Json) is { } list)
                Items.AddRange(list);
        }
        catch { }
    }

    static void Save()
    {
        if (Demo || !_loaded) return;
        var json = JsonSerializer.Serialize(Items, Json);
        var path = IndexPath;
        // Chained so writes land in order.
        _saveChain = _saveChain.ContinueWith(_ =>
        {
            try { File.WriteAllText(path + ".tmp", json); File.Move(path + ".tmp", path, true); } catch { }
        }, TaskScheduler.Default);
    }

    // ---- actions (UI thread)

    /// Puts an item back on the clipboard, marked as Peekaboo's own, and moves it to the top.
    public static bool Copy(ClipItem item)
    {
        try
        {
            var d = new DataObject();
            switch (item.Kind)
            {
                case ClipKind.Text or ClipKind.Link:
                    d.SetText(item.Text ?? "", TextDataFormat.UnicodeText);
                    break;
                case ClipKind.Image:
                    if (FullImage(item) is not { } img) return false;
                    d.SetImage(img);
                    break;
                default:
                    var sc = new System.Collections.Specialized.StringCollection();
                    sc.AddRange((item.Paths ?? new()).ToArray());
                    d.SetFileDropList(sc);
                    break;
            }
            d.SetData(InternalFormat, "1");
            Clipboard.SetDataObject(d, true);
        }
        catch { return false; }
        var i = Items.FindIndex(x => x.Id == item.Id);
        if (i >= 0) Bump(i, null);
        return true;
    }

    public static void TogglePin(ClipItem item) { item.Pinned = !item.Pinned; Save(); Changed?.Invoke(); }

    public static void Delete(ClipItem item)
    {
        DeleteFile(item);
        Items.Remove(item);
        Save();
        Changed?.Invoke();
    }

    /// Removes everything except pinned items, after confirmation.
    public static void ConfirmClear()
    {
        int n = Items.Count(i => !i.Pinned);
        if (n == 0) return;
        if (!UIX.Confirm(L("清空剪贴板历史？", "Clear clipboard history?"), L($"将删除 {n} 条记录，已固定的条目会保留。此操作无法撤销。", $"{n} items will be deleted. Pinned items are kept. This can’t be undone."))) return;
        foreach (var it in Items.Where(i => !i.Pinned)) DeleteFile(it);
        Items.RemoveAll(i => !i.Pinned);
        Save();
        Changed?.Invoke();
    }

    public static void SetMaxItems(int n) { S.MaxItems = n; S.Save(); Evict(); Save(); Changed?.Invoke(); }

    static void DeleteFile(ClipItem item)
    {
        if (item.Image is null || item.Image.StartsWith("demo:")) return;
        try { File.Delete(System.IO.Path.Combine(Folder, item.Image)); } catch { }
    }

    // ---- images

    public static BitmapSource? FullImage(ClipItem item) => LoadImage(item, 0);
    public static BitmapSource? Thumbnail(ClipItem item) => LoadImage(item, 96);

    static BitmapSource? LoadImage(ClipItem item, int decodeWidth)
    {
        if (item.Image is null) return null;
        if (item.Image.StartsWith("demo:")) return ClipboardDemo.Image(item.Image);
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;   // read now, don't keep the file locked
            bi.UriSource = new Uri(System.IO.Path.Combine(Folder, item.Image));
            if (decodeWidth > 0) bi.DecodePixelWidth = decodeWidth;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch { return null; }
    }

    // ---- auto-paste

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public InputUnion u; }
    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion { [FieldOffset(0)] public KEYBDINPUT ki; [FieldOffset(0)] public MOUSEINPUT mi; }
    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);

    /// Brings `target` back to the front and sends Ctrl+V to it.
    public static async void PasteInto(IntPtr target)
    {
        if (target != IntPtr.Zero) SetForegroundWindow(target);
        await Task.Delay(120);
        const ushort VK_CONTROL = 0x11, VK_V = 0x56, VK_SHIFT = 0x10;
        const uint KEYUP = 2;
        INPUT K(ushort vk, uint flags) => new() { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = flags } } };
        // Release Shift in case it is still held from Ctrl+Shift+V.
        var seq = new[] { K(VK_SHIFT, KEYUP), K(VK_CONTROL, 0), K(VK_V, 0), K(VK_V, KEYUP), K(VK_CONTROL, KEYUP) };
        SendInput((uint)seq.Length, seq, Marshal.SizeOf<INPUT>());
    }
}

/// Made-up items for `--demo`, so screenshots never contain anyone's clipboard.
static class ClipboardDemo
{
    public static List<ClipItem> Items()
    {
        DateTime Ago(double m) => DateTime.Now.AddMinutes(-m);
        return new()
        {
            new() { Kind = ClipKind.Text, Text = L("周五下午 3 点在 2 号会议室开产品评审会，记得带上新版原型。", "Product review on Friday at 3 pm in room 2. Please bring the new prototype."), Source = "Notepad", Date = Ago(1), Key = "t:d1" },
            new() { Kind = ClipKind.Link, Text = "https://github.com/peekaboo-app/peekaboo", Source = "Microsoft Edge", Date = Ago(4), Pinned = true, Key = "t:d2" },
            new() { Kind = ClipKind.Image, Image = "demo:0", ImageWidth = 1280, ImageHeight = 800, Source = "Peekaboo", Date = Ago(9), Key = "i:d3" },
            new() { Kind = ClipKind.Text, Text = "public static string Greet(string name) => $\"Hello, {name}!\";", Source = "Visual Studio Code", Date = Ago(18), Key = "t:d4" },
            new() { Kind = ClipKind.File, Paths = new() { Paths.H(@"Documents\Quarterly Report.pdf"), Paths.H(@"Documents\Budget 2026.xlsx") }, Source = "Windows Explorer", Date = Ago(35), Key = "f:d5" },
            new() { Kind = ClipKind.Text, Text = L("北京市海淀区中关村大街 1 号", "1 Microsoft Way, Redmond, WA"), Source = "Maps", Date = Ago(62), Pinned = true, Key = "t:d6" },
            new() { Kind = ClipKind.Image, Image = "demo:1", ImageWidth = 640, ImageHeight = 480, Source = "Photos", Date = Ago(130), Key = "i:d7" },
            new() { Kind = ClipKind.Link, Text = "https://learn.microsoft.com/windows/apps", Source = "Microsoft Edge", Date = Ago(240), Key = "t:d8" },
            new() { Kind = ClipKind.Text, Text = "winget install Peekaboo", Source = "Windows Terminal", Date = Ago(60 * 26), Key = "t:d9" },
        };
    }

    static readonly Dictionary<string, BitmapSource> Cache = new();

    public static BitmapSource Image(string name)
    {
        if (Cache.TryGetValue(name, out var c)) return c;
        bool second = name == "demo:1";
        int w = 320, h = second ? 240 : 200;
        var v = new System.Windows.Media.DrawingVisual();
        using (var dc = v.RenderOpen())
        {
            var a = second ? System.Windows.Media.Color.FromRgb(255, 149, 0) : System.Windows.Media.Color.FromRgb(0, 122, 255);
            var b = second ? System.Windows.Media.Color.FromRgb(255, 45, 85) : System.Windows.Media.Color.FromRgb(90, 200, 250);
            dc.DrawRectangle(new System.Windows.Media.LinearGradientBrush(a, b, 35), null, new Rect(0, 0, w, h));
            var white = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(220, 255, 255, 255));
            if (second) dc.DrawEllipse(white, null, new Point(160, 120), 50, 50);
            else
            {
                dc.DrawRoundedRectangle(white, null, new Rect(30, 30, 260, 140), 10, 10);
                var line = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(150, 0, 122, 255));
                for (int k = 0; k < 4; k++) dc.DrawRectangle(line, null, new Rect(50, 50 + k * 26, 200 - k * 30, 10));
            }
        }
        var bmp = new RenderTargetBitmap(w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bmp.Render(v);
        bmp.Freeze();
        Cache[name] = bmp;
        return bmp;
    }
}
