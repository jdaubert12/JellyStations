using Jellyfin.Plugin.JellyStations.Scheduling;
using Xunit;

namespace Jellyfin.Plugin.JellyStations.Tests.Scheduling;

public class TimeZonesTests
{
    [Fact]
    public void TryFind_KnownId_ReturnsZone_UnknownOrBlank_ReturnsNull()
    {
        Assert.Equal("America/New_York", TimeZones.TryFind("America/New_York")?.Id);
        Assert.Null(TimeZones.TryFind("Nowhere/Atlantis"));
        Assert.Null(TimeZones.TryFind(" "));
        Assert.Null(TimeZones.TryFind(null));
    }

    [Fact]
    public void Resolve_FallsBackToUtc()
    {
        Assert.Equal(TimeZoneInfo.Utc, TimeZones.Resolve("Nowhere/Atlantis"));
        Assert.Equal(TimeZoneInfo.Utc, TimeZones.Resolve(null));
    }
}
