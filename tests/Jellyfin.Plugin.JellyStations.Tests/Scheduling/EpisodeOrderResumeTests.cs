using Jellyfin.Plugin.JellyStations.Models;
using Jellyfin.Plugin.JellyStations.Scheduling;
using Jellyfin.Plugin.JellyStations.Tests.Fixtures;
using Xunit;

namespace Jellyfin.Plugin.JellyStations.Tests.Scheduling;

/// <summary>Where a series continues after a revision bump, and how its episodes rotate within the slots it holds.</summary>
public class EpisodeOrderResumeTests
{
    private static readonly Guid SeriesId = Items.DeterministicGuid("Bluey");

    private static List<PlayableItem> Bluey(int count) => Items.SeriesRun("Bluey", count, 7);

    [Fact]
    public void StartIndex_ById_IsTheEpisodeAfterTheOneThatAired()
    {
        var items = Bluey(5);
        var sorted = EpisodeOrder.Sorted(Enumerable.Range(0, 5), items);
        Assert.Equal(3, EpisodeOrder.StartIndex(sorted, items, new ResumePoint(items[2].ItemId, 1, 3)));
        Assert.Equal(0, EpisodeOrder.StartIndex(sorted, items, new ResumePoint(items[4].ItemId, 1, 5)));
    }

    [Fact]
    public void StartIndex_ByNumber_WhenTheIdIsGone()
    {
        var items = Bluey(5);
        items.RemoveAt(2); // episode 3 vanished
        var sorted = EpisodeOrder.Sorted(Enumerable.Range(0, 4), items);
        var at = EpisodeOrder.StartIndex(sorted, items, new ResumePoint(Guid.NewGuid(), 1, 3), out var found);
        Assert.True(found);
        Assert.Equal(4, items[sorted[at]].EpisodeNumber);
    }

    [Fact]
    public void StartIndex_IsZero_WhenNothingMatches()
    {
        var items = Bluey(3);
        var sorted = EpisodeOrder.Sorted(Enumerable.Range(0, 3), items);
        Assert.Equal(0, EpisodeOrder.StartIndex(sorted, items, new ResumePoint(Guid.NewGuid(), null, null), out var found));
        Assert.False(found);
        Assert.Equal(0, EpisodeOrder.StartIndex(sorted, items, new ResumePoint(Guid.NewGuid(), 1, 9), out found));
        Assert.False(found);
        Assert.Equal(0, EpisodeOrder.StartIndex(new List<int>(), items, new ResumePoint(items[0].ItemId, 1, 1)));
    }

    [Fact]
    public void StartIndex_WeightedDuplicates_ResolveInTheFirstPass()
    {
        var items = Bluey(3);
        items.AddRange(Bluey(3)); // weighted twice: E1 E2 E3 E1 E2 E3 after sorting
        var sorted = EpisodeOrder.Sorted(Enumerable.Range(0, 6), items);
        Assert.Equal(new[] { 1, 2, 3, 1, 2, 3 }, sorted.Select(i => items[i].EpisodeNumber!.Value).ToArray());
        Assert.Equal(2, EpisodeOrder.StartIndex(sorted, items, new ResumePoint(items[1].ItemId, 1, 2)));
    }

    [Fact]
    public void Resume_KeepsWhichSeriesHoldsEachSlot_AndRotatesAtTheFirstRealSlot()
    {
        var items = Bluey(4);
        items.Add(Items.Movie("Film", 90));
        var order = new[] { 0, 4, 1, 2, 3 }; // Bluey, Film, Bluey, Bluey, Bluey
        var starts = new long[] { 0, 10, 20, 30, 40 };
        var next = EpisodeOrder.Resume(order, items, starts, new Dictionary<Guid, int> { [SeriesId] = 2 }, firstRealStart: 20);

        Assert.Equal(4, order[1]); // the film did not move
        // Slot 2 is the first real one and gets episode index 2 (E3); slots 3 and 4 follow (E4, E1); slot 0 wraps back (E2).
        Assert.Equal(new[] { 2, 3, 4, 1 }, new[] { order[0], order[2], order[3], order[4] }.Select(i => items[i].EpisodeNumber!.Value).ToArray());
        // Three real positions after offset 2 -> the next cycle starts at (2 + 3) mod 4 = 1.
        Assert.Equal(1, next[SeriesId]);
    }

    [Fact]
    public void Resume_WithNoOffsetAndEverySlotReal_IsPlainEpisodeOrder()
    {
        var items = Bluey(4);
        var shuffled = new[] { 2, 0, 3, 1 };
        var expected = (int[])shuffled.Clone();
        EpisodeOrder.Apply(expected, items);
        var starts = new long[] { 0, 1, 2, 3 };
        var next = EpisodeOrder.Resume(shuffled, items, starts, new Dictionary<Guid, int>(), long.MinValue);
        Assert.Equal(expected, shuffled);
        Assert.Equal(0, next[SeriesId]);
    }

    [Fact]
    public void Resume_MarathonRunsStayConsecutive()
    {
        var items = Bluey(6);
        var order = new[] { 0, 1, 2, 3, 4, 5 }; // one run of six
        var starts = new long[] { 0, 1, 2, 3, 4, 5 };
        EpisodeOrder.Resume(order, items, starts, new Dictionary<Guid, int> { [SeriesId] = 4 }, firstRealStart: 0);
        Assert.Equal(new[] { 5, 6, 1, 2, 3, 4 }, order.Select(i => items[i].EpisodeNumber!.Value).ToArray());
    }

    [Fact]
    public void OffsetsFrom_ReadsTheSnapshotPerSeries()
    {
        var items = Bluey(5);
        items.AddRange(Items.SeriesRun("Frasier", 3, 22));
        var snapshot = new AirHistorySnapshot
        {
            TakenUtc = Items.Anchor,
            Keys = { ["s:" + SeriesId.ToString("N")] = new SnapshotKey { LastItemId = items[3].ItemId, Season = 1, Episode = 4 } }
        };
        var offsets = EpisodeOrder.OffsetsFrom(snapshot, items);
        Assert.Equal(4, offsets[SeriesId]);
        Assert.False(offsets.ContainsKey(Items.DeterministicGuid("Frasier")));
    }
}
