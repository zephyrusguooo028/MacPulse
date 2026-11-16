using SD = System.Drawing;
using SD2 = System.Drawing.Drawing2D;

namespace Peekaboo;

/// The same icon set as the macOS build: pixel sprites plus three geometric icons.
public static class IconArt
{
    public static string Title(TrayIcon i) => i switch
    {
        TrayIcon.None => "不显示", TrayIcon.Slime => "史莱姆", TrayIcon.Ghost => "小幽灵", TrayIcon.Chick => "小鸡",
        TrayIcon.Whale => "小鲸鱼", TrayIcon.Rocket => "火箭", TrayIcon.Heart => "心跳", TrayIcon.Pulse => "脉搏线",
        TrayIcon.Fan => "风扇", _ => "均衡器",
    };

    public static int Frames(TrayIcon i) => i switch
    {
        TrayIcon.None => 1, TrayIcon.Slime => 6, TrayIcon.Ghost or TrayIcon.Chick or TrayIcon.Rocket => 4,
        TrayIcon.Whale => 8, TrayIcon.Heart => 6, TrayIcon.Pulse => 12, _ => 8,
    };

    // ---------------- sprites (rows top→bottom, '#' = pixel) ----------------

    record Sprite(string[] Rows, int Dy);

    static Sprite Slime(int f)
    {
        var shapes = new (int w, int h, int lift)[] { (14, 9, 0), (16, 7, 0), (14, 9, 0), (12, 11, 2), (12, 11, 4), (12, 11, 2) };
        var (w, h, lift) = shapes[f % shapes.Length];
        const int W = 18;
        var g = Enumerable.Range(0, 16).Select(_ => Enumerable.Repeat('.', W).ToArray()).ToArray();
        double cx = W / 2.0;
        for (int r = 0; r < h; r++)
        {
            double t = (double)r / h, half = w / 2.0 * Math.Sqrt(1 - Math.Pow(t, 2.4));
            int y = 15 - lift - r;
            for (int x = 0; x < W; x++) if (Math.Abs(x + 0.5 - cx) <= half) g[y][x] = '#';
        }
        int eyeRow = 15 - lift - (int)(h * 0.45), eyeH = f % shapes.Length == 1 ? 1 : 2;
        foreach (var x in new[] { (int)cx - 3, (int)cx + 2 })
            for (int k = 0; k < eyeH; k++) if (eyeRow - k >= 0) g[eyeRow - k][x] = '.';
        int sy = 15 - lift - h + 2, sx = (int)cx - w / 2 + 3;
        if (sy >= 0 && sx >= 0 && sx < W) g[sy][sx] = '.';
        return new Sprite(g.Select(r => new string(r)).ToArray(), 0);
    }

    static readonly string[] GhostBody =
    {
        ".....######.....", "...##########...", "..############..", ".##############.",
        ".####.####.####.", ".####.####.####.", ".##############.", "#######..#######",
        "################", ".##############.", ".##############.",
    };

    static Sprite Ghost(int f)
    {
        var body = (string[])GhostBody.Clone();
        if (f >= 2) { body[4] = ".#####.####.###."; body[5] = ".#####.####.###."; }
        var tail = f % 2 == 0
            ? new[] { "..############..", "...#####..####..", "....##.....##..." }
            : new[] { "..############..", "..####..#####...", "...##.....##...." };
        return new Sprite(body.Concat(tail).ToArray(), new[] { 1, 0, 0, 1 }[f % 4] + 1);
    }

    static readonly string[] ChickBody =
    {
        "......####......", ".....######.....", ".....###.##.....", ".....#########..",
        ".....######.....", "...##########...", ".#############..", ".#############..",
        ".#####..######..", ".#############..", "..###########...", "....#######.....",
    };

