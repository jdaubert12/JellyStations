using System.Net.Mime;
using Jellyfin.Plugin.TvStation.Api.Dto;
using Jellyfin.Plugin.TvStation.Library;
using Jellyfin.Plugin.TvStation.Models;
using Jellyfin.Plugin.TvStation.Scheduling;
using Jellyfin.Plugin.TvStation.Services;
using Jellyfin.Plugin.TvStation.Streaming;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.TvStation.Api;

[ApiController]
[Route("TvStation/Stations")]
[Authorize(Policy = Policies.RequiresElevation)]
[Produces(MediaTypeNames.Application.Json)]
public sealed class StationsController : ControllerBase
{
    private readonly StationService _stations;
    private readonly IScheduleResolver _resolver;
    private readonly StreamSessionManager _sessions;
    private readonly StationImportResolver _importResolver;

    public StationsController(StationService stations, IScheduleResolver resolver, StreamSessionManager sessions, StationImportResolver importResolver)
    {
        _importResolver = importResolver;
        _stations = stations;
        _resolver = resolver;
        _sessions = sessions;
    }

    [HttpGet]
    public ActionResult<IEnumerable<StationSummaryDto>> List()
    {
        var now = DateTime.UtcNow;
        var result = _stations.GetAll().Select(s =>
        {
            var compiled = _stations.GetCompiled(s.Id);
            var resolved = _resolver.Resolve(s.Id, now);
            var fault = _sessions.LastFault(s.Id);
            return new StationSummaryDto
            {
                Id = s.Id,
                Name = s.Name,
                ChannelNumber = s.ChannelNumber,
                Mode = s.Mode,
                Enabled = s.Enabled,
                ItemCount = compiled?.TotalItemCount ?? 0,
                MusicCount = compiled?.Music.Count ?? 0,
                LocationCount = s.Mode == ScheduleMode.Weather ? s.Weather.Locations.Count(l => l.IsValid) : 0,
                ExcludedCount = compiled?.Excluded.Count ?? 0,
                Revision = s.Revision,
                HasLogo = _stations.LogoPath(s) is not null,
                NowPlaying = resolved is null ? null : Map.Entry(resolved.Entry, resolved.Offset),
                Streaming = _sessions.Get(s.Id) is { IsDisposed: false },
                Seasonal = SeasonWindows.IsSeasonal(s),
                InSeason = SeasonWindows.IsActive(s, now),
                NextSeasonChange = SeasonWindows.NextChange(s, now)?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                Next = resolved is null ? new List<ScheduleEntryDto>() : NextPrograms(s.Id, now),
                LastError = fault?.Message,
                LastErrorUtc = fault?.Utc,
                Health = StationHealth.Build(s, compiled)
            };
        });
        return Ok(result.ToList());
    }

    /// <summary>The next two programs after the one airing now; bumper clips are skipped and a slate only shows when nothing else follows.</summary>
    private List<ScheduleEntryDto> NextPrograms(Guid stationId, DateTime now)
    {
        var upcoming = _resolver.Enumerate(stationId, now, now.AddHours(12))
            .Where(e => e.StartUtc > now && e.Kind != EntryKind.Bumper)
            .ToList();
        var programs = upcoming.Where(e => e.Kind == EntryKind.Program).Take(2).ToList();
        if (programs.Count == 0 && upcoming.Count > 0)
        {
            programs.Add(upcoming[0]);
        }

        return programs.Select(e => Map.Entry(e, null)).ToList();
    }

