using Jellyfin.Plugin.JellyStations.Models;
using Jellyfin.Plugin.JellyStations.Scheduling;
using Jellyfin.Plugin.JellyStations.Tests.Fixtures;
using Xunit;

namespace Jellyfin.Plugin.JellyStations.Tests.Scheduling;

/// <summary>A block's remainder stream may finish its last program past the block end (per-block option).</summary>
public class BlockOverrunTests
{
    private static readonly Guid MovieBlockId = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid LateBlockId = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly TimeZoneInfo NewYork = WeeklySlotScheduler.ResolveTimeZone("America/New_York");
    private static readonly List<DayOfWeek> EveryDay = Enum.GetValues<DayOfWeek>().ToList();

    private static SlotBlock Block(Guid id, string name, string start, int minutes, bool overrun) => new()
    {
        Id = id,
        Name = name,
        Days = EveryDay,
        StartLocal = start,
        DurationMinutes = minutes,
        Order = PlayOrder.Sequential,
        Overflow = SlotOverflow.FillerRemainder,
        RemainderMayOverrun = overrun
    };

    private static DateTime LocalToUtc(int d, int h, int min) => TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 10, d, h, min, 0, DateTimeKind.Unspecified), NewYork);

    /// <summary>18:00 block with one 84-minute movie; 22-minute episodes take the remainder, so the second straddles 20:00.</summary>
    private static ScheduleInput Input(bool overrun, SlotBlock? second = null)
    {
        var blocks = new List<SlotBlock> { Block(MovieBlockId, "Movie", "18:00", 120, overrun) };
        var items = new Dictionary<Guid, List<PlayableItem>> { [MovieBlockId] = new() { Items.Movie("Beauty and the Beast", 84) } };
        if (second is not null)
        {
            blocks.Add(second);
            items[second.Id] = new List<PlayableItem> { Items.Movie("Late Movie", 90) };
        }

        return Items.PoolWithBlocksInput(Items.SeriesRun("Korra", 40, 22), blocks, items, PlayOrder.Sequential, avoid: false);
    }

    private static List<ScheduleEntry> Evening(ScheduleInput input)
        => ScheduleResolver.EnumerateInput(input, LocalToUtc(13, 18, 0), LocalToUtc(13, 21, 0)).Where(e => e.Kind == EntryKind.Program).ToList();

    [Fact]
    public void Default_CutsThePoolItemAtTheBlockEnd()
    {
        var evening = Evening(Input(overrun: false));
        var straddler = evening.Single(e => e.StartUtc < LocalToUtc(13, 20, 0) && e.EndUtc > LocalToUtc(13, 19, 50) && e.SourceLabel == "Pool");
        Assert.True(straddler.IsTruncated);
        Assert.Equal(LocalToUtc(13, 20, 0), straddler.EndUtc);
    }

    [Fact]
    public void Overrun_LetsThePoolItemFinish_ThenThePoolContinues()
    {
        var evening = Evening(Input(overrun: true));
        var straddler = evening.Single(e => e.StartUtc < LocalToUtc(13, 20, 0) && e.EndUtc > LocalToUtc(13, 20, 0));
        Assert.Equal("Pool", straddler.SourceLabel);
        Assert.False(straddler.IsTruncated);
        Assert.Equal(TimeSpan.FromMinutes(22), straddler.Duration);
        Assert.Equal(LocalToUtc(13, 19, 46), straddler.StartUtc); // 18:00 + 84 min + 22 min
        Assert.Equal(LocalToUtc(13, 20, 8), straddler.EndUtc);

        // No gap and no cut after it: the pool simply carries on with the next episode.
        var next = evening.First(e => e.StartUtc >= straddler.EndUtc);
        Assert.Equal(straddler.EndUtc, next.StartUtc);
        Assert.Equal(straddler.Item!.EpisodeNumber + 1, next.Item!.EpisodeNumber);

        // The following evening's block still starts on time.
        var nextBlock = ScheduleResolver.ResolveInput(Input(overrun: true), LocalToUtc(14, 18, 0))!;
        Assert.Equal("Movie", nextBlock.Entry.SourceLabel);
        Assert.Equal(TimeSpan.Zero, nextBlock.Offset);
    }

    [Fact]
    public void Overrun_StillCuts_WhenAnotherBlockStartsAtTheBlockEnd()
    {
        var late = Block(LateBlockId, "Late", "20:00", 120, overrun: true);
        var evening = Evening(Input(overrun: true, late));
        var straddler = evening.Single(e => e.StartUtc < LocalToUtc(13, 20, 0) && e.EndUtc > LocalToUtc(13, 19, 50) && e.SourceLabel == "Pool");
        Assert.True(straddler.IsTruncated);
        Assert.Equal(LocalToUtc(13, 20, 0), straddler.EndUtc);

        var lateMovie = ScheduleResolver.ResolveInput(Input(overrun: true, late), LocalToUtc(13, 20, 0))!;
        Assert.Equal("Late", lateMovie.Entry.SourceLabel);
        Assert.Equal(TimeSpan.Zero, lateMovie.Offset);
    }
}
