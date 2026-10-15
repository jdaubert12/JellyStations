using System.Diagnostics;
using Jellyfin.Plugin.TvStation.Streaming;
using Xunit;

namespace Jellyfin.Plugin.TvStation.Tests.Streaming;

public class TsPacerTests
{
    private static byte[] Packet(int pid, long? pcrBase)
    {
        var p = new byte[188];
        p[0] = 0x47;
        p[1] = (byte)((pid >> 8) & 0x1f);
        p[2] = (byte)(pid & 0xff);
        if (pcrBase.HasValue)
        {
            p[3] = 0x30; // adaptation field + payload
            p[4] = 7;    // adaptation field length
            p[5] = 0x10; // PCR flag
            var b = pcrBase.Value;
            p[6] = (byte)(b >> 25);
            p[7] = (byte)(b >> 17);
            p[8] = (byte)(b >> 9);
            p[9] = (byte)(b >> 1);
            p[10] = (byte)((b & 1) << 7);
        }
        else
        {
            p[3] = 0x10; // payload only
        }

        return p;
    }

    [Fact]
    public void LastPcr_FindsLatestPcrInChunk()
    {
        var chunk = Packet(256, 90_000).Concat(Packet(257, null)).Concat(Packet(256, 270_000)).Concat(Packet(257, null)).ToArray();
        Assert.Equal(270_000, TsPacer.LastPcr(chunk));
        Assert.Equal(-1, TsPacer.LastPcr(Packet(257, null)));
    }

    [Fact]
    public void LastPcr_HandlesLargeValues()
    {
        var big = (1L << 33) - 5;
        Assert.Equal(big, TsPacer.LastPcr(Packet(256, big)));
    }

    [Fact]
    public async Task Pacer_DelaysWhenAheadOfLead()
    {
        var pacer = new TsPacer { Lead = TimeSpan.FromMilliseconds(200) };
        await pacer.PaceAsync(Packet(256, 0), CancellationToken.None);
        var sw = Stopwatch.StartNew();
        // 1.0 s of stream time with a 0.2 s lead should block ~0.8 s.
        await pacer.PaceAsync(Packet(256, 90_000), CancellationToken.None);
        Assert.InRange(sw.Elapsed.TotalMilliseconds, 500, 1500);
        Assert.InRange(pacer.Position.TotalSeconds, 0.99, 1.01);
    }

    [Fact]
    public async Task Pacer_ResyncsOnBackwardJump()
    {
        var pacer = new TsPacer { Lead = TimeSpan.FromSeconds(5) };
        await pacer.PaceAsync(Packet(256, 90_000 * 100), CancellationToken.None);
        await pacer.PaceAsync(Packet(256, 90_000 * 10), CancellationToken.None);
        Assert.Equal(TimeSpan.Zero, pacer.Position);
    }

    [Fact]
    public void Aligner_ReturnsWholePacketsAndCarriesRemainder()
    {
        var aligner = new TsAligner();
        var two = Packet(256, null).Concat(Packet(256, null)).ToArray();
        var first = aligner.Push(two.AsSpan(0, 300));
        Assert.Equal(188, first.Length);
        var second = aligner.Push(two.AsSpan(300));
        Assert.Equal(188, second.Length);
        Assert.Equal(0x47, second.Span[0]);
    }

    [Fact]
    public void Aligner_ResyncsToSyncByte()
    {
        var aligner = new TsAligner();
        var garbage = new byte[] { 1, 2, 3 }.Concat(Packet(256, null)).ToArray();
        var out1 = aligner.Push(garbage);
        Assert.Equal(188, out1.Length);
        Assert.Equal(0x47, out1.Span[0]);
    }
}
