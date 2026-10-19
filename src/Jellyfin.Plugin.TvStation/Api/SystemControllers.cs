using System.Net.Mime;
using System.Reflection;
using Jellyfin.Plugin.TvStation.Api.Dto;
using Jellyfin.Plugin.TvStation.Library;
using Jellyfin.Plugin.TvStation.Models;
using Jellyfin.Plugin.TvStation.Services;
using Jellyfin.Plugin.TvStation.Storage;
using Jellyfin.Plugin.TvStation.Streaming;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.TvStation.Api;

[ApiController]
[Route("TvStation/Settings")]
[Authorize(Policy = Policies.RequiresElevation)]
[Produces(MediaTypeNames.Application.Json)]
public sealed class SettingsController : ControllerBase
{
    private readonly SettingsRepository _settings;
    private readonly StreamSessionManager _sessions;
    private readonly StationService _stations;

    public SettingsController(SettingsRepository settings, StreamSessionManager sessions, StationService stations)
    {
        _settings = settings;
        _sessions = sessions;
        _stations = stations;
    }

    [HttpGet]
    public ActionResult<GlobalSettings> Get() => Ok(_settings.Get());

    [HttpPut]
    public ActionResult<GlobalSettings> Put([FromBody] GlobalSettings settings)
    {
        settings.DefaultEncoding ??= new EncodingProfile();
        var errors = StationService.ValidateEncoding(settings.DefaultEncoding);
        if (settings.LingerSeconds < 0 || settings.LingerSeconds > 600)
        {
            errors.Add("Linger must be between 0 and 600 seconds.");
        }

        if (settings.MaxGuideDays < 1 || settings.MaxGuideDays > 30)
        {
            errors.Add("Max guide days must be between 1 and 30.");
        }

        if (settings.MaxConcurrentSessions < 1 || settings.MaxConcurrentSessions > 64)
        {
            errors.Add("Max concurrent sessions must be between 1 and 64.");
        }

        if (!string.IsNullOrWhiteSpace(settings.FfmpegPathOverride) && !System.IO.File.Exists(settings.FfmpegPathOverride))
        {
            errors.Add("ffmpeg override path does not exist on the server.");
        }

        if (errors.Count > 0)
        {
            return BadRequest(new ValidationErrorDto { Errors = errors });
        }

        settings.ExcludedTags = StationService.NormalizeTags(settings.ExcludedTags);
        var previous = _settings.Get();
        _settings.Save(settings);
        if (JsonFileStore.Serialize(previous.DefaultEncoding) != JsonFileStore.Serialize(settings.DefaultEncoding))
        {
            _sessions.StopAll();
        }

        if (!previous.ExcludedTags.OrderBy(t => t, StringComparer.OrdinalIgnoreCase).SequenceEqual(settings.ExcludedTags.OrderBy(t => t, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
        {
            _stations.RecompileAll();
        }

        return Ok(settings);
    }
}

[ApiController]
[Route("TvStation/Guide")]
[Authorize(Policy = Policies.RequiresElevation)]
public sealed class GuideController : ControllerBase
{
    private readonly GuideRefreshService _guide;

    public GuideController(GuideRefreshService guide)
    {
        _guide = guide;
    }

    [HttpPost("Refresh")]
    public IActionResult Refresh() => _guide.TriggerNow() ? Accepted() : StatusCode(503, new ValidationErrorDto { Errors = { "The Live TV guide refresh task is not available." } });
}

[ApiController]
[Route("TvStation")]
[Authorize(Policy = Policies.RequiresElevation)]
[Produces(MediaTypeNames.Application.Json)]
public sealed class StatusController : ControllerBase
{
    private readonly StreamSessionManager _sessions;
    private readonly TvStationPaths _paths;
    private readonly StationService _stations;

    public StatusController(StreamSessionManager sessions, TvStationPaths paths, StationService stations)
    {
        _sessions = sessions;
        _paths = paths;
        _stations = stations;
    }

    /// <summary>Per-station health from the last compile: empty sources, under a day of unique content, items skipped.</summary>
    [HttpGet("Health")]
    public ActionResult<HealthReportDto> Health()
    {
        var stations = _stations.GetAll()
            .OrderBy(s => s.ChannelNumber, StringComparer.OrdinalIgnoreCase)
            .Select(s => StationHealth.Build(s, _stations.GetCompiled(s.Id)))
            .ToList();
        return Ok(new HealthReportDto
        {
            GeneratedUtc = DateTime.UtcNow.ToString("O"),
            Stations = stations,
            StationsWithWarnings = stations.Count(s => !s.Healthy)
        });
    }

    [HttpGet("Status")]
    public ActionResult<StatusDto> Status()
    {
        var ffmpeg = _sessions.FfmpegPath;
        return Ok(new StatusDto
        {
            FfmpegPath = ffmpeg,
            FfmpegExists = System.IO.File.Exists(ffmpeg),
            DataPath = _paths.Root,
            Version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?",
            ServerTimeUtc = DateTime.UtcNow.ToString("O"),
            Sessions = _sessions.Sessions.Where(s => !s.IsDisposed).Select(s => new SessionStatusDto
            {
                StationId = s.StationId,
                StationName = s.StationName,
                UniqueId = s.UniqueId,
                Consumers = s.ConsumerCount,
                Readers = s.ReaderCount,
                StartedUtc = s.CreatedUtc,
                BytesStreamed = s.BytesStreamed,
                EncoderPid = s.EncoderPid,
                Current = s.CurrentEntry is null ? null : Map.Entry(s.CurrentEntry, DateTime.UtcNow - s.CurrentEntry.StartUtc),
                RecentLog = s.RecentErrors.ToList()
            }).ToList()
        });
    }

    [HttpGet("TimeZones")]
    public ActionResult<IEnumerable<string>> TimeZones()
    {
        return Ok(TimeZoneInfo.GetSystemTimeZones().Select(t => t.Id).OrderBy(t => t, StringComparer.Ordinal).ToList());
    }
}
