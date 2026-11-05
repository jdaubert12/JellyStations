using System.Diagnostics;
using Jellyfin.Plugin.JellyStations.Models;
using Jellyfin.Plugin.JellyStations.Scheduling;
using Jellyfin.Plugin.JellyStations.Services;
using Jellyfin.Plugin.JellyStations.Tests.Fixtures;
using Xunit;

namespace Jellyfin.Plugin.JellyStations.Tests.Scheduling;

/// <summary>A pool station continuing from an air history snapshot after a revision bump.</summary>
public class HistoryContinuityTests
{
    private static List<PlayableItem> ThreeSeries(int episodes = 10)
    {
        var pool = Items.SeriesRun("Bluey", episodes, 22);
        pool.AddRange(Items.SeriesRun("Frasier", episodes, 22));
        pool.AddRange(Items.SeriesRun("Seinfeld", episodes, 22));
        return pool;
    }

    private static List<PlayableItem> Films(int count) => Enumerable.Range(1, count).Select(i => Items.Movie("Film " + i, 100)).ToList();

    private static ScheduleInput Input(List<PlayableItem> pool, PlayOrder order, int revision, AirHistorySnapshot? history = null, int cooldown = 0, bool byLength = false)
    {
        var station = Items.PoolStation(pool, order, grid: 30, revision: revision, avoid: true, byLength: byLength);
        station.Pool.CooldownHours = cooldown;
        return ScheduleInput.From(station, new CompiledStation { StationId = station.Id, Revision = revision, Pool = pool, History = history });
    }

    private static List<ScheduleEntry> Programs(ScheduleInput input, DateTime from, DateTime to)
        => ScheduleResolver.EnumerateInput(input, from, to).Where(e => e.Kind == EntryKind.Program).ToList();

    /// <summary>The log of what revision 1 aired up to <paramref name="taken"/>, frozen for the pool.</summary>
    private static (AirLog Log, AirHistorySnapshot Snapshot) Aired(ScheduleInput revision1, List<PlayableItem> pool, DateTime taken)
    {
        var log = new AirLog { StationId = revision1.StationId };
        AirLogRecorder.Observe(log, ScheduleResolver.EnumerateInput(revision1, Items.Anchor, taken.AddTicks(1)), taken);
        var snapshot = AirLogRecorder.Snapshot(log, taken, new CompiledStation { Pool = pool });
        Assert.NotNull(snapshot);
        return (log, snapshot!);
    }

    private static int Next(int episode, int count) => (episode % count) + 1;

    [Fact]
    public void EpisodesInOrder_ContinueFromTheNextEpisode_AndStayContinuousAcrossCycles()
    {
        var pool = ThreeSeries();
        var rev1 = Input(pool, PlayOrder.ShuffleEpisodesInOrder, 1);
        var taken = Items.Anchor.AddDays(2).AddHours(7).AddMinutes(10);
        var (log, snapshot) = Aired(rev1, pool, taken);
        var rev2 = Input(pool, PlayOrder.ShuffleEpisodesInOrder, 2, snapshot);

        var after = Programs(rev2, taken, taken.AddDays(6)).Where(e => e.StartUtc >= taken).ToList();
        foreach (var series in after.GroupBy(e => e.Item!.SeriesId!.Value))
        {
            var last = log.Keys["s:" + series.Key.ToString("N")];
            var episodes = series.Select(e => e.Item!.EpisodeNumber!.Value).ToList();
            Assert.Equal(Next(last.Episode!.Value, 10), episodes[0]);
            for (var i = 1; i < episodes.Count; i++)
            {
                Assert.Equal(Next(episodes[i - 1], 10), episodes[i]);
            }

            Assert.True(episodes.Count > 30, "three cycles were checked");
        }
    }

