using Jellyfin.Plugin.JellyStations.Streaming;
using Xunit;

namespace Jellyfin.Plugin.JellyStations.Tests.Streaming;

public class TsPacerMarginTests
{
    private static byte[] Packet(long pcrBase)
    {
        var p = new byte[188];
        p[0] = 0x47;
        p[1] = 1;
        p[2] = 0;
        p[3] = 0x30;
        p[4] = 7;
        p[5] = 0x10;
        p[6] = (byte)(pcrBase >> 25);
        p[7] = (byte)(pcrBase >> 17);
        p[8] = (byte)(pcrBase >> 9);
        p[9] = (byte)(pcrBase >> 1);
        p[10] = (byte)((pcrBase & 1) << 7);
        return p;
    }

    [Fact]
    public async Task Margin_IsNullBeforeTheFirstPcr_AndZeroAtIt()
    {
        var pacer = new TsPacer { Lead = TimeSpan.FromSeconds(5) };
        Assert.Null(pacer.Margin);
        await pacer.PaceAsync(Packet(0), CancellationToken.None);
        Assert.Equal(TimeSpan.Zero, pacer.Margin);
    }

    [Fact]
    public async Task Margin_GrowsWhenTheEncoderRunsAhead()
    {
        // A large lead so nothing blocks: 2 s of stream time arriving at once is 2 s ahead of the wall clock.
        var pacer = new TsPacer { Lead = TimeSpan.FromSeconds(10) };
        await pacer.PaceAsync(Packet(0), CancellationToken.None);
        await pacer.PaceAsync(Packet(180_000), CancellationToken.None);
        Assert.InRange(pacer.Margin!.Value.TotalSeconds, 1.5, 2.1);
        Assert.InRange(pacer.MinMargin!.Value.TotalSeconds, 1.5, 2.1);
    }

    [Fact]
    public async Task Margin_GoesNegativeWhenTheEncoderIsLate_AndMinRemembersIt()
    {
        var pacer = new TsPacer { Lead = TimeSpan.FromSeconds(10) };
        await pacer.PaceAsync(Packet(0), CancellationToken.None);
        await Task.Delay(700);
        // Only 0.1 s of stream time after 0.7 s of wall time: the encoder is behind.
        await pacer.PaceAsync(Packet(9_000), CancellationToken.None);
        Assert.True(pacer.Margin!.Value.TotalSeconds < -0.3, "margin " + pacer.Margin);
        await pacer.PaceAsync(Packet(450_000), CancellationToken.None);
        Assert.True(pacer.Margin!.Value.TotalSeconds > 3, "margin " + pacer.Margin);
        Assert.True(pacer.MinMargin!.Value.TotalSeconds < -0.3, "min " + pacer.MinMargin);
    }
}
