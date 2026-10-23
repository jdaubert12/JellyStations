using System.Globalization;
using Jellyfin.Plugin.TvStation.Models;
using Jellyfin.Plugin.TvStation.Scheduling;
using SkiaSharp;

namespace Jellyfin.Plugin.TvStation.Weather.Screens;

/// <summary>One place with whatever report it has.</summary>
public sealed record PlaceWeather(WeatherLocation Location, WeatherReport? Report, bool Stale);

/// <summary>Everything one screen is drawn from. Pure data, so a test can render a screen from a fixture.</summary>
public sealed record ScreenRequest(
    WeatherScreenKind Kind,
    PlaceWeather? Place,
    IReadOnlyList<PlaceWeather> All,
    WeatherConfig Config,
    DateTime NowUtc,
    TimeZoneInfo StationZone,
    string? LogoPath,
    int Width,
    int Height,
    string Attribution);

/// <summary>The colours of one theme.</summary>
public sealed record Theme(SKColor BgTop, SKColor BgBottom, SKColor Band, SKColor Panel, SKColor Text, SKColor Secondary, SKColor Accent, SKColor Cool, SKColor Low, SKColor Line, SKColor BandText, IconPalette Icons)
{
    public static Theme For(WeatherTheme theme) => theme switch
    {
        WeatherTheme.Dark => new Theme(
            new SKColor(0x0F, 0x12, 0x18), new SKColor(0x1C, 0x22, 0x30), new SKColor(0x09, 0x0B, 0x10), new SKColor(0xFF, 0xFF, 0xFF, 0x12),
            new SKColor(0xF2, 0xF4, 0xF8), new SKColor(0x9A, 0xA3, 0xB5), new SKColor(0xFF, 0xB0, 0x3B), new SKColor(0x6E, 0xC1, 0xFF), new SKColor(0xA9, 0xC4, 0xF0),
            new SKColor(0xFF, 0xFF, 0xFF, 0x22), new SKColor(0xF2, 0xF4, 0xF8), IconPalette.OnDark),
        WeatherTheme.Light => new Theme(
            new SKColor(0xF4, 0xF7, 0xFB), new SKColor(0xD3, 0xDE, 0xEE), new SKColor(0x1F, 0x3A, 0x66), new SKColor(0x10, 0x22, 0x40, 0x12),
            new SKColor(0x12, 0x22, 0x40), new SKColor(0x4A, 0x5B, 0x78), new SKColor(0xD9, 0x73, 0x1A), new SKColor(0x1E, 0x6F, 0xB8), new SKColor(0x3F, 0x63, 0x9E),
            new SKColor(0x10, 0x22, 0x40, 0x28), new SKColor(0xFF, 0xFF, 0xFF), IconPalette.OnLight),
        _ => new Theme(
            new SKColor(0x0A, 0x2A, 0x6B), new SKColor(0x16, 0x3F, 0x9A), new SKColor(0x06, 0x1B, 0x45), new SKColor(0xFF, 0xFF, 0xFF, 0x1A),
            new SKColor(0xFF, 0xFF, 0xFF), new SKColor(0xB9, 0xC8, 0xE8), new SKColor(0xFF, 0xB0, 0x3B), new SKColor(0x7F, 0xD0, 0xFF), new SKColor(0x9F, 0xC4, 0xFF),
            new SKColor(0xFF, 0xFF, 0xFF, 0x2E), new SKColor(0xFF, 0xFF, 0xFF), IconPalette.OnDark)
    };
}

/// <summary>
/// Draws the weather screens with SkiaSharp. Everything is laid out on a 1920×1080 grid and scaled to the
/// encoding profile's picture, so a 720p station gets the same screen smaller. The lower band leaves room on
/// the left for the live clock and in the middle for the music credit, both drawn later by ffmpeg.
/// </summary>
public sealed class WeatherPainter
{
    /// <summary>The band across the bottom, in 1080-grid units: y from here to 1080.</summary>
    public const float BandTop = 950f;

    /// <summary>Where ffmpeg draws the clock (1080 grid): left edge and top of the text box.</summary>
    public const float ClockX = 80f;
    public const float ClockY = 988f;
    public const float ClockSize = 54f;

    /// <summary>Where ffmpeg draws the music credit (1080 grid): top of the text box, centred horizontally.</summary>
    public const float CreditY = 1004f;
    public const float CreditSize = 28f;

    private readonly WeatherAssets _assets;

    public WeatherPainter(WeatherAssets assets)
    {
        _assets = assets;
    }

