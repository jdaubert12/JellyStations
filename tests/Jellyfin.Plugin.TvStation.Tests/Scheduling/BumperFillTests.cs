using Jellyfin.Plugin.TvStation.Models;
using Jellyfin.Plugin.TvStation.Scheduling;
using Jellyfin.Plugin.TvStation.Tests.Fixtures;
using Xunit;

namespace Jellyfin.Plugin.TvStation.Tests.Scheduling;

public class BumperFillTests
{
    private static List<PlayableItem> Clips()
    {
        var ident = Items.Movie("Ident 30s", 1);
        ident.RuntimeTicks = TimeSpan.FromSeconds(30).Ticks;
        return new List<PlayableItem> { ident, Items.Movie("Trailer 2m", 2), Items.Movie("Promo 1m", 1), Items.Movie("Long 4m", 4) };
    }

    [Fact]
    public void Plan_PlacesOnlyClipsThatFit_EachAtMostOnce_ThenCardRemainder()
    {
        var clips = Clips();
        var gap = TimeSpan.FromMinutes(3).Ticks;
        var pieces = BumperFill.Plan(gap, clips, 42UL);

        Assert.Equal(0, pieces[0].StartOffset);
        for (var i = 1; i < pieces.Count; i++)
        {
            Assert.Equal(pieces[i - 1].EndOffset, pieces[i].StartOffset);
        }

        Assert.Equal(gap, pieces[^1].EndOffset);
        var used = pieces.Where(p => p.Clip is not null).Select(p => p.Clip!.Title).ToList();
        Assert.Equal(used.Distinct().Count(), used.Count);
        Assert.DoesNotContain("Long 4m", used);
        Assert.True(pieces.Sum(p => p.Clip?.RuntimeTicks ?? 0) <= gap);
    }

    [Fact]
    public void Plan_IsDeterministic_AndEmptyClipsGiveOneCard()
    {
        var clips = Clips();
        var a = BumperFill.Plan(TimeSpan.FromMinutes(5).Ticks, clips, 7UL);
        var b = BumperFill.Plan(TimeSpan.FromMinutes(5).Ticks, clips, 7UL);
        Assert.Equal(a.Select(p => p.Clip?.ItemId), b.Select(p => p.Clip?.ItemId));

        var none = BumperFill.Plan(TimeSpan.FromMinutes(5).Ticks, Array.Empty<PlayableItem>(), 7UL);
        Assert.Single(none);
        Assert.Null(none[0].Clip);
    }

    private static ScheduleInput PoolWithBumpers(int grid, int breakMinutes, bool enabled = true)
    {
        var pool = new List<PlayableItem> { Items.Movie("A", 47), Items.Movie("B", 61), Items.Movie("C", 22) };
        var station = Items.PoolStation(pool, PlayOrder.Sequential, grid: grid);
        station.Bumpers = new BumperConfig { Enabled = enabled, BreakMinutes = breakMinutes, MaxClipMinutes = 5 };
        return ScheduleInput.From(station, new CompiledStation { StationId = station.Id, Revision = 1, Pool = pool, Bumpers = Clips() });
    }

    [Fact]
    public void Pool_GapsAreFilledWithClips_ContiguousAndResolvable()
    {
        var input = PoolWithBumpers(grid: 15, breakMinutes: 0);
        var entries = ScheduleResolver.EnumerateInput(input, Items.Anchor, Items.Anchor.AddHours(6));
        for (var i = 1; i < entries.Count; i++)
        {
            Assert.Equal(entries[i - 1].EndUtc, entries[i].StartUtc);
        }

        Assert.Contains(entries, e => e.Kind == EntryKind.Bumper);
        Assert.Contains(entries, e => e.Kind == EntryKind.Program && e.Item!.Title == "A");

        // Every bumper directly follows a program or another bumper, never a program it would overlap.
        for (var i = 1; i < entries.Count; i++)
        {
            if (entries[i].Kind == EntryKind.Bumper)
            {
                Assert.NotEqual(EntryKind.Slate, entries[i - 1].Kind);
            }
        }

        var rng = new Random(5);
        for (var n = 0; n < 200; n++)
        {
            var t = Items.Anchor.AddSeconds(rng.NextDouble() * TimeSpan.FromHours(6).TotalSeconds);
            Assert.Equal(entries.Single(e => e.Contains(t)), ScheduleResolver.ResolveInput(input, t)!.Entry);
        }
    }

