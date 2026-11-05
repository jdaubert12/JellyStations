using System.Net.Mime;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyStations.Api.Dto;
using Jellyfin.Plugin.JellyStations.Library;
using Jellyfin.Plugin.JellyStations.Models;
using Jellyfin.Plugin.JellyStations.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyStations.Api;

[ApiController]
[Route("JellyStations/Library")]
[Authorize(Policy = Policies.RequiresElevation)]
[Produces(MediaTypeNames.Application.Json)]
public sealed class LibraryController : ControllerBase
{
    private readonly LibrarySearchService _search;
    private readonly LibraryAnalysisService _analysis;
    private readonly StationService _stations;
    private readonly LibraryContentResolver _resolver;
    private readonly ClipFolderScanner _clips;

    public LibraryController(LibrarySearchService search, LibraryAnalysisService analysis, StationService stations, LibraryContentResolver resolver, ClipFolderScanner clips)
    {
        _clips = clips;
        _search = search;
        _analysis = analysis;
        _stations = stations;
        _resolver = resolver;
    }

    /// <summary>The audio and subtitle languages on an item's files (every episode of a series, every film of a collection), with counts.</summary>
    [HttpGet("Items/{id}/Languages")]
    public ActionResult<TrackLanguagesDto> Languages([FromRoute] Guid id) => Ok(_resolver.TrackLanguages(id));

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
    /// A folder on the server as a bumper source would see it: where the name leads (an absolute path, or a folder
    /// under the plugin's bumpers folder), whether it is there and how many video files it holds. Nothing is probed.
    /// </summary>
    [HttpGet("ClipFolder")]
    public ActionResult<ClipFolderDto> ClipFolder([FromQuery] string? path)
    {
        var files = _clips.Files(path);
        return Ok(new ClipFolderDto
        {
            Path = Text.TrimOrNull(path) ?? string.Empty,
            ResolvedPath = _clips.Resolve(path) ?? string.Empty,
            Exists = files is not null,
            Videos = files?.Count ?? 0
        });
    }

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
        var (onChannels, channels) = _stations.ItemsOnChannels(includeDisabled);
        return Ok(LibraryCoverage.Build(_analysis.Facts(refresh), onChannels, channels, DateTime.UtcNow));
    }
}
