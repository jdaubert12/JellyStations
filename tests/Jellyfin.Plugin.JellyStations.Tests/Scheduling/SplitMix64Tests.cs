using Jellyfin.Plugin.JellyStations.Scheduling;
using Xunit;

namespace Jellyfin.Plugin.JellyStations.Tests.Scheduling;

public class SplitMix64Tests
{
    [Fact]
    public void SameSeed_SameSequence()
    {
        var a = new SplitMix64(42);
        var b = new SplitMix64(42);
        for (var i = 0; i < 100; i++)
        {
            Assert.Equal(a.NextUInt64(), b.NextUInt64());
        }

        Assert.NotEqual(new SplitMix64(1).NextUInt64(), new SplitMix64(2).NextUInt64());
    }

    [Fact]
    public void Permutation_IsDeterministicAndComplete()
    {
        var a = SplitMix64.Permutation(20, 12345);
        var b = SplitMix64.Permutation(20, 12345);
        var c = SplitMix64.Permutation(20, 54321);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.Equal(Enumerable.Range(0, 20), a.OrderBy(x => x));
    }

    [Fact]
    public void Seed_DependsOnEveryComponent()
    {
        var g = Guid.NewGuid();
        var s0 = StableHash.Seed(g, 1, "pool", 0);
        Assert.NotEqual(s0, StableHash.Seed(g, 2, "pool", 0));
        Assert.NotEqual(s0, StableHash.Seed(g, 1, "filler", 0));
        Assert.NotEqual(s0, StableHash.Seed(g, 1, "pool", 1));
        Assert.NotEqual(s0, StableHash.Seed(Guid.NewGuid(), 1, "pool", 0));
        Assert.Equal(s0, StableHash.Seed(g, 1, "pool", 0));
    }
}