    [Fact]
    public void Pool_BreakMinutes_ExtendsEveryProgramSlot()
    {
        var without = PoolWithBumpers(grid: 0, breakMinutes: 0);
        var with = PoolWithBumpers(grid: 0, breakMinutes: 2);
        var a = ScheduleResolver.EnumerateInput(without, Items.Anchor, Items.Anchor.AddMinutes(130));
        var b = ScheduleResolver.EnumerateInput(with, Items.Anchor, Items.Anchor.AddMinutes(136));

        Assert.Equal(EntryKind.Program, a[1].Kind); // no gap at all without padding or break
        Assert.Equal(Items.Anchor.AddMinutes(47), b[0].EndUtc);
        Assert.NotEqual(EntryKind.Program, b[1].Kind); // the 2-minute break follows, holding a clip
        Assert.Equal(EntryKind.Bumper, b[1].Kind);
        Assert.Equal(Items.Anchor.AddMinutes(49), b.First(e => e.Kind == EntryKind.Program && e.Item!.Title == "B").StartUtc);
    }

    [Fact]
    public void Disabled_ProducesNoBumpers_AndSameTimelineAsBefore()
    {
        var off = PoolWithBumpers(grid: 15, breakMinutes: 3, enabled: false);
        var entries = ScheduleResolver.EnumerateInput(off, Items.Anchor, Items.Anchor.AddHours(3));
        Assert.DoesNotContain(entries, e => e.Kind == EntryKind.Bumper);
        Assert.Equal(Items.Anchor.AddMinutes(60), entries.First(e => e.Kind == EntryKind.Program && e.Item!.Title == "B").StartUtc);
    }

    [Fact]
    public void Slots_SlatesBecomeClipsPlusCard()
    {
        var blockId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var block = new SlotBlock { Id = blockId, Name = "Prime", Days = new List<DayOfWeek> { DayOfWeek.Monday }, StartLocal = "20:00", DurationMinutes = 60, Order = PlayOrder.Sequential };
        var station = new Station
        {
            Id = Items.StationA, Name = "S", ChannelNumber = "1", AnchorUtc = Items.Anchor, Revision = 1, PaddingGridMinutes = 30, Mode = ScheduleMode.Slots, TimeZoneId = "America/New_York",
            Slots = new SlotsConfig { Blocks = new List<SlotBlock> { block } },
            Bumpers = new BumperConfig { Enabled = true, MaxClipMinutes = 5 }
        };
        var compiled = new CompiledStation { StationId = station.Id, Revision = 1, Blocks = { [blockId] = Items.SeriesRun("Show", 4, 22) }, Filler = Items.SeriesRun("Fill", 10, 11), Bumpers = Clips() };
        var input = ScheduleInput.From(station, compiled);

        var entries = ScheduleResolver.EnumerateInput(input, Items.Anchor, Items.Anchor.AddDays(1));
        Assert.Contains(entries, e => e.Kind == EntryKind.Bumper);
        for (var i = 1; i < entries.Count; i++)
        {
            Assert.Equal(entries[i - 1].EndUtc, entries[i].StartUtc);
        }

        // A 22-minute program on a 30-minute grid leaves 8 minutes: clips, then card for the rest.
        var show = entries.First(e => e.Kind == EntryKind.Program && e.Item!.Title == "Show");
        var next = entries[entries.ToList().IndexOf(show) + 1];
        Assert.Equal(EntryKind.Bumper, next.Kind);
    }

    private static List<PlayableItem> Idents()
    {
        var a = Items.Movie("Ident A", 1);
        a.RuntimeTicks = TimeSpan.FromSeconds(10).Ticks;
        var b = Items.Movie("Ident B", 1);
        b.RuntimeTicks = TimeSpan.FromSeconds(15).Ticks;
        return new List<PlayableItem> { a, b };
    }

    private static List<PlayableItem> Outros()
    {
        var a = Items.Movie("Outro A", 1);
        a.RuntimeTicks = TimeSpan.FromSeconds(20).Ticks;
        return new List<PlayableItem> { a };
    }

