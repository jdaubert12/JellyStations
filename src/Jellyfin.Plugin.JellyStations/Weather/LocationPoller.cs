using Jellyfin.Plugin.JellyStations.Models;
using Jellyfin.Plugin.JellyStations.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyStations.Weather;

/// <summary>
/// The polling shell the weather, alert and radar services share: a timer that ticks every 30 seconds (and soon
/// after a station is saved), one tick at a time, walking every valid place of every weather channel in use that
/// the service cares about. Fetches run one at a time under <see cref="Gate"/>. Safe to dispose twice.
/// </summary>
public abstract class LocationPoller : IDisposable
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);

    private readonly StationRepository _stations;
    private readonly ILogger _logger;
    private readonly string _name;
    private readonly TimeSpan _startDelay;
    private readonly TimeSpan _nudgeDelay;
    private readonly CancellationTokenSource _cts = new();
    private Timer? _timer;
    private int _disposed;
    private int _ticking;

    /// <param name="stations">The stations to poll for; a save nudges the poller.</param>
    /// <param name="logger">The service's own logger.</param>
    /// <param name="name">What the service fetches, for the poll-failure log line ("weather", "alerts", "radar").</param>
    /// <param name="startDelay">The wait before the first tick.</param>
    /// <param name="nudgeDelay">The wait before the tick after a station is saved.</param>
    protected LocationPoller(StationRepository stations, ILogger logger, string name, TimeSpan startDelay, TimeSpan nudgeDelay)
    {
        _stations = stations;
        _logger = logger;
        _name = name;
        _startDelay = startDelay;
        _nudgeDelay = nudgeDelay;
        _stations.Changed += (_, _) => Nudge();
    }

    /// <summary>Held around every fetch so only one runs at a time.</summary>
    protected SemaphoreSlim Gate { get; } = new(1, 1);

    /// <summary>Starts polling. Called once by the hosted service.</summary>
    public void Start()
    {
        _timer ??= new Timer(_ => _ = TickAsync(), null, _startDelay, Tick);
    }

    /// <summary>Polls soon (after a station was saved) rather than at the next regular tick.</summary>
    public void Nudge()
    {
        try
        {
            _timer?.Change(_nudgeDelay, Tick);
        }
        catch (ObjectDisposedException)
        {
            // Shutting down.
        }
    }

    /// <summary>Whether this service polls for a weather channel that is in use.</summary>
    protected abstract bool Polls(Station station);

    /// <summary>Fetches whatever is due for one place of a polled station.</summary>
    protected abstract Task PollAsync(Station station, WeatherLocation location, DateTime nowUtc, CancellationToken ct);

    private async Task TickAsync()
    {
        if (Volatile.Read(ref _disposed) == 1 || Interlocked.Exchange(ref _ticking, 1) == 1)
        {
            return;
        }

        try
        {
            var now = DateTime.UtcNow;
            foreach (var station in WeatherSources.InUse(_stations.GetAll(), now).Where(Polls))
            {
                foreach (var location in station.Weather.Locations.Where(l => l.IsValid))
                {
                    if (_cts.IsCancellationRequested)
                    {
                        return;
                    }

                    await PollAsync(station, location, now, _cts.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "JellyStations {Poller}: poll failed", _name);
        }
        finally
        {
            Volatile.Write(ref _ticking, 0);
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
        Gate.Dispose();
        GC.SuppressFinalize(this);
    }
}
