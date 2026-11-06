using Jellyfin.Plugin.JellyStations.Models;

namespace Jellyfin.Plugin.JellyStations.Library;

/// <summary>
/// The starter time blocks offered next to the saved block templates: the days, time, length and play order of a
/// familiar piece of a broadcast week, without sources (item ids do not travel between servers; the editor asks for
/// them when the block is added). Pure data; a saved template of the same name takes a starter's place.
/// </summary>
public static class BlockTemplates
{
    private static readonly DayOfWeek[] EveryDay =
    {
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday
    };

    /// <summary>The starter set. Ids are fixed so links and exports hold.</summary>
    public static IReadOnlyList<SlotBlock> BuiltIn { get; } = new List<SlotBlock>
    {
        Starter("7c1b3a20-0001-4b5e-9a6d-6e1f0c2d3e01", "Saturday morning cartoons", new[] { DayOfWeek.Saturday }, "07:00", 240, PlayOrder.ShuffleEpisodesInOrder),
        Starter("7c1b3a20-0002-4b5e-9a6d-6e1f0c2d3e02", "Prime-time movie", EveryDay, "20:00", 180, PlayOrder.Shuffle),
        Starter("7c1b3a20-0003-4b5e-9a6d-6e1f0c2d3e03", "Late night", EveryDay, "23:00", 180, PlayOrder.ShuffleEpisodesInOrder),
        WeatherOnTheHour()
    };

    /// <summary>The starters whose names no saved template has taken, in their own order.</summary>
    public static List<SlotBlock> Available(IEnumerable<SlotBlock>? saved)
    {
        var taken = new HashSet<string>((saved ?? Enumerable.Empty<SlotBlock>()).Select(t => (t.Name ?? string.Empty).Trim()), StringComparer.OrdinalIgnoreCase);
        return BuiltIn.Where(b => !taken.Contains(b.Name)).ToList();
    }

    private static SlotBlock Starter(string id, string name, DayOfWeek[] days, string start, int minutes, PlayOrder order) => new()
    {
        Id = Guid.Parse(id),
        Name = name,
        Days = days.ToList(),
        StartLocal = start,
        DurationMinutes = minutes,
        Order = order,
        Overflow = SlotOverflow.FillerRemainder,
        RemainderMayOverrun = true,
        MaxStartDelayMinutes = 30
    };

    /// <summary>Five minutes of the station's weather screens at the top of every hour from 06:00 to 23:00.</summary>
    private static SlotBlock WeatherOnTheHour()
    {
        var block = Starter("7c1b3a20-0004-4b5e-9a6d-6e1f0c2d3e04", "Weather on the hour", EveryDay, "06:00", 5, PlayOrder.Sequential);
        block.Kind = BlockKind.Weather;
        block.RepeatEveryMinutes = 60;
        block.RepeatUntilLocal = "23:00";
        return block;
    }
}
