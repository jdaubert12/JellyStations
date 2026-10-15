using Jellyfin.Plugin.TvStation.Models;
using Jellyfin.Plugin.TvStation.Scheduling;
using Jellyfin.Plugin.TvStation.Tests.Fixtures;
using Xunit;

namespace Jellyfin.Plugin.TvStation.Tests.Scheduling;

/// <summary>Pool mode with time blocks: the pool plays between blocks with its own ordering rules.</summary>
public class PoolWithBlocksTests
{
    private static readonly Guid MovieBlockId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly TimeZoneInfo NewYork = WeeklySlotScheduler.ResolveTimeZone("America/New_York");
    private static readonly List<DayOfWeek> EveryDay = Enum.GetValues<DayOfWeek>().ToList();

    private static SlotBlock MovieNight(string start = "18:00", int minutes = 180, SlotOverflow overflow = SlotOverflow.FillerRemainder)
    {
        return new SlotBlock
        {
            Id = MovieBlockId,
            Name = "Movie Night",
            Days = EveryDay,
            StartLocal = start,
            DurationMinutes = minutes,
            Order = PlayOrder.Shuffle,
            Overflow = overflow
        };
    }

    private static List<PlayableItem> KidsPool(int episodes = 20)
    {
        var pool = Items.SeriesRun("Bluey", episodes, 22);
        pool.AddRange(Items.SeriesRun("Avatar", episodes, 22));
        pool.AddRange(Items.SeriesRun("Thornberrys", episodes, 22));
        return pool;
    }

    private static Dictionary<Guid, List<PlayableItem>> Movies(int count = 8, int minutes = 100)
    {
        return new Dictionary<Guid, List<PlayableItem>>
        {
            [MovieBlockId] = Enumerable.Range(1, count).Select(i => Items.Movie("Movie " + i, minutes)).ToList()
        };
    }

    private static DateTime LocalToUtc(int y, int m, int d, int h, int min)
    {
        return TimeZoneInfo.ConvertTimeToUtc(new DateTime(y, m, d, h, min, 0, DateTimeKind.Unspecified), NewYork);
    }

    private static List<ScheduleEntry> PoolPrograms(ScheduleInput input, DateTime fromUtc, DateTime toUtc)
    {
        return ScheduleResolver.EnumerateInput(input, fromUtc, toUtc)
            .Where(e => e.Kind == EntryKind.Program && e.SourceLabel == "Pool")
            .ToList();
    }

    [Fact]
    public void Block_TakesOverThePool_AtItsLocalTime()
    {
        var input = Items.PoolWithBlocksInput(KidsPool(), new List<SlotBlock> { MovieNight() }, Movies());

        var morning = ScheduleResolver.ResolveInput(input, LocalToUtc(2026, 10, 13, 10, 0))!;
        Assert.Equal("Pool", morning.Entry.SourceLabel);
        Assert.True(morning.Entry.Item!.IsSeries);

        var evening = ScheduleResolver.ResolveInput(input, LocalToUtc(2026, 10, 13, 18, 0))!;
        Assert.Equal("Movie Night", evening.Entry.SourceLabel);
        Assert.True(evening.Entry.Item!.IsMovie);
        Assert.Equal(TimeSpan.Zero, evening.Offset);

        var late = ScheduleResolver.ResolveInput(input, LocalToUtc(2026, 10, 13, 21, 30))!;
        Assert.Equal("Pool", late.Entry.SourceLabel);
        Assert.True(late.Entry.Item!.IsSeries);
    }

