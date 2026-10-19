using System.Collections.Concurrent;
using Jellyfin.Plugin.TvStation.Models;

namespace Jellyfin.Plugin.TvStation.Scheduling;

/// <summary>
/// Pool (without blocks) and Lineup modes: the item list repeats in cycles from the anchor. Every cycle has the same
/// duration, so the cycle containing an instant is a division and the item inside it a binary search.
/// </summary>
internal sealed class CyclicScheduler : IStationScheduler
{
    private const string Scope = "pool";

    private readonly ScheduleInput _input;
    private readonly IReadOnlyList<PlayableItem> _items;
    private readonly PlayOrder _order;
    private readonly bool _avoidRepeats;
    private readonly int _marathonMin;
    private readonly int _marathonMax;
    private readonly long[] _padded;
    private readonly BumperFill.Clips _clips;
    private readonly long _cycleTicks;
    private readonly TimeZoneInfo _tz;
    private readonly IReadOnlyList<DaypartRuleInput> _rules;
    private readonly ConcurrentDictionary<long, Cycle> _cycles = new();

    public CyclicScheduler(ScheduleInput input)
    {
        _input = input;
        if (input.Mode == ScheduleMode.Lineup)
        {
            _items = input.Lineup;
            _order = PlayOrder.Sequential;
            _avoidRepeats = false;
        }
        else
        {
            _items = input.Pool;
            _order = input.PoolOrder;
            _avoidRepeats = input.AvoidSameSeriesBackToBack;
            _marathonMin = input.MarathonMin;
            _marathonMax = input.MarathonMax;
        }

        _padded = _items.Select(i => Padding.Pad(i.Runtime + input.Break, input.PaddingGrid, input.PaddingByLength).Ticks).ToArray();
        _clips = BumperFill.Clips.From(input);
        _cycleTicks = _padded.Sum();
        _tz = WeeklySlotScheduler.ResolveTimeZone(input.TimeZoneId);
        _rules = input.DaypartRules;
    }

    public bool IsEmpty => _items.Count == 0 || _cycleTicks <= 0;

    public ResolvedProgram? Resolve(DateTime utc)
    {
        if (IsEmpty)
        {
            var slate = SlateHour(utc);
            return new ResolvedProgram(slate, utc - slate.StartUtc);
        }

        var elapsed = utc.Ticks - _input.AnchorUtc.Ticks;
        var k = Padding.FloorDiv(elapsed, _cycleTicks);
        var inCycle = elapsed - (k * _cycleTicks);
        var cycle = GetCycle(k);
        var i = cycle.IndexAt(inCycle);
        var entry = MakeEntry(cycle, k, i, inCycle);
        return new ResolvedProgram(entry, utc - entry.StartUtc);
    }

    public IEnumerable<ScheduleEntry> Enumerate(DateTime fromUtc, DateTime toUtc)
    {
        if (toUtc <= fromUtc)
        {
            yield break;
        }

        if (IsEmpty)
        {
            var cur = SlateHour(fromUtc);
            while (cur.StartUtc < toUtc)
            {
                yield return cur;
                cur = cur with { StartUtc = cur.EndUtc, EndUtc = cur.EndUtc.AddHours(1) };
            }

            yield break;
        }

        var first = Resolve(fromUtc)!.Entry;
        var current = first;
        while (current.StartUtc < toUtc)
        {
            yield return current;
            current = Next(current);
        }
    }

    private ScheduleEntry Next(ScheduleEntry entry)
    {
        // Resolve a tick after the end; cheap because cycles are cached.
        return Resolve(entry.EndUtc)!.Entry;
    }

