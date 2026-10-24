namespace Jellyfin.Plugin.JellyStations.Scheduling;

/// <summary>A deterministic timeline for one station at one revision.</summary>
internal interface IStationScheduler
{
    /// <summary>Returns the entry airing at the given UTC instant, or null if the station has no content.</summary>
    ResolvedProgram? Resolve(DateTime utc);

    /// <summary>Returns all entries overlapping [fromUtc, toUtc), contiguous and ordered.</summary>
    IEnumerable<ScheduleEntry> Enumerate(DateTime fromUtc, DateTime toUtc);
}
