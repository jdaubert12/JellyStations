using Jellyfin.Plugin.JellyStations.Library;
using Jellyfin.Plugin.JellyStations.Models;
using Jellyfin.Plugin.JellyStations.Scheduling;
using Jellyfin.Plugin.JellyStations.Tests.Fixtures;
using Xunit;

namespace Jellyfin.Plugin.JellyStations.Tests.Library;

/// <summary>The household's play history: what it leaves out, where each series stands, and how that reaches the schedulers.</summary>
public class HouseholdHistoryTests
{
    private static readonly DateTime Now = new(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);

    private static WatchState Played(int daysAgo) => new(true, Now.AddDays(-daysAgo));

    private static readonly WatchState Unplayed = new(false, null);

    private static Dictionary<Guid, IReadOnlyList<WatchState>> States(params (PlayableItem Item, WatchState[] States)[] rows)
        => rows.ToDictionary(r => r.Item.ItemId, r => (IReadOnlyList<WatchState>)r.States);

    [Fact]
    public void Watched_NeedsEveryone_AndFinishedIsTheLatestDate()
    {
        Assert.False(HouseholdHistory.Watched(null));
        Assert.False(HouseholdHistory.Watched(Array.Empty<WatchState>()));
        Assert.False(HouseholdHistory.Watched(new[] { Played(1), Unplayed }));
        Assert.True(HouseholdHistory.Watched(new[] { Played(1), Played(9) }));

        Assert.Equal(Now.AddDays(-1), HouseholdHistory.FinishedUtc(new[] { Played(9), Played(1) }));
        Assert.Null(HouseholdHistory.FinishedUtc(new[] { Played(1), Unplayed }));
        // Played without a date: watched, but when is unknown, so it is not "recent".
        Assert.Null(HouseholdHistory.FinishedUtc(new[] { Played(1), new WatchState(true, null) }));
        Assert.True(HouseholdHistory.RecentlyFinished(new[] { Played(2) }, 7, Now));
        Assert.False(HouseholdHistory.RecentlyFinished(new[] { Played(10) }, 7, Now));
        Assert.False(HouseholdHistory.RecentlyFinished(new[] { Played(2) }, 0, Now));
    }

    [Fact]
    public void SkipFinished_DropsRecentOnes_RecordsEachOnce_KeepsOldAndUnplayed()
    {
        var recent = Items.Movie("Recent", 90);
        var old = Items.Movie("Old", 90);
        var fresh = Items.Movie("Fresh", 90);
        var states = States((recent, new[] { Played(2), Played(5) }), (old, new[] { Played(30) }), (fresh, new[] { Unplayed }));
        var compiled = new CompiledStation();

        var pool = HouseholdHistory.SkipFinished(new List<PlayableItem> { recent, old, fresh }, 7, Now, states, compiled);
        var filler = HouseholdHistory.SkipFinished(new List<PlayableItem> { recent }, 7, Now, states, compiled);

        Assert.Equal(new[] { "Old", "Fresh" }, pool.Select(i => i.Title));
        Assert.Empty(filler);
        Assert.Equal("Recent", Assert.Single(compiled.HouseholdExcluded).Title);
        Assert.Equal(ExclusionReason.HouseholdWatched, Assert.Single(compiled.Excluded).Reason);

        // Off: identity.
        var list = new List<PlayableItem> { recent };
        Assert.Same(list, HouseholdHistory.SkipFinished(list, 0, Now, states, new CompiledStation()));
    }

    [Fact]
    public void Progress_IsTheEpisodeBeforeTheFirstUnwatched()
    {
        var bluey = Items.SeriesRun("Bluey", 10, 22);
        var frasier = Items.SeriesRun("Frasier", 10, 22);
        var done = Items.SeriesRun("Done", 3, 22);
        var states = new Dictionary<Guid, IReadOnlyList<WatchState>>();
        foreach (var ep in bluey.Take(3))
        {
            states[ep.ItemId] = new[] { Played(3), Played(1) };
        }

        // Episode 5 watched out of order does not move the position past the gap at 4.
        states[bluey[4].ItemId] = new[] { Played(1), Played(1) };
        // Frasier: one of two users watched episode 1; the household has not.
        states[frasier[0].ItemId] = new[] { Played(1), Unplayed };
        foreach (var ep in done)
        {
            states[ep.ItemId] = new[] { Played(1), Played(1) };
        }

        var progress = HouseholdHistory.Progress(bluey.Concat(frasier).Concat(done).Concat(new[] { Items.Movie("Film", 100) }), states);

        var position = Assert.Single(progress).Value;
        Assert.Equal(bluey[2].ItemId, position.ItemId);
        Assert.Equal(3, position.Episode);
        Assert.Equal(PoolOrdering.KeyOf(bluey[0]), progress.Keys.Single());
    }

