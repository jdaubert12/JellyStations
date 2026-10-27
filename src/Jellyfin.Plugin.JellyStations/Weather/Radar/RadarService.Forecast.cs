using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.JellyStations.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyStations.Weather.Radar;

/// <summary>
/// The forecast side: one forecast <see cref="RadarLoop"/> per place from the newest model run, holding the frames
/// from about now out to the station's look-ahead plus a margin for the hour until the next run is published.
/// Frames are named by run and forecast minute, so a sliding window only fetches the minutes it lacks and a new
/// run replaces the lot. Cached under the place's radar folder in "forecast".
/// </summary>
public sealed partial class RadarService
{
    /// <summary>How often the newest run is looked for and the window topped up.</summary>
    public static readonly TimeSpan ForecastInterval = TimeSpan.FromMinutes(10);

    /// <summary>Minutes fetched past the look-ahead so the loop stays whole until the next run (about an hour after this one) is up.</summary>
    public const int ForecastRunMarginMinutes = 120;

    /// <summary>A run older than this is not worth loading from disk; the next fetch replaces it.</summary>
    private static readonly TimeSpan KeepForecastRuns = TimeSpan.FromHours(12);

    private readonly ConcurrentDictionary<Guid, RadarLoop> _forecastLoops = new();
    private readonly ConcurrentDictionary<Guid, RadarFetchStatus> _forecastStatus = new();

    public IReadOnlyList<IForecastRadarSource> ForecastSources => _forecastSources;

    /// <summary>The first forecast source that covers the place, or null when none does.</summary>
    public IForecastRadarSource? ForecastSourceFor(WeatherLocation location) => _forecastSources.FirstOrDefault(s => s.Covers(location.Latitude, location.Longitude));

    /// <summary>The forecast frames on hand for a place, from memory or from the files left by an earlier run; null when there are none.</summary>
    public RadarLoop? GetForecast(Guid locationId)
    {
        if (_forecastLoops.TryGetValue(locationId, out var loop))
        {
            return loop;
        }

        loop = LoadForecastFromDisk(locationId);
        if (loop is not null)
        {
            _forecastLoops.TryAdd(locationId, loop);
        }

        return loop;
    }

    public RadarFetchStatus ForecastStatus(Guid locationId) => _forecastStatus.GetOrAdd(locationId, id => new RadarFetchStatus { LocationId = id });

    /// <summary>
    /// The forecast minutes of a run worth having now: from the step at or before now out to the look-ahead plus
    /// the margin, capped at what the model offers.
    /// </summary>
    public static IReadOnlyList<int> ForecastMinutes(DateTime runUtc, DateTime nowUtc, int hours, int stepMinutes, int maxMinutes)
    {
        var list = new List<int>();
        if (stepMinutes <= 0)
        {
            return list;
        }

        var elapsed = (nowUtc - runUtc).TotalMinutes;
        var first = Math.Max(0, (int)Math.Floor(elapsed / stepMinutes) * stepMinutes);
        var last = Math.Min(maxMinutes, (int)Math.Ceiling((Math.Max(0, elapsed) + (hours * 60) + ForecastRunMarginMinutes) / stepMinutes) * stepMinutes);
        for (var m = first; m <= last; m += stepMinutes)
        {
            list.Add(m);
        }

        return list;
    }

