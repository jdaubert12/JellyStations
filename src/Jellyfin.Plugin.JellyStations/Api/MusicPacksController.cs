using System.Net.Mime;
using Jellyfin.Plugin.JellyStations.Api.Dto;
using Jellyfin.Plugin.JellyStations.Music;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyStations.Api;

/// <summary>Music packs for weather channels: the catalogue with what is installed, and starting or cancelling a download.</summary>
[ApiController]
[Route("JellyStations/MusicPacks")]
[Authorize(Policy = Policies.RequiresElevation)]
[Produces(MediaTypeNames.Application.Json)]
public sealed class MusicPacksController : ControllerBase
{
    private readonly MusicPackService _packs;

    public MusicPacksController(MusicPackService packs)
    {
        _packs = packs;
    }

    [HttpGet]
    public ActionResult<IEnumerable<MusicPackStatusDto>> List() => Ok(_packs.StatusAll());

    [HttpGet("{id}")]
    public ActionResult<MusicPackStatusDto> Status(string id)
    {
        var pack = _packs.Get(id);
        return pack is null ? NotFound() : Ok(_packs.Status(pack));
    }

    /// <summary>Starts fetching the pack's tracks into the plugin's music folder; poll the status for progress.</summary>
    [HttpPost("{id}/Install")]
    public ActionResult<MusicPackStatusDto> Install(string id)
    {
        var pack = _packs.Get(id);
        if (pack is null)
        {
            return NotFound();
        }

        try
        {
            return Ok(_packs.Install(pack));
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(StatusCodes.Status409Conflict, ValidationErrorDto.Of(ex.Message));
        }
    }

    [HttpPost("{id}/Cancel")]
    public ActionResult<MusicPackStatusDto> Cancel(string id)
    {
        var pack = _packs.Get(id);
        return pack is null ? NotFound() : Ok(_packs.Cancel(pack));
    }
}
