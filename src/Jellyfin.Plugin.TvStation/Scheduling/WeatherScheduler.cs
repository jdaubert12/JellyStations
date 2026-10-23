using Jellyfin.Plugin.TvStation.Models;

namespace Jellyfin.Plugin.TvStation.Scheduling;

/// <summary>
/// Weather mode: one guide entry per <see cref="WeatherConfig.GuideEntryMinutes"/>, aligned to the station's local
/// clock (so hourly entries start on the hour), titled from the config's template. The screen cycle runs inside
/// each entry (<see cref="WeatherCycle"/>); the guide reads like a channel instead of four hundred screens.
/// </summary>
internal sealed class WeatherScheduler : IStationScheduler
{
    private readonly ScheduleInput _input;
    private readonly WeatherConfig _config;
    private readonly long _gridTicks;
    private readonly TimeZoneInfo _tz;
    private readonly string _title;

    public WeatherScheduler(ScheduleInput input)
    {
        _input = input;
        _config = input.Weather ?? new WeatherConfig();
        _gridTicks = TimeSpan.FromMinutes(Math.Clamp(_config.GuideEntryMinutes, 15, 240)).Ticks;
        _tz = WeeklySlotScheduler.ResolveTimeZone(input.TimeZoneId);
        _title = Title(_config);
    }

    /// <summary>"Local weather: Boston, Denver, Austin" or whatever the template says.</summary>
    public static string Title(WeatherConfig config)
    {
        var places = string.Join(", ", config.Locations.Where(l => l.IsValid).Select(l => l.Name));
        var title = config.EffectiveGuideTitle.Replace("{places}", places, StringComparison.OrdinalIgnoreCase).Trim();
        return title.Length == 0 ? "Local weather" : title;
    }

    public ResolvedProgram? Resolve(DateTime utc)
    {
        var entry = EntryAt(utc);
        return new ResolvedProgram(entry, utc - entry.StartUtc);
    }

    public IEnumerable<ScheduleEntry> Enumerate(DateTime fromUtc, DateTime toUtc)
    {
        if (toUtc <= fromUtc)
        {
            yield break;
        }

        var entry = EntryAt(fromUtc);
        var guard = 0;
        while (entry.StartUtc < toUtc && guard++ < 100000)
        {
            yield return entry;
            entry = EntryAt(entry.EndUtc);
        }
    }

    /// <summary>The entry containing <paramref name="utc"/>: the grid is laid on the station's local clock.</summary>
    public ScheduleEntry EntryAt(DateTime utc)
    {
        var offset = _tz.GetUtcOffset(utc).Ticks;
        var start = new DateTime(Padding.FloorDiv(utc.Ticks + offset, _gridTicks) * _gridTicks - offset, DateTimeKind.Utc);
        var end = new DateTime(start.Ticks + _gridTicks, DateTimeKind.Utc);

        // Across a daylight-saving change the local grid shifts; keep the next entry aligned to the new offset.
        var offsetAtEnd = _tz.GetUtcOffset(end).Ticks;
        if (offsetAtEnd != offset)
        {
            var realigned = new DateTime(Padding.FloorDiv(end.Ticks + offsetAtEnd, _gridTicks) * _gridTicks - offsetAtEnd, DateTimeKind.Utc);
            if (realigned > start)
            {
                end = realigned;
            }
        }

        return new ScheduleEntry(_input.StationId, _input.Revision, EntryKind.Weather, null, start, end, false, _title);
    }
}