    [Fact]
    public void EpisodesInOrder_WithoutASnapshot_IsUnchanged_AndCyclesBeforeTheSnapshotAreIdentical()
    {
        var pool = ThreeSeries();
        var rev1 = Input(pool, PlayOrder.ShuffleEpisodesInOrder, 1);
        var taken = Items.Anchor.AddDays(3).AddHours(1);
        var (_, snapshot) = Aired(rev1, pool, taken);

        var plain = Programs(Input(pool, PlayOrder.ShuffleEpisodesInOrder, 2), Items.Anchor, Items.Anchor.AddDays(5));
        var withHistory = Programs(Input(pool, PlayOrder.ShuffleEpisodesInOrder, 2, snapshot), Items.Anchor, Items.Anchor.AddDays(5));

        // Up to the cycle containing the snapshot the two agree slot for slot.
        var cycle = TimeSpan.FromMinutes(30 * 30);
        var chainStart = Items.Anchor + TimeSpan.FromTicks(((taken - Items.Anchor).Ticks / cycle.Ticks) * cycle.Ticks);
        static string Key(ScheduleEntry e) => e.ProgramId + "/" + e.Item!.ItemId.ToString("N");
        Assert.Equal(plain.Where(e => e.StartUtc < chainStart).Select(Key), withHistory.Where(e => e.StartUtc < chainStart).Select(Key));
        // Which series holds each slot never changes either, only the episode within it.
        Assert.Equal(plain.Select(e => e.StartUtc + "/" + e.Item!.SeriesId), withHistory.Select(e => e.StartUtc + "/" + e.Item!.SeriesId));
        Assert.NotEqual(plain.Select(Key), withHistory.Select(Key));
    }

    [Fact]
    public void PlainShuffle_OnlyTheSnapshotCycleDiffers_AndTheLastKeyDoesNotLead()
    {
        var pool = Films(12);
        var rev1 = Input(pool, PlayOrder.Shuffle, 1, byLength: true);
        var found = false;
        for (var hours = 1; hours < 48 && !found; hours += 3)
        {
            var taken = Items.Anchor.AddHours(hours);
            var (_, snapshot) = Aired(rev1, pool, taken);
            var plain = Input(pool, PlayOrder.Shuffle, 2, byLength: true);
            var withHistory = Input(pool, PlayOrder.Shuffle, 2, snapshot, byLength: true);

            var first = Programs(withHistory, taken, taken.AddDays(2)).First(e => e.StartUtc >= taken);
            Assert.NotEqual(snapshot.LastKey, PoolOrdering.KeyOf(first.Item!));

            // From the next cycle on the two timelines agree.
            var cycle = TimeSpan.FromHours(2 * 12);
            var nextCycle = Items.Anchor + TimeSpan.FromTicks((((taken - Items.Anchor).Ticks / cycle.Ticks) + 1) * cycle.Ticks);
            static string Key(ScheduleEntry e) => e.ProgramId + "/" + e.Item!.ItemId.ToString("N");
            Assert.Equal(Programs(plain, nextCycle, nextCycle.AddDays(3)).Select(Key), Programs(withHistory, nextCycle, nextCycle.AddDays(3)).Select(Key));

            // At least one of the tried instants had the plain order leading with what just aired, so the seam fix did something.
            var plainFirst = Programs(plain, taken, taken.AddDays(1)).First(e => e.StartUtc >= taken);
            found |= PoolOrdering.KeyOf(plainFirst.Item!) == snapshot.LastKey;
        }

        Assert.True(found, "no tried instant exercised the seam; widen the search");
    }

    [Fact]
    public void Cooldown_RestsWhatTheSnapshotSaysJustAired()
    {
        var pool = Films(8);
        var taken = Items.Anchor.AddDays(1).AddHours(3);
        var rested = pool.Take(3).Select(PoolOrdering.KeyOf).ToList();
        var snapshot = new AirHistorySnapshot { TakenUtc = taken, LastKey = rested[0] };
        foreach (var key in rested)
        {
            snapshot.Keys[key] = new SnapshotKey { LastEndUtc = taken, LastItemId = Guid.Empty };
        }

        var input = Input(pool, PlayOrder.Shuffle, 2, snapshot, cooldown: 6, byLength: true);
        var soon = Programs(input, taken, taken.AddHours(6)).Where(e => e.StartUtc >= taken).Select(e => PoolOrdering.KeyOf(e.Item!)).ToList();
        Assert.NotEmpty(soon);
        Assert.Empty(soon.Intersect(rested));
    }

