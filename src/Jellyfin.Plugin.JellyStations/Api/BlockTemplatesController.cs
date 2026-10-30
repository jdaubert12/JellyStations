using System.Net.Mime;
using Jellyfin.Plugin.JellyStations.Api.Dto;
using Jellyfin.Plugin.JellyStations.Models;
using Jellyfin.Plugin.JellyStations.Storage;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyStations.Api;

/// <summary>Saved time blocks (days, time, sources, order) reusable across stations. Stored in the plugin settings.</summary>
[ApiController]
[Route("JellyStations/BlockTemplates")]
[Authorize(Policy = Policies.RequiresElevation)]
[Produces(MediaTypeNames.Application.Json)]
public sealed class BlockTemplatesController : ControllerBase
{
    private readonly SettingsRepository _settings;

    public BlockTemplatesController(SettingsRepository settings)
    {
        _settings = settings;
    }

    [HttpGet]
    public ActionResult<IEnumerable<SlotBlock>> List() => Ok(Templates(_settings.Get()));

    /// <summary>Adds a template, or replaces the one with the same name (case-insensitive). Returns the full list.</summary>
    [HttpPost]
    public ActionResult<IEnumerable<SlotBlock>> Save([FromBody] SlotBlock template)
    {
        if (string.IsNullOrWhiteSpace(template.Name))
        {
            return BadRequest(ValidationErrorDto.Of("Give the template a name."));
        }

        var settings = _settings.Get();
        var list = Templates(settings);
        Upsert(list, template);
        settings.BlockTemplates = list.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
        _settings.Save(settings);
        return Ok(settings.BlockTemplates);
    }

    [HttpDelete("{id}")]
    public ActionResult<IEnumerable<SlotBlock>> Delete([FromRoute] Guid id)
    {
        var settings = _settings.Get();
        var list = Templates(settings);
        list.RemoveAll(t => t.Id == id);
        settings.BlockTemplates = list;
        _settings.Save(settings);
        return Ok(list);
    }

    /// <summary>
    /// Cleans a named template (trimmed name, empty lists for missing ones, an id when it has none) and puts it in the
    /// list in place of one with the same name (case-insensitive) or id.
    /// </summary>
    internal static void Upsert(List<SlotBlock> list, SlotBlock template)
    {
        template.Name = template.Name.Trim();
        template.Days ??= new List<DayOfWeek>();
        template.Sources ??= new List<ContentSource>();
        if (template.Id == Guid.Empty)
        {
            template.Id = Guid.NewGuid();
        }

        list.RemoveAll(t => string.Equals(t.Name, template.Name, StringComparison.OrdinalIgnoreCase) || t.Id == template.Id);
        list.Add(template);
    }

    private static List<SlotBlock> Templates(GlobalSettings settings) => settings.BlockTemplates ??= new List<SlotBlock>();
}
