namespace Jellyfin.Plugin.TvStation.Models;

/// <summary>How a station's schedule is generated.</summary>
public enum ScheduleMode
{
    /// <summary>Continuous passes through a content pool, optionally interrupted by weekly time blocks.</summary>
    Pool,

    /// <summary>Weekly recurring time-slot blocks with a filler pool between them.</summary>
    Slots,

    /// <summary>A hand-ordered list that loops forever from the anchor time.</summary>
    Lineup
}

/// <summary>Order in which items of a pool or block are played.</summary>
public enum PlayOrder
{
    /// <summary>Seeded shuffle, re-rolled each cycle/occurrence.</summary>
    Shuffle,

    /// <summary>Compiled order (series by season/episode, movies by name).</summary>
    Sequential,

    /// <summary>Seeded shuffle of the slots, but each series fills its slots with episodes in season/episode order.</summary>
    ShuffleEpisodesInOrder
}

/// <summary>What kind of library object a content source references.</summary>
public enum SourceKind
{
    Movie,
    Series,
    Season,
    Episode,
    BoxSet,
    Playlist,
    Genre,
    Library,

    /// <summary>Every movie/episode carrying a library tag (series tags apply to their episodes).</summary>
    Tag
}

/// <summary>What to do when the next item does not fit in the remaining time of a slot block.</summary>
public enum SlotOverflow
{
    /// <summary>Hand the remainder to whatever plays between blocks (the filler pool, or the station's pool in Pool mode).</summary>
    FillerRemainder,

    /// <summary>Play the next item anyway and cut it off at the block end.</summary>
    TruncateLast
}

/// <summary>Kind of a schedule entry.</summary>
public enum EntryKind
{
    Program,
    Slate,

    /// <summary>A short clip (ident, trailer, bumper) filling a gap between programs.</summary>
    Bumper
}

/// <summary>Reason an item was excluded during compilation.</summary>
public enum ExclusionReason
{
    NoRuntime,
    NoPath,
    NotFound,
    NotVideo,

    /// <summary>Removed by the station's exclusion list.</summary>
    UserExcluded,

    /// <summary>Removed because it carries a tag on the station's or the global excluded-tags list.</summary>
    TagExcluded,

    /// <summary>Shorter than the station's minimum runtime.</summary>
    TooShort
}
