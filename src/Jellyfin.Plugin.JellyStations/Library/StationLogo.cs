using System.Globalization;
using System.Text;
using Jellyfin.Plugin.JellyStations.Models;
using SkiaSharp;

namespace Jellyfin.Plugin.JellyStations.Library;

/// <summary>
/// The generated station logo, drawn server side: an 800 x 450 card with the name (up to two lines) and the
/// channel number on a patterned background seeded from the station. The same hash, random source, colors and
/// six patterns as the editor's canvas (jellystations.js), so a card drawn here and one drawn in the browser
/// match for the same seed apart from the font. Suggested stations get theirs from here.
/// </summary>
public static class StationLogo
{
    public const int Width = 800;
    public const int Height = 450;

    /// <summary>The seed the editor uses: name, number, everything the station plays, and the "Another look" counter.</summary>
    public static string SeedText(Station s, int look = 0)
    {
        var parts = new List<string> { s.Name ?? string.Empty, s.ChannelNumber ?? string.Empty };
        void Add(IEnumerable<ContentSource>? sources)
        {
            foreach (var src in sources ?? Enumerable.Empty<ContentSource>())
            {
                parts.Add(FirstOf(src.DisplayName, src.Genre, src.Tag, src.ItemId?.ToString("N"), src.Kind.ToString()));
            }
        }

        Add(s.Pool?.Sources);
        foreach (var block in s.Slots?.Blocks ?? new List<SlotBlock>())
        {
            Add(block.Sources);
        }

        Add(s.Slots?.Filler?.Sources);
        foreach (var item in s.Lineup?.Items ?? new List<LineupItem>())
        {
            parts.Add(FirstOf(item.DisplayName, item.ItemId.ToString("N")));
        }

        parts.Add("look" + look.ToString(CultureInfo.InvariantCulture));
        return string.Join("|", parts).ToLowerInvariant();
    }

    /// <summary>The editor's default color for a name: a hue from a simple hash, mid-saturated, dark enough for white text.</summary>
    public static string ColorForName(string name)
    {
        uint h = 0;
        foreach (var c in name ?? string.Empty)
        {
            h = unchecked(h * 31 + c);
        }

        return HslToHex(h % 360, 55, 38);
    }

    /// <summary>The editor's "Pick the color too" color: any hue from the whole seed.</summary>
    public static string ColorForSeed(string seedText)
    {
        var rnd = new Mulberry32(Fnv1a(seedText));
        return HslToHex(Math.Floor(rnd.Next() * 360), 45 + Math.Floor(rnd.Next() * 35), 28 + Math.Floor(rnd.Next() * 20));
    }

    /// <summary>A "#rrggbb" color, or null when the text is not one.</summary>
    public static string? ValidColor(string? hex)
        => hex is { Length: 7 } && hex[0] == '#' && hex.Skip(1).All(Uri.IsHexDigit) ? hex.ToLowerInvariant() : null;

    /// <summary>Draws the card and encodes it as PNG.</summary>
    public static byte[] Png(string name, string? number, string color, string seedText, SKTypeface bold, SKTypeface semiBold)
    {
        using var surface = SKSurface.Create(new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul))
            ?? throw new InvalidOperationException("SkiaSharp could not create a drawing surface");
        Draw(surface.Canvas, name, number, color, seedText, bold, semiBold);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public static void Draw(SKCanvas canvas, string name, string? number, string color, string seedText, SKTypeface bold, SKTypeface semiBold)
    {
        name = string.IsNullOrWhiteSpace(name) ? "TV" : name.Trim();
        number = string.IsNullOrWhiteSpace(number) ? null : number.Trim();
        color = ValidColor(color) ?? ColorForName(name);
        var fg = Luminance(color) > 0.6 ? "#111111" : "#ffffff";
        Background(canvas, color, fg, new Mulberry32(Fnv1a(string.IsNullOrEmpty(seedText) ? name : seedText)));

        // The name: one line, or two when the words do not fit at a readable size.
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var lines = new List<string> { string.Join(" ", words) };
        var size = 120f;
        using var font = new SKFont(bold, size) { Edging = SKFontEdging.SubpixelAntialias, Subpixel = true };
        bool Fits(string text, float px)
        {
            font.Size = px;
            return font.MeasureText(text) <= 700;
        }

        while (size > 40 && !lines.All(l => Fits(l, size)))
        {
            if (lines.Count == 1 && words.Length > 1 && size < 96)
            {
                var mid = (int)Math.Ceiling(words.Length / 2.0);
                lines = new List<string> { string.Join(" ", words.Take(mid)), string.Join(" ", words.Skip(mid)) };
                size = 110;
            }
            else
            {
                size -= 6;
            }
        }

        font.Size = size;
        using var text = new SKPaint { Color = SKColor.Parse(fg), IsAntialias = true };
        text.ImageFilter = SKImageFilter.CreateDropShadow(0, 0, 6, 6, new SKColor(0, 0, 0, (byte)(0.35 * 255)));
        var lineHeight = size * 1.15f;
        var top = Height / 2f - ((lines.Count - 1) * lineHeight) / 2 - (number is null ? 0 : 16);
        for (var i = 0; i < lines.Count; i++)
        {
            canvas.DrawText(lines[i], Width / 2f, Middle(font, top + i * lineHeight), SKTextAlign.Center, font, text);
        }

        if (number is not null)
        {
            text.ImageFilter = null;
            text.Color = SKColor.Parse(fg).WithAlpha((byte)(0.85 * 255));
            using var small = new SKFont(semiBold, 34) { Edging = SKFontEdging.SubpixelAntialias, Subpixel = true };
            canvas.DrawText("CH " + number, Width / 2f, Middle(small, 400), SKTextAlign.Center, small, text);
        }
    }

