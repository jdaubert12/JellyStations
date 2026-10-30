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

    /// <summary>A program airing <paramref name="item"/> on the input's station.</summary>
    public static ScheduleEntry Program(ScheduleInput input, PlayableItem item, DateTime startUtc, DateTime endUtc, bool truncated = false, string? label = null)
        => new(input.StationId, input.Revision, EntryKind.Program, item, startUtc, endUtc, truncated, label);

    /// <summary>Dead air with the station card.</summary>
    public static ScheduleEntry Slate(ScheduleInput input, DateTime startUtc, DateTime endUtc, string? label = null)
        => new(input.StationId, input.Revision, EntryKind.Slate, null, startUtc, endUtc, false, label);

    /// <summary>A run of weather screens, titled <paramref name="label"/>.</summary>
    public static ScheduleEntry Weather(ScheduleInput input, DateTime startUtc, DateTime endUtc, string? label)
        => new(input.StationId, input.Revision, EntryKind.Weather, null, startUtc, endUtc, false, label);
}

/// <summary>The entry airing at a given instant and how far into it we are.</summary>
public sealed record ResolvedProgram(ScheduleEntry Entry, TimeSpan Offset)
{
    public TimeSpan Remaining => Entry.EndUtc - (Entry.StartUtc + Offset);
}
