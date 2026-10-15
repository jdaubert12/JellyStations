namespace Jellyfin.Plugin.TvStation.Models;

/// <summary>
/// The expanded, frozen content of a station at a given revision. The schedule resolver reads only this.
/// </summary>
public sealed class CompiledStation
{
    public Guid StationId { get; set; }

    public int Revision { get; set; }

    public DateTime CompiledUtc { get; set; }

    public List<PlayableItem> Pool { get; set; } = new();

    public List<PlayableItem> Lineup { get; set; } = new();

    public List<PlayableItem> Filler { get; set; } = new();

    /// <summary>Items per slot block id.</summary>
    public Dictionary<Guid, List<PlayableItem>> Blocks { get; set; } = new();

    public List<ExcludedItem> Excluded { get; set; } = new();

    /// <summary>Snapshots of items the user excluded, kept so the content view can still show them.</summary>
    public List<PlayableItem> UserExcluded { get; set; } = new();

    /// <summary>Snapshots of items dropped by an excluded tag, kept so the content view can show them.</summary>
    public List<PlayableItem> TagExcluded { get; set; } = new();

    /// <summary>Clips available to fill gaps (empty when bumpers are off).</summary>
    public List<PlayableItem> Bumpers { get; set; } = new();

    public int TotalItemCount => Pool.Count + Lineup.Count + Filler.Count + Blocks.Values.Sum(b => b.Count);
}

public sealed class ExcludedItem
{
    public Guid ItemId { get; set; }

    public string Name { get; set; } = string.Empty;

    public ExclusionReason Reason { get; set; }
}
