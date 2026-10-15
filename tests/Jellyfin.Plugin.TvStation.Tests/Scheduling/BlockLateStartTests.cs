using Jellyfin.Plugin.TvStation.Models;
using Jellyfin.Plugin.TvStation.Scheduling;
using Jellyfin.Plugin.TvStation.Tests.Fixtures;
using Xunit;

namespace Jellyfin.Plugin.TvStation.Tests.Scheduling;

/// <summary>A block may start late so the program already playing can finish (per-block allowance).</summary>
public class BlockLateStartTests
{
    private static readonly Guid MovieBlockId = Guid.Parse("88888888-8888-8888-8888-888888888888");
    private static readonly Guid LateBlockId = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly TimeZoneInfo NewYork = WeeklySlotScheduler.ResolveTimeZone("America/New_York");
    private static readonly List<DayOfWeek> EveryDay = Enum.GetValues<DayOfWeek>().ToList();

    private static SlotBlock Block(Guid id, string name, string start, int minutes, int delay) => new()
    {
        Id = id,
        Name = name,
        Days = EveryDay,
        StartLocal = start,
        DurationMinutes = minutes,
        Order = PlayOrder.Sequential,
        Overflow = SlotOverflow.FillerRemainder,
        MaxStartDelayMinutes = delay
    };

    private static DateTime At(int d, int h, int min) => TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 10, d, h, min, 0, DateTimeKind.Unspecified), NewYork);

    /// <summary>
    /// Anchor Monday 00:00; 22-minute episodes in order, so episode 50 runs 17:58 to 18:20 across the 18:00 block
    /// start. The block holds one 84-minute movie.
    /// </summary>
    private static ScheduleInput Input(int delay, SlotBlock? second = null)
    {
        var blocks = new List<SlotBlock> { Block(MovieBlockId, "Movie", "18:00", 120, delay) };
        var items = new Dictionary<Guid, List<PlayableItem>> { [MovieBlockId] = new() { Items.Movie("Beauty and the Beast", 84) } };
        if (second is not null)
        {
            blocks.Add(second);
            items[second.Id] = new List<PlayableItem> { Items.Movie("Late Movie", 90) };
        }

        return Items.PoolWithBlocksInput(Items.SeriesRun("Korra", 60, 22), blocks, items, PlayOrder.Sequential, avoid: false);
    }

    /// <summary>Only two minutes are left before the block, too little to cut an episode into, so the card shows.</summary>
    private static void AssertCardThenBlockOnTime(ScheduleInput input)
    {
        var before = ScheduleResolver.ResolveInput(input, At(12, 17, 59))!.Entry;
        Assert.Equal(EntryKind.Slate, before.Kind);
        Assert.Equal(At(12, 17, 58), before.StartUtc);
        Assert.Equal(At(12, 18, 0), before.EndUtc);

        var block = ScheduleResolver.ResolveInput(input, At(12, 18, 0))!;
        Assert.Equal("Movie", block.Entry.SourceLabel);
        Assert.Equal(TimeSpan.Zero, block.Offset);
    }

    [Fact]
    public void NoAllowance_BlockStartsOnTime() => AssertCardThenBlockOnTime(Input(delay: 0));

    [Fact]
    public void TooSmallAnAllowance_BlockStartsOnTime() => AssertCardThenBlockOnTime(Input(delay: 10));

    [Fact]
    public void Allowance_LetsTheProgramFinish_BlockStartsLateAndKeepsItsDuration()
    {
        var input = Input(delay: 30);

        var episode = ScheduleResolver.ResolveInput(input, At(12, 18, 5))!.Entry;
        Assert.Equal("Pool", episode.SourceLabel);
        Assert.False(episode.IsTruncated);
        Assert.Equal(50, episode.Item!.EpisodeNumber);
        Assert.Equal(At(12, 17, 58), episode.StartUtc);
        Assert.Equal(At(12, 18, 20), episode.EndUtc);

        var movie = ScheduleResolver.ResolveInput(input, At(12, 18, 20))!;
        Assert.Equal("Movie", movie.Entry.SourceLabel);
        Assert.Equal(TimeSpan.Zero, movie.Offset);
        Assert.Equal(At(12, 19, 44), movie.Entry.EndUtc);

        // The block kept its two hours (18:20 to 20:20): the pool fills the remainder and is cut at the shifted end.
        var tail = ScheduleResolver.ResolveInput(input, At(12, 20, 10))!.Entry;
        Assert.Equal("Pool", tail.SourceLabel);
        Assert.True(tail.IsTruncated);
        Assert.Equal(At(12, 20, 20), tail.EndUtc);

        // Next day's block still exists at its time (give or take its own allowance).
        var tuesday = ScheduleResolver.ResolveInput(input, At(13, 18, 35))!;
        Assert.Equal("Movie", tuesday.Entry.SourceLabel);
    }

    [Fact]
    public void LateStart_NeverPushesIntoTheNextBlock()
    {
        var late = Block(LateBlockId, "Late", "20:00", 120, delay: 0);
        var input = Input(delay: 30, late);

        var movie = ScheduleResolver.ResolveInput(input, At(12, 18, 20))!;
        Assert.Equal("Movie", movie.Entry.SourceLabel);

        var lateMovie = ScheduleResolver.ResolveInput(input, At(12, 20, 0))!;
        Assert.Equal("Late", lateMovie.Entry.SourceLabel);
        Assert.Equal(TimeSpan.Zero, lateMovie.Offset);
    }
}
