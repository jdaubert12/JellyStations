using Jellyfin.Plugin.JellyStations.Models;
using Jellyfin.Plugin.JellyStations.Scheduling;
using Jellyfin.Plugin.JellyStations.Services;
using Jellyfin.Plugin.JellyStations.Tests.Fixtures;
using Xunit;

namespace Jellyfin.Plugin.JellyStations.Tests.Scheduling;

/// <summary>A new schedule takes over from the one on the air at the next program boundary, not wherever its layout happens to fall.</summary>
public class HandoverTests
{
    private static readonly Guid BlockId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly TimeZoneInfo NewYork = TimeZones.Resolve("America/New_York");

    private static List<PlayableItem> Films(int count, int minutes = 50) => Enumerable.Range(1, count).Select(i => Items.Movie("Film " + i, minutes)).ToList();

    private static DateTime Local(int day, int hour, int minute = 0)
        => TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 10, day, hour, minute, 0, DateTimeKind.Unspecified), NewYork);

    /// <summary>Revision 2 of a pool station that takes over from <paramref name="handover"/>.</summary>
    private static ScheduleInput Revision2(List<PlayableItem> pool, PlayOrder order, int grid, Handover? handover, AirHistorySnapshot? history = null)
    {
        var station = Items.PoolStation(pool, order, grid, 2, true);
        return ScheduleInput.From(station, new CompiledStation { StationId = station.Id, Revision = 2, Pool = pool, History = history, Handover = handover });
    }

    private static void AssertContiguous(IReadOnlyList<ScheduleEntry> entries)
    {
        for (var i = 1; i < entries.Count; i++)
        {
            Assert.Equal(entries[i - 1].EndUtc, entries[i].StartUtc);
        }
    }

    [Fact]
    public void Plan_LetsTheProgramOnTheAirAndItsBreakFinish_ThenTakesOverOnTheGrid()
    {
        var rev1 = Items.PoolInput(Films(6), PlayOrder.Shuffle, grid: 30);
        var now = Items.Anchor.AddHours(2).AddMinutes(10); // 50-minute films in hour slots: a program runs 2:00-2:50, its break to 3:00
        var onAir = ScheduleResolver.ResolveInput(rev1, now)!.Entry;
        Assert.Equal(EntryKind.Program, onAir.Kind);

        var plan = Handovers.Plan(ScheduleResolver.EnumerateInput(rev1, now, now + Handovers.Lookahead), now, TimeSpan.FromMinutes(30), false);
        Assert.NotNull(plan);
        Assert.Equal(now, plan!.FromUtc);
        Assert.Equal(Items.Anchor.AddHours(3), plan.UntilUtc);
        Assert.Equal(onAir.ProgramId, plan.Entries[0].ProgramId);
        Assert.Equal(Items.Anchor.AddHours(2), plan.Entries[0].StartUtc);
        Assert.Equal(plan.UntilUtc, plan.Entries[^1].EndUtc);
        Assert.All(plan.Entries, e => Assert.True(e.StartUtc < plan.UntilUtc));
        AssertContiguous(plan.Entries);

        // In the break after the program, the break still plays out and the same boundary is kept.
        var inBreak = Items.Anchor.AddHours(2).AddMinutes(55);
        var plan2 = Handovers.Plan(ScheduleResolver.EnumerateInput(rev1, inBreak, inBreak + Handovers.Lookahead), inBreak, TimeSpan.FromMinutes(30), false);
        Assert.Equal(Items.Anchor.AddHours(3), plan2!.UntilUtc);
        Assert.NotEqual(EntryKind.Program, plan2.Entries[0].Kind);
    }

    [Fact]
    public void Plan_WithoutAGrid_TakesOverWhenTheBreakEnds_AndNothingOnTheAirIsNull()
    {
        var rev1 = Items.PoolInput(Films(6, 45), PlayOrder.Shuffle);
        var now = Items.Anchor.AddMinutes(100);
        var entries = ScheduleResolver.EnumerateInput(rev1, now, now + Handovers.Lookahead);
        var plan = Handovers.Plan(entries, now, TimeSpan.Zero, false)!;
        var nextProgram = entries.First(e => e.Kind == EntryKind.Program && e.StartUtc > now);
        Assert.Equal(nextProgram.StartUtc, plan.UntilUtc);

        Assert.Null(Handovers.Plan(Array.Empty<ScheduleEntry>(), now, TimeSpan.Zero, false));
        Assert.Null(Handovers.Plan(entries.Where(e => e.StartUtc > now).ToList(), now, TimeSpan.Zero, false));
    }

    [Fact]
    public void Plan_WithNothingToPlay_HandsOverAtOnce_OnTheGrid()
    {
        var empty = Items.PoolInput(new List<PlayableItem>(), PlayOrder.Shuffle, grid: 30);
        var now = Items.Anchor.AddMinutes(100);
        var plan = Handovers.Plan(ScheduleResolver.EnumerateInput(empty, now, now + Handovers.Lookahead), now, TimeSpan.FromMinutes(30), false)!;
        Assert.Equal(Items.Anchor.AddMinutes(120), plan.UntilUtc);
        Assert.All(plan.Entries, e => Assert.Equal(EntryKind.Slate, e.Kind));
        Assert.Equal(plan.UntilUtc, plan.Entries[^1].EndUtc);
    }

    [Fact]
    public void Plan_KeepsTheOldGridPhase_WaitingForTheNextSlotOnlyWithinGrace()
    {
        // A station whose timeline runs at :x4/:x9 on a 5-minute grid: clock-aligned boundaries would split its slots.
        var station = Guid.NewGuid();
        var t0 = Items.Anchor.AddMinutes(4);
        ScheduleEntry Make(EntryKind kind, int startMin, int endMin, bool inBreak = false)
            => new(station, 1, kind, kind == EntryKind.Program ? Items.Movie("Film " + startMin, endMin - startMin) : null, t0.AddMinutes(startMin), t0.AddMinutes(endMin), false, null, inBreak);

        // Program to :49, clip to :52, card to :59, next program at :59 (7 min after the break: within grace) -> :59.
        var shortCard = new List<ScheduleEntry> { Make(EntryKind.Program, 0, 45), Make(EntryKind.Bumper, 45, 48), Make(EntryKind.Slate, 48, 55), Make(EntryKind.Program, 55, 100) };
        var now = t0.AddMinutes(20);
        var plan = Handovers.Plan(shortCard, now, TimeSpan.FromMinutes(5), false)!;
        Assert.Equal(t0.AddMinutes(55), plan.UntilUtc);
        Assert.Equal(EntryKind.Slate, plan.Entries[^1].Kind);
        Assert.Equal(plan.UntilUtc, plan.Entries[^1].EndUtc);

        // The same, but the card runs 72 minutes: too long to wait for, so the break's end is moved up to the old phase (:x4 / :x9), not to the clock.
        var longCard = new List<ScheduleEntry> { Make(EntryKind.Program, 0, 45), Make(EntryKind.Bumper, 45, 48), Make(EntryKind.Slate, 48, 120), Make(EntryKind.Program, 120, 165) };
        var plan2 = Handovers.Plan(longCard, now, TimeSpan.FromMinutes(5), false)!;
        Assert.Equal(t0.AddMinutes(50), plan2.UntilUtc);
        Assert.Equal(0, (plan2.UntilUtc - t0).Ticks % TimeSpan.FromMinutes(5).Ticks);
        Assert.NotEqual(0, (plan2.UntilUtc - Items.Anchor).Ticks % TimeSpan.FromMinutes(5).Ticks);

        // Without a grid the break's end is the boundary whatever the card does.
        Assert.Equal(t0.AddMinutes(48), Handovers.Plan(longCard, now, TimeSpan.Zero, false)!.UntilUtc);
    }

    [Fact]
    public void Scheduler_ServesTheOldStretch_ThenTheNewTimelineFromTheBoundary()
    {
        var films = Films(6);
        var rev1 = Items.PoolInput(films, PlayOrder.Shuffle, grid: 30);
        var now = Items.Anchor.AddHours(5).AddMinutes(20);
        var old = ScheduleResolver.EnumerateInput(rev1, now, now + Handovers.Lookahead);
        var plan = Handovers.Plan(old, now, TimeSpan.FromMinutes(30), false)!;
        var rev2 = Revision2(films, PlayOrder.Shuffle, 30, plan);

        var entries = ScheduleResolver.EnumerateInput(rev2, now.AddHours(-3), now.AddHours(8));
        AssertContiguous(entries);
        var begin = plan.Entries[0].StartUtc;
        Assert.All(entries.Where(e => e.EndUtc <= begin), e => Assert.Equal(EntryKind.Slate, e.Kind));

        var stretch = entries.Where(e => e.StartUtc >= begin && e.StartUtc < plan.UntilUtc).ToList();
        Assert.Equal(plan.Entries.Select(e => (e.ProgramId, e.Kind, e.EndUtc)), stretch.Select(e => (e.ProgramId, e.Kind, e.EndUtc)));
        Assert.All(stretch, e => Assert.True(e.FromPrevious));
        Assert.Equal(1, stretch[0].Revision);

        var first = entries.First(e => e.StartUtc >= plan.UntilUtc);
        Assert.Equal(plan.UntilUtc, first.StartUtc);
        Assert.Equal(EntryKind.Program, first.Kind);
        Assert.Equal(2, first.Revision);
        Assert.False(first.FromPrevious);

        // Resolve agrees with Enumerate on both sides of the boundary and before the stretch.
        var rng = new Random(7);
        for (var i = 0; i < 60; i++)
        {
            var t = now.AddMinutes(rng.Next(-170, 470));
            var resolved = ScheduleResolver.ResolveInput(rev2, t)!.Entry;
            var listed = entries.Single(e => e.Contains(t));
            Assert.Equal((listed.ProgramId, listed.Kind, listed.EndUtc), (resolved.ProgramId, resolved.Kind, resolved.EndUtc));
        }
    }

    [Fact]
    public void Pool_ContinuesEachSeries_FromTheBoundary_WithoutRepeatingWhatJustAired()
    {
        var pool = Items.SeriesRun("Bluey", 10, 22);
        pool.AddRange(Items.SeriesRun("Frasier", 10, 22));
        pool.AddRange(Items.SeriesRun("Seinfeld", 10, 22));
        var rev1 = Items.PoolInput(pool, PlayOrder.ShuffleEpisodesInOrder, grid: 30);
        var taken = Items.Anchor.AddDays(2).AddHours(7).AddMinutes(10);

        var log = new AirLog { StationId = rev1.StationId };
        AirLogRecorder.Observe(log, ScheduleResolver.EnumerateInput(rev1, Items.Anchor, taken.AddTicks(1)), taken);
        var snapshot = AirLogRecorder.Snapshot(log, taken, new CompiledStation { Pool = pool })!;
        var plan = Handovers.Plan(ScheduleResolver.EnumerateInput(rev1, taken, taken + Handovers.Lookahead), taken, TimeSpan.FromMinutes(30), false)!;
        var rev2 = Revision2(pool, PlayOrder.ShuffleEpisodesInOrder, 30, plan, snapshot);

        var programs = ScheduleResolver.EnumerateInput(rev2, taken, taken.AddDays(3)).Where(e => e.Kind == EntryKind.Program).ToList();
        var lastOld = programs.Last(e => e.FromPrevious);
        var after = programs.Where(e => !e.FromPrevious).ToList();
        Assert.Equal(plan.UntilUtc, after[0].StartUtc);
        Assert.NotEqual(PoolOrdering.KeyOf(lastOld.Item!), PoolOrdering.KeyOf(after[0].Item!));

        foreach (var series in after.GroupBy(e => e.Item!.SeriesId!.Value))
        {
            var last = log.Keys["s:" + series.Key.ToString("N")];
            var episodes = series.Select(e => e.Item!.EpisodeNumber!.Value).ToList();
            Assert.Equal((last.Episode!.Value % 10) + 1, episodes[0]);
            for (var i = 1; i < episodes.Count; i++)
            {
                Assert.Equal((episodes[i - 1] % 10) + 1, episodes[i]);
            }
        }
    }

    [Fact]
    public void Weekly_JoinsARunningBlockAtTheBoundary_AndHoldsNothingBefore()
    {
        var seinfeld = Items.SeriesRun("Seinfeld", 10, 22);
        var cartoons = Items.SeriesRun("Cartoons", 30, 11);
        var block = new SlotBlock
        {
            Id = BlockId,
            Name = "Prime",
            Days = new List<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday },
            StartLocal = "20:00",
            DurationMinutes = 60,
            Order = PlayOrder.Sequential,
            Overflow = SlotOverflow.FillerRemainder,
            MaxStartDelayMinutes = 30
        };
        var boundary = Local(12, 20, 30);
        var handover = new Handover
        {
            FromUtc = boundary.AddMinutes(-7),
            UntilUtc = boundary,
            Entries = { new ScheduleEntry(Items.StationA, 1, EntryKind.Program, seinfeld[0], boundary.AddMinutes(-22), boundary, false, "Prime") }
        };
        var station = new Station
        {
            Id = Items.StationA,
            Name = "Slots",
            ChannelNumber = "101",
            AnchorUtc = Items.Anchor,
            Revision = 2,
            Mode = ScheduleMode.Slots,
            TimeZoneId = "America/New_York",
            Slots = new SlotsConfig { Blocks = new List<SlotBlock> { block }, Filler = new FillerConfig { Order = PlayOrder.Sequential } }
        };
        var compiled = new CompiledStation { StationId = station.Id, Revision = 2, Filler = cartoons, Blocks = { [BlockId] = seinfeld }, Handover = handover };
        var input = ScheduleInput.From(station, compiled);

        var entries = ScheduleResolver.EnumerateInput(input, boundary.AddHours(-1), boundary.AddHours(3));
        AssertContiguous(entries);
        Assert.All(entries.Where(e => e.EndUtc <= handover.Entries[0].StartUtc), e => Assert.Equal(EntryKind.Slate, e.Kind));

        var first = entries.First(e => e.StartUtc >= boundary);
        Assert.Equal(boundary, first.StartUtc);
        Assert.Equal("Prime", first.SourceLabel);
        Assert.Equal(EntryKind.Program, first.Kind);

        // Joined mid-occurrence, the block still ends at 21:00: no late-start extension, filler takes over on time.
        var blockEntries = entries.Where(e => e.SourceLabel == "Prime" && !e.FromPrevious).ToList();
        Assert.All(blockEntries, e => Assert.True(e.EndUtc <= Local(12, 21)));
        Assert.Equal("Filler", entries.First(e => e.StartUtc >= Local(12, 21) && e.Kind == EntryKind.Program).SourceLabel);

        // Weeks before the start hold nothing of this schedule.
        Assert.All(ScheduleResolver.EnumerateInput(input, boundary.AddDays(-10), boundary.AddDays(-9)), e => Assert.Equal(EntryKind.Slate, e.Kind));
        var resolved = ScheduleResolver.ResolveInput(input, boundary.AddMinutes(10))!.Entry;
        Assert.Equal(first.ProgramId, resolved.ProgramId);
    }
}