    /// <summary>Baseline y for text whose vertical middle should sit at y (the canvas "middle" baseline).</summary>
    private static float Middle(SKFont font, float y)
    {
        var m = font.Metrics;
        return y - (m.Ascent + m.Descent) / 2;
    }

    /// <summary>
    /// One of six patterns in the base color and a neighbouring hue, then the soft disc every card has. Each draw
    /// consumes the random source in the same order as the canvas version.
    /// </summary>
    private static void Background(SKCanvas canvas, string color, string fg, Mulberry32 rnd)
    {
        var (bh, bs, bl) = HexToHsl(color);
        var shift = (rnd.Next() < 0.5 ? -1 : 1) * (25 + rnd.Next() * 45);
        var partner = HslToHex((bh + shift + 360) % 360, Math.Min(100, bs + 10), Math.Max(12, Math.Min(88, bl + (rnd.Next() < 0.5 ? -9 : 9))));
        var deep = HslToHex(bh, bs, Math.Max(6, bl - 14));
        var angle = rnd.Next() * Math.PI * 2;
        var cos = (float)Math.Cos(angle);
        var sin = (float)Math.Sin(angle);
        SKShader Gradient(string from, string to) => SKShader.CreateLinearGradient(
            new SKPoint(400 - cos * 450, 225 - sin * 450),
            new SKPoint(400 + cos * 450, 225 + sin * 450),
            new[] { SKColor.Parse(from), SKColor.Parse(to) },
            SKShaderTileMode.Clamp);

        var style = (int)Math.Floor(rnd.Next() * 6);
        using (var fill = new SKPaint { IsAntialias = true })
        {
            if (style == 0)
            {
                fill.Shader = Gradient(color, partner);
            }
            else
            {
                fill.Color = SKColor.Parse(color);
            }

            canvas.DrawRect(0, 0, Width, Height, fill);
        }

        canvas.Save();
        using var paint = new SKPaint { IsAntialias = true };
        if (style == 1)
        {
            // Diagonal stripes.
            var width = (float)(30 + rnd.Next() * 60);
            canvas.Translate(400, 225);
            canvas.RotateRadians((float)angle);
            paint.Color = SKColor.Parse(partner).WithAlpha(Alpha(0.22));
            for (var x = -700f; x < 700; x += width * 2)
            {
                canvas.DrawRect(x, -700, width, 1400, paint);
            }
        }
        else if (style == 2)
        {
            // A grid of dots.
            var gap = (float)(36 + rnd.Next() * 44);
            var radius = (float)(gap * (0.12 + rnd.Next() * 0.16));
            var offsetX = (float)(rnd.Next() * gap);
            var offsetY = (float)(rnd.Next() * gap);
            paint.Color = SKColor.Parse(fg).WithAlpha(Alpha(0.12));
            for (var y = offsetY - gap; y < Height + gap; y += gap)
            {
                for (var x = offsetX - gap; x < Width + gap; x += gap)
                {
                    canvas.DrawCircle(x, y, radius, paint);
                }
            }
        }
        else if (style == 3)
        {
            // Rings spreading from one corner.
            var cx = rnd.Next() < 0.5 ? -40f : 840f;
            var cy = rnd.Next() < 0.5 ? -40f : 490f;
            var gap = (float)(40 + rnd.Next() * 50);
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = gap * 0.45f;
            paint.Color = SKColor.Parse(partner).WithAlpha(Alpha(0.2));
            for (var r = gap; r < 1100; r += gap)
            {
                canvas.DrawCircle(cx, cy, r, paint);
            }
        }
        else if (style == 4)
        {
            // Two or three big tilted slabs.
            var count = 2 + (int)Math.Floor(rnd.Next() * 2);
            paint.Color = SKColor.Parse(partner).WithAlpha(Alpha(0.28));
            for (var i = 0; i < count; i++)
            {
                canvas.Save();
                canvas.Translate((float)(rnd.Next() * Width), (float)(rnd.Next() * Height));
                canvas.RotateRadians((float)(angle + i * (0.5 + rnd.Next())));
                var h1 = (float)(40 + rnd.Next() * 120); // the canvas version draws the top edge and the height from two draws
                var h2 = (float)(40 + rnd.Next() * 120);
                canvas.DrawRect(-900, -h1 / 2, 1800, h2, paint);
                canvas.Restore();
            }
        }
        else if (style == 5)
        {
            // Color bars along the bottom, like a test card.
            using (var fill = new SKPaint { Shader = Gradient(color, deep) })
            {
                canvas.DrawRect(0, 0, Width, Height, fill);
            }

            var bars = 6 + (int)Math.Floor(rnd.Next() * 3);
            var height = (float)(22 + rnd.Next() * 10); // stays under the channel number
            var width = (float)Width / bars;
            for (var i = 0; i < bars; i++)
            {
                paint.Color = SKColor.Parse(HslToHex((bh + i * (360.0 / bars)) % 360, 60, 48)).WithAlpha(Alpha(0.55));
                canvas.DrawRect(i * width, Height - height, width + 1, height, paint);
            }
        }

        canvas.Restore();

        // The soft disc every card has, placed by the seed.
        paint.Style = SKPaintStyle.Fill;
        paint.StrokeWidth = 0;
        paint.Color = SKColor.Parse(fg).WithAlpha(Alpha(0.08));
        var dx = (float)(rnd.Next() < 0.5 ? 120 + rnd.Next() * 80 : 600 + rnd.Next() * 80);
        var dy = (float)(rnd.Next() < 0.5 ? 60 + rnd.Next() * 60 : 340 + rnd.Next() * 60);
        canvas.DrawCircle(dx, dy, (float)(200 + rnd.Next() * 120), paint);
    }