    [Fact]
    public void Block_HandsItsRemainderBackToThePool()
    {
        // 100-minute movies in a 3-hour block: one movie fits, the rest of the block is pool content.
        var input = Items.PoolWithBlocksInput(KidsPool(), new List<SlotBlock> { MovieNight() }, Movies());
        var start = LocalToUtc(2026, 10, 13, 18, 0);
        var block = ScheduleResolver.EnumerateInput(input, start, start.AddHours(3)).Where(e => e.Kind == EntryKind.Program).ToList();

        Assert.Equal("Movie Night", block[0].SourceLabel);
        Assert.Equal(start, block[0].StartUtc);
        Assert.Equal(start.AddMinutes(100), block[0].EndUtc);
        Assert.All(block.Skip(1), e => Assert.Equal("Pool", e.SourceLabel));
        Assert.Equal(start.AddMinutes(100), block[1].StartUtc);
        Assert.True(block.Count > 2);
    }

    [Fact]
    public void TruncateLast_PlaysASecondMovieCutAtTheBlockEnd()
    {
        var input = Items.PoolWithBlocksInput(KidsPool(), new List<SlotBlock> { MovieNight(overflow: SlotOverflow.TruncateLast) }, Movies());
        var start = LocalToUtc(2026, 10, 13, 18, 0);
        var block = ScheduleResolver.EnumerateInput(input, start, start.AddHours(3)).Where(e => e.Kind == EntryKind.Program).ToList();

        Assert.Equal(2, block.Count);
        Assert.All(block, e => Assert.Equal("Movie Night", e.SourceLabel));
        Assert.True(block[1].IsTruncated);
        Assert.Equal(start.AddHours(3), block[1].EndUtc);
    }

    [Fact]
    public void SequentialPool_ResumesWhereItLeftOff_AfterABlock()
    {
        // Anchor is Monday 00:00 local. 18 hours of 22-minute episodes = 49 full episodes (3 passes minus 11),
        // so after the block (all movies, the last one cut) the pool resumes with episode 10 of its third pass.
        var input = Items.PoolWithBlocksInput(Items.SeriesRun("Bluey", 20, 22), new List<SlotBlock> { MovieNight(overflow: SlotOverflow.TruncateLast) }, Movies(), PlayOrder.Sequential, avoid: false);

        var first = ScheduleResolver.ResolveInput(input, Items.Anchor)!;
        Assert.Equal(1, first.Entry.Item!.EpisodeNumber);

        var afterBlock = ScheduleResolver.ResolveInput(input, LocalToUtc(2026, 10, 12, 21, 0))!;
        Assert.Equal("Pool", afterBlock.Entry.SourceLabel);
        Assert.Equal(10, afterBlock.Entry.Item!.EpisodeNumber);
        Assert.Equal(TimeSpan.Zero, afterBlock.Offset);
    }

    [Fact]
    public void ShuffledPool_KeepsTheSameSeriesApart_BetweenBlocks()
    {
        var input = Items.PoolWithBlocksInput(KidsPool(), new List<SlotBlock> { MovieNight() }, Movies());
        var programs = PoolPrograms(input, LocalToUtc(2026, 10, 13, 0, 0), LocalToUtc(2026, 10, 13, 18, 0));

        Assert.True(programs.Count > 40);
        for (var i = 1; i < programs.Count; i++)
        {
            Assert.NotEqual(programs[i - 1].Item!.SeriesId, programs[i].Item!.SeriesId);
        }

        Assert.Equal(3, programs.Select(p => p.Item!.SeriesId).Distinct().Count());
    }

    [Fact]
    public void Marathons_PlayBetweenBlocks()
    {
        var input = Items.PoolWithBlocksInput(KidsPool(), new List<SlotBlock> { MovieNight() }, Movies(), marathonMin: 2, marathonMax: 2);
        var programs = PoolPrograms(input, LocalToUtc(2026, 10, 13, 0, 0), LocalToUtc(2026, 10, 13, 18, 0));

        // Runs of one series; the first and last may be cut by the window edges.
        var runs = new List<int>();
        for (var i = 0; i < programs.Count; i++)
        {
            if (i > 0 && programs[i].Item!.SeriesId == programs[i - 1].Item!.SeriesId)
            {
                runs[^1]++;
            }
            else
            {
                runs.Add(1);
            }
        }

        Assert.True(runs.Count > 10);
        Assert.All(runs.Skip(1).Take(runs.Count - 2), len => Assert.Equal(2, len));
    }