    private ScheduleEntry MakeEntry(Cycle cycle, long k, int i, long inCycle)
    {
        var cycleStart = _input.AnchorUtc.Ticks + (k * _cycleTicks);
        var item = _items[cycle.Order[i]];
        var itemStart = cycleStart + cycle.Starts[i];
        var itemEnd = itemStart + item.RuntimeTicks;
        var slotEnd = cycleStart + cycle.Starts[i] + _padded[cycle.Order[i]];

        if (cycle.Gated is not null && cycle.Gated[i])
        {
            // The item is not allowed at this time of day and nothing else fit here: dead air for the whole slot.
            return new ScheduleEntry(_input.StationId, _input.Revision, EntryKind.Slate, null, Utc(itemStart), Utc(slotEnd), false, "Not allowed at this time");
        }

        if (inCycle < cycle.Starts[i] + item.RuntimeTicks)
        {
            return new ScheduleEntry(_input.StationId, _input.Revision, EntryKind.Program, item, Utc(itemStart), Utc(itemEnd), false, null);
        }

        if (_input.HasBumpers && slotEnd > itemEnd)
        {
            // The gap after the program: post-roll, fill clips, card for whatever is left, pre-roll for what follows.
            var offset = inCycle - (cycle.Starts[i] + item.RuntimeTicks);
            var gap = new ScheduleEntry(_input.StationId, _input.Revision, EntryKind.Slate, null, Utc(itemEnd), Utc(slotEnd), false, null);
            var pieces = BumperFill.Plan(slotEnd - itemEnd, _clips, StableHash.Seed(_input.StationId, _input.Revision, "bumper:" + i, k), programBefore: true, programAfter: !NextIsGated(cycle, k, i));
            foreach (var p in pieces)
            {
                if (offset >= p.StartOffset && offset < p.EndOffset)
                {
                    return BumperFill.ToEntry(gap, p);
                }
            }
        }

        return new ScheduleEntry(_input.StationId, _input.Revision, EntryKind.Slate, null, Utc(itemEnd), Utc(slotEnd), false, null);
    }

    /// <summary>True when the slot after position i (wrapping into the next cycle) is dead air, so no pre-roll should lead into it.</summary>
    private bool NextIsGated(Cycle cycle, long k, int i)
    {
        if (_rules.Count == 0)
        {
            return false;
        }

        var wrap = i + 1 >= cycle.Order.Length;
        var next = wrap ? GetCycle(k + 1) : cycle;
        var j = wrap ? 0 : i + 1;
        return next.Gated is not null && j < next.Gated.Length && next.Gated[j];
    }

    private ScheduleEntry SlateHour(DateTime utc)
    {
        var start = new DateTime(utc.Ticks / TimeSpan.TicksPerHour * TimeSpan.TicksPerHour, DateTimeKind.Utc);
        return new ScheduleEntry(_input.StationId, _input.Revision, EntryKind.Slate, null, start, start.AddHours(1), false, null);
    }

    private static DateTime Utc(long ticks) => new(ticks, DateTimeKind.Utc);

    private Cycle GetCycle(long k)
    {
        if (_cycles.TryGetValue(k, out var c))
        {
            return c;
        }

        if (_cycles.Count > 64)
        {
            _cycles.Clear();
        }

        c = BuildCycle(k);
        _cycles[k] = c;
        return c;
    }

    private Cycle BuildCycle(long k)
    {
        var cycleStart = _input.AnchorUtc.Ticks + (k * _cycleTicks);
        var runs = GatedRuns(k, cycleStart);

        if (_rules.Count > 0 && _input.Mode == ScheduleMode.Pool && _avoidRepeats && runs.Count > 2)
        {
            // The daypart gate can undo the seam check PoolOrdering made: the previous cycle may now end with the
            // same series or movie this one starts with, possibly with gated dead air in between. Re-check
            // against what the previous cycle actually airs last, as the gate left it.
            var prev = GatedRuns(k - 1, cycleStart - _cycleTicks);
            var prevLast = LastAired(prev, cycleStart - _cycleTicks);
            if (prevLast >= 0 && FixSeamAfterGate(runs, cycleStart, MarathonOrder.KeyOfRun(prev[prevLast], _items)))
            {
                runs = RestoreEpisodeOrder(runs);
            }
        }

        var order = runs.SelectMany(r => r).ToArray();
        var starts = new long[order.Length];
        long acc = 0;
        for (var i = 0; i < order.Length; i++)
        {
            starts[i] = acc;
            acc += _padded[order[i]];
        }

        var gated = _rules.Count > 0 ? DaypartGate.Flags(order, _items, cycleStart, _padded, _tz, _rules) : null;
        return new Cycle(order, starts, gated);
    }

