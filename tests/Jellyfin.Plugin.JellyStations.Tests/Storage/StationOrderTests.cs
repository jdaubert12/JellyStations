using Jellyfin.Plugin.JellyStations.Models;
using Jellyfin.Plugin.JellyStations.Storage;
using Xunit;

namespace Jellyfin.Plugin.JellyStations.Tests.Storage;

public class StationOrderTests
{
    private static Station S(string channel, string name = "x") => new() { ChannelNumber = channel, Name = name };

    [Fact]
    public void ByChannel_SortsNumerically_DecimalsBetween_NonNumericLast_ThenByName()
    {
        var stations = new[] { S("10"), S("News"), S("2.1"), S("2", "b"), S("2", "a"), S("3"), S("1") };
        var sorted = stations.OrderBy(s => s, StationOrder.ByChannel).Select(s => s.ChannelNumber + s.Name).ToList();
        Assert.Equal(new[] { "1x", "2a", "2b", "2.1x", "3x", "10x", "Newsx" }, sorted);
    }
}
