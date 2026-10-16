namespace Jellyfin.Plugin.TvStation.Models;

public sealed class PoolConfig
{
    public List<ContentSource> Sources { get; set; } = new();

    public PlayOrder Order { get; set; } = PlayOrder.Shuffle;

    public bool AvoidSameSeriesBackToBack { get; set; } = true;

    /// <summary>When shuffling: fewest episodes of one show to play back to back before switching. 1 = no marathons.</summary>
    public int MarathonMin { get; set; } = 1;

    /// <summary>When shuffling: most episodes of one show to play back to back before switching. 1 = no marathons.</summary>
    public int MarathonMax { get; set; } = 1;

    /// <summary>Time-of-day rating limits (station time zone). Items that would air inside a rule's window must be at or below its rating.</summary>
    public List<DaypartRule> DaypartRules { get; set; } = new();
}

/// <summary>"Nothing above PG between 06:00 and 20:00". The window may wrap midnight; equal times mean all day.</summary>
public sealed class DaypartRule
{
    /// <summary>Local start time, "HH:mm".</summary>
    public string StartLocal { get; set; } = "06:00";

    /// <summary>Local end time, "HH:mm" (exclusive).</summary>
    public string EndLocal { get; set; } = "20:00";

    /// <summary>Highest allowed rating on the RatingLadder while the window is in effect.</summary>
    public string MaxRating { get; set; } = "PG";

    /// <summary>Whether items without a ladder rating may air inside the window.</summary>
    public bool AllowUnrated { get; set; }
}

public sealed class SlotsConfig
{
    public List<SlotBlock> Blocks { get; set; } = new();

    public FillerConfig Filler { get; set; } = new();
}

public sealed class SlotBlock
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    /// <summary>Days of week (in the station's time zone) the block occurs on.</summary>
    public List<DayOfWeek> Days { get; set; } = new();

    /// <summary>Local start time, "HH:mm".</summary>
    public string StartLocal { get; set; } = "20:00";

    public int DurationMinutes { get; set; } = 60;

    public List<ContentSource> Sources { get; set; } = new();

    public PlayOrder Order { get; set; } = PlayOrder.Sequential;

    public SlotOverflow Overflow { get; set; } = SlotOverflow.FillerRemainder;

    /// <summary>
    /// When the block's own content ends early and the pool / filler takes the remainder: let that stream's last item
    /// finish past the block end instead of cutting it, unless another block starts right then. Off keeps the
    /// pre-1.4 behaviour (cut at the block end); the editor turns it on for new blocks.
    /// </summary>
    public bool RemainderMayOverrun { get; set; }

    /// <summary>
    /// Let the program already playing finish before the block starts, delaying the block by up to this many
    /// minutes; the block then keeps its full duration (capped by the next block). 0 = start on time and cut the
    /// program. The editor sets 30 for new blocks.
    /// </summary>
    public int MaxStartDelayMinutes { get; set; }
}

public sealed class FillerConfig
{
    public List<ContentSource> Sources { get; set; } = new();

    public PlayOrder Order { get; set; } = PlayOrder.Shuffle;
}

/// <summary>A recurring yearly date range, inclusive on both ends, in the station's time zone.</summary>
public sealed class SeasonWindow
{
    public string? Label { get; set; }

    public int StartMonth { get; set; } = 12;

    public int StartDay { get; set; } = 1;

    public int EndMonth { get; set; } = 12;

    public int EndDay { get; set; } = 31;
}

/// <summary>Short clips that fill gaps between programs. When off, or when no clip fits a gap, the station card is shown.</summary>
public sealed class BumperConfig
{
    public bool Enabled { get; set; }

    public List<ContentSource> Sources { get; set; } = new();

    /// <summary>Minutes reserved after every program (before grid padding) so there is always room for a clip. 0 = only fill existing gaps.</summary>
    public int BreakMinutes { get; set; }

    /// <summary>Clips longer than this are ignored, so a feature film in the source list cannot become a bumper.</summary>
    public int MaxClipMinutes { get; set; } = 5;

    /// <summary>An ident played right before every program, at the tail of the break that precedes it.</summary>
    public BumperRollConfig PreRoll { get; set; } = new();

    /// <summary>A clip played right after every program, at the head of the break that follows it.</summary>
    public BumperRollConfig PostRoll { get; set; } = new();
}

/// <summary>
/// A fixed position in every break (pre-roll or post-roll). The clip comes from its own sources, or from the
/// main bumper sources when none are given. Its time comes out of the existing break, so the grid holds; when
/// no clip fits the break it is skipped quietly.
/// </summary>
public sealed class BumperRollConfig
{
    public bool Enabled { get; set; }

    /// <summary>Clips to pick from; empty = the main bumper sources.</summary>
    public List<ContentSource> Sources { get; set; } = new();
}

public sealed class LineupConfig
{
    public List<LineupItem> Items { get; set; } = new();
}

public sealed class LineupItem
{
    public Guid ItemId { get; set; }

    /// <summary>Display name captured at pick time (for UI only).</summary>
    public string? DisplayName { get; set; }
}
