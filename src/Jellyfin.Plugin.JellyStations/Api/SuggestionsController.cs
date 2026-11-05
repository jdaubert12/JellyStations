using System.Net.Mime;
using Jellyfin.Plugin.JellyStations.Api.Dto;
using Jellyfin.Plugin.JellyStations.Models;
using Jellyfin.Plugin.JellyStations.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyStations.Api;

[ApiController]
[Route("JellyStations/Suggestions")]
[Authorize(Policy = Policies.RequiresElevation)]
[Produces(MediaTypeNames.Application.Json)]
public sealed class SuggestionsController : ControllerBase
{
    private readonly StationSuggestionService _suggestions;

    public SuggestionsController(StationSuggestionService suggestions)
    {
        _suggestions = suggestions;
    }

    /// <summary>
    /// Stations the library could support, built from the library analysis. refresh=true recomputes the analysis;
    /// minHours / minEpisodes lower the content a rule needs (defaults 8 h and 20 episodes), for small libraries.
    /// </summary>
    [HttpGet]
    public ActionResult<IEnumerable<StationSuggestionDto>> List([FromQuery] bool refresh = false, [FromQuery] double? minHours = null, [FromQuery] int? minEpisodes = null)
        => Ok(_suggestions.List(refresh, minHours, minEpisodes));

    /// <summary>
    /// What the suggestions page shows: the suggestions with the hours no enabled station plays yet (the fully
    /// covered ones flagged), the rules that fell just short of their threshold, the libraries of short clips that
    /// could be the bumpers, the thresholds in force (from Settings) and how many stations were counted.
    /// </summary>
    [HttpGet("Report")]
    public ActionResult<SuggestionReportDto> Report([FromQuery] bool refresh = false, [FromQuery] double? minHours = null, [FromQuery] int? minEpisodes = null)
        => Ok(_suggestions.Report(refresh, minHours, minEpisodes));

    /// <summary>
    /// Creates the suggestions with the given keys: numbered from FirstChannelNumber (default: after the highest
    /// existing channel, skipping numbers in use), in TimeZoneId, with the look of the station template TemplateId
    /// when given, and a generated logo unless Logo is false.
    /// </summary>
    [HttpPost("Create")]
    public ActionResult<SuggestionCreateResultDto> Create([FromBody] SuggestionCreateRequestDto request)
        => Ok(_suggestions.Create(request));

    /// <summary>One suggestion as an unsaved station with the same options applied, to open in the editor before creating it.</summary>
    [HttpPost("Draft")]
    public ActionResult<Station> Draft([FromBody] SuggestionDraftRequestDto request)
    {
        var draft = _suggestions.Draft(request);
        return draft is null ? NotFound(ValidationErrorDto.Of("No such suggestion; the library may have changed. Reload the list.")) : Ok(draft);
    }

    /// <summary>
    /// The next N hours one suggestion would play if it were created now with the same options; nothing is saved.
    /// A shuffled channel draws its order again when it is created, so this shows a sample day.
    /// </summary>
    [HttpPost("Preview")]
    public ActionResult<IEnumerable<ScheduleEntryDto>> Preview([FromBody] SuggestionDraftRequestDto request, [FromQuery] int hours = 24)
    {
        var start = DateTime.UtcNow;
        var end = start.AddHours(Math.Clamp(hours, 1, 24 * 14));
        try
        {
            var entries = _suggestions.Preview(request, start, end);
            return entries is null
                ? NotFound(ValidationErrorDto.Of("No such suggestion; the library may have changed. Reload the list."))
                : Ok(entries.Select(e => Map.Entry(e, null)).ToList());
        }
        catch (Exception ex)
        {
            return BadRequest(ValidationErrorDto.Of(ex.Message));
        }
    }
}
