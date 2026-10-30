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
public sealed partial class RadarService : LocationPoller
{
    /// <summary>Frames per loop: 45 minutes of NEXRAD at five-minute steps, 90 minutes of RainViewer at ten.</summary>
    public const int FrameCount = 10;

    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan KeepFrames = TimeSpan.FromHours(3);

    private readonly JellyStationsPaths _paths;
    private readonly IReadOnlyList<IRadarSource> _sources;
    private readonly IReadOnlyList<IForecastRadarSource> _forecastSources;
    private readonly ILogger<RadarService> _logger;
    private readonly ConcurrentDictionary<Guid, RadarLoop> _loops = new();
    private readonly ConcurrentDictionary<Guid, RadarFetchStatus> _status = new();

    public RadarService(StationRepository stations, JellyStationsPaths paths, IEnumerable<IRadarSource> sources, IEnumerable<IForecastRadarSource> forecastSources, ILogger<RadarService> logger)
        : base(stations, logger, "radar", startDelay: TimeSpan.FromSeconds(5), nudgeDelay: TimeSpan.FromSeconds(2))
    {
        _paths = paths;
        _sources = sources.ToList();
        _forecastSources = forecastSources.ToList();
        _logger = logger;
    }

    public IReadOnlyList<IRadarSource> Sources => _sources;

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

    /// <summary>Fetches every place of a station now (radar and forecast radar, whichever the station shows); used by the editor's "Fetch now".</summary>
    public async Task<IReadOnlyList<RadarFetchStatus>> RefreshAsync(Station station, CancellationToken ct)
    {
        var result = new List<RadarFetchStatus>();
        foreach (var location in station.Weather.Locations.Where(l => l.IsValid))
        {
            if (station.Weather.ShowRadar)
            {
                await FetchAsync(station, location, ct).ConfigureAwait(false);
                result.Add(Status(location.Id));
            }

            if (station.Weather.ShowForecastRadar)
            {
                await FetchForecastAsync(station, location, ct).ConfigureAwait(false);
                result.Add(ForecastStatus(location.Id));
            }
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

        return FetchBackoff.IsDue(status, () => loop?.FetchedUtc, Interval, nowUtc);
    }

    public static string FrameFileName(string sourceId, RadarZoom zoom, DateTime timeUtc)
        => sourceId + "-" + zoom.ToString().ToLowerInvariant() + "-" + ((long)(timeUtc - DateTime.UnixEpoch).TotalSeconds).ToString(CultureInfo.InvariantCulture) + ".png";

    protected override bool Polls(Station station) => station.Weather.ShowRadar || station.Weather.ShowForecastRadar;

    protected override async Task PollAsync(Station station, WeatherLocation location, DateTime nowUtc, CancellationToken ct)
    {
        if (station.Weather.ShowRadar && IsDue(location.Id, station.Weather.RadarZoom, nowUtc))
        {
            await FetchAsync(station, location, ct).ConfigureAwait(false);
        }

        if (station.Weather.ShowForecastRadar && IsForecastDue(location.Id, station.Weather, nowUtc))
        {
            await FetchForecastAsync(station, location, ct).ConfigureAwait(false);
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
                    try
                    {
                        if (await EnsureFrameFileAsync(path, () => source.FetchOverlayAsync(viewport, time, ct), ct).ConfigureAwait(false))
                        {
                            fetched++;
                        }
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

                    frames.Add(new RadarFrame(time, path));
                }

                if (frames.Count == 0)
                {
                    throw lastFrameError ?? new InvalidOperationException("The radar source listed no frames");
                }

                var loop = new RadarLoop(source.Id, source.Name, source.Attribution, zoom, frames, now);
                _loops[location.Id] = loop;
                status.MarkSuccess(now);
                Sweep(dir, frames);
                _logger.LogDebug("JellyStations radar [{Station}]: {Place} has {Count} frame(s) from {Source}, {New} new, newest {Time:HH:mm} UTC", station.Name, location.Name, frames.Count, source.Id, fetched, loop.Latest.TimeUtc);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                status.MarkFailure(ex.Message);
                _logger.LogWarning("JellyStations radar [{Station}]: fetch for {Place} from {Source} failed ({Count} in a row): {Error}", station.Name, location.Name, source.Id, status.Failures, ex.Message);
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Fetches one frame into <paramref name="path"/> (through a temp file) unless it is cached already; true when it was fetched now. A failed fetch throws.</summary>
    private static async Task<bool> EnsureFrameFileAsync(string path, Func<Task<byte[]>> fetch, CancellationToken ct)
    {
        if (File.Exists(path))
        {
            return false;
        }

        var png = await fetch().ConfigureAwait(false);
        var tmp = path + ".tmp";
        await File.WriteAllBytesAsync(tmp, png, ct).ConfigureAwait(false);
        File.Move(tmp, path, overwrite: true);
        return true;
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
}
