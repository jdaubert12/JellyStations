using Jellyfin.Plugin.TvStation.Models;
using Jellyfin.Plugin.TvStation.Scheduling;
using Jellyfin.Plugin.TvStation.Tests.Fixtures;
using Xunit;

namespace Jellyfin.Plugin.TvStation.Tests.Scheduling;

public class CyclicSchedulerTests
{
    private static List<PlayableItem> MixedPool()
    {
        var pool = Items.SeriesRun("Seinfeld", 6, 22);
        pool.AddRange(Items.SeriesRun("Frasier", 4, 23));
        pool.Add(Items.Movie("Heat", 170));
        pool.Add(Items.Movie("Alien", 117));
        return pool;
    }

    [Fact]
    public void Lineup_StartsAtAnchorWithFirstItemAndLoops()
    {
        var lineup = new List<PlayableItem> { Items.Movie("A", 30), Items.Movie("B", 45), Items.Movie("A", 30) };
        var input = Items.LineupInput(lineup);

        var r = ScheduleResolver.ResolveInput(input, Items.Anchor)!;
        Assert.Equal("A", r.Entry.Item!.Title);
        Assert.Equal(TimeSpan.Zero, r.Offset);

        var later = ScheduleResolver.ResolveInput(input, Items.Anchor.AddMinutes(105 + 40))!;
        Assert.Equal("B", later.Entry.Item!.Title);
        Assert.Equal(TimeSpan.FromMinutes(10), later.Offset);
    }

    [Fact]
    public void Enumerate_IsContiguousAndCoversWindow()
    {
        var input = Items.PoolInput(MixedPool());
        var from = Items.Anchor.AddHours(5);
        var to = from.AddHours(72);
        var entries = ScheduleResolver.EnumerateInput(input, from, to);

        Assert.NotEmpty(entries);
        Assert.True(entries[0].StartUtc <= from);
        Assert.True(entries[^1].EndUtc >= to);
        for (var i = 1; i < entries.Count; i++)
        {
            Assert.Equal(entries[i - 1].EndUtc, entries[i].StartUtc);
        }
    }

    [Fact]
    public void Resolve_MatchesEnumerate_AtRandomInstants()
    {
        var input = Items.PoolInput(MixedPool());
        var from = Items.Anchor.AddDays(-2);
        var to = Items.Anchor.AddDays(30);
        var entries = ScheduleResolver.EnumerateInput(input, from, to);
        var rng = new Random(7);
        for (var n = 0; n < 500; n++)
        {
            var t = from.AddSeconds(rng.NextDouble() * (to - from).TotalSeconds);
            var r = ScheduleResolver.ResolveInput(input, t)!;
            var expected = entries.Single(e => e.Contains(t));
            Assert.Equal(expected, r.Entry);
            Assert.Equal(t - expected.StartUtc, r.Offset);
        }
    }

    [Fact]
    public void ShuffleEpisodesInOrder_InterleavesSeries_ButPlaysEachSeriesInOrder()
    {
        var pool = MixedPool();
        var cycle = TimeSpan.FromTicks(pool.Sum(i => i.RuntimeTicks));
        var input = Items.PoolInput(pool, PlayOrder.ShuffleEpisodesInOrder);
        var programs = ScheduleResolver.EnumerateInput(input, Items.Anchor, Items.Anchor + (cycle * 3))
            .Where(e => e.Kind == EntryKind.Program && e.StartUtc >= Items.Anchor && e.StartUtc < Items.Anchor + (cycle * 3))
            .ToList();
        Assert.Equal(pool.Count * 3, programs.Count);

        for (var k = 0; k < 3; k++)
        {
            var start = Items.Anchor + (cycle * k);
            var inCycle = programs.Where(e => e.StartUtc >= start && e.StartUtc < start + cycle).ToList();
            Assert.Equal(pool.Count, inCycle.Count);
            foreach (var series in inCycle.Where(e => e.Item!.SeriesId.HasValue).GroupBy(e => e.Item!.SeriesId))
            {
                var episodes = series.Select(e => e.Item!.EpisodeNumber!.Value).ToList();
                Assert.Equal(episodes.OrderBy(n => n), episodes);
            }
        }

        // It is still a shuffle: the first cycle is not simply the compiled (sequential) order.
        var sequential = ScheduleResolver.EnumerateInput(Items.PoolInput(pool, PlayOrder.Sequential), Items.Anchor, Items.Anchor + cycle)
            .Where(e => e.Kind == EntryKind.Program).Select(e => e.Item!.ItemId).ToList();
        var first = programs.Take(pool.Count).Select(e => e.Item!.ItemId).ToList();
        Assert.NotEqual(sequential, first);
        Assert.Equal(sequential.OrderBy(g => g), first.OrderBy(g => g));
    }

