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
        var marathon = MarathonOrder.Enabled(_order, _marathonMin, _marathonMax);
        var runs = PoolOrdering.BuildRuns(_items, _order, _avoidRepeats, _marathonMin, _marathonMax, Seed, k);
        var cycleStart = _input.AnchorUtc.Ticks + (k * _cycleTicks);

        if (_rules.Count > 0 && _input.Mode == ScheduleMode.Pool)
        {
            // Hard rule: move items that may not air at their slot's time of day. Soft rules (series spacing)
            // may be disturbed; episode order is restored afterwards.
            DaypartGate.Place(runs, _items, cycleStart, _padded, _tz, _rules);
            if (_order == PlayOrder.ShuffleEpisodesInOrder)
            {
                if (marathon)
                {
                    MarathonOrder.KeepRunOrder(runs, _items);
                }
                else
                {
                    var flat = runs.SelectMany(r => r).ToArray();
                    EpisodeOrder.Apply(flat, _items);
                    runs = flat.Select(i => new[] { i }).ToList();
                }
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
