using Jellyfin.Plugin.JellyStations.Models;
using Jellyfin.Plugin.JellyStations.Scheduling;
using Jellyfin.Plugin.JellyStations.Tests.Fixtures;
using Xunit;

namespace Jellyfin.Plugin.JellyStations.Tests.Scheduling;

public class BlockRecurrenceTests
{
    private static readonly Guid BlockId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly TimeZoneInfo NewYork = WeeklySlotScheduler.ResolveTimeZone("America/New_York");

    private static DateTime LocalToUtc(int y, int m, int d, int h, int min)
        => TimeZoneInfo.ConvertTimeToUtc(new DateTime(y, m, d, h, min, 0, DateTimeKind.Unspecified), NewYork);

    private static SlotBlock Weekend(BlockRecurrence recurrence, int minutes = 240)
    {
        return new SlotBlock
        {
            Id = BlockId,
            Name = "Marathon",
            Days = new List<DayOfWeek> { DayOfWeek.Saturday, DayOfWeek.Sunday },
            StartLocal = "20:00",
            DurationMinutes = minutes,
            Order = PlayOrder.Sequential,
            Overflow = SlotOverflow.FillerRemainder,
            Recurrence = recurrence
        };
    }

    private static ScheduleInput Input(SlotBlock block)
    {
        var films = new List<PlayableItem> { Items.Movie("Film 1", 100), Items.Movie("Film 2", 100), Items.Movie("Film 3", 100), Items.Movie("Film 4", 100) };
        var blockItems = new Dictionary<Guid, List<PlayableItem>> { [BlockId] = films };
        return Items.SlotsInput(new List<SlotBlock> { block }, blockItems, Items.SeriesRun("Cartoons", 40, 11));
    }

    private static string TitleAt(ScheduleInput input, int y, int m, int d, int h, int min)
        => ScheduleResolver.ResolveInput(input, LocalToUtc(y, m, d, h, min))!.Entry.Item!.Title;

    [Fact]
    public void Rule_WeeksRunMondayToSunday_AndBelongToTheMonthOfTheirMonday()
    {
        // November 2026 starts on a Sunday: that Sunday closes October's last week; the first weekend of November is the 7th and 8th.
        Assert.Equal(new DateTime(2026, 11, 2), RecurrenceRule.MondayOf(new DateTime(2026, 11, 7)));
        Assert.Equal(new DateTime(2026, 10, 26), RecurrenceRule.MondayOf(new DateTime(2026, 11, 1)));
        Assert.Equal(new DateTime(2026, 10, 26), RecurrenceRule.MondayOf(new DateTime(2026, 10, 26)));

        Assert.Equal(1, RecurrenceRule.WeekOfMonth(new DateTime(2026, 11, 2)));
        Assert.Equal(4, RecurrenceRule.WeekOfMonth(new DateTime(2026, 10, 26)));
        Assert.True(RecurrenceRule.IsLastWeekOfMonth(new DateTime(2026, 10, 26)));
        Assert.False(RecurrenceRule.IsLastWeekOfMonth(new DateTime(2026, 10, 19)));
        Assert.True(RecurrenceRule.IsLastWeekOfMonth(new DateTime(2026, 11, 30)));
        Assert.Equal(5, RecurrenceRule.WeekOfMonth(new DateTime(2026, 11, 30)));

        Assert.Equal(new DateTime(2026, 11, 14), RecurrenceRule.ParseDate("2026-11-14"));
        Assert.Null(RecurrenceRule.ParseDate("14/11/2026"));
        Assert.Null(RecurrenceRule.ParseDate(null));
    }

    [Fact]
    public void EveryNWeeks_CountsFromTheWeekOfTheStartDate_InBothDirections()
    {
        var block = Weekend(BlockRecurrence.EveryNWeeks);
        block.IntervalWeeks = 2;
        block.RecurrenceStart = "2026-10-21"; // a Wednesday in the week of Sat Oct 24
        var input = Input(block);

        Assert.Equal("Cartoons", TitleAt(input, 2026, 10, 17, 20, 30));
        Assert.Equal("Film 1", TitleAt(input, 2026, 10, 24, 20, 30));
        Assert.Equal("Film 3", TitleAt(input, 2026, 10, 25, 20, 30)); // Sunday continues where Saturday stopped
        Assert.Equal("Cartoons", TitleAt(input, 2026, 10, 31, 20, 30));
        Assert.Equal("Cartoons", TitleAt(input, 2026, 11, 1, 20, 30));
        Assert.Equal("Film 1", TitleAt(input, 2026, 11, 7, 20, 30)); // the lineup wrapped: 4 films, 2 per night
        Assert.Equal("Cartoons", TitleAt(input, 2026, 11, 14, 20, 30));
        Assert.Equal("Film 1", TitleAt(input, 2026, 11, 21, 20, 30));
    }

