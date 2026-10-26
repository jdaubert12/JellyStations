using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.JellyStations.Models;
using Jellyfin.Plugin.JellyStations.Scheduling;
using Jellyfin.Plugin.JellyStations.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyStations.Weather.Radar;

/// <summary>
/// Keeps a <see cref="RadarLoop"/> for every place on every enabled weather station that shows radar: every
/// <see cref="Interval"/> it asks the place's source for the newest frame times, fetches only the frames it does not
/// have yet (each one a small transparent PNG of the place's map window, cached under the data folder), and drops
/// the ones that fell out of the loop. A restart picks the cached frames back up. Nothing is fetched for a station
/// nobody enabled.
/// </summary>
public sealed partial class RadarService : IDisposable
{
    /// <summary>Frames per loop: 45 minutes of NEXRAD at five-minute steps, 90 minutes of RainViewer at ten.</summary>
    public const int FrameCount = 10;

    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan KeepFrames = TimeSpan.FromHours(3);

    private readonly StationRepository _stations;
    private readonly JellyStationsPaths _paths;
    private readonly IReadOnlyList<IRadarSource> _sources;
    private readonly ILogger<RadarService> _logger;
    private readonly ConcurrentDictionary<Guid, RadarLoop> _loops = new();
    private readonly ConcurrentDictionary<Guid, RadarFetchStatus> _status = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private Timer? _timer;
    private int _disposed;
    private int _ticking;

    public RadarService(StationRepository stations, JellyStationsPaths paths, IEnumerable<IRadarSource> sources, ILogger<RadarService> logger)
    {
        _stations = stations;
        _paths = paths;
        _sources = sources.ToList();
        _logger = logger;
        _stations.Changed += (_, _) => Nudge();
    }

    public IReadOnlyList<IRadarSource> Sources => _sources;

    /// <summary>Starts polling. Called once by the hosted service.</summary>
    public void Start()
    {
        _timer ??= new Timer(_ => _ = TickAsync(), null, TimeSpan.FromSeconds(5), Tick);
    }

    /// <summary>Polls soon (after a station was saved) rather than at the next regular tick.</summary>
    public void Nudge()
    {
        try
        {
            _timer?.Change(TimeSpan.FromSeconds(2), Tick);
        }
        catch (ObjectDisposedException)
        {
            // Shutting down.
        }
    }

    /// <summary>The first source that covers the place, or null when none does.</summary>
    public IRadarSource? SourceFor(WeatherLocation location) => _sources.FirstOrDefault(s => s.Covers(location.Latitude, location.Longitude));

    /// <summary>The frames on hand for a place, from memory or from the files left by an earlier run; null when there are none.</summary>
    public RadarLoop? Get(Guid locationId)
    {
        if (_loops.TryGetValue(locationId, out var loop))
        {
            return loop;
        }

        loop = LoadFromDisk(locationId);
        if (loop is not null)
        {
            _loops.TryAdd(locationId, loop);
        }

        return loop;
    }

    public RadarFetchStatus Status(Guid locationId) => _status.GetOrAdd(locationId, id => new RadarFetchStatus { LocationId = id });

    /// <summary>Fetches every place of a station now; used by the editor's "Fetch now".</summary>
    public async Task<IReadOnlyList<RadarFetchStatus>> RefreshAsync(Station station, CancellationToken ct)
    {
        var result = new List<RadarFetchStatus>();
        foreach (var location in station.Weather.Locations.Where(l => l.IsValid))
        {
            await FetchAsync(station, location, ct).ConfigureAwait(false);
            result.Add(Status(location.Id));
        }

        return result;
    }

    /// <summary>Due when never fetched, when the loop is older than the interval, when the zoom changed, or when a failure's backoff has passed.</summary>
    public bool IsDue(Guid locationId, RadarZoom zoom, DateTime nowUtc)
    {
        var status = Status(locationId);
        var loop = Get(locationId);
        if (loop is not null && loop.Zoom != zoom)
        {
            return true;
        }

        if (status.LastAttemptUtc is null)
        {
            return loop is null || nowUtc - loop.FetchedUtc >= Interval;
        }

        if (status.Failures > 0)
        {
            var backoff = TimeSpan.FromMinutes(Math.Min(15, Math.Pow(2, Math.Min(10, status.Failures - 1))));
            return nowUtc - status.LastAttemptUtc.Value >= backoff;
        }

        return nowUtc - status.LastAttemptUtc.Value >= Interval;
    }

    public static string FrameFileName(string sourceId, RadarZoom zoom, DateTime timeUtc)
        => sourceId + "-" + zoom.ToString().ToLowerInvariant() + "-" + ((long)(timeUtc - DateTime.UnixEpoch).TotalSeconds).ToString(CultureInfo.InvariantCulture) + ".png";

