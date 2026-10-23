using Jellyfin.Plugin.TvStation.Scheduling;
using Jellyfin.Plugin.TvStation.Storage;
using Jellyfin.Plugin.TvStation.Streaming;
using Jellyfin.Plugin.TvStation.Weather;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvStation.Services;

/// <summary>Startup/shutdown hooks: prepare folders, compile missing snapshots, stop encoders on exit.</summary>
public sealed class TvStationHostedService : IHostedService
{
    private readonly TvStationPaths _paths;
    private readonly StationService _stations;
    private readonly StreamSessionManager _sessions;
    private readonly GuideRefreshService _guide;
    private readonly WeatherService _weather;
    private readonly Music.MusicPackService _packs;
    private readonly ILogger<TvStationHostedService> _logger;
    private readonly object _seasonLock = new();
    private HashSet<Guid>? _inSeason;
    private Timer? _seasonTimer;

    public TvStationHostedService(TvStationPaths paths, StationService stations, StreamSessionManager sessions, GuideRefreshService guide, WeatherService weather, Music.MusicPackService packs, ILogger<TvStationHostedService> logger)
    {
        _weather = weather;
        _packs = packs;
        _paths = paths;
        _stations = stations;
        _sessions = sessions;
        _guide = guide;
        _logger = logger;
    }

    /// <summary>
    /// Re-evaluates which seasonal stations are in season. When the set changes (a window opened or closed since
    /// the last check), the guide is refreshed so the channel appears or disappears, and streams of channels that
    /// just went off season are stopped.
    /// </summary>
    public void CheckSeasons()
    {
        lock (_seasonLock)
        {
            var now = DateTime.UtcNow;
            var stations = _stations.GetAll();
            var current = stations.Where(s => s.Enabled && SeasonWindows.IsActive(s, now)).Select(s => s.Id).ToHashSet();
            if (_inSeason is null)
            {
                _inSeason = current;
                return;
            }

            var opened = current.Except(_inSeason).ToList();
            var closed = _inSeason.Except(current).ToList();
            _inSeason = current;
            if (opened.Count == 0 && closed.Count == 0)
            {
                return;
            }

            foreach (var id in closed)
            {
                _sessions.StopStation(id);
            }

            _logger.LogInformation("TvStation: season change ({Opened} opened, {Closed} closed); refreshing guide", opened.Count, closed.Count);
            _guide.Trigger();
        }
    }

    /// <summary>Forgets the last known season state so the next check re-baselines (after a station edit).</summary>
    public void ResetSeasonBaseline()
    {
        lock (_seasonLock)
        {
            _inSeason = null;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _paths.EnsureDirectories();
            _stations.EnsureCompiled();
            CheckSeasons();
            _weather.Start();
            _seasonTimer = new Timer(_ => SafeCheckSeasons(), null, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));
            _logger.LogInformation(
                "TvStation started: {Count} station(s), data at {Path}",
                _stations.GetAll().Count,
                _paths.Root);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TvStation: startup failed");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _seasonTimer?.Dispose();
        _weather.Dispose();
        _packs.Dispose();
        _sessions.StopAll();
        return Task.CompletedTask;
    }

    private void SafeCheckSeasons()
    {
        try
        {
            CheckSeasons();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TvStation: season check failed");
        }
    }
}
