using System.Threading.Channels;

namespace Jellyfin.Plugin.JellyStations.Streaming;

/// <summary>
/// Fan-out of one MPEG-TS byte stream to any number of readers. Keeps a short pre-roll so a new reader
/// immediately receives recent data, and hands it over starting at a keyframe: the reader first gets the
/// latest PAT and PMT, then the stream from the earliest keyframe still in the pre-roll. Starting anywhere
/// else made every joining decoder (a TV app, Jellyfin's ffprobe, Jellyfin's transcoder) log "non-existing
/// PPS referenced" and wait for the next IDR frame before showing a picture.
/// </summary>
public sealed class BroadcastBuffer
{
    private const int TsPacket = 188;
    private const int PreRollBytes = 2 * 1024 * 1024;
    private const int ConsumerQueueChunks = 1024;

    private const int PatPid = 0;
    private const byte StreamTypeH264 = 0x1B;
    private const byte StreamTypeHevc = 0x24;

    private readonly object _lock = new();
    private readonly LinkedList<Chunk> _preRoll = new();
    private readonly List<Channel<ReadOnlyMemory<byte>>> _consumers = new();
    private byte[] _carry = Array.Empty<byte>();
    private long _preRollSize;
    private bool _completed;
    private DateTime? _idleSinceUtc = DateTime.UtcNow;

    // Program tables and the video PID, learned from the stream itself (ffmpeg writes PAT and PMT before the
    // first frame and repeats them every 100 ms).
    private byte[]? _pat;
    private byte[]? _pmt;
    private int _pmtPid = -1;
    private int _videoPid = -1;
    private byte _videoStreamType;

    public long TotalBytes { get; private set; }

    /// <summary>When the last reader left (or the buffer was created), or null while at least one reader is attached.</summary>
    public DateTime? IdleSinceUtc
    {
        get
        {
            lock (_lock)
            {
                return _idleSinceUtc;
            }
        }
    }

    public int ConsumerCount
    {
        get
        {
            lock (_lock)
            {
                return _consumers.Count;
            }
        }
    }

    /// <summary>Whether a keyframe is currently in the pre-roll, so a new reader starts clean.</summary>
    public bool HasKeyframe
    {
        get
        {
            lock (_lock)
            {
                return _pat is not null && _pmt is not null && _preRoll.Any(c => c.StartsAtKeyframe);
            }
        }
    }

    /// <summary>Appends raw bytes from the encoder; chunks are re-aligned to TS packet boundaries.</summary>
    public void Write(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return;
        }

        byte[] combined;
        if (_carry.Length == 0)
        {
            combined = data.ToArray();
        }
        else
        {
            combined = new byte[_carry.Length + data.Length];
            _carry.CopyTo(combined, 0);
            data.CopyTo(combined.AsSpan(_carry.Length));
        }

        var aligned = combined.Length / TsPacket * TsPacket;
        _carry = aligned == combined.Length ? Array.Empty<byte>() : combined.AsSpan(aligned).ToArray();
        if (aligned == 0)
        {
            return;
        }