    [Fact]
    public void EpisodesInOrder_HoldAcrossBlocks()
    {
        var input = Items.PoolWithBlocksInput(KidsPool(), new List<SlotBlock> { MovieNight() }, Movies(), PlayOrder.ShuffleEpisodesInOrder);
        var programs = PoolPrograms(input, Items.Anchor, Items.Anchor.AddDays(7));

        foreach (var series in programs.GroupBy(p => p.Item!.SeriesId))
        {
            var episodes = series.Select(p => p.Item!.EpisodeNumber!.Value).ToList();
            Assert.True(episodes.Count > 20);
            Assert.Equal(1, episodes[0]);
            for (var i = 1; i < episodes.Count; i++)
            {
                Assert.Equal((episodes[i - 1] % 20) + 1, episodes[i]);
            }
        }
    }

    [Fact]
    public void DaytimeRules_KeepRestrictedItemsOffTheAir_UntilAllowed()
    {
        var pool = KidsPool(10);
        pool.ForEach(e => e.OfficialRating = "PG");
        var lateMovies = Enumerable.Range(1, 4).Select(i => Items.Movie("Slasher " + i, 100)).ToList();
        lateMovies.ForEach(m => m.OfficialRating = "R");
        pool.AddRange(lateMovies);

        var rules = new List<DaypartRule> { new() { StartLocal = "06:00", EndLocal = "20:00", MaxRating = "PG" } };
        var input = Items.PoolWithBlocksInput(pool, new List<SlotBlock> { MovieNight() }, Movies(), rules: rules);
        var rule = input.DaypartRules.Single();

        var entries = ScheduleResolver.EnumerateInput(input, Items.Anchor, Items.Anchor.AddDays(28));
        var restricted = entries.Where(e => e.Kind == EntryKind.Program && e.SourceLabel == "Pool" && e.Item!.OfficialRating == "R").ToList();
        Assert.NotEmpty(restricted);
        foreach (var e in restricted)
        {
            var localStart = TimeZoneInfo.ConvertTimeFromUtc(e.StartUtc, NewYork);
            var localEnd = TimeZoneInfo.ConvertTimeFromUtc(e.EndUtc, NewYork);
            Assert.False(DaypartGate.Overlaps(rule, localStart, localEnd), $"R movie aired at {localStart:t}");
        }

        // The block is not gated, and PG content is always available, so there is never dead air for the rule.
        Assert.DoesNotContain(entries, e => e.Kind == EntryKind.Slate && e.SourceLabel == "Not allowed at this time");
        Assert.Contains(entries, e => e.Kind == EntryKind.Program && e.SourceLabel == "Movie Night");
    }

    [Fact]
    public void PoolWithoutBlocks_IsUnchanged()
    {
        var pool = KidsPool();
        var withEmptyBlocks = Items.PoolWithBlocksInput(pool, new List<SlotBlock>(), new Dictionary<Guid, List<PlayableItem>>());
        var plain = Items.PoolInput(pool);
        Assert.False(withEmptyBlocks.HasBlocks);

        var a = ScheduleResolver.EnumerateInput(withEmptyBlocks, Items.Anchor, Items.Anchor.AddDays(1)).Select(e => (e.StartUtc, e.Item?.ItemId)).ToList();
        var b = ScheduleResolver.EnumerateInput(plain, Items.Anchor, Items.Anchor.AddDays(1)).Select(e => (e.StartUtc, e.Item?.ItemId)).ToList();
        Assert.Equal(b, a);
        Assert.All(ScheduleResolver.EnumerateInput(plain, Items.Anchor, Items.Anchor.AddHours(2)), e => Assert.Null(e.SourceLabel));
    }
}