    static Sprite Chick(int f)
    {
        var legs = new[]
        {
            new[] { "......#..#......", ".....##.##......" }, new[] { ".....#....#.....", "....##...##....." },
            new[] { "......#..#......", ".....##.##......" }, new[] { ".......##.......", "......####......" },
        };
        return new Sprite(ChickBody.Concat(legs[f % 4]).ToArray(), new[] { 1, 0, 1, 0 }[f % 4] + 1);
    }

    static readonly string[] WhaleBody =
    {
        "..........#####...", "........#########.", "##.....##########.", ".##..######.#####.",
        "..###############.", "..###############.", ".##..###########..", "##.....#######....",
    };

    static Sprite Whale(int f)
    {
        const string e = "..................";
        string R(params int[] cols) { var c = e.ToCharArray(); foreach (var x in cols) c[x] = '#'; return new string(c); }
        var spouts = new[]
        {
            new[] { e, e, e, e, e }, new[] { e, e, e, e, R(12) }, new[] { e, e, e, R(12), R(12) },
            new[] { e, R(11, 13), R(12), R(12), R(12) }, new[] { R(10, 14), R(11, 13), R(12), e, e },
            new[] { R(9, 15), e, e, e, e }, new[] { e, e, e, e, e }, new[] { e, e, e, e, e },
        };
        return new Sprite(spouts[f % 8].Concat(WhaleBody).ToArray(), 1 + new[] { 0, 0, 0, 0, 1, 1, 1, 0 }[f % 8]);
    }

    static Sprite Rocket(int f)
    {
        var rows = new List<string>
        {
            "......##......", ".....####.....", "....######....", "....##..##....", "....##..##....", "....######....",
            "....######....", "...########...", "..##.####.##..", "..#..####..#..", ".....#..#.....",
        };
        var flames = new[]
        {
            new[] { ".....####.....", "......##......", ".............." }, new[] { ".....####.....", ".....####.....", "......##......" },
            new[] { "......##......", "......##......", ".............." }, new[] { ".....####.....", "......##......", "......#......." },
        };
        rows.AddRange(flames[f % 4]);
        var chars = rows.Select(r => r.ToCharArray()).ToArray();
        int y = (f * 3) % 14; if (y < chars.Length) chars[y][0] = '#';
        int y2 = (f * 3 + 7) % 14; if (y2 < chars.Length) chars[y2][13] = '#';
        return new Sprite(chars.Select(c => new string(c)).ToArray(), 1);
    }

    static readonly string[] HeartBig =
    {
        "..###...###..", ".#..##.#####.", "#.##########.", "#############", "#############", ".###########.",
        "..#########..", "...#######...", "....#####....", ".....###.....", "......#......",
    };
    static readonly string[] HeartSmall =
    {
        ".##...##.", "#..#.####", "#########", "#########", ".#######.", "..#####..", "...###...", "....#....",
    };

    static Sprite Heart(int f)
    {
        bool big = new[] { true, false, true, false, false, false }[f % 6];
        var pad = big ? "." : "...";
        return new Sprite((big ? HeartBig : HeartSmall).Select(r => pad + r + pad).ToArray(), big ? 3 : 5);
    }

    static Sprite? SpriteFor(TrayIcon icon, int f) => icon switch
    {
        TrayIcon.Slime => Slime(f), TrayIcon.Ghost => Ghost(f), TrayIcon.Chick => Chick(f),
        TrayIcon.Whale => Whale(f), TrayIcon.Rocket => Rocket(f), TrayIcon.Heart => Heart(f), _ => null,
    };

    // ---------------- rendering ----------------

