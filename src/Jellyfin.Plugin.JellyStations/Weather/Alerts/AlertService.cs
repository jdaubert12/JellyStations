using System.Collections.Concurrent;
using Jellyfin.Plugin.JellyStations.Models;
using Jellyfin.Plugin.JellyStations.Scheduling;
using Jellyfin.Plugin.JellyStations.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyStations.Weather.Alerts;

/// <summary>What one place's last alert fetch brought back. Serialized to disk so a restart has it until the next poll.</summary>
public sealed class AlertSet
{
    public string SourceId { get; set; } = string.Empty;

    public string SourceName { get; set; } = string.Empty;

    public string Attribution { get; set; } = string.Empty;

    public DateTime FetchedUtc { get; set; }

    public List<WeatherAlert> Alerts { get; set; } = new();

    /// <summary>The alerts still in force at <paramref name="nowUtc"/>, in the source's order (strongest first).</summary>
    public IReadOnlyList<WeatherAlert> Active(DateTime nowUtc) => Alerts.Where(a => a.IsActive(nowUtc)).ToList();
}

/// <summary>How the last alert fetches for one place went.</summary>
public sealed class AlertFetchStatus : FetchStatus
{
    /// <summary>The source asked, or null when none covers the place.</summary>
    public string? SourceName { get; set; }

    /// <summary>The source said it does not cover the place (at these coordinates); not asked again until they change.</summary>
    public bool NotCovered { get; set; }

    /// <summary>The coordinates the last attempt was for, so a moved place is asked again.</summary>
    public double Latitude { get; set; }

    public double Longitude { get; set; }
}

/// <summary>
/// Keeps the watches, warnings and advisories in force for every place on every enabled weather station that shows
/// them: every <see cref="Interval"/> it asks the place's source (with backoff after failures), caches the answer on
/// disk so a restart has it, and tells the channel which places have something to show. Nothing is fetched for a
/// station nobody enabled, and a place no source covers is left alone.
/// </summary>
public sealed class AlertService : LocationPoller
{
    /// <summary>Warnings can be issued at any minute; five minutes is the NWS's own suggested polling rate.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private readonly JellyStationsPaths _paths;
    private readonly IReadOnlyList<IAlertSource> _sources;
    private readonly ILogger<AlertService> _logger;
    private readonly ConcurrentDictionary<Guid, AlertSet> _sets = new();
    private readonly ConcurrentDictionary<Guid, AlertFetchStatus> _status = new();

    public AlertService(StationRepository stations, JellyStationsPaths paths, IEnumerable<IAlertSource> sources, ILogger<AlertService> logger)
        : base(stations, logger, "alerts", startDelay: TimeSpan.FromSeconds(4), nudgeDelay: TimeSpan.FromSeconds(2))
    {
        _paths = paths;
        _sources = sources.ToList();
        _logger = logger;
    }

    public IReadOnlyList<IAlertSource> Sources => _sources;

    /// <summary>The first source that claims the place, or null when none does.</summary>
    public IAlertSource? SourceFor(WeatherLocation location) => _sources.FirstOrDefault(s => s.Covers(location.Latitude, location.Longitude));

    /// <summary>The last alerts fetched for a place, from memory or from the file left by an earlier run; null when nothing was ever fetched.</summary>
    public AlertSet? Get(Guid locationId)
    {
        if (_sets.TryGetValue(locationId, out var set))
        {
            return set;
        }

        var cached = JsonFileStore.Read<AlertSet>(_paths.AlertFile(locationId));
        if (cached is not null)
        {
            _sets.TryAdd(locationId, cached);
        }

        return cached;
    }

    /// <summary>The alerts in force for a place at <paramref name="nowUtc"/>, strongest first; empty when there are none or nothing was fetched.</summary>
    public IReadOnlyList<WeatherAlert> Active(Guid locationId, DateTime nowUtc) => Get(locationId)?.Active(nowUtc) ?? Array.Empty<WeatherAlert>();