    [Fact]
    public void Apply_MakesASnapshot_OrMovesASeriesForward_NeverBack()
    {
        var bluey = Items.SeriesRun("Bluey", 10, 22);
        var frasier = Items.SeriesRun("Frasier", 10, 22);
        var items = bluey.Concat(frasier).ToList();
        var key = PoolOrdering.KeyOf(bluey[0]);
        var household = new HouseholdSnapshot { Progress = { [key] = new HouseholdPosition { ItemId = bluey[2].ItemId, Season = 1, Episode = 3 } } };

        // Nothing aired yet: a snapshot taken now, with the household's position and nothing else.
        var made = HouseholdHistory.Apply(null, household, items, Now)!;
        Assert.Equal(Now, made.TakenUtc);
        Assert.Null(made.LastKey);
        Assert.Equal(bluey[2].ItemId, Assert.Single(made.Keys).Value.LastItemId);
        Assert.Equal(DateTime.MinValue, made.Keys[key].LastEndUtc);

        // The station is behind (aired episode 1): it skips to the household's place; the other series is untouched; the input is not changed.
        var behind = new AirHistorySnapshot { TakenUtc = Now.AddHours(-1), LastKey = key };
        behind.Keys[key] = new SnapshotKey { LastEndUtc = Now.AddHours(-1), LastItemId = bluey[0].ItemId, Season = 1, Episode = 1 };
        behind.Keys[PoolOrdering.KeyOf(frasier[0])] = new SnapshotKey { LastEndUtc = Now.AddHours(-2), LastItemId = frasier[5].ItemId, Season = 1, Episode = 6 };
        behind.Items[bluey[0].ItemId] = Now.AddHours(-1.5);
        behind.Items[frasier[5].ItemId] = Now.AddHours(-2.5);
        var moved = HouseholdHistory.Apply(behind, household, items, Now)!;
        Assert.Equal(bluey[2].ItemId, moved.Keys[key].LastItemId);
        Assert.Equal(3, moved.Keys[key].Episode);
        Assert.Equal(Now.AddHours(-1), moved.Keys[key].LastEndUtc);
        Assert.Equal(frasier[5].ItemId, moved.Keys[PoolOrdering.KeyOf(frasier[0])].LastItemId);
        Assert.False(moved.Items.ContainsKey(bluey[0].ItemId));
        Assert.True(moved.Items.ContainsKey(frasier[5].ItemId));
        Assert.Equal(bluey[0].ItemId, behind.Keys[key].LastItemId);

        // The station is ahead (aired episode 7): nothing changes, same object back.
        var ahead = new AirHistorySnapshot { TakenUtc = Now.AddHours(-1) };
        ahead.Keys[key] = new SnapshotKey { LastEndUtc = Now.AddHours(-1), LastItemId = bluey[6].ItemId, Season = 1, Episode = 7 };
        Assert.Same(ahead, HouseholdHistory.Apply(ahead, household, items, Now));

        // The aired episode is gone from the library: placed by number.
        var gone = new AirHistorySnapshot { TakenUtc = Now.AddHours(-1) };
        gone.Keys[key] = new SnapshotKey { LastEndUtc = Now.AddHours(-1), LastItemId = Guid.NewGuid(), Season = 1, Episode = 8 };
        Assert.Same(gone, HouseholdHistory.Apply(gone, household, items, Now));

        // No progress: untouched.
        Assert.Null(HouseholdHistory.Apply(null, new HouseholdSnapshot(), items, Now));
        Assert.Same(ahead, HouseholdHistory.Apply(ahead, null, items, Now));
    }

    [Fact]
    public void SameProgress_ComparesSeriesAndEpisodes()
    {
        var a = new HouseholdSnapshot { Progress = { ["s:1"] = new HouseholdPosition { ItemId = Items.DeterministicGuid("x") } } };
        var b = new HouseholdSnapshot { Progress = { ["s:1"] = new HouseholdPosition { ItemId = Items.DeterministicGuid("x") } } };
        var c = new HouseholdSnapshot { Progress = { ["s:1"] = new HouseholdPosition { ItemId = Items.DeterministicGuid("y") } } };
        Assert.True(HouseholdHistory.SameProgress(a, b));
        Assert.False(HouseholdHistory.SameProgress(a, c));
        Assert.False(HouseholdHistory.SameProgress(a, null));
        Assert.True(HouseholdHistory.SameProgress(null, new HouseholdSnapshot()));
    }

