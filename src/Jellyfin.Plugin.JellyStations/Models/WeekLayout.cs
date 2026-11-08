namespace Jellyfin.Plugin.JellyStations.Models;

/// <summary>
/// A whole broadcast week in one piece: several time blocks (morning cartoons, daytime reruns, a prime-time
/// movie, late night) with their days, times and play order, and for each what kind of content belongs in it,
/// so the suggester can fill the blocks from the library and the editor can add them all to a station at once.
/// The built-in layouts ship with the plugin; saved ones come from a station's blocks and live in the settings.
/// </summary>
public sealed class WeekLayout
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>One or two sentences on what the week looks like.</summary>
    public string? Description { get; set; }

    /// <summary>Ships with the plugin; not stored in settings and cannot be deleted.</summary>
    public bool BuiltIn { get; set; }

    /// <summary>Name the suggested station gets. Null = the layout's name.</summary>
    public string? StationName { get; set; }

    public List<LayoutBlock> Blocks { get; set; } = new();

    /// <summary>What plays between the blocks when the suggester builds the station.</summary>
    public LayoutFill Filler { get; set; } = LayoutFill.Everything;

    /// <summary>Family hours the suggested station gets (the pool's daypart rules); empty = none.</summary>
    public List<DaypartRule> DaypartRules { get; set; } = new();

    public DateTime CreatedUtc { get; set; }
}

/// <summary>One block of a week layout: the block itself and the kind of content the suggester fills it with.</summary>
public sealed class LayoutBlock
{
    public SlotBlock Block { get; set; } = new();

    /// <summary>
    /// What the suggester puts in the block. <see cref="LayoutFill.None"/> keeps whatever sources the block
    /// carries (a layout saved with its sources) and drops the block when it has none.
    /// </summary>
    public LayoutFill Fill { get; set; }
}

/// <summary>The kinds of content a layout block or its filler can be filled with from the library.</summary>
public enum LayoutFill
{
    /// <summary>The block's own sources, if any.</summary>
    None,

    /// <summary>Every movie and show, no rating cap.</summary>
    Everything,

    /// <summary>Everything rated G, TV-G or below.</summary>
    Kids,

    /// <summary>Everything rated PG, TV-PG or below.</summary>
    Family,

    /// <summary>Movies rated PG or below.</summary>
    FamilyMovies,

    /// <summary>Movies, any rating.</summary>
    Movies,

    /// <summary>Half-hour comedies when there are enough of them, otherwise every show.</summary>
    Reruns,

    /// <summary>Everything rated PG-13, TV-14 or above.</summary>
    Grownup
}
