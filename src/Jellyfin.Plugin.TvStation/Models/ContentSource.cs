namespace Jellyfin.Plugin.TvStation.Models;

/// <summary>A reference to library content that expands to one or more playable items.</summary>
public sealed class ContentSource
{
    public SourceKind Kind { get; set; }

    /// <summary>Jellyfin item id for Movie/Series/Season/Episode/BoxSet/Playlist/Library kinds.</summary>
    public Guid? ItemId { get; set; }

    /// <summary>Genre name for the Genre kind.</summary>
    public string? Genre { get; set; }

    /// <summary>Tag name for the Tag kind (case-insensitive).</summary>
    public string? Tag { get; set; }

    /// <summary>For Genre, Playlist, BoxSet and Library kinds: which item kinds to include (Movie, Episode). Empty = both.</summary>
    public List<SourceKind> ItemKinds { get; set; } = new();

    /// <summary>For Series kind: first season to include (null = from the start).</summary>
    public int? SeasonFrom { get; set; }

    /// <summary>For Series kind: last season to include (null = through the end).</summary>
    public int? SeasonTo { get; set; }

    /// <summary>For Series kind: drop season 0 (specials).</summary>
    public bool ExcludeSpecials { get; set; }

    /// <summary>How many times this source's items appear in each cycle or block (1-10). 2 = twice as often.</summary>
    public int Weight { get; set; } = 1;

    /// <summary>Highest allowed rating on the G..NC-17 / TV-Y..TV-MA ladder (see RatingLadder). Null = any.</summary>
    public string? MaxRating { get; set; }

    /// <summary>Explicit allow-list of ratings (case-insensitive); "NR" admits unrated items. Empty = not used.</summary>
    public List<string> Ratings { get; set; } = new();

    /// <summary>When MaxRating or Ratings is set, whether items without a rating still pass.</summary>
    public bool AllowUnrated { get; set; }

    /// <summary>Inclusive production year range. Items without a year are dropped when either bound is set.</summary>
    public int? YearFrom { get; set; }

    public int? YearTo { get; set; }

    /// <summary>For Genre and Tag kinds: optional library to restrict to.</summary>
    public Guid? LibraryId { get; set; }

    /// <summary>Display name captured at pick time (for UI only).</summary>
    public string? DisplayName { get; set; }
}