    /// Renders a frame centred in a square bitmap of `px` pixels.
    public static SD.Bitmap Render(TrayIcon icon, int frame, double cpu, SD.Color color, int px = 32)
    {
        var bmp = new SD.Bitmap(px, px, SD.Imaging.PixelFormat.Format32bppArgb);
        using var g = SD.Graphics.FromImage(bmp);
        g.Clear(SD.Color.Transparent);
        frame %= Frames(icon);
        var sprite = SpriteFor(icon, frame);
        if (sprite is not null)
        {
            g.SmoothingMode = SD2.SmoothingMode.None;
            g.PixelOffsetMode = SD2.PixelOffsetMode.Half;
            int w = sprite.Rows.Max(r => r.Length);
            double s = px / 18.0;
            double ox = (px - w * s) / 2;
            using var b = new SD.SolidBrush(color);
            for (int r = 0; r < sprite.Rows.Length; r++)
                for (int c = 0; c < sprite.Rows[r].Length; c++)
                    if (sprite.Rows[r][c] == '#')
                    {
                        float x0 = (float)Math.Round(ox + c * s), y0 = (float)Math.Round((r + sprite.Dy) * s);
                        float x1 = (float)Math.Round(ox + (c + 1) * s), y1 = (float)Math.Round((r + sprite.Dy + 1) * s);
                        g.FillRectangle(b, x0, y0, x1 - x0, y1 - y0);
                    }
            return bmp;
        }
        g.SmoothingMode = SD2.SmoothingMode.AntiAlias;
        g.ScaleTransform(px / 18f, px / 18f);
        switch (icon)
        {
            case TrayIcon.Pulse: DrawPulse(g, frame, color); break;
            case TrayIcon.Fan: DrawFan(g, frame, color); break;
            case TrayIcon.Bars: DrawBars(g, frame, cpu, color); break;
        }
        return bmp;
    }

    static void DrawPulse(SD.Graphics g, int f, SD.Color color)
    {
        static double Y(double x)
        {
            double p = x % 18;
            if (p >= 6 && p < 7.5) return 9 - (p - 6) * 1.3;
            if (p >= 7.5 && p < 8.5) return 7 + (p - 7.5) * 2;
            if (p >= 9.5 && p < 10.3) return 9 + (p - 9.5) * 3;
            if (p >= 10.3 && p < 11.5) return 11.4 - (p - 10.3) * 8.5;
            if (p >= 11.5 && p < 12.7) return 1.2 + (p - 11.5) * 11;
            if (p >= 12.7 && p < 13.5) return 14.4 - (p - 12.7) * 6.7;
            return 9;
        }
        double shift = f * 1.5;
        var pts = Enumerable.Range(0, 73).Select(i => { double x = 0.5 + i / 72.0 * 16.5; return new SD.PointF((float)x, (float)Y(x + shift)); }).ToArray();
        using var pen = new SD.Pen(color, 1.5f) { LineJoin = SD2.LineJoin.Round, StartCap = SD2.LineCap.Round, EndCap = SD2.LineCap.Round };
        g.DrawLines(pen, pts);
        using var b = new SD.SolidBrush(color);
        var head = pts[^1];
        g.FillEllipse(b, head.X - 1.3f, head.Y - 1.3f, 2.6f, 2.6f);
    }

    static void DrawFan(SD.Graphics g, int f, SD.Color color)
    {
        using var pen = new SD.Pen(color, 1.4f);
        g.DrawEllipse(pen, 1, 1, 16, 16);
        using var b = new SD.SolidBrush(color);
        var c = new SD.PointF(9, 9);
        double baseA = f * 15 * Math.PI / 180;
        for (int k = 0; k < 3; k++)
        {
            double a = baseA + k * 2 * Math.PI / 3, side = a + Math.PI / 2;
            SD.PointF P(double dx, double dy) => new((float)(c.X + dx), (float)(c.Y + dy));
            var tip = P(Math.Cos(a) * 6.2, Math.Sin(a) * 6.2);
            using var path = new SD2.GraphicsPath();
            path.AddBezier(c, P(Math.Cos(a) * 2 + Math.Cos(side) * 3.6, Math.Sin(a) * 2 + Math.Sin(side) * 3.6),
                           new SD.PointF(tip.X + (float)(Math.Cos(side) * 2.4), tip.Y + (float)(Math.Sin(side) * 2.4)), tip);
            path.AddBezier(tip, new SD.PointF(tip.X - (float)(Math.Cos(side) * 0.6), tip.Y - (float)(Math.Sin(side) * 0.6)),
                           P(Math.Cos(a) * 2, Math.Sin(a) * 2), c);
            g.FillPath(b, path);
        }
        g.FillEllipse(b, c.X - 1.6f, c.Y - 1.6f, 3.2f, 3.2f);
    }