    [Fact]
    public void Wanted_NeedsUsersAndASwitch_AndNotAFixedList()
    {
        var station = new Station { Mode = ScheduleMode.Pool, Household = new HouseholdConfig { FollowProgress = true } };
        var users = new List<Guid> { Guid.NewGuid() };
        Assert.True(HouseholdHistory.Wanted(station, users));
        Assert.False(HouseholdHistory.Wanted(station, new List<Guid>()));
        Assert.False(HouseholdHistory.Wanted(new Station { Mode = ScheduleMode.Pool }, users));
        Assert.False(HouseholdHistory.Wanted(new Station { Mode = ScheduleMode.Lineup, Household = new HouseholdConfig { SkipFinishedDays = 7 } }, users));
        Assert.True(HouseholdHistory.Wanted(new Station { Mode = ScheduleMode.Slots, Household = new HouseholdConfig { SkipFinishedDays = 7 } }, users));
    }

    [Fact]
    public void APoolWithEpisodesInOrder_StartsEachSeriesAtTheHouseholdsFirstUnwatched()
    {
        var bluey = Items.SeriesRun("Bluey", 10, 22);
        var frasier = Items.SeriesRun("Frasier", 10, 22);
        var pool = bluey.Concat(frasier).ToList();
        var household = new HouseholdSnapshot
        {
            Progress = { [PoolOrdering.KeyOf(bluey[0])] = new HouseholdPosition { ItemId = bluey[3].ItemId, Season = 1, Episode = 4 } }
        };
        var taken = Items.Anchor.AddDays(1).AddHours(9);
        var station = Items.PoolStation(pool, PlayOrder.ShuffleEpisodesInOrder, grid: 30, revision: 2);
        var compiled = new CompiledStation { StationId = station.Id, Revision = 2, Pool = pool, Household = household };
        compiled.History = HouseholdHistory.Apply(null, household, pool, taken);
        var input = ScheduleInput.From(station, compiled);

        var after = ScheduleResolver.EnumerateInput(input, taken, taken.AddDays(3))
            .Where(e => e.Kind == EntryKind.Program && e.StartUtc >= taken)
            .ToList();
        var blueyEpisodes = after.Where(e => e.Item!.SeriesId == bluey[0].SeriesId).Select(e => e.Item!.EpisodeNumber!.Value).ToList();
        var frasierEpisodes = after.Where(e => e.Item!.SeriesId == frasier[0].SeriesId).Select(e => e.Item!.EpisodeNumber!.Value).ToList();

        Assert.Equal(5, blueyEpisodes[0]);
        Assert.Equal(1, frasierEpisodes[0]);
        for (var i = 1; i < 6; i++)
        {
            Assert.Equal((blueyEpisodes[i - 1] % 10) + 1, blueyEpisodes[i]);
        }
    }

    [Fact]
    public void FillerWithEpisodesInOrder_StartsAtTheHouseholdsFirstUnwatched()
    {
        var bluey = Items.SeriesRun("Bluey", 8, 22);
        var household = new HouseholdSnapshot
        {
            Progress = { [PoolOrdering.KeyOf(bluey[0])] = new HouseholdPosition { ItemId = bluey[2].ItemId, Season = 1, Episode = 3 } }
        };
        var taken = Items.Anchor.AddHours(10);
        var station = new Station
        {
            Id = Items.StationA,
            Name = "Slots",
            ChannelNumber = "101",
            AnchorUtc = Items.Anchor,
            Revision = 2,
            Mode = ScheduleMode.Slots,
            TimeZoneId = "America/New_York",
            Slots = new SlotsConfig { Filler = new FillerConfig { Order = PlayOrder.ShuffleEpisodesInOrder } }
        };
        var compiled = new CompiledStation { StationId = station.Id, Revision = 2, Filler = bluey, Household = household };
        compiled.History = HouseholdHistory.Apply(null, household, bluey, taken);
        var input = ScheduleInput.From(station, compiled);

        var episodes = ScheduleResolver.EnumerateInput(input, taken, taken.AddHours(6))
            .Where(e => e.Kind == EntryKind.Program && e.StartUtc >= taken)
            .Select(e => e.Item!.EpisodeNumber!.Value)
            .ToList();
        Assert.Equal(4, episodes[0]);
        Assert.Equal(5, episodes[1]);
    }
}
