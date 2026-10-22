using Jellyfin.Plugin.TvStation.Streaming;
using Xunit;

namespace Jellyfin.Plugin.TvStation.Tests.Streaming;

public class UnreadStreamWatchdogTests
{
    [Fact]
    public void Buffer_IsIdleFromCreation_UntilSomeoneReads()
    {
        var before = DateTime.UtcNow;
        var buffer = new BroadcastBuffer();

        Assert.NotNull(buffer.IdleSinceUtc);
        Assert.InRange(buffer.IdleSinceUtc!.Value, before, DateTime.UtcNow);

        using var reader = buffer.Subscribe();
        Assert.Null(buffer.IdleSinceUtc);
    }

    [Fact]
    public void Buffer_BecomesIdle_WhenTheLastReaderDetaches_NotTheFirst()
    {
        var buffer = new BroadcastBuffer();
        var first = buffer.Subscribe();
        var second = buffer.Subscribe();

        first.Dispose();
        Assert.Null(buffer.IdleSinceUtc);

        var before = DateTime.UtcNow;
        second.Dispose();
        Assert.NotNull(buffer.IdleSinceUtc);
        Assert.InRange(buffer.IdleSinceUtc!.Value, before, DateTime.UtcNow);
        Assert.Equal(0, buffer.ConsumerCount);
    }

    [Fact]
    public void Buffer_ANewReaderClearsTheIdleMark()
    {
        var buffer = new BroadcastBuffer();
        buffer.Subscribe().Dispose();
        Assert.NotNull(buffer.IdleSinceUtc);

        using var again = buffer.Subscribe();
        Assert.Null(buffer.IdleSinceUtc);
    }

    [Fact]
    public void UnreadFor_IsZeroWhileSomeoneReads()
    {
        Assert.Equal(TimeSpan.Zero, StreamSessionManager.UnreadFor(null, DateTime.UtcNow));
    }

    [Fact]
    public void UnreadFor_MeasuresFromTheIdleMark_AndNeverGoesNegative()
    {
        var now = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal(TimeSpan.FromSeconds(150), StreamSessionManager.UnreadFor(now.AddSeconds(-150), now));
        Assert.Equal(TimeSpan.Zero, StreamSessionManager.UnreadFor(now.AddSeconds(5), now));
    }
}