    /// <summary>All station definitions as a JSON array suitable for backup or for POST Import.</summary>
    [HttpGet("Export")]
    public ActionResult<IEnumerable<Station>> Export()
    {
        return Ok(_stations.GetAll().OrderBy(s => s.ChannelNumber, StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>
    /// Imports station definitions. Each station is matched to an existing one by id, then by name, and updated;
    /// unmatched stations are created. Item references whose ids are unknown on this server are matched by the
    /// display name stored beside them; what cannot be matched is dropped and reported. Logos are not part of the import.
    /// </summary>
    [HttpPost("Import")]
    public ActionResult<ImportResultDto> Import([FromBody] List<Station> stations)
    {
        var result = new ImportResultDto();
        var existing = _stations.GetAll();
        foreach (var draft in stations)
        {
            var name = draft.Name ?? "(unnamed)";
            try
            {
                var resolution = _importResolver.Resolve(draft);
                result.Resolved.AddRange(resolution.Resolved.Select(r => name + ": " + r));
                result.Unmatched.AddRange(resolution.Unmatched.Select(r => name + ": " + r));

                var match = existing.FirstOrDefault(s => draft.Id != Guid.Empty && s.Id == draft.Id)
                    ?? existing.FirstOrDefault(s => string.Equals(s.Name?.Trim(), draft.Name?.Trim(), StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    _stations.Update(match.Id, draft);
                    result.Updated.Add(name);
                }
                else
                {
                    draft.Id = Guid.Empty;
                    _stations.Create(draft);
                    result.Created.Add(name);
                }
            }
            catch (ValidationException ex)
            {
                result.Errors.Add(name + ": " + string.Join(" ", ex.Errors));
            }
            catch (Exception ex)
            {
                result.Errors.Add(name + ": " + ex.Message);
            }
        }

        return Ok(result);
    }

    [HttpGet("{id}")]
    public ActionResult<StationDetailDto> Get([FromRoute] Guid id)
    {
        var station = _stations.Get(id);
        if (station is null)
        {
            return NotFound();
        }

        var compiled = _stations.GetCompiled(id);
        return Ok(new StationDetailDto
        {
            Station = station,
            Compiled = compiled is null ? null : StationService.ToCompileResult(compiled, false),
            CycleDuration = CycleDuration(station, compiled)
        });
    }

    [HttpPost]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ValidationErrorDto))]
    public ActionResult<Station> Create([FromBody] Station draft)
    {
        try
        {
            return Ok(_stations.Create(draft));
        }
        catch (ValidationException ex)
        {
            return BadRequest(new ValidationErrorDto { Errors = ex.Errors });
        }
    }

    [HttpPut("{id}")]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ValidationErrorDto))]
    public ActionResult<Station> Update([FromRoute] Guid id, [FromBody] Station draft)
    {
        try
        {
            return Ok(_stations.Update(id, draft));
        }
        catch (ValidationException ex)
        {
            return BadRequest(new ValidationErrorDto { Errors = ex.Errors });
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpDelete("{id}")]
    public IActionResult Delete([FromRoute] Guid id)
    {
        return _stations.Delete(id) ? NoContent() : NotFound();
    }

    /// <summary>Re-expands every station against the library (e.g. after adding a batch of films) and refreshes the guide if anything changed.</summary>
    [HttpPost("RecompileAll")]
    public ActionResult<RecompileAllResultDto> RecompileAll() => Ok(_stations.RecompileAll());

    [HttpPost("{id}/Recompile")]
    public ActionResult<CompileResultDto> Recompile([FromRoute] Guid id)
    {
        try
        {
            return Ok(_stations.Recompile(id));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpGet("{id}/Schedule")]
    public ActionResult<IEnumerable<ScheduleEntryDto>> Schedule([FromRoute] Guid id, [FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] int hours = 24)
    {
        if (_stations.Get(id) is null)
        {
            return NotFound();
        }

        var start = (from ?? DateTime.UtcNow).ToUniversalTime();
        var end = (to ?? start.AddHours(Math.Clamp(hours, 1, 24 * 21))).ToUniversalTime();
        return Ok(_resolver.Enumerate(id, start, end).Select(e => Map.Entry(e, null)).ToList());
    }

    /// <summary>Compiled content of a saved station, excluded items last.</summary>
    [HttpGet("{id}/Items")]
    public ActionResult<ContentListDto> Items([FromRoute] Guid id, [FromQuery] int offset = 0, [FromQuery] int limit = 5000)
    {
        var station = _stations.Get(id);
        var compiled = station is null ? null : _stations.GetCompiled(id);
        if (station is null || compiled is null)
        {
            return NotFound();
        }

        return Ok(StationService.ToContentList(station, compiled, offset, limit));
    }

    [HttpGet("{id}/NowPlaying")]
    public ActionResult<ScheduleEntryDto> NowPlaying([FromRoute] Guid id)
    {
        if (_stations.Get(id) is null)
        {
            return NotFound();
        }

        var r = _resolver.Resolve(id, DateTime.UtcNow);
        return r is null ? NotFound() : Ok(Map.Entry(r.Entry, r.Offset));
    }

    [HttpPost("{id}/Logo")]
    [Consumes("image/png", "image/jpeg", "image/webp", "image/gif", "application/octet-stream")]
    public async Task<IActionResult> UploadLogo([FromRoute] Guid id)
    {
        if (_stations.Get(id) is null)
        {
            return NotFound();
        }

        var ext = (Request.ContentType ?? string.Empty).ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            _ => ".png"
        };

        using var ms = new MemoryStream();
        await Request.Body.CopyToAsync(ms).ConfigureAwait(false);
        if (ms.Length == 0 || ms.Length > 5 * 1024 * 1024)
        {
            return BadRequest(new ValidationErrorDto { Errors = { "Logo must be between 1 byte and 5 MB." } });
        }

        ms.Position = 0;
        _stations.SaveLogo(id, ext, ms);
        return NoContent();
    }

    [HttpGet("{id}/Logo")]
    [AllowAnonymous]
    public IActionResult GetLogo([FromRoute] Guid id)
    {
        var station = _stations.Get(id);
        var path = station is null ? null : _stations.LogoPath(station);
        if (path is null)
        {
            return NotFound();
        }

        var mime = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            _ => "image/png"
        };
        return PhysicalFile(path, mime);
    }

    [HttpPost("{id}/Stream/Restart")]
    public IActionResult RestartStream([FromRoute] Guid id)
    {
        _sessions.RestartStation(id);
        return NoContent();
    }

    /// <summary>Forgets the last streaming fault shown on the station row.</summary>
    [HttpPost("{id}/Stream/ClearError")]
    public IActionResult ClearStreamError([FromRoute] Guid id)
    {
        _sessions.ClearFault(id);
        return NoContent();
    }

    [HttpPost("{id}/Stream/Stop")]
    public IActionResult StopStream([FromRoute] Guid id)
    {
        _sessions.StopStation(id);
        return NoContent();
    }

    private static TimeSpan? CycleDuration(Station station, CompiledStation? compiled)
    {
        if (compiled is null || station.Mode == ScheduleMode.Slots)
        {
            return null;
        }

        var items = station.Mode == ScheduleMode.Lineup ? compiled.Lineup : compiled.Pool;
        var grid = TimeSpan.FromMinutes(station.PaddingGridMinutes);
        long ticks = 0;
        foreach (var i in items)
        {
            var d = i.Runtime;
            if (grid > TimeSpan.Zero || station.PaddingByLength)
            {
                d = Scheduling.Padding.Pad(d, grid, station.PaddingByLength);
            }

            ticks += d.Ticks;
        }

        return TimeSpan.FromTicks(ticks);
    }
}

internal static class Map
{
    public static ScheduleEntryDto Entry(ScheduleEntry e, TimeSpan? offset)
    {
        return new ScheduleEntryDto
        {
            ProgramId = e.ProgramId,
            StartUtc = DateTime.SpecifyKind(e.StartUtc, DateTimeKind.Utc),
            EndUtc = DateTime.SpecifyKind(e.EndUtc, DateTimeKind.Utc),
            Kind = e.Kind,
            Title = e.Title,
            EpisodeTitle = e.Item?.IsSeries == true ? e.Item.EpisodeTitle : null,
            SeasonNumber = e.Item?.SeasonNumber,
            EpisodeNumber = e.Item?.EpisodeNumber,
            ItemId = e.Item?.ItemId,
            IsTruncated = e.IsTruncated,
            SourceLabel = e.SourceLabel,
            OffsetSeconds = offset?.TotalSeconds,
            IsMovie = e.Item?.IsMovie ?? false,
            IsSeries = e.Item?.IsSeries ?? false
        };
    }
}
