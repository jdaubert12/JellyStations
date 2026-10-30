using System.Globalization;
using Jellyfin.Plugin.JellyStations.Models;
using Jellyfin.Plugin.JellyStations.Scheduling;
using SkiaSharp;

namespace Jellyfin.Plugin.JellyStations.Weather.Screens;

/// <summary>The title card that opens a place, the alerts screen, and the alert strip the forecast screens carry while an alert is in force.</summary>
public sealed partial class WeatherPainter
{
    /// <summary>At most this many alerts on the screen; the rest are counted.</summary>
    public const int MaxAlertsOnScreen = 3;

    /// <summary>The colour an alert's severity is shown in: the NWS's own Extreme/Severe/Moderate/Minor ramp, muted enough to sit on any theme.</summary>
    public static SKColor AlertColor(string? severity) => severity?.ToLowerInvariant() switch
    {
        "extreme" => new SKColor(0xD5, 0x1F, 0x5B),
        "severe" => new SKColor(0xE0, 0x3A, 0x2F),
        "moderate" => new SKColor(0xF0, 0x8C, 0x1A),
        "minor" => new SKColor(0xE8, 0xC2, 0x2A),
        _ => new SKColor(0x8E, 0x9A, 0xB0)
    };

    // ---- Title card -------------------------------------------------------------------------

    private void TitleCard(Ctx x)
    {
        var place = x.R.Place;
        var name = place?.Location.Name ?? "Weather";
        var region = place?.Location.Region ?? string.Empty;
        var rep = x.Report;

        // A faint, large version of the current conditions icon behind the name gives the card some weather.
        if (rep is not null)
        {
            using (new SaveLayer(x.Canvas, 0x30))
            {
                WeatherIcons.Draw(x.Canvas, WmoWeather.Icon(rep.Current.WeatherCode), rep.Current.IsDay, x.W * 0.5f, 430, 620, x.T.Icons);
            }
        }

        var centre = BandTop / 2f + 20;
        Line(x, x.W * 0.3f, centre - 150, x.W * 0.7f, centre - 150);
        Text(x, name, x.W / 2, centre + 10, 150, FontWeight.Bold, x.T.Text, SKTextAlign.Center, maxWidth: x.W - 160);
        if (region.Length > 0)
        {
            Text(x, region, x.W / 2, centre + 80, 46, FontWeight.Regular, x.T.Secondary, SKTextAlign.Center, maxWidth: x.W - 160);
        }

        Line(x, x.W * 0.3f, centre + 130, x.W * 0.7f, centre + 130);

        if (rep is not null && !(place?.Stale ?? false))
        {
            var now = Units.Temperature(rep.Current.TemperatureC, x.C.Units) + " · " + WmoWeather.Describe(rep.Current.WeatherCode);
            Text(x, now, x.W / 2, centre + 200, 38, FontWeight.SemiBold, x.T.Accent, SKTextAlign.Center, maxWidth: x.W - 160);
        }

        Text(x, x.LongDate, x.W / 2, centre + 256, 30, FontWeight.Regular, x.T.Secondary, SKTextAlign.Center);
    }

    /// <summary>A canvas layer at an opacity, restored on dispose.</summary>
    private readonly struct SaveLayer : IDisposable
    {
        private readonly SKCanvas _canvas;

        public SaveLayer(SKCanvas canvas, byte alpha)
        {
            _canvas = canvas;
            using var paint = new SKPaint { Color = new SKColor(0xFF, 0xFF, 0xFF, alpha) };
            canvas.SaveLayer(paint);
        }

        public void Dispose() => _canvas.Restore();
    }

    // ---- Alerts screen ----------------------------------------------------------------------

    private void Alerts(Ctx x)
    {
        var place = x.R.Place;
        if (place is null)
        {
            Unavailable(x, "No place", null);
            return;
        }

        var alerts = place.ActiveAlerts;
        if (alerts.Count == 0)
        {
            Unavailable(x, "No active alerts", place.Location.Name);
            return;
        }

        var shown = alerts.Take(MaxAlertsOnScreen).ToList();
        var more = alerts.Count - shown.Count;
        const float top = 172f;
        var bottom = BandTop - 44f;
        if (!string.IsNullOrEmpty(place.AlertAttribution))
        {
            Text(x, place.AlertAttribution, x.W - 80, bottom + 4, 22, FontWeight.Regular, x.T.Secondary, SKTextAlign.Right);
            bottom -= 30;
        }

        const float gap = 18f;
        var panelH = (bottom - top - (gap * (shown.Count - 1))) / shown.Count;
        for (var i = 0; i < shown.Count; i++)
        {
            var rect = new SKRect(80, top + ((panelH + gap) * i), x.W - 80, top + ((panelH + gap) * i) + panelH);
            AlertPanel(x, shown[i], rect, shown.Count == 1, i == shown.Count - 1 ? more : 0);
        }
    }