    public void Paint(ScreenRequest r, string outputPath)
    {
        using var surface = SKSurface.Create(new SKImageInfo(r.Width, r.Height, SKColorType.Rgba8888, SKAlphaType.Premul))
            ?? throw new InvalidOperationException("SkiaSharp could not create a drawing surface");
        var canvas = surface.Canvas;
        var scale = r.Height / 1080f;
        canvas.Scale(scale);
        var width = r.Width / scale; // in 1080-grid units; 1920 for 16:9
        var t = Theme.For(r.Config.Theme);
        var ctx = new Ctx(canvas, t, width, r);

        Background(ctx);
        Header(ctx);
        switch (r.Kind)
        {
            case WeatherScreenKind.Current:
                Current(ctx);
                break;
            case WeatherScreenKind.Hourly:
                Hourly(ctx);
                break;
            case WeatherScreenKind.Daily:
                Daily(ctx);
                break;
            case WeatherScreenKind.Travel:
                Travel(ctx);
                break;
        }

        Band(ctx);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        var tmp = outputPath + ".tmp";
        using (var file = File.Create(tmp))
        {
            data.SaveTo(file);
        }

        File.Move(tmp, outputPath, overwrite: true);
    }

    private sealed class Ctx
    {
        public Ctx(SKCanvas canvas, Theme theme, float width, ScreenRequest request)
        {
            Canvas = canvas;
            T = theme;
            W = width;
            R = request;
        }

        public SKCanvas Canvas { get; }

        public Theme T { get; }

        public float W { get; }

        public ScreenRequest R { get; }

        public WeatherConfig C => R.Config;

        public WeatherReport? Report => R.Place?.Report;

        /// <summary>Wall-clock "now" at the screen's place (or the station when there is no place).</summary>
        public DateTime NowLocal
        {
            get
            {
                if (R.Place?.Report is { } rep)
                {
                    return R.NowUtc.AddSeconds(rep.UtcOffsetSeconds);
                }

                return R.NowUtc + R.StationZone.GetUtcOffset(R.NowUtc);
            }
        }
    }

    // ---- Chrome -----------------------------------------------------------------------------

