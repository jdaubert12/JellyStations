using Jellyfin.Plugin.JellyStations.Models;
using Jellyfin.Plugin.JellyStations.Scheduling;
using Jellyfin.Plugin.JellyStations.Tests.Fixtures;
using Xunit;

namespace Jellyfin.Plugin.JellyStations.Tests.Scheduling;

public class MarathonOrderTests
{
    private static List<PlayableItem> Pool()
    {
        var pool = Items.SeriesRun("Seinfeld", 12, 22);
        pool.AddRange(Items.SeriesRun("Frasier", 9, 23));
        pool.Add(Items.Movie("Heat", 170));
        pool.Add(Items.Movie("Alien", 117));
        return pool;
    }

    [Theory]
    [InlineData(PlayOrder.Shuffle)]
    [InlineData(PlayOrder.ShuffleEpisodesInOrder)]
    public void BuildRuns_CutsSeriesIntoRunsOfRequestedLength_AndPlaysEveryItemOnce(PlayOrder order)
    {
        var items = Pool();
        var runs = MarathonOrder.BuildRuns(items, order, 2, 3, true, 12345UL);

        Assert.Equal(items.Count, runs.Sum(r => r.Length));
        Assert.Equal(Enumerable.Range(0, items.Count), runs.SelectMany(r => r).OrderBy(i => i));

        foreach (var seriesRuns in runs.Where(r => MarathonOrder.SeriesOf(r, items).HasValue).GroupBy(r => MarathonOrder.SeriesOf(r, items)))
        {
            var lengths = seriesRuns.Select(r => r.Length).ToList();
            Assert.True(lengths.Count(l => l < 2) <= 1, "at most one short remainder run per series");
            Assert.All(lengths, l => Assert.InRange(l, 1, 3));
            Assert.All(seriesRuns, r => Assert.True(r.All(i => items[i].SeriesId == MarathonOrder.SeriesOf(r, items)), "a run never mixes series"));
        }

        Assert.All(runs.Where(r => !MarathonOrder.SeriesOf(r, items).HasValue), r => Assert.Single(r));

        if (order == PlayOrder.ShuffleEpisodesInOrder)
        {
            foreach (var series in runs.SelectMany(r => r).Select(i => items[i]).Where(i => i.SeriesId.HasValue).GroupBy(i => i.SeriesId))
            {
                var episodes = series.Select(i => i.EpisodeNumber!.Value).ToList();
                Assert.Equal(episodes.OrderBy(n => n), episodes);
            }
        }
    }

    [Fact]
    public void BuildRuns_KeepsRunsOfTheSameSeriesApart()
    {
        var items = Pool();
        var runs = MarathonOrder.BuildRuns(items, PlayOrder.Shuffle, 2, 2, true, 777UL);
        for (var i = 1; i < runs.Count; i++)
        {
            var a = MarathonOrder.SeriesOf(runs[i - 1], items);
            var b = MarathonOrder.SeriesOf(runs[i], items);
            Assert.False(a.HasValue && a == b, $"runs {i - 1} and {i} are both {items[runs[i][0]].Title}");
        }
    }

    [Fact]
    public void Scheduler_WithMarathon_IsContiguous_PlaysEachItemOncePerCycle_AndMatchesResolve()
    {
        var pool = Pool();
        var station = Items.PoolStation(pool, PlayOrder.ShuffleEpisodesInOrder);
        station.Pool.MarathonMin = 2;
        station.Pool.MarathonMax = 4;
        var input = ScheduleInput.From(station, new CompiledStation { StationId = station.Id, Revision = 1, Pool = pool });
        var cycle = TimeSpan.FromTicks(pool.Sum(i => i.RuntimeTicks));

        var entries = ScheduleResolver.EnumerateInput(input, Items.Anchor, Items.Anchor + (cycle * 2));
        for (var i = 1; i < entries.Count; i++)
        {
            Assert.Equal(entries[i - 1].EndUtc, entries[i].StartUtc);
        }

        var first = entries.Where(e => e.StartUtc >= Items.Anchor && e.StartUtc < Items.Anchor + cycle).Select(e => e.Item!.ItemId).ToList();
        Assert.Equal(pool.Count, first.Count);
        Assert.Equal(pool.Select(i => i.ItemId).OrderBy(g => g), first.OrderBy(g => g));

        // Marathons actually happen: at least one pair of consecutive programs is the same series.
        Assert.Contains(entries.Zip(entries.Skip(1)), p => p.First.Item?.SeriesId is { } s && p.Second.Item?.SeriesId == s);

        var rng = new Random(3);
        for (var n = 0; n < 200; n++)
        {
            var t = Items.Anchor.AddSeconds(rng.NextDouble() * (cycle * 2).TotalSeconds);
            var r = ScheduleResolver.ResolveInput(input, t)!;
            Assert.Equal(entries.Single(e => e.Contains(t)), r.Entry);
        }
    }

    [Fact]
    public void EpisodeOrderSorted_WithWeightedDuplicates_PlaysWholePassesBeforeRepeating()
    {
        var items = Items.SeriesRun("Frasier", 3, 22);
        items.AddRange(Items.SeriesRun("Frasier", 3, 22)); // weight 2: the resolver appends the block again
        var sorted = EpisodeOrder.Sorted(new[] { 5, 2, 0, 4, 1, 3 }, items);
        Assert.Equal(new[] { 1, 2, 3, 1, 2, 3 }, sorted.Select(i => items[i].EpisodeNumber!.Value));
    }

    [Fact]
    public void EpisodeOrderSorted_PutsSpecialsLast()
    {
        var items = new List<PlayableItem>
        {
            Items.Episode("X", 0, 1, 10),
            Items.Episode("X", 2, 1, 10),
            Items.Episode("X", 1, 2, 10),
            Items.Episode("X", 1, 1, 10)
        };
        var sorted = EpisodeOrder.Sorted(new[] { 0, 1, 2, 3 }, items);
        Assert.Equal(new[] { 3, 2, 1, 0 }, sorted);
    }
}
