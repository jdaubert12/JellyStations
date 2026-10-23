using Jellyfin.Plugin.TvStation.Models;
using Jellyfin.Plugin.TvStation.Scheduling;
using Jellyfin.Plugin.TvStation.Storage;
using Jellyfin.Plugin.TvStation.Streaming;
using Jellyfin.Plugin.TvStation.Weather.Screens;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvStation.Weather;

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
    private readonly WeatherScreenRenderer _renderer;
    private readonly WeatherAssets _assets;
    private readonly TvStationPaths _paths;
    private readonly ILogger<WeatherChannelService> _logger;

    public WeatherChannelService(StationRepository stations, CompiledStationRepository compiled, WeatherService weather, WeatherScreenRenderer renderer, WeatherAssets assets, TvStationPaths paths, ILogger<WeatherChannelService> logger)
    {
        _stations = stations;
        _compiled = compiled;
        _weather = weather;
        _renderer = renderer;
        _assets = assets;
        _paths = paths;
        _logger = logger;
    }

    /// <summary>The segment starting at <paramref name="nowUtc"/> inside <paramref name="entry"/>, or null when the station has no usable place.</summary>
    public WeatherSegment? BuildSegment(Guid stationId, DateTime nowUtc, ScheduleEntry entry, EncodingProfile profile)
    {
        var station = _stations.Get(stationId);
        if (station is null || station.Mode != ScheduleMode.Weather)
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
        var screens = WeatherCycle.Timeline(config, locations.Count, nowUtc, entry.EndUtc, entry.StartUtc, zone);
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

        var places = locations.Select(l =>
        {
            var report = _weather.Get(l.Id);
            return new PlaceWeather(l, report, WeatherService.IsStale(report, config, nowUtc));
        }).ToList();
        var attribution = places.Select(p => p.Report?.Attribution).FirstOrDefault(a => !string.IsNullOrEmpty(a)) ?? _weather.Provider.Attribution;
        var logoPath = config.ShowLogo && !string.IsNullOrEmpty(station.LogoFileName) ? _paths.LogoFile(station.LogoFileName) : null;
        var unixStart = (long)Math.Floor((segStart - DateTime.UnixEpoch).TotalSeconds);

        var slides = new List<WeatherSlide>(kept.Count);
        foreach (var s in kept)
        {
            var place = s.Screen.LocationIndex >= 0 && s.Screen.LocationIndex < places.Count ? places[s.Screen.LocationIndex] : null;
            var request = new ScreenRequest(s.Screen.Kind, place, places, config, nowUtc, zone, logoPath, profile.Width, profile.Height, attribution);
            var path = _renderer.Render(station.Id, request);
            var offset = place is null ? (long)zone.GetUtcOffset(nowUtc).TotalSeconds : PlaceOffsetSeconds(place, zone, nowUtc);
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
                        _logger.LogWarning("TvStation weather [{Station}]: music file missing, skipping: {Path}", station.Name, piece.Track.Path);
                        continue;
                    }

                    audio.Add(new WeatherAudio(piece.Track.Path, piece.InPoint.TotalSeconds, piece.Duration.TotalSeconds));
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

    /// <summary>"Title – Artist" or just the title.</summary>
    public static string CreditText(MusicTrack track)
        => string.IsNullOrWhiteSpace(track.Artist) ? track.Title : track.Title + " – " + track.Artist;

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
