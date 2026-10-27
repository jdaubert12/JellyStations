using Jellyfin.Plugin.JellyStations.Scheduling;
using Jellyfin.Plugin.JellyStations.Storage;
using Jellyfin.Plugin.JellyStations.Streaming;
using Jellyfin.Plugin.JellyStations.Weather;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyStations.Services;

/// <summary>Startup/shutdown hooks: prepare folders, compile missing snapshots, stop encoders on exit.</summary>
public sealed class JellyStationsHostedService : IHostedService
{
    private readonly JellyStationsPaths _paths;
    private readonly StationService _stations;
    private readonly StreamSessionManager _sessions;
    private readonly GuideRefreshService _guide;
    private readonly WeatherService _weather;
    private readonly Weather.Radar.RadarService _radar;
    private readonly Weather.Alerts.AlertService _alerts;
    private readonly Library.MusicLoudnessService _loudness;
    private readonly Music.MusicPackService _packs;
    private readonly ILogger<JellyStationsHostedService> _logger;
    private readonly object _seasonLock = new();
    private HashSet<Guid>? _inSeason;
    private Timer? _seasonTimer;

    public JellyStationsHostedService(JellyStationsPaths paths, StationService stations, StreamSessionManager sessions, GuideRefreshService guide, WeatherService weather, Weather.Radar.RadarService radar, Weather.Alerts.AlertService alerts, Library.MusicLoudnessService loudness, Music.MusicPackService packs, ILogger<JellyStationsHostedService> logger)
    {
        _weather = weather;
        _loudness = loudness;
        _radar = radar;
        _alerts = alerts;
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

            _logger.LogInformation("JellyStations: season change ({Opened} opened, {Closed} closed); refreshing guide", opened.Count, closed.Count);
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
            var legacy = LegacyInstall.MarkOtherNamesForDeletion(_paths.PluginsPath, Plugin.PluginId, Plugin.PluginName);
            if (legacy.Count > 0)
            {
                _logger.LogWarning(
                    "JellyStations: an older install of this plugin under its previous name is still loaded ({Folders}); it has been marked for removal. Restart Jellyfin once more to finish the upgrade.",
                    string.Join(", ", legacy.Select(Path.GetFileName)));
            }

            _stations.EnsureCompiled();
            CheckSeasons();
            _weather.Start();
            _radar.Start();
            _alerts.Start();
            _loudness.Start();
            _seasonTimer = new Timer(_ => SafeCheckSeasons(), null, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));
            _logger.LogInformation(
                "JellyStations started: {Count} station(s), data at {Path}",
                _stations.GetAll().Count,
                _paths.Root);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "JellyStations: startup failed");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _seasonTimer?.Dispose();
        _weather.Dispose();
        _radar.Dispose();
        _alerts.Dispose();
        _loudness.Dispose();
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
            _logger.LogError(ex, "JellyStations: season check failed");
        }
    }
}