    [Fact]
    public void Plan_PostRollOpensTheGap_PreRollClosesIt_FillInBetween()
    {
        var clips = new BumperFill.Clips(Clips(), Idents(), Outros());
        var gap = TimeSpan.FromMinutes(3).Ticks;
        var pieces = BumperFill.Plan(gap, clips, 42UL);

        Assert.Equal(BumperRole.PostRoll, pieces[0].Role);
        Assert.Equal("Outro A", pieces[0].Clip!.Title);
        Assert.Equal(0, pieces[0].StartOffset);

        Assert.Equal(BumperRole.PreRoll, pieces[^1].Role);
        Assert.StartsWith("Ident", pieces[^1].Clip!.Title);
        Assert.Equal(gap, pieces[^1].EndOffset);

        for (var i = 1; i < pieces.Count; i++)
        {
            Assert.Equal(pieces[i - 1].EndOffset, pieces[i].StartOffset);
        }

        Assert.Contains(pieces, p => p.Role == BumperRole.Fill && p.Clip is not null);
        Assert.Single(pieces, p => p.Role == BumperRole.PreRoll);
        Assert.Single(pieces, p => p.Role == BumperRole.PostRoll);
    }

    [Fact]
    public void Plan_RollsAreSkippedWhenTheyDoNotFit_OrNoProgramIsAdjacent()
    {
        var clips = new BumperFill.Clips(Array.Empty<PlayableItem>(), Idents(), Outros());

        // 12 seconds: the 20-second post-roll cannot fit, the 10-second ident can.
        var tight = BumperFill.Plan(TimeSpan.FromSeconds(12).Ticks, clips, 1UL);
        Assert.DoesNotContain(tight, p => p.Role == BumperRole.PostRoll);
        Assert.Equal("Ident A", tight[^1].Clip!.Title);
        Assert.Equal(TimeSpan.FromSeconds(12).Ticks, tight[^1].EndOffset);

        var noProgramAfter = BumperFill.Plan(TimeSpan.FromMinutes(1).Ticks, clips, 1UL, programBefore: true, programAfter: false);
        Assert.DoesNotContain(noProgramAfter, p => p.Role == BumperRole.PreRoll);
        Assert.Contains(noProgramAfter, p => p.Role == BumperRole.PostRoll);

        var noProgramBefore = BumperFill.Plan(TimeSpan.FromMinutes(1).Ticks, clips, 1UL, programBefore: false, programAfter: true);
        Assert.DoesNotContain(noProgramBefore, p => p.Role == BumperRole.PostRoll);
        Assert.Contains(noProgramBefore, p => p.Role == BumperRole.PreRoll);

        // A clip used as pre-roll is not placed again by the gap fill in the same gap.
        var shared = new BumperFill.Clips(Idents(), Idents(), Array.Empty<PlayableItem>());
        var pieces = BumperFill.Plan(TimeSpan.FromMinutes(1).Ticks, shared, 3UL);
        var titles = pieces.Where(p => p.Clip is not null).Select(p => p.Clip!.Title).ToList();
        Assert.Equal(titles.Distinct().Count(), titles.Count);
    }

    private static ScheduleInput PoolWithRolls(int grid, int breakMinutes, bool pre, bool post, bool ownSources = false)
    {
        var pool = new List<PlayableItem> { Items.Movie("A", 47), Items.Movie("B", 61), Items.Movie("C", 22) };
        var station = Items.PoolStation(pool, PlayOrder.Sequential, grid: grid);
        station.Bumpers = new BumperConfig
        {
            Enabled = true,
            BreakMinutes = breakMinutes,
            MaxClipMinutes = 5,
            PreRoll = new BumperRollConfig { Enabled = pre },
            PostRoll = new BumperRollConfig { Enabled = post }
        };
        var compiled = new CompiledStation
        {
            StationId = station.Id,
            Revision = 1,
            Pool = pool,
            Bumpers = Clips(),
            PreRoll = pre ? (ownSources ? Idents() : Clips()) : new List<PlayableItem>(),
            PostRoll = post ? (ownSources ? Outros() : Clips()) : new List<PlayableItem>()
        };
        return ScheduleInput.From(station, compiled);
    }

