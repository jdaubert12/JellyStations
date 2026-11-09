using Jellyfin.Plugin.JellyStations.Models;
using Jellyfin.Plugin.JellyStations.Scheduling;
using Jellyfin.Plugin.JellyStations.Services;
using Jellyfin.Plugin.JellyStations.Tests.Fixtures;
using Xunit;

namespace Jellyfin.Plugin.JellyStations.Tests.Scheduling;

/// <summary>Blocks and the between-block stream continuing from an air history snapshot after a revision bump.</summary>
public class WeeklyContinuityTests
{
    private static readonly Guid BlockId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly TimeZoneInfo NewYork = TimeZones.Resolve("America/New_York");

    private static SlotBlock WeekdayBlock(PlayOrder order, int minutes = 60) => new()
    {
        Id = BlockId,
        Name = "Prime",
        Days = new List<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday },
        StartLocal = "20:00",
        DurationMinutes = minutes,
        Order = order,
        Overflow = SlotOverflow.FillerRemainder
    };

    private static DateTime Local(int day, int hour, int minute = 0)
        => TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 10, day, hour, minute, 0, DateTimeKind.Unspecified), NewYork);

    private static ScheduleInput Slots(PlayOrder blockOrder, List<PlayableItem> blockItems, List<PlayableItem> filler, PlayOrder fillerOrder, int revision, AirHistorySnapshot? history = null, DateTime? anchor = null)
    {
        var input = Items.SlotsInput(new List<SlotBlock> { WeekdayBlock(blockOrder) }, new Dictionary<Guid, List<PlayableItem>> { [BlockId] = blockItems }, filler, fillerOrder);
        var station = new Station
        {
            Id = input.StationId,
            Name = "Slots",
            ChannelNumber = "101",
            AnchorUtc = anchor ?? Items.Anchor,
            Revision = revision,
            Mode = ScheduleMode.Slots,
            TimeZoneId = "America/New_York",
            Slots = new SlotsConfig { Blocks = new List<SlotBlock> { WeekdayBlock(blockOrder) }, Filler = new FillerConfig { Order = fillerOrder } }
        };
        var compiled = new CompiledStation { StationId = station.Id, Revision = revision, Filler = filler, Blocks = new Dictionary<Guid, List<PlayableItem>> { [BlockId] = blockItems }, History = history };
        return ScheduleInput.From(station, compiled);
    }

    private static (AirLog Log, AirHistorySnapshot Snapshot) Aired(ScheduleInput revision1, CompiledStation compiled, DateTime taken)
    {
        var log = new AirLog { StationId = revision1.StationId };
        AirLogRecorder.Observe(log, ScheduleResolver.EnumerateInput(revision1, Items.Anchor, taken.AddTicks(1)), taken);
        var snapshot = AirLogRecorder.Snapshot(log, taken, compiled);
        Assert.NotNull(snapshot);
        return (log, snapshot!);
    }

    private static List<ScheduleEntry> Programs(ScheduleInput input, DateTime from, DateTime to, string? label = null)
        => ScheduleResolver.EnumerateInput(input, from, to).Where(e => e.Kind == EntryKind.Program && (label is null || e.SourceLabel == label)).ToList();

    [Theory]
    [InlineData(PlayOrder.Sequential)]
    [InlineData(PlayOrder.ShuffleEpisodesInOrder)]
    public void Block_ResumesAfterTheLastAiredItem(PlayOrder order)
    {
        var seinfeld = Items.SeriesRun("Seinfeld", 10, 22);
        var cartoons = Items.SeriesRun("Cartoons", 30, 11);
        var rev1 = Slots(order, seinfeld, cartoons, PlayOrder.Sequential, 1);
        var compiled = new CompiledStation { Filler = cartoons, Blocks = { [BlockId] = seinfeld } };

        // Monday and Tuesday aired episodes 1-4; the snapshot is taken Wednesday noon.
        var taken = Local(14, 12);
        var (log, snapshot) = Aired(rev1, compiled, taken);
        Assert.Equal(4, log.Keys["s:" + Items.DeterministicGuid("Seinfeld").ToString("N")].Episode);

        // The edit put another series in front of Seinfeld, which used to make the block walk the new list from
        // Monday again (Wednesday: items 5 and 6 of the new list, Seinfeld episodes 1 and 2).
        var mixed = Items.SeriesRun("Frasier", 4, 22);
        mixed.AddRange(seinfeld);
        var rev2 = Slots(order, mixed, cartoons, PlayOrder.Sequential, 2, snapshot);
        var firstSeinfeld = Programs(rev2, Local(14, 20), Local(21, 0), "Prime").First(e => e.Item!.Title == "Seinfeld");
        Assert.Equal(5, firstSeinfeld.Item!.EpisodeNumber);

        if (order == PlayOrder.Sequential)
        {
            Assert.Equal(new int?[] { 5, 6 }, Programs(rev2, Local(14, 20), Local(14, 21), "Prime").Select(e => e.Item!.EpisodeNumber).ToList());
            var plain = Programs(Slots(order, mixed, cartoons, PlayOrder.Sequential, 2), Local(14, 20), Local(14, 21), "Prime").Select(e => e.Item!.Title + " " + e.Item.EpisodeNumber).ToList();
            Assert.Equal(new[] { "Seinfeld 1", "Seinfeld 2" }, plain);
        }
    }

    [Fact]
    public void Block_StraddlingTheSnapshot_ResumesAtTheRightItem()
    {
        var seinfeld = Items.SeriesRun("Seinfeld", 10, 22);
        var cartoons = Items.SeriesRun("Cartoons", 30, 11);
        var rev1 = Slots(PlayOrder.Sequential, seinfeld, cartoons, PlayOrder.Sequential, 1);
        var compiled = new CompiledStation { Filler = cartoons, Blocks = { [BlockId] = seinfeld } };

        // Tuesday 20:10: episode 3 is airing (20:00-20:22); episode 4 is due at 20:22.
        var taken = Local(13, 20, 10);
        var (_, snapshot) = Aired(rev1, compiled, taken);
        var rev2 = Slots(PlayOrder.Sequential, seinfeld, cartoons, PlayOrder.Sequential, 2, snapshot);
        var at2022 = ScheduleResolver.ResolveInput(rev2, Local(13, 20, 22))!.Entry;
        Assert.Equal(4, at2022.Item!.EpisodeNumber);
    }

    [Fact]
    public void ShuffleBlock_IsNotSeeded()
    {
        var seinfeld = Items.SeriesRun("Seinfeld", 10, 22);
        var cartoons = Items.SeriesRun("Cartoons", 30, 11);
        var rev1 = Slots(PlayOrder.Shuffle, seinfeld, cartoons, PlayOrder.Sequential, 1);
        var compiled = new CompiledStation { Filler = cartoons, Blocks = { [BlockId] = seinfeld } };
        var taken = Local(14, 12);
        var (_, snapshot) = Aired(rev1, compiled, taken);

        static string Key(ScheduleEntry e) => e.StartUtc + "/" + e.Item!.ItemId.ToString("N");
        var plain = Programs(Slots(PlayOrder.Shuffle, seinfeld, cartoons, PlayOrder.Shuffle, 2), Local(14, 20), Local(16, 0), "Prime").Select(Key);
        var withHistory = Programs(Slots(PlayOrder.Shuffle, seinfeld, cartoons, PlayOrder.Shuffle, 2, snapshot), Local(14, 20), Local(16, 0), "Prime").Select(Key);
        Assert.Equal(plain, withHistory);
    }

    [Fact]
    public void SequentialFiller_SkipsWhatItHadPlayedThisPass_ThenTheNextPassIsFull()
    {
        var seinfeld = Items.SeriesRun("Seinfeld", 10, 22);
        var cartoons = Items.SeriesRun("Cartoons", 30, 11);
        var rev1 = Slots(PlayOrder.Sequential, seinfeld, cartoons, PlayOrder.Sequential, 1);
        var compiled = new CompiledStation { Filler = cartoons, Blocks = { [BlockId] = seinfeld } };

        var taken = Local(12, 10, 5);
        var (log, snapshot) = Aired(rev1, compiled, taken);
        var lastAired = log.Keys["s:" + Items.DeterministicGuid("Cartoons").ToString("N")].Episode!.Value;
        Assert.InRange(lastAired, 2, 29);

        var rev2 = Slots(PlayOrder.Sequential, seinfeld, cartoons, PlayOrder.Sequential, 2, snapshot);
        var after = Programs(rev2, taken, Local(12, 20), "Filler").Where(e => e.StartUtc >= taken).Select(e => e.Item!.EpisodeNumber!.Value).ToList();
        Assert.Equal(lastAired + 1, after[0]);
        // The short pass runs to 30, then full passes start at 1.
        var expected = Enumerable.Range(lastAired + 1, 30 - lastAired).Concat(Enumerable.Range(1, 30)).Concat(Enumerable.Range(1, 30)).Concat(Enumerable.Range(1, 30)).Take(after.Count);
        Assert.Equal(expected, after);
    }

    [Fact]
    public void EpisodesInOrderFiller_ContinuesEachSeries()
    {
        var seinfeld = Items.SeriesRun("Seinfeld", 10, 22);
        var filler = Items.SeriesRun("Bluey", 12, 11);
        filler.AddRange(Items.SeriesRun("Frasier", 12, 11));
        var rev1 = Slots(PlayOrder.Sequential, seinfeld, filler, PlayOrder.ShuffleEpisodesInOrder, 1);
        var compiled = new CompiledStation { Filler = filler, Blocks = { [BlockId] = seinfeld } };

        var taken = Local(12, 2, 3);
        var (log, snapshot) = Aired(rev1, compiled, taken);
        var rev2 = Slots(PlayOrder.Sequential, seinfeld, filler, PlayOrder.ShuffleEpisodesInOrder, 2, snapshot);
        var after = Programs(rev2, taken, Local(12, 9), "Filler").Where(e => e.StartUtc >= taken).ToList();
        foreach (var series in after.GroupBy(e => e.Item!.SeriesId!.Value))
        {
            var last = log.Keys["s:" + series.Key.ToString("N")].Episode!.Value;
            Assert.InRange(last, 1, 11);
            Assert.Equal(last + 1, series.First().Item!.EpisodeNumber);
        }
    }

    [Fact]
    public void PoolWithBlocks_TheItemOnTheAirAcrossTheSnapshot_DoesNotLeadTheSeededPass()
    {
        // By day a family-hours rule leaves one series on the air. The new timeline's pick that is on the air when the
        // snapshot is taken plays out, so the seeded pass that follows must not open with that same item (seen
        // 2026-11-08: a Sequential pool previewed S01E01 twice in a row at the first slot).
        var pool = Items.SeriesRun("Test Show", 4, 22);
        pool.ForEach(e => e.OfficialRating = "PG");
        var late = new List<PlayableItem> { Items.Movie("Alpha", 22) };
        late.AddRange(Items.SeriesRun("Other", 2, 22));
        late.ForEach(i => i.OfficialRating = "R");
        pool.AddRange(late);
        var block = WeekdayBlock(PlayOrder.Sequential);
        var blockItems = new Dictionary<Guid, List<PlayableItem>> { [BlockId] = Items.SeriesRun("Seinfeld", 10, 22) };
        var rev1 = Items.PoolWithBlocksInput(pool, new List<SlotBlock> { block }, blockItems, PlayOrder.Sequential);
        var compiled = new CompiledStation { Pool = pool, Blocks = blockItems };

        var straddled = 0;
        for (var minute = 0; minute < 12 * 60; minute += 7)
        {
            var taken = Local(12, 8).AddMinutes(minute);
            var (_, snapshot) = Aired(rev1, compiled, taken);
            var station = Items.PoolStation(pool, PlayOrder.Sequential, 0, 2, true);
            station.Pool.DaypartRules = new List<DaypartRule> { new() { StartLocal = "06:00", EndLocal = "21:00", MaxRating = "PG" } };
            station.Slots = new SlotsConfig { Blocks = new List<SlotBlock> { block } };
            var rev2 = ScheduleInput.From(station, new CompiledStation { StationId = station.Id, Revision = 2, Pool = pool, Blocks = blockItems, History = snapshot });
            // What the new timeline shows from the snapshot on: the program on the air across it, then the seeded picks.
            var programs = Programs(rev2, taken.AddHours(-1), taken.AddHours(3), "Pool").Where(e => e.EndUtc > taken).ToList();
            var listing = string.Join(", ", programs.Select(e => e.Item!.Title + " E" + e.Item.EpisodeNumber + " " + e.StartUtc.ToString("HH:mm")));
            if (programs[0].StartUtc < taken)
            {
                straddled++;
            }

            for (var i = 1; i < programs.Count; i++)
            {
                Assert.True(programs[i - 1].Item!.ItemId != programs[i].Item!.ItemId, $"minute {minute} (snapshot {taken:HH:mm}): {listing}");
            }
        }

        Assert.True(straddled > 10, "the sweep never caught a program on the air across the snapshot");
    }

    [Fact]
    public void PoolWithBlocks_FirstPickAfterTheSnapshot_AvoidsTheLastKey_AndResolveMatchesEnumerate()
    {
        var films = Enumerable.Range(1, 6).Select(i => Items.Movie("Film " + i, 60)).ToList();
        var block = WeekdayBlock(PlayOrder.Sequential);
        var blockItems = new Dictionary<Guid, List<PlayableItem>> { [BlockId] = Items.SeriesRun("Seinfeld", 10, 22) };
        var rev1 = Items.PoolWithBlocksInput(films, new List<SlotBlock> { block }, blockItems, PlayOrder.Shuffle, avoid: true);
        var compiled = new CompiledStation { Pool = films, Blocks = blockItems };
        var hit = false;
        var checkedResolve = false;
        for (var hour = 1; hour < 240 && !hit; hour += 1)
        {
            var taken = Local(12, 0).AddHours(hour).AddMinutes(30);
            var (_, snapshot) = Aired(rev1, compiled, taken);
            var station = Items.PoolStation(films, PlayOrder.Shuffle, 0, 2, true);
            station.Slots = new SlotsConfig { Blocks = new List<SlotBlock> { block } };
            var rev2 = ScheduleInput.From(station, new CompiledStation { StationId = station.Id, Revision = 2, Pool = films, Blocks = blockItems, History = snapshot });
            var first = Programs(rev2, taken, taken.AddHours(12), "Pool").First(e => e.StartUtc >= taken);
            Assert.NotEqual(snapshot.LastKey, PoolOrdering.KeyOf(first.Item!));

            var plainStation = Items.PoolStation(films, PlayOrder.Shuffle, 0, 2, true);
            plainStation.Slots = new SlotsConfig { Blocks = new List<SlotBlock> { block } };
            var plain = ScheduleInput.From(plainStation, new CompiledStation { StationId = station.Id, Revision = 2, Pool = films, Blocks = blockItems });
            hit |= PoolOrdering.KeyOf(Programs(plain, taken, taken.AddHours(12), "Pool").First(e => e.StartUtc >= taken).Item!) == snapshot.LastKey;
            if (checkedResolve)
            {
                continue;
            }

            checkedResolve = true;

            // Resolve agrees with Enumerate across the snapshot and far past the week cache.
            var entries = ScheduleResolver.EnumerateInput(rev2, taken.AddDays(-1), taken.AddDays(2)).ToList();
            var rng = new Random(hour);
            for (var i = 0; i < 40; i++)
            {
                var t = taken.AddMinutes(rng.Next(-24 * 60, 2 * 24 * 60));
                Assert.Equal(entries.Single(e => e.Contains(t)).ProgramId, ScheduleResolver.ResolveInput(rev2, t)!.Entry.ProgramId);
            }

            var week30 = ScheduleResolver.ResolveInput(rev2, taken.AddDays(7 * 30))!.Entry;
            var back = ScheduleResolver.ResolveInput(rev2, taken.AddMinutes(5))!.Entry;
            Assert.Equal(entries.Single(e => e.Contains(taken.AddMinutes(5))).ProgramId, back.ProgramId);
            Assert.NotNull(week30);
        }

        Assert.True(hit, "no tried instant exercised the filler seam; widen the search");
    }
}
