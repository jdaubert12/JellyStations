using Jellyfin.Plugin.JellyStations.Models;
using Jellyfin.Plugin.JellyStations.Scheduling;
using Jellyfin.Plugin.JellyStations.Services;
using Jellyfin.Plugin.JellyStations.Storage;
using Jellyfin.Plugin.JellyStations.Tests.Fixtures;
using Xunit;

namespace Jellyfin.Plugin.JellyStations.Tests.History;

/// <summary>The pure half of the air log recorder: what one look at the timeline logs, and the snapshot it freezes.</summary>
public class AirLogRecorderTests
{
    private static readonly DateTime T0 = Items.Anchor;

    private static ScheduleInput Input() => Items.PoolInput(Items.SeriesRun("Bluey", 3, 7));

    private static ScheduleEntry Program(PlayableItem item, DateTime start, int minutes) => ScheduleEntry.Program(Input(), item, start, start.AddMinutes(minutes));

    [Fact]
    public void Observe_LogsPrograms_NotSlatesBumpersWeatherOrTheFuture()
    {
        var input = Input();
        var ep = Items.Episode("Bluey", 1, 1, 7);
        var clip = Items.Movie("Ident", 1);
        var entries = new[]
        {
            ScheduleEntry.Slate(input, T0, T0.AddMinutes(5)),
            Program(ep, T0.AddMinutes(5), 7),
            ScheduleEntry.Program(input, clip, T0.AddMinutes(12), T0.AddMinutes(13)) with { Kind = EntryKind.Bumper },
            ScheduleEntry.Weather(input, T0.AddMinutes(13), T0.AddMinutes(20), "Weather"),
            Program(Items.Episode("Bluey", 1, 2, 7), T0.AddMinutes(30), 7)
        };
        var log = new AirLog { StationId = input.StationId };
        Assert.True(AirLogRecorder.Observe(log, entries, T0.AddMinutes(20)));
        Assert.Single(log.Keys);
        Assert.Single(log.Items);
        var key = log.Keys[PoolOrdering.KeyOf(ep)];
        Assert.Equal(1, key.Count);
        Assert.Equal(T0.AddMinutes(5), key.LastStartUtc);
        Assert.Equal(T0.AddMinutes(12), key.LastEndUtc);
        Assert.Equal(ep.ItemId, key.LastItemId);
        Assert.Equal(1, key.Episode);
        Assert.Equal(T0.AddMinutes(20), log.LastTickUtc);
        Assert.Equal(entries[1].ProgramId, log.LastProgramId);
    }

    [Fact]
    public void Observe_CountsAProgramOnce_AcrossTicksAndRestarts()
    {
        var ep = Items.Episode("Bluey", 1, 1, 7);
        var entry = Program(ep, T0, 7);
        var log = new AirLog();
        Assert.True(AirLogRecorder.Observe(log, new[] { entry }, T0.AddMinutes(1)));
        Assert.False(AirLogRecorder.Observe(log, new[] { entry, entry }, T0.AddMinutes(2)));
        var reloaded = JsonFileStore.Clone(log);
        Assert.False(AirLogRecorder.Observe(reloaded, new[] { entry }, T0.AddMinutes(3)));
        Assert.Equal(1, reloaded.Keys[PoolOrdering.KeyOf(ep)].Count);
        Assert.Equal(1, reloaded.Items[ep.ItemId].Count);
    }

    [Fact]
    public void Observe_AShortProgramBetweenTwoTicks_IsLogged_AndCountsClimb()
    {
        var a = Items.Episode("Bluey", 1, 1, 7);
        var b = Items.Movie("Short", 1);
        var c = Items.Episode("Bluey", 1, 2, 7);
        var log = new AirLog();
        AirLogRecorder.Observe(log, new[] { Program(a, T0, 7) }, T0.AddMinutes(1));
        // The next look, a minute later, sees a's tail, the 45-second film and c starting.
        var second = new[] { Program(a, T0, 7), ScheduleEntry.Program(Input(), b, T0.AddMinutes(7), T0.AddMinutes(7).AddSeconds(45)), Program(c, T0.AddMinutes(8), 7) };
        Assert.True(AirLogRecorder.Observe(log, second, T0.AddMinutes(8).AddSeconds(30)));
        Assert.Equal(1, log.Keys[PoolOrdering.KeyOf(b)].Count);
        Assert.Equal(2, log.Keys[PoolOrdering.KeyOf(a)].Count);
        Assert.Equal(c.ItemId, log.Keys[PoolOrdering.KeyOf(a)].LastItemId);
        Assert.Equal(2, log.Keys[PoolOrdering.KeyOf(a)].Episode);
    }

