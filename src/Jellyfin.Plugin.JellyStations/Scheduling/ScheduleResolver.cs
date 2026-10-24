using System.Collections.Concurrent;
using Jellyfin.Plugin.JellyStations.Models;

namespace Jellyfin.Plugin.JellyStations.Scheduling;

public interface IScheduleResolver
{
    /// <summary>What is airing on the station at the given UTC instant (null if the station is unknown).</summary>
    ResolvedProgram? Resolve(Guid stationId, DateTime utc);

    /// <summary>All entries overlapping [fromUtc, toUtc), contiguous and ordered (empty if unknown).</summary>
    IReadOnlyList<ScheduleEntry> Enumerate(Guid stationId, DateTime fromUtc, DateTime toUtc);

    /// <summary>Drops cached state for a station (call after any revision change).</summary>
    void Invalidate(Guid stationId);
}

/// <summary>Builds and caches one scheduler per (station, revision) from a ScheduleInput provider.</summary>
public sealed class ScheduleResolver : IScheduleResolver
{
    private readonly Func<Guid, ScheduleInput?> _inputProvider;
    private readonly ConcurrentDictionary<Guid, (int Revision, IStationScheduler Scheduler)> _cache = new();

    public ScheduleResolver(Func<Guid, ScheduleInput?> inputProvider)
    {
        _inputProvider = inputProvider;
    }

    public ResolvedProgram? Resolve(Guid stationId, DateTime utc)
    {
        return GetScheduler(stationId)?.Resolve(EnsureUtc(utc));
    }

    public IReadOnlyList<ScheduleEntry> Enumerate(Guid stationId, DateTime fromUtc, DateTime toUtc)
    {
        var s = GetScheduler(stationId);
        return s is null ? Array.Empty<ScheduleEntry>() : s.Enumerate(EnsureUtc(fromUtc), EnsureUtc(toUtc)).ToList();
    }

    public void Invalidate(Guid stationId) => _cache.TryRemove(stationId, out _);

    /// <summary>Enumerates a schedule for an arbitrary input (used for previews of unsaved drafts).</summary>
    public static IReadOnlyList<ScheduleEntry> EnumerateInput(ScheduleInput input, DateTime fromUtc, DateTime toUtc)
    {
        return Create(input).Enumerate(EnsureUtc(fromUtc), EnsureUtc(toUtc)).ToList();
    }

    public static ResolvedProgram? ResolveInput(ScheduleInput input, DateTime utc) => Create(input).Resolve(EnsureUtc(utc));

    private IStationScheduler? GetScheduler(Guid stationId)
    {
        var input = _inputProvider(stationId);
        if (input is null)
        {
            _cache.TryRemove(stationId, out _);
            return null;
        }

        if (_cache.TryGetValue(stationId, out var cached) && cached.Revision == input.Revision)
        {
            return cached.Scheduler;
        }

        var scheduler = Create(input);
        _cache[stationId] = (input.Revision, scheduler);
        return scheduler;
    }

    private static IStationScheduler Create(ScheduleInput input)
    {
        if (input.Mode == ScheduleMode.Weather)
        {
            return new WeatherScheduler(input);
        }

        return input.HasBlocks
            ? new WeeklySlotScheduler(input)
            : new CyclicScheduler(input);
    }

    private static DateTime EnsureUtc(DateTime dt) => dt.Kind == DateTimeKind.Utc ? dt : DateTime.SpecifyKind(dt.ToUniversalTime(), DateTimeKind.Utc);
}
