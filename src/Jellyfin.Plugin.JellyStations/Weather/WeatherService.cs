using System.Collections.Concurrent;
using Jellyfin.Plugin.JellyStations.Models;
using Jellyfin.Plugin.JellyStations.Scheduling;
using Jellyfin.Plugin.JellyStations.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyStations.Weather;

/// <summary>How the last fetches for one place went.</summary>
public sealed class LocationFetchStatus
{
    public Guid LocationId { get; set; }

    public DateTime? LastAttemptUtc { get; set; }

    public DateTime? LastSuccessUtc { get; set; }

    public string? LastError { get; set; }

    /// <summary>Consecutive failures since the last success.</summary>
    public int Failures { get; set; }
}

/// <summary>
/// Keeps a current <see cref="WeatherReport"/> for every place on every enabled weather station: polls the
/// provider every <see cref="Interval"/> (with backoff after failures), caches reports on disk so a restart has
/// something to show, and raises <see cref="Changed"/> when a place's report changes. Only stations that are
/// enabled and in season are polled; nothing is fetched for a station nobody configured.
/// </summary>
public sealed class WeatherService : IDisposable
{
    /// <summary>How often a place is refreshed; Open-Meteo updates current conditions every 15 minutes.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);

    private readonly StationRepository _stations;
    private readonly JellyStationsPaths _paths;
    private readonly IWeatherProvider _provider;
    private readonly ILogger<WeatherService> _logger;
    private readonly ConcurrentDictionary<Guid, WeatherReport> _reports = new();
    private readonly ConcurrentDictionary<Guid, LocationFetchStatus> _status = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private Timer? _timer;
    private int _disposed;
    private int _ticking;

    public WeatherService(StationRepository stations, JellyStationsPaths paths, IWeatherProvider provider, ILogger<WeatherService> logger)
    {
        _stations = stations;
        _paths = paths;
        _provider = provider;
        _logger = logger;
        _stations.Changed += (_, _) => Nudge();
    }

    /// <summary>Raised with the station ids that show a place whose report just changed.</summary>
    public event Action<Guid>? Changed;

    public IWeatherProvider Provider => _provider;

    /// <summary>Starts polling. Called once by the hosted service.</summary>
    public void Start()
    {
        _timer ??= new Timer(_ => _ = TickAsync(), null, TimeSpan.FromSeconds(3), Tick);
    }

    /// <summary>Polls soon (after a station was saved) rather than at the next regular tick.</summary>
    public void Nudge()
    {
        try
        {
            _timer?.Change(TimeSpan.FromSeconds(1), Tick);
        }
        catch (ObjectDisposedException)
        {
            // Shutting down.
        }
    }

    /// <summary>The latest report for a place, from memory or the disk cache; null when nothing was ever fetched.</summary>
    public WeatherReport? Get(Guid locationId)
    {
        if (_reports.TryGetValue(locationId, out var r))
        {
            return r;
        }

        var cached = JsonFileStore.Read<WeatherReport>(_paths.WeatherReportFile(locationId));
        if (cached is not null)
        {
            _reports.TryAdd(locationId, cached);
        }

        return cached;
    }

    public LocationFetchStatus Status(Guid locationId) => _status.GetOrAdd(locationId, id => new LocationFetchStatus { LocationId = id });

    /// <summary>Fetches every place of a station now, in order, and reports per place; used by the editor's "Fetch now" and after a save.</summary>
    public async Task<IReadOnlyList<LocationFetchStatus>> RefreshAsync(Station station, CancellationToken ct)
    {
        var result = new List<LocationFetchStatus>();
        foreach (var location in station.Weather.Locations.Where(l => l.IsValid))
        {
            await FetchAsync(station, location, ct).ConfigureAwait(false);
            result.Add(Status(location.Id));
        }

        return result;
    }

    /// <summary>True when the report is older than the station allows and the screens should say so.</summary>
    public static bool IsStale(WeatherReport? report, WeatherConfig config, DateTime nowUtc)
        => report is null || nowUtc - report.FetchedUtc > TimeSpan.FromHours(Math.Max(1, config.StaleHours));

    private async Task TickAsync()
    {
        if (Volatile.Read(ref _disposed) == 1 || Interlocked.Exchange(ref _ticking, 1) == 1)
        {
            return;
        }

        try
        {
            var now = DateTime.UtcNow;
            foreach (var station in _stations.GetAll().Where(s => s.UsesWeather && s.Enabled && SeasonWindows.IsActive(s, now)))
            {
                foreach (var location in station.Weather.Locations.Where(l => l.IsValid))
                {
                    if (_cts.IsCancellationRequested)
                    {
                        return;
                    }

                    if (IsDue(location.Id, now))
                    {
                        await FetchAsync(station, location, _cts.Token).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "JellyStations weather: poll failed");
        }
        finally
        {
            Volatile.Write(ref _ticking, 0);
        }
    }

    /// <summary>Due when never fetched, when the last success is older than the interval, or when a failure's backoff has passed.</summary>
    public bool IsDue(Guid locationId, DateTime nowUtc)
    {
        var status = Status(locationId);
        var report = Get(locationId);
        if (status.LastAttemptUtc is null)
        {
            return report is null || nowUtc - report.FetchedUtc >= Interval;
        }

        if (status.Failures > 0)
        {
            var backoff = TimeSpan.FromMinutes(Math.Min(15, Math.Pow(2, Math.Min(10, status.Failures - 1))));
            return nowUtc - status.LastAttemptUtc.Value >= backoff;
        }

        return nowUtc - status.LastAttemptUtc.Value >= Interval;
    }

    private async Task FetchAsync(Station station, WeatherLocation location, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var status = Status(location.Id);
            status.LastAttemptUtc = DateTime.UtcNow;
            try
            {
                var report = await _provider.FetchAsync(location, ct).ConfigureAwait(false);
                status.LastSuccessUtc = report.FetchedUtc;
                status.LastError = null;
                status.Failures = 0;
                _reports[location.Id] = report;
                try
                {
                    JsonFileStore.Write(_paths.WeatherReportFile(location.Id), report);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "JellyStations weather: could not cache the report for {Place}", location.Name);
                }

                _logger.LogDebug("JellyStations weather [{Station}]: {Place} {Temp:0.0}°C, code {Code}", station.Name, location.Name, report.Current.TemperatureC, report.Current.WeatherCode);
                Changed?.Invoke(station.Id);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                status.Failures++;
                status.LastError = ex.Message;
                _logger.LogWarning("JellyStations weather [{Station}]: fetch for {Place} failed ({Count} in a row): {Error}", station.Name, location.Name, status.Failures, ex.Message);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Safe to call twice: the hosted service disposes on stop and the container disposes again on shutdown.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _cts.Cancel();
        _timer?.Dispose();
        _cts.Dispose();
        _gate.Dispose();
    }
}