    [Fact]
    public void Pool_PreRollEndsEveryBreak_PostRollOpensIt_ProgramsStayOnGrid()
    {
        var input = PoolWithRolls(grid: 30, breakMinutes: 0, pre: true, post: true, ownSources: true);
        var entries = ScheduleResolver.EnumerateInput(input, Items.Anchor, Items.Anchor.AddHours(6)).ToList();
        for (var i = 1; i < entries.Count; i++)
        {
            Assert.Equal(entries[i - 1].EndUtc, entries[i].StartUtc);
        }

        var programs = entries.Where(e => e.Kind == EntryKind.Program).ToList();
        Assert.True(programs.Count >= 4);
        foreach (var p in programs)
        {
            Assert.Equal(0, p.StartUtc.Ticks % TimeSpan.FromMinutes(30).Ticks);
            var idx = entries.IndexOf(p);
            if (idx > 0)
            {
                Assert.Equal(BumperFill.PreRollLabel, entries[idx - 1].SourceLabel);
                Assert.StartsWith("Ident", entries[idx - 1].Item!.Title);
            }

            if (idx + 1 < entries.Count)
            {
                Assert.Equal(BumperFill.PostRollLabel, entries[idx + 1].SourceLabel);
                Assert.Equal("Outro A", entries[idx + 1].Item!.Title);
            }
        }

        // Resolve agrees with Enumerate at random instants, including inside the rolls.
        var rng = new Random(9);
        for (var n = 0; n < 200; n++)
        {
            var t = Items.Anchor.AddSeconds(rng.NextDouble() * TimeSpan.FromHours(6).TotalSeconds);
            Assert.Equal(entries.Single(e => e.Contains(t)), ScheduleResolver.ResolveInput(input, t)!.Entry);
        }
    }

    [Fact]
    public void Pool_RollsOff_KeepsTheExistingFillUnchanged()
    {
        var plain = PoolWithBumpers(grid: 15, breakMinutes: 0);
        var rollsOff = PoolWithRolls(grid: 15, breakMinutes: 0, pre: false, post: false);
        var a = ScheduleResolver.EnumerateInput(plain, Items.Anchor, Items.Anchor.AddHours(6));
        var b = ScheduleResolver.EnumerateInput(rollsOff, Items.Anchor, Items.Anchor.AddHours(6));
        Assert.Equal(a.Select(e => (e.Kind, e.Item?.ItemId, e.StartUtc, e.EndUtc)), b.Select(e => (e.Kind, e.Item?.ItemId, e.StartUtc, e.EndUtc)));
        Assert.DoesNotContain(b, e => e.SourceLabel is BumperFill.PreRollLabel or BumperFill.PostRollLabel);
    }

    [Fact]
    public void Slots_PreRollLeadsIntoPrograms_PostRollFollowsThem()
    {
        var blockId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var block = new SlotBlock { Id = blockId, Name = "Prime", Days = new List<DayOfWeek> { DayOfWeek.Monday }, StartLocal = "20:00", DurationMinutes = 60, Order = PlayOrder.Sequential };
        var station = new Station
        {
            Id = Items.StationA, Name = "S", ChannelNumber = "1", AnchorUtc = Items.Anchor, Revision = 1, PaddingGridMinutes = 30, Mode = ScheduleMode.Slots, TimeZoneId = "America/New_York",
            Slots = new SlotsConfig { Blocks = new List<SlotBlock> { block } },
            Bumpers = new BumperConfig { Enabled = true, MaxClipMinutes = 5, PreRoll = new BumperRollConfig { Enabled = true }, PostRoll = new BumperRollConfig { Enabled = true } }
        };
        var compiled = new CompiledStation
        {
            StationId = station.Id,
            Revision = 1,
            Blocks = { [blockId] = Items.SeriesRun("Show", 4, 22) },
            Filler = Items.SeriesRun("Fill", 10, 11),
            Bumpers = Clips(),
            PreRoll = Idents(),
            PostRoll = Outros()
        };
        var input = ScheduleInput.From(station, compiled);

        var list = ScheduleResolver.EnumerateInput(input, Items.Anchor, Items.Anchor.AddDays(1)).ToList();
        for (var i = 1; i < list.Count; i++)
        {
            Assert.Equal(list[i - 1].EndUtc, list[i].StartUtc);
        }

        var shows = list.Where(e => e.Kind == EntryKind.Program && e.Item!.Title == "Show").ToList();
        Assert.True(shows.Count >= 2);
        foreach (var show in shows)
        {
            var idx = list.IndexOf(show);
            Assert.Equal(BumperFill.PreRollLabel, list[idx - 1].SourceLabel);
            Assert.Equal(BumperFill.PostRollLabel, list[idx + 1].SourceLabel);
        }

        // Every pre-roll is immediately followed by a program; every post-roll immediately follows one
        // (the window may end on a pre-roll whose program starts after the cutoff).
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].SourceLabel == BumperFill.PreRollLabel && i + 1 < list.Count)
            {
                Assert.Equal(EntryKind.Program, list[i + 1].Kind);
            }

            if (list[i].SourceLabel == BumperFill.PostRollLabel && i > 0)
            {
                Assert.Equal(EntryKind.Program, list[i - 1].Kind);
            }
        }
    }
}
