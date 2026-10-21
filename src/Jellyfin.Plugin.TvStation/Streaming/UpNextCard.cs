using System.Globalization;
using Jellyfin.Plugin.TvStation.Models;
using Jellyfin.Plugin.TvStation.Scheduling;

namespace Jellyfin.Plugin.TvStation.Streaming;

/// <summary>What goes on one "Up next" card: the text lines, the artwork and the card's size on the picture.</summary>
public sealed record UpNextCardSpec(
    string Label,
    string Title,
    string? Subtitle,
    string? Times,
    string? ThumbnailPath,
    int Width,
    int Height,
    int Opacity);

/// <summary>The pure parts of the "Up next" card: which program it names, when it shows, and what it says.</summary>
public static class UpNextCard
{
    /// <summary>How far ahead the schedule is searched for the next program.</summary>
    public static readonly TimeSpan Lookahead = TimeSpan.FromHours(6);

    /// <summary>
    /// The next program after <paramref name="afterUtc"/>: bumper clips and slates are skipped, as on the station row.
    /// Null when nothing playable follows within the lookahead.
    /// </summary>
    public static ScheduleEntry? NextProgram(IEnumerable<ScheduleEntry> upcoming, DateTime afterUtc)
        => upcoming.FirstOrDefault(e => e.StartUtc >= afterUtc && e.Kind == EntryKind.Program && e.Item is not null);

    /// <summary>
    /// When the card shows, in seconds since the start of the ffmpeg segment that ends the program: one window per
    /// lead time that still falls inside the segment, earliest first. Empty when the segment is too short for any
    /// (each showing needs a few seconds of program before it, and the card must end with the program).
    /// </summary>
    public static IReadOnlyList<(double From, double To)> Windows(TimeSpan segmentLength, UpNextCardConfig c)
    {
        var length = segmentLength.TotalSeconds;
        var leads = (c.LeadTimesSeconds is { Count: > 0 } ? c.LeadTimesSeconds : new List<int> { UpNextCardConfig.DefaultLeadSeconds })
            .Select(s => Math.Max(5, s))
            .Distinct()
            .OrderByDescending(s => s);
        var show = Math.Clamp(c.ShowSeconds, 3, 60);
        var windows = new List<(double, double)>();
        foreach (var lead in leads)
        {
            if (length < lead + 5)
            {
                continue;
            }

            var from = length - lead;
            windows.Add((Math.Round(from, 3), Math.Round(from + Math.Min(show, lead), 3)));
        }

        return windows;
    }

    /// <summary>The card for <paramref name="next"/>, laid out for the profile's picture size.</summary>
    public static UpNextCardSpec Spec(ScheduleEntry next, UpNextCardConfig c, TimeZoneInfo zone, EncodingProfile p)
    {
        var item = next.Item!;
        var height = Math.Max(24, p.Height * Math.Clamp(c.HeightPercent, 8, 30) / 100);
        var width = Math.Min(p.Width * 45 / 100, height * 42 / 10);
        var thumbnail = c.ShowThumbnail && !string.IsNullOrEmpty(item.PrimaryImagePath) && File.Exists(item.PrimaryImagePath) ? item.PrimaryImagePath : null;
        var textWidth = width - TextX(height, thumbnail is not null) - Pad(height);

        string? subtitle = null;
        if (item.IsSeries)
        {
            var parts = new List<string>();
            if (item.SeasonNumber.HasValue && item.EpisodeNumber.HasValue)
            {
                parts.Add("S" + item.SeasonNumber.Value.ToString(CultureInfo.InvariantCulture) + " E" + item.EpisodeNumber.Value.ToString(CultureInfo.InvariantCulture));
            }

            if (!string.IsNullOrWhiteSpace(item.EpisodeTitle))
            {
                parts.Add(item.EpisodeTitle.Trim());
            }

            subtitle = parts.Count > 0 ? string.Join(" · ", parts) : null;
        }
        else if (item.ProductionYear.HasValue)
        {
            subtitle = item.ProductionYear.Value.ToString(CultureInfo.InvariantCulture);
        }

        return new UpNextCardSpec(
            "UP NEXT",
            Fit(item.Title, textWidth, TitleSize(height)),
            subtitle is null ? null : Fit(subtitle, textWidth, SmallSize(height)),
            c.ShowTimes ? TimeRange(next.StartUtc, next.EndUtc, zone, c.TwentyFourHourClock) : null,
            thumbnail,
            width,
            height,
            Math.Clamp(c.Opacity, 30, 100));
    }

    /// <summary>"8:00 – 9:30 PM", "11:30 PM – 1:00 AM" or "20:00 – 21:30", in the station's time zone.</summary>
    public static string TimeRange(DateTime startUtc, DateTime endUtc, TimeZoneInfo zone, bool twentyFourHour)
    {
        var start = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(startUtc, DateTimeKind.Utc), zone);
        var end = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(endUtc, DateTimeKind.Utc), zone);
        if (twentyFourHour)
        {
            return start.ToString("HH:mm", CultureInfo.InvariantCulture) + " – " + end.ToString("HH:mm", CultureInfo.InvariantCulture);
        }

        var sameHalf = start.ToString("tt", CultureInfo.InvariantCulture) == end.ToString("tt", CultureInfo.InvariantCulture) && (end - start) < TimeSpan.FromHours(12);
        var first = start.ToString(sameHalf ? "h:mm" : "h:mm tt", CultureInfo.InvariantCulture);
        return first + " – " + end.ToString("h:mm tt", CultureInfo.InvariantCulture);
    }

    /// <summary>The station's time zone, or UTC when the id is unknown on this host.</summary>
    public static TimeZoneInfo Zone(string? timeZoneId)
    {
        try
        {
            return string.IsNullOrWhiteSpace(timeZoneId) ? TimeZoneInfo.Utc : TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (Exception)
        {
            return TimeZoneInfo.Utc;
        }
    }

    /// <summary>Cuts a line that would run past the card, with an ellipsis. drawtext does not wrap.</summary>
    public static string Fit(string text, int widthPx, int fontSize)
    {
        var t = text.Trim();
        var max = Math.Max(4, (int)(widthPx / (fontSize * 0.55)));
        return t.Length <= max ? t : t[..(max - 1)].TrimEnd() + "…";
    }

    // Layout, all from the card height so every profile looks the same.
    public static int Pad(int height) => Math.Max(4, height * 7 / 100);

    public static int ThumbBoxWidth(int height) => height * 6 / 5;

    public static int TextX(int height, bool withThumbnail) => Pad(height) + (withThumbnail ? ThumbBoxWidth(height) + Pad(height) : 0);

    public static int LabelSize(int height) => Math.Max(8, height * 14 / 100);

    public static int TitleSize(int height) => Math.Max(10, height * 22 / 100);

    public static int SmallSize(int height) => Math.Max(8, height * 17 / 100);
}
