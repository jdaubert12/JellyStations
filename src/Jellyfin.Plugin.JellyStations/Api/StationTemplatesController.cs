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
/// Station templates: the built-in presets plus the ones saved from a station editor (stored in the plugin settings).
/// A template is a station without its content; the New station page starts a draft from one.
/// </summary>
[ApiController]
[Route("JellyStations/StationTemplates")]
[Authorize(Policy = Policies.RequiresElevation)]
[Produces(MediaTypeNames.Application.Json)]
public sealed class StationTemplatesController : ControllerBase
{
    private readonly SettingsRepository _settings;

    public StationTemplatesController(SettingsRepository settings)
    {
        _settings = settings;
    }

    /// <summary>Saved templates first, then the built-in ones not shadowed by a saved one of the same name.</summary>
    [HttpGet]
    public ActionResult<IEnumerable<StationTemplate>> List() => Ok(StationTemplates.Merge(Saved(_settings.Get())));

    [HttpGet("{id}")]
    public ActionResult<StationTemplate> Get([FromRoute] Guid id)
    {
        var template = StationTemplates.Find(Saved(_settings.Get()), id);
        return template is null ? NotFound() : Ok(template);
    }

    /// <summary>
    /// A fresh station draft from the template: its settings under a blank name, no number, the given time zone.
    /// The page fills in the number and opens the editor with it.
    /// </summary>
    [HttpGet("{id}/Draft")]
    public ActionResult<Station> Draft([FromRoute] Guid id, [FromQuery] string? timeZoneId)
    {
        var template = StationTemplates.Find(Saved(_settings.Get()), id);
        if (template is null)
        {
            return NotFound();
        }

        return Ok(StationTemplates.ApplyTo(template, new Station { TimeZoneId = timeZoneId ?? string.Empty }));
    }

    /// <summary>
    /// Saves a template from a station (the body's Settings, content stripped here), or replaces the saved one with
    /// the same name or id. Saving under a built-in name takes that preset's place on the New station page. Returns
    /// the full list.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ValidationErrorDto))]
    public ActionResult<IEnumerable<StationTemplate>> Save([FromBody] StationTemplate template)
    {
        if (string.IsNullOrWhiteSpace(template.Name))
        {
            return BadRequest(ValidationErrorDto.Of("Give the template a name."));
        }

        var settings = _settings.Get();
        var list = Saved(settings);
        Upsert(list, template);
        settings.StationTemplates = list.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
        _settings.Save(settings);
        return Ok(StationTemplates.Merge(settings.StationTemplates));
    }

    /// <summary>Deletes a saved template. Built-in ones cannot be deleted; a saved one that shadowed a built-in gives it back.</summary>
    [HttpDelete("{id}")]
    public ActionResult<IEnumerable<StationTemplate>> Delete([FromRoute] Guid id)
    {
        if (StationTemplates.BuiltIn.Any(b => b.Id == id))
        {
            return BadRequest(ValidationErrorDto.Of("Built-in templates cannot be deleted. Save your own under the same name to replace it."));
        }

        var settings = _settings.Get();
        var list = Saved(settings);
        list.RemoveAll(t => t.Id == id);
        settings.StationTemplates = list;
        _settings.Save(settings);
        return Ok(StationTemplates.Merge(list));
    }

    /// <summary>Cleans a template for storage and puts it in the list in place of one with the same name or id.</summary>
    internal static StationTemplate Upsert(List<StationTemplate> list, StationTemplate template)
    {
        var clean = StationTemplates.FromStation(template.Settings ?? new Station(), template.Name);
        clean.Id = template.Id == Guid.Empty ? Guid.NewGuid() : template.Id;
        clean.Description = Text.TrimOrNull(template.Description);
        clean.Icon = Text.TrimOrNull(template.Icon);
        clean.NextStep = Text.TrimOrNull(template.NextStep);
        clean.DefaultName = Text.TrimOrNull(template.DefaultName);
        clean.LogoColor = template.LogoColor is { Length: 7 } color && color[0] == '#' ? color.ToLowerInvariant() : null;
        clean.SourceDefaults = template.SourceDefaults;
        clean.CreatedUtc = template.CreatedUtc == default ? DateTime.UtcNow : template.CreatedUtc;
        list.RemoveAll(t => string.Equals(t.Name, clean.Name, StringComparison.OrdinalIgnoreCase) || t.Id == clean.Id);
        list.Add(clean);
        return clean;
    }

    private static List<StationTemplate> Saved(GlobalSettings settings) => settings.StationTemplates ??= new List<StationTemplate>();
}