    private void Background(Ctx x)
    {
        using var paint = new SKPaint { IsAntialias = true };
        paint.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, 1080), new[] { x.T.BgTop, x.T.BgBottom }, SKShaderTileMode.Clamp);
        x.Canvas.DrawRect(0, 0, x.W, 1080, paint);
    }

    private void Header(Ctx x)
    {
        var place = x.R.Place;
        var title = x.R.Kind == WeatherScreenKind.Travel ? "Travel cities" : place?.Location.Name ?? "Weather";
        var subtitle = x.R.Kind == WeatherScreenKind.Travel
            ? x.NowLocal.ToString("dddd, MMMM d", CultureInfo.InvariantCulture)
            : place?.Location.Region ?? string.Empty;
        Text(x, title, 80, 86, 62, FontWeight.SemiBold, x.T.Text, maxWidth: x.W * 0.55f);
        if (subtitle.Length > 0)
        {
            Text(x, subtitle, 80, 124, 28, FontWeight.Regular, x.T.Secondary, maxWidth: x.W * 0.55f);
        }

        var label = x.R.Kind switch
        {
            WeatherScreenKind.Current => "CURRENT CONDITIONS",
            WeatherScreenKind.Hourly => "NEXT 12 HOURS",
            WeatherScreenKind.Daily => "7-DAY FORECAST",
            _ => "AROUND THE MAP"
        };
        Text(x, label, x.W - 80, 78, 30, FontWeight.SemiBold, x.T.Text, SKTextAlign.Right);

        string? under = null;
        if (x.R.Kind == WeatherScreenKind.Current && x.Report is { } rep)
        {
            under = "Updated " + Units.Clock(rep.ObservedUtc.AddSeconds(rep.UtcOffsetSeconds), x.C.TwentyFourHourClock);
        }
        else if (x.R.Kind != WeatherScreenKind.Travel)
        {
            under = x.NowLocal.ToString("dddd, MMMM d", CultureInfo.InvariantCulture);
        }
        else
        {
            under = x.C.Units == WeatherUnits.Imperial ? "Temperatures in °F" : "Temperatures in °C";
        }

        if (under is not null)
        {
            Text(x, under, x.W - 80, 118, 26, FontWeight.Regular, x.T.Secondary, SKTextAlign.Right);
        }

        Line(x, 80, 148, x.W - 80, 148);
    }

    private void Band(Ctx x)
    {
        using var paint = new SKPaint { IsAntialias = true, Color = x.T.Band };
        x.Canvas.DrawRect(0, BandTop, x.W, 1080 - BandTop, paint);

        var clockLabel = x.R.Kind == WeatherScreenKind.Travel ? "STATION TIME" : "LOCAL TIME";
        Text(x, clockLabel, ClockX, 978, 20, FontWeight.SemiBold, x.T.BandText.WithAlpha(0xAA));

        // Logo at the right of the band, attribution to its left.
        var right = x.W - 80;
        if (x.C.ShowLogo && !string.IsNullOrEmpty(x.R.LogoPath) && File.Exists(x.R.LogoPath))
        {
            try
            {
                using var img = SKImage.FromEncodedData(x.R.LogoPath);
                if (img is not null)
                {
                    var h = 76f;
                    var w = Math.Min(260f, img.Width * h / Math.Max(1, img.Height));
                    var dest = new SKRect(right - w, BandTop + (1080 - BandTop - h) / 2, right, BandTop + (1080 - BandTop - h) / 2 + h);
                    using var p = new SKPaint { IsAntialias = true };
                    x.Canvas.DrawImage(img, dest, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), p);
                    right -= w + 36;
                }
            }
            catch (Exception)
            {
                // A logo that cannot be decoded is simply not drawn.
            }
        }

        if (!string.IsNullOrEmpty(x.R.Attribution))
        {
            Text(x, x.R.Attribution, right, 1022, 22, FontWeight.Regular, x.T.BandText.WithAlpha(0xAA), SKTextAlign.Right);
        }
    }

    // ---- Screens ----------------------------------------------------------------------------

    private void Current(Ctx x)
    {
        var place = x.R.Place;
        var rep = x.Report;
        if (place is null || rep is null)
        {
            Unavailable(x, "Waiting for the first forecast", place?.Location.Name);
            return;
        }

        var cur = rep.Current;
        var units = x.C.Units;
        var icons = x.T.Icons;

        WeatherIcons.Draw(x.Canvas, WmoWeather.Icon(cur.WeatherCode), cur.IsDay, 300, 480, 340, icons);

        Text(x, Units.Temperature(cur.TemperatureC, units), 560, 560, 250, FontWeight.Bold, x.T.Accent);
        Text(x, WmoWeather.Describe(cur.WeatherCode), 570, 650, 56, FontWeight.SemiBold, x.T.Text, maxWidth: 620);
        Text(x, "Feels like " + Units.Temperature(cur.FeelsLikeC, units), 570, 712, 38, FontWeight.Regular, x.T.Secondary);

        var today = rep.Daily.FirstOrDefault(d => d.Date.Date == x.NowLocal.Date) ?? rep.Daily.FirstOrDefault();
        if (today is not null)
        {
            Text(x, "High " + Units.Temperature(today.HighC, units) + "   Low " + Units.Temperature(today.LowC, units), 570, 770, 34, FontWeight.Regular, x.T.Secondary);
        }

        // Details panel.
        var panel = new SKRect(1240, 170, x.W - 80, 920);
        Panel(x, panel);
        var rows = new List<(string Label, string Value)>
        {
            ("Humidity", cur.HumidityPercent.ToString(CultureInfo.InvariantCulture) + "%"),
            ("Wind", WmoWeather.Compass(cur.WindDirectionDegrees) + " " + Units.Speed(cur.WindKmh, units)),
            ("Gusts", Units.Speed(cur.GustKmh, units)),
            ("Pressure", Units.Pressure(cur.PressureHpa, units)),
            ("Cloud cover", cur.CloudCoverPercent.ToString(CultureInfo.InvariantCulture) + "%")
        };
        if (today?.Sunrise is { } rise)
        {
            rows.Add(("Sunrise", Units.Clock(rise, x.C.TwentyFourHourClock)));
        }

        if (today?.Sunset is { } set)
        {
            rows.Add(("Sunset", Units.Clock(set, x.C.TwentyFourHourClock)));
        }

        if (rows.Count < 7 && today is not null)
        {
            rows.Add(("Rain chance", today.PrecipitationProbabilityPercent.ToString(CultureInfo.InvariantCulture) + "%"));
        }

        var rowH = (panel.Height - 40) / rows.Count;
        for (var i = 0; i < rows.Count; i++)
        {
            var baseline = panel.Top + 20 + rowH * i + rowH * 0.62f;
            Text(x, rows[i].Label, panel.Left + 44, baseline, 30, FontWeight.Regular, x.T.Secondary);
            Text(x, rows[i].Value, panel.Right - 44, baseline, 40, FontWeight.SemiBold, x.T.Text, SKTextAlign.Right);
            if (i < rows.Count - 1)
            {
                Line(x, panel.Left + 44, panel.Top + 20 + rowH * (i + 1), panel.Right - 44, panel.Top + 20 + rowH * (i + 1));
            }
        }

        if (place.Stale)
        {
            StaleBanner(x, rep);
        }
    }

    private void Hourly(Ctx x)
    {
        var rep = x.Report;
        if (x.R.Place is null || rep is null)
        {
            Unavailable(x, "Waiting for the first forecast", x.R.Place?.Location.Name);
            return;
        }

        var nowHour = new DateTime(x.NowLocal.Year, x.NowLocal.Month, x.NowLocal.Day, x.NowLocal.Hour, 0, 0);
        var hours = rep.Hourly.Where(h => h.Local >= nowHour).Take(12).ToList();
        if (hours.Count == 0)
        {
            hours = rep.Hourly.TakeLast(12).ToList();
        }

        if (hours.Count == 0)
        {
            Unavailable(x, "No hourly forecast", x.R.Place.Location.Name);
            return;
        }

        var left = 80f;
        var right = x.W - 80f;
        var colW = (right - left) / hours.Count;
        var units = x.C.Units;
        for (var i = 0; i < hours.Count; i++)
        {
            var h = hours[i];
            var cx = left + colW * (i + 0.5f);
            var label = i == 0 && h.Local == nowHour ? "Now" : Units.Hour(h.Local, x.C.TwentyFourHourClock);
            Text(x, label, cx, 215, 30, i == 0 ? FontWeight.SemiBold : FontWeight.Regular, i == 0 ? x.T.Text : x.T.Secondary, SKTextAlign.Center);
            WeatherIcons.Draw(x.Canvas, WmoWeather.Icon(h.WeatherCode), h.IsDay, cx, 320, Math.Min(110, colW * 0.8f), x.T.Icons);
            Text(x, Units.Temperature(h.TemperatureC, units), cx, 455, 50, FontWeight.SemiBold, x.T.Text, SKTextAlign.Center);
            if (h.PrecipitationProbabilityPercent > 0)
            {
                Text(x, h.PrecipitationProbabilityPercent.ToString(CultureInfo.InvariantCulture) + "%", cx, 505, 28, FontWeight.Regular, x.T.Cool, SKTextAlign.Center);
            }

            if (i > 0)
            {
                Line(x, left + colW * i, 190, left + colW * i, 520, 0x18);
            }
        }

        // Temperature curve across the bottom of the body.
        var temps = hours.Select(h => units == WeatherUnits.Imperial ? Units.CToF(h.TemperatureC) : h.TemperatureC).ToList();
        var min = temps.Min();
        var max = temps.Max();
        if (max - min < 4)
        {
            max = min + 4;
        }

        const float top = 600f;
        const float bottom = 880f;
        var pts = new SKPoint[temps.Count];
        for (var i = 0; i < temps.Count; i++)
        {
            var cx = left + colW * (i + 0.5f);
            var cy = (float)(bottom - (temps[i] - min) / (max - min) * (bottom - top - 40) - 20);
            pts[i] = new SKPoint(cx, cy);
        }

        using var curve = new SKPath();
        curve.MoveTo(pts[0]);
        for (var i = 1; i < pts.Length; i++)
        {
            var prev = pts[i - 1];
            var cur = pts[i];
            var midX = (prev.X + cur.X) / 2;
            curve.CubicTo(midX, prev.Y, midX, cur.Y, cur.X, cur.Y);
        }

        using (var area = new SKPath(curve))
        {
            area.LineTo(pts[^1].X, bottom + 10);
            area.LineTo(pts[0].X, bottom + 10);
            area.Close();
            using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
            fill.Shader = SKShader.CreateLinearGradient(new SKPoint(0, top), new SKPoint(0, bottom + 10), new[] { x.T.Accent.WithAlpha(0x60), x.T.Accent.WithAlpha(0x05) }, SKShaderTileMode.Clamp);
            x.Canvas.DrawPath(area, fill);
        }

        using (var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 5, Color = x.T.Accent, StrokeCap = SKStrokeCap.Round })
        {
            x.Canvas.DrawPath(curve, stroke);
        }

        using (var dot = new SKPaint { IsAntialias = true, Color = x.T.Text })
        {
            foreach (var p in pts)
            {
                x.Canvas.DrawCircle(p, 7, dot);
            }
        }

        if (x.R.Place.Stale)
        {
            StaleBanner(x, rep);
        }
    }

    private void Daily(Ctx x)
    {
        var rep = x.Report;
        if (x.R.Place is null || rep is null)
        {
            Unavailable(x, "Waiting for the first forecast", x.R.Place?.Location.Name);
            return;
        }

        var days = rep.Daily.Where(d => d.Date.Date >= x.NowLocal.Date).Take(7).ToList();
        if (days.Count == 0)
        {
            days = rep.Daily.Take(7).ToList();
        }

        if (days.Count == 0)
        {
            Unavailable(x, "No daily forecast", x.R.Place.Location.Name);
            return;
        }

        var left = 80f;
        var right = x.W - 80f;
        var colW = (right - left) / days.Count;
        var units = x.C.Units;
        for (var i = 0; i < days.Count; i++)
        {
            var d = days[i];
            var cx = left + colW * (i + 0.5f);
            Text(x, Units.DayName(d.Date, x.NowLocal), cx, 222, 38, FontWeight.SemiBold, x.T.Text, SKTextAlign.Center, maxWidth: colW - 20);
            Text(x, d.Date.ToString("MMM d", CultureInfo.InvariantCulture), cx, 258, 26, FontWeight.Regular, x.T.Secondary, SKTextAlign.Center);
            WeatherIcons.Draw(x.Canvas, WmoWeather.Icon(d.WeatherCode), true, cx, 400, Math.Min(170, colW * 0.75f), x.T.Icons);
            Text(x, WmoWeather.Short(d.WeatherCode), cx, 522, 28, FontWeight.Regular, x.T.Secondary, SKTextAlign.Center, maxWidth: colW - 16);
            Text(x, Units.Temperature(d.HighC, units), cx, 640, 64, FontWeight.Bold, x.T.Accent, SKTextAlign.Center);
            Text(x, Units.Temperature(d.LowC, units), cx, 700, 42, FontWeight.Regular, x.T.Low, SKTextAlign.Center);
            if (d.PrecipitationProbabilityPercent > 0)
            {
                Text(x, d.PrecipitationProbabilityPercent.ToString(CultureInfo.InvariantCulture) + "%", cx, 780, 30, FontWeight.Regular, x.T.Cool, SKTextAlign.Center);
            }

            if (d.PrecipitationMm >= 0.1)
            {
                Text(x, Units.Precipitation(d.PrecipitationMm, units), cx, 820, 26, FontWeight.Regular, x.T.Secondary, SKTextAlign.Center);
            }

            if (i > 0)
            {
                Line(x, left + colW * i, 190, left + colW * i, 860, 0x18);
            }
        }

        if (x.R.Place.Stale)
        {
            StaleBanner(x, rep);
        }
    }

    private void Travel(Ctx x)
    {
        var places = x.R.All;
        if (places.Count == 0)
        {
            Unavailable(x, "No places configured", null);
            return;
        }

        var units = x.C.Units;
        var columns = places.Count > 4 ? 2 : 1;
        var perColumn = (int)Math.Ceiling(places.Count / (double)columns);
        var left = 80f;
        var colW = (x.W - 160f) / columns;
        // Rows fill the space between the header and the band; a short list is centred in it rather than
        // stacked at the top with empty space below.
        var areaTop = 180f;
        var areaH = BandTop - 40 - areaTop;
        var rowH = Math.Min(190f, areaH / perColumn);
        var top = areaTop + (areaH - rowH * perColumn) / 2f;

        for (var i = 0; i < places.Count; i++)
        {
            var col = i / perColumn;
            var row = i % perColumn;
            var px = left + colW * col;
            var py = top + rowH * row;
            var p = places[i];
            var rep = p.Report;

            var nameY = py + rowH * 0.46f;
            Text(x, p.Location.Name, px + rowH * 0.9f, nameY, Math.Min(46, rowH * 0.3f), FontWeight.SemiBold, x.T.Text, maxWidth: colW * 0.5f);
            if (rep is not null)
            {
                var cur = rep.Current;
                WeatherIcons.Draw(x.Canvas, WmoWeather.Icon(cur.WeatherCode), cur.IsDay, px + rowH * 0.4f, py + rowH * 0.5f, rowH * 0.62f, x.T.Icons);
                Text(x, WmoWeather.Short(cur.WeatherCode), px + rowH * 0.9f, nameY + rowH * 0.24f, Math.Min(30, rowH * 0.19f), FontWeight.Regular, x.T.Secondary, maxWidth: colW * 0.5f);
                Text(x, Units.Temperature(cur.TemperatureC, units), px + colW - 40, py + rowH * 0.56f, Math.Min(66, rowH * 0.42f), FontWeight.Bold, x.T.Accent, SKTextAlign.Right);
                var today = rep.Daily.FirstOrDefault();
                if (today is not null)
                {
                    Text(x, Units.Temperature(today.HighC, units) + " / " + Units.Temperature(today.LowC, units), px + colW - 40, py + rowH * 0.56f + rowH * 0.24f, Math.Min(28, rowH * 0.18f), FontWeight.Regular, x.T.Secondary, SKTextAlign.Right);
                }

                if (p.Stale)
                {
                    Text(x, "not current", px + colW - 40, py + rowH * 0.56f + rowH * 0.42f, Math.Min(22, rowH * 0.14f), FontWeight.Regular, x.T.Accent.WithAlpha(0xCC), SKTextAlign.Right);
                }
            }
            else
            {
                WeatherIcons.Draw(x.Canvas, WeatherIcon.Unknown, true, px + rowH * 0.4f, py + rowH * 0.5f, rowH * 0.62f, x.T.Icons);
                Text(x, "No forecast yet", px + rowH * 0.9f, nameY + rowH * 0.24f, Math.Min(30, rowH * 0.19f), FontWeight.Regular, x.T.Secondary);
            }

            if (row < perColumn - 1)
            {
                Line(x, px, py + rowH, px + colW - 40, py + rowH, 0x22);
            }
        }
    }

    private void Unavailable(Ctx x, string message, string? place)
    {
        WeatherIcons.Draw(x.Canvas, WeatherIcon.Unknown, true, x.W / 2, 440, 260, x.T.Icons);
        Text(x, message, x.W / 2, 660, 56, FontWeight.SemiBold, x.T.Text, SKTextAlign.Center);
        if (place is not null)
        {
            Text(x, place, x.W / 2, 720, 34, FontWeight.Regular, x.T.Secondary, SKTextAlign.Center);
        }
    }

    private void StaleBanner(Ctx x, WeatherReport rep)
    {
        var text = "Forecast not updated since " + Units.Clock(rep.FetchedUtc.AddSeconds(rep.UtcOffsetSeconds), x.C.TwentyFourHourClock) + " · showing the last report";
        using var paint = new SKPaint { IsAntialias = true, Color = x.T.Accent.WithAlpha(0x33) };
        x.Canvas.DrawRoundRect(new SKRect(80, 160, x.W - 80, 206), 10, 10, paint);
        Text(x, text, x.W / 2, 192, 26, FontWeight.SemiBold, x.T.Text, SKTextAlign.Center);
    }

    // ---- Primitives -------------------------------------------------------------------------

    private void Panel(Ctx x, SKRect rect)
    {
        using var paint = new SKPaint { IsAntialias = true, Color = x.T.Panel };
        x.Canvas.DrawRoundRect(rect, 24, 24, paint);
    }

    private static void Line(Ctx x, float x1, float y1, float x2, float y2, byte alpha = 0)
    {
        using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2, Color = alpha == 0 ? x.T.Line : x.T.Line.WithAlpha(alpha) };
        x.Canvas.DrawLine(x1, y1, x2, y2, paint);
    }

    /// <summary>Draws one line of text at a baseline; text wider than <paramref name="maxWidth"/> is shortened with an ellipsis.</summary>
    private void Text(Ctx x, string text, float px, float baseline, float size, FontWeight weight, SKColor color, SKTextAlign align = SKTextAlign.Left, float maxWidth = 0)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        using var font = new SKFont(_assets.Typeface(weight), size) { Edging = SKFontEdging.SubpixelAntialias, Subpixel = true };
        using var paint = new SKPaint { IsAntialias = true, Color = color };
        if (maxWidth > 0 && font.MeasureText(text) > maxWidth)
        {
            var shortened = text;
            while (shortened.Length > 1 && font.MeasureText(shortened + "…") > maxWidth)
            {
                shortened = shortened[..^1].TrimEnd();
            }

            text = shortened + "…";
        }

        x.Canvas.DrawText(text, px, baseline, align, font, paint);
    }
}
