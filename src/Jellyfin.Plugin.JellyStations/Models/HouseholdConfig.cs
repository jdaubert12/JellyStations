namespace Jellyfin.Plugin.JellyStations.Models;

/// <summary>
/// How a station uses the household's Jellyfin play history (the users ticked under Settings &gt; Our household).
/// Everything is off by default; a station with nothing on schedules exactly as before.
/// </summary>
public sealed class HouseholdConfig
{
    public const int MaxSkipDays = 365;

    /// <summary>
    /// Leave out anything everyone in the household has finished within this many days (by Jellyfin's played flag
    /// and last-played date). 0 = off. The station is recompiled when the household's history changes, so a film
    /// comes back on its own once the days have passed.
    /// </summary>
    public int SkipFinishedDays { get; set; }

    /// <summary>
    /// A series plays from the first episode nobody in the household has watched yet and skips ahead as they
    /// watch; the station never goes back behind where it already is. Applies to the "episodes in order" play
    /// orders. Sampled at compile time, so the channel moves on the next refresh.
    /// </summary>
    public bool FollowProgress { get; set; }

    public bool Any => SkipFinishedDays > 0 || FollowProgress;

    public void Normalize() => SkipFinishedDays = Math.Clamp(SkipFinishedDays, 0, MaxSkipDays);
}

/// <summary>What the compiler read from the household's history: frozen into the compiled station like the air history.</summary>
public sealed class HouseholdSnapshot
{
    public DateTime TakenUtc { get; set; }

    /// <summary>The users whose history was read.</summary>
    public List<Guid> UserIds { get; set; } = new();

    /// <summary>
    /// Per series key (<c>PoolOrdering.KeyOf</c>): the last episode the whole household has watched before the first
    /// one somebody has not. Series with nothing watched, or everything watched, have no entry.
    /// </summary>
    public Dictionary<string, HouseholdPosition> Progress { get; set; } = new(StringComparer.Ordinal);

    /// <summary>How many items were left out as recently finished.</summary>
    public int Skipped { get; set; }
}

/// <summary>An episode by id and by number (the number places it when the file is gone).</summary>
public sealed class HouseholdPosition
{
    public Guid ItemId { get; set; }

    public int? Season { get; set; }

    public int? Episode { get; set; }
}
