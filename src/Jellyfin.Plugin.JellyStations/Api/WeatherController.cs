using System.Net.Mime;
using Jellyfin.Plugin.JellyStations.Api.Dto;
using Jellyfin.Plugin.JellyStations.Models;
using Jellyfin.Plugin.JellyStations.Scheduling;
using Jellyfin.Plugin.JellyStations.Services;
using Jellyfin.Plugin.JellyStations.Storage;
using Jellyfin.Plugin.JellyStations.Weather;
using Jellyfin.Plugin.JellyStations.Weather.Radar;
using Jellyfin.Plugin.JellyStations.Weather.Screens;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyStations.Api;

/// <summary>Weather channel support for the editor: place search, fetch status, a forced refresh and screen previews.</summary>
[ApiController]
[Route("JellyStations/Weather")]
[Authorize(Policy = Policies.RequiresElevation)]
[Produces(MediaTypeNames.Application.Json)]
public sealed class WeatherController : ControllerBase
{
    private readonly WeatherService _weather;
    private readonly RadarService _radar;
    private readonly StationService _stations;
    private readonly WeatherScreenRenderer _renderer;
    private readonly JellyStationsPaths _paths;

    public WeatherController(WeatherService weather, RadarService radar, StationService stations, WeatherScreenRenderer renderer, JellyStationsPaths paths)
    {
        _weather = weather;
        _radar = radar;
        _stations = stations;
        _renderer = renderer;
        _paths = paths;
    }

    /// <summary>Places matching a name, through the server (the browser never talks to the provider).</summary>
    [HttpGet("Search")]
    public async Task<ActionResult<IEnumerable<WeatherPlaceDto>>> Search([FromQuery] string q, [FromQuery] int count = 8, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2)
        {
            return Ok(Array.Empty<WeatherPlaceDto>());
        }

        try
        {
            var results = await _weather.Provider.SearchAsync(q, Math.Clamp(count, 1, 20), ct).ConfigureAwait(false);
            return Ok(results.Select(r => new WeatherPlaceDto
            {
                Name = r.Name,
                Region = string.Join(", ", new[] { r.Region, r.Country }.Where(s => !string.IsNullOrWhiteSpace(s))),
                Latitude = r.Latitude,
                Longitude = r.Longitude,
                TimeZoneId = r.TimeZoneId,
                Population = r.Population
            }));
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new ValidationErrorDto { Errors = new List<string> { "Place search failed: " + ex.Message } });
        }
    }

    /// <summary>How each place of a station is doing: last fetch, last success, the current temperature.</summary>
    [HttpGet("Status/{stationId}")]
    public ActionResult<WeatherStatusDto> Status(Guid stationId)
    {
        var station = _stations.Get(stationId);
        if (station is null)
        {
            return NotFound();
        }

        return Ok(BuildStatus(station));
    }

    /// <summary>Fetches every place of a station now and returns the new status.</summary>
    [HttpPost("Refresh/{stationId}")]
    public async Task<ActionResult<WeatherStatusDto>> Refresh(Guid stationId, CancellationToken ct)
    {
        var station = _stations.Get(stationId);
        if (station is null)
        {
            return NotFound();
        }

        await _weather.RefreshAsync(station, ct).ConfigureAwait(false);
        if (station.Weather.ShowRadar)
        {
            await _radar.RefreshAsync(station, ct).ConfigureAwait(false);
        }

        return Ok(BuildStatus(station));
    }

    /// <summary>One screen as it would air right now, as a PNG, for the editor's preview.</summary>
    [HttpGet("Preview/{stationId}")]
    [Produces("image/png")]
    public ActionResult Preview(Guid stationId, [FromQuery] string screen = "Current", [FromQuery] int location = 0, [FromQuery] int width = 1280)
    {
        var station = _stations.Get(stationId);
        if (station is null)
        {
            return NotFound();
        }

        if (!Enum.TryParse<WeatherScreenKind>(screen, true, out var kind))
        {
            kind = WeatherScreenKind.Current;
        }

        var config = station.Weather;
        var locations = config.Locations.Where(l => l.IsValid).ToList();
        var now = DateTime.UtcNow;
        var places = locations.Select(l =>
        {
            var report = _weather.Get(l.Id);
            return new PlaceWeather(l, report, WeatherService.IsStale(report, config, now));
        }).ToList();
        var place = kind == WeatherScreenKind.Travel ? null : places.Count == 0 ? null : places[Math.Clamp(location, 0, places.Count - 1)];
        if (kind != WeatherScreenKind.Travel && place is null)
        {
            return NotFound(new ValidationErrorDto { Errors = new List<string> { "The station has no places yet." } });
        }

        RadarFrameRequest? radar = null;
        if (kind == WeatherScreenKind.Radar && place is not null)
        {
            var loop = _radar.Get(place.Location.Id);
            if (loop is null)
            {
                return NotFound(new ValidationErrorDto { Errors = new List<string> { "No radar frames for " + place.Location.Name + " yet." } });
            }

            var frame = loop.Latest;
            radar = new RadarFrameRequest(loop.SourceId, loop.SourceName, loop.Attribution, frame.TimeUtc, frame.Path, loop.Frames.Count - 1, loop.Frames.Count, WebMercator.ZoomOf(loop.Zoom));
        }

        var w = Math.Clamp(width, 320, 1920);
        var h = w * 9 / 16;
        var attribution = places.Select(p => p.Report?.Attribution).FirstOrDefault(a => !string.IsNullOrEmpty(a)) ?? _weather.Provider.Attribution;
        var logo = config.ShowLogo && !string.IsNullOrEmpty(station.LogoFileName) ? _paths.LogoFile(station.LogoFileName) : null;
        var request = new ScreenRequest(kind, place, places, config, now, WeeklySlotScheduler.ResolveTimeZone(station.TimeZoneId), logo, w, h, attribution, radar);
        var path = _renderer.Render(station.Id, request);
        return PhysicalFile(path, "image/png");
    }

    private WeatherStatusDto BuildStatus(Station station)
    {
        var now = DateTime.UtcNow;
        var dto = new WeatherStatusDto { StationId = station.Id, Provider = _weather.Provider.Id, Attribution = _weather.Provider.Attribution };
        foreach (var location in station.Weather.Locations)
        {
            var status = _weather.Status(location.Id);
            var report = _weather.Get(location.Id);
            var radarStatus = _radar.Status(location.Id);
            var loop = _radar.Get(location.Id);
            dto.Locations.Add(new WeatherLocationStatusDto
            {
                RadarSource = radarStatus.SourceName ?? loop?.SourceName,
                RadarFrames = loop?.Frames.Count ?? 0,
                RadarLatestUtc = loop?.Latest.TimeUtc,
                RadarError = radarStatus.LastError,
                LocationId = location.Id,
                Name = location.Name,
                Valid = location.IsValid,
                LastAttemptUtc = status.LastAttemptUtc,
                LastSuccessUtc = status.LastSuccessUtc ?? report?.FetchedUtc,
                LastError = status.LastError,
                Failures = status.Failures,
                Stale = WeatherService.IsStale(report, station.Weather, now),
                TemperatureC = report?.Current.TemperatureC,
                WeatherCode = report?.Current.WeatherCode,
                Conditions = report is null ? null : WmoWeather.Describe(report.Current.WeatherCode),
                TimeZoneId = location.TimeZoneId ?? report?.TimeZoneId
            });
        }

        return dto;
    }
}
