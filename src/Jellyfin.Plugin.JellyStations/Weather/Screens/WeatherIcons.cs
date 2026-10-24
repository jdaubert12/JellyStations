using SkiaSharp;

namespace Jellyfin.Plugin.JellyStations.Weather.Screens;

/// <summary>Colours the icons are drawn with; the light theme needs darker clouds.</summary>
public sealed record IconPalette(SKColor Sun, SKColor Moon, SKColor Cloud, SKColor CloudBack, SKColor Rain, SKColor Snow, SKColor Bolt, SKColor Fog)
{
    public static readonly IconPalette OnDark = new(
        new SKColor(0xFF, 0xC9, 0x4A), new SKColor(0xE8, 0xE6, 0xD5), new SKColor(0xF4, 0xF7, 0xFB), new SKColor(0xB8, 0xC6, 0xDC),
        new SKColor(0x6E, 0xC1, 0xFF), new SKColor(0xFF, 0xFF, 0xFF), new SKColor(0xFF, 0xD2, 0x4A), new SKColor(0xD8, 0xE1, 0xEE));

    public static readonly IconPalette OnLight = new(
        new SKColor(0xF2, 0xA8, 0x1D), new SKColor(0x8A, 0x93, 0xA8), new SKColor(0x8F, 0xA3, 0xC0), new SKColor(0x6B, 0x7F, 0x9E),
        new SKColor(0x1E, 0x6F, 0xB8), new SKColor(0x6B, 0x8C, 0xB8), new SKColor(0xE0, 0x9A, 0x10), new SKColor(0x8A, 0x9C, 0xB8));
}

/// <summary>
/// Weather icons drawn as vector shapes, no image files: sun, moon, clouds, drops, flakes, bolts, fog. Each is
/// drawn inside a square of <c>size</c> centred on a point; the night flag swaps the sun for a moon where it matters.
/// </summary>
public static class WeatherIcons
{
    public static void Draw(SKCanvas canvas, WeatherIcon icon, bool isDay, float cx, float cy, float size, IconPalette c)
    {
        var s = size / 100f; // all coordinates below are in a 100-unit box
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };

        switch (icon)
        {
            case WeatherIcon.Clear:
                if (isDay)
                {
                    Sun(canvas, cx, cy, 36 * s, c, fill, stroke);
                }
                else
                {
                    Moon(canvas, cx, cy, 34 * s, c, fill);
                }

                break;

            case WeatherIcon.PartlyCloudy:
                Celestial(canvas, isDay, cx - 16 * s, cy - 18 * s, 24 * s, c, fill, stroke);
                Cloud(canvas, cx + 6 * s, cy + 12 * s, 72 * s, c.Cloud, fill);
                break;

            case WeatherIcon.Cloudy:
                Cloud(canvas, cx + 14 * s, cy - 14 * s, 60 * s, c.CloudBack, fill);
                Cloud(canvas, cx - 4 * s, cy + 10 * s, 80 * s, c.Cloud, fill);
                break;

            case WeatherIcon.Fog:
                Cloud(canvas, cx, cy - 16 * s, 76 * s, c.Cloud, fill);
                stroke.Color = c.Fog;
                stroke.StrokeWidth = 6 * s;
                canvas.DrawLine(cx - 34 * s, cy + 22 * s, cx + 30 * s, cy + 22 * s, stroke);
                canvas.DrawLine(cx - 26 * s, cy + 36 * s, cx + 36 * s, cy + 36 * s, stroke);
                break;

            case WeatherIcon.Drizzle:
                Cloud(canvas, cx, cy - 14 * s, 76 * s, c.Cloud, fill);
                Dots(canvas, cx, cy + 26 * s, s, 4, c.Rain, fill);
                break;

            case WeatherIcon.Rain:
                Cloud(canvas, cx, cy - 14 * s, 76 * s, c.Cloud, fill);
                Drops(canvas, cx, cy + 24 * s, s, 3, c.Rain, stroke);
                break;

            case WeatherIcon.HeavyRain:
                Cloud(canvas, cx, cy - 16 * s, 76 * s, c.CloudBack, fill);
                Drops(canvas, cx, cy + 22 * s, s, 5, c.Rain, stroke);
                break;

            case WeatherIcon.FreezingRain:
                Cloud(canvas, cx, cy - 14 * s, 76 * s, c.Cloud, fill);
                Drops(canvas, cx - 10 * s, cy + 24 * s, s, 2, c.Rain, stroke);
                Flake(canvas, cx + 22 * s, cy + 30 * s, 9 * s, c.Snow, stroke);
                break;

            case WeatherIcon.Snow:
                Cloud(canvas, cx, cy - 14 * s, 76 * s, c.Cloud, fill);
                Flake(canvas, cx - 18 * s, cy + 28 * s, 8 * s, c.Snow, stroke);
                Flake(canvas, cx + 4 * s, cy + 36 * s, 8 * s, c.Snow, stroke);
                Flake(canvas, cx + 24 * s, cy + 26 * s, 8 * s, c.Snow, stroke);
                break;

            case WeatherIcon.HeavySnow:
                Cloud(canvas, cx, cy - 16 * s, 76 * s, c.CloudBack, fill);
                Flake(canvas, cx - 24 * s, cy + 24 * s, 8 * s, c.Snow, stroke);
                Flake(canvas, cx - 6 * s, cy + 38 * s, 8 * s, c.Snow, stroke);
                Flake(canvas, cx + 12 * s, cy + 24 * s, 8 * s, c.Snow, stroke);
                Flake(canvas, cx + 28 * s, cy + 38 * s, 8 * s, c.Snow, stroke);
                break;

            case WeatherIcon.Sleet:
                Cloud(canvas, cx, cy - 14 * s, 76 * s, c.Cloud, fill);
                Drops(canvas, cx - 12 * s, cy + 24 * s, s, 2, c.Rain, stroke);
                Flake(canvas, cx + 20 * s, cy + 30 * s, 8 * s, c.Snow, stroke);
                break;

            case WeatherIcon.Showers:
                Celestial(canvas, isDay, cx - 20 * s, cy - 24 * s, 20 * s, c, fill, stroke);
                Cloud(canvas, cx + 4 * s, cy - 2 * s, 70 * s, c.Cloud, fill);
                Drops(canvas, cx + 4 * s, cy + 32 * s, s, 3, c.Rain, stroke);
                break;

            case WeatherIcon.SnowShowers:
                Celestial(canvas, isDay, cx - 20 * s, cy - 24 * s, 20 * s, c, fill, stroke);
                Cloud(canvas, cx + 4 * s, cy - 2 * s, 70 * s, c.Cloud, fill);
                Flake(canvas, cx - 10 * s, cy + 34 * s, 8 * s, c.Snow, stroke);
                Flake(canvas, cx + 16 * s, cy + 38 * s, 8 * s, c.Snow, stroke);
                break;

            case WeatherIcon.Thunderstorm:
                Cloud(canvas, cx, cy - 16 * s, 76 * s, c.CloudBack, fill);
                Bolt(canvas, cx + 2 * s, cy + 26 * s, s, c.Bolt, fill);
                Drops(canvas, cx - 26 * s, cy + 22 * s, s, 1, c.Rain, stroke);
                Drops(canvas, cx + 28 * s, cy + 22 * s, s, 1, c.Rain, stroke);
                break;

            default:
                Cloud(canvas, cx, cy - 10 * s, 76 * s, c.Cloud, fill);
                stroke.Color = c.Fog;
                stroke.StrokeWidth = 5 * s;
                canvas.DrawLine(cx - 20 * s, cy + 30 * s, cx + 20 * s, cy + 30 * s, stroke);
                break;
        }
    }

    private static void Celestial(SKCanvas canvas, bool isDay, float cx, float cy, float r, IconPalette c, SKPaint fill, SKPaint stroke)
    {
        if (isDay)
        {
            Sun(canvas, cx, cy, r, c, fill, stroke);
        }
        else
        {
            Moon(canvas, cx, cy, r, c, fill);
        }
    }

    private static void Sun(SKCanvas canvas, float cx, float cy, float r, IconPalette c, SKPaint fill, SKPaint stroke)
    {
        fill.Color = c.Sun;
        canvas.DrawCircle(cx, cy, r * 0.58f, fill);
        stroke.Color = c.Sun;
        stroke.StrokeWidth = r * 0.14f;
        for (var i = 0; i < 8; i++)
        {
            var a = i * MathF.PI / 4;
            var x1 = cx + MathF.Cos(a) * r * 0.78f;
            var y1 = cy + MathF.Sin(a) * r * 0.78f;
            var x2 = cx + MathF.Cos(a) * r;
            var y2 = cy + MathF.Sin(a) * r;
            canvas.DrawLine(x1, y1, x2, y2, stroke);
        }
    }

    private static void Moon(SKCanvas canvas, float cx, float cy, float r, IconPalette c, SKPaint fill)
    {
        using var disc = new SKPath();
        disc.AddCircle(cx, cy, r * 0.8f);
        using var bite = new SKPath();
        bite.AddCircle(cx + r * 0.42f, cy - r * 0.3f, r * 0.66f);
        using var moon = disc.Op(bite, SKPathOp.Difference);
        fill.Color = c.Moon;
        canvas.DrawPath(moon ?? disc, fill);
    }

    /// <summary>A cloud about <paramref name="w"/> wide whose flat bottom sits a little below (cx, cy).</summary>
    public static void Cloud(SKCanvas canvas, float cx, float cy, float w, SKColor color, SKPaint fill)
    {
        using var path = new SKPath { FillType = SKPathFillType.Winding };
        var h = w * 0.26f;
        path.AddRoundRect(new SKRect(cx - w * 0.5f, cy - h * 0.1f, cx + w * 0.5f, cy + h), h * 0.5f, h * 0.5f);
        path.AddCircle(cx - w * 0.22f, cy - w * 0.02f, w * 0.2f);
        path.AddCircle(cx + w * 0.06f, cy - w * 0.12f, w * 0.27f);
        path.AddCircle(cx + w * 0.3f, cy - w * 0.01f, w * 0.18f);
        fill.Color = color;
        canvas.DrawPath(path, fill);
    }

    private static void Drops(SKCanvas canvas, float cx, float top, float s, int count, SKColor color, SKPaint stroke)
    {
        stroke.Color = color;
        stroke.StrokeWidth = 6 * s;
        var spacing = 16 * s;
        var x0 = cx - (count - 1) * spacing / 2;
        for (var i = 0; i < count; i++)
        {
            var x = x0 + i * spacing;
            var lift = i % 2 == 1 ? 6 * s : 0;
            canvas.DrawLine(x + 3 * s, top + lift, x - 3 * s, top + lift + 16 * s, stroke);
        }
    }

    private static void Dots(SKCanvas canvas, float cx, float top, float s, int count, SKColor color, SKPaint fill)
    {
        fill.Color = color;
        var spacing = 14 * s;
        var x0 = cx - (count - 1) * spacing / 2;
        for (var i = 0; i < count; i++)
        {
            canvas.DrawCircle(x0 + i * spacing, top + (i % 2 == 1 ? 10 * s : 0), 3.5f * s, fill);
        }
    }

    private static void Flake(SKCanvas canvas, float cx, float cy, float r, SKColor color, SKPaint stroke)
    {
        stroke.Color = color;
        stroke.StrokeWidth = r * 0.28f;
        for (var i = 0; i < 3; i++)
        {
            var a = i * MathF.PI / 3;
            canvas.DrawLine(cx - MathF.Cos(a) * r, cy - MathF.Sin(a) * r, cx + MathF.Cos(a) * r, cy + MathF.Sin(a) * r, stroke);
        }
    }

    private static void Bolt(SKCanvas canvas, float cx, float cy, float s, SKColor color, SKPaint fill)
    {
        using var path = new SKPath();
        path.MoveTo(cx + 4 * s, cy - 14 * s);
        path.LineTo(cx - 10 * s, cy + 6 * s);
        path.LineTo(cx - 1 * s, cy + 6 * s);
        path.LineTo(cx - 6 * s, cy + 22 * s);
        path.LineTo(cx + 11 * s, cy - 1 * s);
        path.LineTo(cx + 2 * s, cy - 1 * s);
        path.LineTo(cx + 7 * s, cy - 14 * s);
        path.Close();
        fill.Color = color;
        canvas.DrawPath(path, fill);
    }
}