    /// <summary>One alert in a panel: a colour bar for the severity, the event, when it runs, then as much of the text as fits.</summary>
    private void AlertPanel(Ctx x, WeatherAlert a, SKRect rect, bool single, int more)
    {
        Panel(x, rect);
        var color = AlertColor(a.Severity);
        using (var bar = new SKPaint { IsAntialias = true, Color = color })
        {
            x.Canvas.Save();
            x.Canvas.ClipRoundRect(new SKRoundRect(rect, 24, 24));
            x.Canvas.DrawRect(rect.Left, rect.Top, 22, rect.Height, bar);
            x.Canvas.Restore();
        }

        var left = rect.Left + 60;
        var right = rect.Right - 44;
        var titleSize = single ? 72f : 48f;
        var y = rect.Top + (single ? 96f : 62f);
        var whenWidth = single ? 700f : 600f;
        WarningGlyph(x, left + (titleSize * 0.42f), y - (titleSize * 0.36f), titleSize * 0.46f, color);
        Text(x, a.Event, left + (titleSize * 1.05f), y, titleSize, FontWeight.Bold, x.T.Text, maxWidth: right - left - (titleSize * 1.05f) - whenWidth - 24);

        var when = When(x, a);
        Text(x, when, right, y, single ? 34f : 28f, FontWeight.SemiBold, x.T.Accent, SKTextAlign.Right, maxWidth: whenWidth);

        var area = a.Area;
        if (!string.IsNullOrEmpty(area))
        {
            y += single ? 46f : 34f;
            Text(x, area, left, y, single ? 28f : 23f, FontWeight.Regular, x.T.Secondary, maxWidth: right - left);
        }

        // The body: the headline's own words would repeat the event and the times, so the description comes first.
        var bodySize = single ? 32f : 26f;
        var lineH = bodySize * 1.3f;
        y += single ? 60f : 42f;
        var reserve = more > 0 ? 34f : 0f;
        var maxLines = (int)Math.Floor((rect.Bottom - 26 - reserve - y) / lineH) + 1;
        if (maxLines <= 0)
        {
            return;
        }

        // The description keeps a blank line between its paragraphs; the instruction follows one blank line, its paragraphs run on.
        var lines = new List<(string Text, bool Instruction)>();
        void AddParagraphs(string text, bool instruction, bool blankBetween)
        {
            foreach (var paragraph in text.Split('\n'))
            {
                if (blankBetween && lines.Count > 0)
                {
                    lines.Add((string.Empty, false));
                }

                foreach (var line in Wrap(paragraph, bodySize, instruction ? FontWeight.SemiBold : FontWeight.Regular, right - left))
                {
                    lines.Add((line, instruction));
                }
            }
        }

        AddParagraphs(a.Description ?? a.Headline, instruction: false, blankBetween: true);
        if (!string.IsNullOrEmpty(a.Instruction))
        {
            lines.Add((string.Empty, false));
            AddParagraphs(a.Instruction, instruction: true, blankBetween: false);
        }

        var cut = lines.Count > maxLines;
        var show = cut ? maxLines - 1 : lines.Count;
        for (var i = 0; i < show; i++)
        {
            var (text, instruction) = lines[i];
            if (text.Length > 0)
            {
                Text(x, text, left, y, bodySize, instruction ? FontWeight.SemiBold : FontWeight.Regular, instruction ? x.T.Text : x.T.Text.WithAlpha(0xE6));
            }

            y += lineH;
        }

        if (cut)
        {
            Text(x, "…", left, y, bodySize, FontWeight.Regular, x.T.Secondary);
        }

        if (more > 0)
        {
            Text(x, "+ " + more.ToString(CultureInfo.InvariantCulture) + (more == 1 ? " more alert" : " more alerts"), right, rect.Bottom - 22, 26, FontWeight.SemiBold, x.T.Accent, SKTextAlign.Right);
        }
    }

