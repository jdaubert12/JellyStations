using System.Net.Mime;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.TvStation.Api.Dto;
using Jellyfin.Plugin.TvStation.Library;
using Jellyfin.Plugin.TvStation.Models;
using Jellyfin.Plugin.TvStation.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.TvStation.Api;

[ApiController]
[Route("TvStation/Library")]
[Authorize(Policy = Policies.RequiresElevation)]
[Produces(MediaTypeNames.Application.Json)]
public sealed class LibraryController : ControllerBase
{
    private readonly LibrarySearchService _search;
    private readonly LibraryAnalysisService _analysis;
    private readonly StationService _stations;

    public LibraryController(LibrarySearchService search, LibraryAnalysisService analysis, StationService stations)
    {
        _search = search;
        _analysis = analysis;
        _stations = stations;
    }

    /// <summary>Searches the library. kinds is a comma-separated list: Movie, Series, Season, Episode, BoxSet, Playlist.</summary>
    [HttpGet("Search")]
    public ActionResult<IEnumerable<LibraryItemDto>> Search([FromQuery] string? q, [FromQuery] string? kinds, [FromQuery] int limit = 50)
    {
        var kindList = string.IsNullOrWhiteSpace(kinds)
            ? null
            : kinds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return Ok(_search.Search(q, kindList, limit));
    }

    [HttpGet("Items/{id}/Children")]
    public ActionResult<IEnumerable<LibraryItemDto>> Children([FromRoute] Guid id) => Ok(_search.Children(id));

    [HttpGet("Genres")]
    public ActionResult<IEnumerable<GenreDto>> Genres() => Ok(_search.Genres());

    [HttpGet("Tags")]
    public ActionResult<IEnumerable<TagDto>> Tags() => Ok(_search.Tags());

    [HttpGet("Playlists")]
    public ActionResult<IEnumerable<LibraryItemDto>> Playlists() => Ok(_search.OfKind(BaseItemKind.Playlist));

    [HttpGet("Collections")]
    public ActionResult<IEnumerable<LibraryItemDto>> Collections() => Ok(_search.OfKind(BaseItemKind.BoxSet));

    [HttpGet("Libraries")]
    public ActionResult<IEnumerable<LibraryFolderDto>> Libraries() => Ok(_search.Libraries());

    /// <summary>
    /// One pass over the library: hours per genre, series with episode counts and ratings, collections, franchises
    /// detected from titles, decades, ratings, tags and libraries. Cached for 15 minutes unless refresh=true.
    /// </summary>
    [HttpGet("Analysis")]
    public ActionResult<LibraryAnalysisDto> Analysis([FromQuery] bool refresh = false) => Ok(_analysis.Get(refresh));

    /// <summary>
    /// Movies and series no channel plays, from the compiled content of every enabled station (every station with
    /// includeDisabled=true). Clips a station uses as bumpers or idents count as on that channel. refresh=true
    /// re-reads the library.
    /// </summary>
    [HttpGet("Coverage")]
    public ActionResult<CoverageDto> Coverage([FromQuery] bool includeDisabled = false, [FromQuery] bool refresh = false)
    {
        var onChannels = new HashSet<Guid>();
        var channels = 0;
        foreach (var station in _stations.GetAll().Where(s => includeDisabled || s.Enabled))
        {
            if (_stations.GetCompiled(station.Id) is not { } compiled)
            {
                continue;
            }

            channels++;
            foreach (var item in Played(compiled))
            {
                onChannels.Add(item.ItemId);
            }
        }

        return Ok(LibraryCoverage.Build(_analysis.Facts(refresh), onChannels, channels, DateTime.UtcNow));
    }

    /// <summary>Everything a station plays: pool, lineup, filler and block content, plus its bumper and ident clips.</summary>
    private static IEnumerable<PlayableItem> Played(CompiledStation compiled)
        => compiled.Pool.Concat(compiled.Lineup).Concat(compiled.Filler).Concat(compiled.Blocks.Values.SelectMany(b => b))
            .Concat(compiled.Bumpers).Concat(compiled.PreRoll).Concat(compiled.PostRoll);
}