    private async Task TickAsync()
    {
        if (Volatile.Read(ref _disposed) == 1 || Interlocked.Exchange(ref _ticking, 1) == 1)
        {
            return;
        }

        try
        {
            var now = DateTime.UtcNow;
            foreach (var station in _stations.GetAll().Where(s => s.Mode == ScheduleMode.Weather && s.Enabled && s.Weather.ShowRadar && SeasonWindows.IsActive(s, now)))
            {
                foreach (var location in station.Weather.Locations.Where(l => l.IsValid))
                {
                    if (_cts.IsCancellationRequested)
                    {
                        return;
                    }

                    if (IsDue(location.Id, station.Weather.RadarZoom, now))
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
            _logger.LogError(ex, "JellyStations radar: poll failed");
        }
        finally
        {
            Volatile.Write(ref _ticking, 0);
        }
    }

    private async Task FetchAsync(Station station, WeatherLocation location, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var status = Status(location.Id);
            var now = DateTime.UtcNow;
            status.LastAttemptUtc = now;
            var source = SourceFor(location);
            if (source is null)
            {
                status.SourceName = null;
                status.LastError = "No radar source covers this place";
                status.Failures = 0;
                return;
            }

            status.SourceName = source.Name;
            var zoom = station.Weather.RadarZoom;
            var viewport = RadarViewport.For(location, WebMercator.ZoomOf(zoom));
            try
            {
                var times = await source.FrameTimesAsync(now, FrameCount, ct).ConfigureAwait(false);
                var dir = _paths.RadarDir(location.Id);
                Directory.CreateDirectory(dir);
                var frames = new List<RadarFrame>(times.Count);
                var fetched = 0;
                Exception? lastFrameError = null;
                foreach (var time in times)
                {
                    var path = Path.Combine(dir, FrameFileName(source.Id, zoom, time));
                    if (!File.Exists(path))
                    {
                        try
                        {
                            var png = await source.FetchOverlayAsync(viewport, time, ct).ConfigureAwait(false);
                            var tmp = path + ".tmp";
                            await File.WriteAllBytesAsync(tmp, png, ct).ConfigureAwait(false);
                            File.Move(tmp, path, overwrite: true);
                            fetched++;
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            lastFrameError = ex;
                            _logger.LogDebug(ex, "JellyStations radar [{Station}]: frame {Time} for {Place} failed", station.Name, time, location.Name);
                            continue;
                        }
                    }

                    frames.Add(new RadarFrame(time, path));
                }

                if (frames.Count == 0)
                {
                    throw lastFrameError ?? new InvalidOperationException("The radar source listed no frames");
                }

                var loop = new RadarLoop(source.Id, source.Name, source.Attribution, zoom, frames, now);
                _loops[location.Id] = loop;
                status.LastSuccessUtc = now;
                status.LastError = null;
                status.Failures = 0;
                Sweep(dir, frames);
                _logger.LogDebug("JellyStations radar [{Station}]: {Place} has {Count} frame(s) from {Source}, {New} new, newest {Time:HH:mm} UTC", station.Name, location.Name, frames.Count, source.Id, fetched, loop.Latest.TimeUtc);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                status.Failures++;
                status.LastError = ex.Message;
                _logger.LogWarning("JellyStations radar [{Station}]: fetch for {Place} from {Source} failed ({Count} in a row): {Error}", station.Name, location.Name, source.Id, status.Failures, ex.Message);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Removes frame files that are no longer part of the loop (older frames, another zoom, another source).</summary>
    private void Sweep(string dir, IReadOnlyList<RadarFrame> keep)
    {
        var keepSet = new HashSet<string>(keep.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                if (!keepSet.Contains(file))
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "JellyStations radar: sweep of {Dir} failed", dir);
        }
    }

    private RadarLoop? LoadFromDisk(Guid locationId)
    {
        var dir = _paths.RadarDir(locationId);
        if (!Directory.Exists(dir))
        {
            return null;
        }

        try
        {
            var now = DateTime.UtcNow;
            var groups = new Dictionary<(string Source, RadarZoom Zoom), List<RadarFrame>>();
            foreach (var file in Directory.EnumerateFiles(dir, "*.png"))
            {
                var m = FrameName().Match(Path.GetFileName(file));
                if (!m.Success || !Enum.TryParse<RadarZoom>(m.Groups[2].Value, true, out var zoom))
                {
                    continue;
                }

                var time = DateTime.UnixEpoch.AddSeconds(long.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture));
                if (now - time > KeepFrames)
                {
                    continue;
                }

                var key = (m.Groups[1].Value, zoom);
                if (!groups.TryGetValue(key, out var list))
                {
                    groups[key] = list = new List<RadarFrame>();
                }

                list.Add(new RadarFrame(time, file));
            }

            if (groups.Count == 0)
            {
                return null;
            }

            var best = groups.OrderByDescending(g => g.Value.Max(f => f.TimeUtc)).First();
            var frames = best.Value.OrderBy(f => f.TimeUtc).ToList();
            var source = _sources.FirstOrDefault(s => s.Id == best.Key.Source);
            var fetched = frames.Max(f => File.GetLastWriteTimeUtc(f.Path));
            return new RadarLoop(best.Key.Source, source?.Name ?? best.Key.Source, source?.Attribution ?? string.Empty, best.Key.Zoom, frames, fetched);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "JellyStations radar: could not read cached frames in {Dir}", dir);
            return null;
        }
    }

    [GeneratedRegex(@"^([a-z0-9]+)-([a-z]+)-(\d+)\.png$", RegexOptions.IgnoreCase)]
    private static partial Regex FrameName();

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
