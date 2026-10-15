using System.Net.Mime;
using Jellyfin.Plugin.TvStation.Api.Dto;
using Jellyfin.Plugin.TvStation.Models;
using Jellyfin.Plugin.TvStation.Services;
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

    public ScheduleController(StationService stations)
    {
        _stations = stations;
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
