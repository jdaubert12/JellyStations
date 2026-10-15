using Jellyfin.Plugin.TvStation.Models;
using Jellyfin.Plugin.TvStation.Scheduling;
using Xunit;

namespace Jellyfin.Plugin.TvStation.Tests.Scheduling;

public class SeasonWindowsTests
{
    private static Station WithWindows(string tz, params SeasonWindow[] windows)
        => new() { Name = "S", TimeZoneId = tz, ActiveWindows = windows.ToList() };

    private static readonly SeasonWindow Christmas = new() { StartMonth = 12, StartDay = 1, EndMonth = 1, EndDay = 6 };
    private static readonly SeasonWindow October = new() { StartMonth = 10, StartDay = 1, EndMonth = 10, EndDay = 31 };

    [Fact]
    public void NoWindows_IsAlwaysActive()
    {
        var s = WithWindows("UTC");
        Assert.False(SeasonWindows.IsSeasonal(s));
        Assert.True(SeasonWindows.IsActive(s, new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc)));
        Assert.Null(SeasonWindows.NextChange(s, DateTime.UtcNow));
    }

    [Theory]
    [InlineData(10, 1, true)]
    [InlineData(10, 31, true)]
    [InlineData(9, 30, false)]
    [InlineData(11, 1, false)]
    public void SimpleRange_IsInclusive(int month, int day, bool expected)
    {
        var s = WithWindows("UTC", October);
        Assert.Equal(expected, SeasonWindows.IsActive(s, new DateTime(2026, month, day, 12, 0, 0, DateTimeKind.Utc)));
    }

    [Theory]
    [InlineData(12, 1, true)]
    [InlineData(12, 25, true)]
    [InlineData(1, 6, true)]
    [InlineData(1, 7, false)]
    [InlineData(11, 30, false)]
    [InlineData(7, 4, false)]
    public void WrappingRange_CrossesNewYear(int month, int day, bool expected)
    {
        var s = WithWindows("UTC", Christmas);
        Assert.Equal(expected, SeasonWindows.IsActive(s, new DateTime(2026, month, day, 12, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void UsesStationTimeZone_NotUtc()
    {
        // 03:00 UTC on Jan 7 is still 22:00 on Jan 6 in New York, so the Christmas window is still open there.
        var utc = new DateTime(2027, 1, 7, 3, 0, 0, DateTimeKind.Utc);
        Assert.True(SeasonWindows.IsActive(WithWindows("America/New_York", Christmas), utc));
        Assert.False(SeasonWindows.IsActive(WithWindows("UTC", Christmas), utc));
    }

    [Fact]
    public void MultipleWindows_AnyMatch()
    {
        var s = WithWindows("UTC", October, Christmas);
        Assert.True(SeasonWindows.IsActive(s, new DateTime(2026, 10, 14, 0, 0, 0, DateTimeKind.Utc)));
        Assert.True(SeasonWindows.IsActive(s, new DateTime(2026, 12, 24, 0, 0, 0, DateTimeKind.Utc)));
        Assert.False(SeasonWindows.IsActive(s, new DateTime(2026, 11, 14, 0, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void NextChange_ReportsNextFlipDate()
    {
        var s = WithWindows("UTC", Christmas);
        Assert.Equal(new DateOnly(2026, 12, 1), SeasonWindows.NextChange(s, new DateTime(2026, 10, 14, 0, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(new DateOnly(2027, 1, 7), SeasonWindows.NextChange(s, new DateTime(2026, 12, 20, 0, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void Validation_RejectsImpossibleDates()
    {
        Assert.True(SeasonWindows.IsValid(new SeasonWindow { StartMonth = 2, StartDay = 29, EndMonth = 3, EndDay = 1 }));
        Assert.False(SeasonWindows.IsValid(new SeasonWindow { StartMonth = 2, StartDay = 30, EndMonth = 3, EndDay = 1 }));
        Assert.False(SeasonWindows.IsValid(new SeasonWindow { StartMonth = 13, StartDay = 1, EndMonth = 1, EndDay = 1 }));
        Assert.False(SeasonWindows.IsValid(new SeasonWindow { StartMonth = 4, StartDay = 31, EndMonth = 5, EndDay = 1 }));
    }
}
