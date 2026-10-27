using Jellyfin.Plugin.JellyStations.Models;

namespace Jellyfin.Plugin.JellyStations.Scheduling;

/// <summary>One occurrence on a station's timeline: a program, a slate (dead air with a card), a bumper clip or a run of weather screens.</summary>
/// <remarks><paramref name="InBreak"/> marks weather placed in a break between programs: it plays like a bumper and, like one, is not a guide entry.</remarks>
public sealed record ScheduleEntry(
    Guid StationId,
    int Revision,
    EntryKind Kind,
    PlayableItem? Item,
    DateTime StartUtc,
    DateTime EndUtc,
    bool IsTruncated,
    string? SourceLabel,
    bool InBreak = false)
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
