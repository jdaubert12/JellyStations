using Jellyfin.Plugin.JellyStations.Models;

namespace Jellyfin.Plugin.JellyStations.Scheduling;

/// <summary>
/// Weekly template of blocks in the station's local time zone with a continuous stream between them: the
/// filler pool in Slots mode, or the station's pool (with its shuffle, series spacing, marathon and daytime
/// rules) in Pool mode with blocks. Weeks are materialized in order from the anchor week so cursors advance
/// deterministically; results are cached per week.
/// </summary>
internal sealed class WeeklySlotScheduler : IStationScheduler
{
    private const int MaxCachedWeeks = 24;
    private const string FillerScope = "filler";
    private static readonly TimeSpan MinTruncatedFiller = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan GateStep = TimeSpan.FromMinutes(15);

    private readonly ScheduleInput _input;
    private readonly TimeZoneInfo _tz;
    private readonly DateTime _anchorMondayLocal;
    private readonly FillerStream _filler;
    private readonly object _lock = new();
    private readonly Dictionary<long, WeekState> _stateOut = new();
    private readonly Dictionary<long, List<ScheduleEntry>> _entries = new();
    private readonly Dictionary<(string Scope, long Round), int[]> _perms = new();
    private readonly Dictionary<long, int[]> _fillerOrders = new();

    public WeeklySlotScheduler(ScheduleInput input)
    {
        _input = input;
        _tz = ResolveTimeZone(input.TimeZoneId);
        var anchorLocal = TimeZoneInfo.ConvertTimeFromUtc(input.AnchorUtc, _tz).Date;
        var daysSinceMonday = ((int)anchorLocal.DayOfWeek + 6) % 7;
        _anchorMondayLocal = anchorLocal.AddDays(-daysSinceMonday);
        // Slots-mode filler keeps its original behaviour (plain pass order, cut at the gap end) so existing stations keep their timeline.
        _filler = input.Mode == ScheduleMode.Pool
            ? new FillerStream(input.Pool, input.PoolOrder, input.AvoidSameSeriesBackToBack, input.MarathonMin, input.MarathonMax, input.DaypartRules, input.PoolOrder != PlayOrder.Sequential, "Pool")
            : new FillerStream(input.Filler, input.FillerOrder, false, 1, 1, Array.Empty<DaypartRuleInput>(), false, "Filler");
    }

