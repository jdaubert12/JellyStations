using System.Net.Mime;
using Jellyfin.Plugin.TvStation.Api.Dto;
using Jellyfin.Plugin.TvStation.Library;
using Jellyfin.Plugin.TvStation.Models;
using Jellyfin.Plugin.TvStation.Services;
using Jellyfin.Plugin.TvStation.Storage;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.TvStation.Api;

[ApiController]
[Route("TvStation/Schedule")]
[Authorize(Policy = Policies.RequiresElevation)]
[Produces(MediaTypeNames.Application.Json)]
public sealed class ScheduleController : ControllerBase
{
    private readonly StationService _stations;
    private readonly LibraryContentResolver _resolver;
    private readonly SettingsRepository _settings;

    public ScheduleController(StationService stations, LibraryContentResolver resolver, SettingsRepository settings)
    {
        _stations = stations;
        _resolver = resolver;
        _settings = settings;
    }

    /// <summary>
    /// Which existing source of a section already plays each item, so the editor can say "already included via Genre
    /// "Comedy"" next to a movie before (or after) it is hand-picked. Items nothing covers are left out of the answer.
    /// </summary>
    [HttpPost("CoveredBy")]
    public ActionResult<List<CoveredItemDto>> CoveredBy([FromBody] CoveredByRequestDto request)
    {
        try
        {
            var tags = StationCompiler.EffectiveExcludedTags(request.Draft ?? new Station(), _settings.Get().ExcludedTags);
            var covered = _resolver.CoveredBy(request.Sources ?? new List<ContentSource>(), request.ItemIds ?? new List<Guid>(), tags);
            return Ok(covered.Select(kv => new CoveredItemDto
            {
                ItemId = kv.Key,
                Kind = kv.Value.Kind,
                Name = kv.Value.DisplayName ?? kv.Value.Genre ?? kv.Value.Tag ?? string.Empty,
                Label = SourceExpansion.Describe(kv.Value)
            }).ToList());
        }
        catch (Exception ex)
        {
            return BadRequest(new ValidationErrorDto { Errors = { ex.Message } });
        }
    }

    /// <summary>Compiles an unsaved station draft in memory and lists what it would play.</summary>
    [HttpPost("PreviewItems")]
    public ActionResult<ContentListDto> PreviewItems([FromBody] Station draft, [FromQuery] int offset = 0, [FromQuery] int limit = 5000)
    {
        try
        {
            var compiled = _stations.CompileDraft(draft);
            return Ok(StationService.ToContentList(draft, compiled, offset, limit));
        }
        catch (Exception ex)
        {
            return BadRequest(new ValidationErrorDto { Errors = { ex.Message } });
        }
    }

    /// <summary>Compiles an unsaved station draft in memory and returns its schedule for the next N hours.</summary>
    [HttpPost("Preview")]
    public ActionResult<IEnumerable<ScheduleEntryDto>> Preview([FromBody] Station draft, [FromQuery] int hours = 24, [FromQuery] DateTime? from = null)
    {
        var start = (from ?? DateTime.UtcNow).ToUniversalTime();
        var end = start.AddHours(Math.Clamp(hours, 1, 24 * 14));
        try
        {
            var entries = _stations.Preview(draft, start, end);
            return Ok(entries.Select(e => Map.Entry(e, null)).ToList());
        }
        catch (Exception ex)
        {
            return BadRequest(new ValidationErrorDto { Errors = { ex.Message } });
        }
    }
}