    [Fact]
    public void Resolve_MatchesEnumerate_AroundTheSnapshot()
    {
        var pool = ThreeSeries();
        var rev1 = Input(pool, PlayOrder.ShuffleEpisodesInOrder, 1);
        var taken = Items.Anchor.AddDays(1).AddHours(5).AddMinutes(17);
        var (_, snapshot) = Aired(rev1, pool, taken);
        var rev2 = Input(pool, PlayOrder.ShuffleEpisodesInOrder, 2, snapshot);
        var entries = ScheduleResolver.EnumerateInput(rev2, taken.AddDays(-1), taken.AddDays(3)).ToList();
        var rng = new Random(7);
        for (var i = 0; i < 200; i++)
        {
            var t = taken.AddMinutes(rng.Next(-24 * 60, 3 * 24 * 60));
            var expected = entries.Single(e => e.Contains(t));
            var resolved = ScheduleResolver.ResolveInput(rev2, t)!;
            Assert.Equal(expected.ProgramId, resolved.Entry.ProgramId);
            Assert.Equal(expected.Item?.ItemId, resolved.Entry.Item?.ItemId);
        }
    }

    [Fact]
    public void Lineup_IgnoresTheSnapshot()
    {
        var lineup = Items.SeriesRun("Bluey", 5, 7);
        var station = Items.PoolStation(lineup, PlayOrder.Sequential, grid: 0);
        station.Mode = ScheduleMode.Lineup;
        var snapshot = new AirHistorySnapshot { TakenUtc = Items.Anchor.AddHours(1), LastKey = "s:x" };
        var input = ScheduleInput.From(station, new CompiledStation { StationId = station.Id, Revision = 1, Lineup = lineup, History = snapshot });
        Assert.Null(input.History);
    }

    [Fact]
    public void ChainedCycles_StayCheap()
    {
        var pool = new List<PlayableItem>();
        for (var s = 0; s < 40; s++)
        {
            pool.AddRange(Items.SeriesRun("Series " + s, 50, 22));
        }

        var taken = Items.Anchor.AddHours(3);
        var snapshot = new AirHistorySnapshot { TakenUtc = taken, LastKey = PoolOrdering.KeyOf(pool[0]) };
        snapshot.Keys[snapshot.LastKey] = new SnapshotKey { LastEndUtc = taken, LastItemId = pool[7].ItemId, Season = 1, Episode = 8 };
        var input = Input(pool, PlayOrder.ShuffleEpisodesInOrder, 2, snapshot);
        var cycle = TimeSpan.FromMinutes(30 * 2000);
        var sw = Stopwatch.StartNew();
        for (var k = 1; k <= 50; k++)
        {
            Assert.NotNull(ScheduleResolver.ResolveInput(input, taken + TimeSpan.FromTicks(cycle.Ticks * k)));
        }

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(8), "50 chained cycles of 2000 items took " + sw.Elapsed);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 5)]
    public void EpisodesInOrder_OfDifferentLengths_LeaveNoHoleInTheTimeline(int marathonMin, int marathonMax)
    {
        // Continuing a series rotates its episodes within the positions it holds. With episodes of different
        // lengths (any real library) the slot times have to follow the rotated order: laid out before it, a short
        // episode in a long episode's slot left an instant no entry covered, which the walk below never got past
        // (and a schedule enumeration filled memory on until the server died).
        var pool = new List<PlayableItem>();
        foreach (var name in new[] { "Bluey", "Frasier", "Seinfeld" })
        {
            var run = Items.SeriesRun(name, 10, 20);
            for (var i = 0; i < run.Count; i++)
            {
                run[i].RuntimeTicks += TimeSpan.FromSeconds((i * 37) % 300).Ticks;
            }

            pool.AddRange(run);
        }

        ScheduleInput Station(int revision, AirHistorySnapshot? history)
        {
            var station = Items.PoolStation(pool, PlayOrder.ShuffleEpisodesInOrder, grid: 0, revision: revision, avoid: true);
            station.Pool.MarathonMin = marathonMin;
            station.Pool.MarathonMax = marathonMax;
            return ScheduleInput.From(station, new CompiledStation { StationId = station.Id, Revision = revision, Pool = pool, History = history });
        }

        var taken = Items.Anchor.AddDays(2).AddHours(7).AddMinutes(10);
        var (_, snapshot) = Aired(Station(1, null), pool, taken);
        var rev2 = Station(2, snapshot);

        var t = taken;
        var end = taken.AddDays(3);
        var steps = 0;
        while (t < end)
        {
            var entry = ScheduleResolver.ResolveInput(rev2, t)!.Entry;
            Assert.True(entry.StartUtc <= t && entry.EndUtc > t, $"no entry covers {t:o}: got {entry.Kind} {entry.StartUtc:o} to {entry.EndUtc:o}");
            t = entry.EndUtc;
            Assert.True(++steps < 5000, "the walk does not advance");
        }
    }
}