    [Fact]
    public void Shuffle_IsDeterministicAcrossInstances_AndChangesWithRevision()
    {
        var pool = MixedPool();
        var a = ScheduleResolver.EnumerateInput(Items.PoolInput(pool, revision: 1), Items.Anchor, Items.Anchor.AddDays(3));
        var b = ScheduleResolver.EnumerateInput(Items.PoolInput(pool, revision: 1), Items.Anchor, Items.Anchor.AddDays(3));
        var c = ScheduleResolver.EnumerateInput(Items.PoolInput(pool, revision: 2), Items.Anchor, Items.Anchor.AddDays(3));

        Assert.Equal(a.Select(e => e.Item?.ItemId), b.Select(e => e.Item?.ItemId));
        Assert.NotEqual(a.Select(e => e.Item?.ItemId), c.Select(e => e.Item?.ItemId));
    }

    [Fact]
    public void Shuffle_DifferentCyclesHaveDifferentOrder_ButSameItemSet()
    {
        var pool = MixedPool();
        var input = Items.PoolInput(pool);
        var cycle = TimeSpan.FromTicks(pool.Sum(p => p.RuntimeTicks));
        var c0 = ScheduleResolver.EnumerateInput(input, Items.Anchor, Items.Anchor + cycle).Select(e => e.Item!.ItemId).ToList();
        var c1 = ScheduleResolver.EnumerateInput(input, Items.Anchor + cycle, Items.Anchor + cycle + cycle).Select(e => e.Item!.ItemId).ToList();
        Assert.Equal(pool.Count, c0.Count);
        Assert.Equal(pool.Count, c1.Count);
        Assert.Equal(c0.OrderBy(x => x), c1.OrderBy(x => x));
        Assert.NotEqual(c0, c1);
    }

    [Fact]
    public void AvoidSameSeriesBackToBack_HoldsWithinAndAcrossCycles()
    {
        var pool = Items.SeriesRun("A", 5, 20);
        pool.AddRange(Items.SeriesRun("B", 5, 20));
        pool.AddRange(Items.SeriesRun("C", 5, 20));
        var input = Items.PoolInput(pool, avoid: true);
        var entries = ScheduleResolver.EnumerateInput(input, Items.Anchor, Items.Anchor.AddDays(10));
        for (var i = 1; i < entries.Count; i++)
        {
            Assert.NotEqual(entries[i - 1].Item!.SeriesId, entries[i].Item!.SeriesId);
        }
    }

    [Fact]
    public void PaddingGrid_AlignsProgramStartsAndInsertsSlates()
    {
        var pool = new List<PlayableItem> { Items.Movie("A", 22), Items.Movie("B", 44), Items.Movie("C", 90) };
        var input = Items.PoolInput(pool, order: PlayOrder.Sequential, grid: 15);
        var entries = ScheduleResolver.EnumerateInput(input, Items.Anchor, Items.Anchor.AddHours(12));

        foreach (var p in entries.Where(e => e.Kind == EntryKind.Program))
        {
            Assert.Equal(0, p.StartUtc.Minute % 15);
        }

        Assert.Contains(entries, e => e.Kind == EntryKind.Slate);
        var a = entries.First(e => e.Item?.Title == "A");
        var slate = entries[entries.ToList().IndexOf(a) + 1];
        Assert.Equal(EntryKind.Slate, slate.Kind);
        Assert.Equal(TimeSpan.FromMinutes(8), slate.Duration);
    }