    public AlertFetchStatus Status(Guid locationId) => _status.GetOrAdd(locationId, id => new AlertFetchStatus { LocationId = id });

    /// <summary>Fetches every place of a station now; used by the editor's "Fetch now".</summary>
    public async Task<IReadOnlyList<AlertFetchStatus>> RefreshAsync(Station station, CancellationToken ct)
    {
        var result = new List<AlertFetchStatus>();
        foreach (var location in station.Weather.Locations.Where(l => l.IsValid))
        {
            await FetchAsync(station, location, ct).ConfigureAwait(false);
            result.Add(Status(location.Id));
        }

        return result;
    }

    /// <summary>Due when never fetched, when the last success is older than the interval, when the place moved, or when a failure's backoff has passed. Never for a place the source does not cover.</summary>
    public bool IsDue(WeatherLocation location, DateTime nowUtc)
    {
        var status = Status(location.Id);
        var moved = status.LastAttemptUtc is not null && (Math.Abs(status.Latitude - location.Latitude) > 0.0001 || Math.Abs(status.Longitude - location.Longitude) > 0.0001);
        if (moved)
        {
            return true;
        }

        if (status.NotCovered)
        {
            return false;
        }

        return FetchBackoff.IsDue(status, () => Get(location.Id)?.FetchedUtc, Interval, nowUtc);
    }

    protected override bool Polls(Station station) => station.Weather.ShowAlerts;

    protected override async Task PollAsync(Station station, WeatherLocation location, DateTime nowUtc, CancellationToken ct)
    {
        if (IsDue(location, nowUtc))
        {
            await FetchAsync(station, location, ct).ConfigureAwait(false);
        }
    }

    private async Task FetchAsync(Station station, WeatherLocation location, CancellationToken ct)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var status = Status(location.Id);
            var now = DateTime.UtcNow;
            status.LastAttemptUtc = now;
            status.Latitude = location.Latitude;
            status.Longitude = location.Longitude;
            status.NotCovered = false;
            var source = SourceFor(location);
            if (source is null)
            {
                status.SourceName = null;
                status.LastError = "No alert source covers this place";
                status.NotCovered = true;
                status.Failures = 0;
                _sets.TryRemove(location.Id, out _);
                return;
            }

            status.SourceName = source.Name;
            try
            {
                var alerts = await source.FetchAsync(location, ct).ConfigureAwait(false);
                var set = new AlertSet { SourceId = source.Id, SourceName = source.Name, Attribution = source.Attribution, FetchedUtc = now, Alerts = alerts.ToList() };
                var before = Get(location.Id);
                _sets[location.Id] = set;
                status.MarkSuccess(now);
                try
                {
                    JsonFileStore.Write(_paths.AlertFile(location.Id), set);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "JellyStations alerts: could not cache the alerts for {Place}", location.Name);
                }

                var changed = before is null || !before.Alerts.Select(a => a.Id).SequenceEqual(set.Alerts.Select(a => a.Id));
                if (changed && (alerts.Count > 0 || before is { Alerts.Count: > 0 }))
                {
                    _logger.LogInformation("JellyStations alerts [{Station}]: {Place} now has {Count} alert(s){Which}", station.Name, location.Name, alerts.Count, alerts.Count == 0 ? string.Empty : ": " + string.Join("; ", alerts.Select(a => a.Event)));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (NotCoveredException ex)
            {
                status.NotCovered = true;
                status.LastError = ex.Message;
                status.Failures = 0;
                _sets.TryRemove(location.Id, out _);
                _logger.LogInformation("JellyStations alerts [{Station}]: {Place}: {Error}; the alerts screen is skipped for it", station.Name, location.Name, ex.Message);
            }
            catch (Exception ex)
            {
                status.MarkFailure(ex.Message);
                _logger.LogWarning("JellyStations alerts [{Station}]: fetch for {Place} from {Source} failed ({Count} in a row): {Error}", station.Name, location.Name, source.Id, status.Failures, ex.Message);
            }
        }
        finally
        {
            Gate.Release();
        }
    }
}
