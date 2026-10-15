using System.Net.Mime;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.TvStation.Api.Dto;
using Jellyfin.Plugin.TvStation.Library;
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

    public LibraryController(LibrarySearchService search)
    {
        _search = search;
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
}
