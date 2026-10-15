using Jellyfin.Plugin.TvStation.Library;
using Jellyfin.Plugin.TvStation.Models;

namespace Jellyfin.Plugin.TvStation.Scheduling;

/// <summary>
/// Time-of-day rating gate. An item is allowed in a slot when, for every rule whose window overlaps any part of
/// the item's air time (in the station's time zone), the item's rating is at or below the rule's limit.
/// <see cref="Place"/> reorders a cycle so disallowed items move to later slots where they fit; whatever still
/// cannot be placed is flagged by <see cref="Flags"/> and the scheduler airs a slate in that slot instead.
/// </summary>
internal static class DaypartGate
{
    private static readonly TimeSpan Day = TimeSpan.FromDays(1);

    /// <summary>True when the item may air from <paramref name="startUtc"/> for its whole runtime.</summary>
    public static bool IsAllowed(PlayableItem item, DateTime startUtc, TimeZoneInfo tz, IReadOnlyList<DaypartRuleInput> rules)
    {
        if (rules.Count == 0)
        {
            return true;
        }

        var rank = RatingLadder.Rank(item.OfficialRating);
        var localStart = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(startUtc, DateTimeKind.Utc), tz);
        var localEnd = localStart + item.Runtime;
        foreach (var rule in rules)
        {
            var passes = rank.HasValue ? rank.Value <= rule.MaxRank : rule.AllowUnrated;
            if (passes)
            {
                continue;
            }

            if (Overlaps(rule, localStart, localEnd))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether the rule's window (on any day the interval touches) intersects [localStart, localEnd).</summary>
    public static bool Overlaps(DaypartRuleInput rule, DateTime localStart, DateTime localEnd)
    {
        var windowLength = rule.End > rule.Start ? rule.End - rule.Start : Day - rule.Start + rule.End;
        if (windowLength <= TimeSpan.Zero)
        {
            windowLength = Day; // equal times = all day
        }

        for (var d = localStart.Date.AddDays(-1); d <= localEnd.Date; d = d.AddDays(1))
        {
            var ws = d + rule.Start;
            var we = ws + windowLength;
            if (ws < localEnd && localStart < we)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Greedy forward pass over runs: a run that is not allowed at its slot is swapped with the first later run
    /// that is allowed there. Positions already passed keep their times, so one pass is enough; runs that cannot
    /// be placed stay put and are reported by <see cref="Flags"/>.
    /// </summary>
    public static void Place(List<int[]> runs, IReadOnlyList<PlayableItem> items, long cycleStartTicks, long[] padded, TimeZoneInfo tz, IReadOnlyList<DaypartRuleInput> rules)
    {
        if (rules.Count == 0)
        {
            return;
        }

        long t = cycleStartTicks;
        for (var i = 0; i < runs.Count; i++)
        {
            if (!RunAllowed(runs[i], items, t, padded, tz, rules))
            {
                for (var j = i + 1; j < runs.Count; j++)
                {
                    if (RunAllowed(runs[j], items, t, padded, tz, rules))
                    {
                        (runs[i], runs[j]) = (runs[j], runs[i]);
                        break;
                    }
                }
            }

            t += runs[i].Sum(idx => padded[idx]);
        }
    }

    /// <summary>Per-position "not allowed here" flags for a laid-out order.</summary>
    public static bool[] Flags(int[] order, IReadOnlyList<PlayableItem> items, long cycleStartTicks, long[] padded, TimeZoneInfo tz, IReadOnlyList<DaypartRuleInput> rules)
    {
        var flags = new bool[order.Length];
        long t = cycleStartTicks;
        for (var i = 0; i < order.Length; i++)
        {
            flags[i] = !IsAllowed(items[order[i]], new DateTime(t, DateTimeKind.Utc), tz, rules);
            t += padded[order[i]];
        }

        return flags;
    }

    private static bool RunAllowed(int[] run, IReadOnlyList<PlayableItem> items, long startTicks, long[] padded, TimeZoneInfo tz, IReadOnlyList<DaypartRuleInput> rules)
    {
        var t = startTicks;
        foreach (var idx in run)
        {
            if (!IsAllowed(items[idx], new DateTime(t, DateTimeKind.Utc), tz, rules))
            {
                return false;
            }

            t += padded[idx];
        }

        return true;
    }
}
