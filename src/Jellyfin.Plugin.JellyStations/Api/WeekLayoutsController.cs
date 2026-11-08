using System.Net.Mime;
using Jellyfin.Plugin.JellyStations.Api.Dto;
using Jellyfin.Plugin.JellyStations.Library;
using Jellyfin.Plugin.JellyStations.Models;
using Jellyfin.Plugin.JellyStations.Storage;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyStations.Api;

/// <summary>
/// Week layouts: the built-in ones plus those saved from a station's blocks (stored in the plugin settings). A
/// layout paints a whole broadcast week of blocks onto a station at once; the suggester fills the built-in ones
/// from the library.
/// </summary>
[ApiController]
[Route("JellyStations/WeekLayouts")]
[Authorize(Policy = Policies.RequiresElevation)]
[Produces(MediaTypeNames.Application.Json)]
public sealed class WeekLayoutsController : ControllerBase
{
    private readonly SettingsRepository _settings;
    private readonly StationImportResolver _resolver;

    public WeekLayoutsController(SettingsRepository settings, StationImportResolver resolver)
    {
        _settings = settings;
        _resolver = resolver;
    }

    /// <summary>Saved layouts first, then the built-in ones not shadowed by a saved one of the same name.</summary>
    [HttpGet]
    public ActionResult<IEnumerable<WeekLayout>> List() => Ok(WeekLayouts.Merge(Saved(_settings.Get())));

    [HttpGet("{id}")]
    public ActionResult<WeekLayout> Get([FromRoute] Guid id)
    {
        var layout = WeekLayouts.Find(Saved(_settings.Get()), id);
        return layout is null ? NotFound() : Ok(layout);
    }

    /// <summary>
    /// The layout's blocks ready to add to a station: fresh ids, item references whose ids are unknown here matched
    /// by name (a layout imported from another server, or a library that was re-added), the rest dropped and listed.
    /// </summary>
    [HttpPost("{id}/Apply")]
    public ActionResult<WeekLayoutApplyDto> Apply([FromRoute] Guid id)
    {
        var layout = WeekLayouts.Find(Saved(_settings.Get()), id);
        if (layout is null)
        {
            return NotFound();
        }

        var blocks = WeekLayouts.Blocks(layout);
        var resolution = _resolver.Resolve(new Station { Slots = new SlotsConfig { Blocks = blocks } });
        return Ok(new WeekLayoutApplyDto { Blocks = blocks, Resolved = resolution.Resolved, Unmatched = resolution.Unmatched });
    }

    /// <summary>
    /// Saves a station's blocks as a layout (with or without their sources), or replaces the saved layout with the
    /// same name or id. Saving under a built-in name takes that layout's place. Returns the full list.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ValidationErrorDto))]
    public ActionResult<IEnumerable<WeekLayout>> Save([FromBody] SaveWeekLayoutDto request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(ValidationErrorDto.Of("Give the layout a name."));
        }

        if (request.Station?.Slots?.Blocks is not { Count: > 0 })
        {
            return BadRequest(ValidationErrorDto.Of("The station has no time blocks to save as a layout."));
        }

        var layout = WeekLayouts.FromStation(request.Station, request.Name, request.KeepSources);
        var settings = _settings.Get();
        var list = Saved(settings);
        Upsert(list, layout);
        settings.WeekLayouts = list.OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase).ToList();
        _settings.Save(settings);
        return Ok(WeekLayouts.Merge(settings.WeekLayouts));
    }

    /// <summary>Deletes a saved layout. Built-in ones cannot be deleted; a saved one that shadowed a built-in gives it back.</summary>
    [HttpDelete("{id}")]
    public ActionResult<IEnumerable<WeekLayout>> Delete([FromRoute] Guid id)
    {
        if (WeekLayouts.BuiltIn.Any(b => b.Id == id))
        {
            return BadRequest(ValidationErrorDto.Of("Built-in layouts cannot be deleted. Save your own under the same name to replace it."));
        }

        var settings = _settings.Get();
        var list = Saved(settings);
        list.RemoveAll(l => l.Id == id);
        settings.WeekLayouts = list;
        _settings.Save(settings);
        return Ok(WeekLayouts.Merge(list));
    }

    /// <summary>Cleans a layout for storage and puts it in the list in place of one with the same name or id.</summary>
    internal static WeekLayout Upsert(List<WeekLayout> list, WeekLayout layout)
    {
        var clean = WeekLayouts.Clean(layout);
        list.RemoveAll(l => string.Equals(l.Name, clean.Name, StringComparison.OrdinalIgnoreCase) || l.Id == clean.Id);
        list.Add(clean);
        return clean;
    }

    private static List<WeekLayout> Saved(GlobalSettings settings) => settings.WeekLayouts ??= new List<WeekLayout>();
}
