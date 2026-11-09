using System.Collections.Concurrent;
using Jellyfin.Plugin.JellyStations.Models;

namespace Jellyfin.Plugin.JellyStations.Scheduling;

/// <summary>
/// Pool (without blocks) and Lineup modes: the item list repeats in cycles from the origin (a pool's handover from
/// the previous schedule, else the anchor). Every cycle has the same duration, so the cycle containing an instant
/// is a division and the item inside it a binary search.
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
    private readonly long _cooldownTicks;
    private readonly AirHistorySnapshot? _snapshot;
    private readonly long _takenTicks;
    private readonly long _chainStart;
    private readonly long _origin;
    private readonly bool _chains;
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
            _cooldownTicks = TimeSpan.FromHours(Math.Max(0, input.CooldownHours)).Ticks;
            _snapshot = input.History;
        }

        _padded = _items.Select(i => Padding.Pad(i.Runtime + input.Break, input.PaddingGrid, input.PaddingByLength).Ticks).ToArray();
        _clips = BumperFill.Clips.From(input);
        _cycleTicks = _padded.Sum();
        _tz = TimeZones.Resolve(input.TimeZoneId);
        _rules = input.DaypartRules;

        // A pool that took over from the previous schedule starts its first cycle there; a lineup and a pool that
        // never had one count from the anchor.
        _origin = (input.Mode == ScheduleMode.Pool && input.StartUtc is { } start ? start : input.AnchorUtc).Ticks;

        // With a snapshot the cycle containing the moment it was taken is the first that airs under this revision:
        // it continues from the snapshot, and the cycles after it chain from it when they carry anything forward
        // (a cooldown rests what aired; episodes in order carry each series' position). Cycles before it never air
        // under this revision and are built standalone, exactly as without a snapshot. A snapshot taken before the
        // origin (the handover is planned at the same instant) reads as taken at the origin: cycle 0 is the first.
        if (_snapshot is not null && _cycleTicks > 0)
        {
            _takenTicks = Math.Max(DateTime.SpecifyKind(_snapshot.TakenUtc, DateTimeKind.Utc).Ticks, _origin);
            _chainStart = Padding.FloorDiv(_takenTicks - _origin, _cycleTicks);
        }

        _chains = _input.Mode == ScheduleMode.Pool && (_cooldownTicks > 0 || (_snapshot is not null && _order == PlayOrder.ShuffleEpisodesInOrder));
    }

    public bool IsEmpty => _items.Count == 0 || _cycleTicks <= 0;

    public ResolvedProgram? Resolve(DateTime utc)
    {
        if (IsEmpty)
        {
            var slate = SlateHour(utc);
            return new ResolvedProgram(slate, utc - slate.StartUtc);
        }

        var elapsed = utc.Ticks - _origin;
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
            var next = Next(current);
            if (next.EndUtc <= current.EndUtc)
            {
                // The timeline must move forward. A layout fault that does not would repeat one entry without end,
                // and every caller keeps what it is given: stop short rather than take the server down.
                yield break;
            }

            current = next;
        }
    }

    private ScheduleEntry Next(ScheduleEntry entry)
    {
        // Resolve a tick after the end; cheap because cycles are cached.
        return Resolve(entry.EndUtc)!.Entry;
    }

    private ScheduleEntry MakeEntry(Cycle cycle, long k, int i, long inCycle)
    {
        var cycleStart = _origin + (k * _cycleTicks);
        var item = _items[cycle.Order[i]];
        var itemStart = cycleStart + cycle.Starts[i];
        var itemEnd = itemStart + item.RuntimeTicks;
        var slotEnd = cycleStart + cycle.Starts[i] + _padded[cycle.Order[i]];

        if (cycle.Gated is not null && cycle.Gated[i])
        {
            // The item is not allowed at this time of day and nothing else fit here: dead air for the whole slot.
            return ScheduleEntry.Slate(_input, Utc(itemStart), Utc(slotEnd), "Not allowed at this time");
        }

        if (inCycle < cycle.Starts[i] + item.RuntimeTicks)
        {
            return ScheduleEntry.Program(_input, item, Utc(itemStart), Utc(itemEnd));
        }

        var gap = ScheduleEntry.Slate(_input, Utc(itemEnd), Utc(slotEnd));
        if (_input.HasBumpers && slotEnd > itemEnd)
        {
            // The gap after the program: post-roll, fill clips, card for whatever is left, pre-roll for what follows.
            var offset = inCycle - (cycle.Starts[i] + item.RuntimeTicks);
            var pieces = BumperFill.Plan(slotEnd - itemEnd, _clips, StableHash.Seed(_input.StationId, _input.Revision, "bumper:" + i, k), programBefore: true, programAfter: !NextIsGated(cycle, k, i));
            foreach (var p in pieces)
            {
                if (offset >= p.StartOffset && offset < p.EndOffset)
                {
                    return BumperFill.ToEntry(gap, p, _clips.WeatherLabel);
                }
            }
        }

        return gap;
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
        var start = Padding.FloorToGrid(utc, TimeSpan.FromHours(1));
        return ScheduleEntry.Slate(_input, start, start.AddHours(1));
    }

    private static DateTime Utc(long ticks) => new(ticks, DateTimeKind.Utc);

    private Cycle GetCycle(long k)
    {
        if (_cycles.TryGetValue(k, out var c))
        {
            return c;
        }

        if (_chains && k > _chainStart)
        {
            // With a cooldown (or a series position to carry) each cycle reads the previous one as it finally aired,
            // so build the chain from the chain start (the anchor, or the snapshot's cycle; or the nearest cycle
            // still cached) up to k. A cycle is a shuffle and a few passes, so even thousands of them cost milliseconds.
            var from = _cycles.Keys.Where(x => x >= _chainStart && x < k).DefaultIfEmpty(_chainStart - 1).Max();
            Cycle? prev = from >= _chainStart && _cycles.TryGetValue(from, out var cached) ? cached : null;
            for (var i = prev is null ? _chainStart : from + 1; i <= k; i++)
            {
                prev = BuildCycle(i, i == _chainStart ? null : prev);
                Store(i, prev);
            }

            return prev!;
        }

        c = BuildCycle(k, null);
        Store(k, c);
        return c;
    }

    private void Store(long k, Cycle c)
    {
        _cycles[k] = c;
        if (_cycles.Count > 128)
        {
            // Keep the cycles nearest the one just built; with a cooldown the chain restarts from the nearest
            // cached cycle, so a lookup just before the cached range must not fall all the way back to the anchor.
            foreach (var key in _cycles.Keys.OrderByDescending(x => Math.Abs(x - k)).Take(_cycles.Count - 64).ToList())
            {
                _cycles.TryRemove(key, out _);
            }
        }
    }

    /// <summary>
    /// Lays out cycle k. <paramref name="previous"/> is the cycle before it as it finally aired (chain mode, used
    /// with a cooldown); when null the passes look at the previous cycle as the gate left it, which is exact for
    /// the back-to-back seam and close enough before the anchor.
    /// </summary>
    private Cycle BuildCycle(long k, Cycle? previous)
    {
        var cycleStart = _origin + (k * _cycleTicks);
        var runs = GatedRuns(k, cycleStart);
        var fromSnapshot = UsesSnapshot(k, previous);

        if ((_rules.Count > 0 || fromSnapshot) && _input.Mode == ScheduleMode.Pool && _avoidRepeats && runs.Count > 2)
        {
            // The daypart gate can undo the seam check PoolOrdering made: the previous cycle may now end with the
            // same series or movie this one starts with, possibly with gated dead air in between. Re-check
            // against what the previous cycle actually airs last (or, in the snapshot's cycle, what the previous
            // revision aired last, at the first slot this revision really airs).
            var prevKey = PreviousLastKey(k, cycleStart, previous);
            if (prevKey is not null && FixSeamAfterGate(runs, cycleStart, prevKey, fromSnapshot ? FirstRealRun(runs, cycleStart) : 0))
            {
                runs = RestoreEpisodeOrder(runs);
            }
        }

        if (_cooldownTicks > 0 && _input.Mode == ScheduleMode.Pool && runs.Count > 2 && ApplyCooldown(runs, k, cycleStart, previous))
        {
            runs = RestoreEpisodeOrder(runs);
        }

        var order = runs.SelectMany(r => r).ToArray();
        var starts = StartsOf(order);

        Dictionary<Guid, int>? offsets = null;
        if (_snapshot is not null && _order == PlayOrder.ShuffleEpisodesInOrder && _input.Mode == ScheduleMode.Pool && (fromSnapshot || previous is not null))
        {
            // Continuity: each series keeps the positions the passes gave it, but plays on from where the previous
            // revision (snapshot's cycle) or the previous cycle (chain) left it. Positions before the snapshot never
            // air and take the episodes before that point.
            var offsetsIn = previous?.EpisodeOffsets ?? EpisodeOrder.OffsetsFrom(_snapshot, _items);
            offsets = EpisodeOrder.Resume(order, _items, starts, offsetsIn, fromSnapshot ? _takenTicks - cycleStart : long.MinValue);

            // Resume moved episodes between the positions their series holds, and episodes differ in length: the
            // slot times have to be those of the order that airs. Left as laid out before the move, a shorter
            // episode in a longer one's slot ended before the next slot began, and the instant in between resolved
            // to an entry of no length that an enumeration never got past (it filled memory until the server was
            // killed, 1.25.0.0 to 1.26.1.0). The cycle's length does not change: it holds the same items.
            starts = StartsOf(order);
        }

        var gated = _rules.Count > 0 ? DaypartGate.Flags(order, _items, cycleStart, _padded, _tz, _rules) : null;
        return new Cycle(order, starts, gated, offsets);
    }

    /// <summary>Offset of each position's slot from the start of the cycle, for the items in the order given.</summary>
    private long[] StartsOf(int[] order)
    {
        var starts = new long[order.Length];
        long acc = 0;
        for (var i = 0; i < order.Length; i++)
        {
            starts[i] = acc;
            acc += _padded[order[i]];
        }

        return starts;
    }

    /// <summary>True when cycle k is the snapshot's cycle being built fresh: the seam and the cooldown read the snapshot.</summary>
    private bool UsesSnapshot(long k, Cycle? previous) => _snapshot is not null && previous is null && k == _chainStart;

    /// <summary>Index of the first run whose slot starts at or after the snapshot was taken (the first this revision really airs from its start).</summary>
    private int FirstRealRun(List<int[]> runs, long cycleStart)
    {
        var starts = RunStarts(runs, cycleStart);
        for (var r = 0; r < starts.Length; r++)
        {
            if (starts[r] >= _takenTicks)
            {
                return r;
            }
        }

        return starts.Length;
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
    private bool FixSeamAfterGate(List<int[]> runs, long cycleStart, string prevKey, int fromRun = 0)
    {
        var flags = RunFlags(runs, cycleStart);
        var first = fromRun < flags.Length ? Array.IndexOf(flags, false, fromRun) : -1;
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

    /// <summary>
    /// Key of the last series or movie the cycle before k airs: from <paramref name="previous"/> as it finally aired
    /// when given (chain mode), else from that cycle's runs as the gate left them.
    /// </summary>
    private string? PreviousLastKey(long k, long cycleStart, Cycle? previous)
        => previous is not null ? LastAiredKey(previous)
            : UsesSnapshot(k, previous) ? _snapshot!.LastKey
            : LastAiredKeyAsGated(k - 1, cycleStart - _cycleTicks);

    /// <summary>Key of the last series or movie cycle k airs, judged from the runs as the gate left them (null when it airs nothing).</summary>
    private string? LastAiredKeyAsGated(long k, long cycleStart)
    {
        var runs = GatedRuns(k, cycleStart);
        var last = Array.LastIndexOf(RunFlags(runs, cycleStart), false);
        return last < 0 ? null : MarathonOrder.KeyOfRun(runs[last], _items);
    }

    /// <summary>Key of the last series or movie a laid-out cycle airs (null when it airs nothing).</summary>
    private string? LastAiredKey(Cycle cycle)
    {
        for (var i = cycle.Order.Length - 1; i >= 0; i--)
        {
            if (cycle.Gated is null || !cycle.Gated[i])
            {
                return PoolOrdering.KeyOf(_items[cycle.Order[i]]);
            }
        }

        return null;
    }

    /// <summary>Start tick of each run in a laid-out cycle.</summary>
    private long[] RunStarts(List<int[]> runs, long cycleStart)
    {
        var starts = new long[runs.Count];
        var t = cycleStart;
        for (var r = 0; r < runs.Count; r++)
        {
            starts[r] = t;
            t += RunLength(runs[r]);
        }

        return starts;
    }

    private long RunLength(int[] run)
    {
        long length = 0;
        foreach (var i in run)
        {
            length += _padded[i];
        }

        return length;
    }

    /// <summary>
    /// When each series or movie the previous cycle aired within the cooldown window last ended, by key. From the
    /// laid-out cycle when the chain provides it, otherwise from the runs as the gate left them.
    /// </summary>
    private Dictionary<string, long> TailEnds(long k, long cycleStart, Cycle? previous)
    {
        var prevStart = cycleStart - _cycleTicks;
        var ends = new Dictionary<string, long>(StringComparer.Ordinal);
        if (UsesSnapshot(k, previous))
        {
            // What the previous revision aired within the window rests the same way the previous cycle would.
            foreach (var (key, entry) in _snapshot!.Keys)
            {
                var end = DateTime.SpecifyKind(entry.LastEndUtc, DateTimeKind.Utc).Ticks;
                if (end > cycleStart - _cooldownTicks)
                {
                    ends[key] = end;
                }
            }

            return ends;
        }

        if (previous is not null)
        {
            // When the previous cycle is the snapshot's, its slots before the snapshot never aired: what they hold
            // rested according to the snapshot instead.
            var afterSnapshot = _snapshot is not null && k - 1 == _chainStart;
            for (var i = 0; i < previous.Order.Length; i++)
            {
                var start = prevStart + previous.Starts[i];
                var end = start + _padded[previous.Order[i]];
                if ((previous.Gated is null || !previous.Gated[i]) && end > cycleStart - _cooldownTicks && (!afterSnapshot || start >= _takenTicks))
                {
                    ends[PoolOrdering.KeyOf(_items[previous.Order[i]])] = end;
                }
            }

            if (afterSnapshot)
            {
                foreach (var (key, entry) in _snapshot!.Keys)
                {
                    var end = DateTime.SpecifyKind(entry.LastEndUtc, DateTimeKind.Utc).Ticks;
                    if (end > cycleStart - _cooldownTicks && (!ends.TryGetValue(key, out var known) || end > known))
                    {
                        ends[key] = end;
                    }
                }
            }

            return ends;
        }

        var prev = GatedRuns(k - 1, prevStart);
        var flags = RunFlags(prev, prevStart);
        var starts = RunStarts(prev, prevStart);
        for (var r = 0; r < prev.Count; r++)
        {
            var end = starts[r] + RunLength(prev[r]);
            if (!flags[r] && end > cycleStart - _cooldownTicks)
            {
                ends[MarathonOrder.KeyOfRun(prev[r], _items)] = end;
            }
        }

        return ends;
    }

    /// <summary>
    /// Soft rule: while a series or movie airs less than the cooldown after its previous airing, swap it with
    /// another run (later ones first, then earlier) when that leaves fewer such violations, keeps every neighbour
    /// distinct and puts nothing new behind the daypart gate. Positions nothing can fix are left alone, and the
    /// search stops after a budget so a hopeless pool (cooldown longer than the round) costs little.
    /// Weighted repeats inside the cycle are held apart the same way.
    /// </summary>
    private bool ApplyCooldown(List<int[]> runs, long k, long cycleStart, Cycle? previous)
    {
        var tail = TailEnds(k, cycleStart, previous);
        var seamKey = PreviousLastKey(k, cycleStart, previous);
        var flags = RunFlags(runs, cycleStart);
        var gated = flags.Count(f => f);
        // In the snapshot's cycle the slots before the snapshot never air under this revision: a rested film may
        // sit there freely, and only the slots from the snapshot on count as violations.
        var minStart = UsesSnapshot(k, previous) ? _takenTicks : long.MinValue;

        // A series or movie that airs c times a round needs c rests; when those cannot fit in the round the rule
        // is impossible for it, so it is left out rather than searched for in vain.
        var hopeless = new HashSet<string>(StringComparer.Ordinal);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < runs.Count; i++)
        {
            if (!flags[i])
            {
                var key = MarathonOrder.KeyOfRun(runs[i], _items);
                counts[key] = counts.TryGetValue(key, out var c) ? c + 1 : 1;
            }
        }

        foreach (var (key, c) in counts)
        {
            if (c * (double)_cooldownTicks > _cycleTicks)
            {
                hopeless.Add(key);
            }
        }

        var violations = CooldownViolations(runs, cycleStart, tail, flags, hopeless, minStart);
        var stuck = new HashSet<int>();
        var budget = 4000;
        var changed = false;
        while (violations.Count > 0 && budget > 0)
        {
            var i = violations.FirstOrDefault(v => !stuck.Contains(v), -1);
            if (i < 0)
            {
                break;
            }

            var fixedIt = false;
            foreach (var j in Enumerable.Range(i + 1, runs.Count - i - 1).Concat(Enumerable.Range(0, i).Reverse()))
            {
                if (--budget <= 0)
                {
                    break;
                }

                if (!MarathonOrder.TrySwap(runs, _items, i, j, seamKey))
                {
                    continue;
                }

                var newFlags = RunFlags(runs, cycleStart);
                var after = newFlags.Count(f => f) <= gated ? CooldownViolations(runs, cycleStart, tail, newFlags, hopeless, minStart) : null;
                if (after is not null && after.Count < violations.Count)
                {
                    flags = newFlags;
                    gated = newFlags.Count(f => f);
                    violations = after;
                    fixedIt = true;
                    changed = true;
                    break;
                }

                (runs[i], runs[j]) = (runs[j], runs[i]);
            }

            if (!fixedIt)
            {
                stuck.Add(i);
            }
        }

        return changed;
    }

    /// <summary>Positions of aired runs that start less than the cooldown after the previous airing of the same series or movie.</summary>
    private List<int> CooldownViolations(List<int[]> runs, long cycleStart, Dictionary<string, long> tailEnds, bool[] flags, HashSet<string> hopeless, long minStart = long.MinValue)
    {
        var lastEnd = new Dictionary<string, long>(tailEnds, StringComparer.Ordinal);
        var starts = RunStarts(runs, cycleStart);
        var result = new List<int>();
        for (var i = 0; i < runs.Count; i++)
        {
            if (flags[i])
            {
                continue;
            }

            var key = MarathonOrder.KeyOfRun(runs[i], _items);
            if (starts[i] >= minStart && !hopeless.Contains(key) && lastEnd.TryGetValue(key, out var end) && starts[i] - end < _cooldownTicks)
            {
                result.Add(i);
            }

            lastEnd[key] = starts[i] + RunLength(runs[i]);
        }

        return result;
    }

    /// <summary>Per run: true when the daypart gate keeps it off the air in its slot (all false without rules).</summary>
    private bool[] RunFlags(List<int[]> runs, long cycleStart)
    {
        if (_rules.Count == 0)
        {
            return new bool[runs.Count];
        }

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
        public Cycle(int[] order, long[] starts, bool[]? gated, Dictionary<Guid, int>? episodeOffsets = null)
        {
            Order = order;
            Starts = starts;
            Gated = gated;
            EpisodeOffsets = episodeOffsets;
        }

        public int[] Order { get; }

        public long[] Starts { get; }

        /// <summary>Per series: the index in its sorted episodes the next cycle starts at (null outside a continuity chain).</summary>
        public Dictionary<Guid, int>? EpisodeOffsets { get; }

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
