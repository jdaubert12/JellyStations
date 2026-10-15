using Jellyfin.Plugin.TvStation.Models;

namespace Jellyfin.Plugin.TvStation.Scheduling;

/// <summary>Decides whether a station is "in season" from its recurring yearly windows.</summary>
public static class SeasonWindows
{
    public static bool IsSeasonal(Station station) => station.ActiveWindows is { Count: > 0 };

    /// <summary>True when the station has no windows, or the local date (station time zone) falls inside one of them.</summary>
    public static bool IsActive(Station station, DateTime utcNow)
    {
        if (!IsSeasonal(station))
        {
            return true;
        }

        var local = ToLocalDate(station, utcNow);
        return station.ActiveWindows.Any(w => Contains(w, local.Month, local.Day));
    }

    /// <summary>The next local date on which the active state flips, or null when the station is not seasonal or never flips.</summary>
    public static DateOnly? NextChange(Station station, DateTime utcNow)
    {
        if (!IsSeasonal(station))
        {
            return null;
        }

        var today = ToLocalDate(station, utcNow);
        var state = station.ActiveWindows.Any(w => Contains(w, today.Month, today.Day));
        for (var i = 1; i <= 366; i++)
        {
            var d = today.AddDays(i);
            if (station.ActiveWindows.Any(w => Contains(w, d.Month, d.Day)) != state)
            {
                return d;
            }
        }

        return null;
    }

    public static bool Contains(SeasonWindow w, int month, int day)
    {
        var key = (month * 100) + day;
        var start = (w.StartMonth * 100) + w.StartDay;
        var end = (w.EndMonth * 100) + w.EndDay;
        return start <= end ? key >= start && key <= end : key >= start || key <= end;
    }

    public static bool IsValid(SeasonWindow w)
        => w.StartMonth is >= 1 and <= 12 && w.EndMonth is >= 1 and <= 12
           && w.StartDay >= 1 && w.StartDay <= DateTime.DaysInMonth(2024, w.StartMonth)
           && w.EndDay >= 1 && w.EndDay <= DateTime.DaysInMonth(2024, w.EndMonth);

    private static DateOnly ToLocalDate(Station station, DateTime utcNow)
    {
        var tz = WeeklySlotScheduler.ResolveTimeZone(station.TimeZoneId);
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), tz);
        return DateOnly.FromDateTime(local);
    }
}
