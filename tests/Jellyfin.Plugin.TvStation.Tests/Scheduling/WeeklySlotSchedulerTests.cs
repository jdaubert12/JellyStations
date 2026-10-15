using Jellyfin.Plugin.TvStation.Models;
using Jellyfin.Plugin.TvStation.Scheduling;
using Jellyfin.Plugin.TvStation.Tests.Fixtures;
using Xunit;

namespace Jellyfin.Plugin.TvStation.Tests.Scheduling;

public class WeeklySlotSchedulerTests
{
    private static readonly Guid BlockId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly TimeZoneInfo NewYork = WeeklySlotScheduler.ResolveTimeZone("America/New_York");

    private static SlotBlock WeekdayBlock(string start = "20:00", int minutes = 60, PlayOrder order = PlayOrder.Sequential, SlotOverflow overflow = SlotOverflow.FillerRemainder)
    {
        return new SlotBlock
        {
            Id = BlockId,
            Name = "Prime",
            Days = new List<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday },
            StartLocal = start,
            DurationMinutes = minutes,
            Order = order,
            Overflow = overflow
        };
    }

    private static ScheduleInput Standard(PlayOrder blockOrder = PlayOrder.Sequential, SlotOverflow overflow = SlotOverflow.FillerRemainder, int blockMinutes = 60)
    {
        var block = WeekdayBlock(minutes: blockMinutes, order: blockOrder, overflow: overflow);
        var blockItems = new Dictionary<Guid, List<PlayableItem>> { [BlockId] = Items.SeriesRun("Seinfeld", 10, 22) };
        var filler = Items.SeriesRun("Cartoons", 30, 11);
        return Items.SlotsInput(new List<SlotBlock> { block }, blockItems, filler);
    }

    private static DateTime LocalToUtc(int y, int m, int d, int h, int min)
    {
        return TimeZoneInfo.ConvertTimeToUtc(new DateTime(y, m, d, h, min, 0, DateTimeKind.Unspecified), NewYork);
    }

    [Fact]
    public void Block_StartsAtLocalTime_OnListedDays()
    {
        var input = Standard();
        var mondayStart = LocalToUtc(2026, 10, 12, 20, 0);
        var r = ScheduleResolver.ResolveInput(input, mondayStart)!;
        Assert.Equal("Seinfeld", r.Entry.Item!.Title);
        Assert.Equal(1, r.Entry.Item.EpisodeNumber);
        Assert.Equal(TimeSpan.Zero, r.Offset);
        Assert.Equal("Prime", r.Entry.SourceLabel);

        var saturday = ScheduleResolver.ResolveInput(input, LocalToUtc(2026, 10, 17, 20, 30))!;
        Assert.Equal("Cartoons", saturday.Entry.Item!.Title);
    }

    [Fact]
    public void ShuffleEpisodesInOrderBlock_KeepsEachSeriesInOrderWithinAnOccurrence()
    {
        // One series: the shuffle collapses to episode order, so Monday opens with episodes 1 and 2.
        var single = Standard(PlayOrder.ShuffleEpisodesInOrder);
        Assert.Equal(1, ScheduleResolver.ResolveInput(single, LocalToUtc(2026, 10, 12, 20, 0))!.Entry.Item!.EpisodeNumber);
        Assert.Equal(2, ScheduleResolver.ResolveInput(single, LocalToUtc(2026, 10, 12, 20, 30))!.Entry.Item!.EpisodeNumber);

        // Two series in a 3-hour block: each occurrence mixes them, but every series' episodes ascend.
        var block = WeekdayBlock(minutes: 180, order: PlayOrder.ShuffleEpisodesInOrder);
        var items = Items.SeriesRun("Seinfeld", 4, 22);
        items.AddRange(Items.SeriesRun("Frasier", 4, 22));
        var input = Items.SlotsInput(new List<SlotBlock> { block }, new Dictionary<Guid, List<PlayableItem>> { [BlockId] = items }, Items.SeriesRun("Cartoons", 30, 11));
        var sawBothSeries = false;
        for (var day = 12; day <= 16; day++)
        {
            var start = LocalToUtc(2026, 10, day, 20, 0);
            var occurrence = ScheduleResolver.EnumerateInput(input, start, start.AddHours(3))
                .Where(e => e.Kind == EntryKind.Program && e.SourceLabel == "Prime" && e.StartUtc >= start)
                .ToList();
            Assert.Equal(8, occurrence.Count);
            var bySeries = occurrence.GroupBy(e => e.Item!.SeriesId).ToList();
            sawBothSeries |= bySeries.Count == 2 && occurrence[0].Item!.SeriesId != occurrence[1].Item!.SeriesId;
            foreach (var series in bySeries)
            {
                var episodes = series.Select(e => e.Item!.EpisodeNumber!.Value).ToList();
                Assert.Equal(episodes.OrderBy(n => n), episodes);
            }
        }

        Assert.True(sawBothSeries);
    }

    [Fact]
    public void SequentialBlock_AdvancesEpisodesAcrossDays()
    {
        var input = Standard();
        var mon = ScheduleResolver.ResolveInput(input, LocalToUtc(2026, 10, 12, 20, 0))!.Entry.Item!;
        var mon2 = ScheduleResolver.ResolveInput(input, LocalToUtc(2026, 10, 12, 20, 30))!.Entry.Item!;
        var tue = ScheduleResolver.ResolveInput(input, LocalToUtc(2026, 10, 13, 20, 0))!.Entry.Item!;
        Assert.Equal(1, mon.EpisodeNumber);
        Assert.Equal(2, mon2.EpisodeNumber);
        Assert.Equal(3, tue.EpisodeNumber);

        // 60-minute block fits two 22-minute episodes, so a week later we are at episode 11 -> wraps to 1.
        var nextMon = ScheduleResolver.ResolveInput(input, LocalToUtc(2026, 10, 19, 20, 0))!.Entry.Item!;
        Assert.Equal(1, nextMon.EpisodeNumber);
    }

    [Fact]
    public void FillerRemainder_FillsTailOfBlockWithFiller()
    {
        var input = Standard();
        var entries = ScheduleResolver.EnumerateInput(input, LocalToUtc(2026, 10, 12, 20, 0), LocalToUtc(2026, 10, 12, 21, 0));
        var programs = entries.Where(e => e.Kind == EntryKind.Program).ToList();
        Assert.Equal("Seinfeld", programs[0].Item!.Title);
        Assert.Equal("Seinfeld", programs[1].Item!.Title);
        Assert.Equal("Cartoons", programs[2].Item!.Title);
        Assert.Equal(LocalToUtc(2026, 10, 12, 20, 44), programs[2].StartUtc);
    }

    [Fact]
    public void TruncateLast_CutsItemAtBlockEnd()
    {
        var input = Standard(overflow: SlotOverflow.TruncateLast);
        var entries = ScheduleResolver.EnumerateInput(input, LocalToUtc(2026, 10, 12, 20, 0), LocalToUtc(2026, 10, 12, 21, 0));
        var last = entries.Last(e => e.StartUtc < LocalToUtc(2026, 10, 12, 21, 0));
        Assert.Equal("Seinfeld", last.Item!.Title);
        Assert.True(last.IsTruncated);
        Assert.Equal(LocalToUtc(2026, 10, 12, 21, 0), last.EndUtc);
        Assert.Equal(TimeSpan.FromMinutes(16), last.Duration);
    }

    [Fact]
    public void Enumerate_TwoWeeks_IsContiguous()
    {
        var input = Standard();
        var from = Items.Anchor.AddHours(3);
        var to = from.AddDays(14);
        var entries = ScheduleResolver.EnumerateInput(input, from, to);
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
        var input = Standard(blockOrder: PlayOrder.Shuffle);
        var from = Items.Anchor.AddDays(-3);
        var to = Items.Anchor.AddDays(40);
        var entries = ScheduleResolver.EnumerateInput(input, from, to);
        var rng = new Random(11);
        for (var n = 0; n < 500; n++)
        {
            var t = from.AddSeconds(rng.NextDouble() * (to - from).TotalSeconds);
            var r = ScheduleResolver.ResolveInput(input, t)!;
            var expected = entries.Single(e => e.Contains(t));
            Assert.Equal(expected, r.Entry);
        }
    }

    [Fact]
    public void DstTransition_BlockStillStartsAtLocalEight()
    {
        var input = Standard();
        // US DST ends Sunday 2026-11-01. Friday before and Monday after.
        var before = LocalToUtc(2026, 10, 30, 20, 0);
        var after = LocalToUtc(2026, 11, 2, 20, 0);
        Assert.Equal(TimeSpan.Zero, ScheduleResolver.ResolveInput(input, before)!.Offset);
        Assert.Equal(TimeSpan.Zero, ScheduleResolver.ResolveInput(input, after)!.Offset);
        Assert.Equal("Seinfeld", ScheduleResolver.ResolveInput(input, after.AddMinutes(1))!.Entry.Item!.Title);
        Assert.Equal("Cartoons", ScheduleResolver.ResolveInput(input, after.AddMinutes(-1))!.Entry.Item!.Title);
    }

    [Fact]
    public void MidnightCrossingBlock_Resolves()
    {
        var block = WeekdayBlock(start: "23:30", minutes: 90);
        var blockItems = new Dictionary<Guid, List<PlayableItem>> { [BlockId] = Items.SeriesRun("LateShow", 5, 44) };
        var input = Items.SlotsInput(new List<SlotBlock> { block }, blockItems, Items.SeriesRun("Cartoons", 5, 11));
        var r = ScheduleResolver.ResolveInput(input, LocalToUtc(2026, 10, 13, 0, 30))!;
        Assert.Equal("LateShow", r.Entry.Item!.Title);
    }

    [Fact]
    public void EmptyBlockAndEmptyFiller_YieldSlates()
    {
        var block = WeekdayBlock();
        var input = Items.SlotsInput(new List<SlotBlock> { block }, new Dictionary<Guid, List<PlayableItem>>(), new List<PlayableItem>());
        var entries = ScheduleResolver.EnumerateInput(input, Items.Anchor, Items.Anchor.AddDays(2));
        Assert.NotEmpty(entries);
        Assert.All(entries, e => Assert.Equal(EntryKind.Slate, e.Kind));
    }

    [Fact]
    public void IsDeterministicAcrossInstances()
    {
        var a = ScheduleResolver.EnumerateInput(Standard(PlayOrder.Shuffle), Items.Anchor, Items.Anchor.AddDays(10)).Select(e => (e.StartUtc, e.Item?.ItemId)).ToList();
        var b = ScheduleResolver.EnumerateInput(Standard(PlayOrder.Shuffle), Items.Anchor, Items.Anchor.AddDays(10)).Select(e => (e.StartUtc, e.Item?.ItemId)).ToList();
        Assert.Equal(a, b);
    }
}