    [Fact]
    public void Padding_ByLength_HalfHoursUpToAnHour_WholeHoursBeyond()
    {
        Assert.Equal(TimeSpan.FromMinutes(30), Padding.Pad(TimeSpan.FromMinutes(22), TimeSpan.Zero, byLength: true));
        Assert.Equal(TimeSpan.FromMinutes(60), Padding.Pad(TimeSpan.FromMinutes(44), TimeSpan.Zero, byLength: true));
        Assert.Equal(TimeSpan.FromMinutes(60), Padding.Pad(TimeSpan.FromMinutes(60), TimeSpan.Zero, byLength: true));
        Assert.Equal(TimeSpan.FromMinutes(120), Padding.Pad(TimeSpan.FromMinutes(61), TimeSpan.Zero, byLength: true));
        Assert.Equal(TimeSpan.FromMinutes(120), Padding.Pad(TimeSpan.FromMinutes(95), TimeSpan.Zero, byLength: true));
        Assert.Equal(TimeSpan.FromMinutes(180), Padding.Pad(TimeSpan.FromMinutes(130), TimeSpan.Zero, byLength: true));
        // Fixed grid still wins when byLength is off.
        Assert.Equal(TimeSpan.FromMinutes(105), Padding.Pad(TimeSpan.FromMinutes(95), TimeSpan.FromMinutes(15), byLength: false));
    }

    [Fact]
    public void PaddingByLength_SchedulesEpisodeInHalfHour_AndFilmInTwoHours()
    {
        var pool = new List<PlayableItem> { Items.Movie("Ep", 22), Items.Movie("Film", 95), Items.Movie("Long", 130) };
        var input = Items.PoolInput(pool, order: PlayOrder.Sequential, byLength: true);
        var entries = ScheduleResolver.EnumerateInput(input, Items.Anchor, Items.Anchor.AddHours(6)).ToList();

        var ep = entries.First(e => e.Item?.Title == "Ep");
        var film = entries.First(e => e.Item?.Title == "Film");
        var longFilm = entries.First(e => e.Item?.Title == "Long");
        Assert.Equal(Items.Anchor, ep.StartUtc);
        Assert.Equal(Items.Anchor.AddMinutes(30), film.StartUtc);
        Assert.Equal(Items.Anchor.AddMinutes(150), longFilm.StartUtc);
        Assert.Equal(TimeSpan.FromMinutes(8), entries[entries.IndexOf(ep) + 1].Duration);
        Assert.Equal(TimeSpan.FromMinutes(25), entries[entries.IndexOf(film) + 1].Duration);
    }

    [Fact]
    public void Boundary_LastTickBelongsToEntry_NextTickToNext()
    {
        var input = Items.PoolInput(MixedPool(), order: PlayOrder.Sequential);
        var first = ScheduleResolver.ResolveInput(input, Items.Anchor)!.Entry;
        var atEnd = ScheduleResolver.ResolveInput(input, first.EndUtc.AddTicks(-1))!;
        Assert.Equal(first, atEnd.Entry);
        Assert.Equal(first.Duration - TimeSpan.FromTicks(1), atEnd.Offset);

        var next = ScheduleResolver.ResolveInput(input, first.EndUtc)!;
        Assert.Equal(first.EndUtc, next.Entry.StartUtc);
        Assert.Equal(TimeSpan.Zero, next.Offset);
    }

    [Fact]
    public void BeforeAnchor_StillResolves()
    {
        var input = Items.PoolInput(MixedPool());
        var r = ScheduleResolver.ResolveInput(input, Items.Anchor.AddDays(-3))!;
        Assert.NotNull(r.Entry.Item);
        Assert.True(r.Offset >= TimeSpan.Zero && r.Offset < r.Entry.Duration);
    }

    [Fact]
    public void EmptyPool_YieldsSlates()
    {
        var input = Items.PoolInput(new List<PlayableItem>());
        var entries = ScheduleResolver.EnumerateInput(input, Items.Anchor, Items.Anchor.AddHours(3));
        Assert.All(entries, e => Assert.Equal(EntryKind.Slate, e.Kind));
        Assert.True(entries.Count >= 3);
    }

    [Fact]
    public void ProgramId_IsStableAcrossInstances()
    {
        var pool = MixedPool();
        var a = ScheduleResolver.EnumerateInput(Items.PoolInput(pool), Items.Anchor, Items.Anchor.AddDays(1)).Select(e => e.ProgramId);
        var b = ScheduleResolver.EnumerateInput(Items.PoolInput(pool), Items.Anchor, Items.Anchor.AddDays(1)).Select(e => e.ProgramId);
        Assert.Equal(a, b);
    }
}
