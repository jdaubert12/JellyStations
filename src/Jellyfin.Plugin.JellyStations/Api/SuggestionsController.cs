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
}