    public static string ForecastFrameFileName(string sourceId, RadarZoom zoom, DateTime runUtc, int forecastMinute)
        => sourceId + "-" + zoom.ToString().ToLowerInvariant() + "-" + runUtc.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture) + "-f" + forecastMinute.ToString("0000", CultureInfo.InvariantCulture) + ".png";

    /// <summary>
    /// Due when never fetched, when the zoom changed, when the loop lacks a minute the window now needs (it slid, or
    /// the step or look-ahead changed), every <see cref="ForecastInterval"/> to look for a newer run, or when a
    /// failure's backoff has passed.
    /// </summary>
    public bool IsForecastDue(Guid locationId, WeatherConfig config, DateTime nowUtc)
    {
        var status = ForecastStatus(locationId);
        var loop = GetForecast(locationId);
        if (loop is not null && status.Failures == 0)
        {
            if (loop.Zoom != config.RadarZoom)
            {
                return true;
            }

            var have = new HashSet<long>(loop.Frames.Select(f => (long)(f.TimeUtc - loop.RunUtc!.Value).TotalMinutes));
            var max = _forecastSources.FirstOrDefault(s => s.Id == loop.SourceId)?.MaxMinutes ?? 1080;
            if (ForecastMinutes(loop.RunUtc!.Value, nowUtc, config.ForecastRadarHours, config.ForecastRadarStepMinutes, max).Any(m => !have.Contains(m)))
            {
                return true;
            }
        }

        if (status.LastAttemptUtc is null)
        {
            return loop is null || nowUtc - loop.FetchedUtc >= ForecastInterval;
        }

        if (status.Failures > 0)
        {
            var backoff = TimeSpan.FromMinutes(Math.Min(15, Math.Pow(2, Math.Min(10, status.Failures - 1))));
            return nowUtc - status.LastAttemptUtc.Value >= backoff;
        }

        return nowUtc - status.LastAttemptUtc.Value >= ForecastInterval;
    }

    private async Task FetchForecastAsync(Station station, WeatherLocation location, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var status = ForecastStatus(location.Id);
            var now = DateTime.UtcNow;
            status.LastAttemptUtc = now;
            var source = ForecastSourceFor(location);
            if (source is null)
            {
                status.SourceName = null;
                status.LastError = "No forecast radar source covers this place";
                status.Failures = 0;
                return;
            }

            status.SourceName = source.Name;
            var config = station.Weather;
            var zoom = config.RadarZoom;
            var viewport = RadarViewport.For(location, WebMercator.ZoomOf(zoom));
            try
            {
                var run = await source.LatestRunAsync(now, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("No recent " + source.Id.ToUpperInvariant() + " run is published yet");
                var minutes = ForecastMinutes(run, now, config.ForecastRadarHours, config.ForecastRadarStepMinutes, source.MaxMinutes);
                var dir = _paths.ForecastRadarDir(location.Id);
                Directory.CreateDirectory(dir);
                var frames = new List<RadarFrame>(minutes.Count);
                var fetched = 0;
                Exception? lastFrameError = null;
                foreach (var minute in minutes)
                {
                    var path = Path.Combine(dir, ForecastFrameFileName(source.Id, zoom, run, minute));
                    if (!File.Exists(path))
                    {
                        try
                        {
                            var png = await source.FetchOverlayAsync(viewport, run, minute, ct).ConfigureAwait(false);
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
                            _logger.LogDebug(ex, "JellyStations forecast radar [{Station}]: minute {Minute} of the {Run:HH:mm} run for {Place} failed", station.Name, minute, run, location.Name);
                            continue;
                        }
                    }

                    frames.Add(new RadarFrame(run.AddMinutes(minute), path));
                }

                if (frames.Count == 0)
                {
                    throw lastFrameError ?? new InvalidOperationException("The forecast source offered no frames");
                }

                // Keep whatever minutes of the same run an earlier, wider window fetched; they are still valid frames.
                var previous = _forecastLoops.TryGetValue(location.Id, out var old) && old.RunUtc == run && old.Zoom == zoom ? old.Frames : Array.Empty<RadarFrame>();
                var merged = frames.Concat(previous.Where(p => File.Exists(p.Path))).GroupBy(f => f.TimeUtc).Select(g => g.First()).OrderBy(f => f.TimeUtc).ToList();
                var loop = new RadarLoop(source.Id, source.Name, source.Attribution, zoom, merged, now, run);
                _forecastLoops[location.Id] = loop;
                status.LastSuccessUtc = now;
                status.LastError = null;
                status.Failures = 0;
                status.RunUtc = run;
                Sweep(dir, merged);
                _logger.LogDebug("JellyStations forecast radar [{Station}]: {Place} has {Count} frame(s) from the {Run:HH:mm} UTC {Source} run, {New} new, out to {Time:HH:mm} UTC", station.Name, location.Name, merged.Count, run, source.Id, fetched, loop.Latest.TimeUtc);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                status.Failures++;
                status.LastError = ex.Message;
                _logger.LogWarning("JellyStations forecast radar [{Station}]: fetch for {Place} from {Source} failed ({Count} in a row): {Error}", station.Name, location.Name, source.Id, status.Failures, ex.Message);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private RadarLoop? LoadForecastFromDisk(Guid locationId)
    {
        var dir = _paths.ForecastRadarDir(locationId);
        if (!Directory.Exists(dir))
        {
            return null;
        }

        try
        {
            var now = DateTime.UtcNow;
            var groups = new Dictionary<(string Source, RadarZoom Zoom, DateTime Run), List<RadarFrame>>();
            foreach (var file in Directory.EnumerateFiles(dir, "*.png"))
            {
                var m = ForecastFrameName().Match(Path.GetFileName(file));
                if (!m.Success || !Enum.TryParse<RadarZoom>(m.Groups[2].Value, true, out var zoom)
                    || !DateTime.TryParseExact(m.Groups[3].Value, "yyyyMMddHHmm", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var run))
                {
                    continue;
                }

                if (now - run > KeepForecastRuns)
                {
                    continue;
                }

                var minute = int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture);
                var key = (m.Groups[1].Value, zoom, run);
                if (!groups.TryGetValue(key, out var list))
                {
                    groups[key] = list = new List<RadarFrame>();
                }

                list.Add(new RadarFrame(run.AddMinutes(minute), file));
            }

            if (groups.Count == 0)
            {
                return null;
            }

            var best = groups.OrderByDescending(g => g.Key.Run).ThenByDescending(g => g.Value.Count).First();
            var frames = best.Value.OrderBy(f => f.TimeUtc).ToList();
            var source = _forecastSources.FirstOrDefault(s => s.Id == best.Key.Source);
            var fetched = frames.Max(f => File.GetLastWriteTimeUtc(f.Path));
            return new RadarLoop(best.Key.Source, source?.Name ?? best.Key.Source, source?.Attribution ?? string.Empty, best.Key.Zoom, frames, fetched, best.Key.Run);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "JellyStations forecast radar: could not read cached frames in {Dir}", dir);
            return null;
        }
    }

    [GeneratedRegex(@"^([a-z0-9]+)-([a-z]+)-(\d{12})-f(\d{4})\.png$", RegexOptions.IgnoreCase)]
    private static partial Regex ForecastFrameName();
}