    private static byte Alpha(double a) => (byte)Math.Round(a * 255);

    private static string FirstOf(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? string.Empty;

    /// <summary>FNV-1a over the UTF-16 code units, as the page's hashText.</summary>
    public static uint Fnv1a(string text)
    {
        var h = 2166136261u;
        foreach (var c in text)
        {
            h ^= c;
            h = unchecked(h * 16777619u);
        }

        return h;
    }

    public static string HslToHex(double h, double s, double l)
    {
        s /= 100;
        l /= 100;
        double K(double n) => (n + h / 30) % 12;
        var a = s * Math.Min(l, 1 - l);
        double F(double n) => l - a * Math.Max(-1, Math.Min(K(n) - 3, Math.Min(9 - K(n), 1)));
        string Hex(double x) => ((int)Math.Round(255 * x)).ToString("x2", CultureInfo.InvariantCulture);
        return "#" + Hex(F(0)) + Hex(F(8)) + Hex(F(4));
    }

    public static (double H, double S, double L) HexToHsl(string hex)
    {
        var n = int.Parse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var r = ((n >> 16) & 255) / 255.0;
        var g = ((n >> 8) & 255) / 255.0;
        var b = (n & 255) / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        if (max == min)
        {
            return (0, 0, l * 100);
        }

        var d = max - min;
        var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h;
        if (max == r)
        {
            h = (g - b) / d + (g < b ? 6 : 0);
        }
        else if (max == g)
        {
            h = (b - r) / d + 2;
        }
        else
        {
            h = (r - g) / d + 4;
        }

        return (h * 60, s * 100, l * 100);
    }

    public static double Luminance(string hex)
    {
        var n = int.Parse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return (0.299 * ((n >> 16) & 255) + 0.587 * ((n >> 8) & 255) + 0.114 * (n & 255)) / 255;
    }

    /// <summary>mulberry32, the page's seeded random source: the same seed draws the same card.</summary>
    public sealed class Mulberry32
    {
        private uint _a;

        public Mulberry32(uint seed)
        {
            _a = seed;
        }

        public double Next()
        {
            unchecked
            {
                _a += 0x6D2B79F5;
                var t = _a;
                t = (t ^ (t >> 15)) * (t | 1);
                t ^= t + (t ^ (t >> 7)) * (t | 61);
                return (t ^ (t >> 14)) / 4294967296.0;
            }
        }
    }
}