    [Fact]
    public void WindowStart_IsTheLastTickWhenRecent_OtherwiseNow()
    {
        var log = new AirLog();
        Assert.Equal(T0, AirLogRecorder.WindowStart(log, T0));
        log.LastTickUtc = T0.AddMinutes(-1);
        Assert.Equal(T0.AddMinutes(-1), AirLogRecorder.WindowStart(log, T0));
        log.LastTickUtc = T0.AddHours(-1);
        Assert.Equal(T0.AddHours(-1), AirLogRecorder.WindowStart(log, T0));
        log.LastTickUtc = T0.AddHours(-1).AddSeconds(-1);
        Assert.Equal(T0, AirLogRecorder.WindowStart(log, T0));
        log.LastTickUtc = T0.AddMinutes(5);
        Assert.Equal(T0, AirLogRecorder.WindowStart(log, T0));
    }

    [Fact]
    public void Prune_DropsOldEntries_AndCapsTheRest()
    {
        var log = new AirLog();
        for (var i = 0; i < AirLog.MaxKeys + 10; i++)
        {
            log.Keys["m:" + i] = new AirLogKey { LastStartUtc = T0.AddMinutes(i), Count = 1 };
        }

        log.Keys["old"] = new AirLogKey { LastStartUtc = T0.AddDays(-400), Count = 1 };
        log.Items[Guid.NewGuid()] = new AirLogItem { LastStartUtc = T0.AddDays(-400), Count = 1 };
        log.Items[Guid.NewGuid()] = new AirLogItem { LastStartUtc = T0, Count = 1 };
        log.Prune(T0.AddDays(1));
        Assert.Equal(AirLog.MaxKeys, log.Keys.Count);
        Assert.False(log.Keys.ContainsKey("old"));
        Assert.False(log.Keys.ContainsKey("m:0"));
        Assert.True(log.Keys.ContainsKey("m:" + (AirLog.MaxKeys + 9)));
        Assert.Single(log.Items);
    }

    [Fact]
    public void Snapshot_PicksTheLastKey_FiltersToCompiledItems_AndCopiesNumbers()
    {
        var bluey = Items.SeriesRun("Bluey", 3, 7);
        var gone = Items.Movie("Gone", 90);
        var log = new AirLog();
        AirLogRecorder.Observe(log, new[] { Program(bluey[0], T0, 7), Program(gone, T0.AddMinutes(7), 90), Program(bluey[1], T0.AddMinutes(97), 7) }, T0.AddMinutes(100));
        // A later airing in the log is ignored by a snapshot taken before it.
        log.Keys["m:FUTURE|"] = new AirLogKey { LastStartUtc = T0.AddDays(1), Count = 1 };

        var compiled = new CompiledStation { Pool = bluey };
        var snapshot = AirLogRecorder.Snapshot(log, T0.AddMinutes(100), compiled)!;
        Assert.Equal(T0.AddMinutes(100), snapshot.TakenUtc);
        Assert.Equal(PoolOrdering.KeyOf(bluey[0]), snapshot.LastKey);
        Assert.Single(snapshot.Keys);
        var key = snapshot.Keys[snapshot.LastKey!];
        Assert.Equal(bluey[1].ItemId, key.LastItemId);
        Assert.Equal(2, key.Episode);
        Assert.Equal(1, key.Season);
        Assert.Equal(T0.AddMinutes(104), key.LastEndUtc);
        Assert.Equal(2, snapshot.Items.Count);
        Assert.False(snapshot.Items.ContainsKey(gone.ItemId));

        // Nothing of the log on the station: no snapshot at all, so the schedule stays byte-identical.
        Assert.Null(AirLogRecorder.Snapshot(log, T0.AddMinutes(100), new CompiledStation { Pool = new List<PlayableItem> { Items.Movie("Never aired", 90) } }));
        Assert.Null(AirLogRecorder.Snapshot(new AirLog(), T0, compiled));
    }

    [Fact]
    public void HistoryFor_FreshOnABump_OtherwiseCarried()
    {
        var previous = new CompiledStation { History = new AirHistorySnapshot { TakenUtc = T0 } };
        var fresh = new AirHistorySnapshot { TakenUtc = T0.AddDays(1) };
        Assert.Same(fresh, StationService.HistoryFor(true, previous, () => fresh));
        Assert.Same(previous.History, StationService.HistoryFor(false, previous, () => throw new InvalidOperationException("not taken")));
        Assert.Null(StationService.HistoryFor(false, null, () => fresh));
        Assert.Null(StationService.HistoryFor(true, previous, () => null));
    }

    [Fact]
    public void CompiledStation_OfTheOlderFormat_LoadsWithoutHistory()
    {
        var json = "{\"stationId\":\"11111111-1111-1111-1111-111111111111\",\"format\":3,\"revision\":4,\"pool\":[]}";
        var compiled = System.Text.Json.JsonSerializer.Deserialize<CompiledStation>(json, JsonFileStore.Options)!;
        Assert.Null(compiled.History);
        Assert.Equal(3, compiled.Format);
        Assert.NotEqual(CompiledStation.CurrentFormat, compiled.Format);
    }
}