    /// <summary>Runs for cycle k after the pool ordering and, when there are daypart rules, the gate.</summary>
    private List<int[]> GatedRuns(long k, long cycleStart)
    {
        var runs = PoolOrdering.BuildRuns(_items, _order, _avoidRepeats, _marathonMin, _marathonMax, Seed, k);
        if (_rules.Count > 0 && _input.Mode == ScheduleMode.Pool)
        {
            // Hard rule: move items that may not air at their slot's time of day. Soft rules (series spacing)
            // may be disturbed; episode order is restored afterwards.
            DaypartGate.Place(runs, _items, cycleStart, _padded, _tz, _rules);
            runs = RestoreEpisodeOrder(runs);
        }

        return runs;
    }

    private List<int[]> RestoreEpisodeOrder(List<int[]> runs)
    {
        if (_order != PlayOrder.ShuffleEpisodesInOrder)
        {
            return runs;
        }

        if (MarathonOrder.Enabled(_order, _marathonMin, _marathonMax))
        {
            MarathonOrder.KeepRunOrder(runs, _items);
            return runs;
        }

        var flat = runs.SelectMany(r => r).ToArray();
        EpisodeOrder.Apply(flat, _items);
        return flat.Select(i => new[] { i }).ToList();
    }

    /// <summary>
    /// Soft rule: when the first run this cycle airs is the same series or movie the previous cycle aired last,
    /// swap it with a later run (never the last aired one, so the next cycle's check stays valid) that keeps
    /// every neighbour distinct, airs the same last run, and leaves no more daypart violations than before.
    /// Gives up quietly otherwise.
    /// </summary>
    private bool FixSeamAfterGate(List<int[]> runs, long cycleStart, string prevKey)
    {
        var flags = RunFlags(runs, cycleStart);
        var first = Array.IndexOf(flags, false);
        var last = Array.LastIndexOf(flags, false);
        if (first < 0 || MarathonOrder.KeyOfRun(runs[first], _items) != prevKey)
        {
            return false;
        }

        var before = flags.Count(f => f);
        for (var j = first + 1; j < runs.Count; j++)
        {
            // Gated runs behind the last aired one are fair game (a film the gate parked there may fit the
            // first slot); the last aired run itself is not, since the next cycle checks against it.
            if (j == last || !MarathonOrder.TrySwap(runs, _items, first, j, prevKey))
            {
                continue;
            }

            // Accept only when the cycle still airs the same last run (the next cycle checks against it) and the
            // swap did not put anything new behind the gate.
            var after = RunFlags(runs, cycleStart);
            if (MarathonOrder.KeyOfRun(runs[first], _items) != prevKey && Array.LastIndexOf(after, false) == last && after.Count(f => f) <= before)
            {
                return true;
            }

            (runs[first], runs[j]) = (runs[j], runs[first]);
        }

        return false;
    }

    /// <summary>Index of the last run the cycle airs (not gated), or -1 when it airs nothing.</summary>
    private int LastAired(List<int[]> runs, long cycleStart) => Array.LastIndexOf(RunFlags(runs, cycleStart), false);

    /// <summary>Per run: true when the daypart gate keeps it off the air in its slot.</summary>
    private bool[] RunFlags(List<int[]> runs, long cycleStart)
    {
        var flat = DaypartGate.Flags(runs.SelectMany(r => r).ToArray(), _items, cycleStart, _padded, _tz, _rules);
        var result = new bool[runs.Count];
        var pos = 0;
        for (var r = 0; r < runs.Count; r++)
        {
            result[r] = flat[pos];
            pos += runs[r].Length;
        }

        return result;
    }

    private ulong Seed(long k) => StableHash.Seed(_input.StationId, _input.Revision, Scope, k);

    private sealed class Cycle
    {
        public Cycle(int[] order, long[] starts, bool[]? gated)
        {
            Order = order;
            Starts = starts;
            Gated = gated;
        }

        public int[] Order { get; }

        public long[] Starts { get; }

        /// <summary>Per position: true when the item may not air here (daypart rule) and the slot is dead air.</summary>
        public bool[]? Gated { get; }

        public int IndexAt(long inCycle)
        {
            var idx = Array.BinarySearch(Starts, inCycle);
            if (idx < 0)
            {
                idx = ~idx - 1;
            }

            return Math.Clamp(idx, 0, Starts.Length - 1);
        }
    }
}
