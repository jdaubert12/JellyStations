using System.Net.Mime;
using Jellyfin.Plugin.TvStation.Api.Dto;
using Jellyfin.Plugin.TvStation.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.TvStation.Api;

[ApiController]
[Route("TvStation/Suggestions")]
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

    /// <summary>Creates the suggestions with the given keys, numbered after the highest existing channel.</summary>
    [HttpPost("Create")]
    public ActionResult<SuggestionCreateResultDto> Create([FromBody] SuggestionCreateRequestDto request)
        => Ok(_suggestions.Create(request.Keys ?? new List<string>(), request.TimeZoneId, request.MinHours, request.MinEpisodes));
}
