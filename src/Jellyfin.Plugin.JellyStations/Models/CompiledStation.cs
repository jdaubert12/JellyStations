namespace Jellyfin.Plugin.JellyStations.Models;

/// <summary>
/// The expanded, frozen content of a station at a given revision. The schedule resolver reads only this.
/// </summary>
public sealed class CompiledStation
{
    /// <summary>
    /// Bump when a compile starts recording something the restreamer depends on (snapshots saved by an older
    /// plugin are recompiled at startup). 1: items carry <see cref="PlayableItem.IsHdr"/>.
    /// </summary>
    public const int CurrentFormat = 2;

    public Guid StationId { get; set; }

    /// <summary>The <see cref="CurrentFormat"/> this snapshot was compiled with; 0 for snapshots older than the field.</summary>
    public int Format { get; set; }

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

    /// <summary>Idents for the pre-roll position (empty when off; a copy of <see cref="Bumpers"/> when it has no sources of its own).</summary>
    public List<PlayableItem> PreRoll { get; set; } = new();

    /// <summary>Clips for the post-roll position (empty when off; a copy of <see cref="Bumpers"/> when it has no sources of its own).</summary>
    public List<PlayableItem> PostRoll { get; set; } = new();

    /// <summary>How each content source expanded at compile time (for the health report).</summary>
    public List<SourceExpansion> Sources { get; set; } = new();

    /// <summary>Music tracks of a weather channel, from its music sources and folder, in compile order.</summary>
    public List<MusicTrack> Music { get; set; } = new();

    public int TotalItemCount => Pool.Count + Lineup.Count + Filler.Count + Blocks.Values.Sum(b => b.Count);

    /// <summary>Every program item, in the order pool, lineup, blocks, filler (bumper clips not included; repeats kept).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IEnumerable<PlayableItem> ProgramItems => Pool.Concat(Lineup).Concat(Blocks.Values.SelectMany(b => b)).Concat(Filler);

    /// <summary>Everything the station plays: <see cref="ProgramItems"/> plus its bumper, pre-roll and post-roll clips.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IEnumerable<PlayableItem> PlayedItems => ProgramItems.Concat(Bumpers).Concat(PreRoll).Concat(PostRoll);
}

/// <summary>What one content source contributed when the station was compiled.</summary>
public sealed class SourceExpansion
{
    /// <summary>Where the source sits: Pool, Filler, Bumpers, Pre-roll, Post-roll or a block name.</summary>
    public string Section { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public SourceKind Kind { get; set; }

    /// <summary>Library items the source matched before any filtering.</summary>
    public int Found { get; set; }

    /// <summary>Items it actually contributed (after tag exclusions, unusable items and de-duplication against earlier sources).</summary>
    public int Kept { get; set; }

    /// <summary>Items an earlier source in the same section had already contributed, so this one skipped them.</summary>
    public int AlreadyListed { get; set; }

    /// <summary>
    /// True when the source adds nothing to the station: it matched nothing, or everything it matched was unusable.
    /// A source whose items are all already on the station through an earlier source is not empty; its content plays.
    /// </summary>
    public bool IsEmpty => Kept == 0 && (Found == 0 || AlreadyListed < Found);

    /// <summary>The expansion record for a library source, labelled with <see cref="Describe"/>.</summary>
    public static SourceExpansion For(string section, ContentSource source, int found, int kept, int alreadyListed) => new()
    {
        Section = section,
        Label = Describe(source),
        Kind = source.Kind,
        Found = found,
        Kept = kept,
        AlreadyListed = alreadyListed
    };

    /// <summary>'Series "Bluey"', plus the rating or year filter in brackets when the source has one, so an empty result explains itself.</summary>
    public static string Describe(ContentSource source)
    {
        var name = !string.IsNullOrWhiteSpace(source.DisplayName) ? source.DisplayName
            : source.Genre ?? source.Tag ?? source.ItemId?.ToString("N") ?? "?";
        var filters = new List<string>();
        if (!string.IsNullOrWhiteSpace(source.MaxRating))
        {
            filters.Add(source.MaxRating + " or below" + (source.AllowUnrated ? string.Empty : ", unrated left out"));
        }
        else if (source.Ratings.Count > 0)
        {
            filters.Add("rated " + string.Join("/", source.Ratings));
        }

        if (source.YearFrom.HasValue || source.YearTo.HasValue)
        {
            filters.Add((source.YearFrom?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "...") + " to " + (source.YearTo?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "..."));
        }

        return source.Kind + " \"" + name + "\"" + (filters.Count > 0 ? " (" + string.Join(", ", filters) + ")" : string.Empty);
    }
}

public sealed class ExcludedItem
{
    public Guid ItemId { get; set; }

    public string Name { get; set; } = string.Empty;

    public ExclusionReason Reason { get; set; }
}
