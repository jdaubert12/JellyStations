using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.JellyStations.Library;
using Jellyfin.Plugin.JellyStations.Models;
using Jellyfin.Plugin.JellyStations.Scheduling;
using Jellyfin.Plugin.JellyStations.Storage;
using Jellyfin.Plugin.JellyStations.Streaming;
using Jellyfin.Plugin.JellyStations.Weather.Alerts;
using Jellyfin.Plugin.JellyStations.Weather.Radar;
using Jellyfin.Plugin.JellyStations.Weather.Screens;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyStations.Weather;

/// <summary>
/// Turns "this weather station, now" into something the restreamer can play: the screens on the clock for the
/// next few minutes (rendered as needed), the music under them from the station's music timeline, and the
/// clock offsets ffmpeg needs. Everything about the cycle itself is in <see cref="WeatherCycle"/>.
/// </summary>
public sealed class WeatherChannelService
{
    /// <summary>A segment runs about this long; one ffmpeg process per segment.</summary>
    public static readonly TimeSpan TargetSegment = TimeSpan.FromMinutes(4);

    private readonly StationRepository _stations;
    private readonly CompiledStationRepository _compiled;
    private readonly WeatherService _weather;
    private readonly RadarService _radar;
    private readonly AlertService _alerts;
    private readonly MusicLoudnessService _loudness;
    private readonly WeatherScreenRenderer _renderer;
    private readonly WeatherAssets _assets;
    private readonly JellyStationsPaths _paths;
    private readonly ILogger<WeatherChannelService> _logger;

    public WeatherChannelService(StationRepository stations, CompiledStationRepository compiled, WeatherService weather, RadarService radar, AlertService alerts, MusicLoudnessService loudness, WeatherScreenRenderer renderer, WeatherAssets assets, JellyStationsPaths paths, ILogger<WeatherChannelService> logger)
    {
        _stations = stations;
        _compiled = compiled;
        _weather = weather;
        _radar = radar;
        _alerts = alerts;
        _loudness = loudness;
        _renderer = renderer;
        _assets = assets;
        _paths = paths;
        _logger = logger;
    }

