using System.Globalization;
using Jellyfin.Plugin.JellyStations.Library;
using Jellyfin.Plugin.JellyStations.Models;

namespace Jellyfin.Plugin.JellyStations.Scheduling;

/// <summary>
/// Why an entry is on the timeline, in a line for the dashboard: where it came from (block, filler or pool and
/// the play order), the source that brought it in, its place in a marathon run, the family-hours rule in force,
/// and whether the household's progress placed it. The air log's "last aired" is shown beside it by the caller.
/// </summary>
public static class ScheduleTrace
{
    /// <summary>The reasons for every entry of a listing, with marathon runs counted across the list.</summary>
    public static List<string?> Explain(IReadOnlyList<ScheduleEntry> entries, Station station, HouseholdSnapshot? household)
    {
        var result = new List<string?>(entries.Count);
        var runs = Runs(entries, station);
        for (var i = 0; i < entries.Count; i++)
        {
            result.Add(Why(entries[i], station, household, runs[i].Index, runs[i].Length));
        }

        return result;
    }

    /// <summary>The reasons for one entry seen on its own (no marathon position).</summary>
    public static string? Why(ScheduleEntry entry, Station station, HouseholdSnapshot? household) => Why(entry, station, household, 0, 1);

    public static string? Why(ScheduleEntry entry, Station station, HouseholdSnapshot? household, int runIndex, int runLength)
    {
        switch (entry.Kind)
        {
            case EntryKind.Slate:
                return entry.SourceLabel;
            case EntryKind.Weather:
                return entry.InBreak ? "Weather in the break" : "Weather block";
            case EntryKind.Bumper:
                return null;
        }

        var item = entry.Item;
        if (item is null)
        {
            return null;
        }

        var parts = new List<string>();
        var block = BlockFor(entry, station);
        var inPool = block is null && (entry.SourceLabel is null || entry.SourceLabel == "Pool");
        if (block is not null)
        {
            parts.Add("Block “" + (string.IsNullOrWhiteSpace(block.Name) ? "Block" : block.Name) + "”, " + OrderText(block.Order));
        }
        else if (station.Mode == ScheduleMode.Lineup)
        {
            parts.Add("Fixed list");
        }
        else if (entry.SourceLabel == "Filler")
        {
            parts.Add("Between the blocks, " + OrderText(station.Slots.Filler.Order));
        }
        else
        {
            parts.Add((station.Mode == ScheduleMode.Pool && station.Slots.Blocks.Count > 0 ? "Between the blocks, " : string.Empty) + OrderText(station.Pool.Order));
        }

        if (!string.IsNullOrWhiteSpace(item.Source))
        {
            parts.Add("from " + item.Source);
        }

        if (runLength > 1)
        {
            parts.Add("marathon " + (runIndex + 1).ToString(CultureInfo.InvariantCulture) + " of " + runLength.ToString(CultureInfo.InvariantCulture));
        }

        if (inPool && station.Mode == ScheduleMode.Pool && FamilyHours(entry, item, station) is { } hours)
        {
            parts.Add(hours);
        }

        if (station.Household?.FollowProgress == true && item.SeriesId.HasValue && household is not null && household.Progress.ContainsKey(PoolOrdering.KeyOf(item)))
        {
            parts.Add("following the household's progress");
        }

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    /// <summary>The family-hours rule the entry runs under, or that it is outside of when its rating would not pass one.</summary>
    private static string? FamilyHours(ScheduleEntry entry, PlayableItem item, Station station)
    {
        var rules = station.Pool.DaypartRules ?? new List<DaypartRule>();
        if (rules.Count == 0)
        {
            return null;
        }

        var tz = TimeZones.Resolve(station.TimeZoneId);
        var start = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(entry.StartUtc, DateTimeKind.Utc), tz);
        var end = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(entry.EndUtc, DateTimeKind.Utc), tz);
        foreach (var rule in rules)
        {
            var input = new DaypartRuleInput { Start = ScheduleInput.ParseTime(rule.StartLocal), End = ScheduleInput.ParseTime(rule.EndLocal), MaxRank = RatingLadder.Rank(rule.MaxRating) ?? int.MaxValue, AllowUnrated = rule.AllowUnrated };
            if (DaypartGate.Overlaps(input, start, end))
            {
                return "family hours: up to " + rule.MaxRating + " until " + rule.EndLocal;
            }
        }

        var strictest = rules.Select(r => RatingLadder.Rank(r.MaxRating)).Where(r => r.HasValue).DefaultIfEmpty(null).Min();
        var rank = RatingLadder.Rank(item.OfficialRating);
        if (rank is null && rules.Any(r => !r.AllowUnrated))
        {
            return "unrated: outside family hours";
        }

        return rank is not null && strictest is not null && rank > strictest ? item.OfficialRating + ": outside family hours" : null;
    }

    private static SlotBlock? BlockFor(ScheduleEntry entry, Station station)
    {
        if (entry.SourceLabel is null || station.Mode == ScheduleMode.Lineup)
        {
            return null;
        }

        return station.Slots.Blocks.FirstOrDefault(b => b.Kind != BlockKind.Weather && (string.IsNullOrWhiteSpace(b.Name) ? "Block" : b.Name) == entry.SourceLabel);
    }

    public static string OrderText(PlayOrder order) => order switch
    {
        PlayOrder.Sequential => "in order",
        PlayOrder.ShuffleEpisodesInOrder => "shuffled, episodes in order",
        _ => "shuffled"
    };

    /// <summary>
    /// Each entry's place in a marathon run: consecutive pool programs of one series or film (bumpers, cards and
    /// weather between them do not break the run). Only a station that plays marathons counts them, and only runs no
    /// longer than its marathons; everything else is 1 of 1.
    /// </summary>
    private static (int Index, int Length)[] Runs(IReadOnlyList<ScheduleEntry> entries, Station station)
    {
        var result = new (int Index, int Length)[entries.Count];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = (0, 1);
        }

        if (station.Mode != ScheduleMode.Pool || station.Pool.MarathonMax <= 1)
        {
            return result;
        }

        var run = new List<int>();
        string? runKey = null;
        void Close()
        {
            // A run longer than the station's marathons was not one the scheduler made: the family hours kept
            // everything else off the air, which the entry's own line says. Only a real marathon gets the count.
            if (run.Count <= station.Pool.MarathonMax)
            {
                for (var j = 0; j < run.Count; j++)
                {
                    result[run[j]] = (j, run.Count);
                }
            }

            run.Clear();
            runKey = null;
        }

        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            // Bumpers, cards and weather sit on top of the pool's order (a weather block repeating every ten
            // minutes cuts a run into pieces on air; it is still one run of the pool), so none of them close a run.
            if (e.Kind == EntryKind.Bumper || e.Kind == EntryKind.Slate || e.Kind == EntryKind.Weather)
            {
                continue;
            }

            if (e.Kind != EntryKind.Program || e.Item is null || (e.SourceLabel is not null && e.SourceLabel != "Pool"))
            {
                Close();
                continue;
            }

            var key = PoolOrdering.KeyOf(e.Item);
            if (runKey is not null && key != runKey)
            {
                Close();
            }

            runKey = key;
            run.Add(i);
        }

        Close();
        return result;
    }
}