    public static TimeZoneInfo ResolveTimeZone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception)
        {
            return TimeZoneInfo.Utc;
        }
    }

    public ResolvedProgram? Resolve(DateTime utc)
    {
        var w = WeekIndexOf(utc);
        foreach (var candidate in new[] { w, w - 1, w + 1, w - 2, w + 2 })
        {
            var entry = Find(GetWeekEntries(candidate), utc);
            if (entry is not null)
            {
                return new ResolvedProgram(entry, utc - entry.StartUtc);
            }
        }

        return null;
    }

    public IEnumerable<ScheduleEntry> Enumerate(DateTime fromUtc, DateTime toUtc)
    {
        if (toUtc <= fromUtc)
        {
            return Array.Empty<ScheduleEntry>();
        }

        var first = WeekIndexOf(fromUtc) - 2;
        var last = WeekIndexOf(toUtc) + 1;
        var result = new List<ScheduleEntry>();
        for (var w = first; w <= last; w++)
        {
            foreach (var e in GetWeekEntries(w))
            {
                if (e.EndUtc > fromUtc && e.StartUtc < toUtc)
                {
                    result.Add(e);
                }
            }
        }

        return result.OrderBy(e => e.StartUtc).ToList();
    }

    private static ScheduleEntry? Find(List<ScheduleEntry> entries, DateTime utc)
    {
        if (entries.Count == 0)
        {
            return null;
        }

        int lo = 0, hi = entries.Count - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            var e = entries[mid];
            if (utc < e.StartUtc)
            {
                hi = mid - 1;
            }
            else if (utc >= e.EndUtc)
            {
                lo = mid + 1;
            }
            else
            {
                return e;
            }
        }

        return null;
    }

    private long WeekIndexOf(DateTime utc)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(utc, _tz).Date;
        var days = (long)(local - _anchorMondayLocal).TotalDays;
        return Padding.FloorDiv(days, 7);
    }

    private DateTime WeekStartLocal(long w) => _anchorMondayLocal.AddDays(7 * w);

    private DateTime WeekStartUtc(long w) => ToUtc(WeekStartLocal(w));

    private DateTime ToUtc(DateTime local)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (_tz.IsInvalidTime(local))
        {
            local = local.AddHours(1);
        }

        if (_tz.IsAmbiguousTime(local))
        {
            var offset = _tz.GetAmbiguousTimeOffsets(local).Max();
            return DateTime.SpecifyKind(local - offset, DateTimeKind.Utc);
        }

        return TimeZoneInfo.ConvertTimeToUtc(local, _tz);
    }

    private List<Occurrence> OccurrencesOfWeek(long w)
    {
        var list = new List<Occurrence>();
        var weekStart = WeekStartLocal(w);
        for (var d = 0; d < 7; d++)
        {
            var date = weekStart.AddDays(d);
            for (var b = 0; b < _input.Blocks.Count; b++)
            {
                var block = _input.Blocks[b];
                if (!block.OccursOn(date))
                {
                    continue;
                }

                var start = ToUtc(date + block.StartLocal);
                list.Add(new Occurrence(b, start, start + block.Duration));
            }
        }

        list.Sort((a, b) => a.StartUtc != b.StartUtc ? a.StartUtc.CompareTo(b.StartUtc) : a.BlockIndex.CompareTo(b.BlockIndex));

        // Earlier block wins; later overlapping blocks are clipped or dropped.
        var clipped = new List<Occurrence>(list.Count);
        var prevEnd = DateTime.MinValue;
        foreach (var occ in list)
        {
            var start = occ.StartUtc < prevEnd ? prevEnd : occ.StartUtc;
            if (start >= occ.EndUtc)
            {
                continue;
            }

            clipped.Add(occ with { StartUtc = start });
            prevEnd = occ.EndUtc;
        }

        return clipped;
    }

    private DateTime EndGapTarget(long w)
    {
        var next = OccurrencesOfWeek(w + 1);
        return next.Count > 0 ? next[0].StartUtc : WeekStartUtc(w + 2);
    }

    private List<ScheduleEntry> GetWeekEntries(long w)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(w, out var cached))
            {
                return cached;
            }

            if (_entries.Count >= MaxCachedWeeks)
            {
                _entries.Clear();
            }

            var state = StateIn(w);
            var entries = BuildWeek(w, state);
            _entries[w] = entries;
            return entries;
        }
    }

    private WeekState StateIn(long w)
    {
        if (w <= 0)
        {
            return WeekState.Initial(WeekStartUtc(w));
        }

        if (_stateOut.TryGetValue(w - 1, out var s))
        {
            return s;
        }

        // Find the latest computed week below w and roll forward.
        long start = 0;
        for (var k = w - 1; k >= 0; k--)
        {
            if (_stateOut.ContainsKey(k))
            {
                start = k + 1;
                break;
            }
        }

        var state = start == 0 ? WeekState.Initial(WeekStartUtc(0)) : _stateOut[start - 1];
        for (var k = start; k < w; k++)
        {
            BuildWeek(k, state);
            state = _stateOut[k];
        }

        return state;
    }

    private List<ScheduleEntry> BuildWeek(long w, WeekState stateIn)
    {
        var state = stateIn.Clone();
        var entries = new List<ScheduleEntry>();
        var occurrences = OccurrencesOfWeek(w);

        for (var i = 0; i < occurrences.Count; i++)
        {
            var occ = occurrences[i];
            var block = _input.Blocks[occ.BlockIndex];

            // Whatever comes after this block: the next block of the week, or the first block of the next week
            // (the same instant the end-of-week filler targets). Overruns and late starts never cross it.
            var next = i + 1 < occurrences.Count ? occurrences[i + 1].StartUtc : (w >= 0 ? EndGapTarget(w) : WeekStartUtc(w + 1));

            if (occ.StartUtc > state.CarryEnd)
            {
                // The stream before the block may finish its program past the block start when the block allows a
                // late start, within the block's delay allowance and never into the block's own end.
                var latest = Min(Min(occ.StartUtc + block.MaxStartDelay, occ.EndUtc), next);
                var gapReached = FillFiller(entries, state, state.CarryEnd, occ.StartUtc, block.MaxStartDelay > TimeSpan.Zero && latest > occ.StartUtc ? latest : null);
                if (gapReached > state.CarryEnd)
                {
                    state.CarryEnd = gapReached;
                }
            }

            var blockStart = occ.StartUtc > state.CarryEnd ? occ.StartUtc : state.CarryEnd;
            var blockEnd = occ.EndUtc;
            if (blockStart > occ.StartUtc && block.MaxStartDelay > TimeSpan.Zero)
            {
                // Started late to let a program finish: keep the full duration, up to whatever comes next.
                blockEnd = Min(blockStart + (occ.EndUtc - occ.StartUtc), next);
            }

            var reached = blockEnd > occ.EndUtc ? blockEnd : occ.EndUtc;
            if (blockStart < blockEnd)
            {
                reached = FillBlock(entries, state, block, blockStart, blockEnd, block.RemainderMayOverrun ? next : blockEnd);
            }

            if (reached > state.CarryEnd)
            {
                state.CarryEnd = reached;
            }
        }

        if (w >= 0)
        {
            var target = EndGapTarget(w);
            if (target > state.CarryEnd)
            {
                FillFiller(entries, state, state.CarryEnd, target);
                state.CarryEnd = target;
            }
        }
        else
        {
            var target = WeekStartUtc(w + 1);
            if (target > state.CarryEnd)
            {
                FillFiller(entries, state, state.CarryEnd, target);
                state.CarryEnd = target;
            }

            // Pre-anchor weeks are computed standalone; clip them so they never overlap the next week.
            entries = entries
                .Where(e => e.StartUtc < target)
                .Select(e => e.EndUtc > target ? e with { EndUtc = target } : e)
                .ToList();
        }

        if (w >= 0)
        {
            _stateOut[w] = state;
        }

        return BumperFill.Expand(entries, BumperFill.Clips.From(_input), e => StableHash.Seed(_input.StationId, _input.Revision, "bumper", e.StartUtc.Ticks));
    }

    /// <summary>Fills a block occurrence; returns the instant covered, which exceeds <paramref name="end"/> only when the remainder stream overran.</summary>
    private DateTime FillBlock(List<ScheduleEntry> entries, WeekState state, SlotBlockInput block, DateTime start, DateTime end, DateTime overrunLimit)
    {
        var scope = "block:" + block.Id.ToString("N");
        var cursor = state.BlockCursor(block.Id);
        var reached = FillBlockItems(entries, block.Items, block.Order, cursor, scope, start, end, block.Overflow == SlotOverflow.TruncateLast, block.Name);
        if (reached < end)
        {
            return FillFiller(entries, state, reached, end, overrunLimit > end ? overrunLimit : null);
        }

        return end;
    }

    /// <summary>Packs block items into [start, end) in cursor order. Returns the instant up to which the range was covered.</summary>
    private DateTime FillBlockItems(List<ScheduleEntry> entries, IReadOnlyList<PlayableItem> items, PlayOrder order, Cursor cursor, string scope, DateTime start, DateTime end, bool truncateLast, string label)
    {
        var t = start;
        if (items.Count == 0 || items.All(i => i.RuntimeTicks <= 0))
        {
            entries.Add(Slate(t, end, label));
            return end;
        }

        var placedAny = false;
        while (t < end)
        {
            var item = items[Perm(scope, order, items, cursor.Round)[cursor.Index]];
            if (item.RuntimeTicks <= 0)
            {
                cursor.Advance(items.Count);
                continue;
            }

            if (item.Runtime <= end - t)
            {
                t = Place(entries, item, t, end, label);
                cursor.Advance(items.Count);
                placedAny = true;
                continue;
            }

            // Item does not fit in the remainder: cut it when asked to, or when nothing at all fit; otherwise hand the rest back.
            if (truncateLast || !placedAny)
            {
                entries.Add(Program(item, t, end, true, label));
                cursor.Advance(items.Count);
                return end;
            }

            return t;
        }

        return t;
    }

    /// <summary>
    /// Plays the between-block stream over [start, end): the next unplayed item of the current pass that may air
    /// now, preferring (when shuffling) one that fits before the range ends. Items a daytime rule keeps off the
    /// air right now stay unplayed and come up later in the pass; when nothing unplayed may air, the next pass
    /// starts; when nothing in the pool may air, the card shows until something may.
    /// </summary>
    private DateTime FillFiller(List<ScheduleEntry> entries, WeekState state, DateTime start, DateTime end, DateTime? overrunLimit = null)
    {
        var items = _filler.Items;
        var label = _filler.Label;
        if (items.Count == 0 || items.All(i => i.RuntimeTicks <= 0))
        {
            entries.Add(Slate(start, end, label));
            return end;
        }

        var cursor = state.FillerCursor;
        var t = start;
        while (t < end)
        {
            BeginPass(cursor);
            var remaining = end - t;
            var p = PickFiller(cursor, t, remaining);
            if (p < 0 && cursor.UsedCount > 0 && PickFiller(PeekNextPass(cursor), t, remaining) >= 0)
            {
                // Whatever is left of this pass may not air now, but a fresh pass has something that may: start it.
                cursor.NextPass();
                BeginPass(cursor);
                p = PickFiller(cursor, t, remaining);
            }

            if (p < 0)
            {
                var until = NextAllowed(cursor, t, end);
                entries.Add(Slate(t, until, "Not allowed at this time"));
                t = until;
                continue;
            }

            var item = items[FillerOrder(cursor.Round)[p]];
            if (item.Runtime <= remaining)
            {
                t = Place(entries, item, t, end, label);
                cursor.Mark(p);
                continue;
            }

            // Nothing unplayed fits before the end. Past a block end the same stream carries on anyway, so when
            // allowed let the item finish (padded to the grid) as long as it ends before whatever comes next.
            if (overrunLimit.HasValue && t + item.Runtime <= overrunLimit.Value)
            {
                cursor.Mark(p);
                return Place(entries, item, t, overrunLimit.Value, label);
            }

            // Cut the item when a meaningful part of it fits, otherwise show the card.
            if (remaining >= Min(MinTruncatedFiller, TimeSpan.FromTicks(item.RuntimeTicks / 2)))
            {
                entries.Add(Program(item, t, end, true, label));
                cursor.Mark(p);
            }
            else
            {
                entries.Add(Slate(t, end, label));
            }

            return end;
        }

        return end;
    }

    /// <summary>Starts the cursor's pass if it has not started, and the next pass when every item of it has played.</summary>
    private void BeginPass(FillerCursor cursor)
    {
        var items = _filler.Items;
        if (cursor.Used.Length == items.Count && cursor.UsedCount < items.Count)
        {
            return;
        }

        if (cursor.Used.Length == items.Count)
        {
            cursor.NextPass();
        }

        var order = FillerOrder(cursor.Round);
        cursor.Used = new bool[items.Count];
        cursor.UsedCount = 0;
        for (var p = 0; p < order.Length; p++)
        {
            if (items[order[p]].RuntimeTicks <= 0)
            {
                cursor.Mark(p);
            }
        }
    }

    /// <summary>
    /// Position (in the pass order) of the item to play at <paramref name="t"/>, or -1 when no unplayed item may air
    /// then. With episodes in order, a series' later episodes wait for its earlier ones.
    /// </summary>
    private int PickFiller(FillerCursor cursor, DateTime t, TimeSpan remaining)
    {
        var items = _filler.Items;
        var order = FillerOrder(cursor.Round);
        var preferFit = _filler.PreferFit;
        var waiting = _filler.Order == PlayOrder.ShuffleEpisodesInOrder ? new HashSet<Guid>() : null;
        var first = -1;
        for (var p = 0; p < order.Length; p++)
        {
            if (cursor.Used[p])
            {
                continue;
            }

            var item = items[order[p]];
            var series = item.SeriesId;
            if (waiting is not null && series.HasValue && !waiting.Add(series.Value))
            {
                continue;
            }

            if (!DaypartGate.IsAllowed(item, t, _tz, _filler.Rules))
            {
                continue;
            }

            if (item.Runtime <= remaining || !preferFit)
            {
                return p;
            }

            if (first < 0)
            {
                first = p;
            }
        }

        return first;
    }

    /// <summary>A fresh cursor for the pass after the given one, used to look ahead without moving the stream.</summary>
    private FillerCursor PeekNextPass(FillerCursor cursor)
    {
        var next = cursor.Clone();
        next.NextPass();
        BeginPass(next);
        return next;
    }

    /// <summary>First instant in [t, end] at which something unplayed (this pass or the next) may air, stepping in quarter hours.</summary>
    private DateTime NextAllowed(FillerCursor cursor, DateTime t, DateTime end)
    {
        var next = PeekNextPass(cursor);
        var u = t;
        while (true)
        {
            u += GateStep;
            if (u >= end)
            {
                return end;
            }

            var remaining = end - u;
            if (PickFiller(cursor, u, remaining) >= 0 || PickFiller(next, u, remaining) >= 0)
            {
                return u;
            }
        }
    }

    private int[] FillerOrder(long round)
    {
        if (_fillerOrders.TryGetValue(round, out var order))
        {
            return order;
        }

        if (_fillerOrders.Count > 128)
        {
            _fillerOrders.Clear();
        }

        order = PoolOrdering.Build(_filler.Items, _filler.Order, _filler.AvoidRepeats, _filler.MarathonMin, _filler.MarathonMax, k => StableHash.Seed(_input.StationId, _input.Revision, FillerScope, k), round);
        _fillerOrders[round] = order;
        return order;
    }

    /// <summary>Adds the program at t plus the slate that pads it to the grid (clipped to end). Returns the new t.</summary>
    private DateTime Place(List<ScheduleEntry> entries, PlayableItem item, DateTime t, DateTime end, string label)
    {
        var runtime = item.Runtime;
        entries.Add(Program(item, t, t + runtime, false, label));
        var padded = Padding.Pad(runtime + _input.Break, _input.PaddingGrid, _input.PaddingByLength);
        var slateEnd = t + padded;
        if (slateEnd > end)
        {
            slateEnd = end;
        }

        t += runtime;
        if (slateEnd > t)
        {
            entries.Add(Slate(t, slateEnd, label));
            t = slateEnd;
        }

        return t;
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;

    private int[] Perm(string scope, PlayOrder order, IReadOnlyList<PlayableItem> items, long round)
    {
        var n = items.Count;
        if (order == PlayOrder.Sequential)
        {
            round = 0;
        }

        var key = (scope, round);
        if (_perms.TryGetValue(key, out var p))
        {
            return p;
        }

        if (_perms.Count > 512)
        {
            _perms.Clear();
        }

        p = order == PlayOrder.Sequential
            ? Enumerable.Range(0, n).ToArray()
            : SplitMix64.Permutation(n, StableHash.Seed(_input.StationId, _input.Revision, scope, round));
        if (order == PlayOrder.ShuffleEpisodesInOrder)
        {
            EpisodeOrder.Apply(p, items);
        }

        _perms[key] = p;
        return p;
    }

    private ScheduleEntry Program(PlayableItem item, DateTime start, DateTime end, bool truncated, string label)
        => new(_input.StationId, _input.Revision, EntryKind.Program, item, start, end, truncated, label);

    private ScheduleEntry Slate(DateTime start, DateTime end, string label)
        => new(_input.StationId, _input.Revision, EntryKind.Slate, null, start, end, false, label);

    /// <summary>The stream that plays whenever no block is on. PreferFit: before cutting an item at a gap end, look for an unplayed one that fits.</summary>
    private sealed record FillerStream(IReadOnlyList<PlayableItem> Items, PlayOrder Order, bool AvoidRepeats, int MarathonMin, int MarathonMax, IReadOnlyList<DaypartRuleInput> Rules, bool PreferFit, string Label);

    private sealed record Occurrence(int BlockIndex, DateTime StartUtc, DateTime EndUtc);

    private sealed class Cursor
    {
        public long Round { get; set; }

        public int Index { get; set; }

        public void Advance(int n)
        {
            Index++;
            if (Index >= n)
            {
                Index = 0;
                Round++;
            }
        }

        public Cursor Clone() => new() { Round = Round, Index = Index };
    }

    /// <summary>Where the between-block stream is: which pass, and which positions of it have played.</summary>
    private sealed class FillerCursor
    {
        public long Round { get; set; }

        public bool[] Used { get; set; } = Array.Empty<bool>();

        public int UsedCount { get; set; }

        public void Mark(int p)
        {
            if (!Used[p])
            {
                Used[p] = true;
                UsedCount++;
            }
        }

        public void NextPass()
        {
            Round++;
            Used = Array.Empty<bool>();
            UsedCount = 0;
        }

        public FillerCursor Clone() => new() { Round = Round, Used = (bool[])Used.Clone(), UsedCount = UsedCount };
    }

    private sealed class WeekState
    {
        private readonly Dictionary<Guid, Cursor> _blocks = new();

        public DateTime CarryEnd { get; set; }

        public FillerCursor FillerCursor { get; private set; } = new();

        public static WeekState Initial(DateTime carryEnd) => new() { CarryEnd = carryEnd };

        public Cursor BlockCursor(Guid blockId)
        {
            if (!_blocks.TryGetValue(blockId, out var c))
            {
                c = new Cursor();
                _blocks[blockId] = c;
            }

            return c;
        }

        public WeekState Clone()
        {
            var s = new WeekState { CarryEnd = CarryEnd, FillerCursor = FillerCursor.Clone() };
            foreach (var kv in _blocks)
            {
                s._blocks[kv.Key] = kv.Value.Clone();
            }

            return s;
        }
    }
}
