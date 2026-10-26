using Jellyfin.Plugin.JellyStations.Streaming;
using Xunit;

namespace Jellyfin.Plugin.JellyStations.Tests.Streaming;

/// <summary>
/// A reader joining a station gets the latest PAT and PMT and then the stream from a keyframe, not from
/// wherever the encoder happened to be; Jellyfin's probe and transcoder and TV apps all decode from the first packet.
/// </summary>
public class BroadcastBufferTests
{
    private const int VideoPid = 0x100;
    private const int AudioPid = 0x101;
    private const int PmtPid = 0x1000;

    [Fact]
    public void Subscribe_SendsTablesThenTheEarliestKeyframe()
    {
        var buffer = new BroadcastBuffer();
        var pat = Pat();
        var pmt = Pmt(0x1B);
        var key = Video(pusi: true, rai: true, cc: 3);
        buffer.Write(pat);
        buffer.Write(pmt);
        buffer.Write(Video(pusi: true, rai: false, cc: 0));
        buffer.Write(Video(pusi: false, rai: false, cc: 1));
        buffer.Write(Audio(cc: 0));
        buffer.Write(key);
        buffer.Write(Video(pusi: false, rai: false, cc: 4));
        buffer.Write(Audio(cc: 1));

        Assert.True(buffer.HasKeyframe);
        var got = Read(buffer.Subscribe(), 188 * 5);

        Assert.Equal(pat, got[..188]);
        Assert.Equal(pmt, got[188..376]);
        Assert.Equal(key, got[376..564]);
        Assert.Equal(VideoPid, Pid(got[564..752]));
        Assert.Equal(AudioPid, Pid(got[752..940]));
    }

    [Fact]
    public void Subscribe_SplitsAChunkWhereTheKeyframeBegins()
    {
        var buffer = new BroadcastBuffer();
        var pat = Pat();
        var pmt = Pmt(0x1B);
        var key = Video(pusi: true, rai: true, cc: 2);
        var tail = Video(pusi: false, rai: false, cc: 3);
        var one = Concat(pat, pmt, Video(pusi: true, rai: false, cc: 0), Video(pusi: false, rai: false, cc: 1), key, tail);
        buffer.Write(one);

        var got = Read(buffer.Subscribe(), 188 * 4);

        Assert.Equal(Concat(pat, pmt, key, tail), got);
    }

    [Fact]
    public void Subscribe_WithoutAKeyframe_ReplaysTheWholePreRoll()
    {
        var buffer = new BroadcastBuffer();
        var all = Concat(Pat(), Pmt(0x1B), Video(pusi: true, rai: false, cc: 0), Video(pusi: false, rai: false, cc: 1));
        buffer.Write(all);

        Assert.False(buffer.HasKeyframe);
        Assert.Equal(all, Read(buffer.Subscribe(), all.Length));
    }

    [Fact]
    public void Subscribe_LiveReaderGetsWhatArrivesLater()
    {
        var buffer = new BroadcastBuffer();
        buffer.Write(Concat(Pat(), Pmt(0x1B), Video(pusi: true, rai: true, cc: 0)));
        var reader = buffer.Subscribe();
        Read(reader, 188 * 3);

        var later = Video(pusi: false, rai: false, cc: 1);
        buffer.Write(later);

        Assert.Equal(later, Read(reader, 188));
    }

    [Fact]
    public void ClearPreRoll_ForgetsTablesAndKeyframes()
    {
        var buffer = new BroadcastBuffer();
        buffer.Write(Concat(Pat(), Pmt(0x1B), Video(pusi: true, rai: true, cc: 0)));
        Assert.True(buffer.HasKeyframe);

        buffer.ClearPreRoll();

        Assert.False(buffer.HasKeyframe);
    }