        lock (_lock)
        {
            if (_completed)
            {
                return;
            }

            TotalBytes += aligned;

            // Split the data where a keyframe begins so the pre-roll can hand a reader exactly that packet first.
            var start = 0;
            var startsAtKeyframe = false;
            for (var offset = 0; offset < aligned; offset += TsPacket)
            {
                if (!Inspect(combined.AsSpan(offset, TsPacket)))
                {
                    continue;
                }

                if (offset > start)
                {
                    Append(new ReadOnlyMemory<byte>(combined, start, offset - start), startsAtKeyframe);
                    start = offset;
                }

                startsAtKeyframe = true;
            }

            Append(new ReadOnlyMemory<byte>(combined, start, aligned - start), startsAtKeyframe);
        }
    }

    /// <summary>Creates a reader that first receives the pre-roll and then live data.</summary>
    public ConsumerStream Subscribe()
    {
        var channel = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(ConsumerQueueChunks)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        lock (_lock)
        {
            var node = _pat is not null && _pmt is not null ? FirstKeyframeNode() : null;
            if (node is not null)
            {
                var tables = new byte[_pat!.Length + _pmt!.Length];
                _pat.CopyTo(tables, 0);
                _pmt.CopyTo(tables, _pat.Length);
                channel.Writer.TryWrite(tables);
            }
            else
            {
                node = _preRoll.First;
            }

            for (; node is not null; node = node.Next)
            {
                channel.Writer.TryWrite(node.Value.Data);
            }

            if (_completed)
            {
                channel.Writer.TryComplete();
            }
            else
            {
                _consumers.Add(channel);
                _idleSinceUtc = null;
            }
        }

        return new ConsumerStream(channel.Reader, () => Unsubscribe(channel));
    }

    /// <summary>Drops the pre-roll (used after an encoder restart so stale data is not replayed).</summary>
    public void ClearPreRoll()
    {
        lock (_lock)
        {
            _preRoll.Clear();
            _preRollSize = 0;
            _carry = Array.Empty<byte>();
            _pat = null;
            _pmt = null;
            _pmtPid = -1;
            _videoPid = -1;
            _videoStreamType = 0;
        }
    }

    public void Complete()
    {
        lock (_lock)
        {
            _completed = true;
            foreach (var c in _consumers)
            {
                c.Writer.TryComplete();
            }

            _consumers.Clear();
            _idleSinceUtc ??= DateTime.UtcNow;
        }
    }

    /// <summary>Whether this packet is the first of a keyframe, judged from ffmpeg's random-access flag or the NAL units it carries.</summary>
    internal static bool IsKeyframeStart(ReadOnlySpan<byte> packet, int videoPid = -1, byte videoStreamType = 0)
    {
        if (packet.Length != TsPacket || packet[0] != 0x47 || (packet[1] & 0x40) == 0)
        {
            return false;
        }

        var pid = Pid(packet);
        if (pid == PatPid || (videoPid >= 0 && pid != videoPid))
        {
            return false;
        }

        if (RandomAccessFlag(packet))
        {
            return true;
        }

        var payload = PayloadStart(packet);
        if (payload < 0 || packet.Length - payload < 9 || packet[payload] != 0 || packet[payload + 1] != 0 || packet[payload + 2] != 1)
        {
            return false;
        }

        var es = payload + 9 + packet[payload + 8];
        for (var i = es; i + 3 < packet.Length; i++)
        {
            if (packet[i] != 0 || packet[i + 1] != 0 || packet[i + 2] != 1)
            {
                continue;
            }

            var nal = packet[i + 3];
            var h264 = nal & 0x1F;
            var hevc = (nal >> 1) & 0x3F;
            if (videoStreamType != StreamTypeHevc && (h264 == 5 || h264 == 7))
            {
                return true;
            }

            if (videoStreamType != StreamTypeH264 && (hevc is 19 or 20 or 32 or 33))
            {
                return true;
            }
        }

        return false;
    }

    private void Append(ReadOnlyMemory<byte> chunk, bool startsAtKeyframe)
    {
        _preRoll.AddLast(new Chunk(chunk, startsAtKeyframe));
        _preRollSize += chunk.Length;
        while (_preRollSize > PreRollBytes && _preRoll.First is not null)
        {
            _preRollSize -= _preRoll.First.Value.Data.Length;
            _preRoll.RemoveFirst();
        }

        foreach (var c in _consumers)
        {
            c.Writer.TryWrite(chunk);
        }
    }

    private LinkedListNode<Chunk>? FirstKeyframeNode()
    {
        for (var node = _preRoll.First; node is not null; node = node.Next)
        {
            if (node.Value.StartsAtKeyframe)
            {
                return node;
            }
        }

        return null;
    }

    /// <summary>Learns the tables from PAT/PMT packets and reports whether the packet starts a keyframe.</summary>
    private bool Inspect(ReadOnlySpan<byte> packet)
    {
        if (packet[0] != 0x47)
        {
            return false;
        }

        var pid = Pid(packet);
        var pusi = (packet[1] & 0x40) != 0;
        if (pid == PatPid)
        {
            if (pusi && TryParsePat(packet, out var pmtPid))
            {
                _pat = packet.ToArray();
                _pmtPid = pmtPid;
            }

            return false;
        }

        if (pid == _pmtPid)
        {
            if (pusi && TryParsePmt(packet, out var videoPid, out var streamType))
            {
                _pmt = packet.ToArray();
                _videoPid = videoPid;
                _videoStreamType = streamType;
            }

            return false;
        }

        return IsKeyframeStart(packet, _videoPid, _videoStreamType);
    }

    private static int Pid(ReadOnlySpan<byte> packet) => ((packet[1] & 0x1F) << 8) | packet[2];

    private static bool RandomAccessFlag(ReadOnlySpan<byte> packet)
        => (packet[3] & 0x20) != 0 && packet[4] > 0 && (packet[5] & 0x40) != 0;

    /// <summary>Index of the first payload byte, or -1 when the packet carries none.</summary>
    private static int PayloadStart(ReadOnlySpan<byte> packet)
    {
        if ((packet[3] & 0x10) == 0)
        {
            return -1;
        }

        var start = (packet[3] & 0x20) != 0 ? 5 + packet[4] : 4;
        return start < packet.Length ? start : -1;
    }

    /// <summary>The PSI section inside a payload-unit-start packet (after the pointer field), or an empty span.</summary>
    private static ReadOnlySpan<byte> Section(ReadOnlySpan<byte> packet)
    {
        var payload = PayloadStart(packet);
        if (payload < 0 || payload >= packet.Length)
        {
            return ReadOnlySpan<byte>.Empty;
        }

        var sectionStart = payload + 1 + packet[payload];
        if (sectionStart + 3 > packet.Length)
        {
            return ReadOnlySpan<byte>.Empty;
        }

        var length = 3 + (((packet[sectionStart + 1] & 0x0F) << 8) | packet[sectionStart + 2]);
        return packet.Slice(sectionStart, Math.Min(length, packet.Length - sectionStart));
    }

    private static bool TryParsePat(ReadOnlySpan<byte> packet, out int pmtPid)
    {
        pmtPid = -1;
        var s = Section(packet);
        if (s.Length < 12 || s[0] != 0)
        {
            return false;
        }

        for (var i = 8; i + 4 <= s.Length - 4; i += 4)
        {
            var program = (s[i] << 8) | s[i + 1];
            if (program != 0)
            {
                pmtPid = ((s[i + 2] & 0x1F) << 8) | s[i + 3];
                return true;
            }
        }

        return false;
    }

    private static bool TryParsePmt(ReadOnlySpan<byte> packet, out int videoPid, out byte streamType)
    {
        videoPid = -1;
        streamType = 0;
        var s = Section(packet);
        if (s.Length < 12 || s[0] != 2)
        {
            return false;
        }

        var i = 12 + (((s[10] & 0x0F) << 8) | s[11]);
        while (i + 5 <= s.Length - 4)
        {
            var type = s[i];
            var pid = ((s[i + 1] & 0x1F) << 8) | s[i + 2];
            var infoLength = ((s[i + 3] & 0x0F) << 8) | s[i + 4];
            if (type is 0x01 or 0x02 or 0x10 or StreamTypeH264 or StreamTypeHevc)
            {
                videoPid = pid;
                streamType = type;
                return true;
            }

            i += 5 + infoLength;
        }

        return false;
    }

    private void Unsubscribe(Channel<ReadOnlyMemory<byte>> channel)
    {
        lock (_lock)
        {
            if (_consumers.Remove(channel) && _consumers.Count == 0)
            {
                _idleSinceUtc = DateTime.UtcNow;
            }
        }

        channel.Writer.TryComplete();
    }

    private readonly record struct Chunk(ReadOnlyMemory<byte> Data, bool StartsAtKeyframe);
}