    /// <summary>The segment starting at <paramref name="nowUtc"/> inside <paramref name="entry"/>, or null when the station has no usable place.</summary>
    public WeatherSegment? BuildSegment(Guid stationId, DateTime nowUtc, ScheduleEntry entry, EncodingProfile profile)
    {
        var station = _stations.Get(stationId);
        if (station is null || !station.UsesWeather)
        {
            return null;
        }

        var config = station.Weather;
        var locations = config.Locations.Where(l => l.IsValid).ToList();
        if (locations.Count == 0)
        {
            return null;
        }

        var zone = WeeklySlotScheduler.ResolveTimeZone(station.TimeZoneId);
        var loops = locations.Select(l => config.ShowRadar ? _radar.Get(l.Id) : null).ToList();
        var forecasts = locations.Select(l => config.ShowForecastRadar ? _radar.GetForecast(l.Id) : null).ToList();
        var forecastFrames = forecasts.Select(f => f?.ForecastFrames(nowUtc, config.ForecastRadarHours, config.ForecastRadarStepMinutes) ?? Array.Empty<RadarFrame>()).ToList();
        var alerts = locations.Select(l => config.ShowAlerts ? _alerts.Active(l.Id, nowUtc) : Array.Empty<WeatherAlert>()).ToList();
        var available = new CycleAvailability(i => loops[i] is { IsPlayable: true }, i => forecastFrames[i].Count >= 2, i => alerts[i].Count > 0);
        var screens = WeatherCycle.Timeline(config, locations.Count, nowUtc, entry.EndUtc, entry.StartUtc, zone, available);
        if (screens.Count == 0)
        {
            return null;
        }

        // Cut the segment at the first screen boundary past the target length, so the next segment starts clean.
        var cutAt = nowUtc + TargetSegment;
        var kept = new List<TimedScreen>();
        foreach (var s in screens)
        {
            kept.Add(s);
            if (s.EndUtc >= cutAt)
            {
                break;
            }
        }

        var segStart = kept[0].StartUtc;
        var segEnd = kept[^1].EndUtc;

        var places = locations.Select((l, i) =>
        {
            var report = _weather.Get(l.Id);
            return new PlaceWeather(l, report, WeatherService.IsStale(report, config, nowUtc), alerts[i], _alerts.Get(l.Id)?.Attribution);
        }).ToList();
        var attribution = places.Select(p => p.Report?.Attribution).FirstOrDefault(a => !string.IsNullOrEmpty(a)) ?? _weather.Provider.Attribution;
        var logoPath = config.ShowLogo && !string.IsNullOrEmpty(station.LogoFileName) ? _paths.LogoFile(station.LogoFileName) : null;
        var unixStart = (long)Math.Floor((segStart - DateTime.UnixEpoch).TotalSeconds);

        var slides = new List<WeatherSlide>(kept.Count);
        foreach (var s in kept)
        {
            var place = s.Screen.LocationIndex >= 0 && s.Screen.LocationIndex < places.Count ? places[s.Screen.LocationIndex] : null;
            var offset = place is null ? (long)zone.GetUtcOffset(nowUtc).TotalSeconds : PlaceOffsetSeconds(place, zone, nowUtc);
            var loop = s.Screen.Kind == WeatherScreenKind.Radar && place is not null ? loops[s.Screen.LocationIndex] : null;
            if (loop is { IsPlayable: true })
            {
                var listPath = RadarSequence(station.Id, loop, loop.Frames, s, place!, places, config, nowUtc, zone, logoPath, profile, attribution);
                slides.Add(new WeatherSlide(listPath, s.Duration.TotalSeconds, unixStart + offset, IsSequence: true));
                continue;
            }

            var forecast = s.Screen.Kind == WeatherScreenKind.ForecastRadar && place is not null ? forecasts[s.Screen.LocationIndex] : null;
            if (forecast is not null && forecastFrames[s.Screen.LocationIndex].Count >= 2)
            {
                var listPath = RadarSequence(station.Id, forecast, forecastFrames[s.Screen.LocationIndex], s, place!, places, config, nowUtc, zone, logoPath, profile, attribution);
                slides.Add(new WeatherSlide(listPath, s.Duration.TotalSeconds, unixStart + offset, IsSequence: true));
                continue;
            }

            var request = new ScreenRequest(s.Screen.Kind, place, places, config, nowUtc, zone, logoPath, profile.Width, profile.Height, attribution);
            var path = _renderer.Render(station.Id, request);
            slides.Add(new WeatherSlide(path, s.Duration.TotalSeconds, unixStart + offset));
        }

        var audio = new List<WeatherAudio>();
        var credits = new List<WeatherCredit>();
        if (config.MusicEnabled)
        {
            var compiled = _compiled.Get(station.Id);
            if (compiled is { Music.Count: > 0 })
            {
                var timeline = new MusicTimeline(compiled.Music, station.AnchorUtc == default ? DateTime.UnixEpoch : station.AnchorUtc, station.Id, station.Revision);
                foreach (var piece in timeline.Between(segStart, segEnd))
                {
                    if (!File.Exists(piece.Track.Path))
                    {
                        _logger.LogWarning("JellyStations weather [{Station}]: music file missing, skipping: {Path}", station.Name, piece.Track.Path);
                        continue;
                    }

                    audio.Add(AudioPiece(piece, config.NormalizeMusic ? _loudness.GainDb(piece.Track) : 0));
                    if (config.ShowMusicCredit)
                    {
                        var from = (piece.StartUtc - segStart).TotalSeconds;
                        credits.Add(new WeatherCredit(CreditText(piece.Track), from, from + piece.Duration.TotalSeconds));
                    }
                }
            }
        }

        return new WeatherSegment
        {
            StartUtc = segStart,
            EndUtc = segEnd,
            Slides = slides,
            Audio = audio,
            Credits = credits,
            FontPath = _assets.FontFile(FontWeight.SemiBold),
            TwentyFourHourClock = config.TwentyFourHourClock,
            VolumePercent = config.MusicVolumePercent,
            Title = entry.Title
        };
    }