    [Fact]
    public void WeeksOfMonth_FirstWeek_KeepsTheWeekendTogether()
    {
        var block = Weekend(BlockRecurrence.WeeksOfMonth);
        block.WeeksOfMonth = new List<int> { 1 };
        var input = Input(block);

        Assert.Equal("Cartoons", TitleAt(input, 2026, 11, 1, 20, 30)); // Sunday Nov 1 belongs to October's last week
        Assert.Equal("Film 1", TitleAt(input, 2026, 11, 7, 20, 30));
        Assert.Equal("Film 3", TitleAt(input, 2026, 11, 8, 20, 30));
        Assert.Equal("Cartoons", TitleAt(input, 2026, 11, 14, 20, 30));
        Assert.Equal("Cartoons", TitleAt(input, 2026, 11, 28, 20, 30));
        Assert.Equal("Film 1", TitleAt(input, 2026, 12, 12, 20, 30)); // December's first Monday is the 7th
        Assert.Equal("Cartoons", TitleAt(input, 2026, 12, 5, 20, 30));
    }

    [Fact]
    public void WeeksOfMonth_Last_IsTheWeekOfTheLastMonday()
    {
        var block = Weekend(BlockRecurrence.WeeksOfMonth);
        block.WeeksOfMonth = new List<int> { 5 };
        var input = Input(block);

        Assert.Equal("Film 1", TitleAt(input, 2026, 10, 31, 20, 30));
        Assert.Equal("Film 3", TitleAt(input, 2026, 11, 1, 20, 30));
        Assert.Equal("Cartoons", TitleAt(input, 2026, 11, 7, 20, 30));
        Assert.Equal("Cartoons", TitleAt(input, 2026, 11, 28, 20, 30)); // Nov 30 is the last Monday; Nov 28 is in the week before
        Assert.Equal("Film 1", TitleAt(input, 2026, 12, 5, 20, 30));
    }

    [Fact]
    public void Dates_IgnoreTheDayPicker_AndContinueTheLineupAcrossListedDates()
    {
        var block = Weekend(BlockRecurrence.Dates);
        block.Days = new List<DayOfWeek>();
        block.Dates = new List<string> { "2026-11-14", "2026-11-15", "not a date", "2026-12-05" };
        var input = Input(block);

        Assert.Equal("Cartoons", TitleAt(input, 2026, 11, 7, 20, 30));
        Assert.Equal("Film 1", TitleAt(input, 2026, 11, 14, 20, 30));
        Assert.Equal("Film 2", TitleAt(input, 2026, 11, 14, 21, 45));
        Assert.Equal("Cartoons", TitleAt(input, 2026, 11, 14, 23, 30)); // Film 3 does not fit the last 40 minutes
        Assert.Equal("Film 3", TitleAt(input, 2026, 11, 15, 20, 30));
        Assert.Equal("Cartoons", TitleAt(input, 2026, 11, 21, 20, 30));
        Assert.Equal("Film 1", TitleAt(input, 2026, 12, 5, 20, 30));

        var entries = ScheduleResolver.EnumerateInput(input, LocalToUtc(2026, 11, 13, 0, 0), LocalToUtc(2026, 11, 17, 0, 0)).ToList();
        for (var i = 1; i < entries.Count; i++)
        {
            Assert.Equal(entries[i - 1].EndUtc, entries[i].StartUtc);
        }

        Assert.Equal(4, entries.Count(e => e.SourceLabel == "Marathon"));
    }

    [Fact]
    public void Weekly_IsUnchanged_ByTheNewFields()
    {
        var plain = Weekend(BlockRecurrence.Weekly);
        var noisy = Weekend(BlockRecurrence.Weekly);
        noisy.IntervalWeeks = 3;
        noisy.WeeksOfMonth = new List<int> { 2 };
        noisy.Dates = new List<string> { "2026-11-14" };
        noisy.RecurrenceStart = "2026-01-01";

        var a = ScheduleResolver.EnumerateInput(Input(plain), Items.Anchor, Items.Anchor.AddDays(28)).Select(e => (e.Item?.ItemId, e.StartUtc, e.EndUtc));
        var b = ScheduleResolver.EnumerateInput(Input(noisy), Items.Anchor, Items.Anchor.AddDays(28)).Select(e => (e.Item?.ItemId, e.StartUtc, e.EndUtc));
        Assert.Equal(a, b);
        Assert.Equal("Film 1", TitleAt(Input(plain), 2026, 10, 17, 20, 30));
        Assert.Equal("Film 1", TitleAt(Input(plain), 2026, 10, 31, 20, 30));
    }
}
