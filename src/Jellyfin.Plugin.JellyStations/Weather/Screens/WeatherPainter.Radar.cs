using Jellyfin.Plugin.JellyStations.Models;
using Jellyfin.Plugin.JellyStations.Weather.Radar;
using SkiaSharp;

namespace Jellyfin.Plugin.JellyStations.Weather.Screens;

/// <summary>The map colours of one theme.</summary>
public sealed record RadarPalette(SKColor Water, SKColor Land, SKColor CountryLine, SKColor StateLine, SKColor CityDot, SKColor Halo, SKColor Pill)
{
    public static RadarPalette For(WeatherTheme theme) => theme switch
    {
        WeatherTheme.Dark => new RadarPalette(
            new SKColor(0x0A, 0x0D, 0x14), new SKColor(0x26, 0x2D, 0x3B), new SKColor(0x8A, 0x96, 0xAC), new SKColor(0x56, 0x61, 0x78),
            new SKColor(0xE8, 0xEC, 0xF4), new SKColor(0x00, 0x00, 0x00, 0xCC), new SKColor(0x00, 0x00, 0x00, 0xB0)),
        WeatherTheme.Light => new RadarPalette(
            new SKColor(0xB9, 0xD2, 0xEE), new SKColor(0xF1, 0xF3, 0xF6), new SKColor(0x5E, 0x6E, 0x8A), new SKColor(0x9A, 0xA7, 0xBC),
            new SKColor(0x1F, 0x2D, 0x4A), new SKColor(0xFF, 0xFF, 0xFF, 0xD0), new SKColor(0xFF, 0xFF, 0xFF, 0xD8)),
        _ => new RadarPalette(
            new SKColor(0x07, 0x1A, 0x44), new SKColor(0x27, 0x46, 0x7E), new SKColor(0xB4, 0xC6, 0xEC), new SKColor(0x7F, 0x97, 0xC8),
            new SKColor(0xFF, 0xFF, 0xFF), new SKColor(0x06, 0x1B, 0x45, 0xCC), new SKColor(0x06, 0x1B, 0x45, 0xB8))
    };
}

/// <summary>The radar screen: the base map in the theme's colours, the frame's radar overlay, the place, nearby towns, a legend and the frame clock.</summary>
public sealed partial class WeatherPainter
{
    /// <summary>The reflectivity ramp the legend shows, light to heavy (the NWS palette both sources use).</summary>
    private static readonly SKColor[] LegendColors =
    {
        new(0x04, 0xE9, 0xE7), new(0x01, 0x9F, 0xF4), new(0x02, 0xFD, 0x02), new(0x00, 0x8E, 0x00), new(0xFD, 0xF8, 0x02),
        new(0xFD, 0x95, 0x00), new(0xFD, 0x00, 0x00), new(0xBC, 0x00, 0x00), new(0xF8, 0x00, 0xFD)
    };

    private void Radar(Ctx x)
    {
        var place = x.R.Place;
        var frame = x.R.Radar;
        if (place is null || frame is null)
        {
            Unavailable(x, "Radar not available here yet", place?.Location.Name);
            return;
        }

        var pal = RadarPalette.For(x.C.Theme);
        var rect = new SKRect(80, 170, x.W - 80, 930);
        var viewport = new RadarViewport(place.Location.Latitude, place.Location.Longitude, frame.Zoom, RadarViewport.MapWidth, RadarViewport.MapHeight);
        var originX = rect.MidX - (viewport.Width / 2f);
        var originY = rect.MidY - (viewport.Height / 2f);
        var map = BaseMap.Instance;
        var bounds = viewport.Bounds(0.15);

        x.Canvas.Save();
        x.Canvas.ClipRoundRect(new SKRoundRect(rect, 24, 24), antialias: true);
        using (var water = new SKPaint { Color = pal.Water })
        {
            x.Canvas.DrawRect(rect, water);
        }

        FillLayer(x, map.Land, viewport, originX, originY, bounds, pal.Land);
        FillLayer(x, map.Lakes, viewport, originX, originY, bounds, pal.Water);
        StrokeLayer(x, map.StateBorders, viewport, originX, originY, bounds, pal.StateLine, 1.5f);
        StrokeLayer(x, map.CountryBorders, viewport, originX, originY, bounds, pal.CountryLine, 2.5f);

        if (File.Exists(frame.OverlayPath))
        {
            try
            {
                using var overlay = SKImage.FromEncodedData(frame.OverlayPath);
                if (overlay is not null)
                {
                    using var paint = new SKPaint { Color = SKColors.White.WithAlpha(0xDE) };
                    x.Canvas.DrawImage(overlay, new SKRect(originX, originY, originX + viewport.Width, originY + viewport.Height), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), paint);
                }
            }
            catch (Exception)
            {
                // A frame that cannot be decoded leaves the map bare; the next fetch replaces it.
            }
        }