    /// <summary>
    /// One music piece as ffmpeg plays it: levelled by its measured gain, faded in when the track really starts here
    /// and out when it really ends here (never at a segment cut, which the next segment continues seamlessly).
    /// </summary>
    public static WeatherAudio AudioPiece(MusicPiece piece, double gainDb)
    {
        var fadeIn = piece.InPoint <= TimeSpan.Zero;
        var fadeOut = piece.InPoint + piece.Duration >= TimeSpan.FromTicks(piece.Track.RuntimeTicks) - TimeSpan.FromMilliseconds(20);
        return new WeatherAudio(piece.Track.Path, piece.InPoint.TotalSeconds, piece.Duration.TotalSeconds, gainDb, fadeIn, fadeOut);
    }

    /// <summary>
    /// Renders the frames of one radar screen (each frame once, cached like any screen) and writes the concat list
    /// that plays them on <see cref="RadarTiming"/>'s clock; the list is named by its content, so a repeat of the
    /// same screen with the same frames reuses it.
    /// </summary>
    private string RadarSequence(Guid stationId, RadarLoop loop, IReadOnlyList<RadarFrame> frames, TimedScreen s, PlaceWeather place, IReadOnlyList<PlaceWeather> places, WeatherConfig config, DateTime nowUtc, TimeZoneInfo zone, string? logoPath, EncodingProfile profile, string attribution)
    {
        var plan = RadarTiming.Plan(s.Duration.TotalSeconds + WeatherSegment.FadeSeconds + 1.0, frames.Count);
        var framePaths = new string?[frames.Count];
        var entries = new List<(string Path, double Seconds)>(plan.Count);
        var zoom = WebMercator.ZoomOf(loop.Zoom);
        foreach (var (index, seconds) in plan)
        {
            var path = framePaths[index];
            if (path is null)
            {
                var frame = frames[index];
                var radar = new RadarFrameRequest(loop.SourceId, loop.SourceName, loop.Attribution, frame.TimeUtc, frame.Path, index, frames.Count, zoom, loop.RunUtc);
                var request = new ScreenRequest(s.Screen.Kind, place, places, config, nowUtc, zone, logoPath, profile.Width, profile.Height, attribution, radar);
                framePaths[index] = path = _renderer.Render(stationId, request);
            }

            entries.Add((path, seconds));
        }

        var text = ConcatList.Build(entries);
        var name = "seq-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..24].ToLowerInvariant() + ".txt";
        var listPath = Path.Combine(_paths.WeatherScreensDir(stationId), name);
        if (!File.Exists(listPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(listPath)!);
            File.WriteAllText(listPath, text);
        }

        return listPath;
    }

    /// <summary>"Now playing: Title – Artist", or without the artist when none is known.</summary>
    public static string CreditText(MusicTrack track)
        => "Now playing: " + (string.IsNullOrWhiteSpace(track.Artist) ? track.Title : track.Title + " – " + track.Artist);

    /// <summary>The place's UTC offset now: its configured zone, else the provider's, else the station's.</summary>
    public static long PlaceOffsetSeconds(PlaceWeather place, TimeZoneInfo stationZone, DateTime nowUtc)
    {
        foreach (var id in new[] { place.Location.TimeZoneId, place.Report?.TimeZoneId })
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            try
            {
                return (long)TimeZoneInfo.FindSystemTimeZoneById(id).GetUtcOffset(nowUtc).TotalSeconds;
            }
            catch (Exception)
            {
                // Unknown id on this host; try the next source.
            }
        }

        if (place.Report is not null)
        {
            return place.Report.UtcOffsetSeconds;
        }

        return (long)stationZone.GetUtcOffset(nowUtc).TotalSeconds;
    }
}