    [Fact]
    public void KeyframeStart_RecognisesAnSpsWithoutTheRandomAccessFlag()
    {
        var sps = Video(pusi: true, rai: false, cc: 0, nal: 0x67);
        var slice = Video(pusi: true, rai: false, cc: 0, nal: 0x41);
        var hevcVps = Video(pusi: true, rai: false, cc: 0, nal: 0x40);

        Assert.True(BroadcastBuffer.IsKeyframeStart(sps, VideoPid, 0x1B));
        Assert.False(BroadcastBuffer.IsKeyframeStart(slice, VideoPid, 0x1B));
        Assert.True(BroadcastBuffer.IsKeyframeStart(hevcVps, VideoPid, 0x24));
        Assert.False(BroadcastBuffer.IsKeyframeStart(hevcVps, VideoPid, 0x1B));
        Assert.False(BroadcastBuffer.IsKeyframeStart(Audio(cc: 0), VideoPid, 0x1B));
    }

    private static byte[] Read(Stream stream, int count)
    {
        var result = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = stream.Read(result, read, count - read);
            Assert.True(n > 0, "stream ended early");
            read += n;
        }

        return result;
    }

    private static int Pid(byte[] packet) => ((packet[1] & 0x1F) << 8) | packet[2];

    private static byte[] Concat(params byte[][] packets)
    {
        var result = new byte[packets.Sum(p => p.Length)];
        var at = 0;
        foreach (var p in packets)
        {
            p.CopyTo(result, at);
            at += p.Length;
        }

        return result;
    }

    private static byte[] Packet(int pid, bool pusi, byte[] payload, bool rai, int cc)
    {
        var p = new byte[188];
        Array.Fill(p, (byte)0xFF);
        p[0] = 0x47;
        p[1] = (byte)((pusi ? 0x40 : 0) | (pid >> 8));
        p[2] = (byte)pid;
        if (rai)
        {
            p[3] = (byte)(0x30 | cc);
            p[4] = 1;
            p[5] = 0x40;
            payload.CopyTo(p, 6);
        }
        else
        {
            p[3] = (byte)(0x10 | cc);
            payload.CopyTo(p, 4);
        }

        return p;
    }

    private static byte[] Pat()
    {
        // table_id, syntax+length 13, transport_stream_id 1, version 0 current, section 0 of 0, program 1 -> PMT PID, CRC
        var section = new byte[] { 0x00, 0xB0, 0x0D, 0x00, 0x01, 0xC1, 0x00, 0x00, 0x00, 0x01, (byte)(0xE0 | (PmtPid >> 8)), (byte)(PmtPid & 0xFF), 0, 0, 0, 0 };
        return Packet(0, pusi: true, Concat(new byte[] { 0 }, section), rai: false, cc: 0);
    }

    private static byte[] Pmt(byte videoType)
    {
        var section = new byte[]
        {
            0x02, 0xB0, 0x17, 0x00, 0x01, 0xC1, 0x00, 0x00,
            (byte)(0xE0 | (VideoPid >> 8)), (byte)(VideoPid & 0xFF), 0xF0, 0x00,
            videoType, (byte)(0xE0 | (VideoPid >> 8)), (byte)(VideoPid & 0xFF), 0xF0, 0x00,
            0x0F, (byte)(0xE0 | (AudioPid >> 8)), (byte)(AudioPid & 0xFF), 0xF0, 0x00,
            0, 0, 0, 0
        };
        return Packet(PmtPid, pusi: true, Concat(new byte[] { 0 }, section), rai: false, cc: 0);
    }

    private static byte[] Video(bool pusi, bool rai, int cc, byte nal = 0x41)
    {
        // PES header (no PTS) then an Annex B NAL unit; a non-start packet carries slice data only.
        var payload = pusi
            ? new byte[] { 0x00, 0x00, 0x01, 0xE0, 0x00, 0x00, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, nal, 0x12, 0x34 }
            : new byte[] { 0x12, 0x34, 0x56 };
        return Packet(VideoPid, pusi, payload, rai, cc);
    }

    private static byte[] Audio(int cc)
        => Packet(AudioPid, pusi: true, new byte[] { 0x00, 0x00, 0x01, 0xC0, 0x00, 0x00, 0x80, 0x00, 0x00, 0xFF, 0xF1 }, rai: false, cc);
}