    /// <summary>"Until Thu 4:00 PM", "From 7:00 AM until Fri 1:00 AM" or "In effect", in the place's zone.</summary>
    private static string When(Ctx x, WeatherAlert a)
    {
        string Stamp(DateTime utc) => Units.DayClock(utc + x.Offset, x.NowLocal, x.C.TwentyFourHourClock);

        var start = a.StartUtc is { } s && s > x.R.NowUtc ? "From " + Stamp(s) : null;
        var end = a.EndUtc ?? a.ExpiresUtc;
        if (start is not null && end is not null)
        {
            return start + " until " + Stamp(end.Value);
        }

        if (start is not null)
        {
            return start;
        }

        return end is not null ? "Until " + Stamp(end.Value) : "In effect";
    }

    /// <summary>A short line for the strip and the status table: the strongest alert and when it ends, plus how many more.</summary>
    public static string Summary(IReadOnlyList<WeatherAlert> alerts, DateTime nowUtc, long offsetSeconds, bool twentyFourHour)
    {
        if (alerts.Count == 0)
        {
            return string.Empty;
        }

        var a = alerts[0];
        var text = a.Event;
        var end = a.EndUtc ?? a.ExpiresUtc;
        if (end is { } e)
        {
            text += " until " + Units.DayClock(e.AddSeconds(offsetSeconds), nowUtc.AddSeconds(offsetSeconds), twentyFourHour);
        }

        if (alerts.Count > 1)
        {
            text += " · " + (alerts.Count - 1).ToString(CultureInfo.InvariantCulture) + " more";
        }

        return text;
    }

    /// <summary>A slim strip under the header on the forecast screens while an alert is in force; the stale banner takes the spot when the forecast is old.</summary>
    private void AlertStrip(Ctx x)
    {
        var place = x.R.Place;
        if (place is null || place.ActiveAlerts.Count == 0 || !x.C.ShowAlerts)
        {
            return;
        }

        var a = place.ActiveAlerts[0];
        var color = AlertColor(a.Severity);
        var text = Summary(place.ActiveAlerts, x.R.NowUtc, (long)x.Offset.TotalSeconds, x.C.TwentyFourHourClock);
        FillRoundRect(x, new SKRect(80, 153, x.W - 80, 193), 10, color.WithAlpha(0x55));

        WarningGlyph(x, 112, 173, 20, color);
        Text(x, text, 142, 182, 25, FontWeight.SemiBold, x.T.Text, maxWidth: x.W - 240);
    }

    /// <summary>A warning triangle with an exclamation mark, centred on (cx, cy), <paramref name="size"/> high.</summary>
    private void WarningGlyph(Ctx x, float cx, float cy, float size, SKColor color)
    {
        var h = size;
        var w = size * 1.12f;
        using var path = new SKPath();
        path.MoveTo(cx, cy - (h / 2));
        path.LineTo(cx + (w / 2), cy + (h / 2));
        path.LineTo(cx - (w / 2), cy + (h / 2));
        path.Close();
        using var fill = new SKPaint { IsAntialias = true, Color = color, StrokeJoin = SKStrokeJoin.Round, Style = SKPaintStyle.StrokeAndFill, StrokeWidth = size * 0.12f };
        x.Canvas.DrawPath(path, fill);
        using var mark = new SKPaint { IsAntialias = true, Color = x.T.Band, Style = SKPaintStyle.Fill };
        var mw = size * 0.11f;
        x.Canvas.DrawRoundRect(new SKRect(cx - mw, cy - (h * 0.18f), cx + mw, cy + (h * 0.2f)), mw, mw, mark);
        x.Canvas.DrawCircle(cx, cy + (h * 0.36f), mw * 1.15f, mark);
    }

    /// <summary>Breaks a paragraph into lines no wider than <paramref name="maxWidth"/> at word boundaries; a word wider than the line is cut.</summary>
    private IReadOnlyList<string> Wrap(string text, float size, FontWeight weight, float maxWidth)
    {
        var lines = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return lines;
        }

        using var font = new SKFont(_assets.Typeface(weight), size);
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var line = string.Empty;
        foreach (var word in words)
        {
            var candidate = line.Length == 0 ? word : line + " " + word;
            if (font.MeasureText(candidate) <= maxWidth)
            {
                line = candidate;
                continue;
            }

            if (line.Length > 0)
            {
                lines.Add(line);
            }

            line = word;
            while (font.MeasureText(line) > maxWidth && line.Length > 1)
            {
                var fit = line.Length - 1;
                while (fit > 1 && font.MeasureText(line[..fit]) > maxWidth)
                {
                    fit--;
                }

                lines.Add(line[..fit]);
                line = line[fit..];
            }
        }

        if (line.Length > 0)
        {
            lines.Add(line);
        }

        return lines;
    }
}
