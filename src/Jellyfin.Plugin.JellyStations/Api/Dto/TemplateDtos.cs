using Jellyfin.Plugin.JellyStations.Models;

namespace Jellyfin.Plugin.JellyStations.Api.Dto;

/// <summary>
/// A block template made ready for a station: its block with a fresh id, item references whose ids are unknown on
/// this server matched by the name stored beside them, and what could not be matched dropped.
/// </summary>
public sealed class BlockApplyDto
{
    public SlotBlock Block { get; set; } = new();

    /// <summary>References whose id was unknown here and that were matched by name.</summary>
    public List<string> Resolved { get; set; } = new();

    /// <summary>References that could not be matched and were dropped.</summary>
    public List<string> Unmatched { get; set; } = new();
}

/// <summary>A week layout's blocks made ready for a station, matched and trimmed the same way.</summary>
public sealed class WeekLayoutApplyDto
{
    public List<SlotBlock> Blocks { get; set; } = new();

    public List<string> Resolved { get; set; } = new();

    public List<string> Unmatched { get; set; } = new();
}

/// <summary>What the editor sends to save a station's blocks as a week layout.</summary>
public sealed class SaveWeekLayoutDto
{
    public string Name { get; set; } = string.Empty;

    /// <summary>The station whose blocks (and, in Shuffle mode, family hours) become the layout.</summary>
    public Station Station { get; set; } = new();

    /// <summary>Keep each block's sources (their ids are matched by name on another server) or save the shape alone.</summary>
    public bool KeepSources { get; set; } = true;
}
