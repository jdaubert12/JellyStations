using Jellyfin.Plugin.JellyStations.Models;
using Jellyfin.Plugin.JellyStations.Scheduling;
using Jellyfin.Plugin.JellyStations.Tests.Fixtures;
using Xunit;

namespace Jellyfin.Plugin.JellyStations.Tests.Scheduling;

public class ScheduleResolverTests
{
    [Fact]
    public void UnknownStation_ReturnsNullAndEmpty()
    {
        var resolver = new ScheduleResolver(_ => null);
        Assert.Null(resolver.Resolve(Guid.NewGuid(), DateTime.UtcNow));
        Assert.Empty(resolver.Enumerate(Guid.NewGuid(), DateTime.UtcNow, DateTime.UtcNow.AddHours(1)));
    }

    [Fact]
    public void RevisionChange_RebuildsScheduler_AndInvalidateIsSafe()
    {
        var pool = Items.SeriesRun("A", 8, 20);
        pool.AddRange(Items.SeriesRun("B", 8, 20));
        var revision = 1;
        var resolver = new ScheduleResolver(id => id == Items.StationA ? Items.PoolInput(pool, revision: revision) : null);

        var first = resolver.Enumerate(Items.StationA, Items.Anchor, Items.Anchor.AddHours(6)).Select(e => e.Item!.ItemId).ToList();
        var again = resolver.Enumerate(Items.StationA, Items.Anchor, Items.Anchor.AddHours(6)).Select(e => e.Item!.ItemId).ToList();
        Assert.Equal(first, again);

        revision = 2;
        var changed = resolver.Enumerate(Items.StationA, Items.Anchor, Items.Anchor.AddHours(6)).Select(e => e.Item!.ItemId).ToList();
        Assert.NotEqual(first, changed);

        resolver.Invalidate(Items.StationA);
        var afterInvalidate = resolver.Enumerate(Items.StationA, Items.Anchor, Items.Anchor.AddHours(6)).Select(e => e.Item!.ItemId).ToList();
        Assert.Equal(changed, afterInvalidate);
    }

    [Fact]
    public void NonUtcInput_IsNormalized()
    {
        var pool = Items.SeriesRun("A", 3, 20);
        var resolver = new ScheduleResolver(_ => Items.PoolInput(pool, order: PlayOrder.Sequential));
        var utc = Items.Anchor.AddMinutes(5);
        var local = utc.ToLocalTime();
        var a = resolver.Resolve(Items.StationA, utc)!;
        var b = resolver.Resolve(Items.StationA, local)!;
        Assert.Equal(a.Entry, b.Entry);
        Assert.Equal(a.Offset, b.Offset);
    }
}