        var taken = new List<SKRect>();
        var (mx, my) = viewport.Project(place.Location.Latitude, place.Location.Longitude);
        mx += originX;
        my += originY;
        Towns(x, map, viewport, originX, originY, bounds, rect, mx, my, pal, taken);

        // The place: a ring with a dot, its name beside it.
        using (var ring = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 4, Color = x.T.Accent })
        using (var dot = new SKPaint { IsAntialias = true, Color = SKColors.White })
        using (var shadow = new SKPaint { IsAntialias = true, Color = pal.Halo })
        {
            x.Canvas.DrawCircle(mx, my, 16, shadow);
            x.Canvas.DrawCircle(mx, my, 13, ring);
            x.Canvas.DrawCircle(mx, my, 5, dot);
        }

        HaloText(x, place.Location.Name, mx + 24, my + 11, 32, FontWeight.Bold, x.T.Text, pal.Halo);

        FrameClock(x, frame, rect, pal);
        Legend(x, rect, pal);
        HaloText(x, frame.SourceName, rect.Right - 20, rect.Top + 36, 20, FontWeight.Regular, x.T.Text.WithAlpha(0xCC), pal.Halo, SKTextAlign.Right);
        x.Canvas.Restore();

        using var border = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2, Color = x.T.Line };
        x.Canvas.DrawRoundRect(rect, 24, 24, border);
    }

    private static void AddPolyline(SKPath path, MapPolyline line, RadarViewport viewport, float originX, float originY, bool close)
    {
        for (var i = 0; i < line.Count; i++)
        {
            var (px, py) = viewport.Project(line.Latitudes[i], line.Longitudes[i]);
            if (i == 0)
            {
                path.MoveTo(px + originX, py + originY);
            }
            else
            {
                path.LineTo(px + originX, py + originY);
            }
        }

        if (close)
        {
            path.Close();
        }
    }

    private static void FillLayer(Ctx x, IReadOnlyList<MapPolyline> layer, RadarViewport viewport, float originX, float originY, (double, double, double, double) bounds, SKColor color)
    {
        using var path = new SKPath { FillType = SKPathFillType.EvenOdd };
        foreach (var line in BaseMap.Within(layer, bounds))
        {
            AddPolyline(path, line, viewport, originX, originY, close: true);
        }

        using var paint = new SKPaint { IsAntialias = true, Color = color };
        x.Canvas.DrawPath(path, paint);
    }

    private static void StrokeLayer(Ctx x, IReadOnlyList<MapPolyline> layer, RadarViewport viewport, float originX, float originY, (double, double, double, double) bounds, SKColor color, float width)
    {
        using var path = new SKPath();
        foreach (var line in BaseMap.Within(layer, bounds))
        {
            AddPolyline(path, line, viewport, originX, originY, close: false);
        }

        using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = width, Color = color, StrokeJoin = SKStrokeJoin.Round };
        x.Canvas.DrawPath(path, paint);
    }

    /// <summary>Up to nine towns in view, the important ones first, each labelled only where its label does not cover another.</summary>
    private void Towns(Ctx x, BaseMap map, RadarViewport viewport, float originX, float originY, (double, double, double, double) bounds, SKRect rect, float mx, float my, RadarPalette pal, List<SKRect> taken)
    {
        const float size = 22f;
        using var font = new SKFont(_assets.Typeface(FontWeight.SemiBold), size);
        taken.Add(new SKRect(mx - 20, my - 20, mx + 24 + font.MeasureText(x.R.Place!.Location.Name) + 10, my + 24));
        var shown = 0;
        foreach (var town in map.PlacesWithin(bounds).OrderBy(p => p.Rank).ThenByDescending(p => p.Population))
        {
            if (shown >= 9)
            {
                break;
            }

            var (px, py) = viewport.Project(town.Latitude, town.Longitude);
            px += originX;
            py += originY;
            if (px < rect.Left + 20 || px > rect.Right - 20 || py < rect.Top + 50 || py > rect.Bottom - 80)
            {
                continue;
            }

            if (Math.Abs(px - mx) < 40 && Math.Abs(py - my) < 40)
            {
                continue;
            }

            var w = font.MeasureText(town.Name);
            var label = new SKRect(px - 8, py - 18, px + 12 + w + 6, py + 12);
            if (taken.Any(t => t.IntersectsWith(label)))
            {
                continue;
            }

            taken.Add(label);
            shown++;
            using var dot = new SKPaint { IsAntialias = true, Color = pal.CityDot };
            using var halo = new SKPaint { IsAntialias = true, Color = pal.Halo };
            x.Canvas.DrawCircle(px, py, 6, halo);
            x.Canvas.DrawCircle(px, py, 4, dot);
            HaloText(x, town.Name, px + 12, py + 8, size, FontWeight.SemiBold, x.T.Text, pal.Halo);
        }
    }

    /// <summary>The frame's time in a pill at the bottom-left of the map, with a dot per frame showing where the loop is.</summary>
    private void FrameClock(Ctx x, RadarFrameRequest frame, SKRect rect, RadarPalette pal)
    {
        var local = frame.TimeUtc + (x.NowLocal - x.R.NowUtc);
        var text = Units.Clock(local, x.C.TwentyFourHourClock);
        using var font = new SKFont(_assets.Typeface(FontWeight.SemiBold), 30);
        var textWidth = font.MeasureText(text);
        var dotsWidth = (frame.Count * 16f) + 8;
        var pill = new SKRect(rect.Left + 24, rect.Bottom - 24 - 58, rect.Left + 24 + 24 + textWidth + 20 + dotsWidth + 20, rect.Bottom - 24);
        using (var paint = new SKPaint { IsAntialias = true, Color = pal.Pill })
        {
            x.Canvas.DrawRoundRect(pill, 14, 14, paint);
        }

        Text(x, text, pill.Left + 24, pill.MidY + 11, 30, FontWeight.SemiBold, x.T.Text);
        var dx = pill.Left + 24 + textWidth + 20;
        for (var i = 0; i < frame.Count; i++)
        {
            using var paint = new SKPaint { IsAntialias = true, Color = i <= frame.Index ? x.T.Accent : x.T.Text.WithAlpha(0x55) };
            x.Canvas.DrawCircle(dx + 8 + (i * 16f), pill.MidY, i == frame.Index ? 6 : 4.5f, paint);
        }
    }

    /// <summary>Light-to-heavy colour bar at the bottom-right of the map.</summary>
    private void Legend(Ctx x, SKRect rect, RadarPalette pal)
    {
        const float barWidth = 300f;
        var pill = new SKRect(rect.Right - 24 - barWidth - 48, rect.Bottom - 24 - 58, rect.Right - 24, rect.Bottom - 24);
        using (var paint = new SKPaint { IsAntialias = true, Color = pal.Pill })
        {
            x.Canvas.DrawRoundRect(pill, 14, 14, paint);
        }

        var bar = new SKRect(pill.Left + 24, pill.MidY - 2, pill.Left + 24 + barWidth, pill.MidY + 12);
        using (var paint = new SKPaint { IsAntialias = true })
        {
            paint.Shader = SKShader.CreateLinearGradient(new SKPoint(bar.Left, 0), new SKPoint(bar.Right, 0), LegendColors, SKShaderTileMode.Clamp);
            x.Canvas.DrawRoundRect(bar, 4, 4, paint);
        }

        Text(x, "LIGHT", bar.Left, pill.MidY - 8, 16, FontWeight.SemiBold, x.T.Text.WithAlpha(0xCC));
        Text(x, "HEAVY", bar.Right, pill.MidY - 8, 16, FontWeight.SemiBold, x.T.Text.WithAlpha(0xCC), SKTextAlign.Right);
    }

    /// <summary>Text with a halo so it reads over any map colour.</summary>
    private void HaloText(Ctx x, string text, float px, float baseline, float size, FontWeight weight, SKColor color, SKColor halo, SKTextAlign align = SKTextAlign.Left)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        using var font = new SKFont(_assets.Typeface(weight), size) { Edging = SKFontEdging.SubpixelAntialias, Subpixel = true };
        using var stroke = new SKPaint { IsAntialias = true, Color = halo, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(3, size / 6), StrokeJoin = SKStrokeJoin.Round };
        using var fill = new SKPaint { IsAntialias = true, Color = color };
        x.Canvas.DrawText(text, px, baseline, align, font, stroke);
        x.Canvas.DrawText(text, px, baseline, align, font, fill);
    }
}
