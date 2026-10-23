using Jellyfin.Plugin.TvStation.Models;

namespace Jellyfin.Plugin.TvStation.Scheduling;

/// <summary>One occurrence on a station's timeline: a program or a slate (dead air with a card).</summary>
public sealed record ScheduleEntry(
    Guid StationId,
    int Revision,
    EntryKind Kind,
    PlayableItem? Item,
    DateTime StartUtc,
    DateTime EndUtc,
    bool IsTruncated,
    string? SourceLabel)
{
    public TimeSpan Duration => EndUtc - StartUtc;

    /// <summary>Stable id derived from station and start time; identical across guide refreshes.</summary>
    public string ProgramId => StationId.ToString("N") + "_" + new DateTimeOffset(StartUtc, TimeSpan.Zero).ToUnixTimeSeconds();

    public bool Contains(DateTime utc) => utc >= StartUtc && utc < EndUtc;

    public string Title => Kind switch
    {
        EntryKind.Slate => "Station Break",
        EntryKind.Weather => SourceLabel ?? "Local weather",
        _ => Item?.Title ?? string.Empty
    };
}

/// <summary>The entry airing at a given instant and how far into it we are.</summary>
public sealed record ResolvedProgram(ScheduleEntry Entry, TimeSpan Offset)
{
    public TimeSpan Remaining => Entry.EndUtc - (Entry.StartUtc + Offset);
}
