using Jellyfin.Plugin.JellyStations.Scheduling;
using Xunit;

namespace Jellyfin.Plugin.JellyStations.Tests.Scheduling;

public class AdjacencyTests
{
    [Fact]
    public void Spread_KeepsEqualKeysApart_WhenPossible()
    {
        var list = new List<string> { "a", "a", "b", "b", "c" };
        Adjacency.Spread(list, s => s);
        for (var i = 1; i < list.Count; i++)
        {
            Assert.NotEqual(list[i - 1], list[i]);
        }

        Assert.True(Adjacency.FixSeam(list, s => s, list[0]));
        Assert.False(Adjacency.FixSeam(list, s => s, "z"));
    }
}