    static void DrawBars(SD.Graphics g, int f, double cpu, SD.Color color)
    {
        double amp = 0.3 + 0.7 * Math.Clamp(cpu, 0, 1);
        using var b = new SD.SolidBrush(color);
        for (int i = 0; i < 5; i++)
        {
            uint h = unchecked((uint)((f % 8) * 31 + i * 17) * 2654435761u);
            h ^= h >> 13;
            double r = h % 1000 / 1000.0, height = 3 + (0.35 + 0.65 * r) * amp * 12;
            float x = 1.5f + i * 3.1f;
            g.FillRectangle(b, x, (float)(16 - height), 2.2f, (float)height);
        }
    }

    /// A tray icon showing a number, with a coloured bar so metrics can be told apart.
    public static SD.Bitmap Number(string text, SD.Color textColor, SD.Color accent, int px = 32)
    {
        var bmp = new SD.Bitmap(px, px, SD.Imaging.PixelFormat.Format32bppArgb);
        using var g = SD.Graphics.FromImage(bmp);
        g.Clear(SD.Color.Transparent);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        float size = text.Length <= 2 ? px * 0.62f : text.Length == 3 ? px * 0.46f : px * 0.36f;
        using var font = new SD.Font("Segoe UI", size, SD.FontStyle.Bold, SD.GraphicsUnit.Pixel);
        using var tb = new SD.SolidBrush(textColor);
        var sz = g.MeasureString(text, font, px, SD.StringFormat.GenericTypographic);
        g.DrawString(text, font, tb, (px - sz.Width) / 2, (px * 0.82f - sz.Height) / 2, SD.StringFormat.GenericTypographic);
        using var ab = new SD.SolidBrush(accent);
        g.FillRectangle(ab, px * 0.12f, px * 0.84f, px * 0.76f, px * 0.12f);
        return bmp;
    }

    public static SD.Bitmap Graph(IList<double> history, SD.Color color, int px = 32)
    {
        var bmp = new SD.Bitmap(px, px, SD.Imaging.PixelFormat.Format32bppArgb);
        using var g = SD.Graphics.FromImage(bmp);
        g.Clear(SD.Color.Transparent);
        var vals = Enumerable.Repeat(0.0, 8).Concat(history).TakeLast(8).ToList();
        using var bg = new SD.SolidBrush(SD.Color.FromArgb(70, color));
        g.FillRectangle(bg, 1, 3, px - 2, px - 6);
        using var b = new SD.SolidBrush(color);
        float bw = (px - 4) / 8f;
        for (int i = 0; i < vals.Count; i++)
        {
            float h = Math.Max(1, (px - 8) * (float)Math.Clamp(vals[i], 0, 1));
            g.FillRectangle(b, 2 + i * bw + 0.5f, px - 4 - h, bw - 1, h);
        }
        return bmp;
    }

    /// For WPF previews.
    public static System.Windows.Media.Imaging.BitmapSource ToWpf(SD.Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, SD.Imaging.ImageFormat.Png);
        ms.Position = 0;
        var img = new System.Windows.Media.Imaging.BitmapImage();
        img.BeginInit();
        img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        img.StreamSource = ms;
        img.EndInit();
        img.Freeze();
        return img;
    }

    public static SD.Color Tint(double cpu) => cpu > 0.85 ? SD.Color.FromArgb(235, 71, 77) : cpu > 0.6 ? SD.Color.FromArgb(245, 158, 26) : SD.Color.FromArgb(33, 179, 140);
}
