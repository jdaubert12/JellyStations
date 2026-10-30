using Jellyfin.Plugin.JellyStations.Weather;
using Jellyfin.Plugin.JellyStations.Weather.Radar;
using Xunit;

namespace Jellyfin.Plugin.JellyStations.Tests.Weather;

/// <summary>The polling schedule the weather, alert and radar services share, and the small helpers around their fetches.</summary>
public class FetchBackoffTests
{
    private static readonly DateTime Now = new(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    [Fact]
    public void NeverAttempted_DueWithoutACachedCopy_OrWithAnOldOne()
    {
        var status = new RadarFetchStatus();
        Assert.True(FetchBackoff.IsDue(status, () => null, Interval, Now));
        Assert.True(FetchBackoff.IsDue(status, () => Now - Interval, Interval, Now));
        Assert.False(FetchBackoff.IsDue(status, () => Now.AddMinutes(-4), Interval, Now));
    }

    [Fact]
    public void AfterAnAttempt_TheCachedCopyIsNotAskedFor()
    {
        var status = new RadarFetchStatus { LastAttemptUtc = Now.AddMinutes(-1) };
        Assert.False(FetchBackoff.IsDue(status, () => throw new InvalidOperationException("not asked"), Interval, Now));
    }

    [Fact]
    public void AfterASuccess_DueEveryInterval()
    {
        var status = new RadarFetchStatus { LastAttemptUtc = Now.AddMinutes(-4) };
        Assert.False(FetchBackoff.IsDue(status, () => null, Interval, Now));
        Assert.True(FetchBackoff.IsDue(status, () => null, Interval, Now.AddMinutes(1)));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(4, 8)]
    [InlineData(5, 15)]
    [InlineData(40, 15)]
    public void AfterFailures_BacksOff(int failures, int minutes)
    {
        Assert.Equal(TimeSpan.FromMinutes(minutes), FetchBackoff.Delay(failures));
        var status = new RadarFetchStatus { LastAttemptUtc = Now, Failures = failures };
        Assert.False(FetchBackoff.IsDue(status, () => null, TimeSpan.FromHours(1), Now.AddMinutes(minutes).AddSeconds(-1)));
        Assert.True(FetchBackoff.IsDue(status, () => null, TimeSpan.FromHours(1), Now.AddMinutes(minutes)));
    }

    [Fact]
    public void MarkSuccess_ClearsTheFailures_MarkFailure_CountsThem()
    {
        var status = new LocationFetchStatus();
        status.MarkFailure("timeout");
        status.MarkFailure("503");
        Assert.Equal(2, status.Failures);
        Assert.Equal("503", status.LastError);
        Assert.Null(status.LastSuccessUtc);

        status.MarkSuccess(Now);
        Assert.Equal(0, status.Failures);
        Assert.Null(status.LastError);
        Assert.Equal(Now, status.LastSuccessUtc);
    }

    [Theory]
    [InlineData("{\"reason\":\"Latitude must be in range\"}", "Latitude must be in range")]
    [InlineData("{\"detail\":\"x\"}", null)]
    [InlineData("<html>Bad gateway</html>", null)]
    [InlineData("", null)]
    public void ErrorBodies_ReadTheirReason(string body, string? expected)
    {
        Assert.Equal(expected, WeatherHttp.TryReadJsonString(body, "reason"));
    }

    [Fact]
    public void DayClock_NamesTheDayOnlyWhenItIsNotToday()
    {
        var today = new DateTime(2026, 10, 29, 9, 0, 0);
        Assert.Equal("4:00 PM", Units.DayClock(new DateTime(2026, 10, 29, 16, 0, 0), today, false));
        Assert.Equal("Fri 16:00", Units.DayClock(new DateTime(2026, 10, 30, 16, 0, 0), today, true));
    }
}
